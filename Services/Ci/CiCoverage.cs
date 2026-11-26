using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;

namespace ByteBench.Services.Ci;

/// <summary>
/// Test coverage of a job: a <c>coverage:</c> regex applied to the job's log, or a Cobertura / LCOV report from
/// <c>artifacts.reports</c>. Values are percentages from 0 to 100 with two decimals.
/// </summary>
public static class CiCoverage
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Checks a <c>coverage:</c> value (GitLab style <c>'/Total.*?(\d+\.?\d*)%/'</c>, or the bare regex). The regex is compiled
    /// with <see cref="RegexOptions.NonBacktracking"/>, so a hostile pattern can't make the server spin (no lookarounds or
    /// backreferences). Returns the pattern without the slashes, or an error.
    /// </summary>
    public static (string? Pattern, string? Error) NormalizePattern(string raw)
    {
        var p = raw.Trim();
        if (p.Length >= 2 && p[0] == '/' && p[^1] == '/') p = p[1..^1];
        if (p.Length == 0) return (null, "'coverage' needs a regular expression, e.g. '/Total.*?(\\d+\\.?\\d*)%/'.");
        if (p.Length > CiFile.MaxCoverageChars) return (null, $"'coverage' is longer than {CiFile.MaxCoverageChars} characters.");
        try { _ = Build(p); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return (null, $"'coverage' is not a usable regular expression ({ex.Message.Split('\n')[0]}); lookarounds and backreferences are not supported.");
        }
        return (p, null);
    }

    private static Regex Build(string pattern) =>
        new(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, MatchTimeout);

    /// <summary>The last match of <paramref name="pattern"/> in <paramref name="log"/>: its first capture group, else the first number of the match.</summary>
    public static double? FromLog(string pattern, string log)
    {
        if (string.IsNullOrEmpty(log)) return null;
        try
        {
            var rx = Build(pattern);
            double? found = null;
            foreach (Match m in rx.Matches(log))
            {
                var text = m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : Regex.Match(m.Value, @"\d+(\.\d+)?").Value;
                if (double.TryParse(text.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v is >= 0 and <= 100)
                    found = Math.Round(v, 2);
            }
            return found;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    /// <summary>Line coverage of a Cobertura XML report (<c>lines-covered</c> / <c>lines-valid</c>, else <c>line-rate</c>); null if unreadable.</summary>
    public static double? FromCobertura(string xml)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64L * 1024 * 1024 };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (!reader.Name.Equals("coverage", StringComparison.OrdinalIgnoreCase)) return null;
                var valid = reader.GetAttribute("lines-valid");
                var covered = reader.GetAttribute("lines-covered");
                if (double.TryParse(valid, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0
                    && double.TryParse(covered, NumberStyles.Float, CultureInfo.InvariantCulture, out var c) && c >= 0 && c <= v)
                    return Math.Round(c / v * 100, 2);
                if (double.TryParse(reader.GetAttribute("line-rate"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) && rate is >= 0 and <= 1)
                    return Math.Round(rate * 100, 2);
                return null;
            }
        }
        catch (XmlException) { }
        return null;
    }

    /// <summary>Line coverage of an LCOV <c>.info</c> file: the sum of <c>LH</c> over the sum of <c>LF</c> (else counted from <c>DA</c> lines); null if empty.</summary>
    public static double? FromLcov(string text)
    {
        long lf = 0, lh = 0, daTotal = 0, daHit = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("LF:", StringComparison.Ordinal) && long.TryParse(line.AsSpan(3), CultureInfo.InvariantCulture, out var f)) lf += f;
            else if (line.StartsWith("LH:", StringComparison.Ordinal) && long.TryParse(line.AsSpan(3), CultureInfo.InvariantCulture, out var h)) lh += h;
            else if (line.StartsWith("DA:", StringComparison.Ordinal))
            {
                var parts = line[3..].Split(',');
                if (parts.Length >= 2 && long.TryParse(parts[1], CultureInfo.InvariantCulture, out var hits)) { daTotal++; if (hits > 0) daHit++; }
            }
        }
        if (lf > 0) return Math.Round(Math.Min(lh, lf) * 100.0 / lf, 2);
        return daTotal > 0 ? Math.Round(daHit * 100.0 / daTotal, 2) : null;
    }

    /// <summary>The pipeline's coverage: the average of its jobs' (null when none reports one).</summary>
    public static double? Average(IEnumerable<double?> jobs)
    {
        var values = jobs.Where(v => v != null).Select(v => v!.Value).ToList();
        return values.Count == 0 ? null : Math.Round(values.Average(), 2);
    }

    /// <summary>
    /// Reads a job's coverage: its <c>coverage:</c> regex over the log first, else a Cobertura or LCOV report it published.
    /// <paramref name="notes"/> receives lines for the job log.
    /// </summary>
    public static double? ForJob(string? pattern, string log, IReadOnlyDictionary<string, string>? reports, string workspace, List<string> notes)
    {
        if (pattern != null)
        {
            if (FromLog(pattern, log) is { } fromLog) { notes.Add($"[bytebench] Coverage: {fromLog:0.##}% (from the log)."); return fromLog; }
            notes.Add("[bytebench] Coverage: the 'coverage' pattern matched nothing in the log.");
        }
        foreach (var (type, file) in reports ?? new Dictionary<string, string>())
        {
            if (type is not ("cobertura" or "lcov")) continue;
            var text = Security.SecurityIngestor.ReadReport(workspace, file, out var problem);
            if (text == null) { notes.Add($"[bytebench] Coverage report '{file}' ({type}): {problem}"); continue; }
            var value = type == "cobertura" ? FromCobertura(text) : FromLcov(text);
            if (value is { } v) { notes.Add($"[bytebench] Coverage: {v:0.##}% (from {file}, {type})."); return v; }
            notes.Add($"[bytebench] Coverage report '{file}' ({type}) could not be read.");
        }
        return null;
    }
}
