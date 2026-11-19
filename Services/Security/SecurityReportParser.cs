using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GitBench.Data;

namespace GitBench.Services.Security;

/// <summary>One finding read from a report, before it is stored.</summary>
public sealed record ParsedFinding
{
    public required string Fingerprint { get; init; }
    public required SecurityCategory Category { get; init; }
    public required SecuritySeverity Severity { get; init; }
    public required string RuleId { get; init; }
    public required string Title { get; init; }
    public string? Message { get; init; }
    public string? PackageName { get; init; }
    public string? PackageVersion { get; init; }
    public string? Ecosystem { get; init; }
    public IReadOnlyList<string> FixedVersions { get; init; } = [];
    public string? FilePath { get; init; }
    public int? Line { get; init; }
    public IReadOnlyList<string> Engines { get; init; } = [];
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public string? HelpUrl { get; init; }
    public string? Context { get; init; }
    public SecurityTriageState ReportedTriage { get; init; }
}

/// <summary>A parsed report: which tool wrote it and its findings, deduplicated by fingerprint.</summary>
public sealed record ParsedReport(string Format, string? ToolName, string? ToolVersion, IReadOnlyList<ParsedFinding> Findings);

/// <summary>
/// Reads security reports into <see cref="ParsedFinding"/>s:
/// <list type="bullet">
/// <item><b>Night Owl JSON</b> (<c>nightowl scan --format json</c>, its <c>ScanReport</c>): camelCase, enums as names
/// (<c>"category": "Vulnerability"</c>, <c>"severity": "High"</c>). The finding <c>id</c> is Night Owl's fingerprint and is kept
/// as is, so triage keyed by it matches Night Owl's own <c>.nightowl/triage.json</c>.</item>
/// <item><b>SARIF 2.1.0</b> from any tool. Night Owl's SARIF carries the fingerprint in
/// <c>partialFingerprints["nightowl/v1"]</c> and the severity in <c>properties.severity</c>; for other tools the first
/// partial fingerprint is used, else one is derived like Night Owl's code fingerprint (rule, path, snippet or line), and
/// the severity comes from <c>security-severity</c> or the result level.</item>
/// </list>
/// Findings with the same fingerprint are merged (highest severity, all engines), as Night Owl does across engines.
/// </summary>
public static partial class SecurityReportParser
{
    public const string NightOwlFormat = "nightowl";
    public const string SarifFormat = "sarif";
    public const int MaxFindings = 20_000;

