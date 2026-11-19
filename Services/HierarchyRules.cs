using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services;

/// <summary>A change of an issue's parent, recorded by <see cref="HierarchyRules.RecordAsync"/> for notifications after saving.</summary>
public sealed record HierarchyChange(Issue Issue, int? OldParentId, int? NewParentId);

/// <summary>
/// Rules for the issue hierarchy (epics → stories/tasks/bugs/features → sub-tasks). Enforced by
/// <see cref="IssueService"/> on every create and update, so the UI and the API can't get around them.
/// <list type="bullet">
/// <item>An epic has no parent. Its children may be any type except Epic and Subtask.</item>
/// <item>A sub-task must have a parent, which is neither an epic nor a sub-task. Sub-tasks have no children.</item>
/// <item>Other types may have an epic as parent, or none. Their children can only be sub-tasks.</item>
/// <item>Parent and child are in the same project; no cycles; at most three levels.</item>
/// </list>
/// </summary>
public static class HierarchyRules
{
    public const int MaxDepth = 3;

    public static bool CanBeChildOf(IssueType child, IssueType parent) => child switch
    {
        IssueType.Epic => false,
        IssueType.Subtask => parent is not (IssueType.Epic or IssueType.Subtask),
        _ => parent == IssueType.Epic,
    };

    /// <summary>Which parent types <paramref name="child"/> may have (empty for epics).</summary>
    public static IssueType[] AllowedParentTypes(IssueType child) =>
        Enum.GetValues<IssueType>().Where(p => CanBeChildOf(child, p)).ToArray();

    /// <summary>Which child types an issue of type <paramref name="parent"/> may have.</summary>
    public static IssueType[] AllowedChildTypes(IssueType parent) =>
        Enum.GetValues<IssueType>().Where(c => CanBeChildOf(c, parent)).ToArray();

