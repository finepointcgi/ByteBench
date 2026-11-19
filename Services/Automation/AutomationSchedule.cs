using System.Globalization;

namespace ByteBench.Services.Automation;

/// <summary>Pure schedule arithmetic for the "scheduled" trigger (all times UTC).</summary>
public static class AutomationSchedule
{
    public static bool TryParseTime(string? text, out TimeSpan time) =>
        TimeSpan.TryParseExact((text ?? "").Trim(), [@"hh\:mm", @"h\:mm"], CultureInfo.InvariantCulture, out time)
        && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);

    /// <summary>Null if the schedule is usable, else why not.</summary>
    public static string? Validate(TriggerOptions t) => t.Schedule switch
    {
        ScheduleKind.EveryHours when t.Hours is < 1 or > 168 => "Run every 1 to 168 hours.",
        ScheduleKind.Daily or ScheduleKind.Weekly when !TryParseTime(t.TimeOfDay, out _) => "Enter the time as HH:mm (UTC), e.g. 09:00.",
        _ => null,
    };

    /// <summary>The latest scheduled moment at or before <paramref name="now"/> (daily/weekly), or null for interval schedules.</summary>
    public static DateTime? PreviousOccurrence(TriggerOptions t, DateTime now)
    {
        if (!TryParseTime(t.TimeOfDay, out var time)) return null;
        switch (t.Schedule)
        {
            case ScheduleKind.Daily:
                var today = now.Date + time;
                return today <= now ? today : today.AddDays(-1);
            case ScheduleKind.Weekly:
                var back = ((int)now.DayOfWeek - (int)t.DayOfWeek + 7) % 7;
                var candidate = now.Date.AddDays(-back) + time;
                return candidate <= now ? candidate : candidate.AddDays(-7);
            default:
                return null;
        }
    }

    /// <summary>
    /// True if the rule should run at <paramref name="now"/>: a scheduled moment passed since it last ran (or since it was
    /// created/changed, for a rule that never ran). Missed runs while the server was down collapse into one.
    /// </summary>
    public static bool IsDue(TriggerOptions t, DateTime? lastRun, DateTime since, DateTime now)
    {
        var after = lastRun ?? since;
        if (t.Schedule == ScheduleKind.EveryHours)
            return t.Hours >= 1 && now - after >= TimeSpan.FromHours(t.Hours);
        return PreviousOccurrence(t, now) is { } prev && prev > after;
    }

    public static string Describe(TriggerOptions t) => t.Schedule switch
    {
        ScheduleKind.Daily => $"daily at {t.TimeOfDay} UTC",
        ScheduleKind.Weekly => $"every {t.DayOfWeek} at {t.TimeOfDay} UTC",
        _ => t.Hours == 1 ? "every hour" : $"every {t.Hours} hours",
    };
}
