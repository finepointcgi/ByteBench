using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace ByteBench.Services.Workspaces;

/// <summary>
/// One allowed destination: a host (<c>example.com</c>, <c>*.example.com</c> for every subdomain, or an IPv4 literal) and a port.
/// <see cref="Port"/> is <see cref="DefaultWebPorts"/> (80 and 443, the default when none is written), <see cref="AnyPort"/> (<c>host:*</c>) or one port.
/// </summary>
public sealed record EgressRule(string Host, int Port)
{
    public const int DefaultWebPorts = 0;
    public const int AnyPort = -1;

    public override string ToString() => Port == DefaultWebPorts ? Host : Port == AnyPort ? Host + ":*" : $"{Host}:{Port}";

    public bool MatchesPort(int port) => Port == AnyPort || (Port == DefaultWebPorts ? port is 80 or 443 : Port == port);

    public bool MatchesHost(string host)
    {
        host = host.ToLowerInvariant().TrimEnd('.');
        return Host.StartsWith("*.", StringComparison.Ordinal) ? host.EndsWith(Host[1..], StringComparison.Ordinal) && host.Length > Host.Length - 1 : host == Host;
    }

    public bool Matches(string host, int port) => MatchesPort(port) && MatchesHost(host);

    /// <summary>Whether everything <paramref name="narrower"/> allows is also allowed by this rule.</summary>
    public bool Covers(EgressRule narrower)
    {
        bool hostOk;
        if (Host.StartsWith("*.", StringComparison.Ordinal))
        {
            // "*.example.com" covers "a.example.com", "*.example.com" and "*.a.example.com", but not the apex "example.com".
            var suffix = Host[1..]; // ".example.com"
            hostOk = (narrower.Host.StartsWith("*.", StringComparison.Ordinal) ? narrower.Host[1..] : narrower.Host).EndsWith(suffix, StringComparison.Ordinal);
        }
        else hostOk = narrower.Host == Host;
        if (!hostOk) return false;
        return Port switch
        {
            AnyPort => true,
            DefaultWebPorts => narrower.Port == DefaultWebPorts || narrower.Port is 80 or 443,
            _ => narrower.Port == Port,
        };
    }
}

/// <summary>A workspace's network policy: <c>open</c>, <c>public-only</c> (no private networks), <c>allowlist</c> (only the rules) or <c>none</c>.</summary>
public sealed record EffectiveEgress(string Mode, IReadOnlyList<EgressRule> Rules, IReadOnlyList<string> Warnings, string AdminMode, bool RepoNarrowed)
{
    public bool UsesProxy => Mode == WorkspaceEgressPolicy.Allowlist;

    public bool Allows(string host, int port) => Mode switch
    {
        WorkspaceEgressPolicy.Allowlist => Rules.Any(r => r.Matches(host, port)),
        WorkspaceEgressPolicy.None => false,
        _ => true,
    };
}

/// <summary>What a repository's <c>.bytebench/workspace.yml</c> <c>egress:</c> section asked for (before it is held against the admin policy).</summary>
public sealed record RepoEgressSpec(string? Mode, IReadOnlyList<EgressRule>? Allow, IReadOnlyList<string> Errors);

/// <summary>
/// The rules of workspace egress. The admin sets a mode and an allow-list; a repository's <c>egress:</c> may only <i>narrow</i> that
/// (<see cref="Narrow"/>): a stricter mode, and an allow-list reduced to what the admin's list already covers. Anything wider is dropped with a warning.
/// </summary>
public static partial class WorkspaceEgressPolicy
{
    public const string Open = "open", PublicOnly = "public-only", Allowlist = "allowlist", None = "none";
    public static readonly string[] Modes = [Open, PublicOnly, Allowlist, None];
    public const int MaxRules = 200;

    /// <summary>The admin default allow-list when nothing is stored: git hosts and the package registries a developer needs, nothing else.</summary>
    public static readonly string[] DefaultAllow = ["@git", "@npm", "@nuget", "@pypi", "@crates", "@go", "@maven"];

