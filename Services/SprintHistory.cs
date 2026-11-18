using System.Text.RegularExpressions;
using GitBench.Data;

namespace GitBench.Services;

/// <summary>One day of a sprint burndown. Remaining values are null for days that haven't happened yet.</summary>
public record BurndownDay(DateTime Date, int? RemainingPoints, int? RemainingCount, double IdealPoints, double IdealCount,
    int? ScopePoints, int? ScopeCount);

/// <summary>A sprint's burndown: one entry per calendar day from its start date to its end date.</summary>
public record Burndown(Sprint Sprint, IReadOnlyList<BurndownDay> Days, int StartPoints, int StartCount);

/// <summary>Committed vs completed for one finished sprint.</summary>
public record VelocityEntry(Sprint Sprint, int CommittedPoints, int CompletedPoints, int CommittedCount, int CompletedCount);

/// <summary>
/// The activity texts sprint-related changes write, and the replay that rebuilds an issue's past state from them.
/// <para>
/// Issue history lives in <see cref="Activity"/> rows (Markdown text). The burndown starts from each issue's current
/// state and walks its activities from newest to oldest, undoing the ones this class understands:
/// <list type="bullet">
/// <item>status changes, <c>changed status from **A** to **B**</c> (only "is it Done?" matters);</item>
/// <item>estimates, written by <see cref="EstimateText"/>: <c>changed the estimate from **3** to **5** story points</c>;</item>
/// <item>sprint membership, written by <see cref="SprintText"/>; each sprint is a link ending <c>#sprint-{id}</c>, so
/// renaming a sprint doesn't break the history.</item>
/// </list>
/// Older "estimated **5** story points" entries are read as "was unestimated" and a bare "removed the estimate" as "unknown, unchanged".
/// </para>
/// </summary>
public static partial class SprintHistory
{
    // ----- Writing -----

    public static string SprintLink(string projectKey, Sprint s) =>
        $"[{s.Name.Replace('[', '(').Replace(']', ')')}](/p/{projectKey}/backlog#sprint-{s.Id})";

    /// <summary>Activity text for an issue moving from one sprint (null = backlog) to another; null if nothing changed.</summary>
    public static string? SprintText(string projectKey, Sprint? from, Sprint? to) => (from, to) switch
    {
        (null, null) => null,
        (null, { } t) => $"added this issue to sprint {SprintLink(projectKey, t)}",
        ({ } f, null) => $"removed this issue from sprint {SprintLink(projectKey, f)}",
        ({ } f, { } t) when f.Id == t.Id => null,
        ({ } f, { } t) => $"moved this issue from sprint {SprintLink(projectKey, f)} to sprint {SprintLink(projectKey, t)}",
    };

    /// <summary>Activity text for a story point change; records the old value so history can be replayed.</summary>
    public static string EstimateText(int? from, int? to) => (from, to) switch
    {
        (null, { } t) => $"estimated **{t}** story points",
        ({ } f, null) => $"removed the estimate of **{f}** story points",
        ({ } f, { } t) => $"changed the estimate from **{f}** to **{t}** story points",
        _ => "removed the estimate",
    };

    /// <summary>The marker every activity about <paramref name="sprintId"/> contains (for finding an issue set by SQL LIKE).</summary>
    public static string Marker(int sprintId) => $"#sprint-{sprintId})";

    // ----- Reading -----

    [GeneratedRegex(@"changed status from \*\*(.+?)\*\* to \*\*(.+?)\*\*")] private static partial Regex StatusRx();
    [GeneratedRegex(@"changed the estimate from \*\*(\d+)\*\* to \*\*(\d+)\*\*")] private static partial Regex EstimateChangedRx();
    [GeneratedRegex(@"removed the estimate of \*\*(\d+)\*\*")] private static partial Regex EstimateRemovedRx();
    [GeneratedRegex(@"(?<!changed the )estimated \*\*(\d+)\*\* story points")] private static partial Regex EstimatedRx();
    [GeneratedRegex(@"(added this issue to|removed this issue from|moved this issue from) sprint \[[^\]]*\]\([^)]*#sprint-(\d+)\)")] private static partial Regex SprintRx();

    private static readonly IReadOnlySet<string> DefaultDoneNames = new HashSet<string>([IssueStatus.Done.Name()], StringComparer.OrdinalIgnoreCase);

