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

public enum WorkspaceOriginKind { Ide, Preview }

/// <param name="Port">The forwarded port, for a preview.</param>
public sealed record WorkspaceOrigin(WorkspaceOriginKind Kind, int? Port, string WorkspaceId);

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

    /// <summary>The single label in front of a workspace base domain (ide-... / p3000-...), or null when the host is not directly under one.</summary>
    private static string? WorkspaceLabelOf(Uri target, Uri server, string? advertised)
    {
        if (target.Scheme != server.Scheme || target.Port != server.Port) return null;
        var host = target.IdnHost.ToLowerInvariant();
        foreach (var b in WorkspaceBases(server.IdnHost, advertised))
        {
            if (!host.EndsWith("." + b, StringComparison.Ordinal)) continue;
            var label = host[..(host.Length - b.Length - 1)];
            if (WorkspaceLabel().IsMatch(label)) return label; // exactly one label in front of the base
        }
        return null;
    }

    public static bool IsWorkspaceUrl(Uri target, Uri server, string? advertised) => WorkspaceLabelOf(target, server, advertised) is not null;

    /// <summary>The exact labels ByteBench mints: ide-{id} and p{port}-{id}, an id being 20 characters of [a-z2-7].</summary>
    [GeneratedRegex(@"^(?:(?<ide>ide)|p(?<port>[0-9]{1,5}))-(?<id>[a-z2-7]{20})$")]
    private static partial Regex StrictLabel();

    /// <summary>Which workspace (and port) a URL on one of the server's workspace origins belongs to; null for anything else, including look-alike labels.</summary>
    public static WorkspaceOrigin? ParseWorkspaceOrigin(string? raw, NavContext ctx)
    {
        if (Classify(raw, ctx) != NavDecision.Workspace) return null;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var target) || !Uri.TryCreate(ctx.ServerOrigin, UriKind.Absolute, out var server)) return null;
        var label = WorkspaceLabelOf(target, server, ctx.AdvertisedWorkspaceBase);
        var m = label is null ? null : StrictLabel().Match(label);
        if (m is null || !m.Success) return null;
        if (m.Groups["ide"].Success) return new WorkspaceOrigin(WorkspaceOriginKind.Ide, null, m.Groups["id"].Value);
        return int.TryParse(m.Groups["port"].Value, out var port) && port is >= 1 and <= 65535
            ? new WorkspaceOrigin(WorkspaceOriginKind.Preview, port, m.Groups["id"].Value)
            : null;
    }

    /// <summary>
    /// Whether a URL the server handed back for "open port {port} of workspace {workspaceId}" is exactly that: the single-use entry link
    /// (/_bb/enter?ticket=...&amp;next=/) on that workspace's own preview origin for that port. A server (or a middlebox) can't use this call to make
    /// the app load another page, another workspace, the IDE or another port.
    /// </summary>
    public static bool IsPortEntryUrl(string? raw, NavContext ctx, string workspaceId, int port)
    {
        var o = ParseWorkspaceOrigin(raw, ctx);
        if (o is not { Kind: WorkspaceOriginKind.Preview } || o.WorkspaceId != workspaceId || o.Port != port) return false;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var u) || u.AbsolutePath != "/_bb/enter" || u.Fragment.Length > 0) return false;
        string? ticket = null, next = null;
        foreach (var pair in u.Query.TrimStart('?').Split('&'))
        {
            var kv = pair.Split('=', 2);
            var value = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
            switch (kv[0])
            {
                case "ticket" when ticket is null: ticket = value; break;
                case "next" when next is null: next = value; break;
                default: return false; // an extra or repeated parameter
            }
        }
        return !string.IsNullOrEmpty(ticket) && next is not null && next.StartsWith('/') && !next.StartsWith("//", StringComparison.Ordinal) && !next.Contains('\\');
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
