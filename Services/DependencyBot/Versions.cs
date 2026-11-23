using System.Globalization;
using System.Text.RegularExpressions;
using ByteBench.Data;

namespace ByteBench.Services.DependencyBot;

/// <summary>A tolerant version: up to four numeric parts plus an optional pre-release (semver <c>-rc.1</c>, PEP 440 <c>1.0rc1</c>, NuGet <c>-beta</c>).</summary>
public sealed partial class DepVersion : IComparable<DepVersion>
{
    public int[] Parts { get; }
    public string Pre { get; }
    public string Text { get; }
    public bool IsPrerelease => Pre.Length > 0;
    public int Major => Parts[0];
    public int Minor => Parts[1];
    public int Patch => Parts[2];

    private DepVersion(int[] parts, string pre, string text) { Parts = parts; Pre = pre; Text = text; }

    [GeneratedRegex(@"^v?(\d+(?:\.\d+){0,3})(.*)$")]
    private static partial Regex Pattern();

    /// <summary>Null if <paramref name="text"/> isn't a version.</summary>
    public static DepVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Pattern().Match(text.Trim());
        if (!m.Success) return null;
        var nums = m.Groups[1].Value.Split('.');
        var parts = new int[4];
        for (var i = 0; i < nums.Length; i++)
            if (!int.TryParse(nums[i], NumberStyles.None, CultureInfo.InvariantCulture, out parts[i])) return null;
        var rest = m.Groups[2].Value;
        var plus = rest.IndexOf('+');
        if (plus >= 0) rest = rest[..plus]; // build metadata
        // Go's "+incompatible" is build metadata too, handled above. ".post1" (PEP 440) is a final release; a, b, rc, dev and dashes are not.
        var pre = rest.Length == 0 || PostRelease().IsMatch(rest) ? "" : rest.TrimStart('-', '.', '_');
        if (rest.Length > 0 && pre.Length == 0 && !PostRelease().IsMatch(rest)) return null;
        return new DepVersion(parts, pre, text.Trim());
    }

    [GeneratedRegex(@"^[.\-_]?(post|rev|r)\d*$", RegexOptions.IgnoreCase)]
    private static partial Regex PostRelease();

    public int CompareTo(DepVersion? other)
    {
        if (other == null) return 1;
        for (var i = 0; i < 4; i++)
            if (Parts[i] != other.Parts[i]) return Parts[i].CompareTo(other.Parts[i]);
        if (Pre.Length == 0 && other.Pre.Length == 0) return 0;
        if (Pre.Length == 0) return 1;
        if (other.Pre.Length == 0) return -1;
        return ComparePre(Pre, other.Pre);
    }

    private static int ComparePre(string a, string b)
    {
        var x = Regex.Split(a, @"[.\-_]|(?<=\d)(?=\D)|(?<=\D)(?=\d)");
        var y = Regex.Split(b, @"[.\-_]|(?<=\d)(?=\D)|(?<=\D)(?=\d)");
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            var xn = int.TryParse(x[i], out var xi); var yn = int.TryParse(y[i], out var yi);
            var c = xn && yn ? xi.CompareTo(yi) : xn ? -1 : yn ? 1 : string.Compare(x[i], y[i], StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
        }
        return x.Length.CompareTo(y.Length);
    }

    public static bool operator <(DepVersion a, DepVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(DepVersion a, DepVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(DepVersion a, DepVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(DepVersion a, DepVersion b) => a.CompareTo(b) >= 0;
    public override string ToString() => Text;

    /// <summary>Which part differs: "major", "minor" or "patch" (from <paramref name="from"/> to this).</summary>
    public string BumpFrom(DepVersion from) => Parts[0] != from.Parts[0] ? "major" : Parts[1] != from.Parts[1] ? "minor" : "patch";
}

/// <summary>
/// A version range for the ignore list: comparators separated by spaces or commas, all of which must hold.
/// Forms: <c>&gt;=1.2</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>&lt;</c>, <c>=1.2.3</c> or just <c>1.2.3</c>, <c>^1.2.3</c>, <c>~1.2.3</c>, <c>1.x</c>, <c>1.2.*</c>, <c>*</c>.
/// </summary>
public sealed class VersionRange
{
    private readonly List<Func<DepVersion, bool>> checks = [];
    public bool IsValid { get; private set; } = true;

    public static VersionRange Parse(string? text)
    {
        var range = new VersionRange();
        var tokens = (text ?? "").Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) { range.checks.Add(_ => true); return range; }
        for (var i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            // "> 1.2" written with a space.
            if (t is ">=" or "<=" or ">" or "<" or "=" or "^" or "~" && i + 1 < tokens.Length) t += tokens[++i];
            if (!range.Add(t)) range.IsValid = false;
        }
        return range;
    }

    private bool Add(string token)
    {
        if (token == "*") { checks.Add(_ => true); return true; }
        string op = "=";
        foreach (var candidate in new[] { ">=", "<=", ">", "<", "=", "^", "~" })
            if (token.StartsWith(candidate, StringComparison.Ordinal)) { op = candidate; token = token[candidate.Length..]; break; }
        var wild = Regex.Match(token, @"^v?(\d+)(?:\.(\d+|[xX*]))?(?:\.([xX*]))?$");
        if (op == "=" && wild.Success && (token.Contains('x') || token.Contains('X') || token.Contains('*')))
        {
            var major = int.Parse(wild.Groups[1].Value, CultureInfo.InvariantCulture);
            var minorText = wild.Groups[2].Value;
            if (minorText.Length == 0 || minorText is "x" or "X" or "*") checks.Add(v => v.Major == major);
            else { var minor = int.Parse(minorText, CultureInfo.InvariantCulture); checks.Add(v => v.Major == major && v.Minor == minor); }
            return true;
        }
        if (DepVersion.Parse(token) is not { } ver) return false;
        switch (op)
        {
            case ">=": checks.Add(v => v >= ver); break;
            case "<=": checks.Add(v => v <= ver); break;
            case ">": checks.Add(v => v > ver); break;
            case "<": checks.Add(v => v < ver); break;
            case "^":
                checks.Add(v => v >= ver && (ver.Major > 0 ? v.Major == ver.Major : ver.Minor > 0 ? v.Major == 0 && v.Minor == ver.Minor : v.Major == 0 && v.Minor == 0 && v.Patch == ver.Patch));
                break;
            case "~": checks.Add(v => v >= ver && v.Major == ver.Major && v.Minor == ver.Minor); break;
            default: checks.Add(v => v.CompareTo(ver) == 0); break;
        }
        return true;
    }

    public bool Matches(DepVersion version) => IsValid && checks.All(c => c(version));
}

/// <summary>One ignore-list line: a package name (trailing <c>*</c>: prefix match) with an optional version range.</summary>
public sealed record IgnoreRule(string Name, VersionRange? Range)
{
    public static List<IgnoreRule> ParseAll(string? text)
    {
        var rules = new List<IgnoreRule>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            var hash = line.IndexOf(" #", StringComparison.Ordinal);
            if (hash >= 0) line = line[..hash].Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            // The version separator is the last '@' that isn't the leading one of a scoped npm name.
            var at = line.LastIndexOf('@');
            if (at <= 0) rules.Add(new IgnoreRule(line, null));
            else rules.Add(new IgnoreRule(line[..at].Trim(), VersionRange.Parse(line[(at + 1)..])));
        }
        return rules;
    }

    public bool AppliesTo(string package) =>
        Name.EndsWith('*') ? package.StartsWith(Name[..^1], StringComparison.OrdinalIgnoreCase) : string.Equals(Name, package, StringComparison.OrdinalIgnoreCase);

    /// <summary>True if this rule forbids updating <paramref name="package"/> to <paramref name="version"/>.</summary>
    public bool Blocks(string package, DepVersion version) => AppliesTo(package) && (Range == null || Range.Matches(version));
}

