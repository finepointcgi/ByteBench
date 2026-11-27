using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace ByteBench.Services.Workspaces;

/// <summary>A named command from <c>tasks:</c>: run inside the workspace container from the repository directory.</summary>
public sealed record WorkspaceTaskDefinition(string Name, string Command, int? Port, bool Preview);

/// <summary>A <c>services:</c> sidecar: a container on a private per-workspace network, reachable from the workspace by <see cref="Name"/>.</summary>
public sealed record WorkspaceSidecar(string Name, string Image, IReadOnlyDictionary<string, string> Env, IReadOnlyList<int> Ports);

/// <summary>
/// What a repository asks of its workspace: toolchain or image, setup commands, tasks, ports, plain environment variables, submodules and
/// sidecar services. Parsed from <c>.bytebench/workspace.yml</c> (or the useful subset of a devcontainer.json) by <see cref="WorkspaceEnvFile"/>.
/// Everything in it is validated before use; the commands are never interpreted by the host, they only run inside the container.
/// </summary>
public sealed record WorkspaceEnvironment(string Source, string? Toolchain, string? Image, IReadOnlyList<string> Setup,
    IReadOnlyList<WorkspaceTaskDefinition> Tasks, IReadOnlyList<int> Ports, IReadOnlyDictionary<string, string> Env, bool Submodules,
    IReadOnlyList<WorkspaceSidecar> Services, IReadOnlyList<string> Notes)
{
    public static readonly WorkspaceEnvironment Empty = new("", null, null, [], [], [], new Dictionary<string, string>(), true, [], []);

    public bool IsEmpty => Source == "";

    /// <summary>Changes whenever the setup block (or what it runs on) changes; the setup commands re-run only then.</summary>
    public string SetupHash
    {
        get
        {
            if (Setup.Count == 0) return "";
            var sb = new StringBuilder();
            sb.Append("v1\n").Append(Toolchain).Append('\n').Append(Image).Append('\n');
            foreach (var s in Setup) sb.Append(s.Length).Append(':').Append(s).Append('\n');
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..32].ToLowerInvariant();
        }
    }

    /// <summary>All ports the page offers: tasks' ports, <c>ports:</c> (deduplicated, in order).</summary>
    public IReadOnlyList<int> AllPorts => Tasks.Where(t => t.Port != null).Select(t => t.Port!.Value).Concat(Ports).Distinct().ToList();
}

/// <summary>The outcome of reading an environment file: the environment, or the reasons it was refused (one message per problem).</summary>
public sealed record WorkspaceEnvResult(WorkspaceEnvironment? Environment, string Source, IReadOnlyList<string> Errors, IReadOnlyList<string> Notes)
{
    public bool Ok => Environment != null && Errors.Count == 0;
    public static WorkspaceEnvResult Failure(string source, params string[] errors) => new(null, source, errors, []);
}

/// <summary>A toolchain from the curated list and the public base image it runs on.</summary>
public sealed record WorkspaceToolchain(string Id, string Display, string Version, string BaseImage, string Alias);

public static class WorkspaceToolchains
{
    /// <summary>The curated list. The base images are pinned here; nothing a repository says ever reaches a Docker argument except through this table.</summary>
    public static readonly IReadOnlyList<WorkspaceToolchain> All =
    [
        new("dotnet", ".NET SDK", "10", "mcr.microsoft.com/dotnet/sdk:10.0", "dotnet10"),
        new("node", "Node.js", "22", "node:22-bookworm", "node22"),
        new("python", "Python", "3.13", "python:3.13-bookworm", "python313"),
        new("go", "Go", "1.24", "golang:1.24-bookworm", "go124"),
        new("java", "Java (JDK + Maven)", "21", "maven:3.9-eclipse-temurin-21", "java21"),
        new("rust", "Rust", "stable", "rust:1-bookworm", "rust"),
    ];

