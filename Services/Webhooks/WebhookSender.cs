using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace GitBench.Services.Webhooks;

public sealed class WebhookOptions
{
    /// <summary>
    /// Allow deliveries to loopback, private (RFC 1918 / ULA), link-local, CGNAT and cloud-metadata addresses.
    /// Off by default to prevent SSRF; appsettings.Development.json turns it on for local testing.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }
}

/// <summary>Thrown (inside the connect callback) when a webhook host resolves only to blocked addresses.</summary>
public sealed class WebhookBlockedException(string message) : Exception(message);

public sealed record WebhookAttempt(bool Success, int? StatusCode, string? Error, string? ResponseBody, int DurationMs, bool Retryable);

/// <summary>
/// Sends one webhook request. SSRF protection: only http/https; DNS is resolved at connect time and the socket connects
/// only to an address that passed the check (so DNS rebinding can't swap it); no proxy; redirects are not followed.
/// </summary>
public sealed class WebhookSender : IDisposable
{
    public const int MaxResponseBytes = 4096;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient client;
    private readonly IOptionsMonitor<WebhookOptions> options;

    public WebhookSender(IOptionsMonitor<WebhookOptions> options)
    {
        this.options = options;
        client = new HttpClient(CreateGuardedHandler()) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GitBench-Hookshot/1.0");
    }

    /// <summary>
    /// A handler with the same SSRF protection as webhook deliveries (checked addresses at connect time, no proxy, no
    /// redirects), for other outgoing integrations such as Slack and Teams.
    /// </summary>
    public SocketsHttpHandler CreateGuardedHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        ConnectTimeout = Timeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = ConnectAsync,
    };

    public static string Sign(string secret, byte[] body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    /// <summary>Null if <paramref name="url"/> is an acceptable webhook URL, else why not. Literal IPs are checked here too.</summary>
    public string? ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
            return "Enter an absolute http:// or https:// URL.";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "Credentials in the URL aren't allowed; use the secret to authenticate deliveries.";
        if (options.CurrentValue.AllowPrivateNetworks) return null;
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return "Webhooks can't target this server or other local addresses.";
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IsBlocked(ip))
            return $"Webhooks can't target private, loopback, link-local or metadata addresses ({ip}).";
        return null;
    }

    public async Task<WebhookAttempt> SendAsync(string url, string eventName, string deliveryId, string secret, byte[] body,
        bool redelivery, int webhookId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (ValidateUrl(url) is { } invalid) return new(false, null, invalid, null, 0, Retryable: false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        request.Headers.Add("X-GitBench-Event", eventName);
        request.Headers.Add("X-GitBench-Delivery", deliveryId);
        request.Headers.Add("X-GitBench-Hook-ID", webhookId.ToString());
        request.Headers.Add("X-GitBench-Signature-256", Sign(secret, body));
        if (redelivery) request.Headers.Add("X-GitBench-Redelivery", "true");

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var text = await ReadPrefixAsync(response, timeout.Token);
            var status = (int)response.StatusCode;
            var ok = status is >= 200 and < 300;
            string? error = ok ? null
                : status is >= 300 and < 400 ? $"Redirect ({status}) not followed{(response.Headers.Location is { } l ? $": {l}" : "")}."
                : $"HTTP {status} {response.ReasonPhrase}";
            return new(ok, status, error, text, (int)sw.ElapsedMilliseconds, Retryable: status >= 500);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, null, $"Timed out after {Timeout.TotalSeconds:0} s.", null, (int)sw.ElapsedMilliseconds, Retryable: true);
        }
        catch (HttpRequestException ex) when (Find<WebhookBlockedException>(ex) is { } blocked)
        {
            return new(false, null, blocked.Message, null, (int)sw.ElapsedMilliseconds, Retryable: false);
        }
        catch (HttpRequestException ex)
        {
            return new(false, null, Truncate(ex.InnerException?.Message ?? ex.Message, 1000), null, (int)sw.ElapsedMilliseconds, Retryable: true);
        }
    }

    private static async Task<string?> ReadPrefixAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[MaxResponseBytes];
            var filled = 0;
            int n;
            while (filled < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(filled), ct)) > 0) filled += n;
            if (filled == 0) return null;
            var text = Encoding.UTF8.GetString(buffer, 0, filled);
            return text.Length > MaxResponseBytes ? text[..MaxResponseBytes] : text;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException) { return null; }
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        if (!options.CurrentValue.AllowPrivateNetworks)
        {
            var blocked = addresses.Where(IsBlocked).ToList();
            addresses = addresses.Where(a => !IsBlocked(a)).ToArray();
            if (addresses.Length == 0)
                throw new WebhookBlockedException(blocked.Count == 0
                    ? $"Could not resolve {host}."
                    : $"Blocked: {host} resolves to a private, loopback, link-local or metadata address ({blocked[0]}). " +
                      "Set Webhooks:AllowPrivateNetworks to allow it.");
        }
        if (addresses.Length == 0) throw new HttpRequestException($"Could not resolve {host}.");

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex) { socket.Dispose(); last = ex; }
            catch { socket.Dispose(); throw; }
        }
        throw last!;
    }

    /// <summary>Loopback, unspecified, private, CGNAT, link-local (incl. 169.254.169.254 metadata), multicast and reserved ranges.</summary>
    public static bool IsBlocked(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] switch
            {
                0 or 10 or 127 => true,                                 // "this network", private, loopback
                100 => b[1] >= 64 && b[1] <= 127,                       // CGNAT 100.64/10
                169 => b[1] == 254,                                     // link-local, cloud metadata
                172 => b[1] >= 16 && b[1] <= 31,                        // private 172.16/12
                192 => (b[1] == 168) || (b[1] == 0 && b[2] == 0) || (b[1] == 0 && b[2] == 2), // private, IETF, TEST-NET-1
                198 => b[1] == 18 || b[1] == 19 || (b[1] == 51 && b[2] == 100),             // benchmarking, TEST-NET-2
                203 => b[1] == 0 && b[2] == 113,                        // TEST-NET-3
                >= 224 => true,                                         // multicast, reserved, broadcast
                _ => false,
            };
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.IPv6None) || ip.Equals(IPAddress.IPv6Any)) return true;
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal) return true; // fe80::/10, fec0::/10, ff00::/8, fc00::/7 (incl. fd00:ec2::254)
            var b = ip.GetAddressBytes();
            // NAT64 64:ff9b::/96 and 6to4 2002::/16 embed an IPv4 address: check that one.
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4..12].All(x => x == 0))
                return IsBlocked(new IPAddress(b[12..16]));
            if (b[0] == 0x20 && b[1] == 0x02) return IsBlocked(new IPAddress(b[2..6]));
            if (b[..12].All(x => x == 0)) return true; // deprecated IPv4-compatible ::a.b.c.d
            return false;
        }
        return true;
    }

    private static T? Find<T>(Exception ex) where T : Exception
    {
        for (Exception? e = ex; e != null; e = e.InnerException) if (e is T t) return t;
        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public void Dispose() => client.Dispose();
}
