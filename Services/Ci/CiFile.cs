using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GitBench.Services.Ci;

/// <summary>A parsed, validated <c>.gitbench-ci.yml</c>.</summary>
public sealed record CiDefinition(CiTriggers On, IReadOnlyList<CiJobDefinition> Jobs);

/// <summary>
/// When pipelines run automatically. <see cref="Branches"/> null means every branch; patterns use <c>*</c>
/// (anything but <c>/</c>) and <c>**</c> (anything).
/// </summary>
public sealed record CiTriggers(IReadOnlyList<string>? Branches, bool PullRequests)
{
    public bool MatchesBranch(string branch) => Branches == null || Branches.Any(p => CiFile.GlobMatch(p, branch));
}

public sealed record CiJobDefinition(string Name, string Image, IReadOnlyList<string> Needs, IReadOnlyList<CiStepDefinition> Steps);
public sealed record CiStepDefinition(string Name, string Run);

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
///     steps:
///       - name: Build
///         run: dotnet build
///   test:
///     needs: [build]                     # jobs without needs run in parallel
///     steps: [ { name: Test, run: ./test.sh } ]
/// </code>
/// Unknown keys are errors, so typos are caught.
/// </summary>
public static partial class CiFile
{
    public const string FileName = ".gitbench-ci.yml";
    public const string DefaultImage = "alpine:3.20";
    public const int MaxJobs = 20, MaxStepsPerJob = 50, MaxFileChars = 64 * 1024;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex JobNamePattern();

    // Docker image reference: registry/name:tag@digest. Must not start with '-' so it can never be read as a flag.
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/:@-]{0,254}$")]
    private static partial Regex ImagePattern();

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
    }

    private sealed class StepDto
    {
        public string? Name { get; set; }
        public string? Run { get; set; }
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
            jobs.Add(new CiJobDefinition(name!, image, needs, steps));
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
