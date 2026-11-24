using System.Net;
using ByteBench.Services.Webhooks;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Push;

/// <summary>Thrown (inside the connect callback) when a push endpoint resolves only to blocked addresses.</summary>
public sealed class PushBlockedException(string message) : Exception(message);

/// <summary>
/// The SSRF guard for push subscription endpoints, which browsers hand to the page and the page posts to the server, so they
/// are user-supplied: https only, no credentials or fragment, optionally only <see cref="PushOptions.AllowedHostSuffixes"/>,
/// and no loopback, private, link-local, CGNAT or metadata addresses (the same check as webhooks) unless
/// <c>Push:AllowPrivateNetworks</c> (default: <c>Webhooks:AllowPrivateNetworks</c>) allows it; then plain http is also
/// accepted for loopback addresses (local fake push services). The sender's connect callback re-checks every resolved
/// address, so DNS rebinding can't swap it.
/// </summary>
public sealed class PushEndpointGuard(IOptionsMonitor<PushOptions> options, IOptionsMonitor<WebhookOptions> webhookOptions)
{
    public const int MaxEndpointLength = 2000;

    public bool AllowPrivateNetworks => options.CurrentValue.AllowPrivateNetworks ?? webhookOptions.CurrentValue.AllowPrivateNetworks;

    /// <summary>Null when <paramref name="endpoint"/> is acceptable, else why not (shown to the user).</summary>
    public string? Validate(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return "The subscription has no endpoint.";
        if (endpoint.Length > MaxEndpointLength) return "The push endpoint is too long.";
        if (endpoint.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return "The push endpoint can't contain spaces or control characters.";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return "The push endpoint isn't an absolute URL.";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "The push endpoint can't contain credentials.";
        if (endpoint.Contains('#')) return "The push endpoint can't contain a fragment.";
        var loopback = uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
        var allowPrivate = AllowPrivateNetworks;
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && allowPrivate && loopback))
            return "The push endpoint must be an https:// address.";
        var suffixes = options.CurrentValue.AllowedHostSuffixes.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim().TrimStart('.')).ToList();
        if (suffixes.Count > 0 && !suffixes.Any(s => uri.Host.Equals(s, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + s, StringComparison.OrdinalIgnoreCase)))
            return "This push service isn't allowed on this server (Push:AllowedHostSuffixes).";
        if (allowPrivate) return null;
        if (loopback) return "The push endpoint can't be on this server or another local address.";
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && WebhookSender.IsBlocked(ip))
            return $"The push endpoint can't be on a private, loopback, link-local or metadata address ({ip}).";
        return null;
    }

    /// <summary>The addresses a connection to <paramref name="host"/> may use (blocked ones removed). Throws <see cref="PushBlockedException"/> when none is left.</summary>
    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        if (AllowPrivateNetworks) return addresses;
        var allowed = addresses.Where(a => !WebhookSender.IsBlocked(a)).ToArray();
        if (allowed.Length == 0)
            throw new PushBlockedException(addresses.Length == 0 ? $"Could not resolve {host}."
                : $"The push endpoint {host} resolves to a private, loopback, link-local or metadata address.");
        return allowed;
    }

    /// <summary><see cref="Validate"/> plus a DNS check (at subscription time): null when acceptable.</summary>
    public async Task<string?> ValidateResolvedAsync(string? endpoint, CancellationToken ct)
    {
        if (Validate(endpoint) is { } invalid) return invalid;
        try { await ResolveAsync(new Uri(endpoint!).Host, ct); return null; }
        catch (PushBlockedException ex) { return ex.Message; }
        catch (System.Net.Sockets.SocketException) { return "The push endpoint's host could not be resolved."; }
    }
}
