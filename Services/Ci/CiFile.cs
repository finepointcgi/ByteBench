using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GitBench.Services.Ci;

/// <summary>A parsed, validated <c>.gitbench-ci.yml</c>.</summary>
public sealed record CiDefinition(CiTriggers On, IReadOnlyList<CiJobDefinition> Jobs)
{
    /// <summary>
    /// The jobs that run on <paramref name="branch"/> (after <c>rules</c>/<c>only</c>), each with its effective
    /// <c>when</c>. An error if a remaining job needs one that was left out.
    /// </summary>
    public (IReadOnlyList<CiJobDefinition> Jobs, string? Error) ForBranch(string branch)
    {
        var kept = new List<CiJobDefinition>();
        foreach (var j in Jobs)
            if (j.WhenFor(branch) is { } when)
                kept.Add(when == j.When ? j : j with { When = when });
        var names = kept.Select(j => j.Name).ToHashSet();
        foreach (var j in kept)
            foreach (var n in j.Needs)
                if (!names.Contains(n))
                    return ([], $"{CiFile.FileName}: job '{j.Name}' needs '{n}', which doesn't run on branch '{branch}' (see its rules).");
        return (kept, null);
    }
}

/// <summary>
/// When pipelines run automatically. <see cref="Branches"/> null means every branch; patterns use <c>*</c>
/// (anything but <c>/</c>) and <c>**</c> (anything).
/// </summary>
public sealed record CiTriggers(IReadOnlyList<string>? Branches, bool PullRequests)
{
    public bool MatchesBranch(string branch) => Branches == null || Branches.Any(p => CiFile.GlobMatch(p, branch));
}

public sealed record CiJobDefinition(string Name, string Image, IReadOnlyList<string> Needs, IReadOnlyList<CiStepDefinition> Steps)
{
    /// <summary><c>when:</c> on_success (default) or manual.</summary>
    public CiJobWhen When { get; init; } = CiJobWhen.OnSuccess;
    /// <summary><c>allow_failure:</c> as written; null when the file doesn't say.</summary>
    public bool? AllowFailureSetting { get; init; }
    /// <summary>A failure doesn't fail the pipeline. Defaults to true for manual jobs (they don't block), false otherwise.</summary>
    public bool AllowFailure => AllowFailureSetting ?? When == CiJobWhen.Manual;
    public CiArtifactsDefinition? Artifacts { get; init; }
    public CiCacheDefinition? Cache { get; init; }
    public CiEnvironmentDefinition? Environment { get; init; }
    /// <summary><c>rules:</c> (or <c>only:</c>); empty means the job runs on every branch.</summary>
    public IReadOnlyList<CiRule> Rules { get; init; } = [];

    /// <summary>
    /// How the job runs on <paramref name="branch"/>: null if it doesn't (no rule matched, or the first matching rule
    /// says <c>never</c>), else its <c>when</c>. The first rule whose branches match decides.
    /// </summary>
    public CiJobWhen? WhenFor(string branch)
    {
        if (Rules.Count == 0) return When;
        foreach (var r in Rules)
        {
            if (r.Branches != null && !r.Branches.Any(p => CiFile.GlobMatch(p, branch))) continue;
            return r.When switch { null => When, CiRuleWhen.Never => null, CiRuleWhen.Manual => CiJobWhen.Manual, _ => CiJobWhen.OnSuccess };
        }
        return null;
    }
}

public sealed record CiStepDefinition(string Name, string Run);

public enum CiJobWhen { OnSuccess, Manual }
public enum CiRuleWhen { OnSuccess, Manual, Never }
public enum CiArtifactsWhen { OnSuccess, OnFailure, Always }

/// <summary>One entry of <c>rules:</c>. <see cref="Branches"/> null matches every branch; <see cref="When"/> null keeps the job's.</summary>
public sealed record CiRule(IReadOnlyList<string>? Branches, CiRuleWhen? When);

/// <summary><c>artifacts:</c>. <see cref="ExpireIn"/> null means they never expire.</summary>
public sealed record CiArtifactsDefinition(IReadOnlyList<string> Paths, TimeSpan? ExpireIn, CiArtifactsWhen When);

