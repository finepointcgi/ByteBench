namespace ByteBench.Desktop.Core;

public sealed record DeepLinkResult
{
    public bool Ok { get; init; }
    public string ProfileId { get; init; } = "";
    public string Path { get; init; } = "";
    public string Url { get; init; } = "";
    public string? Reason { get; init; }
    public static DeepLinkResult Fail(string reason) => new() { Reason = reason };
}

/// <summary>
/// bytebench://open?server=&lt;id|name|host&gt;&amp;path=/p/KEY/issues/12. A link can only navigate inside a profile the user already
/// configured; it can never add a server or open another origin.
/// </summary>
public static class DeepLink
{
    public const string Scheme = "bytebench";
    private const int MaxLink = 4096;

    /// <summary>A server-relative path: a single leading "/", no control characters or backslashes, and it stays on the same origin once resolved.</summary>
    public static bool IsSafePath(string? path, string origin)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 2048) return false;
        if (path[0] != '/' || path.StartsWith("//", StringComparison.Ordinal) || path.StartsWith("/\\", StringComparison.Ordinal)) return false;
        if (path.Any(c => c < 0x20 || c == 0x7f || c == '\\')) return false;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var o)) return false;
        return Uri.TryCreate(o, path, out var resolved) && NavigationPolicy.SameOrigin(resolved, o);
    }

    public static DeepLinkResult Parse(string? raw, IReadOnlyList<ServerProfile> profiles)
    {
        if (raw is null || raw.Length > MaxLink) return DeepLinkResult.Fail("The link is too long.");
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var link)) return DeepLinkResult.Fail("The link is not valid.");
        if (!string.Equals(link.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)) return DeepLinkResult.Fail("Not a bytebench:// link.");
        if (link.UserInfo.Length > 0) return DeepLinkResult.Fail("The link is not valid.");
        if (!string.Equals(link.Host, "open", StringComparison.OrdinalIgnoreCase)) return DeepLinkResult.Fail("Unsupported link action.");

        var query = ParseQuery(link.Query);
        query.TryGetValue("server", out var server);
        query.TryGetValue("path", out var pathValue);
        var path = string.IsNullOrEmpty(pathValue) ? "/" : pathValue;

        List<ServerProfile> matches;
        if (string.IsNullOrEmpty(server))
        {
            if (profiles.Count != 1) return DeepLinkResult.Fail("The link does not say which server to use.");
            matches = [.. profiles];
        }
        else
        {
            matches = [.. profiles.Where(p => p.Id == server)];
            if (matches.Count == 0) matches = [.. profiles.Where(p => string.Equals(p.Name, server, StringComparison.OrdinalIgnoreCase))];
            if (matches.Count == 0) matches = [.. profiles.Where(p => AuthorityOf(p.Origin).Equals(server, StringComparison.OrdinalIgnoreCase) || p.Origin.Equals(server, StringComparison.OrdinalIgnoreCase))];
            if (matches.Count == 0) return DeepLinkResult.Fail($"No saved server matches \"{server}\". Add the server first.");
            if (matches.Count > 1) return DeepLinkResult.Fail($"More than one saved server matches \"{server}\".");
        }

        var profile = matches[0];
        if (!IsSafePath(path, profile.Origin)) return DeepLinkResult.Fail("The link points outside the server.");
        return new DeepLinkResult { Ok = true, ProfileId = profile.Id, Path = path, Url = new Uri(new Uri(profile.Origin), path).ToString() };
    }

    private static string AuthorityOf(string origin) => Uri.TryCreate(origin, UriKind.Absolute, out var u) ? u.Authority : "";

    /// <summary>Finds a bytebench:// URL among command-line arguments (Windows delivers deep links as an argument).</summary>
    public static string? Extract(IEnumerable<string> args) =>
        args.FirstOrDefault(a => a.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=');
            var key = Decode(i < 0 ? pair : pair[..i]);
            var value = i < 0 ? "" : Decode(pair[(i + 1)..]);
            result.TryAdd(key, value); // the first occurrence wins
        }
        return result;
    }

    private static string Decode(string s)
    {
        try { return Uri.UnescapeDataString(s.Replace('+', ' ')); }
        catch (UriFormatException) { return s; }
    }
}
