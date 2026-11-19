using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Reports;

/// <summary>A finished issue with its lead time (created → done) and cycle time (first in progress → done), in days.</summary>
public sealed record IssueTimeRow(int IssueId, string Key, string Title, IssueType Type, string? Assignee, DateTime CreatedAt, DateTime? StartedAt,
    DateTime DoneAt, double LeadDays, double? CycleDays)
{
    public string Url => $"/p/{Key[..Key.LastIndexOf('-')]}/issues/{Key[(Key.LastIndexOf('-') + 1)..]}";
}

public sealed record HistogramBucket(string Label, int Index, int Count);

public sealed record TrendPoint(DateTime Period, int Count, double? LeadP50, double? LeadP85, double? CycleP50, double? CycleP85);

/// <param name="WithoutStart">Finished issues that never entered an In Progress status: counted in lead time only.</param>
public sealed record CycleTimeReport(Percentiles? Lead, Percentiles? Cycle, IReadOnlyList<HistogramBucket> LeadHistogram,
    IReadOnlyList<HistogramBucket> CycleHistogram, IReadOnlyList<TrendPoint> Trend, IReadOnlyList<IssueTimeRow> Issues, int WithoutStart, bool Truncated);

/// <param name="Bottleneck">Average time per issue is at least 1.5 times the mean of the other working statuses.</param>
public sealed record StatusTimeRow(string Status, IssueStatus Category, int Order, int Issues, double AvgDays, double MedianDays, double TotalDays,
    double SharePercent, bool Bottleneck);

public sealed record IssueStatusTimes(int IssueId, string Key, string Title, string CurrentStatus, double TotalDays, string LongestStatus,
    IReadOnlyDictionary<string, double> Days);

public sealed record TimeInStatusReport(IReadOnlyList<StatusTimeRow> Statuses, IReadOnlyList<IssueStatusTimes> Issues, int IssueCount);

public sealed record CfdSeries(string Status, IssueStatus Category);

public sealed record CfdDay(DateTime Date, IReadOnlyList<int> Counts);

/// <summary>Cumulative flow: for every day, how many issues were in each status at its end. <see cref="Series"/> runs from Done (bottom) to Backlog (top).</summary>
public sealed record CumulativeFlowReport(IReadOnlyList<CfdSeries> Series, IReadOnlyList<CfdDay> Days);

public sealed record ThroughputPeriod(DateTime Period, int Created, int Done, int Points);

public sealed record SprintThroughput(int SprintId, string Name, DateTime CompletedAt, int CommittedCount, int CompletedCount, int CommittedPoints, int CompletedPoints);

public sealed record WipRow(string Status, IssueStatus Category, int Order, int Issues, int Points);

public sealed record ThroughputReport(IReadOnlyList<ThroughputPeriod> Periods, IReadOnlyList<SprintThroughput> Sprints, double AverageDone);

public sealed record WipReport(IReadOnlyList<WipRow> Rows, int InProgress, int Total);

/// <summary>
/// Flow reports built from issues and their status-change activity (<c>changed status from **A** to **B**</c>, which
/// IssueService and WorkflowService write): cycle and lead time, time in status, cumulative flow, throughput and WIP.
/// Filtering and counting run in SQL; only the matching issues' status-change rows are read to rebuild timelines.
/// Every report needs ViewProject (<see cref="ProjectAccess"/>).
/// </summary>
public class DeliveryReportService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access)
{
    public const int MaxIssueRows = 5000;

    private sealed record IssueRow(int Id, int Number, string Title, IssueType Type, IssueStatus Status, string? StatusName, string? Assignee,
        DateTime CreatedAt, DateTime? ClosedAt, int? Points);

    private sealed record Loaded(Project Project, List<(IssueRow Issue, IssueTimeline Timeline)> Items, Func<string, IssueStatus> CategoryOf,
        List<WorkflowStatus> Workflow);

    // ----- Cycle & lead time -----

