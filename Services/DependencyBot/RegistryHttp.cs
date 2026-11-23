using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using ByteBench.Services.Webhooks;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.DependencyBot;

/// <summary>Configuration section <c>DependencyBot</c>.</summary>
public sealed class DependencyBotOptions
{
    /// <summary>The background service that runs scheduled checks. Off: checks only run on demand ("Check now").</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Allow registry URLs on loopback, private, link-local and metadata addresses. Null follows <c>Webhooks:AllowPrivateNetworks</c>.</summary>
    public bool? AllowPrivateNetworks { get; set; }
    /// <summary>How often the background service looks for repositories that are due.</summary>
    public int PollSeconds { get; set; } = 60;
    /// <summary>One registry request is abandoned after this many seconds.</summary>
    public int RequestTimeoutSeconds { get; set; } = 30;
    /// <summary>A registry answer larger than this is refused.</summary>
    public int MaxResponseBytes { get; set; } = 8 * 1024 * 1024;
    /// <summary>Registry requests per second across the server (0: no limit).</summary>
    public double RequestsPerSecond { get; set; } = 5;
    /// <summary>Distinct packages looked up per check; the rest are reported as skipped.</summary>
    public int MaxPackagesPerRun { get; set; } = 200;
    /// <summary>Manifests read per check.</summary>
    public int MaxManifests { get; set; } = 100;
    /// <summary>A manifest larger than this is skipped.</summary>
    public int MaxManifestBytes { get; set; } = 1024 * 1024;
    /// <summary>Public registry base URLs. Private registries (and tokens for them) are not supported.</summary>
    public DependencyRegistries Registries { get; set; } = new();
}

public sealed class DependencyRegistries
{
    /// <summary>npm registry: <c>{base}/{name}</c> (scoped names as <c>@scope%2Fname</c>).</summary>
    public string Npm { get; set; } = "https://registry.npmjs.org/";
    /// <summary>NuGet v3 flat container: <c>{base}/{lower-id}/index.json</c>.</summary>
    public string NuGet { get; set; } = "https://api.nuget.org/v3-flatcontainer/";
    /// <summary>PyPI JSON API: <c>{base}/{name}/json</c>.</summary>
    public string PyPI { get; set; } = "https://pypi.org/pypi/";
    /// <summary>Go module proxy: <c>{base}/{module}/@v/list</c>.</summary>
    public string Go { get; set; } = "https://proxy.golang.org/";
}

/// <summary>A registry call failed in a way worth showing next to the package (never carries a request secret).</summary>
public sealed class RegistryException(string message, bool fatalForRegistry = false) : Exception(message)
{
    /// <summary>The registry itself is unreachable or refusing us: stop asking it for the rest of this run.</summary>
    public bool FatalForRegistry { get; } = fatalForRegistry;
}

/// <summary>
/// The SSRF guard for registry URLs (like the webhook, mirror and importer ones): http(s) only, no credentials, query or
/// fragment, and no loopback, private, link-local, CGNAT or metadata addresses unless <c>DependencyBot:AllowPrivateNetworks</c>
/// (default: follow <c>Webhooks:AllowPrivateNetworks</c>) allows it. Every resolved address is re-checked when connecting.
/// </summary>
public sealed class RegistryUrlGuard(IOptionsMonitor<DependencyBotOptions> options, IOptionsMonitor<WebhookOptions> webhookOptions)
{
    public bool AllowPrivateNetworks => options.CurrentValue.AllowPrivateNetworks ?? webhookOptions.CurrentValue.AllowPrivateNetworks;

    public string? Validate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "The registry address is empty.";
        url = url.Trim();
        if (url.Length > 2000) return "The registry address is too long.";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
            return "The registry address must be an absolute http:// or https:// URL.";
        if (url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return "The registry address can't contain spaces or control characters.";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "Registry addresses can't carry credentials (private registries are not supported).";
        if (!string.IsNullOrEmpty(uri.Fragment)) return "The registry address can't have a fragment.";
        if (AllowPrivateNetworks) return null;
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return "The registry is on this server or another local address (set DependencyBot:AllowPrivateNetworks to allow it).";
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && WebhookSender.IsBlocked(ip))
            return $"The registry is on a private, loopback, link-local or metadata address ({ip}).";
        return null;
    }

    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        if (AllowPrivateNetworks) return addresses;
        var allowed = addresses.Where(a => !WebhookSender.IsBlocked(a)).ToArray();
        if (allowed.Length == 0)
            throw new RegistryException(addresses.Length == 0 ? $"Could not resolve {host}."
                : $"Blocked: {host} resolves to a private, loopback, link-local or metadata address ({addresses[0]}). Set DependencyBot:AllowPrivateNetworks to allow it.", fatalForRegistry: true);
        return allowed;
    }
}

