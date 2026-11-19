using System.Globalization;
using System.Text.RegularExpressions;
using GitBench.Data;

namespace GitBench.Services.Reports;

/// <summary>One stretch of an issue's life in one status. <see cref="End"/> is null while it is still there.</summary>
public sealed record StatusSegment(string Status, IssueStatus Category, DateTime Start, DateTime? End);

/// <summary>An issue's rebuilt status history.</summary>
public sealed record IssueTimeline(IReadOnlyList<StatusSegment> Segments)
{
    /// <summary>First time the issue entered an In Progress or In Review status, or null if it never did.</summary>
    public DateTime? StartedAt => Segments.FirstOrDefault(s => s.Category is IssueStatus.InProgress or IssueStatus.InReview)?.Start;
    /// <summary>When the issue last entered a Done status (its current one if it is done now).</summary>
    public DateTime? DoneAt => Segments.LastOrDefault(s => s.Category == IssueStatus.Done)?.Start;
}

public sealed record Percentiles(int Count, double Average, double P50, double P85, double P95, double Max);

public sealed record DoraBand(string Name, string Rule);

/// <summary>Pure calculations behind the delivery reports (no database, no permissions).</summary>
public static partial class ReportMath
{
    [GeneratedRegex(@"^changed status from \*\*(.+?)\*\* to \*\*(.+?)\*\*")] private static partial Regex StatusRx();

    /// <summary>Parses "changed status from **A** to **B**" (the activity IssueService and WorkflowService write).</summary>
    public static bool TryParseStatusChange(string text, out string from, out string to)
    {
        var m = StatusRx().Match(text);
        (from, to) = m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : ("", "");
        return m.Success;
    }

    /// <summary>
    /// Rebuilds an issue's status timeline from its creation, its current status and its status-change activities
    /// (any order). The status before the first change is that change's "from"; with no changes the issue was always in
    /// its current status.
    /// </summary>
    public static IssueTimeline BuildTimeline(DateTime createdAt, string currentStatus, IEnumerable<(DateTime At, string From, string To)> changes,
        Func<string, IssueStatus> categoryOf)
    {
        var list = changes.OrderBy(c => c.At).ToList();
        var segments = new List<StatusSegment>();
        var status = list.Count > 0 ? list[0].From : currentStatus;
        var start = createdAt;
        foreach (var c in list)
        {
            var at = c.At < start ? start : c.At;
            segments.Add(new StatusSegment(status, categoryOf(status), start, at));
            (status, start) = (c.To, at);
        }
        segments.Add(new StatusSegment(status, categoryOf(status), start, null));
        return new IssueTimeline(segments);
    }

    /// <summary>The p-th percentile (0–100) by linear interpolation between closest ranks (the Excel PERCENTILE.INC rule). Values need not be sorted.</summary>
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        var rank = Math.Clamp(p, 0, 100) / 100 * (sorted.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    public static Percentiles? Summarize(IReadOnlyList<double> values) => values.Count == 0 ? null
        : new Percentiles(values.Count, Math.Round(values.Average(), 2), Math.Round(Percentile(values, 50), 2), Math.Round(Percentile(values, 85), 2),
            Math.Round(Percentile(values, 95), 2), Math.Round(values.Max(), 2));

    /// <summary>Upper bounds (days) of the histogram buckets; the last bucket is open-ended.</summary>
    public static readonly double[] BucketBounds = [1, 2, 3, 5, 8, 13, 21, 34, 55];

    public static string BucketLabel(int index) => index == 0 ? "< 1d" : index >= BucketBounds.Length ? $"{BucketBounds[^1]:0}d+" : $"{BucketBounds[index - 1]:0}–{BucketBounds[index]:0}d";

    /// <summary>Index of the bucket a duration in days falls in.</summary>
    public static int BucketOf(double days)
    {
        for (var i = 0; i < BucketBounds.Length; i++) if (days < BucketBounds[i]) return i;
        return BucketBounds.Length;
    }

    /// <summary>The Monday on or before <paramref name="d"/>, or the first of its month.</summary>
    public static DateTime PeriodStart(DateTime d, bool monthly) =>
        monthly ? new DateTime(d.Year, d.Month, 1) : d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    public static DateTime NextPeriod(DateTime start, bool monthly) => monthly ? start.AddMonths(1) : start.AddDays(7);

    /// <summary>Every period start from the one containing <paramref name="from"/> through the one containing <paramref name="to"/>.</summary>
    public static List<DateTime> Periods(DateTime from, DateTime to, bool monthly)
    {
        var result = new List<DateTime>();
        for (var p = PeriodStart(from, monthly); p <= to && result.Count < 400; p = NextPeriod(p, monthly)) result.Add(p);
        return result;
    }

    public static double Days(TimeSpan t) => Math.Max(0, t.TotalDays);
    public static double Hours(TimeSpan t) => Math.Max(0, t.TotalHours);

    // ----- DORA bands (the commonly used DORA / State of DevOps ranges) -----

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<DoraBand>> Bands = new Dictionary<string, IReadOnlyList<DoraBand>>
    {
        ["frequency"] = [new("Elite", "On demand: at least one deployment a day on average"), new("High", "Between once a day and once a week"),
            new("Medium", "Between once a week and once a month"), new("Low", "Less than once a month")],
        ["leadTime"] = [new("Elite", "Less than one day"), new("High", "One day to one week"), new("Medium", "One week to one month"), new("Low", "More than one month")],
        ["failureRate"] = [new("Elite", "0–5%"), new("High", "5–10%"), new("Medium", "10–15%"), new("Low", "More than 15%")],
        ["restoreTime"] = [new("Elite", "Less than one hour"), new("High", "Less than one day"), new("Medium", "Less than one week"), new("Low", "One week or more")],
    };

    /// <summary>Band for deployments per day.</summary>
    public static string FrequencyBand(double perDay) => perDay >= 1 ? "Elite" : perDay >= 1.0 / 7 ? "High" : perDay >= 1.0 / 30 ? "Medium" : "Low";

    public static string LeadTimeBand(double hours) => hours < 24 ? "Elite" : hours < 24 * 7 ? "High" : hours < 24 * 30 ? "Medium" : "Low";

    /// <summary>Band for a failure rate given as a fraction (0.04 = 4%).</summary>
    public static string FailureRateBand(double rate) => rate <= 0.05 ? "Elite" : rate <= 0.10 ? "High" : rate <= 0.15 ? "Medium" : "Low";

    public static string RestoreBand(double hours) => hours < 1 ? "Elite" : hours < 24 ? "High" : hours < 24 * 7 ? "Medium" : "Low";

    public static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