    public async Task<CycleTimeReport> CycleTimeAsync(int projectId, ReportFilter filter)
    {
        await access.RequireAsync(projectId, Permission.ViewProject);
        var (from, to, toEx, now) = filter.Resolve();
        await using var db = await dbFactory.CreateDbContextAsync();
        var loaded = await LoadAsync(db, projectId, filter, q => q.Where(i => i.Status == IssueStatus.Done && i.ClosedAt != null && i.ClosedAt >= from && i.ClosedAt < toEx));

        var rows = new List<IssueTimeRow>();
        foreach (var (i, tl) in loaded.Items)
        {
            var done = i.ClosedAt ?? tl.DoneAt;
            if (done is not { } d) continue;
            var started = tl.StartedAt;
            double? cycle = started is { } s ? ReportMath.Days(d - s) : null;
            rows.Add(new IssueTimeRow(i.Id, $"{loaded.Project.Key}-{i.Number}", i.Title, i.Type, i.Assignee, i.CreatedAt, started, d, ReportMath.Days(d - i.CreatedAt), cycle));
        }
        rows = rows.OrderByDescending(r => r.DoneAt).ToList();

        var lead = rows.Select(r => r.LeadDays).ToList();
        var cycle2 = rows.Where(r => r.CycleDays != null).Select(r => r.CycleDays!.Value).ToList();
        var monthly = filter.Monthly;
        var trend = ReportMath.Periods(from, to, monthly).Select(p =>
        {
            var next = ReportMath.NextPeriod(p, monthly);
            var inP = rows.Where(r => r.DoneAt >= p && r.DoneAt < next).ToList();
            var l = inP.Select(r => r.LeadDays).ToList();
            var c = inP.Where(r => r.CycleDays != null).Select(r => r.CycleDays!.Value).ToList();
            return new TrendPoint(p, inP.Count, l.Count == 0 ? null : Round(ReportMath.Percentile(l, 50)), l.Count == 0 ? null : Round(ReportMath.Percentile(l, 85)),
                c.Count == 0 ? null : Round(ReportMath.Percentile(c, 50)), c.Count == 0 ? null : Round(ReportMath.Percentile(c, 85)));
        }).ToList();

        return new CycleTimeReport(ReportMath.Summarize(lead), ReportMath.Summarize(cycle2), Histogram(lead), Histogram(cycle2), trend,
            rows.Take(MaxIssueRows).ToList(), rows.Count - cycle2.Count, rows.Count > MaxIssueRows);
    }

    private static double Round(double v) => Math.Round(v, 2);

    private static List<HistogramBucket> Histogram(IReadOnlyList<double> days)
    {
        var counts = new int[ReportMath.BucketBounds.Length + 1];
        foreach (var d in days) counts[ReportMath.BucketOf(d)]++;
        return counts.Select((c, k) => new HistogramBucket(ReportMath.BucketLabel(k), k, c)).ToList();
    }

    // ----- Time in status -----

    public async Task<TimeInStatusReport> TimeInStatusAsync(int projectId, ReportFilter filter)
    {
        await access.RequireAsync(projectId, Permission.ViewProject);
        var (from, _, toEx, now) = filter.Resolve();
        await using var db = await dbFactory.CreateDbContextAsync();
        // Issues that existed at some point in the window.
        var loaded = await LoadAsync(db, projectId, filter, q => q.Where(i => i.CreatedAt < toEx && (i.ClosedAt == null || i.ClosedAt >= from)));
        var windowEnd = now < toEx ? now : toEx;

        var perIssue = new List<(IssueRow Issue, Dictionary<string, double> Days)>();
        foreach (var (i, tl) in loaded.Items)
        {
            var days = new Dictionary<string, double>();
            foreach (var seg in tl.Segments)
            {
                if (seg.Category == IssueStatus.Done) continue;
                var s = seg.Start < from ? from : seg.Start;
                var e = seg.End ?? windowEnd;
                if (e > windowEnd) e = windowEnd;
                if (e <= s) continue;
                days[seg.Status] = days.GetValueOrDefault(seg.Status) + (e - s).TotalDays;
            }
            if (days.Count > 0) perIssue.Add((i, days));
        }

        var order = StatusOrder(loaded);
        var grand = perIssue.Sum(p => p.Days.Values.Sum());
        var rows = perIssue.SelectMany(p => p.Days).GroupBy(kv => kv.Key).Select(g =>
        {
            var vals = g.Select(kv => kv.Value).ToList();
            var (cat, ord) = order.GetValueOrDefault(g.Key, (loaded.CategoryOf(g.Key), 1000));
            return new StatusTimeRow(g.Key, cat, ord, vals.Count, Round(vals.Average()), Round(ReportMath.Percentile(vals, 50)), Round(vals.Sum()),
                grand <= 0 ? 0 : Math.Round(vals.Sum() / grand * 100, 1), false);
        }).OrderBy(r => r.Order).ThenBy(r => r.Status).ToList();

        // Bottlenecks: working statuses (not Backlog, not Done) whose average is >= 1.5x the mean of the other working statuses.
        var working = rows.Where(r => r.Category != IssueStatus.Backlog).ToList();
        for (var k = 0; k < rows.Count; k++)
        {
            var r = rows[k];
            var others = working.Where(w => w.Status != r.Status).ToList();
            if (r.Category != IssueStatus.Backlog && others.Count > 0 && r.AvgDays >= 1.5 * others.Average(o => o.AvgDays) && r.AvgDays > 0)
                rows[k] = r with { Bottleneck = true };
        }

        var issueRows = perIssue.Select(p =>
        {
            var total = p.Days.Values.Sum();
            var longest = p.Days.OrderByDescending(kv => kv.Value).First().Key;
            var cur = p.Issue.StatusName ?? p.Issue.Status.Name();
            return new IssueStatusTimes(p.Issue.Id, $"{loaded.Project.Key}-{p.Issue.Number}", p.Issue.Title, cur, Round(total), longest,
                p.Days.ToDictionary(kv => kv.Key, kv => Round(kv.Value)));
        }).OrderByDescending(r => r.TotalDays).Take(500).ToList();

        return new TimeInStatusReport(rows, issueRows, perIssue.Count);
    }

