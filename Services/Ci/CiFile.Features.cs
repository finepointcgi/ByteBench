using System.Collections;
using System.Text.RegularExpressions;
using YamlDotNet.Core;

namespace ByteBench.Services.Ci;

/// <summary>A <c>services:</c> sidecar container: started before the job's steps on a private network, reachable as <see cref="Alias"/>.</summary>
public sealed record CiServiceDefinition(string Image, string Alias, IReadOnlyList<string>? Command = null,
    IReadOnlyDictionary<string, string>? Variables = null, int? Port = null);

/// <summary>Stages, <c>extends</c>, job features (services, coverage, resource_group, start_in) and the checks that go with them.</summary>
public static partial class CiFile
{
    public const int MaxStages = 10, MaxServices = 5, MaxExtendsDepth = 10, MaxIncludeDepth = 5, MaxIncludes = 20, MaxCoverageChars = 200;
    public const int MaxIncludeTotalChars = 256 * 1024;
    public static readonly string[] DefaultStages = ["build", "test", "deploy"];
    public const string DefaultStage = "test";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,31}$")]
    private static partial Regex StageNamePattern();

    [GeneratedRegex(@"^\s*(\d{1,5})\s*(s|secs?|seconds?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex StartInSeconds();

    [GeneratedRegex("^[a-z0-9][a-z0-9_.-]{0,62}$")]
    private static partial Regex ServiceAliasPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 _./-]{0,63}$")]
    private static partial Regex ResourceGroupPattern();

    // ----------------------------------------------------------------- extends

    /// <summary>
    /// Resolves <c>extends:</c> (one name or a list; later parents win, the job's own keys win over all): hashes are merged key by
    /// key (<c>variables</c>, <c>artifacts</c>, <c>cache</c>, <c>environment</c>), everything else (steps, needs, rules...) is
    /// replaced. Jobs whose name starts with <c>.</c> are hidden templates: they can be extended but never run. Returns an error or null.
    /// </summary>
    private static string? ResolveExtends(FileDto dto)
    {
        var jobs = dto.Jobs!;
        var resolved = new Dictionary<string, JobDto?>();
        var stack = new List<string>();

        (JobDto? Job, string? Error) Resolve(string name)
        {
            if (resolved.TryGetValue(name, out var done)) return (done, null);
            var job = jobs[name];
            if (job?.Extends == null) { resolved[name] = job; return (job, null); }
            if (stack.Contains(name)) return (null, $"'extends' forms a cycle: {string.Join(" → ", stack.SkipWhile(s => s != name).Append(name))}.");
            if (stack.Count >= MaxExtendsDepth) return (null, $"Job '{name}': 'extends' goes more than {MaxExtendsDepth} levels deep.");
            var parents = job.Extends switch
            {
                string s => [s],
                IEnumerable<object> list => list.Select(o => o as string).ToList(),
                _ => null,
            };
            if (parents == null || parents.Count == 0 || parents.Any(string.IsNullOrWhiteSpace))
                return (null, $"Job '{name}': 'extends' must be a job name or a list of job names (e.g. extends: .base).");
            stack.Add(name);
            JobDto? merged = null;
            foreach (var raw in parents)
            {
                var parent = raw!.Trim();
                if (!jobs.ContainsKey(parent)) return (null, $"Job '{name}' extends '{parent}', which is not defined.");
                var (pj, error) = Resolve(parent);
                if (error != null) return (null, error);
                merged = (JobDto?)MergeDto(merged, pj);
            }
            merged = (JobDto?)MergeDto(merged, job) ?? new JobDto();
            merged.Extends = null;
            stack.RemoveAt(stack.Count - 1);
            resolved[name] = merged;
            return (merged, null);
        }

        var result = new Dictionary<string, JobDto?>();
        foreach (var name in jobs.Keys)
        {
            var (job, error) = Resolve(name);
            if (error != null) return error;
            if (!IsHidden(name)) result[name] = job;
        }
        dto.Jobs = result;
        return null;
    }

    private static bool IsHidden(string? name) => name != null && name.StartsWith('.');

    /// <summary>
    /// Overlays <paramref name="child"/> on <paramref name="parent"/>: dictionaries and the file's option objects merge key by key,
    /// everything else (lists, scalars) is replaced by the child's value. Neither argument is changed.
    /// </summary>
    private static object? MergeDto(object? parent, object? child)
    {
        if (child == null) return parent;
        if (parent == null) return child;
        var type = child.GetType();
        if (child is IDictionary cd && parent is IDictionary pd && type == parent.GetType())
        {
            var merged = (IDictionary)Activator.CreateInstance(type)!;
            foreach (DictionaryEntry e in pd) merged[e.Key] = e.Value;
            foreach (DictionaryEntry e in cd) merged[e.Key] = e.Value;
            return merged;
        }
        if (type.IsNestedPrivate && type.Name.EndsWith("Dto", StringComparison.Ordinal) && type == parent.GetType())
        {
            var result = Activator.CreateInstance(type)!;
            foreach (var prop in type.GetProperties().Where(p => p is { CanRead: true, CanWrite: true }))
                prop.SetValue(result, MergeDto(prop.GetValue(parent), prop.GetValue(child)));
            return result;
        }
        return child;
    }

    // ----------------------------------------------------------------- stages

    /// <summary>
    /// <c>stages:</c>. The file uses stages when it lists them or any job has a <c>stage:</c>; the default list is
    /// build, test, deploy, and a job without <c>stage:</c> is in <c>test</c>. Files without either behave as before.
    /// </summary>
    private static (IReadOnlyList<string>? Stages, string? Error) ParseStages(FileDto dto)
    {
        var uses = dto.Stages != null || dto.Jobs!.Values.Any(j => j?.Stage != null);
        if (!uses) return (null, null);
        if (dto.Stages == null) return (DefaultStages, null);
        var list = dto.Stages.Select(s => s?.Trim() ?? "").ToList();
        if (list.Count == 0) return (null, "'stages' needs at least one stage name.");
        if (list.Count > MaxStages) return (null, $"Too many stages ({list.Count}); the limit is {MaxStages}.");
        foreach (var s in list)
            if (!StageNamePattern().IsMatch(s)) return (null, $"Stage name '{s}' is invalid: use letters, digits, '_', '.' or '-' (up to 32 characters).");
        if (list.Distinct().Count() != list.Count) return (null, "'stages' lists the same stage twice.");
        return (list, null);
    }

    /// <summary>
    /// Gives each job that has no <c>needs</c> key the jobs of the nearest earlier stage as needs, so a stage starts when the stage
    /// before it has finished successfully. Optional manual jobs (<c>when: manual</c> with <c>allow_failure</c>) never hold the next
    /// stage up. Jobs that write <c>needs</c> (even <c>[]</c>) keep exactly what they wrote.
    /// </summary>
    internal static List<CiJobDefinition> ApplyStageNeeds(IReadOnlyList<CiJobDefinition> jobs, IReadOnlyList<string>? stages)
    {
        if (stages == null) return jobs.ToList();
        var result = new List<CiJobDefinition>(jobs.Count);
        foreach (var j in jobs)
        {
            var index = j.Stage == null ? -1 : stages.ToList().IndexOf(j.Stage);
            if (!j.ImplicitNeeds || index <= 0) { result.Add(j); continue; }
            var needs = new List<string>();
            for (var s = index - 1; s >= 0 && needs.Count == 0; s--)
                needs = jobs.Where(o => o.Stage == stages[s] && !(o.When == CiJobWhen.Manual && o.AllowFailure)).Select(o => o.Name).ToList();
            result.Add(j with { Needs = needs });
        }
        return result;
    }

    // ----------------------------------------------------------------- job features

    private sealed record Features(string? Stage, IReadOnlyList<CiServiceDefinition> Services, string? Coverage, string? ResourceGroup,
        TimeSpan? StartIn, string? Error);

    private static Features ParseFeatures(string name, JobDto job, IReadOnlyList<string>? stages, CiJobWhen when, IReadOnlyList<CiRule> rules)
    {
        Features Err(string e) => new(null, [], null, null, null, $"Job '{name}': {e}");

        string? stage = null;
        if (stages != null)
        {
            stage = job.Stage?.Trim() is { Length: > 0 } st ? st : DefaultStage;
            if (!stages.Contains(stage))
                return Err(job.Stage == null
                    ? $"it has no 'stage:' and the default stage '{DefaultStage}' is not in 'stages' ({string.Join(", ", stages)}); give it a stage."
                    : $"stage '{stage}' is not in 'stages' ({string.Join(", ", stages)}).");
        }

        var (services, serviceError) = ParseServices(job.Services);
        if (serviceError != null) return Err(serviceError);

        string? coverage = null;
        if (job.Coverage != null)
        {
            var (pattern, coverageError) = CiCoverage.NormalizePattern(job.Coverage);
            if (coverageError != null) return Err(coverageError);
            coverage = pattern;
        }

        string? group = null;
        if (job.ResourceGroup != null)
        {
            group = job.ResourceGroup.Trim();
            if (!ResourceGroupPattern().IsMatch(group)) return Err($"resource_group '{group}' is invalid: use letters, digits, spaces, '_', '.', '/' or '-' (up to 64 characters).");
        }

        TimeSpan? startIn = null;
        var delayed = when == CiJobWhen.Delayed || rules.Any(r => r.When == CiRuleWhen.Delayed);
        if (job.StartIn != null)
        {
            if (!delayed) return Err("'start_in' only applies to 'when: delayed'.");
            var seconds = StartInSeconds().Match(job.StartIn);
            if (seconds.Success && int.TryParse(seconds.Groups[1].Value, out var n)) startIn = TimeSpan.FromSeconds(n);
            else if (!TryParseDuration(job.StartIn, out startIn)) startIn = null;
            if (startIn is not { } d || d <= TimeSpan.Zero || d > TimeSpan.FromDays(1))
                return Err($"'start_in: {job.StartIn}' is not valid: use e.g. 30s, 30m or 2h (up to 24h).");
        }
        else if (delayed) return Err("'when: delayed' needs 'start_in', e.g. start_in: 10m.");

        return new Features(stage, services, coverage, group, startIn, null);
    }

    /// <summary><c>services:</c> entries are an image name or <c>{ image, alias, command, variables, port }</c>.</summary>
    private static (IReadOnlyList<CiServiceDefinition> Services, string? Error) ParseServices(List<object?>? raw)
    {
        if (raw == null) return ([], null);
        if (raw.Count > MaxServices) return ([], $"too many services (limit {MaxServices}).");
        var list = new List<CiServiceDefinition>();
        foreach (var entry in raw)
        {
            string? image = null, alias = null;
            List<string>? command = null;
            Dictionary<string, string>? variables = null;
            int? port = null;
            switch (entry)
            {
                case string text: image = text.Trim(); break;
                case IDictionary<object, object> map:
                    foreach (var (k, v) in map)
                    {
                        var key = k?.ToString()?.Trim() ?? "";
                        switch (key)
                        {
                            case "image" or "name": image = v?.ToString()?.Trim(); break;
                            case "alias": alias = v?.ToString()?.Trim(); break;
                            case "command":
                                if (v is string one) command = [one];
                                else if (v is IEnumerable<object> items) command = items.Select(i => i?.ToString() ?? "").ToList();
                                else return ([], "a service's 'command' must be a list of arguments.");
                                break;
                            case "variables":
                                if (v is not IDictionary<object, object> vars) return ([], "a service's 'variables' must be a mapping.");
                                variables = new(StringComparer.Ordinal);
                                if (vars.Count > MaxVariables) return ([], $"a service has too many variables (limit {MaxVariables}).");
                                foreach (var (vk, vv) in vars)
                                {
                                    var vn = vk?.ToString() ?? "";
                                    if (ValidateVariableName(vn) is { } ne) return ([], "service " + ne);
                                    if (vv is IDictionary<object, object> or IList<object>) return ([], $"service variable '{vn}' must be a plain value.");
                                    var text = vv?.ToString() ?? "";
                                    if (ValidateVariableValue(vn, text) is { } ve) return ([], "service " + ve);
                                    variables[vn] = text;
                                }
                                break;
                            case "port":
                                if (!int.TryParse(v?.ToString(), out var p) || p is < 1 or > 65535) return ([], $"a service's 'port' must be a number from 1 to 65535, not '{v}'.");
                                port = p;
                                break;
                            default: return ([], $"a service has an unknown key '{key}': use image, alias, command, variables or port.");
                        }
                    }
                    break;
                default: return ([], "each service must be an image name or a mapping with 'image'.");
            }
            if (string.IsNullOrEmpty(image) || !ImagePattern().IsMatch(image)) return ([], $"service image '{image}' is not a valid image name.");
            if (command is { Count: > 50 } || command?.Any(c => c.Length > 1000 || c.Contains('\0')) == true) return ([], "a service's 'command' is too long.");
            alias = string.IsNullOrEmpty(alias) ? DefaultAlias(image) : alias.ToLowerInvariant();
            if (!ServiceAliasPattern().IsMatch(alias)) return ([], $"service alias '{alias}' is invalid: use lowercase letters, digits, '_', '.' or '-' (up to 63 characters).");
            if (list.Any(s => s.Alias == alias)) return ([], $"two services have the alias '{alias}'; give one an 'alias:'.");
            list.Add(new CiServiceDefinition(image, alias, command, variables, port));
        }
        return (list, null);
    }

    /// <summary><c>registry/ns/postgres:17</c> becomes <c>postgres</c>.</summary>
    public static string DefaultAlias(string image)
    {
        var name = image.Split('@')[0];
        name = name[(name.LastIndexOf('/') + 1)..];
        var colon = name.IndexOf(':');
        if (colon >= 0) name = name[..colon];
        var clean = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '-').ToArray());
        return clean.Length == 0 ? "service" : clean;
    }

    /// <summary><c>environment.on_stop</c> must name a job that stops the same environment.</summary>
    private static string? ValidateOnStop(List<CiJobDefinition> jobs)
    {
        foreach (var j in jobs.Where(j => j.Environment?.OnStop != null))
        {
            var target = jobs.FirstOrDefault(o => o.Name == j.Environment!.OnStop);
            if (target == null) return $"Job '{j.Name}': environment on_stop '{j.Environment!.OnStop}' is not a job in this file.";
            if (target.Environment is not { IsStop: true } || target.Environment.Name != j.Environment!.Name)
                return $"Job '{j.Name}': on_stop job '{target.Name}' must have 'environment: {{ name: {j.Environment!.Name}, action: stop }}'.";
        }
        return null;
    }

    // ----------------------------------------------------------------- include

    private sealed record IncludeEntry(string Kind, string Value);

    private sealed class IncludeState(ICiIncludeProvider? provider)
    {
        public ICiIncludeProvider? Provider { get; } = provider;
        public HashSet<string> Seen { get; } = [];
        public int Count, TotalChars;
    }

    /// <summary>
    /// <c>include:</c> merges other files' jobs, variables and stages into this one: a built-in template name, a repository file
    /// (<c>local: path</c>) or a remote URL (<c>remote:</c>, off unless the admin allows it). Entries are read in order (a later
    /// one replaces a job of the same name from an earlier one) and the file's own jobs win over everything it includes. Included
    /// files can include more (at most <see cref="MaxIncludeDepth"/> levels, <see cref="MaxIncludes"/> files, a cycle is an error).
    /// </summary>
    private static string? ResolveIncludes(FileDto dto, ICiIncludeProvider? provider) =>
        ProcessIncludes(dto, new IncludeState(provider), 0, []);

    private static string? ProcessIncludes(FileDto file, IncludeState state, int depth, List<string> stack)
    {
        if (file.Include == null) return null;
        var (entries, parseError) = ParseIncludeEntries(file.Include);
        if (parseError != null) return parseError;
        if (entries.Count == 0) return null;
        if (depth >= MaxIncludeDepth) return $"'include' goes more than {MaxIncludeDepth} levels deep.";

        var gatheredJobs = new Dictionary<string, JobDto?>();
        var gatheredVars = new Dictionary<string, object?>();
        List<string>? gatheredStages = null;
        foreach (var entry in entries)
        {
            var key = entry.Kind + ":" + (entry.Kind == "template" ? entry.Value.ToLowerInvariant() : entry.Value);
            var label = entry.Kind == "template" ? $"template '{entry.Value}'" : $"{entry.Kind} '{entry.Value}'";
            if (stack.Contains(key)) return $"'include' forms a cycle: {string.Join(" → ", stack.SkipWhile(s => s != key).Append(key).Select(s => s[(s.IndexOf(':') + 1)..]))}.";
            if (!state.Seen.Add(key)) continue; // already included (a file two includes share)
            if (++state.Count > MaxIncludes) return $"Too many included files (limit {MaxIncludes}).";

            string? text, readError = null;
            switch (entry.Kind)
            {
                case "template":
                    text = CiTemplates.Snippet(entry.Value);
                    if (text == null) return $"'include: {entry.Value}' is not a built-in template. Available: {string.Join(", ", CiTemplates.Names)}.";
                    text = "jobs:\n" + string.Join("\n", text.Split('\n').Select(l => "  " + l));
                    break;
                case "local" or "remote":
                    if (state.Provider == null) return $"include of {label}: files can't be included here.";
                    var read = entry.Kind == "local" ? state.Provider.ReadLocal(entry.Value) : state.Provider.ReadRemote(entry.Value);
                    (text, readError) = (read.Text, read.Error);
                    break;
                default: return $"Unknown include kind '{entry.Kind}'.";
            }
            if (text == null) return $"include of {label}: {readError ?? "could not be read"}.";
            if (text.Length > MaxFileChars) return $"include of {label}: the file is larger than {MaxFileChars / 1024} KB.";
            state.TotalChars += text.Length;
            if (state.TotalChars > MaxIncludeTotalChars) return $"The included files are larger than {MaxIncludeTotalChars / 1024} KB together.";

            FileDto? child;
            try { child = DeserializeYaml<FileDto>(text); }
            catch (YamlException ex) { return $"include of {label}: {Describe(ex)}"; }
            if (child == null) return $"include of {label}: the file is empty.";
            child.Jobs ??= [];

            stack.Add(key);
            var error = ProcessIncludes(child, state, depth + 1, stack);
            stack.RemoveAt(stack.Count - 1);
            if (error != null) return error;

            foreach (var (name, job) in child.Jobs)
            {
                if (job != null && job.Image == null && child.Image != null) job.Image = child.Image; // the included file's default image
                gatheredJobs[name] = job;
            }
            foreach (var (k, v) in child.Variables ?? []) gatheredVars[k] = v;
            gatheredStages ??= child.Stages;
        }

        file.Jobs ??= [];
        foreach (var (name, job) in gatheredJobs) file.Jobs.TryAdd(name, job); // the file's own job of that name wins
        if (gatheredVars.Count > 0)
        {
            file.Variables ??= [];
            foreach (var (k, v) in gatheredVars) file.Variables.TryAdd(k, v);
        }
        file.Stages ??= gatheredStages;
        return null;
    }

    private static (List<IncludeEntry> Entries, string? Error) ParseIncludeEntries(object include)
    {
        const string Hint = "'include' must be a template name, a list of them, or entries like { local: path/to/file.yml }, { template: nightowl } or { remote: https://... }.";
        var entries = new List<IncludeEntry>();
        var items = include switch { string s => [s], IEnumerable<object> list => list.ToList(), _ => null };
        if (items == null) return ([], Hint);
        foreach (var item in items)
        {
            switch (item)
            {
                case string text when !string.IsNullOrWhiteSpace(text):
                    var t = text.Trim();
                    if (CiTemplates.Snippet(t) != null || !(t.Contains('/') || t.Contains('.'))) entries.Add(new("template", t));
                    else if (t.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) entries.Add(new("remote", t));
                    else entries.Add(new("local", t));
                    break;
                case IDictionary<object, object> map when map.Count == 1:
                    var (k, v) = map.First();
                    var kind = k?.ToString()?.Trim();
                    var value = v?.ToString()?.Trim();
                    if (string.IsNullOrEmpty(value)) return ([], Hint);
                    switch (kind)
                    {
                        case "local" or "remote" or "template": entries.Add(new(kind, value)); break;
                        default: return ([], $"'include' has an unknown key '{kind}': use local, template or remote.");
                    }
                    break;
                default: return ([], Hint);
            }
        }
        return (entries, null);
    }
}
