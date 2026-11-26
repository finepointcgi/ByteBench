using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ByteBench.Services.Ci;

/// <summary>A parsed, validated <c>.bytebench-ci.yml</c>.</summary>
public sealed record CiDefinition(CiTriggers On, IReadOnlyList<CiJobDefinition> Jobs)
{
    /// <summary>
    /// The jobs that run on <paramref name="branch"/> (after <c>rules</c>/<c>only</c>), each with its effective
    /// <c>when</c>. An error if a remaining job needs one that was left out.
    /// </summary>
    public (IReadOnlyList<CiJobDefinition> Jobs, string? Error) ForBranch(string branch) => ForRef(CiRuleContext.ForBranch(branch));

    /// <summary>The jobs that run for a branch or tag (see <see cref="CiRuleContext"/>); an error if a remaining job needs one that was left out.</summary>
    public (IReadOnlyList<CiJobDefinition> Jobs, string? Error) ForRef(CiRuleContext ctx)
    {
        var kept = new List<CiJobDefinition>();
        foreach (var j in Jobs)
            if (j.WhenFor(ctx) is { } when)
                kept.Add(when == j.When ? j : j with { When = when });
        var names = kept.Select(j => j.Name).ToHashSet();
        foreach (var j in kept)
            foreach (var n in j.Needs)
                if (!names.Contains(n))
                    return ([], $"{CiFile.FileName}: job '{j.Name}' needs '{n}', which doesn't run on {ctx.Describe} (see its rules).");
        return (kept, null);
    }

    /// <summary>
    /// True if a pushed tag starts a pipeline: <c>on.tags</c> lists a pattern that matches it, or (when the file has no
    /// <c>on.tags</c>) some job has a rule about tags, so files written before tag pipelines existed never start on a tag.
    /// </summary>
    public bool RunsOnTag(string tag) => On.Tags is { } t
        ? t.Any(p => CiFile.GlobMatch(p, tag))
        : Jobs.Any(j => j.Rules.Any(r => r.Tags != null || r.If?.Mentions("CI_COMMIT_TAG") == true));
}

/// <summary>
/// What a job's <c>rules</c> are matched against: the branch or tag a pipeline runs for, and the variables <c>if:</c>
/// conditions can read (<c>CI_COMMIT_TAG</c>, <c>CI_COMMIT_BRANCH</c>, <c>CI_COMMIT_REF_NAME</c>, <c>CI_PIPELINE_SOURCE</c> and the
/// pipeline's own schedule or manual-run variables).
/// </summary>
public sealed record CiRuleContext(string RefName, bool IsTag, IReadOnlyDictionary<string, string> Variables)
{
    public string Describe => IsTag ? $"tag '{RefName}'" : $"branch '{RefName}'";

    public static CiRuleContext ForBranch(string branch) => For(branch, "push", null);

    /// <summary><paramref name="pipelineRef"/> is a branch name, or <c>refs/tags/{tag}</c> for a tag pipeline.</summary>
    public static CiRuleContext For(string pipelineRef, string source, IReadOnlyDictionary<string, string>? extra)
    {
        var tag = CiRef.IsTag(pipelineRef);
        var name = tag ? CiRef.TagName(pipelineRef) : pipelineRef;
        var vars = new Dictionary<string, string>(extra ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var (k, v) in CiRef.Predefined(pipelineRef, source)) vars[k] = v;
        return new CiRuleContext(name, tag, vars);
    }
}

