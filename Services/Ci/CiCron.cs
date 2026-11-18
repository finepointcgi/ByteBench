namespace GitBench.Services.Ci;

/// <summary>
/// A 5-field cron expression (minute hour day-of-month month day-of-week), evaluated in UTC. Fields take <c>*</c>,
/// numbers, ranges <c>1-5</c>, steps <c>*/15</c> or <c>0-30/10</c>, and lists <c>1,15</c>. Months and days also take
/// names (<c>jan</c>, <c>mon-fri</c>); day-of-week 0 and 7 are Sunday. When both day-of-month and day-of-week are
/// restricted, either one matching is enough (as in classic cron). Shorthands: <c>@hourly</c>, <c>@daily</c>
/// (<c>@midnight</c>), <c>@weekly</c>, <c>@monthly</c>, <c>@yearly</c> (<c>@annually</c>).
/// </summary>
public sealed class CiCron
{
    private static readonly string[] MonthNames = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
    private static readonly string[] DayNames = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    private readonly bool[] minutes = new bool[60], hours = new bool[24], days = new bool[32], months = new bool[13], weekdays = new bool[7];
    private bool dayRestricted, weekdayRestricted;

    public string Expression { get; }

    private CiCron(string expression) => Expression = expression;

    /// <summary>Parses <paramref name="expression"/>; <paramref name="error"/> says what is wrong when it returns null.</summary>
    public static CiCron? TryParse(string? expression, out string? error)
    {
        error = null;
        var text = (expression ?? "").Trim().ToLowerInvariant();
        text = text switch
        {
            "@hourly" => "0 * * * *",
            "@daily" or "@midnight" => "0 0 * * *",
            "@weekly" => "0 0 * * 0",
            "@monthly" => "0 0 1 * *",
            "@yearly" or "@annually" => "0 0 1 1 *",
            _ => text,
        };
        var parts = text.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
        {
            error = "A cron expression has 5 fields: minute hour day-of-month month day-of-week (e.g. \"0 3 * * 1-5\").";
            return null;
        }
        var c = new CiCron((expression ?? "").Trim());
        if ((error = Field(parts[0], 0, 59, null, c.minutes, "minute")) != null) return null;
        if ((error = Field(parts[1], 0, 23, null, c.hours, "hour")) != null) return null;
        if ((error = Field(parts[2], 1, 31, null, c.days, "day of month")) != null) return null;
        if ((error = Field(parts[3], 1, 12, MonthNames, c.months, "month")) != null) return null;
        var dow = new bool[8];
        if ((error = Field(parts[4], 0, 7, DayNames, dow, "day of week")) != null) return null;
        for (var i = 0; i < 7; i++) c.weekdays[i] = dow[i];
        if (dow[7]) c.weekdays[0] = true;
        c.dayRestricted = parts[2] != "*";
        c.weekdayRestricted = parts[4] != "*";
        if (c.Next(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)) == null)
        {
            error = "This expression never matches a real date.";
            return null;
        }
        return c;
    }

    public static CiCron Parse(string expression) =>
        TryParse(expression, out var error) ?? throw new FormatException(error);

    /// <summary>The first matching minute strictly after <paramref name="afterUtc"/>, or null if none within 5 years.</summary>
    public DateTime? Next(DateTime afterUtc)
    {
        var t = new DateTime(afterUtc.Year, afterUtc.Month, afterUtc.Day, afterUtc.Hour, afterUtc.Minute, 0, DateTimeKind.Utc).AddMinutes(1);
        var limit = t.AddYears(5);
        while (t < limit)
        {
            if (!months[t.Month]) { t = new DateTime(t.Year, t.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1); continue; }
            if (!DayMatches(t)) { t = t.Date.AddDays(1); continue; }
            if (!hours[t.Hour]) { t = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc).AddHours(1); continue; }
            if (!minutes[t.Minute]) { t = t.AddMinutes(1); continue; }
            return t;
        }
        return null;
    }

    private bool DayMatches(DateTime t)
    {
        var dom = days[t.Day];
        var dow = weekdays[(int)t.DayOfWeek];
        if (dayRestricted && weekdayRestricted) return dom || dow;
        return dom && dow;
    }

    private static string? Field(string field, int min, int max, string[]? names, bool[] target, string label)
    {
        foreach (var item in field.Split(','))
        {
            if (item.Length == 0) return $"The {label} field has an empty list item.";
            var step = 1;
            var range = item;
            var slash = item.IndexOf('/');
            if (slash >= 0)
            {
                if (!int.TryParse(item[(slash + 1)..], out step) || step <= 0) return $"'{item}' has an invalid step in the {label} field.";
                range = item[..slash];
            }
            int lo, hi;
            if (range == "*") { lo = min; hi = max; }
            else
            {
                var dash = range.IndexOf('-');
                if (dash > 0)
                {
                    if (Value(range[..dash], names, min) is not { } a || Value(range[(dash + 1)..], names, min) is not { } b)
                        return $"'{item}' is not a valid {label}.";
                    lo = a; hi = b;
                }
                else
                {
                    if (Value(range, names, min) is not { } v) return $"'{item}' is not a valid {label}.";
                    lo = v;
                    hi = slash >= 0 ? max : v; // "5/15" means 5, 20, 35, 50
                }
            }
            if (lo < min || hi > max || lo > hi) return $"'{item}' is out of range for the {label} ({min}-{max}).";
            for (var i = lo; i <= hi; i += step) target[i] = true;
        }
        return null;
    }

    private static int? Value(string s, string[]? names, int min)
    {
        if (int.TryParse(s, out var n)) return n;
        if (names == null) return null;
        var i = Array.IndexOf(names, s);
        return i < 0 ? null : i + (min == 1 ? 1 : 0);
    }

    public override string ToString() => Expression;
}