    /// <summary>Parses <paramref name="json"/> as <paramref name="format"/> ("nightowl" or "sarif"; null detects it).</summary>
    /// <exception cref="InvalidDataException">Not valid JSON, or neither format.</exception>
    public static ParsedReport Parse(string json, string? format = null)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 128 }); }
        catch (JsonException ex) { throw new InvalidDataException($"The report is not valid JSON: {ex.Message}", ex); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The report must be a JSON object.");
            var isSarif = root.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array;
            var isNightOwl = root.TryGetProperty("findings", out var findings) && findings.ValueKind == JsonValueKind.Array;
            format = format?.Trim().ToLowerInvariant();
            if (format == SarifFormat || (format == null && isSarif))
            {
                if (!isSarif) throw new InvalidDataException("Not a SARIF report: no 'runs' array.");
                return ParseSarif(root);
            }
            if (format is NightOwlFormat or null && isNightOwl) return ParseNightOwl(root);
            throw new InvalidDataException(format == NightOwlFormat
                ? "Not a Night Owl report: no 'findings' array (use `nightowl scan --format json`)."
                : "Unrecognised report: expected Night Owl JSON ('findings') or SARIF 2.1 ('runs').");
        }
    }

    // ------------------------------------------------------------------ Night Owl JSON

    private static ParsedReport ParseNightOwl(JsonElement root)
    {
        var list = new List<ParsedFinding>();
        // The report has no tool version of its own; the signed engine manifest's sequence identifies the engine set.
        var version = Prop(root, "manifestSequence") is { ValueKind: JsonValueKind.Number } seq && seq.TryGetInt64(out var n) ? "manifest " + n : null;
        foreach (var f in root.GetProperty("findings").EnumerateArray())
        {
            if (f.ValueKind != JsonValueKind.Object) continue;
            if (list.Count >= MaxFindings) break;
            var id = Str(f, "id");
            var rule = Str(f, "ruleId") ?? "unknown";
            var title = Str(f, "title") ?? rule;
            var location = Prop(f, "location");
            var package = Prop(f, "package");
            var path = location is { ValueKind: JsonValueKind.Object } l ? Str(l, "path") : null;
            var line = location is { ValueKind: JsonValueKind.Object } l2 ? Int(l2, "startLine") : null;
            var snippet = location is { ValueKind: JsonValueKind.Object } l3 ? Str(l3, "snippet") : null;
            var engineList = Strings(f, "engines");
            var ecosystem = package is { ValueKind: JsonValueKind.Object } p0 ? Str(p0, "ecosystem") : null;
            var category = MapNightOwlCategory(Str(f, "category"), ecosystem, engineList);
            var refs = Strings(f, "references");
            list.Add(new ParsedFinding
            {
                Fingerprint = Clean(id) ?? CodeFingerprint(rule, path ?? "", snippet, line),
                Category = category,
                Severity = ParseSeverity(Str(f, "severity")),
                RuleId = rule,
                Title = title,
                Message = Str(f, "description"),
                PackageName = package is { ValueKind: JsonValueKind.Object } p1 ? Str(p1, "name") : null,
                PackageVersion = package is { ValueKind: JsonValueKind.Object } p2 ? Str(p2, "version") : null,
                Ecosystem = ecosystem,
                FixedVersions = Strings(f, "fixedVersions"),
                FilePath = path,
                Line = line,
                Engines = engineList,
                Aliases = Strings(f, "aliases"),
                HelpUrl = refs.FirstOrDefault(IsHttpUrl),
                Context = Str(f, "context")?.ToLowerInvariant(),
                ReportedTriage = ParseTriageState(Str(f, "triage")) ?? SecurityTriageState.New,
            });
        }
        return new ParsedReport(NightOwlFormat, "Night Owl", version, Dedupe(list));
    }

    /// <summary>Night Owl's <c>FindingCategory</c> → GitBench's. Vulnerabilities in container images become <see cref="SecurityCategory.Container"/>.</summary>
    public static SecurityCategory MapNightOwlCategory(string? category, string? ecosystem, IReadOnlyList<string> engines) =>
        (category ?? "").ToLowerInvariant() switch
        {
            "vulnerability" when IsContainer(ecosystem, engines) => SecurityCategory.Container,
            "vulnerability" => SecurityCategory.Dependency,
            "license" => SecurityCategory.License,
            "secret" => SecurityCategory.Secret,
            "infrastructure" => SecurityCategory.Infrastructure,
            "dynamic" => SecurityCategory.Dynamic,
            "quality" => SecurityCategory.Quality,
            _ => SecurityCategory.Code,
        };

    // Night Owl's image scanner (trivy-image) puts the image in the ecosystem: "debian (nginx:1.25)", or "container image".
    private static bool IsContainer(string? ecosystem, IReadOnlyList<string> engines) =>
        engines.Any(e => e.Equals("trivy-image", StringComparison.OrdinalIgnoreCase))
        || ecosystem != null && (ecosystem.Equals("container image", StringComparison.OrdinalIgnoreCase) || ContainerEcosystem().IsMatch(ecosystem));

    [GeneratedRegex(@"\s\([^()]+:[^()]+\)$")]
    private static partial Regex ContainerEcosystem();

    // ------------------------------------------------------------------ SARIF 2.1

    private static ParsedReport ParseSarif(JsonElement root)
    {
        var list = new List<ParsedFinding>();
        string? toolName = null, toolVersion = null;
        foreach (var run in root.GetProperty("runs").EnumerateArray())
        {
            if (run.ValueKind != JsonValueKind.Object) continue;
            var driver = Prop(run, "tool") is { ValueKind: JsonValueKind.Object } tool ? Prop(tool, "driver") : null;
            var name = driver is { ValueKind: JsonValueKind.Object } d ? Str(d, "name") : null;
            toolName ??= name;
            toolVersion ??= driver is { ValueKind: JsonValueKind.Object } d2 ? Str(d2, "version") ?? Str(d2, "semanticVersion") : null;
            var rules = new List<JsonElement>();
            if (driver is { ValueKind: JsonValueKind.Object } d3 && Prop(d3, "rules") is { ValueKind: JsonValueKind.Array } ra)
                rules.AddRange(ra.EnumerateArray());
            var ruleById = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var r in rules)
                if (r.ValueKind == JsonValueKind.Object && Str(r, "id") is { } rid) ruleById.TryAdd(rid, r);
            var engineName = (name ?? "sarif").ToLowerInvariant();

            if (Prop(run, "results") is not { ValueKind: JsonValueKind.Array } results) continue;
            foreach (var res in results.EnumerateArray())
            {
                if (res.ValueKind != JsonValueKind.Object) continue;
                if (list.Count >= MaxFindings) break;
                JsonElement? rule = null;
                var ruleId = Str(res, "ruleId");
                if (ruleId != null && ruleById.TryGetValue(ruleId, out var byId)) rule = byId;
                else if (Int(res, "ruleIndex") is { } idx && idx >= 0 && idx < rules.Count) { rule = rules[idx]; ruleId ??= Str(rules[idx], "id"); }
                ruleId ??= Prop(res, "rule") is { ValueKind: JsonValueKind.Object } rr ? Str(rr, "id") : null;
                ruleId ??= "unknown";

                var message = Prop(res, "message") is { ValueKind: JsonValueKind.Object } m ? Str(m, "text") ?? Str(m, "markdown") : null;
                var ruleTitle = rule is { } r1 ? Text(r1, "shortDescription") ?? Str(r1, "name") : null;
                var (path, line, snippet) = FirstLocation(res);
                var props = Prop(res, "properties");
                var ruleProps = rule is { } r2 ? Prop(r2, "properties") : null;
                var tags = new List<string>();
                if (ruleProps is { ValueKind: JsonValueKind.Object } rp) tags.AddRange(Strings(rp, "tags"));
                if (props is { ValueKind: JsonValueKind.Object } pp) tags.AddRange(Strings(pp, "tags"));

                var engines = props is { ValueKind: JsonValueKind.Object } pe && Strings(pe, "engines") is { Count: > 0 } es ? es : [engineName];
                var severity = props is { ValueKind: JsonValueKind.Object } ps && Str(ps, "severity") is { } sv ? ParseSeverity(sv)
                    : SecuritySeverityScore(ruleProps) ?? SecuritySeverityScore(props) ?? LevelSeverity(Str(res, "level") ?? (rule is { } r3 ? DefaultLevel(r3) : null));

                list.Add(new ParsedFinding
                {
                    Fingerprint = SarifFingerprint(res) ?? CodeFingerprint(ruleId, path ?? "", snippet, line),
                    Category = CategoryFromTags(tags, ruleId),
                    Severity = severity,
                    RuleId = ruleId,
                    Title = Truncate(ruleTitle ?? FirstLine(message) ?? ruleId, 500),
                    Message = message,
                    FilePath = path,
                    Line = line,
                    Engines = engines,
                    HelpUrl = rule is { } r4 && Str(r4, "helpUri") is { } help && IsHttpUrl(help) ? help : null,
                });
            }
        }
        return new ParsedReport(SarifFormat, toolName, toolVersion, Dedupe(list));
    }

    private static (string? Path, int? Line, string? Snippet) FirstLocation(JsonElement result)
    {
        if (Prop(result, "locations") is not { ValueKind: JsonValueKind.Array } locs) return (null, null, null);
        foreach (var loc in locs.EnumerateArray())
        {
            if (loc.ValueKind != JsonValueKind.Object || Prop(loc, "physicalLocation") is not { ValueKind: JsonValueKind.Object } phys) continue;
            var uri = Prop(phys, "artifactLocation") is { ValueKind: JsonValueKind.Object } al ? Str(al, "uri") : null;
            int? line = null;
            string? snippet = null;
            if (Prop(phys, "region") is { ValueKind: JsonValueKind.Object } region)
            {
                line = Int(region, "startLine");
                snippet = Prop(region, "snippet") is { ValueKind: JsonValueKind.Object } sn ? Str(sn, "text") : null;
            }
            if (uri != null) uri = NormalizeUri(uri);
            return (uri, line, snippet);
        }
        return (null, null, null);
    }

    private static string NormalizeUri(string uri)
    {
        if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) uri = uri[7..];
        uri = Uri.UnescapeDataString(uri).Replace('\\', '/');
        while (uri.StartsWith("./", StringComparison.Ordinal)) uri = uri[2..];
        // Absolute paths from a CI workspace: keep the part after /workspace/ (GitBench mounts the checkout there).
        var ws = uri.IndexOf("/workspace/", StringComparison.Ordinal);
        if (uri.StartsWith('/') && ws >= 0) uri = uri[(ws + "/workspace/".Length)..];
        return uri;
    }

    private static string? SarifFingerprint(JsonElement result)
    {
        foreach (var key in new[] { "partialFingerprints", "fingerprints" })
        {
            if (Prop(result, key) is not { ValueKind: JsonValueKind.Object } fp) continue;
            if (Prop(fp, "nightowl/v1") is { ValueKind: JsonValueKind.String } no && Clean(no.GetString()) is { } own) return own;
            foreach (var p in fp.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } v)
                    return v.Length <= 64 && SafeId().IsMatch(v) ? v : Hash("sarif", p.Name, v);
        }
        return null;
    }

    private static SecuritySeverity? SecuritySeverityScore(JsonElement? props)
    {
        if (props is not { ValueKind: JsonValueKind.Object } p || Prop(p, "security-severity") is not { } s) return null;
        double score;
        if (s.ValueKind == JsonValueKind.Number) score = s.GetDouble();
        else if (s.ValueKind == JsonValueKind.String && double.TryParse(s.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)) score = parsed;
        else return null;
        // CVSS bands, as GitHub code scanning uses them.
        return score switch { >= 9.0 => SecuritySeverity.Critical, >= 7.0 => SecuritySeverity.High, >= 4.0 => SecuritySeverity.Medium, > 0 => SecuritySeverity.Low, _ => SecuritySeverity.Info };
    }

    private static string? DefaultLevel(JsonElement rule) =>
        Prop(rule, "defaultConfiguration") is { ValueKind: JsonValueKind.Object } dc ? Str(dc, "level") : null;

    /// <summary>SARIF level → severity (the inverse of Night Owl's SarifWriter: error is High, warning Medium, note Low).</summary>
    private static SecuritySeverity LevelSeverity(string? level) => (level ?? "warning").ToLowerInvariant() switch
    {
        "error" => SecuritySeverity.High,
        "warning" => SecuritySeverity.Medium,
        "note" => SecuritySeverity.Low,
        "none" => SecuritySeverity.Info,
        _ => SecuritySeverity.Medium,
    };

    /// <summary>
    /// Category from SARIF tags (Night Owl writes its category as a tag: vulnerability, license, secret, code,
    /// infrastructure, dynamic, quality) or common words other tools use.
    /// </summary>
    private static SecurityCategory CategoryFromTags(IReadOnlyList<string> tags, string ruleId)
    {
        var t = tags.Select(x => x.ToLowerInvariant()).ToHashSet();
        if (t.Contains("container") || t.Contains("image")) return SecurityCategory.Container;
        if (t.Contains("vulnerability") || t.Contains("dependency") || t.Contains("sca") || ruleId.StartsWith("CVE-", StringComparison.OrdinalIgnoreCase)
            || ruleId.StartsWith("GHSA-", StringComparison.OrdinalIgnoreCase)) return SecurityCategory.Dependency;
        if (t.Contains("license")) return SecurityCategory.License;
        if (t.Contains("secret") || t.Contains("secrets")) return SecurityCategory.Secret;
        if (t.Contains("infrastructure") || t.Contains("iac") || t.Contains("misconfiguration")) return SecurityCategory.Infrastructure;
        if (t.Contains("dynamic") || t.Contains("dast")) return SecurityCategory.Dynamic;
        if (t.Contains("quality") || t.Contains("maintainability")) return SecurityCategory.Quality;
        return SecurityCategory.Code;
    }

    // ------------------------------------------------------------------ shared

    public static SecuritySeverity ParseSeverity(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "critical" => SecuritySeverity.Critical,
        "high" => SecuritySeverity.High,
        "medium" or "moderate" => SecuritySeverity.Medium,
        "low" => SecuritySeverity.Low,
        "info" or "informational" or "none" => SecuritySeverity.Info,
        _ => SecuritySeverity.Unknown,
    };

    /// <summary>Night Owl triage state names, in either the report's PascalCase or triage.json's kebab-case. Null if unknown.</summary>
    public static SecurityTriageState? ParseTriageState(string? value) => (value ?? "").Trim().ToLowerInvariant().Replace("_", "-") switch
    {
        "new" or "reset" or "" => SecurityTriageState.New,
        "confirmed" or "confirm" => SecurityTriageState.Confirmed,
        "falsepositive" or "false-positive" or "fp" => SecurityTriageState.FalsePositive,
        "acceptedrisk" or "accepted-risk" or "accept" or "wont-fix" => SecurityTriageState.AcceptedRisk,
        "fixed" => SecurityTriageState.Fixed,
        _ => null,
    };

    /// <summary>Night Owl's kebab-case name for a triage state (as in <c>.nightowl/triage.json</c>).</summary>
    public static string TriageStateName(SecurityTriageState s) => s switch
    {
        SecurityTriageState.FalsePositive => "false-positive",
        SecurityTriageState.AcceptedRisk => "accepted-risk",
        SecurityTriageState.Confirmed => "confirmed",
        SecurityTriageState.Fixed => "fixed",
        _ => "new",
    };

    /// <summary>
    /// The same scheme as Night Owl's <c>Fingerprint.ForCode</c>: SHA-256 over rule, normalized path and the normalized
    /// snippet (or the line when there is none), first 32 hex digits.
    /// </summary>
    public static string CodeFingerprint(string ruleId, string path, string? snippet, int? line)
    {
        var normalizedPath = path.Replace('\\', '/').TrimStart('.', '/');
        var anchor = string.IsNullOrWhiteSpace(snippet) ? $"line:{line}" : Whitespace().Replace(snippet, " ").Trim();
        return Hash("code", ruleId, normalizedPath, anchor);
    }

    private static string Hash(params string[] parts) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', parts))))[..32];

    private static List<ParsedFinding> Dedupe(List<ParsedFinding> list)
    {
        var byId = new Dictionary<string, ParsedFinding>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var f in list)
        {
            if (!byId.TryGetValue(f.Fingerprint, out var seen)) { byId[f.Fingerprint] = f; order.Add(f.Fingerprint); continue; }
            var keep = f.Severity > seen.Severity ? f : seen;
            byId[f.Fingerprint] = keep with
            {
                Engines = seen.Engines.Concat(f.Engines).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Aliases = seen.Aliases.Concat(f.Aliases).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                FixedVersions = seen.FixedVersions.Concat(f.FixedVersions).Distinct(StringComparer.Ordinal).ToList(),
                ReportedTriage = seen.ReportedTriage != SecurityTriageState.New ? seen.ReportedTriage : f.ReportedTriage,
            };
        }
        return order.Select(id => byId[id]).ToList();
    }

    /// <summary>A fingerprint from a report: kept when it's a short safe token, else hashed.</summary>
    private static string? Clean(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        id = id.Trim();
        return id.Length <= 64 && SafeId().IsMatch(id) ? id : Hash("id", id);
    }

    [GeneratedRegex("^[A-Za-z0-9._:/+=-]+$")]
    private static partial Regex SafeId();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private static JsonElement? Prop(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (e.TryGetProperty(name, out var v)) return v;
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        Prop(e, name) is { } v ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() is { Length: > 0 } s ? s : null,
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static int? Int(JsonElement e, string name) =>
        Prop(e, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var i) ? i : null;

    private static string? Text(JsonElement e, string name) =>
        Prop(e, name) is { ValueKind: JsonValueKind.Object } o ? Str(o, "text") : null;

    private static List<string> Strings(JsonElement e, string name) =>
        Prop(e, name) is { ValueKind: JsonValueKind.Array } a
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(s => s.Length > 0).ToList()
            : [];

    private static bool IsHttpUrl(string s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    private static string? FirstLine(string? s) => s?.Trim().Split('\n')[0].Trim() is { Length: > 0 } l ? l : null;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
