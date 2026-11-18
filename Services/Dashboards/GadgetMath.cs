using GitBench.Data;

namespace GitBench.Services.Dashboards;

/// <summary>One slice or bar of an issue chart. <see cref="Slot"/> is the categorical colour slot (1–8; 0 = "Other").</summary>
public sealed record ChartSlice(string Label, int Count, int Slot);

/// <summary>Created and resolved issues on one day, plus running totals over the window.</summary>
public sealed record CreatedResolvedDay(DateTime Date, int Created, int Resolved, int TotalCreated, int TotalResolved);

/// <summary>Pure aggregation for dashboard gadgets (no database, no permissions: callers pass issues the viewer may see).</summary>
public static class GadgetMath
{
    public static readonly string[] Fields = ["status", "assignee", "priority", "type", "label"];
    public static readonly string[] Columns = ["key", "title", "type", "status", "priority", "assignee", "due", "points", "updated"];
    public static readonly string[] DefaultColumns = ["key", "title", "status", "assignee"];

    /// <summary>Distinct categorical slots before the rest fold into "Other".</summary>
    public const int MaxSlices = 7;

    public static string FieldName(string field) => field switch
    {
        "assignee" => "Assignee", "priority" => "Priority", "type" => "Type", "label" => "Label", _ => "Status",
    };

    public static string ColumnName(string column) => column switch
    {
        "key" => "Key", "title" => "Title", "type" => "Type", "status" => "Status", "priority" => "Priority",
        "assignee" => "Assignee", "due" => "Due", "points" => "Points", "updated" => "Updated", _ => column,
    };

    /// <summary>
    /// Counts issues per value of <paramref name="field"/>. An issue with several labels counts once per label (and
    /// "No label" when it has none). Status, priority and type keep their natural order; assignees and labels are
    /// sorted by count. Values past <see cref="MaxSlices"/> fold into one "Other" slice. Colours follow the value's
    /// position in that order, so the same value keeps its colour while counts change.
    /// </summary>
    public static List<ChartSlice> CountBy(IEnumerable<Issue> issues, string field)
    {
        var list = issues.ToList();
        IEnumerable<(string Label, int Count)> groups = field switch
        {
            "assignee" => list.GroupBy(i => i.Assignee?.DisplayName ?? "Unassigned").Select(g => (g.Key, g.Count()))
                .OrderByDescending(g => g.Item2).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase),
            "priority" => list.GroupBy(i => i.Priority).OrderByDescending(g => g.Key).Select(g => (g.Key.ToString(), g.Count())),
            "type" => list.GroupBy(i => i.Type).OrderBy(g => g.Key).Select(g => (g.Key.ToString(), g.Count())),
            "label" => list.SelectMany(i => i.Labels.Count == 0 ? ["No label"] : i.Labels.Select(l => l.Name).Distinct())
                .GroupBy(n => n).Select(g => (g.Key, g.Count()))
                .OrderByDescending(g => g.Item2).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase),
            _ => list.GroupBy(i => (i.Status, Name: Display.StatusName(i), Order: i.WorkflowStatus?.Order ?? 0))
                .OrderBy(g => g.Key.Status).ThenBy(g => g.Key.Order).ThenBy(g => g.Key.Name)
                .Select(g => (g.Key.Name, g.Count())),
        };
        var ordered = groups.ToList();
        var result = ordered.Take(MaxSlices).Select((g, k) => new ChartSlice(g.Label, g.Count, k + 1)).ToList();
        if (ordered.Count > MaxSlices) result.Add(new ChartSlice("Other", ordered.Skip(MaxSlices).Sum(g => g.Count), 0));
        return result;
    }

    /// <summary>
    /// Created and resolved counts for each of the last <paramref name="days"/> days ending on <paramref name="today"/>
    /// (inclusive), with running totals. Rows outside the window are ignored; a reopened issue counts by its current
    /// <c>ClosedAt</c> only.
    /// </summary>
    public static List<CreatedResolvedDay> CreatedVsResolved(IEnumerable<(DateTime Created, DateTime? Closed)> rows, DateTime today, int days)
    {
        days = Math.Max(1, days);
        var from = today.Date.AddDays(-(days - 1));
        var created = new int[days];
        var resolved = new int[days];
        foreach (var (c, r) in rows)
        {
            var ci = (int)(c.Date - from).TotalDays;
            if (ci >= 0 && ci < days) created[ci]++;
            if (r is { } rd)
            {
                var ri = (int)(rd.Date - from).TotalDays;
                if (ri >= 0 && ri < days) resolved[ri]++;
            }
        }
        var result = new List<CreatedResolvedDay>(days);
        int tc = 0, tr = 0;
        for (var k = 0; k < days; k++)
        {
            tc += created[k];
            tr += resolved[k];
            result.Add(new CreatedResolvedDay(from.AddDays(k), created[k], resolved[k], tc, tr));
        }
        return result;
    }
}