    /// <summary>Resolves <c>node</c>, <c>node@22</c>, <c>dotnet@10</c>, <c>python@3.13</c>, …; null when it isn't in the list (with <paramref name="error"/> saying what is).</summary>
    public static WorkspaceToolchain? Resolve(string? spec, out string? error)
    {
        error = null;
        var s = (spec ?? "").Trim().ToLowerInvariant();
        var at = s.IndexOf('@');
        var id = at < 0 ? s : s[..at];
        var version = at < 0 ? "" : s[(at + 1)..];
        id = id switch { "nodejs" => "node", "csharp" or ".net" or "dotnet-sdk" => "dotnet", "golang" => "go", "jdk" or "maven" => "java", _ => id };
        var found = All.FirstOrDefault(t => t.Id == id);
        if (found == null)
        {
            error = $"'{spec}' is not a toolchain ByteBench knows. Use one of: {string.Join(", ", All.Select(t => $"{t.Id}@{t.Version}"))}.";
            return null;
        }
        if (version.Length > 0 && version != found.Version && !(found.Id == "rust" && version == "1") && !(found.Id == "go" && version == "1.24"))
        {
            error = $"toolchain '{spec}': only {found.Id}@{found.Version} is available (versions are pinned by the server).";
            return null;
        }
        return found;
    }
}

/// <summary>Strict reader for <c>.bytebench/workspace.yml</c> and (a subset of) devcontainer.json.</summary>
public static partial class WorkspaceEnvFile
{
    public const string Path = ".bytebench/workspace.yml";
    public const int MaxFileBytes = 64 * 1024;
    public const int MaxSetup = 20, MaxTasks = 20, MaxPorts = 20, MaxEnv = 50, MaxServices = 5, MaxCommandChars = 4000, MaxEnvValueChars = 2000;

