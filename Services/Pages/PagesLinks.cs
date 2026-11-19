using GitBench.Api;
using GitBench.Data;
using Microsoft.Extensions.Options;

namespace GitBench.Services.Pages;

/// <summary>Where a request or a link points: a project key, a repository slug and a slot ("" main site, else a review site).</summary>
public sealed record PagesTarget(string Key, string Slug, string Slot);

/// <summary>
/// URLs of sites (singleton). Path mode: <c>/pages/{KEY}/{slug}/</c> and <c>/pages/{KEY}/{slug}/-/r/{slot}/</c>. Host mode (when
/// <c>Pages:BaseDomain</c> is set and the slug is a valid host label): <c>{slug}.{key}.{BaseDomain}</c> and
/// <c>{slot}.{slug}.{key}.{BaseDomain}</c>. The first path segments <c>-/t/{token}/</c> and <c>-/r/{slot}/</c> are reserved,
/// so a site cannot have a top-level folder named <c>-</c>.
/// </summary>
public sealed class PagesLinks(IOptionsMonitor<PagesOptions> options, PublicBaseUrl baseUrl)
{
    public const string PathPrefix = "/pages";

    /// <summary>The host name of a site, or null when host mode is off or the slug can't be a host label.</summary>
    public string? HostFor(string key, string slug, string slot)
    {
        if (options.CurrentValue.Domain is not { } domain) return null;
        if (!PagesSlots.IsHostLabel(slug) || slot.Length > 0 && !PagesSlots.IsValid(slot)) return null;
        var head = slot.Length > 0 ? $"{slot}." : "";
        return $"{head}{slug}.{key.ToLowerInvariant()}.{domain}";
    }

    /// <summary>Reads a request host (no port) as a site, or null if it isn't under the base domain or doesn't have 2 or 3 labels in front of it.</summary>
    public PagesTarget? ParseHost(string host)
    {
        if (options.CurrentValue.Domain is not { } domain) return null;
        host = host.ToLowerInvariant().TrimEnd('.');
        if (!host.EndsWith("." + domain, StringComparison.Ordinal)) return null;
        var labels = host[..^(domain.Length + 1)].Split('.');
        if (labels.Any(l => !PagesSlots.IsHostLabel(l))) return null;
        return labels.Length switch
        {
            2 => new PagesTarget(labels[1].ToUpperInvariant(), labels[0], ""),
            3 when PagesSlots.IsValid(labels[0]) => new PagesTarget(labels[2].ToUpperInvariant(), labels[1], labels[0]),
            _ => null,
        };
    }

    /// <summary>True if <paramref name="host"/> is under the base domain (whether or not it names a site).</summary>
    public bool IsPagesHost(string host) =>
        options.CurrentValue.Domain is { } d && host.TrimEnd('.').EndsWith("." + d, StringComparison.OrdinalIgnoreCase);

    /// <summary>The path-mode URL without host: <c>/pages/KEY/slug/</c> (plus the review part).</summary>
    public static string PathUrl(string key, string slug, string slot = "") =>
        $"{PathPrefix}/{key}/{slug}/" + (slot.Length > 0 ? $"-/r/{slot}/" : "");

    /// <summary>
    /// The URL to show for a site: its own host when host mode applies, else the path URL (absolute when the site's base URL
    /// is known). The scheme and port of a dedicated host follow <c>PublicBaseUrl</c>.
    /// </summary>
    public string SiteUrl(string key, string slug, string slot = "")
    {
        var b = baseUrl.Value;
        if (HostFor(key, slug, slot) is { } host)
        {
            var scheme = "http";
            var port = "";
            if (Uri.TryCreate(b, UriKind.Absolute, out var u)) { scheme = u.Scheme; port = u.IsDefaultPort ? "" : ":" + u.Port; }
            return $"{scheme}://{host}{port}/";
        }
        return b + PathUrl(key, slug, slot);
    }

    public string SiteUrl(Repository repo, string slot = "") => SiteUrl(repo.Project.Key, repo.Slug, slot);
}
