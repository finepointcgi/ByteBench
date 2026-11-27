using System.Text.RegularExpressions;

namespace ByteBench.Desktop.Core;

/// <summary>
/// Internal: the profile's own server, stays in the app. Workspace: an IDE/preview origin of the same server, stays in the app.
/// External: another web page or mailto: link, handed to the system browser. Blocked: anything else, never opened.
/// </summary>
public enum NavDecision { Internal, Workspace, External, Blocked }

/// <summary>Which kind of workspace origin a URL is: the IDE (<c>ide-{id}.{base}</c>) or a forwarded-port preview (<c>p{port}-{id}.{base}</c>).</summary>
public enum WorkspaceKind { None, Ide, Preview }

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

    public static bool IsWorkspaceUrl(Uri target, Uri server, string? advertised) => KindOf(target, server, advertised) != WorkspaceKind.None;

    private static WorkspaceKind KindOf(Uri target, Uri server, string? advertised)
    {
        if (target.Scheme != server.Scheme || target.Port != server.Port) return WorkspaceKind.None;
        var host = target.IdnHost.ToLowerInvariant();
        foreach (var b in WorkspaceBases(server.IdnHost, advertised))
        {
            if (!host.EndsWith("." + b, StringComparison.Ordinal)) continue;
            var label = host[..(host.Length - b.Length - 1)];
            // exactly one label in front of the base
            if (WorkspaceLabel().IsMatch(label)) return label.StartsWith("ide-", StringComparison.Ordinal) ? WorkspaceKind.Ide : WorkspaceKind.Preview;
        }
        return WorkspaceKind.None;
    }

    /// <summary>The workspace origin kind of <paramref name="raw"/> for this server, or None (also for the server's own pages and other sites).</summary>
    public static WorkspaceKind WorkspaceKindOf(string? raw, NavContext ctx) =>
        Uri.TryCreate(raw, UriKind.Absolute, out var target) && Uri.TryCreate(ctx.ServerOrigin, UriKind.Absolute, out var server) && target.UserInfo.Length == 0
            ? KindOf(target, server, ctx.AdvertisedWorkspaceBase)
            : WorkspaceKind.None;

    /// <summary>
    /// A page of ByteBench (the server's own origin, not a workspace) is framing a workspace origin in an iframe. WebKit gives such a frame no
    /// session cookie (third-party cookie blocking), so the app opens it as the top-level document instead. A frame inside a workspace page
    /// (the IDE's own webviews) is never promoted, and neither is a frame of any other site.
    /// </summary>
    public static bool ShouldPromoteFrame(string? frameUrl, string? topUrl, NavContext ctx) =>
        WorkspaceKindOf(frameUrl, ctx) != WorkspaceKind.None && Classify(topUrl, ctx) == NavDecision.Internal;

    /// <summary>The id in <c>/workspaces/{id}</c> on the server's own origin, or null (also for the list <c>/workspaces</c> and other pages).</summary>
    public static string? WorkspaceIdFromPage(string? raw, NavContext ctx)
    {
        if (Classify(raw, ctx) != NavDecision.Internal || !Uri.TryCreate(raw, UriKind.Absolute, out var u)) return null;
        var parts = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0].Equals("workspaces", StringComparison.OrdinalIgnoreCase) ? parts[1] : null;
    }

    /// <summary>Title for the native workspace bar: the ByteBench page title without the site suffix, else <paramref name="fallback"/>.</summary>
    public static string WorkspaceTitle(string? pageTitle, string fallback = "Workspace")
    {
        var t = (pageTitle ?? "").Trim();
        foreach (var sep in new[] { " - ByteBench", " | ByteBench", " \u2013 ByteBench" })
            if (t.EndsWith(sep, StringComparison.OrdinalIgnoreCase)) t = t[..^sep.Length].Trim();
        if (t.Length == 0 || t.Equals("ByteBench", StringComparison.OrdinalIgnoreCase)) return fallback;
        return t.Length > 80 ? t[..80] + "..." : t;
    }

    /// <summary>
    /// How many entries back the workspace bar's Back goes (1 = the entry just before the current one): the first earlier entry on a
    /// different host than the current workspace page, and if that is the ByteBench page that framed the workspace (which would only
    /// re-open it), the one before that. Null: nothing earlier, so the caller opens the workspace list. <paramref name="backList"/> is oldest first.
    /// </summary>
    public static int? WorkspaceBackSteps(string? currentUrl, IReadOnlyList<string> backList, string? framedFromUrl)
    {
        if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var cur)) return null;
        var i = backList.Count - 1;
        while (i >= 0 && Uri.TryCreate(backList[i], UriKind.Absolute, out var u) && string.Equals(u.IdnHost, cur.IdnHost, StringComparison.OrdinalIgnoreCase)) i--;
        if (i >= 0 && framedFromUrl is not null && backList[i] == framedFromUrl) i--;
        return i >= 0 ? backList.Count - i : null;
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
