using ByteBench.Data;

namespace ByteBench.Services.Digests;

/// <summary>The <c>Digest</c> configuration section.</summary>
public sealed class DigestOptions
{
    /// <summary>Run <see cref="DigestWorker"/>. When false nothing is sent on schedule (previews still work).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>How often the worker looks for users whose digest is due.</summary>
    public int PollMinutes { get; set; } = 5;
    /// <summary>At most this many users per poll; the rest wait for the next one.</summary>
    public int MaxPerRun { get; set; } = 200;
    /// <summary>"Send me a preview now" presses per user per hour.</summary>
    public int PreviewsPerHour { get; set; } = 3;
}

/// <summary>
/// When a user's next digest is due: the next local <c>hour:00</c> (weekly: on the chosen weekday) in their IANA time zone
/// after a given instant, as UTC. Across DST: an hour that doesn't exist (spring forward) is moved to the first valid
/// minute after the gap; an hour that occurs twice (fall back) uses the first occurrence.
/// </summary>
public static class DigestSchedule
{
    public const string DefaultZone = "UTC";

    /// <summary>The zone for an IANA id; null when it isn't a known IANA zone (Windows ids like "Pacific Standard Time" are refused).</summary>
    public static TimeZoneInfo? FindZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (id.Length > 64 || id.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return null;
        // Windows ids resolve too, but without an IANA id: those are refused.
        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz) && tz.HasIanaId ? tz : null;
    }

    /// <summary>Common IANA zones for the picker (every zone the system knows that has an IANA id).</summary>
    public static IReadOnlyList<string> KnownZones { get; } = TimeZoneInfo.GetSystemTimeZones()
        .Select(z => z.HasIanaId ? z.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(z.Id, out var iana) ? iana : null)
        .Where(z => z != null && z.Contains('/') && !z.StartsWith("Etc/", StringComparison.Ordinal)).Select(z => z!)
        .Append(DefaultZone).Distinct().Order(StringComparer.Ordinal).ToList();

    public static DateTime? Next(UserNotificationSettings s, DateTime afterUtc) =>
        s.DigestFrequency == DigestFrequency.Off ? null
            : Next(s.DigestFrequency, s.DigestHour ?? UserNotificationSettings.DefaultDigestHour, s.DigestWeekday ?? DayOfWeek.Monday,
                FindZone(s.TimeZoneId) ?? TimeZoneInfo.Utc, afterUtc);

    /// <summary>The first scheduled instant strictly after <paramref name="afterUtc"/> (UTC).</summary>
    public static DateTime Next(DigestFrequency frequency, int hour, DayOfWeek weekday, TimeZoneInfo zone, DateTime afterUtc)
    {
        if (frequency == DigestFrequency.Off) throw new ArgumentException("Digests are off.", nameof(frequency));
        hour = Math.Clamp(hour, 0, 23);
        afterUtc = DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);
        var localDate = TimeZoneInfo.ConvertTimeFromUtc(afterUtc, zone).Date;
        for (var day = -1; day <= 8; day++)
        {
            var local = DateTime.SpecifyKind(localDate.AddDays(day).AddHours(hour), DateTimeKind.Unspecified);
            if (frequency == DigestFrequency.Weekly && local.DayOfWeek != weekday) continue;
            var utc = ToUtc(local, zone);
            if (utc > afterUtc) return utc;
        }
        throw new InvalidOperationException("No digest time found."); // unreachable: a week always has the weekday
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        // Spring forward: hour:00 doesn't exist; use the first valid minute after the gap.
        var guard = 0;
        while (zone.IsInvalidTime(local) && guard++ < 24 * 60) local = local.AddMinutes(1);
        if (zone.IsAmbiguousTime(local))
        {
            // Fall back: hour:00 happens twice; the first occurrence has the larger offset.
            var offset = zone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