/// <summary>
/// When pipelines run automatically. <see cref="Branches"/> null means every branch; patterns use <c>*</c>
/// (anything but <c>/</c>) and <c>**</c> (anything).
/// </summary>
public sealed record CiTriggers(IReadOnlyList<string>? Branches, bool PullRequests)
{
    /// <summary><c>on.tags</c>: patterns of the tags that start a pipeline; null when the file doesn't say (see <see cref="CiDefinition.RunsOnTag"/>).</summary>
    public IReadOnlyList<string>? Tags { get; init; }

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
    /// <summary>Set for a job that publishes a static site (<c>pages:</c>, <c>publish:</c>, or a job named <c>pages</c>).</summary>
    public CiPagesDefinition? Pages { get; init; }
    /// <summary><c>rules:</c> (or <c>only:</c>); empty means the job runs on every branch.</summary>
    public IReadOnlyList<CiRule> Rules { get; init; } = [];
    /// <summary><c>retry:</c> how many times a failed job runs again by itself (0 to 3).</summary>
    public int Retry { get; init; }
    /// <summary><c>timeout:</c> kills the job after this long; it can only lower the server's <c>Ci:JobTimeoutMinutes</c>.</summary>
    public TimeSpan? Timeout { get; init; }
    /// <summary><c>variables:</c> of the file and of the job (the job's win): plain environment variables, not secret.</summary>
    public IReadOnlyDictionary<string, string> Variables { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// How the job runs on <paramref name="branch"/>: null if it doesn't (no rule matched, or the first matching rule
    /// says <c>never</c>), else its <c>when</c>. The first rule whose branches match decides.
    /// </summary>
    public CiJobWhen? WhenFor(string branch) => WhenFor(CiRuleContext.ForBranch(branch));

    /// <summary>
    /// The same for a branch or tag. A rule matches when its <c>branches</c> (branch pipelines) or <c>tags</c> (tag pipelines)
    /// match, if it has any, and its <c>if</c> condition holds, if it has one.
    /// </summary>
    public CiJobWhen? WhenFor(CiRuleContext ctx)
    {
        if (Rules.Count == 0) return When;
        foreach (var r in Rules)
        {
            if (!r.Matches(ctx)) continue;
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
public sealed record CiRule(IReadOnlyList<string>? Branches, CiRuleWhen? When)
{
    /// <summary>Tag patterns: the rule matches tag pipelines whose tag matches one. Null: the rule has no tag filter.</summary>
    public IReadOnlyList<string>? Tags { get; init; }
    /// <summary><c>if:</c> condition, evaluated with the pipeline's variables.</summary>
    public CiRuleExpression? If { get; init; }

    public bool Matches(CiRuleContext ctx)
    {
        if (Branches != null || Tags != null)
        {
            var patterns = ctx.IsTag ? Tags : Branches;
            if (patterns == null || !patterns.Any(p => CiFile.GlobMatch(p, ctx.RefName))) return false;
        }
        return If == null || If.Evaluate(ctx.Variables);
    }
}

/// <summary>
/// <c>artifacts:</c>. <see cref="ExpireIn"/> null means they never expire. <see cref="Reports"/> maps a report type
/// (<see cref="CiFile.ReportTypes"/>: <c>nightowl</c>, <c>sarif</c>) to the file the job writes it to; those files are
/// always part of <see cref="Paths"/>, and ByteBench reads them after the job (Services/Security).
/// </summary>
public sealed record CiArtifactsDefinition(IReadOnlyList<string> Paths, TimeSpan? ExpireIn, CiArtifactsWhen When,
    IReadOnlyDictionary<string, string>? Reports = null);

/// <summary><c>cache:</c>. <see cref="Key"/> is a template: literal text plus <c>{{ hashFiles('a', 'b/**') }}</c>.</summary>
public sealed record CiCacheDefinition(string Key, IReadOnlyList<string> Paths);

/// <summary>
/// <c>environment:</c> the job deploys to <see cref="Name"/>. <see cref="Action"/> <c>stop</c> makes it a stop job: when it
/// passes, the environment is marked stopped.
/// </summary>
public sealed record CiEnvironmentDefinition(string Name, string? Url, string? Action = null)
{
    public bool IsStop => Action == "stop";
}

/// <summary>
/// A job that publishes a static site when it passes: the files of <see cref="Path"/> (a folder of the workspace) become the
/// repository's site. On the default branch that is the main site; with <see cref="Review"/> other branches get review sites
/// that expire after <see cref="ReviewExpireIn"/> (null: the site's setting).
/// </summary>
public sealed record CiPagesDefinition(string Path, bool Review, TimeSpan? ReviewExpireIn)
{
    public const string DefaultPath = "public";
    public const string JobName = "pages";
}

/// <summary>The result of parsing: a definition, or a readable error.</summary>
public sealed record CiParseResult(CiDefinition? Definition, string? Error)
{
    public bool Ok => Definition != null;
}

/// <summary>
/// Parser for <c>.bytebench-ci.yml</c>. Format:
/// <code>
/// image: alpine:3.20                     # default image for jobs (default alpine:3.20)
/// include: [nightowl]                    # optional built-in jobs (<see cref="CiTemplates"/>); a job of the same name in this file wins
/// on:                                    # optional; default: every push and every pull request
///   branches: [main, "feature/*"]
///   pull_requests: true
/// jobs:
///   build:
///     image: mcr.microsoft.com/dotnet/sdk:10.0   # optional per-job override
///     cache: { key: "nuget-{{ hashFiles('**/*.csproj') }}", paths: [.nuget/] }
///     artifacts: { paths: [dist/, "*.log"], expire_in: 7d, when: on_success }
///     # artifacts: { reports: { nightowl: nightowl.json, sarif: nightowl.sarif } }  # security reports (see Services/Security)
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
///     environment: { name: production, url: https://example.com }   # action: stop for a job that tears it down
///     steps: [ { run: ./deploy.sh } ]
/// </code>
/// Unknown keys are errors, so typos are caught.
/// </summary>
public static partial class CiFile
{
    public const string FileName = ".bytebench-ci.yml";
    /// <summary>The file name before the product was renamed from GitBench; still read when <see cref="FileName"/> is absent.</summary>
    public const string LegacyFileName = ".gitbench-ci.yml";

    /// <summary>The pipeline file at <paramref name="rev"/> (the current name, else the pre-rename one), or null.</summary>
    public static ByteBench.Services.Git.BlobInfo? GetBlob(string repoPath, string? rev) =>
        ByteBench.Services.Git.GitOps.GetBlob(repoPath, rev, FileName) ?? ByteBench.Services.Git.GitOps.GetBlob(repoPath, rev, LegacyFileName);
    public const string DefaultImage = "alpine:3.20";
    public const int MaxJobs = 20, MaxStepsPerJob = 50, MaxFileChars = 64 * 1024, MaxPaths = 50, MaxRules = 20, MaxRetry = 3, MaxVariables = 50, MaxVariableValueChars = 2000;
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
        public object? Include { get; set; }
        public OnDto? On { get; set; }
        public Dictionary<string, object?>? Variables { get; set; }
        public Dictionary<string, JobDto?>? Jobs { get; set; }
    }

    private sealed class OnDto
    {
        public List<string>? Branches { get; set; }
        public List<string>? Tags { get; set; }
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
        public ParallelDto? Parallel { get; set; }
        public int? Retry { get; set; }
        public string? Timeout { get; set; }
        public Dictionary<string, object?>? Variables { get; set; }
        public ArtifactsDto? Artifacts { get; set; }
        public CacheDto? Cache { get; set; }
        public EnvironmentDto? Environment { get; set; }
        /// <summary><c>pages: true</c>, <c>pages: false</c>, <c>pages: dist</c> or <c>pages: { path, review, expire_in }</c>.</summary>
        public object? Pages { get; set; }
        /// <summary>Alias: <c>publish: dist</c> is <c>pages: { path: dist }</c>.</summary>
        public string? Publish { get; set; }
    }

    private sealed class ParallelDto
    {
        public List<Dictionary<string, List<object?>>?>? Matrix { get; set; }
    }

    private sealed class StepDto
    {
        public string? Name { get; set; }
        public string? Run { get; set; }
    }

    private sealed class RuleDto
    {
        public List<string>? Branches { get; set; }
        public List<string>? Tags { get; set; }
        public string? If { get; set; }
        public string? When { get; set; }
    }

    private sealed class ArtifactsDto
    {
        public List<string>? Paths { get; set; }
        public string? ExpireIn { get; set; }
        public string? When { get; set; }
        public Dictionary<string, string?>? Reports { get; set; }
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
        public string? Action { get; set; }
    }

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    private static Dictionary<string, JobDto?> Deserialize(string yaml) =>
        Deserializer.Deserialize<Dictionary<string, JobDto?>>(yaml);

    /// <summary>Report types <c>artifacts.reports</c> accepts.</summary>
    public static readonly IReadOnlyList<string> ReportTypes = ["nightowl", "sarif", "junit", "trx", "xunit", "ctrf"];

    /// <summary>Report types that are test results (Services/Tests): these may name several files with a pattern.</summary>
    public static bool IsTestReportType(string type) => type is "junit" or "trx" or "xunit" or "ctrf";

    public static CiParseResult Parse(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return Fail("The file is empty.");
        if (yaml.Length > MaxFileChars) return Fail($"The file is larger than {MaxFileChars / 1024} KB.");

        FileDto? dto;
        try { dto = Deserializer.Deserialize<FileDto>(yaml); }
        catch (YamlException ex) { return Fail(Describe(ex)); }
        if (dto == null) return Fail("The file is empty.");

        if (CiTemplates.Expand(dto.Include, dto.Jobs ??= [], Deserialize) is { } includeError) return Fail(includeError);

        var defaultImage = dto.Image?.Trim() is { Length: > 0 } i ? i : DefaultImage;
        if (!ImagePattern().IsMatch(defaultImage)) return Fail($"'{defaultImage}' is not a valid image name.");

        if (dto.Jobs == null || dto.Jobs.Count == 0) return Fail("Define at least one job under 'jobs:'.");
        if (dto.Jobs.Count > MaxJobs) return Fail($"Too many jobs ({dto.Jobs.Count}); the limit is {MaxJobs}.");

        var (globalVars, varsError) = ParseVariables(dto.Variables, "variables");
        if (varsError != null) return Fail(varsError);

        var jobs = new List<CiJobDefinition>();
        var matrices = new Dictionary<string, List<Dictionary<string, string>>>();
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
            var (jobVars, jobVarsError) = ParseVariables(job.Variables, $"job '{name}' variables");
            if (jobVarsError != null) return Fail(jobVarsError);
            var (combos, matrixError) = ParseMatrix(job.Parallel, name!);
            if (matrixError != null) return Fail(matrixError);
            if (combos.Count > 0) matrices[name!] = combos;
            var merged = new Dictionary<string, string>(globalVars, StringComparer.Ordinal);
            foreach (var (k, v) in jobVars) merged[k] = v;
            jobs.Add(new CiJobDefinition(name!, image, needs, steps)
            {
                When = extras.When,
                AllowFailureSetting = job.AllowFailure,
                Rules = extras.Rules,
                Artifacts = extras.Artifacts,
                Cache = extras.Cache,
                Environment = extras.Environment,
                Pages = extras.Pages,
                Retry = extras.Retry,
                Timeout = extras.Timeout,
                Variables = merged,
            });
        }

        foreach (var j in jobs)
            if (j.Needs.Contains(j.Name)) return Fail($"Job '{j.Name}' can't need itself.");
        if (matrices.Count > 0)
        {
            // parallel: matrix: one job per combination (named {job}-{value}-{value}), each with the combination as variables;
            // a job that needs the matrix job needs every instance.
            var instances = new Dictionary<string, List<string>>();
            var expanded = new List<CiJobDefinition>();
            foreach (var j in jobs)
            {
                if (!matrices.TryGetValue(j.Name, out var combos)) { expanded.Add(j); continue; }
                var list = instances[j.Name] = [];
                foreach (var combo in combos)
                {
                    var instanceName = j.Name + "-" + string.Join("-", combo.Values.Select(SanitizeForName));
                    if (!JobNamePattern().IsMatch(instanceName)) return Fail($"Job '{j.Name}': the matrix instance name '{instanceName}' is too long (up to 64 characters).");
                    if (jobs.Any(o => o.Name == instanceName) || list.Contains(instanceName)) return Fail($"Job '{j.Name}': the matrix makes the name '{instanceName}' twice.");
                    var vars = new Dictionary<string, string>(j.Variables, StringComparer.Ordinal);
                    foreach (var (k, v) in combo) vars[k] = v;
                    list.Add(instanceName);
                    expanded.Add(j with { Name = instanceName, Variables = vars });
                }
            }
            jobs = expanded.Select(j => j with { Needs = j.Needs.SelectMany(n => instances.TryGetValue(n, out var inst) ? inst : [n]).Distinct().ToList() }).ToList();
            if (jobs.Count > MaxJobs) return Fail($"Too many jobs after expanding 'parallel: matrix' ({jobs.Count}); the limit is {MaxJobs}.");
        }

        var names = jobs.Select(j => j.Name).ToHashSet();
        foreach (var j in jobs)
            foreach (var need in j.Needs)
            {
                if (!names.Contains(need)) return Fail($"Job '{j.Name}' needs '{need}', which is not defined.");
            }
        if (FindCycle(jobs) is { } cycle) return Fail($"The 'needs' of these jobs form a cycle: {cycle}.");

        var branches = dto.On?.Branches?.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim()).ToList();
        var tags = dto.On?.Tags?.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim()).ToList();
        if (dto.On?.Tags != null && tags!.Count == 0) return Fail("'on.tags' needs at least one tag pattern (use \"*\" for every tag).");
        return new CiParseResult(new CiDefinition(new CiTriggers(branches, dto.On?.PullRequests ?? true) { Tags = tags }, jobs), null);
    }