/// <summary>
/// The one HTTP client the bot's registry lookups use (singleton): guarded connections, no redirects, no cookies, no credentials,
/// a response size cap, a per-request timeout and a global requests-per-second limit. A 429 is not retried: it ends the run for that registry.
/// </summary>
public sealed class RegistryHttp : IDisposable
{
    private readonly HttpClient http;
    private readonly RegistryUrlGuard guard;
    private readonly IOptionsMonitor<DependencyBotOptions> options;
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTime nextSlot = DateTime.MinValue;

    public RegistryHttp(RegistryUrlGuard guard, IOptionsMonitor<DependencyBotOptions> options)
    {
        (this.guard, this.options) = (guard, options);
        http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = ConnectAsync,
        }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ByteBench-DependencyBot/1.0");
    }

    public string? Validate(string? url) => guard.Validate(url);

    /// <summary>Requests made since start (tests assert "no network when disabled").</summary>
    public int RequestCount;

    public async Task<string> GetStringAsync(string url, string accept, CancellationToken ct)
    {
        if (guard.Validate(url) is { } invalid) throw new RegistryException(invalid, fatalForRegistry: true);
        await ThrottleAsync(ct);
        var o = options.CurrentValue;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(o.RequestTimeoutSeconds, 1, 300)));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd(accept);
        Interlocked.Increment(ref RequestCount);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            if (status == 404 || status == 410) throw new RegistryException("The registry doesn't know this package.");
            if (status == 429) throw new RegistryException("The registry is rate limiting requests; try again later.", fatalForRegistry: true);
            if (status is 401 or 403) throw new RegistryException($"The registry refused the request (HTTP {status}); private registries are not supported.");
            if (status is >= 300 and < 400) throw new RegistryException($"The registry redirected ({status}); redirects aren't followed.");
            if (status >= 500) throw new RegistryException($"The registry answered HTTP {status}.");
            if (status < 200 || status >= 300) throw new RegistryException($"The registry answered HTTP {status}.");
            if (response.Content.Headers.ContentLength > o.MaxResponseBytes) throw new RegistryException("The registry's answer is too large.");
            return await ReadAsync(response, o.MaxResponseBytes, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RegistryException($"The registry didn't answer within {o.RequestTimeoutSeconds} seconds.");
        }
        catch (HttpRequestException ex) when (Find<RegistryException>(ex) is { } blocked) { throw blocked; }
        catch (HttpRequestException ex)
        {
            var message = ex.InnerException?.Message ?? ex.Message;
            throw new RegistryException("Couldn't reach the registry: " + (message.Length > 160 ? message[..160] : message), fatalForRegistry: true);
        }
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        var rps = options.CurrentValue.RequestsPerSecond;
        if (rps <= 0) return;
        await gate.WaitAsync(ct);
        try
        {
            var wait = nextSlot - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            nextSlot = DateTime.UtcNow + TimeSpan.FromSeconds(1 / rps);
        }
        finally { gate.Release(); }
    }

    private static async Task<string> ReadAsync(HttpResponseMessage response, int max, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > max) throw new RegistryException("The registry's answer is too large.");
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await guard.ResolveAsync(context.DnsEndPoint.Host, ct);
        if (addresses.Length == 0) throw new HttpRequestException($"Could not resolve {context.DnsEndPoint.Host}.");
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
        throw new HttpRequestException(last?.Message ?? "Could not connect.", last);
    }

    private static T? Find<T>(Exception? ex) where T : Exception
    {
        for (; ex != null; ex = ex.InnerException) if (ex is T t) return t;
        return null;
    }

    public void Dispose() { http.Dispose(); gate.Dispose(); }
}
