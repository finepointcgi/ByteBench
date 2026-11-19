using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Services.Audit;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services;

/// <summary>A status change worked out by <see cref="IssueService.ReconcileStatusAsync"/>.</summary>
public sealed record StatusChange(WorkflowStatus? From, WorkflowStatus To, IssueStatus? FromCategory)
{
    /// <summary>Activity text, e.g. "changed status from **To Do** to **Blocked**".</summary>
    public string Describe() => $"changed status from **{From?.Name ?? FromCategory?.Name() ?? "none"}** to **{To.Name}**";
}

/// <summary>A project's workflow: statuses in board order and the allowed transitions between them.</summary>
public sealed class ProjectWorkflow(List<WorkflowStatus> statuses, List<WorkflowTransition> transitions)
{
    public static readonly ProjectWorkflow Empty = new([], []);

    public IReadOnlyList<WorkflowStatus> Statuses { get; } = statuses.OrderBy(s => s.Order).ThenBy(s => s.Id).ToList();
    public IReadOnlyList<WorkflowTransition> Transitions => transitions;

    public WorkflowStatus? Find(int? id) => id == null ? null : Statuses.FirstOrDefault(s => s.Id == id);

    /// <summary>The first status (in board order) of a category: what setting only a category picks.</summary>
    public WorkflowStatus? FirstOf(IssueStatus category) => Statuses.FirstOrDefault(s => s.Category == category);

    /// <summary>The status an issue is shown in: its workflow status if consistent with its category, else the category's first status.</summary>
    public WorkflowStatus? Effective(IssueStatus category, int? workflowStatusId) =>
        Find(workflowStatusId) is { } s && s.Category == category ? s : FirstOf(category);

    public WorkflowStatus? Effective(Issue issue) => Effective(issue.Status, issue.WorkflowStatusId);

    /// <summary>True when the status lists its allowed targets (otherwise it may move anywhere).</summary>
    public bool IsRestricted(int fromId) => transitions.Any(t => t.FromStatusId == fromId);

    public bool IsAllowed(int? fromId, int toId) =>
        fromId is not { } f || f == toId || !IsRestricted(f) || transitions.Any(t => t.FromStatusId == f && t.ToStatusId == toId);

    /// <summary>Statuses an issue currently in <paramref name="from"/> may move to (including staying put).</summary>
    public IEnumerable<WorkflowStatus> AllowedFrom(WorkflowStatus? from) => Statuses.Where(s => IsAllowed(from?.Id, s.Id));
}

/// <summary>
/// Per-project workflows: statuses (name, colour, order, category) and optional allowed transitions. Reading needs
/// <see cref="Permission.ViewProject"/>; every change needs <see cref="Permission.ManageProjectSettings"/> and is audited.
/// Moving issues between statuses goes through <see cref="IssueService"/> (<see cref="IssueService.ReconcileStatusAsync"/>).
/// <para>Rules: names are unique within the project; there is always at least one Done-category status and at least one open one.</para>
/// </summary>
public partial class WorkflowService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, AuditLog audit, LiveEvents live)
{
    public const int MaxStatuses = 30;

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorRx();

    /// <summary>Today's five statuses, one per category.</summary>
    public static List<WorkflowStatus> DefaultStatuses(int projectId = 0) =>
    [
        new() { ProjectId = projectId, Name = IssueStatus.Backlog.Name(), Category = IssueStatus.Backlog, Color = "#9ca3af", Order = 0 },
        new() { ProjectId = projectId, Name = IssueStatus.Todo.Name(), Category = IssueStatus.Todo, Color = "#3b82f6", Order = 1 },
        new() { ProjectId = projectId, Name = IssueStatus.InProgress.Name(), Category = IssueStatus.InProgress, Color = "#f59e0b", Order = 2 },
        new() { ProjectId = projectId, Name = IssueStatus.InReview.Name(), Category = IssueStatus.InReview, Color = "#a855f7", Order = 3 },
        new() { ProjectId = projectId, Name = IssueStatus.Done.Name(), Category = IssueStatus.Done, Color = "#22c55e", Order = 4 },
    ];

