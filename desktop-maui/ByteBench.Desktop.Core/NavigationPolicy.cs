using System.Text.RegularExpressions;

namespace ByteBench.Desktop.Core;

/// <summary>
/// Internal: the profile's own server, stays in the app. Workspace: an IDE/preview origin of the same server, stays in the app.
/// External: another web page or mailto: link, handed to the system browser. Blocked: anything else, never opened.
/// </summary>
public enum NavDecision { Internal, Workspace, External, Blocked }

/// <param name="ServerOrigin">The profile's origin (scheme://host[:port]).</param>
/// <param name="AdvertisedWorkspaceBase">Workspace base domain from server-info, if any.</param>
public sealed record NavContext(string ServerOrigin, string? AdvertisedWorkspaceBase = null);

/// <summary>Navigation allow-list (mirrors desktop/src/main/allowlist.ts). The WebView hooks only act on the decision.</summary>
public static partial class NavigationPolicy
{
    [GeneratedRegex(@"^(?:ide|p\d{1,5})-[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex WorkspaceLabel();

    [GeneratedRegex(@"^[a-z0-9.-]+$")]
    private static partial Regex BaseDomainChars();

    public static IReadOnlyList<string> WorkspaceBases(string serverHost, string? advertised)
    {
        var bases = new List<string> { serverHost.ToLowerInvariant() };
        var adv = advertised?.Trim().ToLowerInvariant().TrimStart('.');
        // The advertised base must be the server's own host or a parent of it, and not a bare public suffix like "com".
        if (!string.IsNullOrEmpty(adv) && BaseDomainChars().IsMatch(adv) && (adv == "localhost" || adv.Contains('.'))
            && (serverHost.Equals(adv, StringComparison.OrdinalIgnoreCase) || serverHost.EndsWith("." + adv, StringComparison.OrdinalIgnoreCase))
            && !bases.Contains(adv))
        {
            bases.Add(adv);
        }
        return bases;
    }

    public static bool IsWorkspaceUrl(Uri target, Uri server, string? advertised)
    {
        if (target.Scheme != server.Scheme || target.Port != server.Port) return false;
        var host = target.IdnHost.ToLowerInvariant();
        foreach (var b in WorkspaceBases(server.IdnHost, advertised))
        {
            if (!host.EndsWith("." + b, StringComparison.Ordinal)) continue;
            var label = host[..(host.Length - b.Length - 1)];
            if (WorkspaceLabel().IsMatch(label)) return true; // exactly one label in front of the base
        }
        return false;
    }

    public static NavDecision Classify(string? raw, NavContext ctx)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var target) || !Uri.TryCreate(ctx.ServerOrigin, UriKind.Absolute, out var server)) return NavDecision.Blocked;
        if (target.Scheme == Uri.UriSchemeMailto) return NavDecision.External;
        if (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps) return NavDecision.Blocked;
        if (target.UserInfo.Length > 0) return NavDecision.Blocked;
        if (SameOrigin(target, server)) return NavDecision.Internal;
        if (IsWorkspaceUrl(target, server, ctx.AdvertisedWorkspaceBase)) return NavDecision.Workspace;
        return NavDecision.External;
    }

    public static bool SameOrigin(Uri a, Uri b) =>
        a.Scheme == b.Scheme && a.Port == b.Port && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the app may hand this URL to the operating system (default browser or mail client).</summary>
    public static bool IsSafeExternal(string? raw) =>
        Uri.TryCreate(raw, UriKind.Absolute, out var u)
        && (u.Scheme is "https" or "http" ? u.UserInfo.Length == 0 : u.Scheme == "mailto");
}
