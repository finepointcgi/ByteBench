using System.Net;
using GitBench.Services.Webhooks;
using Microsoft.Extensions.Options;

namespace GitBench.Services.Mirroring;

/// <summary>Configuration section <c>Mirroring</c>.</summary>
public sealed class MirrorOptions
{
    /// <summary>
    /// Allow mirror URLs on loopback, private, link-local, CGNAT and metadata addresses. Null (the default) follows
    /// <c>Webhooks:AllowPrivateNetworks</c>.
    /// </summary>
    public bool? AllowPrivateNetworks { get; set; }
    /// <summary>The background service that runs pull mirrors on their interval and push mirrors after pushes.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>A sync (one git fetch or push) is killed after this long.</summary>
    public int TimeoutSeconds { get; set; } = 600;
    /// <summary>The shortest pull interval a mirror may use.</summary>
    public int MinIntervalMinutes { get; set; } = 5;
    /// <summary>How often the background service looks for pull mirrors that are due.</summary>
    public int PollSeconds { get; set; } = 30;
    /// <summary>ssh:// mirrors: the private key GitBench authenticates with (none: ssh mirrors can't authenticate).</summary>
    public string? SshKeyFile { get; set; }
    /// <summary>ssh:// mirrors: known hosts file (default <c>{ContentRoot}/keys/mirror_known_hosts</c>); new hosts are trusted on first use.</summary>
    public string? KnownHostsFile { get; set; }
}

/// <summary>
/// The SSRF guard for mirror URLs, like the webhook one (<see cref="WebhookSender"/>): only <c>http</c>, <c>https</c> and
/// <c>ssh</c> URLs, no credentials in the URL, and no loopback, private, link-local, CGNAT or metadata addresses unless
/// <c>Mirroring:AllowPrivateNetworks</c> (or, when that isn't set, <c>Webhooks:AllowPrivateNetworks</c>) is on. The host
/// is resolved right before git runs, and git is pinned to the checked address (<c>http.curloptResolve</c>, or ssh's
/// <c>HostName</c>), so DNS rebinding can't swap it; git itself only speaks those protocols (<c>GIT_ALLOW_PROTOCOL</c>)
/// and doesn't follow redirects.
/// </summary>
public sealed class MirrorUrlGuard(IOptionsMonitor<MirrorOptions> options, IOptionsMonitor<WebhookOptions> webhookOptions)
{
    public bool AllowPrivateNetworks => options.CurrentValue.AllowPrivateNetworks ?? webhookOptions.CurrentValue.AllowPrivateNetworks;

    /// <summary>Null if <paramref name="url"/> is an acceptable mirror URL, else why not. Literal IPs are checked here too.</summary>
    public string? Validate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "Enter the repository URL.";
        url = url.Trim();
        if (url.Length > 2048) return "The URL is too long.";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ssh") || string.IsNullOrEmpty(uri.Host))
            return "Enter an absolute https://, http:// or ssh:// URL (for SSH use ssh://git@host/path.git, not git@host:path).";
        if (url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return "The URL can't contain spaces or control characters.";
        if (uri.Host.StartsWith('-') || uri.UserInfo.StartsWith('-') || uri.AbsolutePath.StartsWith("/-", StringComparison.Ordinal))
            return "The URL isn't valid.";
        if (uri.Scheme == "ssh" ? uri.UserInfo.Contains(':') : uri.UserInfo.Length > 0)
            return "Don't put credentials in the URL: use the username and password or token fields.";
        if (uri.AbsolutePath is "" or "/") return "The URL needs the repository path.";
        if (AllowPrivateNetworks) return null;
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return "Mirrors can't use this server or other local addresses.";
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && WebhookSender.IsBlocked(ip))
            return $"Mirrors can't use private, loopback, link-local or metadata addresses ({ip}).";
        return null;
    }

    /// <summary>
    /// Checks the URL and resolves its host. Returns the address git must connect to (null when private networks are
    /// allowed: git resolves the name itself), or throws <see cref="MirrorException"/>.
    /// </summary>
    public async Task<IPAddress?> ResolveAsync(Uri uri, CancellationToken ct)
    {
        if (Validate(uri.OriginalString) is { } invalid) throw new MirrorException(invalid);
        if (AllowPrivateNetworks) return null;
        var host = uri.Host.Trim('[', ']');
        IPAddress[] addresses;
        try { addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct); }
        catch (System.Net.Sockets.SocketException) { throw new MirrorException($"Could not resolve {host}."); }
        var allowed = addresses.Where(a => !WebhookSender.IsBlocked(a)).ToList();
        if (allowed.Count == 0)
            throw new MirrorException(addresses.Length == 0
                ? $"Could not resolve {host}."
                : $"Blocked: {host} resolves to a private, loopback, link-local or metadata address ({addresses[0]}). " +
                  "Set Mirroring:AllowPrivateNetworks to allow it.");
        return allowed.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? allowed[0];
    }
}

/// <summary>A mirror sync failed for a reason worth showing to the user as the last error.</summary>
public sealed class MirrorException(string message) : Exception(message);
