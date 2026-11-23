using System.Text.Json;
using System.Text.Json.Nodes;
using ByteBench.Api;
using ByteBench.Data;

namespace ByteBench.Services.Mcp;

/// <summary>Hard output caps so a tool can never flood a model's context (or the response).</summary>
public static class McpLimits
{
    public const int MaxRequestBytes = 256 * 1024;
    /// <summary>The text content of one tool result.</summary>
    public const int MaxTextChars = 24_000;
    /// <summary>The structured content of one tool result (serialized); larger results drop it and keep the text.</summary>
    public const int MaxStructuredChars = 96_000;
    public const int MaxFileChars = 20_000;
    public const int MaxDiffChars = 16_000;
    public const int MaxLogChars = 16_000;
    public const int MaxBodyChars = 6_000;
    public const int MaxListItems = 100;
    public const string TruncatedMarker = "[truncated]";

    /// <summary>Cuts <paramref name="s"/> to <paramref name="max"/> characters (never inside a surrogate pair) and appends a marker.</summary>
    public static string Cap(string? s, int max, out bool truncated)
    {
        s ??= "";
        if (s.Length <= max) { truncated = false; return s; }
        var cut = max;
        if (cut > 0 && char.IsHighSurrogate(s[cut - 1])) cut--;
        truncated = true;
        return s[..cut] + "… " + TruncatedMarker;
    }

    public static string Cap(string? s, int max) => Cap(s, max, out _);
}

/// <summary>A tool call that can't run as asked: the message goes back to the model as an error result it can act on.</summary>
public sealed class McpToolException(string message) : Exception(message);

/// <summary>What a tool returns: concise text for the model plus structured content for programs.</summary>
public sealed class McpToolResult
{
    public string Text { get; init; } = "";
    public JsonNode? Structured { get; init; }
    public bool IsError { get; init; }
    public bool Truncated { get; init; }

    public static McpToolResult Ok(string text, object? structured = null, bool truncated = false) =>
        new() { Text = text, Structured = structured == null ? null : JsonSerializer.SerializeToNode(structured, Dto.Json), Truncated = truncated };

    public static McpToolResult Error(string message) => new() { Text = message, IsError = true };
}

/// <summary>The context a tool runs in: the token's user (already set as the acting user on <see cref="ProjectAccess"/>) and its arguments.</summary>
public sealed class McpCall(IServiceProvider services, User user, McpArgs args, CancellationToken ct)
{
    public IServiceProvider Services { get; } = services;
    public User User { get; } = user;
    public int UserId => User.Id;
    public McpArgs Args { get; } = args;
    public CancellationToken Ct { get; } = ct;
    /// <summary>The site's base URL, for links in results.</summary>
    public string BaseUrl { get; init; } = "";
    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>The project key a tool worked in, for the audit trail (never free text).</summary>
    public string? ProjectKey { get; set; }
    public int? ProjectId { get; set; }

    public async Task<Project> RequireProjectAsync(string key)
    {
        var project = await Get<IssueService>().GetProjectAsync(key)
                      ?? throw new McpToolException($"Project '{key.ToUpperInvariant()}' was not found, or you can't see it. Use list_projects to see your projects.");
        ProjectKey = project.Key;
        ProjectId = project.Id;
        return project;
    }

    public async Task<Repository> RequireRepositoryAsync(string repo)
    {
        var (key, slug) = McpArgs.ParseRepository(repo);
        var r = await Get<RepositoryService>().GetRepositoryAsync(key, slug)
                ?? throw new McpToolException($"Repository '{repo}' was not found, or you can't see it. Use list_repositories to see the repositories you can read.");
        ProjectKey = r.Project.Key;
        ProjectId = r.ProjectId;
        return r;
    }
}

public delegate Task<McpToolResult> McpHandler(McpCall call);

/// <summary>A tool: its schema and annotations (sent to clients) and its handler.</summary>
public sealed record McpToolDef(string Name, string Title, string Description, JsonObject InputSchema, bool ReadOnly, bool Destructive, bool Idempotent, McpHandler Handler)
{
    /// <summary>Write tools need a token with the write_api scope; read tools need read_api or write_api.</summary>
    public bool NeedsWrite => !ReadOnly;