    private sealed record Extras(CiJobWhen When, IReadOnlyList<CiRule> Rules, CiArtifactsDefinition? Artifacts,
        CiCacheDefinition? Cache, CiEnvironmentDefinition? Environment, string? Error, CiPagesDefinition? Pages = null,
        int Retry = 0, TimeSpan? Timeout = null);

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
                if (r == null) return Err("a rule is empty: give it 'branches', 'tags', 'if' or 'when'.");
                List<string>? branches = null, ruleTags = null;
                if (r.Branches != null)
                {
                    branches = r.Branches.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim()).ToList();
                    if (branches.Count == 0) return Err("a rule's 'branches' needs at least one pattern.");
                }
                if (r.Tags != null)
                {
                    ruleTags = r.Tags.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim()).ToList();
                    if (ruleTags.Count == 0) return Err("a rule's 'tags' needs at least one pattern.");
                }
                CiRuleExpression? condition = null;
                if (r.If != null)
                {
                    var (expr, exprError) = CiRuleExpression.Parse(r.If);
                    if (expr == null) return Err($"rule 'if: {r.If}' {exprError}.");
                    condition = expr;
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
                rules.Add(new CiRule(branches, rw) { Tags = ruleTags, If = condition });
            }
        }

        if (job.Retry is { } retry && (retry < 0 || retry > MaxRetry)) return Err($"'retry: {retry}' is not valid: use 0 to {MaxRetry}.");
        TimeSpan? timeout = null;
        if (job.Timeout != null)
        {
            if (!TryParseDuration(job.Timeout, out timeout) || timeout is not { } t || t > TimeSpan.FromDays(7))
                return Err($"'timeout: {job.Timeout}' is not valid: use e.g. 30m or 2h (at most 7d).");
        }

        CiArtifactsDefinition? artifacts = null;
        if (job.Artifacts != null)
        {
            var reports = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (type, file) in job.Artifacts.Reports ?? [])
            {
                var t = type?.Trim().ToLowerInvariant() ?? "";
                if (!ReportTypes.Contains(t)) return Err($"artifacts report type '{type}' is not supported: use {string.Join(", ", ReportTypes)}.");
                var f = file?.Trim() ?? "";
                if (f.Length == 0) return Err($"artifacts report '{t}' needs a file name.");
                if ((!IsTestReportType(t) && (f.Contains('*') || f.Contains('?'))) || f.EndsWith('/')) return Err($"artifacts report '{t}' must name one file, not a pattern.");
                if (CiArtifacts.ValidatePattern(f) is { } re) return Err($"artifacts report '{t}' path '{f}' {re}");
                reports[t] = f;
            }
            if (reports.Count > 0)
                job.Artifacts.Paths = (job.Artifacts.Paths ?? []).Concat(reports.Values.Where(r => job.Artifacts.Paths?.Contains(r) != true)).ToList();
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
            artifacts = new CiArtifactsDefinition(Clean(job.Artifacts.Paths!), expire, aw, reports.Count > 0 ? reports : null);
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
            string? action;
            switch (job.Environment.Action?.Trim())
            {
                case null or "" or "start": action = null; break;
                case "stop": action = "stop"; break;
                default: return Err($"environment action '{job.Environment.Action}' is not valid: use start or stop.");
            }
            environment = new CiEnvironmentDefinition(en, url, action);
        }

        var (pages, pagesError) = ParsePages(name, job);
        if (pagesError != null) return Err(pagesError);

        return new Extras(when, rules, artifacts, cache, environment, null, pages, job.Retry ?? 0, timeout);
    }

    /// <summary>
    /// <c>pages:</c> / <c>publish:</c> / a job named <c>pages</c>. <c>pages: false</c> turns the implicit one off.
    /// The folder must be a plain relative path (no globs); it defaults to <c>public</c>.
    /// </summary>
    private static (CiPagesDefinition? Pages, string? Error) ParsePages(string name, JobDto job)
    {
        if (job.Pages != null && job.Publish != null) return (null, "use either 'pages' or 'publish', not both.");
        string? path = null;
        var review = false;
        TimeSpan? expire = null;
        var on = name == CiPagesDefinition.JobName && job.Pages == null && job.Publish == null;
        if (job.Publish != null) { path = job.Publish; on = true; }
        else if (job.Pages is string text)
        {
            switch (text.Trim().ToLowerInvariant())
            {
                case "true": on = true; break;
                case "false" or "": on = false; break;
                default: path = text; on = true; break;
            }
        }
        else if (job.Pages is bool b) on = b;
        else if (job.Pages is IDictionary<object, object> map)
        {
            on = true;
            foreach (var (k, v) in map)
            {
                var key = k?.ToString()?.Trim() ?? "";
                var value = v?.ToString()?.Trim() ?? "";
                switch (key)
                {
                    case "path": path = value; break;
                    case "review":
                        if (!bool.TryParse(value, out review)) return (null, $"pages 'review: {value}' must be true or false.");
                        break;
                    case "expire_in":
                        if (!TryParseDuration(value, out expire) || expire == null)
                            return (null, $"pages 'expire_in: {value}' is not valid: use e.g. 12h, 3d or 2w.");
                        break;
                    default: return (null, $"pages has an unknown key '{key}': use path, review or expire_in.");
                }
            }
        }
        else if (job.Pages != null) return (null, "'pages' must be true, false, a folder name or a mapping with path, review and expire_in.");
        if (!on) return (null, null);

        var p = (path ?? CiPagesDefinition.DefaultPath).Trim().TrimEnd('/');
        if (p.Length == 0) p = CiPagesDefinition.DefaultPath;
        if (p.Contains('*') || p.Contains('?')) return (null, $"pages path '{p}' must name one folder, not a pattern.");
        if (CiArtifacts.ValidatePattern(p) is { } pe) return (null, $"pages path '{p}' {pe}");
        return (new CiPagesDefinition(p, review, expire), null);
    }

    private static string SanitizeForName(string value) =>
        new(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '_').ToArray());

    /// <summary>
    /// <c>parallel: { matrix: [ { A: [1, 2], B: [x] }, ... ] }</c>: the combinations of each entry's lists (an entry's variables
    /// in the order written); several entries add up. Empty when the job has no <c>parallel</c>.
    /// </summary>
    private static (List<Dictionary<string, string>> Combos, string? Error) ParseMatrix(ParallelDto? parallel, string job)
    {
        var result = new List<Dictionary<string, string>>();
        if (parallel == null) return (result, null);
        string Err(string m) => $"Job '{job}': {m}";
        if (parallel.Matrix == null || parallel.Matrix.Count == 0) return ([], Err("'parallel' needs 'matrix:' with at least one entry."));
        foreach (var entry in parallel.Matrix)
        {
            if (entry == null || entry.Count == 0) return ([], Err("a matrix entry needs at least one variable with a list of values."));
            IEnumerable<Dictionary<string, string>> combos = [new(StringComparer.Ordinal)];
            foreach (var (name, values) in entry)
            {
                if (ValidateVariableName(name) is { } ne) return ([], Err("matrix " + ne));
                if (values == null || values.Count == 0) return ([], Err($"matrix variable '{name}' needs at least one value."));
                var texts = new List<string>();
                foreach (var v in values)
                {
                    var text = v?.ToString() ?? "";
                    if (v is IDictionary<object, object> or IList<object>) return ([], Err($"matrix variable '{name}' must list plain values."));
                    if (ValidateVariableValue(name, text) is { } ve) return ([], Err("matrix " + ve));
                    texts.Add(text);
                }
                combos = combos.SelectMany(c => texts.Select(t => new Dictionary<string, string>(c, StringComparer.Ordinal) { [name] = t })).ToList();
                if (combos.Count() > MaxJobs) return ([], Err($"the matrix makes more than {MaxJobs} jobs."));
            }
            result.AddRange(combos);
            if (result.Count > MaxJobs) return ([], Err($"the matrix makes more than {MaxJobs} jobs."));
        }
        return (result, null);
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex VariableNamePattern();

    /// <summary>
    /// Checks a variable name for <c>variables:</c> and for manual-run variables: letters, digits and <c>_</c>, not starting
    /// with a digit, and not <c>CI</c> or a <c>BYTEBENCH_*</c> / <c>GITBENCH_*</c> name (those are set by the runner).
    /// </summary>
    public static string? ValidateVariableName(string name)
    {
        if (!VariableNamePattern().IsMatch(name)) return $"variable name '{name}' is invalid: use letters, digits and '_', not starting with a digit (up to 64 characters).";
        if (name == "CI" || name.StartsWith("BYTEBENCH_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("GITBENCH_", StringComparison.OrdinalIgnoreCase))
            return $"variable name '{name}' is reserved: ByteBench sets CI and BYTEBENCH_* itself.";
        return null;
    }

    /// <summary>A variable's value: one line, at most <see cref="MaxVariableValueChars"/> characters.</summary>
    public static string? ValidateVariableValue(string name, string value) =>
        value.Length > MaxVariableValueChars ? $"variable '{name}' is longer than {MaxVariableValueChars} characters."
        : value.IndexOfAny(['\r', '\n', '\0']) >= 0 ? $"variable '{name}' must be a single line."
        : null;

    private static (Dictionary<string, string> Values, string? Error) ParseVariables(Dictionary<string, object?>? raw, string what)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (raw == null) return (result, null);
        if (raw.Count > MaxVariables) return (result, $"{what}: too many variables (limit {MaxVariables}).");
        foreach (var (name, value) in raw)
        {
            if (ValidateVariableName(name) is { } ne) return (result, $"{what}: {ne}");
            if (value is IDictionary<object, object> or IList<object>) return (result, $"{what}: variable '{name}' must be a plain value.");
            var text = value switch { null => "", string str => str, _ => value.ToString() ?? "" };
            if (ValidateVariableValue(name, text) is { } ve) return (result, $"{what}: {ve}");
            result[name] = text;
        }
        return (result, null);
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
        msg = msg.Replace("ByteBench.Services.Ci.CiFile+", "").Replace("Dto", "");
        return $"invalid YAML at line {inner.Start.Line}, column {inner.Start.Column}: {msg}";
    }

    private static CiParseResult Fail(string error) => new(null, $"{FileName}: {error}");
}