    /// <summary>Named groups of hosts (<c>@npm</c>) for the admin list and a repository's <c>allow:</c>. ByteBench's own server is always reachable (it is the git remote).</summary>
    public static readonly IReadOnlyDictionary<string, (string Title, string[] Hosts)> Presets = new Dictionary<string, (string, string[])>
    {
        ["git"] = ("Git hosts", ["github.com", "codeload.github.com", "objects.githubusercontent.com", "raw.githubusercontent.com", "gitlab.com", "bitbucket.org"]),
        ["npm"] = ("npm", ["registry.npmjs.org", "registry.yarnpkg.com"]),
        ["nuget"] = ("NuGet", ["api.nuget.org", "globalcdn.nuget.org", "*.nuget.org"]),
        ["pypi"] = ("PyPI", ["pypi.org", "files.pythonhosted.org"]),
        ["crates"] = ("crates.io", ["crates.io", "index.crates.io", "static.crates.io"]),
        ["go"] = ("Go modules", ["proxy.golang.org", "sum.golang.org"]),
        ["maven"] = ("Maven Central", ["repo.maven.apache.org", "repo1.maven.org"]),
    };

    public static int Rank(string mode) => mode switch { Open => 0, PublicOnly => 1, Allowlist => 2, None => 3, _ => 1 };
    public static bool IsMode(string? mode) => mode != null && Modes.Contains(mode);

    [GeneratedRegex(@"^(\*\.)?([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$")]
    private static partial Regex HostPattern();

    /// <summary>Parses <c>host</c>, <c>host:port</c>, <c>host:*</c> or <c>@preset</c> into rules; invalid entries go to <paramref name="errors"/>.</summary>
    public static List<EgressRule> ParseEntries(IEnumerable<string> entries, List<string> errors)
    {
        var rules = new List<EgressRule>();
        foreach (var raw in entries)
        {
            var entry = (raw ?? "").Trim();
            if (entry.Length == 0) continue;
            if (entry[0] == '@')
            {
                if (Presets.TryGetValue(entry[1..].ToLowerInvariant(), out var p)) rules.AddRange(p.Hosts.Select(h => new EgressRule(h, EgressRule.DefaultWebPorts)));
                else errors.Add($"Unknown preset '{entry}' (known: {string.Join(", ", Presets.Keys.Select(k => "@" + k))}).");
                continue;
            }
            if (TryParseRule(entry, out var rule, out var error)) rules.Add(rule!);
            else errors.Add(error!);
        }
        var distinct = rules.Distinct().ToList();
        if (distinct.Count > MaxRules) { errors.Add($"At most {MaxRules} rules are allowed."); distinct = distinct.Take(MaxRules).ToList(); }
        return distinct;
    }

    public static bool TryParseRule(string entry, out EgressRule? rule, out string? error)
    {
        rule = null;
        error = null;
        var text = entry.Trim().ToLowerInvariant();
        if (text.Contains("://") || text.Contains('/') || text.Contains(' ') || text.Any(char.IsControl)) { error = $"'{entry}': write a host (and optional port), not a URL."; return false; }
        var host = text;
        var port = EgressRule.DefaultWebPorts;
        var colon = text.LastIndexOf(':');
        if (colon >= 0)
        {
            host = text[..colon];
            var p = text[(colon + 1)..];
            if (p == "*") port = EgressRule.AnyPort;
            else if (int.TryParse(p, out var n) && n is >= 1 and <= 65535) port = n;
            else { error = $"'{entry}': the port must be 1-65535 or *."; return false; }
        }
        host = host.TrimEnd('.');
        var isIpv4 = System.Net.IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
        if (host.Length is 0 or > 253 || !(HostPattern().IsMatch(host) || isIpv4)) { error = $"'{entry}' is not a valid host."; return false; }
        if (host.StartsWith("*.", StringComparison.Ordinal) && !host[2..].Contains('.')) { error = $"'{entry}': a wildcard needs a domain below the top level (*.example.com)."; return false; }
        rule = new EgressRule(host, port);
        return true;
    }

