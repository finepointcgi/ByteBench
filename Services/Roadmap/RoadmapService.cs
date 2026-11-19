using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Roadmap;

/// <summary>Which epics a roadmap shows: an epic is kept when it or one of its children matches (and then only matching children are listed).</summary>
public sealed record RoadmapFilter(int? LabelId = null, int? AssigneeId = null);

/// <summary>An epic or a child issue on the roadmap. <see cref="Done"/>/<see cref="Total"/> count the epic's children (all of them, unfiltered).</summary>
public sealed record RoadmapItem(int Id, int ProjectId, string ProjectKey, int Number, string Title, IssueType Type, IssueStatus Status,
    DateTime? StartDate, DateTime? DueDate, User? Assignee, IReadOnlyList<Label> Labels, int Done, int Total, bool CanEdit,
    IReadOnlyList<RoadmapItem> Children)
{
    public string Key => $"{ProjectKey}-{Number}";
    public string Url => $"/p/{ProjectKey}/issues/{Number}";
    public bool IsDone => Status == IssueStatus.Done;
    public bool IsScheduled => StartDate != null || DueDate != null;
    /// <summary>Child completion in percent; a childless issue is 100 when done, else 0.</summary>
    public int Percent => Total == 0 ? (IsDone ? 100 : 0) : Done * 100 / Total;
}

/// <summary>A "blocks" link between two issues on the roadmap: <see cref="FromIssueId"/> blocks <see cref="ToIssueId"/>.</summary>
public sealed record RoadmapDependency(int FromIssueId, int ToIssueId);

public sealed record RoadmapSprint(int Id, string ProjectKey, string Name, DateTime Start, DateTime End, SprintState State);

public sealed record RoadmapMilestone(int Id, string Title, DateTime Due, bool IsClosed, string? ProjectKey);

public sealed record RoadmapData(IReadOnlyList<RoadmapItem> Epics, IReadOnlyList<RoadmapDependency> Dependencies,
    IReadOnlyList<RoadmapSprint> Sprints, IReadOnlyList<RoadmapMilestone> Milestones,
    IReadOnlyList<Label> Labels, IReadOnlyList<User> Assignees)
{
    public static readonly RoadmapData Empty = new([], [], [], [], [], []);

    /// <summary>Every epic and child, in display order.</summary>
    public IEnumerable<RoadmapItem> AllItems => Epics.SelectMany(e => e.Children.Prepend(e));
}

/// <summary>
/// The epic timeline (<c>/p/{key}/roadmap</c>, <c>/g/{path}/-/roadmap</c>). Reads cover only projects the acting user can
/// view (<see cref="ProjectAccess"/>); date changes go through <see cref="IssueService.UpdateIssueAsync"/>, so they need
/// EditIssues, are written to the issue's activity, and publish the usual live events and webhooks.
/// </summary>
public class RoadmapService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, IssueService issues)
{
    /// <summary>The roadmap of the given projects (those the acting user can't view are skipped).</summary>
    public async Task<RoadmapData> GetAsync(IReadOnlyCollection<int> projectIds, RoadmapFilter? filter = null)
    {
        filter ??= new RoadmapFilter();
        var visible = await access.VisibleProjectIdsAsync();
        var ids = projectIds.Where(visible.Contains).Distinct().ToList();
        if (ids.Count == 0) return RoadmapData.Empty;

        await using var db = await dbFactory.CreateDbContextAsync();
        var epics = await db.Issues.AsNoTracking()
            .Where(i => ids.Contains(i.ProjectId) && i.Type == IssueType.Epic)
            .Include(i => i.Project).Include(i => i.Assignee).Include(i => i.Labels)
            .AsSplitQuery().ToListAsync();
        var epicIds = epics.Select(e => e.Id).ToList();
        var children = await db.Issues.AsNoTracking()
            .Where(i => i.ParentId != null && epicIds.Contains(i.ParentId.Value))
            .Include(i => i.Project).Include(i => i.Assignee).Include(i => i.Labels)
            .AsSplitQuery().ToListAsync();
        var byParent = children.ToLookup(c => c.ParentId!.Value);

        var perms = new Dictionary<int, bool>();
        foreach (var pid in ids) perms[pid] = await access.CanAsync(pid, Permission.EditIssues);

        bool Matches(Issue i) =>
            (filter.LabelId is not { } l || i.Labels.Any(x => x.Id == l)) &&
            (filter.AssigneeId is not { } a || (a == 0 ? i.AssigneeId == null : i.AssigneeId == a));
        var filtering = filter.LabelId != null || filter.AssigneeId != null;

        RoadmapItem ToItem(Issue i, IReadOnlyList<RoadmapItem> kids, int done, int total) =>
            new(i.Id, i.ProjectId, i.Project.Key, i.Number, i.Title, i.Type, i.Status, i.StartDate, i.DueDate, i.Assignee,
                i.Labels.OrderBy(x => x.Name).ToList(), done, total, perms.GetValueOrDefault(i.ProjectId), kids);

        var items = new List<RoadmapItem>();
        foreach (var e in epics)
        {
            var all = byParent[e.Id].ToList();
            var shown = filtering && !Matches(e) ? all.Where(Matches).ToList() : all;
            if (filtering && !Matches(e) && shown.Count == 0) continue;
            var kids = shown.OrderBy(c => c.StartDate ?? c.DueDate ?? DateTime.MaxValue).ThenBy(c => c.Number)
                .Select(c => ToItem(c, [], 0, 0)).ToList();
            items.Add(ToItem(e, kids, all.Count(c => c.Status == IssueStatus.Done), all.Count));
        }
        items = Order(items);

        var shownIds = items.SelectMany(e => e.Children.Prepend(e)).Select(i => i.Id).ToHashSet();
        var shownList = shownIds.ToList();
        var deps = await db.IssueLinks.AsNoTracking()
            .Where(l => l.Type == IssueLinkType.Blocks && shownList.Contains(l.SourceIssueId) && shownList.Contains(l.TargetIssueId))
            .Select(l => new RoadmapDependency(l.SourceIssueId, l.TargetIssueId)).ToListAsync();

        var sprints = await db.Sprints.AsNoTracking()
            .Where(s => ids.Contains(s.ProjectId) && s.StartDate != null && s.EndDate != null)
            .Join(db.Projects, s => s.ProjectId, p => p.Id, (s, p) => new { s, p.Key })
            .ToListAsync();

        // Milestones of the projects and of their groups (inherited), each once.
        var groupIds = new HashSet<int>();
        foreach (var gid in await db.Projects.Where(p => ids.Contains(p.Id) && p.GroupId != null).Select(p => p.GroupId!.Value).Distinct().ToListAsync())
            groupIds.UnionWith(await ProjectAccess.GroupChainAsync(db, gid));
        var groupList = groupIds.ToList();
        var milestones = await db.Milestones.AsNoTracking()
            .Where(m => m.DueDate != null && ((m.ProjectId != null && ids.Contains(m.ProjectId.Value)) || (m.GroupId != null && groupList.Contains(m.GroupId.Value))))
            .Select(m => new { m, Key = db.Projects.Where(p => p.Id == m.ProjectId).Select(p => p.Key).FirstOrDefault() })
            .ToListAsync();

        // Filter options come from every epic and child (before filtering), so a filter can always be changed back.
        var everyone = epics.Concat(children).ToList();
        return new RoadmapData(items, deps,
            sprints.Select(x => new RoadmapSprint(x.s.Id, x.Key, x.s.Name, x.s.StartDate!.Value.Date, x.s.EndDate!.Value.Date, x.s.State))
                .OrderBy(s => s.Start).ToList(),
            milestones.Select(x => new RoadmapMilestone(x.m.Id, x.m.Title, x.m.DueDate!.Value.Date, x.m.IsClosed, x.Key))
                .OrderBy(m => m.Due).ToList(),
            everyone.SelectMany(i => i.Labels).DistinctBy(l => l.Id).OrderBy(l => l.Name).ToList(),
            everyone.Where(i => i.Assignee != null).Select(i => i.Assignee!).DistinctBy(u => u.Id).OrderBy(u => u.DisplayName).ToList());
    }

