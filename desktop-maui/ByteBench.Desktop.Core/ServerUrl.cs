using System.Net;
using System.Text.RegularExpressions;

namespace ByteBench.Desktop.Core;

/// <summary>Result of normalizing what the user typed as a server address.</summary>
public sealed record ServerUrlResult
{
    public bool Ok { get; init; }
    public string Origin { get; init; } = "";
    public string Host { get; init; } = "";
    public bool IsHttps { get; init; }
    public bool PrivateHost { get; init; }
    public string? Error { get; init; }
    /// <summary>True when the only problem is plain http to a public host: the UI may ask the user to confirm and retry.</summary>
    public bool NeedsHttpConfirmation { get; init; }

    public static ServerUrlResult Fail(string error, bool needsConfirm = false) => new() { Error = error, NeedsHttpConfirmation = needsConfirm };
}

/// <summary>Server URL validation and normalization (mirrors desktop/src/main/urls.ts).</summary>
public static partial class ServerUrl
{
    [GeneratedRegex(@"^[a-z][a-z0-9+.-]*://", RegexOptions.IgnoreCase)]
    private static partial Regex SchemeRegex();

    [GeneratedRegex(@"[\s\x00-\x1f]")]
    private static partial Regex BadCharsRegex();

    /// <summary>True for hosts where plain http is acceptable without a prompt: loopback, RFC 1918, link-local, ULA and local-only names.</summary>
    public static bool IsPrivateHost(string rawHost)
    {
        var host = rawHost.Trim('[', ']').ToLowerInvariant().TrimEnd('.');
        if (host.Length == 0) return false;
        if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal)) return true;
        if (host.EndsWith(".local", StringComparison.Ordinal) || host.EndsWith(".lan", StringComparison.Ordinal)
            || host.EndsWith(".internal", StringComparison.Ordinal) || host.EndsWith(".home.arpa", StringComparison.Ordinal)) return true;
        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                return b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
            }
            var v6 = ip.GetAddressBytes();
            return IPAddress.IsLoopback(ip) || (v6[0] & 0xfe) == 0xfc || (v6[0] == 0xfe && (v6[1] & 0xc0) == 0x80);
        }
        // A single-label name (an intranet host such as "bytebench") never resolves on the public internet.
        return !host.Contains('.');
    }

    /// <summary>
    /// Turns what the user typed into an origin. http(s) only; no credentials; path, query and fragment are dropped; a missing
    /// scheme means https (http for private hosts); plain http to a public host needs <paramref name="allowInsecureRemote"/>.
    /// </summary>
    public static ServerUrlResult Normalize(string? input, bool allowInsecureRemote = false)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0) return ServerUrlResult.Fail("Enter the address of your ByteBench server.");
        if (text.Length > 2048 || BadCharsRegex().IsMatch(text)) return ServerUrlResult.Fail("That does not look like a web address.");

        var candidate = text;
        if (!SchemeRegex().IsMatch(text))
        {
            if (!TryParse("https://" + text, out var probe)) return ServerUrlResult.Fail("That does not look like a web address.");
            candidate = (IsPrivateHost(probe.Host) ? "http://" : "https://") + text;
        }
        if (!TryParse(candidate, out var url)) return ServerUrlResult.Fail("That does not look like a web address.");
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) return ServerUrlResult.Fail("Only http and https addresses are supported.");
        if (url.UserInfo.Length > 0) return ServerUrlResult.Fail("Do not put a user name or password in the address; sign in on the server page.");
        if (url.Host.Length == 0) return ServerUrlResult.Fail("The address has no host name.");

        var https = url.Scheme == Uri.UriSchemeHttps;
        var priv = IsPrivateHost(url.Host);
        if (!https && !priv && !allowInsecureRemote)
        {
            return ServerUrlResult.Fail("This address uses plain http on a public host. Traffic, including your password, can be read on the network. Use https, or confirm that you accept this.", needsConfirm: true);
        }
        return new ServerUrlResult
        {
            Ok = true,
            Origin = url.GetLeftPart(UriPartial.Authority),
            Host = url.IdnHost,
            IsHttps = https,
            PrivateHost = priv,
        };
    }

    /// <summary>The display name to suggest for a new server: its host (and port).</summary>
    public static string SuggestName(string origin) => TryParse(origin, out var u) ? u.Authority : origin;

    internal static bool TryParse(string text, out Uri uri)
    {
        if (Uri.TryCreate(text, UriKind.Absolute, out var u) && u is not null) { uri = u; return true; }
        uri = null!;
        return false;
    }
}
