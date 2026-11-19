using System.Globalization;
using System.Text;
using GitBench.Services.Query;

namespace GitBench.Services.Reports;

/// <summary>CSV for each report (formula-injection safe through <see cref="Csv.Cell"/>; download as UTF-8 with BOM through <see cref="Csv.Utf8WithBom"/>).</summary>
public static class ReportCsv
{
    private static string D(DateTime? d) => d?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? "";
    private static string N(double? v) => v?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
    private static string N(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string Build(IEnumerable<string> header, IEnumerable<IEnumerable<string?>> rows)
    {
        var sb = new StringBuilder();
        Csv.AppendRow(sb, header);
        foreach (var r in rows) Csv.AppendRow(sb, r);
        return sb.ToString();
    }

    public static string CycleTime(CycleTimeReport r) => Build(
        ["key", "title", "type", "assignee", "created", "started", "done", "lead_time_days", "cycle_time_days"],
        r.Issues.Select(i => new string?[] { i.Key, i.Title, i.Type.ToString(), i.Assignee, D(i.CreatedAt), D(i.StartedAt), D(i.DoneAt), N(i.LeadDays), N(i.CycleDays) }));

    public static string TimeInStatus(TimeInStatusReport r) => Build(
        ["status", "category", "issues", "average_days", "median_days", "total_days", "share_percent", "bottleneck"],
        r.Statuses.Select(s => new string?[] { s.Status, s.Category.ToString(), N(s.Issues), N(s.AvgDays), N(s.MedianDays), N(s.TotalDays), N(s.SharePercent), s.Bottleneck ? "yes" : "no" }));

    /// <summary>One row per issue; one column per status.</summary>
    public static string TimeInStatusIssues(TimeInStatusReport r)
    {
        var statuses = r.Statuses.Select(s => s.Status).ToList();
        return Build(new[] { "key", "title", "current_status", "total_days", "longest_status" }.Concat(statuses.Select(s => "days_in_" + s)),
            r.Issues.Select(i => new string?[] { i.Key, i.Title, i.CurrentStatus, N(i.TotalDays), i.LongestStatus }
                .Concat(statuses.Select(s => (string?)(i.Days.TryGetValue(s, out var d) ? N(d) : "0")))));
    }

    public static string CumulativeFlow(CumulativeFlowReport r) => Build(new[] { "date" }.Concat(r.Series.Select(s => s.Status)),
        r.Days.Select(d => new string?[] { d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }.Concat(d.Counts.Select(c => (string?)N(c)))));

    public static string Throughput(ThroughputReport r) => Build(["period_start", "created", "done", "story_points_done"],
        r.Periods.Select(p => new string?[] { p.Period.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), N(p.Created), N(p.Done), N(p.Points) }));

    public static string ThroughputSprints(ThroughputReport r) => Build(["sprint", "completed", "committed_issues", "completed_issues", "committed_points", "completed_points"],
        r.Sprints.Select(s => new string?[] { s.Name, D(s.CompletedAt), N(s.CommittedCount), N(s.CompletedCount), N(s.CommittedPoints), N(s.CompletedPoints) }));

    public static string Wip(WipReport r) => Build(["status", "category", "issues", "story_points"],
        r.Rows.Select(w => new string?[] { w.Status, w.Category.ToString(), N(w.Issues), N(w.Points) }));

    /// <summary>One row per successful production deployment in the range.</summary>
    public static string Dora(DoraReport r) => Build(
        ["deployment_id", "repository", "environment", "commit", "finished", "commits", "lead_time_hours", "failed", "failure_signal", "signal_at", "restore_hours"],
        r.Rows.Select(d => new string?[] { N(d.Id), d.Repository, d.Environment, d.CommitSha, D(d.FinishedAt), N(d.Commits), N(d.LeadTimeHours), d.Failed ? "yes" : "no",
            d.FailureSignal, D(d.SignalAt), N(d.RestoreHours) }));

    /// <summary>The four headline metrics.</summary>
    public static string DoraSummary(DoraReport r) => Build(["metric", "value", "unit", "band"],
    [
        new string?[] { "deployment_frequency", N(r.PerDay), "deployments_per_day", r.FrequencyBand },
        new string?[] { "lead_time_for_changes_p50", N(r.LeadTimeHours?.P50), "hours", r.LeadTimeBand },
        new string?[] { "change_failure_rate", N(r.FailureRate is { } f ? f * 100 : null), "percent", r.FailureBand },
        new string?[] { "time_to_restore_p50", N(r.RestoreMedianHours), "hours", r.RestoreBand },
    ]);

    public static string PullRequests(PullRequestReport r) => Build(
        ["repository", "number", "title", "author", "state", "draft", "created", "first_review", "hours_to_first_review", "merged", "hours_to_merge", "lines_changed", "files_changed", "size"],
        r.Rows.Select(p => new string?[] { p.Repository, N(p.Number), p.Title, p.Author, p.State.ToString(), p.IsDraft ? "yes" : "no", D(p.CreatedAt), D(p.FirstReviewAt),
            N(p.HoursToFirstReview), D(p.MergedAt), N(p.HoursToMerge), N(p.Size), N(p.Files), p.SizeClass }));
}
