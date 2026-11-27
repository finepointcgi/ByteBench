using System.Text.Json;
using System.Text.RegularExpressions;

namespace ByteBench.Desktop.Core;

public sealed record WorkspaceSummary(string Id, string Repository, string Ref);

/// <param name="Origin">Where the port is served (its own origin, e.g. http://p5173-{id}.localhost:5108); null when the server can't say.</param>
public sealed record ForwardedPort(int Port, string? Origin);

public sealed record WorkspacePortList(WorkspaceSummary Workspace, IReadOnlyList<ForwardedPort> Ports);

/// <summary>
/// "List a workspace's forwarded ports and open one" without the UI (mirrors desktop/src/main/workspace-ports.ts). Everything a server sends back is
/// validated here before the app acts on it: it trusts nothing a server or a middlebox returns.
/// </summary>
public static partial class WorkspacePorts
{
    /// <summary>The server capability that says GET /api/v1/workspaces/{id}/ports and POST .../ports/{port}/open exist.</summary>
    public const string Capability = "workspace-ports";

    private const int MaxWorkspaces = 50;
    private const int MaxPorts = 200;

    [GeneratedRegex("^[a-z2-7]{20}$")]
    public static partial Regex WorkspaceId();

    public static bool ServerSupportsPorts(IReadOnlyList<string>? capabilities) => capabilities?.Contains(Capability) == true;

    /// <summary>The running workspaces in GET /api/v1/workspaces: anything else, and anything malformed, is dropped.</summary>
    public static IReadOnlyList<WorkspaceSummary> ParseWorkspaces(string? body)
    {
        var list = new List<WorkspaceSummary>();
        if (Root(body) is not { ValueKind: JsonValueKind.Array } arr) return list;
        foreach (var w in arr.EnumerateArray())
        {
            if (list.Count >= MaxWorkspaces) break;
            if (w.ValueKind != JsonValueKind.Object) continue;
            if (Str(w, "state") != "Running" || Str(w, "id") is not { } id || !WorkspaceId().IsMatch(id)) continue;
            list.Add(new WorkspaceSummary(id, $"{Clean(Str(w, "project"), 40)}/{Clean(Str(w, "repository"), 100)}", Clean(Str(w, "ref"), 100)));
        }
        return list;
    }

    /// <summary>The ports in GET /api/v1/workspaces/{id}/ports: valid port numbers only, sorted, without duplicates.</summary>
    public static IReadOnlyList<ForwardedPort> ParsePorts(string? body, NavContext ctx, string workspaceId)
    {
        var seen = new SortedDictionary<int, ForwardedPort>();
        if (Root(body) is not { ValueKind: JsonValueKind.Array } arr) return [];
        foreach (var p in arr.EnumerateArray())
        {
            if (seen.Count >= MaxPorts) break;
            if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("port", out var pn) || pn.ValueKind != JsonValueKind.Number || !pn.TryGetInt32(out var port)) continue;
            if (port is < 1 or > 65535 || seen.ContainsKey(port)) continue;
            var origin = Str(p, "origin");
            seen[port] = new ForwardedPort(port, origin is not null && OriginMatches(origin, ctx, workspaceId, port) ? origin : null);
        }
        return [.. seen.Values];
    }

    /// <summary>An advertised origin is only shown when it really is that port's origin of that workspace on this server.</summary>
    private static bool OriginMatches(string origin, NavContext ctx, string workspaceId, int port) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var u) && u.AbsolutePath == "/" && u.Query.Length == 0 && u.Fragment.Length == 0
        && NavigationPolicy.IsPortEntryUrl($"{u.GetLeftPart(UriPartial.Authority)}/_bb/enter?ticket=x&next=%2F", ctx, workspaceId, port);

    /// <summary>The entry URL of POST .../ports/{port}/open, or null when it isn't exactly the single-use link for that workspace and port on this server.</summary>
    public static string? ParseOpenResponse(string? body, NavContext ctx, string workspaceId, int port) =>
        Root(body) is { ValueKind: JsonValueKind.Object } o && Str(o, "url") is { Length: <= 2048 } url && NavigationPolicy.IsPortEntryUrl(url, ctx, workspaceId, port) ? url : null;

    /// <summary>A short, local-looking address for display: host and port of the workspace origin (p5173-....localhost:5108).</summary>
    public static string DisplayAddress(string? origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var u) ? u.Authority : "";

    /// <summary>One line of a menu or list: the workspace, the port and its address.</summary>
    public static string Label(WorkspaceSummary w, ForwardedPort p) =>
        $"{w.Repository} ({w.Ref}) · port {p.Port}" + (p.Origin is null ? "" : " · " + DisplayAddress(p.Origin));

    private static JsonElement? Root(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonDocument.Parse(body).RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static string Clean(string? s, int max)
    {
        if (s is null) return "";
        var chars = s.Select(c => char.IsControl(c) ? ' ' : c).ToArray();
        return new string(chars, 0, Math.Min(chars.Length, max));
    }
}