    /// <summary>
    /// Startup backfill: gives every project without a workflow the default one, and maps every issue whose workflow
    /// status is missing or doesn't match its category to the first status of that category.
    /// </summary>
    public static void EnsureDefaults(AppDbContext db)
    {
        var bare = db.Projects.Where(p => !p.WorkflowStatuses.Any()).Select(p => p.Id).ToList();
        foreach (var pid in bare) db.WorkflowStatuses.AddRange(DefaultStatuses(pid));
        if (bare.Count > 0) db.SaveChanges();

        var broken = db.Issues.Where(i => i.WorkflowStatusId == null || i.WorkflowStatus!.Category != i.Status).ToList();
        if (broken.Count == 0) return;
        var byProject = db.WorkflowStatuses.AsNoTracking().ToList().GroupBy(s => s.ProjectId)
            .ToDictionary(g => g.Key, g => new ProjectWorkflow([.. g], []));
        foreach (var i in broken)
            if (byProject.GetValueOrDefault(i.ProjectId)?.FirstOf(i.Status) is { } s) i.WorkflowStatusId = s.Id;
        db.SaveChanges();
    }

    /// <summary>The project's workflow without a permission check (creating the default one if it has none). Internal use.</summary>
    internal static async Task<ProjectWorkflow> LoadAsync(IDbContextFactory<AppDbContext> dbFactory, int projectId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var statuses = await db.WorkflowStatuses.AsNoTracking().Where(s => s.ProjectId == projectId).ToListAsync();
        if (statuses.Count == 0 && await db.Projects.AnyAsync(p => p.Id == projectId))
        {
            statuses = DefaultStatuses(projectId);
            db.WorkflowStatuses.AddRange(statuses);
            await db.SaveChangesAsync();
        }
        var transitions = await db.WorkflowTransitions.AsNoTracking().Where(t => t.ProjectId == projectId).ToListAsync();
        return new ProjectWorkflow(statuses, transitions);
    }

    /// <summary>The project's workflow, or an empty one if the acting user can't view the project.</summary>
    public async Task<ProjectWorkflow> GetAsync(int projectId) =>
        await access.CanAsync(projectId, Permission.ViewProject) ? await LoadAsync(dbFactory, projectId) : ProjectWorkflow.Empty;

    /// <summary>Number of issues in each workflow status of the project.</summary>
    public async Task<Dictionary<int, int>> CountIssuesAsync(int projectId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Issues.Where(i => i.ProjectId == projectId && i.WorkflowStatusId != null)
            .GroupBy(i => i.WorkflowStatusId!.Value).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
    }

    public async Task<WorkflowStatus> AddStatusAsync(int projectId, string name, string color, IssueStatus category)
    {
        await access.RequireAsync(projectId, Permission.ManageProjectSettings);
        await using var db = await dbFactory.CreateDbContextAsync();
        var all = await db.WorkflowStatuses.Where(s => s.ProjectId == projectId).ToListAsync();
        if (all.Count >= MaxStatuses) throw new InvalidOperationException($"A workflow can have at most {MaxStatuses} statuses.");
        var status = new WorkflowStatus
        {
            ProjectId = projectId, Name = ValidName(name), Color = ValidColor(color), Category = category,
            Order = all.Count == 0 ? 0 : all.Max(s => s.Order) + 1,
        };
        Validate([.. all, status]);
        db.WorkflowStatuses.Add(status);
        await db.SaveChangesAsync();
        await audit.RecordAsync("workflow.status_added", "workflow_status", status.Id, projectId, new { status.Name, status.Color, category = status.Category.ToString() });
        live.Publish(new BoardChanged(projectId));
        return status;
    }

