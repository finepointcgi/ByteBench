using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ByteBench.Services.Fields;

/// <summary>
/// Jira-style durations for estimates: <c>2h 30m</c>, <c>1d</c>, <c>1.5h</c>, <c>1w 2d</c>. A day is the project's
/// hours per day (default 8) and a week is 5 days. A bare number means hours. Stored as whole minutes.
/// </summary>
public static partial class Duration
{
    public const int DefaultHoursPerDay = 8;
    public const int DaysPerWeek = 5;
    /// <summary>Upper bound for one value (about 10 years of full-time work), to keep typos out.</summary>
    public const int MaxMinutes = 10_000 * 60;

    [GeneratedRegex(@"^(?:(\d+(?:[.,]\d+)?)\s*(w|d|h|m)\s*)+$", RegexOptions.IgnoreCase)]
    private static partial Regex Whole();

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*(w|d|h|m)", RegexOptions.IgnoreCase)]
    private static partial Regex Part();

    /// <summary>Parses <paramref name="text"/> into minutes; null for blank input. Throws <see cref="FormatException"/> for anything else invalid.</summary>
    public static int? Parse(string? text, int hoursPerDay = DefaultHoursPerDay)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!TryParse(text, hoursPerDay, out var minutes))
            throw new FormatException($"'{text.Trim()}' isn't a duration. Use something like 2h 30m, 1d or 45m.");
        return minutes;
    }

    public static bool TryParse(string? text, int hoursPerDay, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (hoursPerDay is < 1 or > 24) hoursPerDay = DefaultHoursPerDay;
        double total;
        if (TryNumber(s, out var hours)) total = hours * 60; // a bare number is hours
        else
        {
            if (!Whole().IsMatch(s)) return false;
            total = 0;
            foreach (Match m in Part().Matches(s))
            {
                if (!TryNumber(m.Groups[1].Value, out var n)) return false;
                total += char.ToLowerInvariant(m.Groups[2].Value[0]) switch
                {
                    'w' => n * DaysPerWeek * hoursPerDay * 60,
                    'd' => n * hoursPerDay * 60,
                    'h' => n * 60,
                    _ => n,
                };
            }
        }
        if (double.IsNaN(total) || total < 0 || total > MaxMinutes) return false;
        minutes = (int)Math.Round(total);
        return true;
    }

    private static bool TryNumber(string s, out double n) =>
        double.TryParse(s.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out n);

    /// <summary>Minutes as <c>1d 2h 30m</c> (no weeks, so it reads the same as Jira's default); <c>0m</c> for zero.</summary>
    public static string Format(int minutes, int hoursPerDay = DefaultHoursPerDay)
    {
        if (minutes <= 0) return "0m";
        if (hoursPerDay is < 1 or > 24) hoursPerDay = DefaultHoursPerDay;
        var perDay = hoursPerDay * 60;
        var (d, rest) = (minutes / perDay, minutes % perDay);
        var (h, m) = (rest / 60, rest % 60);
        var sb = new StringBuilder();
        if (d > 0) sb.Append(d).Append("d ");
        if (h > 0) sb.Append(h).Append("h ");
        if (m > 0) sb.Append(m).Append("m ");
        return sb.ToString().TrimEnd();
    }

    public static string? FormatOrNull(int? minutes, int hoursPerDay = DefaultHoursPerDay) => minutes is { } m ? Format(m, hoursPerDay) : null;

    /// <summary>Hours with up to two decimals, for reports (e.g. <c>7.5</c>).</summary>
    public static string Hours(int minutes) => (minutes / 60.0).ToString("0.##", CultureInfo.InvariantCulture);
}