/// <summary><c>cache:</c>. <see cref="Key"/> is a template: literal text plus <c>{{ hashFiles('a', 'b/**') }}</c>.</summary>
public sealed record CiCacheDefinition(string Key, IReadOnlyList<string> Paths);

/// <summary><c>environment:</c> the job deploys to <see cref="Name"/>.</summary>
public sealed record CiEnvironmentDefinition(string Name, string? Url);

/// <summary>The result of parsing: a definition, or a readable error.</summary>
public sealed record CiParseResult(CiDefinition? Definition, string? Error)
{
    public bool Ok => Definition != null;
}

/// <summary>
/// Parser for <c>.gitbench-ci.yml</c>. Format:
/// <code>
/// image: alpine:3.20                     # default image for jobs (default alpine:3.20)
/// on:                                    # optional; default: every push and every pull request
///   branches: [main, "feature/*"]
///   pull_requests: true
/// jobs:
///   build:
///     image: mcr.microsoft.com/dotnet/sdk:10.0   # optional per-job override
///     cache: { key: "nuget-{{ hashFiles('**/*.csproj') }}", paths: [.nuget/] }
///     artifacts: { paths: [dist/, "*.log"], expire_in: 7d, when: on_success }
///     steps:
///       - name: Build
///         run: dotnet build
///   test:
///     needs: [build]                     # jobs without needs run in parallel; needs also get the needed jobs' artifacts
///     steps: [ { name: Test, run: ./test.sh } ]
///   deploy:
///     needs: [test]
///     when: manual                       # waits for someone to click Run
///     allow_failure: false               # manual jobs don't block the pipeline unless this is false
///     only: [main]                       # or rules: [ { branches: [main], when: manual }, { when: never } ]
///     environment: { name: production, url: https://example.com }
///     steps: [ { run: ./deploy.sh } ]
/// </code>
/// Unknown keys are errors, so typos are caught.
/// </summary>
public static partial class CiFile
{
    public const string FileName = ".gitbench-ci.yml";
    public const string DefaultImage = "alpine:3.20";
    public const int MaxJobs = 20, MaxStepsPerJob = 50, MaxFileChars = 64 * 1024, MaxPaths = 50, MaxRules = 20;
    public static readonly TimeSpan DefaultArtifactExpiry = TimeSpan.FromDays(30);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex JobNamePattern();