    /// <summary>An issue's state at some moment.</summary>
    public record struct State(bool Exists, int? SprintId, bool Done, int Points);

    /// <summary>Undoes one activity: given the state just after it, the state just before it.</summary>
    public static State Undo(State after, string text, IReadOnlySet<string>? doneNames = null)
    {
        var s = after;
        if (StatusRx().Match(text) is { Success: true } st)
            s.Done = (doneNames ?? DefaultDoneNames).Contains(st.Groups[1].Value);

        if (EstimateChangedRx().Match(text) is { Success: true } ec) s.Points = int.Parse(ec.Groups[1].Value);
        else if (EstimateRemovedRx().Match(text) is { Success: true } er) s.Points = int.Parse(er.Groups[1].Value);
        else if (EstimatedRx().IsMatch(text)) s.Points = 0;

        if (SprintRx().Match(text) is { Success: true } sp)
            s.SprintId = sp.Groups[1].Value == "added this issue to" ? null : int.Parse(sp.Groups[2].Value);
        return s;
    }

    /// <summary>
    /// The state of <paramref name="issue"/> at <paramref name="at"/>, from its current state and its activities
    /// (any order; only those later than <paramref name="at"/> are undone).
    /// </summary>
    public static State StateAt(Issue issue, IEnumerable<Activity> activities, DateTime at, IReadOnlySet<string>? doneNames = null)
    {
        var s = new State(issue.CreatedAt <= at, issue.SprintId, issue.Status == IssueStatus.Done, issue.StoryPoints ?? 0);
        foreach (var a in activities.Where(a => a.CreatedAt > at).OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id))
            s = Undo(s, a.Text, doneNames);
        return s;
    }

    /// <summary>
    /// Builds the burndown of <paramref name="sprint"/> from <paramref name="issues"/> (every issue that is or ever was in
    /// it) and their activities. Days run from the start date to the end date; the first is measured when the sprint was
    /// started, the others at the end of the day (or now / when the sprint was completed, whichever is earlier).
    /// </summary>
    public static Burndown Build(Sprint sprint, IReadOnlyList<Issue> issues, ILookup<int, Activity> activities, DateTime now, IReadOnlySet<string>? doneNames = null)
    {
        var start = (sprint.StartDate ?? sprint.StartedAt ?? now).Date;
        var end = (sprint.EndDate ?? start.AddDays(13)).Date;
        if (end < start) end = start;
        var startedAt = sprint.StartedAt ?? start;
        // Just before completion, so open issues carried over at that moment still count as remaining.
        var cutoff = sprint.CompletedAt is { } c && c < now ? c.AddSeconds(-1) : now;

        (int remPts, int remCnt, int scopePts, int scopeCnt) Measure(DateTime at)
        {
            int rp = 0, rc = 0, sp = 0, sc = 0;
            foreach (var i in issues)
            {
                var s = StateAt(i, activities[i.Id], at, doneNames);
                if (!s.Exists || s.SprintId != sprint.Id) continue;
                sp += s.Points; sc++;
                if (!s.Done) { rp += s.Points; rc++; }
            }
            return (rp, rc, sp, sc);
        }

        var first = Measure(startedAt < cutoff ? startedAt : cutoff);
        var startPoints = sprint.CommittedPoints ?? first.scopePts;
        var startCount = sprint.CommittedCount ?? first.scopeCnt;
        var n = (end - start).Days;
        var days = new List<BurndownDay>();
        for (var k = 0; k <= n; k++)
        {
            var day = start.AddDays(k);
            double frac = n == 0 ? 1 : (double)k / n;
            var idealPts = Math.Round(startPoints * (1 - frac), 2);
            var idealCnt = Math.Round(startCount * (1 - frac), 2);
            if (sprint.State == SprintState.Planned || day > cutoff.Date || (k > 0 && day > cutoff))
            {
                days.Add(new BurndownDay(day, null, null, idealPts, idealCnt, null, null));
                continue;
            }
            var m = k == 0 ? first : Measure(day.AddDays(1) < cutoff ? day.AddDays(1) : cutoff);
            days.Add(new BurndownDay(day, m.remPts, m.remCnt, idealPts, idealCnt, m.scopePts, m.scopeCnt));
        }
        return new Burndown(sprint, days, startPoints, startCount);
    }
}
