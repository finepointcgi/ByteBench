using System.Text.Json;

namespace ByteBench.Desktop.Core;

public sealed record ServerInfo(string Name, string Version, int ApiVersion, IReadOnlyList<string> Capabilities, string? WorkspaceBaseDomain);

public enum CompatLevel { Ok, Legacy, Newer, Unavailable, NotByteBench }

public sealed record Compat(CompatLevel Level, ServerInfo? Info, string? Message)
{
    /// <summary>Notifications need a server that answers server-info (current or newer).</summary>
    public bool SupportsNotifications => Level is CompatLevel.Ok or CompatLevel.Newer;
}

public sealed record ApiNotification(int Id, string Kind, string Title, string Url, bool IsRead, string CreatedAt, string? Actor);

public sealed record NotificationList(int UnreadCount, IReadOnlyList<ApiNotification> Items);

/// <summary>Defensive parsing of the server's JSON: the app trusts nothing a server (or a middlebox) returns.</summary>
public static class ServerApi
{
    /// <summary>The highest server-info apiVersion this build understands.</summary>
    public const int SupportedApiVersion = 1;

    /// <summary>Interprets the answer of GET /api/v1/server-info (HTTP status and body text, or a null status when the request failed outright).</summary>
    public static Compat EvaluateServerInfo(int? status, string? body)
    {
        if (status is null) return new(CompatLevel.Unavailable, null, "The server could not be reached.");
        if (status >= 500) return new(CompatLevel.Unavailable, null, $"The server answered with an error (HTTP {status}). It may be starting up or down for maintenance.");
        JsonElement? obj = TryObject(body);
        if (status is 404 or 401 or 405)
        {
            // An older ByteBench answers unknown /api paths with a problem document; a random website does not.
            if (obj is { } o && o.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String && o.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Number)
                return new(CompatLevel.Legacy, null, "This server is older than the desktop app expects: notifications and badges are off until it is updated.");
            return new(CompatLevel.NotByteBench, null, "This address did not answer like a ByteBench server.");
        }
        if (status != 200 || obj is not { } root) return new(CompatLevel.NotByteBench, null, "This address did not answer like a ByteBench server.");
        if (!TryString(root, "name", out var name) || name != "ByteBench" || !TryString(root, "version", out var version)
            || !root.TryGetProperty("apiVersion", out var av) || av.ValueKind != JsonValueKind.Number || !av.TryGetInt32(out var apiVersion))
            return new(CompatLevel.NotByteBench, null, "This address did not answer like a ByteBench server.");

        var caps = new List<string>();
        if (root.TryGetProperty("capabilities", out var c) && c.ValueKind == JsonValueKind.Array)
            caps.AddRange(c.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
        string? baseDomain = TryString(root, "workspaceBaseDomain", out var wb) ? wb : null;
        var info = new ServerInfo("ByteBench", version!, apiVersion, caps, baseDomain);
        return apiVersion > SupportedApiVersion
            ? new(CompatLevel.Newer, info, $"This server (ByteBench {version}) is newer than this desktop app. Update the desktop app for full support.")
            : new(CompatLevel.Ok, info, null);
    }

    /// <summary>Parses GET /api/v1/notifications. Returns null when the body is not the expected shape.</summary>
    public static NotificationList? ParseNotificationList(string? body)
    {
        if (TryObject(body) is not { } root) return null;
        if (!root.TryGetProperty("unreadCount", out var uc) || uc.ValueKind != JsonValueKind.Number || !uc.TryGetInt64(out var unread)) return null;
        if (!root.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var items = new List<ApiNotification>();
        foreach (var n in arr.EnumerateArray())
        {
            if (n.ValueKind != JsonValueKind.Object) continue;
            if (!n.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var idValue)) continue;
            if (!TryString(n, "title", out var title) || !TryString(n, "url", out var url)) continue;
            items.Add(new ApiNotification(
                idValue,
                TryString(n, "kind", out var kind) ? kind! : "",
                Truncate(title!, 200),
                url!,
                n.TryGetProperty("isRead", out var r) && r.ValueKind == JsonValueKind.True,
                TryString(n, "createdAt", out var created) ? created! : "",
                TryString(n, "actor", out var actor) ? Truncate(actor!, 100) : null));
        }
        return new NotificationList((int)Math.Clamp(unread, 0, int.MaxValue), items);
    }

    public static string? ParseUserName(string? body) =>
        TryObject(body) is { } o && TryString(o, "userName", out var u) ? u : null;

    private static JsonElement? TryObject(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool TryString(JsonElement o, string name, out string? value)
    {
        if (o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String) { value = p.GetString(); return value is not null; }
        value = null;
        return false;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
