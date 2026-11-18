using System.Linq.Expressions;
using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services;

/// <summary>A link as seen from one issue: stored type plus direction ("blocks" vs "is blocked by").</summary>
public enum IssueLinkRelation { Blocks, IsBlockedBy, RelatesTo, Duplicates, IsDuplicatedBy, Clones, IsClonedBy }

/// <summary>A lightweight view of an issue for pickers, panels and link lists.</summary>
public sealed record IssueRef(int Id, int ProjectId, string ProjectKey, int Number, string Title, IssueType Type,
    IssueStatus Status, int? StoryPoints, User? Assignee, int? ParentId)
{
    public string Key => $"{ProjectKey}-{Number}";
    public string Url => $"/p/{ProjectKey}/issues/{Number}";
}

public sealed record IssueLinkView(int LinkId, IssueLinkRelation Relation, IssueRef Other, DateTime CreatedAt);

/// <summary>Parent chain (root first) and children of one issue, with progress over the children.</summary>
public sealed record IssueHierarchy(IReadOnlyList<IssueRef> Ancestors, IReadOnlyList<IssueRef> Children)
{
    public int Total => Children.Count;
    public int Done => Children.Count(c => c.Status == IssueStatus.Done);
    public int Points => Children.Sum(c => c.StoryPoints ?? 0);
    public int DonePoints => Children.Where(c => c.Status == IssueStatus.Done).Sum(c => c.StoryPoints ?? 0);
    public int Percent => Total == 0 ? 0 : Done * 100 / Total;
}

public sealed record EpicSummary(IssueRef Epic, int Total, int Done, int Points, int DonePoints)
{
    public int Percent => Total == 0 ? 0 : Done * 100 / Total;
}

/// <summary>
/// Linked work: the epic › story › sub-task hierarchy and issue links (blocks, relates to, duplicates, clones).
/// Hierarchy rules live in <see cref="HierarchyRules"/> and are enforced by <see cref="IssueService"/>, which every
/// parent change here goes through. Links may cross projects, but only to issues the acting user can view, and links
/// to issues they can't view are hidden.
/// </summary>
public class LinkedWorkService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, IssueService issues, NotificationService notifications)
{
    private static readonly Expression<Func<Issue, IssueRef>> ToRef = i =>
        new IssueRef(i.Id, i.ProjectId, i.Project.Key, i.Number, i.Title, i.Type, i.Status, i.StoryPoints, i.Assignee, i.ParentId);

    // ---------- Relations ----------

    public static (IssueLinkType Type, bool Outgoing) Split(IssueLinkRelation r) => r switch
    {
        IssueLinkRelation.Blocks => (IssueLinkType.Blocks, true),
        IssueLinkRelation.IsBlockedBy => (IssueLinkType.Blocks, false),
        IssueLinkRelation.RelatesTo => (IssueLinkType.RelatesTo, true),
        IssueLinkRelation.Duplicates => (IssueLinkType.Duplicates, true),
        IssueLinkRelation.IsDuplicatedBy => (IssueLinkType.Duplicates, false),
        IssueLinkRelation.Clones => (IssueLinkType.Clones, true),
        _ => (IssueLinkType.Clones, false),
    };

    public static IssueLinkRelation Relation(IssueLinkType type, bool outgoing) => type switch
    {
        IssueLinkType.Blocks => outgoing ? IssueLinkRelation.Blocks : IssueLinkRelation.IsBlockedBy,
        IssueLinkType.Duplicates => outgoing ? IssueLinkRelation.Duplicates : IssueLinkRelation.IsDuplicatedBy,
        IssueLinkType.Clones => outgoing ? IssueLinkRelation.Clones : IssueLinkRelation.IsClonedBy,
        _ => IssueLinkRelation.RelatesTo, // symmetric
    };

    public static IssueLinkRelation Inverse(IssueLinkRelation r) { var (t, o) = Split(r); return Relation(t, !o); }

    public static string Phrase(IssueLinkRelation r) => r switch
    {
        IssueLinkRelation.Blocks => "blocks",
        IssueLinkRelation.IsBlockedBy => "is blocked by",
        IssueLinkRelation.RelatesTo => "relates to",
        IssueLinkRelation.Duplicates => "duplicates",
        IssueLinkRelation.IsDuplicatedBy => "is duplicated by",
        IssueLinkRelation.Clones => "clones",
        _ => "is cloned by",
    };

    // ---------- Reads ----------

    private async Task<bool> CanViewIssueAsync(AppDbContext db, int issueId, IReadOnlyList<int> visible) =>
        await db.Issues.AnyAsync(i => i.Id == issueId && visible.Contains(i.ProjectId));