    // Docker image reference: registry/name:tag@digest. Must not start with '-' so it can never be read as a flag.
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/:@-]{0,254}$")]
    private static partial Regex ImagePattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 _./-]{0,63}$")]
    private static partial Regex EnvironmentNamePattern();

    [GeneratedRegex(@"^\s*(\d{1,5})\s*(m|min|mins|minutes?|h|hrs?|hours?|d|days?|w|wks?|weeks?|mo|months?|y|yrs?|years?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DurationPattern();

    private sealed class FileDto
    {
        public string? Image { get; set; }
        public OnDto? On { get; set; }
        public Dictionary<string, JobDto?>? Jobs { get; set; }
    }

    private sealed class OnDto
    {
        public List<string>? Branches { get; set; }
        public bool? PullRequests { get; set; }
    }

    private sealed class JobDto
    {
        public string? Image { get; set; }
        public List<string>? Needs { get; set; }
        public List<StepDto?>? Steps { get; set; }
        public string? When { get; set; }
        public bool? AllowFailure { get; set; }
        public List<string>? Only { get; set; }
        public List<RuleDto?>? Rules { get; set; }
        public ArtifactsDto? Artifacts { get; set; }
        public CacheDto? Cache { get; set; }
        public EnvironmentDto? Environment { get; set; }
    }

    private sealed class StepDto
    {
        public string? Name { get; set; }
        public string? Run { get; set; }
    }

    private sealed class RuleDto
    {
        public List<string>? Branches { get; set; }
        public string? When { get; set; }
    }

    private sealed class ArtifactsDto
    {
        public List<string>? Paths { get; set; }
        public string? ExpireIn { get; set; }
        public string? When { get; set; }
    }

    private sealed class CacheDto
    {
        public string? Key { get; set; }
        public List<string>? Paths { get; set; }
    }

    private sealed class EnvironmentDto
    {
        public string? Name { get; set; }
        public string? Url { get; set; }
    }

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    public static CiParseResult Parse(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return Fail("The file is empty.");
        if (yaml.Length > MaxFileChars) return Fail($"The file is larger than {MaxFileChars / 1024} KB.");

        FileDto? dto;
        try { dto = Deserializer.Deserialize<FileDto>(yaml); }
        catch (YamlException ex) { return Fail(Describe(ex)); }
        if (dto == null) return Fail("The file is empty.");

        var defaultImage = dto.Image?.Trim() is { Length: > 0 } i ? i : DefaultImage;
        if (!ImagePattern().IsMatch(defaultImage)) return Fail($"'{defaultImage}' is not a valid image name.");

        if (dto.Jobs == null || dto.Jobs.Count == 0) return Fail("Define at least one job under 'jobs:'.");
        if (dto.Jobs.Count > MaxJobs) return Fail($"Too many jobs ({dto.Jobs.Count}); the limit is {MaxJobs}.");

        var jobs = new List<CiJobDefinition>();
        foreach (var (name, job) in dto.Jobs)
        {
            if (!JobNamePattern().IsMatch(name ?? ""))
                return Fail($"Job name '{name}' is invalid: use letters, digits, '_', '.' or '-' (up to 64 characters).");
            if (job == null) return Fail($"Job '{name}' is empty: give it 'steps:'.");
            var image = job.Image?.Trim() is { Length: > 0 } ji ? ji : defaultImage;
            if (!ImagePattern().IsMatch(image)) return Fail($"Job '{name}': '{image}' is not a valid image name.");
            if (job.Steps == null || job.Steps.Count == 0) return Fail($"Job '{name}' has no steps.");
            if (job.Steps.Count > MaxStepsPerJob) return Fail($"Job '{name}' has too many steps (limit {MaxStepsPerJob}).");

            var steps = new List<CiStepDefinition>();
            for (var n = 0; n < job.Steps.Count; n++)
            {
                var s = job.Steps[n];
                if (string.IsNullOrWhiteSpace(s?.Run)) return Fail($"Job '{name}', step {n + 1}: 'run:' is required.");
                var stepName = s.Name?.Trim() is { Length: > 0 } sn ? sn : FirstLine(s.Run);
                steps.Add(new CiStepDefinition(stepName.Length > 200 ? stepName[..200] : stepName, s.Run));
            }
            var needs = (job.Needs ?? []).Select(x => x?.Trim() ?? "").Distinct().ToList();

            var extras = ParseExtras(name!, job);
            if (extras.Error != null) return Fail(extras.Error);
            jobs.Add(new CiJobDefinition(name!, image, needs, steps)
            {
                When = extras.When,
                AllowFailureSetting = job.AllowFailure,
                Rules = extras.Rules,
                Artifacts = extras.Artifacts,
                Cache = extras.Cache,
                Environment = extras.Environment,
            });
        }

        var names = jobs.Select(j => j.Name).ToHashSet();
        foreach (var j in jobs)
            foreach (var need in j.Needs)
            {
                if (need == j.Name) return Fail($"Job '{j.Name}' can't need itself.");
                if (!names.Contains(need)) return Fail($"Job '{j.Name}' needs '{need}', which is not defined.");
            }
        if (FindCycle(jobs) is { } cycle) return Fail($"The 'needs' of these jobs form a cycle: {cycle}.");

        var branches = dto.On?.Branches?.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim()).ToList();
        return new CiParseResult(new CiDefinition(new CiTriggers(branches, dto.On?.PullRequests ?? true), jobs), null);
    }

    private sealed record Extras(CiJobWhen When, IReadOnlyList<CiRule> Rules, CiArtifactsDefinition? Artifacts,
        CiCacheDefinition? Cache, CiEnvironmentDefinition? Environment, string? Error);

    private static Extras ParseExtras(string name, JobDto job)
    {
        Extras Err(string e) => new(CiJobWhen.OnSuccess, [], null, null, null, $"Job '{name}': {e}");

        CiJobWhen when;
        switch (job.When?.Trim())
        {
            case null or "" or "on_success": when = CiJobWhen.OnSuccess; break;
            case "manual": when = CiJobWhen.Manual; break;
            default: return Err($"'when: {job.When}' is not valid: use on_success or manual.");
        }

        if (job.Only != null && job.Rules != null) return Err("use either 'only' or 'rules', not both.");
        var rules = new List<CiRule>();
        if (job.Only != null)
        {
            var only = job.Only.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim()).ToList();
            if (only.Count == 0) return Err("'only' needs at least one branch pattern.");
            rules.Add(new CiRule(only, null));
        }
        if (job.Rules != null)
        {
            if (job.Rules.Count == 0) return Err("'rules' needs at least one rule.");
            if (job.Rules.Count > MaxRules) return Err($"too many rules (limit {MaxRules}).");
            foreach (var r in job.Rules)
            {
                if (r == null) return Err("a rule is empty: give it 'branches' or 'when'.");
                List<string>? branches = null;
                if (r.Branches != null)
                {
                    branches = r.Branches.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim()).ToList();
                    if (branches.Count == 0) return Err("a rule's 'branches' needs at least one pattern.");
                }
                CiRuleWhen? rw = r.When?.Trim() switch
                {
                    null or "" => null,
                    "on_success" => CiRuleWhen.OnSuccess,
                    "manual" => CiRuleWhen.Manual,
                    "never" => CiRuleWhen.Never,
                    _ => (CiRuleWhen)(-1),
                };
                if (rw == (CiRuleWhen)(-1)) return Err($"rule 'when: {r.When}' is not valid: use on_success, manual or never.");
                rules.Add(new CiRule(branches, rw));
            }
        }

        CiArtifactsDefinition? artifacts = null;
        if (job.Artifacts != null)
        {
            if (ValidatePaths(job.Artifacts.Paths, "artifacts") is { } pe) return Err(pe);
            TimeSpan? expire = DefaultArtifactExpiry;
            if (job.Artifacts.ExpireIn != null)
            {
                if (!TryParseDuration(job.Artifacts.ExpireIn, out expire))
                    return Err($"artifacts 'expire_in: {job.Artifacts.ExpireIn}' is not valid: use e.g. 30m, 12h, 7d, 2w, 3mo, 1y or never.");
            }
            CiArtifactsWhen aw;
            switch (job.Artifacts.When?.Trim())
            {
                case null or "" or "on_success": aw = CiArtifactsWhen.OnSuccess; break;
                case "on_failure": aw = CiArtifactsWhen.OnFailure; break;
                case "always": aw = CiArtifactsWhen.Always; break;
                default: return Err($"artifacts 'when: {job.Artifacts.When}' is not valid: use on_success, on_failure or always.");
            }
            artifacts = new CiArtifactsDefinition(Clean(job.Artifacts.Paths!), expire, aw);
        }

        CiCacheDefinition? cache = null;
        if (job.Cache != null)
        {
            if (ValidatePaths(job.Cache.Paths, "cache") is { } pe) return Err(pe);
            var key = job.Cache.Key?.Trim() is { Length: > 0 } k ? k : "default";
            if (CiCacheKey.Validate(key) is { } ke) return Err($"cache key '{key}': {ke}");
            cache = new CiCacheDefinition(key, Clean(job.Cache.Paths!));
        }

        CiEnvironmentDefinition? environment = null;
        if (job.Environment != null)
        {
            var en = job.Environment.Name?.Trim() ?? "";
            if (!EnvironmentNamePattern().IsMatch(en))
                return Err($"environment name '{en}' is invalid: use letters, digits, spaces, '_', '.', '/' or '-' (up to 64 characters).");
            string? url = null;
            if (!string.IsNullOrWhiteSpace(job.Environment.Url))
            {
                url = job.Environment.Url.Trim();
                if (url.Length > 500 || !Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http"))
                    return Err($"environment url '{url}' must be an absolute http or https URL.");
            }
            environment = new CiEnvironmentDefinition(en, url);
        }

        return new Extras(when, rules, artifacts, cache, environment, null);
    }

    /// <summary>True if <paramref name="name"/> can name an environment.</summary>
    public static bool IsValidEnvironmentName(string name) => EnvironmentNamePattern().IsMatch(name);

    private static List<string> Clean(List<string> paths) =>
        paths.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct().ToList();

    /// <summary>Artifact and cache paths: relative to the workspace, no <c>..</c>, globs allowed.</summary>
    private static string? ValidatePaths(List<string>? paths, string what)
    {
        var list = paths?.Select(p => p?.Trim() ?? "").Where(p => p.Length > 0).ToList();
        if (list == null || list.Count == 0) return $"{what} 'paths' needs at least one path.";
        if (list.Count > MaxPaths) return $"too many {what} paths (limit {MaxPaths}).";
        foreach (var p in list)
            if (CiArtifacts.ValidatePattern(p) is { } e) return $"{what} path '{p}' {e}";
        return null;
    }

    /// <summary>Parses <c>expire_in</c>: <c>30m</c>, <c>12h</c>, <c>7d</c>, <c>2 weeks</c>, <c>3mo</c>, <c>1y</c>, or <c>never</c> (null).</summary>
    public static bool TryParseDuration(string text, out TimeSpan? value)
    {
        value = null;
        if (text.Trim().Equals("never", StringComparison.OrdinalIgnoreCase)) return true;
        var m = DurationPattern().Match(text);
        if (!m.Success) return false;
        var n = int.Parse(m.Groups[1].Value);
        if (n <= 0) return false;
        var unit = m.Groups[2].Value.ToLowerInvariant();
        var span = unit[0] switch
        {
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            'w' => TimeSpan.FromDays(7 * n),
            'y' => TimeSpan.FromDays(365 * n),
            'm' when unit.StartsWith("mo") => TimeSpan.FromDays(30 * n),
            _ => TimeSpan.FromMinutes(n),
        };
        if (span > TimeSpan.FromDays(3650)) return false; // at most ten years; use 'never' beyond that
        value = span;
        return true;
    }

    /// <summary>Glob match for branch filters: <c>*</c> matches within one path segment, <c>**</c> across segments.</summary>
    public static bool GlobMatch(string pattern, string value)
    {
        var rx = "^" + Regex.Escape(pattern).Replace(@"\*\*", "\u0001").Replace(@"\*", "[^/]*").Replace("\u0001", ".*")
            .Replace(@"\?", "[^/]") + "$";
        return Regex.IsMatch(value, rx, RegexOptions.None, TimeSpan.FromMilliseconds(100));
    }

    private static string? FindCycle(List<CiJobDefinition> jobs)
    {
        var byName = jobs.ToDictionary(j => j.Name);
        var state = new Dictionary<string, int>(); // 1 = visiting, 2 = done
        var stack = new List<string>();
        string? Visit(string n)
        {
            if (state.GetValueOrDefault(n) == 2) return null;
            if (state.GetValueOrDefault(n) == 1) return string.Join(" → ", stack.SkipWhile(s => s != n).Append(n));
            state[n] = 1;
            stack.Add(n);
            foreach (var d in byName[n].Needs)
                if (Visit(d) is { } c) return c;
            stack.RemoveAt(stack.Count - 1);
            state[n] = 2;
            return null;
        }
        foreach (var j in jobs)
            if (Visit(j.Name) is { } c) return c;
        return null;
    }

    private static string FirstLine(string s)
    {
        var line = s.Trim().Split('\n')[0].Trim();
        return line.Length == 0 ? "Step" : line;
    }

    private static string Describe(YamlException ex)
    {
        // YamlDotNet wraps the real problem (e.g. an unknown key) in InnerException.
        var inner = ex.InnerException as YamlException ?? ex;
        var msg = inner.Message;
        var close = msg.IndexOf("): ", StringComparison.Ordinal);
        if (msg.StartsWith('(') && close > 0) msg = msg[(close + 3)..];
        msg = msg.Replace("GitBench.Services.Ci.CiFile+", "").Replace("Dto", "");
        return $"invalid YAML at line {inner.Start.Line}, column {inner.Start.Column}: {msg}";
    }

    private static CiParseResult Fail(string error) => new(null, $"{FileName}: {error}");
}