    public JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["title"] = Title,
        ["description"] = Description,
        ["inputSchema"] = InputSchema.DeepClone(),
        ["annotations"] = new JsonObject
        {
            ["title"] = Title,
            ["readOnlyHint"] = ReadOnly,
            ["destructiveHint"] = !ReadOnly && Destructive,
            ["idempotentHint"] = ReadOnly || Idempotent,
            ["openWorldHint"] = false,
        },
    };
}

/// <summary>Builders for the JSON schemas of tool inputs. <see cref="McpArgs"/> validates arguments against the same schema.</summary>
public static class McpSchema
{
    public static JsonObject Obj(string[]? required = null, params (string Name, JsonObject Schema)[] props)
    {
        var p = new JsonObject();
        foreach (var (name, schema) in props) p[name] = schema;
        var o = new JsonObject { ["type"] = "object", ["properties"] = p, ["additionalProperties"] = false };
        if (required is { Length: > 0 }) o["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
        return o;
    }

    public static JsonObject Str(string description, int maxLength = 200, int minLength = 0, string[]? values = null)
    {
        var o = new JsonObject { ["type"] = "string", ["description"] = description, ["maxLength"] = maxLength };
        if (minLength > 0) o["minLength"] = minLength;
        if (values != null) o["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray());
        return o;
    }

    public static JsonObject Int(string description, int min, int max) =>
        new() { ["type"] = "integer", ["description"] = description, ["minimum"] = min, ["maximum"] = max };

    public static JsonObject Bool(string description) => new() { ["type"] = "boolean", ["description"] = description };

    public static JsonObject StrArray(string description, int maxItems = 20, int maxLength = 100) =>
        new() { ["type"] = "array", ["description"] = description, ["maxItems"] = maxItems, ["items"] = new JsonObject { ["type"] = "string", ["maxLength"] = maxLength } };
}

/// <summary>Arguments of one tool call, validated against the tool's input schema before any handler runs.</summary>
public sealed class McpArgs
{
    private readonly JsonObject values;
    public McpArgs(JsonObject values) => this.values = values;

    public bool Has(string name) => values.ContainsKey(name);
    public bool IsNull(string name) => values.TryGetPropertyValue(name, out var v) && v == null;

    public string? Str(string name) => values[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    public string RequiredStr(string name) => Str(name) is { Length: > 0 } s ? s : throw new McpToolException($"'{name}' is required.");
    public int? Int(string name) => values[name] is JsonValue v && v.TryGetValue<long>(out var n) ? (int)n : null;
    public bool? Bool(string name) => values[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    public List<string>? StrList(string name) =>
        values[name] is JsonArray a ? a.Select(x => x?.GetValue<string>() ?? "").ToList() : null;

    private static readonly System.Text.RegularExpressions.Regex IssueKeyRx = new(@"^([A-Za-z][A-Za-z0-9_]{0,31})-([1-9][0-9]{0,8})$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex ProjectKeyRx = new(@"^[A-Za-z][A-Za-z0-9_]{0,31}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static (string Key, int Number) ParseIssueKey(string value)
    {
        var m = IssueKeyRx.Match(value.Trim());
        if (!m.Success) throw new McpToolException($"'{value}' is not an issue key. Use the form PROJECT-123, for example WEB-12.");
        return (m.Groups[1].Value.ToUpperInvariant(), int.Parse(m.Groups[2].Value));
    }

    public static string ParseProjectKey(string value) =>
        ProjectKeyRx.IsMatch(value.Trim()) ? value.Trim().ToUpperInvariant()
            : throw new McpToolException($"'{value}' is not a project key. Use the short key such as WEB (see list_projects).");

    /// <summary>"KEY/slug" of a repository, as list_repositories prints it.</summary>
    public static (string Key, string Slug) ParseRepository(string value)
    {
        var parts = value.Trim().Split('/');
        if (parts.Length != 2 || !ProjectKeyRx.IsMatch(parts[0]) || parts[1].Length is 0 or > 100 || parts[1].Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new McpToolException($"'{value}' is not a repository. Use PROJECT/slug, for example WEB/api (see list_repositories).");
        return (parts[0].ToUpperInvariant(), parts[1].ToLowerInvariant());
    }

    /// <summary>A repository path or ref from a model: no traversal, control characters or leading dashes.</summary>
    public static string SafePath(string value, string name)
    {
        if (value.Any(c => char.IsControl(c)) || value.Split('/', '\\').Any(s => s is ".." ) || value.StartsWith('-'))
            throw new McpToolException($"'{name}' is not a valid repository path.");
        return value.Trim('/');
    }

    public static string SafeRef(string value)
    {
        if (value.Length is 0 or > 200 || value.StartsWith('-') || value.Any(c => char.IsControl(c) || c == ' ') || value.Contains(".."))
            throw new McpToolException("'ref' is not a valid branch, tag or commit.");
        return value;
    }

    /// <summary>
    /// Checks <paramref name="args"/> against <paramref name="schema"/> (type, required, unknown properties, length, range, enum, array size).
    /// Returns the problems found, one per line, or null when the arguments are valid.
    /// </summary>
    public static string? Validate(JsonObject schema, JsonNode? args)
    {
        if (args is not JsonObject obj) return "arguments must be an object.";
        var props = schema["properties"]!.AsObject();
        var problems = new List<string>();
        foreach (var (name, value) in obj)
        {
            if (!props.TryGetPropertyValue(name, out var s) || s is not JsonObject def)
            {
                problems.Add($"unknown argument '{name}' (allowed: {string.Join(", ", props.Select(p => p.Key))}).");
                continue;
            }
            if (value == null) continue; // null means "not given" (or "clear", for the tools that say so)
            var problem = Check(name, def, value);
            if (problem != null) problems.Add(problem);
        }
        if (schema["required"] is JsonArray required)
            foreach (var r in required.Select(x => x!.GetValue<string>()))
                if (!obj.TryGetPropertyValue(r, out var v) || v == null || v is JsonValue jv && jv.TryGetValue<string>(out var sv) && sv.Trim().Length == 0)
                    problems.Add($"'{r}' is required.");
        return problems.Count == 0 ? null : string.Join(" ", problems);
    }

    private static string? Check(string name, JsonObject def, JsonNode value)
    {
        switch (def["type"]!.GetValue<string>())
        {
            case "string":
                if (value is not JsonValue sv || !sv.TryGetValue<string>(out var s)) return $"'{name}' must be a string.";
                if (def["maxLength"] is { } ml && s.Length > ml.GetValue<int>()) return $"'{name}' is too long (max {ml.GetValue<int>()} characters).";
                if (def["minLength"] is { } minl && s.Length < minl.GetValue<int>()) return $"'{name}' is too short.";
                if (s.Contains('\0')) return $"'{name}' contains an invalid character.";
                if (def["enum"] is JsonArray e && !e.Any(x => string.Equals(x!.GetValue<string>(), s, StringComparison.OrdinalIgnoreCase)))
                    return $"'{name}' must be one of: {string.Join(", ", e.Select(x => x!.GetValue<string>()))}.";
                return null;
            case "integer":
                if (value is not JsonValue iv || !iv.TryGetValue<long>(out var n)) return $"'{name}' must be an integer.";
                if (def["minimum"] is { } mn && n < mn.GetValue<int>() || def["maximum"] is { } mx && n > mx.GetValue<int>())
                    return $"'{name}' must be between {def["minimum"]} and {def["maximum"]}.";
                return null;
            case "boolean":
                return value is JsonValue bv && bv.TryGetValue<bool>(out _) ? null : $"'{name}' must be true or false.";
            case "array":
                if (value is not JsonArray a) return $"'{name}' must be an array of strings.";
                if (def["maxItems"] is { } mi && a.Count > mi.GetValue<int>()) return $"'{name}' has too many items (max {mi.GetValue<int>()}).";
                var itemMax = def["items"]?["maxLength"]?.GetValue<int>() ?? 200;
                foreach (var item in a)
                    if (item is not JsonValue x || !x.TryGetValue<string>(out var xs) || xs.Length > itemMax || xs.Contains('\0'))
                        return $"'{name}' must contain only strings of at most {itemMax} characters.";
                return null;
            default:
                return null;
        }
    }
}