    // ----- Cumulative flow -----

    public async Task<CumulativeFlowReport> CumulativeFlowAsync(int projectId, ReportFilter filter)
    {
        await access.RequireAsync(projectId, Permission.ViewProject);
        var (from, to, toEx, _) = filter.Resolve();
        await using var db = await dbFactory.CreateDbContextAsync();
        var loaded = await LoadAsync(db, projectId, filter, q => q.Where(i => i.CreatedAt < toEx));

        var order = StatusOrder(loaded);
        // Done at the bottom, then the workflow from last to first.
        var names = loaded.Items.SelectMany(x => x.Timeline.Segments).Select(s => s.Status).Concat(order.Keys).Distinct()
            .OrderBy(nm => loaded.CategoryOf(nm) == IssueStatus.Done ? 0 : 1)
            .ThenByDescending(nm => order.GetValueOrDefault(nm, (IssueStatus.Todo, 1000)).Item2).ToList();
        var index = names.Select((nm, k) => (nm, k)).ToDictionary(x => x.nm, x => x.k);

        var n = (int)(to - from).TotalDays + 1; // days in the window
        var diff = new int[names.Count, n + 1];
        foreach (var (_, tl) in loaded.Items)
            foreach (var seg in tl.Segments)
            {
                var first = Math.Max(0, (int)(seg.Start.Date - from).TotalDays);
                var last = seg.End is { } e ? (int)(e.Date - from).TotalDays - 1 : n - 1;
                last = Math.Min(last, n - 1);
                if (first > last) continue;
                var k = index[seg.Status];
                diff[k, first]++;
                diff[k, last + 1]--;
            }

        var running = new int[names.Count];
        var days = new List<CfdDay>(n);
        for (var d = 0; d < n; d++)
        {
            var counts = new int[names.Count];
            for (var k = 0; k < names.Count; k++) { running[k] += diff[k, d]; counts[k] = running[k]; }
            days.Add(new CfdDay(from.AddDays(d), counts));
        }
        return new CumulativeFlowReport(names.Select(x => new CfdSeries(x, loaded.CategoryOf(x))).ToList(), days);
    }

    // ----- Throughput & WIP -----

    public async Task<ThroughputReport> ThroughputAsync(int projectId, ReportFilter filter)
    {
        await access.RequireAsync(projectId, Permission.ViewProject);
        var (from, to, toEx, _) = filter.Resolve();
        await using var db = await dbFactory.CreateDbContextAsync();
        var scope = Scope(db, projectId, filter);
        var done = await scope.Where(i => i.Status == IssueStatus.Done && i.ClosedAt != null && i.ClosedAt >= from && i.ClosedAt < toEx)
            .Select(i => new { ClosedAt = i.ClosedAt!.Value, i.StoryPoints }).ToListAsync();
        var created = await scope.Where(i => i.CreatedAt >= from && i.CreatedAt < toEx).Select(i => i.CreatedAt).ToListAsync();
        var monthly = filter.Monthly;
        var periods = ReportMath.Periods(from, to, monthly).Select(p =>
        {
            var next = ReportMath.NextPeriod(p, monthly);
            var d = done.Where(x => x.ClosedAt >= p && x.ClosedAt < next).ToList();
            return new ThroughputPeriod(p, created.Count(c => c >= p && c < next), d.Count, d.Sum(x => x.StoryPoints ?? 0));
        }).ToList();

        var sprints = (await db.Sprints.AsNoTracking()
            .Where(s => s.ProjectId == projectId && s.State == SprintState.Completed && s.CompletedAt != null && s.CompletedAt >= from && s.CompletedAt < toEx)
            .OrderBy(s => s.CompletedAt).ToListAsync())
            .Select(s => new SprintThroughput(s.Id, s.Name, s.CompletedAt!.Value, s.CommittedCount ?? 0, s.CompletedCount ?? 0, s.CommittedPoints ?? 0, s.CompletedPoints ?? 0)).ToList();
        return new ThroughputReport(periods, sprints, periods.Count == 0 ? 0 : Math.Round(periods.Average(p => (double)p.Done), 2));
    }