public static class UpdateChooser
{
    /// <summary>
    /// The newest stable version above <paramref name="current"/> that the strategy allows and the ignore rules don't block, or null.
    /// Patch: same major and minor. Minor: same major. Major: any. Pre-releases are only proposed to a pre-release current version.
    /// </summary>
    public static DepVersion? Choose(DepVersion current, IEnumerable<DepVersion> available, DependencyStrategy strategy, string package, IReadOnlyList<IgnoreRule> ignore)
    {
        DepVersion? best = null;
        foreach (var v in available)
        {
            if (v <= current || (v.IsPrerelease && !current.IsPrerelease)) continue;
            if (strategy == DependencyStrategy.Patch && (v.Major != current.Major || v.Minor != current.Minor)) continue;
            if (strategy == DependencyStrategy.Minor && v.Major != current.Major) continue;
            if (ignore.Any(r => r.Blocks(package, v))) continue;
            if (best == null || v > best) best = v;
        }
        return best;
    }

    /// <summary>The newest stable version overall (what "Latest" shows), or null.</summary>
    public static DepVersion? Latest(IEnumerable<DepVersion> available, bool allowPrerelease) =>
        available.Where(v => allowPrerelease || !v.IsPrerelease).OrderBy(v => v).LastOrDefault();
}