    /// <summary>Scheduled epics by start (or target) date, then unscheduled ones; done epics after open ones with the same dates.</summary>
    public static List<RoadmapItem> Order(IEnumerable<RoadmapItem> items) => items
        .OrderBy(i => i.IsScheduled ? 0 : 1)
        .ThenBy(i => i.StartDate ?? i.DueDate ?? DateTime.MaxValue)
        .ThenBy(i => i.DueDate ?? DateTime.MaxValue)
        .ThenBy(i => i.ProjectKey).ThenBy(i => i.Number)
        .ToList();

    /// <summary>
    /// Sets an issue's start and target (due) dates (EditIssues). Dates are whole days; the start must not be after the
    /// target. Writes one activity entry describing the change; nothing happens when both dates are unchanged.
    /// </summary>
    public async Task SetDatesAsync(int issueId, DateTime? start, DateTime? due, int actorId)
    {
        start = start?.Date;
        due = due?.Date;
        if (start is { } s && due is { } d && s > d)
            throw new InvalidOperationException("The start date must be on or before the target date.");
        await issues.UpdateIssueAsync(issueId, actorId, (i, _) =>
        {
            var text = DatesText(i.StartDate, i.DueDate, start, due);
            i.StartDate = start;
            i.DueDate = due;
            return Task.FromResult(text);
        });
    }

    /// <summary>The activity text for a date change, or null when nothing changed.</summary>
    public static string? DatesText(DateTime? oldStart, DateTime? oldDue, DateTime? newStart, DateTime? newDue)
    {
        var parts = new List<string>();
        if (oldStart?.Date != newStart?.Date)
            parts.Add(newStart is { } s ? $"set the start date to **{s:MMM d, yyyy}**" : "removed the start date");
        if (oldDue?.Date != newDue?.Date)
            parts.Add(newDue is { } d ? $"set the due date to **{d:MMM d, yyyy}**" : "removed the due date");
        return parts.Count == 0 ? null : string.Join(" and ", parts);
    }

    /// <summary>
    /// New dates after dragging a bar by <paramref name="days"/>: <c>move</c> shifts both dates, <c>start</c> and
    /// <c>end</c> resize one end (never past the other). A missing date stays missing when moving; resizing a bar
    /// that has only one date fills in the other end from it.
    /// </summary>
    public static (DateTime? Start, DateTime? Due) ApplyDrag(DateTime? start, DateTime? due, string mode, int days) => mode switch
    {
        "move" => (start?.AddDays(days), due?.AddDays(days)),
        "start" when (start ?? due) is { } s0 => (Min(s0.AddDays(days), due), due),
        "end" when (due ?? start) is { } e0 => (start, Max(e0.AddDays(days), start)),
        _ => (start, due),
    };

    private static DateTime Min(DateTime a, DateTime? b) => b is { } x && x < a ? x : a;
    private static DateTime Max(DateTime a, DateTime? b) => b is { } x && x > a ? x : a;
}