    /// <summary>Open issues per status right now (a SQL group-by), with how many are in progress or in review.</summary>
    public async Task<WipReport> WipAsync(int projectId, ReportFilter filter)
    {
        await access.RequireAsync(projectId, Permission.ViewProject);
        await using var db = await dbFactory.CreateDbContextAsync();
        var workflow = await db.WorkflowStatuses.AsNoTracking().Where(w => w.ProjectId == projectId).ToListAsync();
        var groups = await Scope(db, projectId, filter).Where(i => i.Status != IssueStatus.Done)
            .GroupBy(i => new { i.WorkflowStatusId, i.Status })
            .Select(g => new { g.Key.WorkflowStatusId, g.Key.Status, Count = g.Count(), Points = g.Sum(x => x.StoryPoints ?? 0) }).ToListAsync();
        var rows = groups.Select(g =>
        {
            var w = workflow.FirstOrDefault(x => x.Id == g.WorkflowStatusId);
            return new WipRow(w?.Name ?? g.Status.Name(), g.Status, w?.Order ?? (int)g.Status, g.Count, g.Points);
        }).GroupBy(r => r.Status).Select(g => g.First() with { Issues = g.Sum(x => x.Issues), Points = g.Sum(x => x.Points) })
            .OrderBy(r => r.Category).ThenBy(r => r.Order).ToList();
        return new WipReport(rows, rows.Where(r => r.Category is IssueStatus.InProgress or IssueStatus.InReview).Sum(r => r.Issues), rows.Sum(r => r.Issues));
    }

    // ----- Shared -----

    private static IQueryable<Issue> Scope(AppDbContext db, int projectId, ReportFilter f)
    {
        var q = db.Issues.AsNoTracking().Where(i => i.ProjectId == projectId);
        if (f.Type is { } t) q = q.Where(i => i.Type == t);
        if (f.LabelId is { } l) q = q.Where(i => i.Labels.Any(x => x.Id == l));
        if (f.ComponentId is { } c) q = q.Where(i => i.Components.Any(x => x.Id == c));
        if (f.AssigneeId is { } a) q = a == 0 ? q.Where(i => i.AssigneeId == null) : q.Where(i => i.AssigneeId == a);
        return q;
    }

    private static async Task<Loaded> LoadAsync(AppDbContext db, int projectId, ReportFilter filter, Func<IQueryable<Issue>, IQueryable<Issue>> narrow)
    {
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId);
        var workflow = await db.WorkflowStatuses.AsNoTracking().Where(w => w.ProjectId == projectId).OrderBy(w => w.Order).ToListAsync();
        var byName = new Dictionary<string, IssueStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Enum.GetValues<IssueStatus>()) byName[s.Name()] = s;
        foreach (var w in workflow) byName[w.Name] = w.Category;
        IssueStatus CategoryOf(string name) => byName.GetValueOrDefault(name, IssueStatus.Todo);

        var scope = narrow(Scope(db, projectId, filter));
        var issues = await scope.Select(i => new IssueRow(i.Id, i.Number, i.Title, i.Type, i.Status, i.WorkflowStatus != null ? i.WorkflowStatus.Name : null,
            i.Assignee != null ? i.Assignee.DisplayName : null, i.CreatedAt, i.ClosedAt, i.StoryPoints)).ToListAsync();

        var ids = scope.Select(i => i.Id);
        var changes = await db.Activities.AsNoTracking()
            .Where(a => ids.Contains(a.IssueId) && a.Text.StartsWith("changed status from"))
            .Select(a => new { a.IssueId, a.CreatedAt, a.Text }).ToListAsync();
        var byIssue = new Dictionary<int, List<(DateTime At, string From, string To)>>();
        foreach (var c in changes)
            if (ReportMath.TryParseStatusChange(c.Text, out var from, out var to))
            {
                if (!byIssue.TryGetValue(c.IssueId, out var l)) byIssue[c.IssueId] = l = [];
                l.Add((c.CreatedAt, from, to));
            }

        var items = issues.Select(i => (i, ReportMath.BuildTimeline(i.CreatedAt, i.StatusName ?? i.Status.Name(),
            byIssue.GetValueOrDefault(i.Id) ?? [], CategoryOf))).ToList();
        return new Loaded(project, items, CategoryOf, workflow);
    }

    /// <summary>Workflow status → (category, board order); statuses no longer in the workflow sort after these.</summary>
    private static Dictionary<string, (IssueStatus, int)> StatusOrder(Loaded l)
    {
        var d = new Dictionary<string, (IssueStatus, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Enum.GetValues<IssueStatus>()) d[s.Name()] = (s, (int)s * 10);
        foreach (var w in l.Workflow) d[w.Name] = (w.Category, w.Order);
        return d;
    }
}