    /// <summary>Where a repository can put its environment, in the order they are tried.</summary>
    public static readonly string[] Candidates = [Path, ".devcontainer/devcontainer.json", ".devcontainer.json", "devcontainer.json"];

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,99}$")] private static partial Regex EnvNamePattern();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,39}$")] private static partial Regex TaskNamePattern();
    [GeneratedRegex("^[a-z][a-z0-9-]{0,30}$")] private static partial Regex ServiceNamePattern();
    // registry[:port]/path:tag@digest. Must start with a letter or digit so it can never be read as a flag.
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*(:[0-9]+)?(/[a-z0-9][a-z0-9._-]*)*(:[A-Za-z0-9_][A-Za-z0-9_.-]{0,127})?(@sha256:[a-f0-9]{64})?$")] private static partial Regex ImagePattern();

    private static readonly HashSet<string> ReservedEnv = new(StringComparer.Ordinal)
    {
        "PATH", "HOME", "USER", "LOGNAME", "SHELL", "BASH_ENV", "ENV", "IFS", "PS1", "PS4", "SHELLOPTS", "BASHOPTS", "PROMPT_COMMAND", "TMPDIR", "HOSTNAME", "PWD", "OLDPWD",
    };

    private static readonly string[] ReservedEnvPrefixes = ["BB_", "LD_", "GIT_", "DOCKER_", "BASH_FUNC_"];

    /// <summary>Null if <paramref name="name"/> may be set from a repository file (or a secret), else why not.</summary>
    public static string? ValidateEnvName(string name)
    {
        if (!EnvNamePattern().IsMatch(name)) return $"'{name}' is not a valid variable name (letters, digits and underscores, not starting with a digit, up to 100 characters).";
        if (ReservedEnv.Contains(name) || ReservedEnvPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
            return $"'{name}' is reserved by the workspace (PATH, HOME, BB_*, GIT_*, LD_*, DOCKER_* and a few shell variables can't be set).";
        return null;
    }

    public static bool IsValidImage(string? image) => !string.IsNullOrEmpty(image) && image.Length <= 200 && ImagePattern().IsMatch(image);

    /// <summary>Null if valid. Ports below 1024 are privileged (the workspace runs unprivileged) and the IDE's own port is taken.</summary>
    public static string? ValidatePort(object? value, string where, bool allowPrivileged = false)
    {
        if (!int.TryParse(value?.ToString(), out var p) || p < (allowPrivileged ? 1 : 1024) || p > 65535)
            return $"{where}: '{value}' is not a port from {(allowPrivileged ? 1 : 1024)} to 65535.";
        if (p == WorkspaceLifecycle.IdePort) return $"{where}: port {p} is used by the IDE itself.";
        return null;
    }

    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    /// <summary>Parses <c>.bytebench/workspace.yml</c>. Every problem is reported (not only the first).</summary>
    public static WorkspaceEnvResult ParseYaml(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return WorkspaceEnvResult.Failure(Path, $"{Path} is empty.");
        if (Encoding.UTF8.GetByteCount(text) > MaxFileBytes) return WorkspaceEnvResult.Failure(Path, $"{Path} is larger than {MaxFileBytes / 1024} KB.");
        object? root;
        try { root = Yaml.Deserialize<object?>(new MergingParser(new Parser(new StringReader(text)))); }
        catch (YamlException ex)
        {
            var inner = ex.InnerException as YamlException ?? ex;
            return WorkspaceEnvResult.Failure(Path, $"{Path}: invalid YAML at line {inner.Start.Line}, column {inner.Start.Column}: {inner.Message.Split("): ").Last()}");
        }
        if (root is not IDictionary<object, object> map) return WorkspaceEnvResult.Failure(Path, $"{Path} must be a mapping (toolchain, setup, tasks, …).");

        var errors = new List<string>();
        var notes = new List<string>();
        string? toolchain = null, image = null;
        var setup = new List<string>();
        var tasks = new List<WorkspaceTaskDefinition>();
        var ports = new List<int>();
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        var submodules = true;
        var services = new List<WorkspaceSidecar>();

        foreach (var (rawKey, value) in map)
        {
            var key = rawKey?.ToString() ?? "";
            switch (key)
            {
                case "toolchain":
                    var spec = value?.ToString();
                    if (WorkspaceToolchains.Resolve(spec, out var tcError) is { } tc) toolchain = $"{tc.Id}@{tc.Version}";
                    else errors.Add("toolchain: " + tcError);
                    break;
                case "image":
                    image = value?.ToString()?.Trim();
                    if (!IsValidImage(image)) { errors.Add($"image: '{value}' is not a valid image name (lowercase, like node:22-bookworm or ghcr.io/org/dev:1.2)."); image = null; }
                    break;
                case "setup":
                    ParseSetup(value, setup, errors);
                    break;
                case "tasks":
                    ParseTasks(value, tasks, errors);
                    break;
                case "ports":
                    if (value is not IList<object> list) { errors.Add("ports: must be a list of port numbers."); break; }
                    if (list.Count > MaxPorts) errors.Add($"ports: too many ports (limit {MaxPorts}).");
                    foreach (var item in list.Take(MaxPorts))
                    {
                        if (ValidatePort(item, "ports") is { } pe) errors.Add(pe);
                        else ports.Add(int.Parse(item.ToString()!));
                    }
                    break;
                case "env":
                    ParseEnv(value, env, errors, "env");
                    break;
                case "submodules":
                    if (value is bool b) submodules = b;
                    else if (bool.TryParse(value?.ToString(), out var b2)) submodules = b2;
                    else errors.Add("submodules: must be true or false.");
                    break;
                case "services":
                    ParseServices(value, services, errors);
                    break;
                default:
                    errors.Add($"Unknown key '{key}'. Use toolchain, image, setup, tasks, ports, env, submodules or services.");
                    break;
            }
        }
        if (toolchain != null && image != null) errors.Add("Use either 'toolchain' or 'image', not both.");
        var seenPorts = new HashSet<int>();
        ports = ports.Where(p => seenPorts.Add(p)).ToList();
        if (errors.Count > 0) return new WorkspaceEnvResult(null, Path, errors, notes);
        return new WorkspaceEnvResult(new WorkspaceEnvironment(Path, toolchain, image, setup, tasks, ports, env, submodules, services, notes), Path, [], notes);
    }

    private static void ParseSetup(object? value, List<string> setup, List<string> errors)
    {
        var items = value switch { string s => [s], IList<object> l => l.Select(x => x?.ToString() ?? "").ToList(), _ => null };
        if (items == null) { errors.Add("setup: must be a command or a list of commands."); return; }
        if (items.Count > MaxSetup) errors.Add($"setup: too many commands (limit {MaxSetup}).");
        for (var i = 0; i < Math.Min(items.Count, MaxSetup); i++)
        {
            var c = items[i].Trim();
            if (c.Length == 0) { errors.Add($"setup[{i + 1}]: the command is empty."); continue; }
            if (c.Length > MaxCommandChars || c.Contains('\0')) { errors.Add($"setup[{i + 1}]: the command is too long (limit {MaxCommandChars} characters) or contains a NUL."); continue; }
            setup.Add(c);
        }
    }

    private static void ParseTasks(object? value, List<WorkspaceTaskDefinition> tasks, List<string> errors)
    {
        if (value is not IDictionary<object, object> map) { errors.Add("tasks: must be a mapping of task name to a command, or to { command, port, preview }."); return; }
        if (map.Count > MaxTasks) errors.Add($"tasks: too many tasks (limit {MaxTasks}).");
        foreach (var (rawName, def) in map.Take(MaxTasks))
        {
            var name = rawName?.ToString() ?? "";
            if (!TaskNamePattern().IsMatch(name)) { errors.Add($"tasks: '{name}' is not a valid task name (letters, digits, '_', '.', '-'; up to 40 characters)."); continue; }
            string? command = null;
            int? port = null;
            var preview = false;
            if (def is string s) command = s;
            else if (def is IDictionary<object, object> d)
            {
                foreach (var (k, v) in d)
                {
                    switch (k?.ToString())
                    {
                        case "command": command = v?.ToString(); break;
                        case "port":
                            if (ValidatePort(v, $"tasks.{name}.port") is { } pe) errors.Add(pe); else port = int.Parse(v!.ToString()!);
                            break;
                        case "preview":
                            if (v is bool pb) preview = pb;
                            else if (bool.TryParse(v?.ToString(), out var pb2)) preview = pb2;
                            else errors.Add($"tasks.{name}.preview: must be true or false.");
                            break;
                        default: errors.Add($"tasks.{name}: unknown key '{k}' (use command, port or preview)."); break;
                    }
                }
            }
            else { errors.Add($"tasks.{name}: must be a command or a mapping with 'command'."); continue; }
            command = command?.Trim();
            if (string.IsNullOrEmpty(command)) { errors.Add($"tasks.{name}: 'command' is required."); continue; }
            if (command.Length > MaxCommandChars || command.Contains('\0')) { errors.Add($"tasks.{name}: the command is too long (limit {MaxCommandChars} characters) or contains a NUL."); continue; }
            if (preview && port == null) { errors.Add($"tasks.{name}: 'preview: true' needs a 'port'."); continue; }
            tasks.Add(new WorkspaceTaskDefinition(name, command, port, preview));
        }
        if (tasks.Count(t => t.Preview) > 1) errors.Add("tasks: only one task can have 'preview: true'.");
    }

    private static void ParseEnv(object? value, Dictionary<string, string> env, List<string> errors, string where)
    {
        if (value is not IDictionary<object, object> map) { errors.Add($"{where}: must be a mapping of NAME: value (secrets don't belong here; use workspace secrets)."); return; }
        if (map.Count > MaxEnv) errors.Add($"{where}: too many variables (limit {MaxEnv}).");
        foreach (var (k, v) in map.Take(MaxEnv))
        {
            var name = k?.ToString() ?? "";
            if (ValidateEnvName(name) is { } ne) { errors.Add($"{where}: {ne}"); continue; }
            if (v is IDictionary<object, object> or IList<object>) { errors.Add($"{where}.{name}: must be a plain value."); continue; }
            var text = v switch { bool b => b ? "true" : "false", null => "", _ => v.ToString() ?? "" };
            if (text.Length > MaxEnvValueChars || text.Contains('\0') || text.Contains('\n') || text.Contains('\r'))
            { errors.Add($"{where}.{name}: the value must be a single line of at most {MaxEnvValueChars} characters."); continue; }
            env[name] = text;
        }
    }

    private static void ParseServices(object? value, List<WorkspaceSidecar> services, List<string> errors)
    {
        if (value is not IDictionary<object, object> map) { errors.Add("services: must be a mapping of service name to { image, env, ports }."); return; }
        if (map.Count > MaxServices) errors.Add($"services: too many services (limit {MaxServices}).");
        foreach (var (rawName, def) in map.Take(MaxServices))
        {
            var name = rawName?.ToString() ?? "";
            if (!ServiceNamePattern().IsMatch(name)) { errors.Add($"services: '{name}' is not a valid service name (lowercase letters, digits and '-', starting with a letter; up to 31 characters). It becomes the host name."); continue; }
            string? image = null;
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            var ports = new List<int>();
            if (def is string s) image = s.Trim();
            else if (def is IDictionary<object, object> d)
            {
                foreach (var (k, v) in d)
                {
                    switch (k?.ToString())
                    {
                        case "image": image = v?.ToString()?.Trim(); break;
                        case "env":
                        case "variables":
                            ParseEnvLoose(v, env, errors, $"services.{name}.env");
                            break;
                        case "ports":
                            if (v is not IList<object> pl) { errors.Add($"services.{name}.ports: must be a list."); break; }
                            foreach (var item in pl.Take(MaxPorts))
                            {
                                if (ValidatePort(item, $"services.{name}.ports", allowPrivileged: true) is { } pe) errors.Add(pe);
                                else ports.Add(int.Parse(item.ToString()!));
                            }
                            break;
                        default: errors.Add($"services.{name}: unknown key '{k}' (use image, env or ports)."); break;
                    }
                }
            }
            else { errors.Add($"services.{name}: must be an image name or a mapping with 'image'."); continue; }
            if (!IsValidImage(image)) { errors.Add($"services.{name}: '{image}' is not a valid image name."); continue; }
            if (ports.Count == 0 && DefaultPortFor(image!) is { } dp) ports.Add(dp);
            if (ports.Count == 0) { errors.Add($"services.{name}: give the port(s) the service listens on ('ports: [1234]'); the workspace's network policy only allows those."); continue; }
            services.Add(new WorkspaceSidecar(name, image!, env, ports.Distinct().ToList()));
        }
    }

    // Service env may use names a database image needs (POSTGRES_PASSWORD…); only the shell-affecting ones are refused.
    private static void ParseEnvLoose(object? value, Dictionary<string, string> env, List<string> errors, string where)
    {
        if (value is not IDictionary<object, object> map) { errors.Add($"{where}: must be a mapping."); return; }
        if (map.Count > MaxEnv) errors.Add($"{where}: too many variables (limit {MaxEnv}).");
        foreach (var (k, v) in map.Take(MaxEnv))
        {
            var name = k?.ToString() ?? "";
            if (!EnvNamePattern().IsMatch(name)) { errors.Add($"{where}: '{name}' is not a valid variable name."); continue; }
            var text = v switch { bool b => b ? "true" : "false", null => "", _ => v.ToString() ?? "" };
            if (v is IDictionary<object, object> or IList<object> || text.Length > MaxEnvValueChars || text.Contains('\0') || text.Contains('\n'))
            { errors.Add($"{where}.{name}: the value must be a single line of at most {MaxEnvValueChars} characters."); continue; }
            env[name] = text;
        }
    }

    /// <summary>The port an official image of the usual services listens on (a service with another image must say).</summary>
    public static int? DefaultPortFor(string image)
    {
        var name = image.Split('/').Last().Split(':')[0].Split('@')[0];
        return name switch
        {
            "postgres" or "postgis" => 5432, "redis" or "valkey" => 6379, "mysql" or "mariadb" => 3306, "mongo" => 27017,
            "rabbitmq" => 5672, "memcached" => 11211, "minio" => 9000, "elasticsearch" or "opensearch" => 9200, "mailhog" or "mailpit" => 1025,
            _ => null,
        };
    }

    // ---------- devcontainer.json ----------

    /// <summary>
    /// The useful subset of a devcontainer.json: <c>image</c>, <c>forwardPorts</c>, <c>postCreateCommand</c> (and onCreate/updateContent), <c>containerEnv</c>,
    /// <c>remoteEnv</c>. <c>features</c>, <c>build.dockerfile</c> and docker-compose are not run; a note says so. JSON with comments is accepted.
    /// </summary>
    public static WorkspaceEnvResult ParseDevcontainer(string? text, string source)
    {
        if (string.IsNullOrWhiteSpace(text)) return WorkspaceEnvResult.Failure(source, $"{source} is empty.");
        if (Encoding.UTF8.GetByteCount(text) > MaxFileBytes) return WorkspaceEnvResult.Failure(source, $"{source} is larger than {MaxFileBytes / 1024} KB.");
        System.Text.Json.JsonElement root;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text, new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 32 });
            root = doc.RootElement.Clone();
        }
        catch (System.Text.Json.JsonException ex) { return WorkspaceEnvResult.Failure(source, $"{source}: invalid JSON: {ex.Message}"); }
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return WorkspaceEnvResult.Failure(source, $"{source} must be a JSON object.");

        var errors = new List<string>();
        var notes = new List<string>();
        string? image = null;
        var setup = new List<string>();
        var ports = new List<int>();
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        var tasks = new List<WorkspaceTaskDefinition>();

        foreach (var p in root.EnumerateObject())
        {
            switch (p.Name)
            {
                case "image":
                    image = p.Value.ValueKind == System.Text.Json.JsonValueKind.String ? p.Value.GetString()!.Trim() : null;
                    if (!IsValidImage(image)) { errors.Add($"image: '{p.Value}' is not a valid image name."); image = null; }
                    break;
                case "forwardPorts":
                    if (p.Value.ValueKind != System.Text.Json.JsonValueKind.Array) { errors.Add("forwardPorts: must be a list of ports."); break; }
                    foreach (var item in p.Value.EnumerateArray().Take(MaxPorts))
                    {
                        // devcontainer allows "host:port" strings; only plain numbers are used.
                        var raw = item.ValueKind == System.Text.Json.JsonValueKind.Number ? item.GetRawText() : item.GetString();
                        if (raw != null && raw.Contains(':')) { notes.Add($"forwardPorts: '{raw}' (host:port) is ignored; only plain ports are forwarded."); continue; }
                        if (ValidatePort(raw, "forwardPorts") is { } pe) errors.Add(pe); else ports.Add(int.Parse(raw!));
                    }
                    break;
                case "postCreateCommand":
                case "onCreateCommand":
                case "updateContentCommand":
                    AddLifecycleCommand(p.Name, p.Value, setup, errors, notes);
                    break;
                case "containerEnv":
                case "remoteEnv":
                    if (p.Value.ValueKind != System.Text.Json.JsonValueKind.Object) { errors.Add($"{p.Name}: must be an object."); break; }
                    foreach (var e in p.Value.EnumerateObject().Take(MaxEnv))
                    {
                        if (e.Value.ValueKind != System.Text.Json.JsonValueKind.String) { notes.Add($"{p.Name}.{e.Name}: only plain string values are used."); continue; }
                        var v = e.Value.GetString() ?? "";
                        if (v.Contains("${")) { notes.Add($"{p.Name}.{e.Name}: values with ${{…}} substitutions are ignored."); continue; }
                        if (ValidateEnvName(e.Name) is { } ne) { errors.Add($"{p.Name}: {ne}"); continue; }
                        if (v.Length > MaxEnvValueChars || v.Contains('\n') || v.Contains('\0')) { errors.Add($"{p.Name}.{e.Name}: the value must be a single line of at most {MaxEnvValueChars} characters."); continue; }
                        env[e.Name] = v;
                    }
                    break;
                case "features":
                    notes.Add("This devcontainer uses \"features\"; ByteBench doesn't install them. Use a 'toolchain' or 'setup' in .bytebench/workspace.yml for what you need.");
                    break;
                case "build":
                case "dockerFile":
                case "dockerComposeFile":
                case "service":
                    if (!notes.Any(n => n.StartsWith("Dockerfile", StringComparison.Ordinal)))
                        notes.Add("Dockerfile and docker-compose based devcontainers are not built (ByteBench never builds a repository's Dockerfile); the default workspace image is used. Pick a 'toolchain' or 'image' in .bytebench/workspace.yml.");
                    break;
                case "customizations": case "name": case "$schema": case "forwardedPorts": case "portsAttributes": case "otherPortsAttributes":
                case "remoteUser": case "containerUser": case "workspaceFolder": case "mounts": case "runArgs": case "capAdd": case "privileged": case "securityOpt":
                case "initializeCommand": case "postAttachCommand": case "postStartCommand": case "hostRequirements": case "shutdownAction": case "overrideCommand":
                case "updateRemoteUserUID": case "userEnvProbe": case "init": case "appPort": case "workspaceMount": case "settings": case "extensions":
                    if (p.Name is "runArgs" or "mounts" or "capAdd" or "privileged" or "securityOpt" or "initializeCommand" or "workspaceMount")
                        notes.Add($"\"{p.Name}\" is ignored (workspaces run with fixed, restricted container settings).");
                    break;
                default:
                    notes.Add($"\"{p.Name}\" is not used.");
                    break;
            }
        }
        if (errors.Count > 0) return new WorkspaceEnvResult(null, source, errors, notes);
        ports = ports.Distinct().ToList();
        return new WorkspaceEnvResult(new WorkspaceEnvironment(source, null, image, setup, tasks, ports, env, true, [], notes), source, [], notes);
    }

    private static void AddLifecycleCommand(string name, System.Text.Json.JsonElement v, List<string> setup, List<string> errors, List<string> notes)
    {
        switch (v.ValueKind)
        {
            case System.Text.Json.JsonValueKind.String:
                Add(v.GetString());
                break;
            case System.Text.Json.JsonValueKind.Array:
                // ["npm", "install"]: an argv. Quote each word so the shell runs it exactly as one command.
                Add(string.Join(' ', v.EnumerateArray().Select(x => ShellQuote(x.GetString() ?? ""))));
                break;
            case System.Text.Json.JsonValueKind.Object:
                foreach (var e in v.EnumerateObject())
                {
                    if (e.Value.ValueKind == System.Text.Json.JsonValueKind.String) Add(e.Value.GetString());
                    else if (e.Value.ValueKind == System.Text.Json.JsonValueKind.Array) Add(string.Join(' ', e.Value.EnumerateArray().Select(x => ShellQuote(x.GetString() ?? ""))));
                }
                break;
            default:
                errors.Add($"{name}: must be a command string, a list or an object of commands.");
                break;
        }
        void Add(string? c)
        {
            c = c?.Trim();
            if (string.IsNullOrEmpty(c)) return;
            if (c.Length > MaxCommandChars || c.Contains('\0')) { errors.Add($"{name}: a command is too long (limit {MaxCommandChars} characters)."); return; }
            if (setup.Count >= MaxSetup) { errors.Add($"{name}: too many commands (limit {MaxSetup})."); return; }
            setup.Add(c);
        }
    }

    /// <summary>POSIX single-quote quoting for one word.</summary>
    public static string ShellQuote(string s) => s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '/' or ':' or '=' or '@' or '+' or ',') ? s : "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>Parses whichever candidate file it is (by its path).</summary>
    public static WorkspaceEnvResult Parse(string path, string? text) =>
        path == Path ? ParseYaml(text) : ParseDevcontainer(text, path);
}
