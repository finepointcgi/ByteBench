using GitBench.Data;

namespace GitBench.Services.Reports;

/// <summary>
/// What the delivery reports cover. Dates are UTC calendar days, both inclusive; the default range is the last 90 days.
/// Type, label, component and assignee narrow the issues (AssigneeId 0 = unassigned); pull-request and DORA reports ignore them.
/// </summary>
public sealed record ReportFilter
{
    public IssueType? Type { get; init; }
    public int? LabelId { get; init; }
    public int? ComponentId { get; init; }
    /// <summary>A user id, or 0 for unassigned issues.</summary>
    public int? AssigneeId { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    /// <summary>Trend and throughput buckets: calendar months instead of weeks.</summary>
    public bool Monthly { get; init; }
    /// <summary>The current moment; null means now. Fixed by tests.</summary>
    public DateTime? Now { get; init; }

    public const int MaxDays = 1100;

    /// <summary>First day, last day (inclusive) and the exclusive end instant.</summary>
    public (DateTime From, DateTime To, DateTime ToExclusive, DateTime Now) Resolve()
    {
        var now = Now ?? DateTime.UtcNow;
        var to = (To ?? now).Date;
        var from = (From ?? to.AddDays(-89)).Date;
        if (from > to) (from, to) = (to, from);
        if ((to - from).TotalDays > MaxDays) from = to.AddDays(-MaxDays);
        return (from, to, to.AddDays(1), now);
    }
}