    /// <summary>Renames, recolours or recategorises a status. Changing the category also changes the category of its issues.</summary>
    public async Task UpdateStatusAsync(int projectId, int statusId, string name, string color, IssueStatus category)
    {
        await access.RequireAsync(projectId, Permission.ManageProjectSettings);
        await using var db = await dbFactory.CreateDbContextAsync();
        var all = await db.WorkflowStatuses.Where(s => s.ProjectId == projectId).ToListAsync();
        var status = all.FirstOrDefault(s => s.Id == statusId) ?? throw new InvalidOperationException("Status not found.");
        var before = new { status.Name, status.Color, category = status.Category.ToString() };
        var oldCategory = status.Category;
        status.Name = ValidName(name);
        status.Color = ValidColor(color);
        status.Category = category;
        Validate(all);
        if (before.Name == status.Name && before.Color == status.Color && oldCategory == category) return;

        await using var tx = await db.Database.BeginTransactionAsync();
        await db.SaveChangesAsync();
        if (oldCategory != category)
        {
            var now = DateTime.UtcNow;
            var issues = db.Issues.Where(i => i.WorkflowStatusId == statusId);
            if (category == IssueStatus.Done)
                await issues.ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, category).SetProperty(i => i.ClosedAt, i => i.ClosedAt ?? now).SetProperty(i => i.UpdatedAt, now));
            else
                await issues.ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, category).SetProperty(i => i.ClosedAt, (DateTime?)null).SetProperty(i => i.UpdatedAt, now));
        }
        await tx.CommitAsync();
        await audit.RecordAsync("workflow.status_updated", "workflow_status", statusId, projectId,
            new { before, after = new { status.Name, status.Color, category = status.Category.ToString() } });
        live.Publish(new BoardChanged(projectId));
    }

    /// <summary>Moves a status one place left (<paramref name="delta"/> −1) or right (+1) on the board.</summary>
    public async Task MoveStatusAsync(int projectId, int statusId, int delta)
    {
        await access.RequireAsync(projectId, Permission.ManageProjectSettings);
        await using var db = await dbFactory.CreateDbContextAsync();
        var all = (await db.WorkflowStatuses.Where(s => s.ProjectId == projectId).ToListAsync()).OrderBy(s => s.Order).ThenBy(s => s.Id).ToList();
        var idx = all.FindIndex(s => s.Id == statusId);
        if (idx < 0) throw new InvalidOperationException("Status not found.");
        var target = Math.Clamp(idx + Math.Sign(delta), 0, all.Count - 1);
        if (target == idx) return;
        var moved = all[idx];
        all.RemoveAt(idx);
        all.Insert(target, moved);
        for (var i = 0; i < all.Count; i++) all[i].Order = i;
        await db.SaveChangesAsync();
        await audit.RecordAsync("workflow.status_reordered", "workflow_status", statusId, projectId,
            new { moved.Name, order = all.Select(s => s.Name).ToList() });
        live.Publish(new BoardChanged(projectId));
    }

    /// <summary>
    /// Deletes a status. If issues still have it, <paramref name="moveIssuesTo"/> must name another status of the project:
    /// they are moved there first (ignoring transition rules) with an activity entry each.
    /// </summary>
    public async Task<int> DeleteStatusAsync(int projectId, int statusId, int? moveIssuesTo)
    {
        await access.RequireAsync(projectId, Permission.ManageProjectSettings);
        var actorId = await access.ResolveUserIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var all = await db.WorkflowStatuses.Where(s => s.ProjectId == projectId).ToListAsync();
        var status = all.FirstOrDefault(s => s.Id == statusId) ?? throw new InvalidOperationException("Status not found.");
        var remaining = all.Where(s => s.Id != statusId).ToList();
        Validate(remaining, $"Can't delete \"{status.Name}\": ");

        var issues = await db.Issues.Where(i => i.WorkflowStatusId == statusId).ToListAsync();
        WorkflowStatus? target = null;
        if (issues.Count > 0)
        {
            target = remaining.FirstOrDefault(s => s.Id == moveIssuesTo)
                     ?? throw new InvalidOperationException($"{issues.Count} issue{(issues.Count == 1 ? " has" : "s have")} the status \"{status.Name}\". Choose a status to move {(issues.Count == 1 ? "it" : "them")} to first.");
            var now = DateTime.UtcNow;
            foreach (var i in issues)
            {
                if (i.Status != IssueStatus.Done && target.Category == IssueStatus.Done) i.ClosedAt = now;
                if (target.Category != IssueStatus.Done) i.ClosedAt = null;
                i.WorkflowStatusId = target.Id;
                i.Status = target.Category;
                i.UpdatedAt = now;
                if (actorId is { } a)
                    db.Activities.Add(new Activity { IssueId = i.Id, ActorId = a, Text = $"changed status from **{status.Name}** to **{target.Name}** (the status \"{status.Name}\" was removed from the workflow)" });
            }
        }
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.SaveChangesAsync();
        db.WorkflowStatuses.Remove(status); // transitions from/to it cascade
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        await audit.RecordAsync("workflow.status_deleted", "workflow_status", statusId, projectId,
            new { status.Name, category = status.Category.ToString(), movedIssues = issues.Count, movedTo = target?.Name });
        live.Publish(new BoardChanged(projectId));
        return issues.Count;
    }

    /// <summary>
    /// Replaces the allowed transitions. <paramref name="allowed"/> maps a status id to the ids it may move to; a status
    /// mapped to nothing (or missing) may move anywhere.
    /// </summary>
    public async Task SetTransitionsAsync(int projectId, IReadOnlyDictionary<int, HashSet<int>> allowed)
    {
        await access.RequireAsync(projectId, Permission.ManageProjectSettings);
        await using var db = await dbFactory.CreateDbContextAsync();
        var statuses = await db.WorkflowStatuses.Where(s => s.ProjectId == projectId).ToDictionaryAsync(s => s.Id);
        var rows = new List<WorkflowTransition>();
        foreach (var (from, targets) in allowed)
        {
            if (!statuses.ContainsKey(from)) throw new InvalidOperationException("Unknown status in transitions.");
            foreach (var to in targets.Where(t => t != from))
            {
                if (!statuses.ContainsKey(to)) throw new InvalidOperationException("Unknown status in transitions.");
                rows.Add(new WorkflowTransition { ProjectId = projectId, FromStatusId = from, ToStatusId = to });
            }
        }
        var existing = await db.WorkflowTransitions.Where(t => t.ProjectId == projectId).ToListAsync();
        static string Describe(IEnumerable<WorkflowTransition> ts, Dictionary<int, WorkflowStatus> st) =>
            string.Join(", ", ts.Select(t => $"{st[t.FromStatusId].Name} → {st[t.ToStatusId].Name}").Order());
        var before = Describe(existing.Where(t => statuses.ContainsKey(t.FromStatusId) && statuses.ContainsKey(t.ToStatusId)), statuses);
        var after = Describe(rows, statuses);
        if (before == after) return;

        await using var tx = await db.Database.BeginTransactionAsync();
        db.WorkflowTransitions.RemoveRange(existing);
        await db.SaveChangesAsync();
        db.WorkflowTransitions.AddRange(rows);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        await audit.RecordAsync("workflow.transitions_changed", "project", projectId, projectId,
            new { before = before.Length == 0 ? "(any move allowed)" : before, after = after.Length == 0 ? "(any move allowed)" : after });
        live.Publish(new BoardChanged(projectId));
    }

    // ---------- Validation ----------

    private static string ValidName(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length is 0 or > 40) throw new InvalidOperationException("Status names are required (max 40 characters).");
        return name;
    }

    private static string ValidColor(string color)
    {
        color = (color ?? "").Trim();
        if (!ColorRx().IsMatch(color)) throw new InvalidOperationException("Colours must look like #1a2b3c.");
        return color.ToLowerInvariant();
    }

    private static void Validate(List<WorkflowStatus> statuses, string prefix = "")
    {
        var dup = statuses.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new InvalidOperationException($"{prefix}There is already a status named \"{dup.Key}\".");
        if (!statuses.Any(s => s.Category == IssueStatus.Done))
            throw new InvalidOperationException($"{prefix}The workflow needs at least one status in the Done category.");
        if (!statuses.Any(s => s.Category != IssueStatus.Done))
            throw new InvalidOperationException($"{prefix}The workflow needs at least one open (not Done) status.");
    }
}