    public static string ParentRule(IssueType child) => child switch
    {
        IssueType.Epic => "An epic can't have a parent.",
        IssueType.Subtask => "A sub-task's parent must be a story, task, bug or feature (not an epic or another sub-task).",
        _ => $"A {child.ToString().ToLowerInvariant()} can only have an epic as its parent.",
    };

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> if <paramref name="issue"/> (tracked or new, with its pending
    /// type and parent) breaks a hierarchy rule, either towards its parent or towards its existing children.
    /// </summary>
    public static async Task ValidateAsync(AppDbContext db, Issue issue)
    {
        var ancestors = 0;
        if (issue.ParentId is { } pid)
        {
            if (pid == issue.Id) throw new InvalidOperationException("An issue can't be its own parent.");
            var parent = await db.Issues.AsNoTracking().Where(i => i.Id == pid)
                .Select(i => new { i.ProjectId, i.Type, i.ParentId }).FirstOrDefaultAsync()
                ?? throw new InvalidOperationException("The parent issue doesn't exist.");
            if (parent.ProjectId != issue.ProjectId) throw new InvalidOperationException("The parent must be in the same project.");
            if (!CanBeChildOf(issue.Type, parent.Type)) throw new InvalidOperationException(ParentRule(issue.Type));

            // Walk up: no cycles, and count the levels above this issue.
            ancestors = 1;
            var seen = new HashSet<int> { issue.Id, pid };
            for (var up = parent.ParentId; up is { } u; ancestors++)
            {
                if (!seen.Add(u)) throw new InvalidOperationException("That parent would create a cycle.");
                up = await db.Issues.AsNoTracking().Where(i => i.Id == u).Select(i => i.ParentId).FirstOrDefaultAsync();
            }
        }
        else if (issue.Type == IssueType.Subtask)
        {
            throw new InvalidOperationException("A sub-task must have a parent issue.");
        }

        if (ancestors + 1 > MaxDepth) throw new InvalidOperationException($"Issues can be nested at most {MaxDepth} levels deep (epic › story › sub-task).");
        if (issue.Id == 0) return; // a new issue has no children yet
        var children = await db.Issues.AsNoTracking().Where(i => i.ParentId == issue.Id).Select(i => new { i.Id, i.Type }).ToListAsync();
        if (children.Count == 0) return;
        if (children.Any(c => c.Id == issue.ParentId)) throw new InvalidOperationException("That parent would create a cycle.");
        var bad = children.Where(c => !CanBeChildOf(c.Type, issue.Type)).Select(c => c.Type).Distinct().ToList();
        if (bad.Count > 0)
            throw new InvalidOperationException(issue.Type == IssueType.Subtask
                ? "A sub-task can't have child issues. Remove its children first."
                : $"This issue has {string.Join("/", bad.Select(t => t.ToString().ToLowerInvariant()))} children, which a {issue.Type.ToString().ToLowerInvariant()} can't have. {ParentRule(bad[0])}");
        var grandchildren = await db.Issues.AnyAsync(i => i.Parent != null && i.Parent.ParentId == issue.Id);
        var levels = ancestors + 1 + (grandchildren ? 2 : 1);
        if (levels > MaxDepth) throw new InvalidOperationException($"Issues can be nested at most {MaxDepth} levels deep (epic › story › sub-task).");
    }

    /// <summary>
    /// If the parent changed, adds "added/removed child" activity to the old and new parents (saved with the caller's
    /// SaveChanges) and bumps their UpdatedAt. Returns the change, or null.
    /// </summary>
    public static async Task<HierarchyChange?> RecordAsync(AppDbContext db, Issue issue, int? oldParentId, int actorId)
    {
        if (issue.ParentId == oldParentId) return null;
        var key = $"{await db.Projects.Where(p => p.Id == issue.ProjectId).Select(p => p.Key).FirstAsync()}-{issue.Number}";
        var now = DateTime.UtcNow;
        if (oldParentId is { } o)
        {
            db.Activities.Add(new Activity { IssueId = o, ActorId = actorId, Text = $"removed child issue **{key}**", CreatedAt = now });
            await db.Issues.Where(i => i.Id == o).ExecuteUpdateAsync(s => s.SetProperty(i => i.UpdatedAt, now));
        }
        if (issue.ParentId is { } n)
        {
            db.Activities.Add(new Activity { IssueId = n, ActorId = actorId, Text = $"added child issue **{key}**", CreatedAt = now });
            await db.Issues.Where(i => i.Id == n).ExecuteUpdateAsync(s => s.SetProperty(i => i.UpdatedAt, now));
        }
        return new HierarchyChange(issue, oldParentId, issue.ParentId);
    }

    /// <summary>
    /// Before an issue is deleted: deletes its sub-tasks (their comments, activity, watches and links cascade).
    /// Other children (an epic's stories and tasks) are unlinked by the ON DELETE SET NULL foreign key.
    /// </summary>
    public static Task<int> DeleteSubtasksAsync(AppDbContext db, int issueId) =>
        db.Issues.Where(i => i.ParentId == issueId && i.Type == IssueType.Subtask).ExecuteDeleteAsync();

    /// <summary>
    /// Fills <see cref="Issue.Work"/> (parent key, sub-task/child progress, open blockers the user can see) for list
    /// rows and board cards, in three queries.
    /// </summary>
    public static async Task<List<Issue>> AnnotateAsync(AppDbContext db, ProjectAccess access, List<Issue> issues)
    {
        if (issues.Count == 0) return issues;
        var ids = issues.Select(i => i.Id).ToList();
        var parentIds = issues.Where(i => i.ParentId != null).Select(i => i.ParentId!.Value).Distinct().ToList();
        var parents = parentIds.Count == 0 ? [] : await db.Issues.Where(i => parentIds.Contains(i.Id))
            .Select(i => new { i.Id, i.Number, i.Title, i.Project.Key }).ToDictionaryAsync(x => x.Id);
        var children = await db.Issues.Where(i => i.ParentId != null && ids.Contains(i.ParentId.Value))
            .GroupBy(i => i.ParentId!.Value)
            .Select(g => new { Id = g.Key, Total = g.Count(), Done = g.Count(c => c.Status == IssueStatus.Done) })
            .ToDictionaryAsync(x => x.Id);
        var visible = await access.VisibleProjectIdsAsync();
        var blockers = (await db.IssueLinks
                .Where(l => l.Type == IssueLinkType.Blocks && ids.Contains(l.TargetIssueId)
                            && l.SourceIssue.Status != IssueStatus.Done && visible.Contains(l.SourceIssue.ProjectId))
                .Select(l => new { l.TargetIssueId, l.SourceIssue.Project.Key, l.SourceIssue.Number }).ToListAsync())
            .ToLookup(x => x.TargetIssueId, x => $"{x.Key}-{x.Number}");

        foreach (var i in issues)
        {
            var p = i.ParentId is { } pid && parents.TryGetValue(pid, out var pp) ? pp : null;
            var c = children.GetValueOrDefault(i.Id);
            i.Work = new IssueWorkInfo(p == null ? null : $"{p.Key}-{p.Number}", p?.Number, p?.Title,
                c?.Done ?? 0, c?.Total ?? 0, blockers[i.Id].ToList());
        }
        return issues;
    }

    /// <summary>The warning shown when a blocked issue is moved to In Progress or Done (the move still happens).</summary>
    public static string? BlockedMoveWarning(string issueKey, IssueStatus to, IReadOnlyCollection<string> openBlockers) =>
        openBlockers.Count == 0 || to is not (IssueStatus.InProgress or IssueStatus.Done) ? null
            : $"⛔ {issueKey} is blocked by {string.Join(", ", openBlockers)}, which {(openBlockers.Count == 1 ? "is" : "are")} still open. Moved to {to.Name()} anyway.";
}

/// <summary>Linked-work filters for the issue list.</summary>
public partial record IssueFilter
{
    /// <summary>Only issues under this epic: its children and their sub-tasks.</summary>
    public int? EpicId { get; set; }
    public bool HideSubtasks { get; set; }

    internal IQueryable<Issue> ApplyLinkedWork(IQueryable<Issue> q)
    {
        if (EpicId is { } e) q = q.Where(i => i.ParentId == e || (i.Parent != null && i.Parent.ParentId == e));
        if (HideSubtasks) q = q.Where(i => i.Type != IssueType.Subtask);
        return q;
    }
}