    /// <summary>Splits a settings text (one entry per line or comma-separated; <c>#</c> starts a comment).</summary>
    public static List<string> SplitEntries(string? text) =>
        (text ?? "").Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Contains('#') ? l[..l.IndexOf('#')].Trim() : l).Where(l => l.Length > 0).ToList();

    /// <summary>The admin policy as stored: a mode and the entries (rules, <c>@presets</c>); an unknown mode falls back to <paramref name="fallbackMode"/>.</summary>
    public static EffectiveEgress Admin(string? mode, string? entriesText, string fallbackMode)
    {
        var m = IsMode(mode) ? mode! : IsMode(fallbackMode) ? fallbackMode : PublicOnly;
        var errors = new List<string>();
        var entries = entriesText == null ? DefaultAllow.ToList() : SplitEntries(entriesText);
        var rules = ParseEntries(entries, errors);
        return new EffectiveEgress(m, rules, errors, m, false);
    }

    // ---------- repository spec ----------

    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    /// <summary>
    /// The <c>egress:</c> section of a <c>.bytebench/workspace.yml</c> (null when there is none). Accepts <c>egress: none</c>, <c>egress: [hosts]</c>
    /// and <c>egress: {mode: allowlist, allow: [hosts]}</c>; with only <c>allow:</c> the mode is <c>allowlist</c>. Other keys in the file are ignored.
    /// </summary>
    public static RepoEgressSpec? ParseRepoSpec(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return null;
        if (yaml.Length > 64 * 1024) return new RepoEgressSpec(null, null, ["workspace.yml is too large."]);
        Dictionary<object, object?>? doc;
        try { doc = Yaml.Deserialize<Dictionary<object, object?>>(yaml); }
        catch (YamlException ex) { return new RepoEgressSpec(null, null, ["workspace.yml isn't valid YAML: " + ex.Message]); }
        if (doc == null || !doc.TryGetValue("egress", out var section) || section == null) return null;

        var errors = new List<string>();
        string? mode = null;
        List<string>? allow = null;
        switch (section)
        {
            case string s: mode = s.Trim().ToLowerInvariant(); break;
            case List<object> list: allow = list.Select(x => x?.ToString() ?? "").ToList(); break;
            case Dictionary<object, object?> map:
                foreach (var (k, v) in map)
                {
                    switch (k?.ToString()?.ToLowerInvariant())
                    {
                        case "mode": mode = v?.ToString()?.Trim().ToLowerInvariant(); break;
                        case "allow": allow = v is List<object> l ? l.Select(x => x?.ToString() ?? "").ToList() : v is string one ? SplitEntries(one) : []; break;
                        default: errors.Add($"Unknown egress key '{k}'."); break;
                    }
                }
                break;
            default: errors.Add("egress: must be a mode, a list of hosts, or a map with mode and allow."); break;
        }
        if (mode != null && !IsMode(mode)) { errors.Add($"Unknown egress mode '{mode}' (use {string.Join(", ", Modes)})."); mode = null; }
        if (mode == null && allow != null) mode = Allowlist;
        var rules = allow == null ? null : ParseEntries(allow, errors);
        return new RepoEgressSpec(mode, rules, errors);
    }

    /// <summary>
    /// Holds a repository's request against the admin policy and returns what applies. Never wider than <paramref name="admin"/>:
    /// a looser mode is ignored, an allow-list keeps only rules the admin list covers (when the admin policy is an allow-list), and
    /// <c>none</c> always wins. Whatever was refused is listed in <see cref="EffectiveEgress.Warnings"/>.
    /// </summary>
    public static EffectiveEgress Narrow(EffectiveEgress admin, RepoEgressSpec? repo)
    {
        var warnings = admin.Warnings.Select(w => "Admin policy: " + w).ToList();
        if (repo == null) return admin with { Warnings = warnings };
        warnings.AddRange(repo.Errors.Select(e => "Repository egress: " + e));
        if (repo.Mode == null) return admin with { Warnings = warnings };

        if (Rank(repo.Mode) < Rank(admin.Mode))
        {
            warnings.Add($"Repository egress '{repo.Mode}' is wider than the server's '{admin.Mode}' and was ignored (a repository can only narrow the policy).");
            return admin with { Warnings = warnings };
        }
        if (repo.Mode == None) return new EffectiveEgress(None, [], warnings, admin.Mode, admin.Mode != None);
        if (repo.Mode != Allowlist) return admin with { Warnings = warnings }; // same strictness as the admin's: nothing narrower to apply

        var wanted = repo.Allow ?? [];
        List<EgressRule> kept;
        if (admin.Mode == Allowlist)
        {
            kept = [];
            foreach (var r in wanted)
            {
                if (admin.Rules.Any(a => a.Covers(r))) kept.Add(r);
                else warnings.Add($"Repository egress rule '{r}' isn't allowed by the server's allow-list and was dropped.");
            }
        }
        else kept = wanted.ToList(); // from open or public-only any list is narrower
        return new EffectiveEgress(Allowlist, kept.Distinct().ToList(), warnings, admin.Mode, true);
    }

    public static string RulesJson(IEnumerable<EgressRule> rules) => JsonSerializer.Serialize(rules.Select(r => r.ToString()));

    public static List<EgressRule> RulesFromJson(string? json)
    {
        try { return ParseEntries(JsonSerializer.Deserialize<List<string>>(json ?? "[]") ?? [], []); }
        catch (JsonException) { return []; }
    }
}