    /// <summary>One issue the acting user can view, by id or null.</summary>
    public async Task<IssueRef?> GetRefAsync(int issueId)
    {
        var visible = await access.VisibleProjectIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Issues.Where(i => i.Id == issueId && visible.Contains(i.ProjectId)).Select(ToRef).FirstOrDefaultAsync();
    }

    /// <summary>An issue by key ("WEB-12"), or null if it doesn't exist or the acting user can't view it.</summary>
    public async Task<IssueRef?> FindByKeyAsync(string key)
    {
        var dash = key.LastIndexOf('-');
        if (dash <= 0 || !int.TryParse(key[(dash + 1)..], out var number)) return null;
        var projectKey = key[..dash].Trim().ToUpperInvariant();
        var visible = await access.VisibleProjectIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Issues.Where(i => i.Project.Key == projectKey && i.Number == number && visible.Contains(i.ProjectId))
            .Select(ToRef).FirstOrDefaultAsync();
    }

    public async Task<IssueHierarchy> GetHierarchyAsync(int issueId)
    {
        var visible = await access.VisibleProjectIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await CanViewIssueAsync(db, issueId, visible)) return new([], []);

        var ancestors = new List<IssueRef>();
        var parentId = await db.Issues.Where(i => i.Id == issueId).Select(i => i.ParentId).FirstAsync();
        while (parentId is { } p && ancestors.Count < HierarchyRules.MaxDepth && ancestors.All(a => a.Id != p))
        {
            var parent = await db.Issues.Where(i => i.Id == p).Select(ToRef).FirstAsync();
            ancestors.Insert(0, parent);
            parentId = parent.ParentId;
        }
        var children = await db.Issues.Where(i => i.ParentId == issueId)
            .OrderBy(i => i.Status == IssueStatus.Done).ThenBy(i => i.SortOrder).ThenBy(i => i.Number)
            .Select(ToRef).ToListAsync();
        return new(ancestors, children);
    }

    /// <summary>Confirmation text for deleting an issue: warns that its sub-tasks go too, and that epic children are unlinked.</summary>
    public async Task<string> DeleteConfirmTextAsync(int issueId, string key)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var subtasks = await db.Issues.CountAsync(i => i.ParentId == issueId && i.Type == IssueType.Subtask);
        var others = await db.Issues.CountAsync(i => i.ParentId == issueId && i.Type != IssueType.Subtask);
        var text = $"Delete {key}?";
        if (subtasks > 0) text += $" Its {subtasks} sub-task{(subtasks == 1 ? "" : "s")} will be deleted too.";
        if (others > 0) text += $" Its {others} child issue{(others == 1 ? "" : "s")} will be kept but no longer belong to it.";
        return text + " This cannot be undone.";
    }

    /// <summary>Links of an issue, from its point of view. Links to issues the acting user can't view are left out.</summary>
    public async Task<List<IssueLinkView>> GetLinksAsync(int issueId)
    {
        var visible = await access.VisibleProjectIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await CanViewIssueAsync(db, issueId, visible)) return [];
        var links = await db.IssueLinks.Where(l => l.SourceIssueId == issueId || l.TargetIssueId == issueId)
            .Select(l => new { l.Id, l.Type, l.SourceIssueId, l.TargetIssueId, l.CreatedAt }).ToListAsync();
        var otherIds = links.Select(l => l.SourceIssueId == issueId ? l.TargetIssueId : l.SourceIssueId).Distinct().ToList();
        var others = await db.Issues.Where(i => otherIds.Contains(i.Id) && visible.Contains(i.ProjectId)).Select(ToRef).ToDictionaryAsync(i => i.Id);
        return links
            .Select(l => (l, outgoing: l.SourceIssueId == issueId))
            .Where(x => others.ContainsKey(x.outgoing ? x.l.TargetIssueId : x.l.SourceIssueId))
            .Select(x => new IssueLinkView(x.l.Id, Relation(x.l.Type, x.outgoing), others[x.outgoing ? x.l.TargetIssueId : x.l.SourceIssueId], x.l.CreatedAt))
            .OrderBy(v => v.Relation).ThenBy(v => v.Other.ProjectKey).ThenBy(v => v.Other.Number)
            .ToList();
    }

    /// <summary>
    /// Issue picker search: by key ("WEB-12"), number ("12") or title words, among issues the acting user can view.
    /// <paramref name="projectId"/> limits to one project; <paramref name="types"/> to some types.
    /// </summary>
    public async Task<List<IssueRef>> SearchAsync(string? term, int? projectId, IReadOnlyCollection<int> excludeIds,
        IReadOnlyCollection<IssueType>? types = null, int take = 8)
    {
        var visible = await access.VisibleProjectIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.Issues.Where(i => visible.Contains(i.ProjectId) && !excludeIds.Contains(i.Id));
        if (projectId is { } pid) q = q.Where(i => i.ProjectId == pid);
        if (types is { Count: > 0 }) q = q.Where(i => types.Contains(i.Type));
        var t = term?.Trim() ?? "";
        if (t.Length > 0)
        {
            var dash = t.LastIndexOf('-');
            if (dash > 0 && int.TryParse(t[(dash + 1)..], out var n))
            {
                var pk = t[..dash].ToUpperInvariant();
                q = q.Where(i => (i.Project.Key == pk && i.Number == n) || i.Title.Contains(t));
            }
            else if (int.TryParse(t, out var num)) q = q.Where(i => i.Number == num || i.Title.Contains(t));
            else q = q.Where(i => i.Title.Contains(t));
        }
        return await q.OrderBy(i => i.Status == IssueStatus.Done).ThenByDescending(i => i.UpdatedAt).Take(take).Select(ToRef).ToListAsync();
    }

    public async Task<List<EpicSummary>> GetEpicsAsync(int projectId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.Issues.Where(i => i.ProjectId == projectId && i.Type == IssueType.Epic)
            .OrderBy(i => i.Status == IssueStatus.Done).ThenBy(i => i.Number)
            .Select(i => new
            {
                Ref = new IssueRef(i.Id, i.ProjectId, i.Project.Key, i.Number, i.Title, i.Type, i.Status, i.StoryPoints, i.Assignee, i.ParentId),
                Total = i.Children.Count,
                Done = i.Children.Count(c => c.Status == IssueStatus.Done),
                Points = i.Children.Sum(c => c.StoryPoints ?? 0),
                DonePoints = i.Children.Where(c => c.Status == IssueStatus.Done).Sum(c => c.StoryPoints ?? 0),
            }).ToListAsync();
        return rows.Select(r => new EpicSummary(r.Ref, r.Total, r.Done, r.Points, r.DonePoints)).ToList();
    }

    // ---------- Hierarchy writes (all through IssueService, which validates and records activity) ----------

    /// <summary>Activity text for the child when its parent changes, e.g. "set the parent to **WEB-9**".</summary>
    public static async Task<string> ParentChangeTextAsync(AppDbContext db, int? oldParentId, int? newParentId)
    {
        async Task<string?> KeyOf(int? id) => id is { } x
            ? await db.Issues.Where(i => i.Id == x).Select(i => i.Project.Key + "-" + i.Number).FirstOrDefaultAsync() : null;
        var (oldKey, newKey) = (await KeyOf(oldParentId), await KeyOf(newParentId));
        return newKey == null ? $"removed the parent **{oldKey}**"
            : oldKey == null ? $"set the parent to **{newKey}**"
            : $"changed the parent from **{oldKey}** to **{newKey}**";
    }

    public Task SetParentAsync(int issueId, int? parentId, int actorId) =>
        issues.UpdateIssueAsync(issueId, actorId, async (i, db) =>
        {
            if (i.ParentId == parentId) return null;
            var text = await ParentChangeTextAsync(db, i.ParentId, parentId);
            i.ParentId = parentId;
            return text;
        });

    /// <summary>Creates a child of <paramref name="parentId"/>: a sub-task, or for an epic a story/task/bug/feature.</summary>
    public async Task<Issue> CreateChildAsync(int parentId, string title, IssueType type, int actorId)
    {
        title = title.Trim();
        if (title.Length is 0 or > 200) throw new InvalidOperationException("Title is required (max 200 characters).");
        await using var db = await dbFactory.CreateDbContextAsync();
        var parent = await db.Issues.Where(i => i.Id == parentId).Select(i => new { i.ProjectId, i.MilestoneId }).FirstOrDefaultAsync()
            ?? throw new InvalidOperationException("Parent issue not found.");
        var child = new Issue
        {
            Title = title, Type = type, ParentId = parentId, Status = IssueStatus.Todo,
            MilestoneId = type == IssueType.Subtask ? parent.MilestoneId : null, // sub-tasks ride along in the parent's sprint
        };
        return await issues.CreateIssueAsync(parent.ProjectId, child, [], actorId);
    }

    /// <summary>
    /// Takes a child out of its parent. A sub-task can't exist on its own, so it becomes a Task.
    /// </summary>
    public Task DetachChildAsync(int childId, int actorId) =>
        issues.UpdateIssueAsync(childId, actorId, async (i, db) =>
        {
            if (i.ParentId == null) return null;
            var text = await ParentChangeTextAsync(db, i.ParentId, null);
            i.ParentId = null;
            if (i.Type != IssueType.Subtask) return text;
            i.Type = IssueType.Task;
            return text + " and converted it to a **Task**";
        });

    // ---------- Links ----------

    /// <summary>
    /// Links <paramref name="fromIssueId"/> to <paramref name="otherIssueId"/> with <paramref name="relation"/> as seen
    /// from the first. Requires EditIssues on the first issue's project and view access to the second.
    /// </summary>
    public async Task<IssueLinkView> AddLinkAsync(int fromIssueId, int otherIssueId, IssueLinkRelation relation, int actorId)
    {
        await access.RequireForIssueAsync(fromIssueId, Permission.EditIssues, actorId);
        if (fromIssueId == otherIssueId) throw new InvalidOperationException("An issue can't be linked to itself.");
        var visible = await access.VisibleProjectIdsAsync(actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var from = await db.Issues.Where(i => i.Id == fromIssueId).Select(ToRef).FirstAsync();
        var other = await db.Issues.Where(i => i.Id == otherIssueId && visible.Contains(i.ProjectId)).Select(ToRef).FirstOrDefaultAsync()
            ?? throw new InvalidOperationException("The issue to link doesn't exist, or you can't view it.");

        var (type, outgoing) = Split(relation);
        var (source, target) = outgoing ? (from.Id, other.Id) : (other.Id, from.Id);
        if (await db.IssueLinks.AnyAsync(l => l.Type == type &&
                ((l.SourceIssueId == source && l.TargetIssueId == target) || (l.SourceIssueId == target && l.TargetIssueId == source))))
            throw new InvalidOperationException($"{from.Key} and {other.Key} already have a “{type switch { IssueLinkType.RelatesTo => "relates to", _ => type.ToString().ToLowerInvariant() }}” link.");

        var link = new IssueLink { SourceIssueId = source, TargetIssueId = target, Type = type, CreatedById = actorId, CreatedAt = DateTime.UtcNow };
        db.IssueLinks.Add(link);
        AddActivity(db, from.Id, actorId, $"added a link: **{Phrase(relation)}** [{other.Key}]({other.Url})");
        AddActivity(db, other.Id, actorId, $"added a link: **{Phrase(Inverse(relation))}** [{from.Key}]({from.Url})");
        await db.SaveChangesAsync();
        await TouchAsync(db, from.Id, other.Id);
        await notifications.OnIssueLinkChangedAsync(source, target, actorId);
        return new IssueLinkView(link.Id, relation, other, link.CreatedAt);
    }

    /// <summary>Removes a link of <paramref name="fromIssueId"/>. Requires EditIssues on that issue's project.</summary>
    public async Task RemoveLinkAsync(int linkId, int fromIssueId, int actorId)
    {
        await access.RequireForIssueAsync(fromIssueId, Permission.EditIssues, actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var link = await db.IssueLinks.FirstOrDefaultAsync(l => l.Id == linkId && (l.SourceIssueId == fromIssueId || l.TargetIssueId == fromIssueId))
            ?? throw new InvalidOperationException("Link not found.");
        var outgoing = link.SourceIssueId == fromIssueId;
        var otherId = outgoing ? link.TargetIssueId : link.SourceIssueId;
        var refs = await db.Issues.Where(i => i.Id == fromIssueId || i.Id == otherId).Select(ToRef).ToDictionaryAsync(i => i.Id);
        var relation = Relation(link.Type, outgoing);
        db.IssueLinks.Remove(link);
        AddActivity(db, fromIssueId, actorId, $"removed the link: **{Phrase(relation)}** [{refs[otherId].Key}]({refs[otherId].Url})");
        AddActivity(db, otherId, actorId, $"removed the link: **{Phrase(Inverse(relation))}** [{refs[fromIssueId].Key}]({refs[fromIssueId].Url})");
        await db.SaveChangesAsync();
        await TouchAsync(db, fromIssueId, otherId);
        await notifications.OnIssueLinkChangedAsync(link.SourceIssueId, link.TargetIssueId, actorId);
    }

    private static void AddActivity(AppDbContext db, int issueId, int actorId, string text) =>
        db.Activities.Add(new Activity { IssueId = issueId, ActorId = actorId, Text = text, CreatedAt = DateTime.UtcNow });

    private static Task TouchAsync(AppDbContext db, params int[] ids) =>
        db.Issues.Where(i => ids.Contains(i.Id)).ExecuteUpdateAsync(s => s.SetProperty(i => i.UpdatedAt, DateTime.UtcNow));
}
