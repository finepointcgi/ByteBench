using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ByteBench.Services.Webhooks;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.TrackerImport;

/// <summary>
/// The SSRF guard for tracker addresses (like the webhook, mirror and Workbench ones): http(s) only, no credentials, query or
/// fragment in the address, and no loopback, private, link-local, CGNAT or metadata addresses unless
/// <c>TrackerImport:AllowPrivateNetworks</c> (default: follow <c>Webhooks:AllowPrivateNetworks</c>) allows it.
/// The connection re-checks every resolved address, so DNS rebinding can't swap it.
/// </summary>
public sealed class TrackerUrlGuard(IOptionsMonitor<TrackerImportOptions> options, IOptionsMonitor<WebhookOptions> webhookOptions)
{
    public bool AllowPrivateNetworks => options.CurrentValue.AllowPrivateNetworks ?? webhookOptions.CurrentValue.AllowPrivateNetworks;

    public string? Validate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "Enter the address.";
        url = url.Trim();
        if (url.Length > 500) return "The address is too long.";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
            return "Enter an absolute http:// or https:// address.";
        if (url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return "The address can't contain spaces or control characters.";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "Don't put credentials in the address; use the token fields.";
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return "Enter just the address (no query or fragment).";
        if (AllowPrivateNetworks) return null;
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return "The source can't be on this server or another local address (set TrackerImport:AllowPrivateNetworks to allow it).";
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && WebhookSender.IsBlocked(ip))
            return $"The source can't be on a private, loopback, link-local or metadata address ({ip}).";
        return null;
    }

    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        if (AllowPrivateNetworks) return addresses;
        var allowed = addresses.Where(a => !WebhookSender.IsBlocked(a)).ToArray();
        if (allowed.Length == 0)
            throw new SourceException(addresses.Length == 0 ? $"Could not resolve {host}."
                : $"Blocked: {host} resolves to a private, loopback, link-local or metadata address ({addresses[0]}). Set TrackerImport:AllowPrivateNetworks to allow it.");
        return allowed;
    }
}

/// <summary>One JSON answer plus the headers readers need for paging.</summary>
public sealed record TrackerResponse(JsonElement Body, HttpResponseHeaders Headers)
{
    /// <summary>The <c>rel="next"</c> URL of a <c>Link</c> header (GitHub, GitLab), or null.</summary>
    public string? NextLink()
    {
        if (!Headers.TryGetValues("Link", out var values)) return null;
        foreach (var part in string.Join(",", values).Split(','))
        {
            var bits = part.Split(';', StringSplitOptions.TrimEntries);
            if (bits.Length > 1 && bits.Skip(1).Any(b => b.Replace(" ", "") == "rel=\"next\"") && bits[0].StartsWith('<') && bits[0].EndsWith('>'))
                return bits[0][1..^1];
        }
        return null;
    }

    public string? Header(string name) => Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;
}

/// <summary>
/// The one HTTP client readers use (singleton): guarded connections, no redirects, no cookies, a response size cap, a per-request
/// timeout, and the rate-limit rules (a 429, or a 403 that says the limit is used up, waits for <c>Retry-After</c> / the reset time
/// when that is short, else fails with a message that says when to try again). Error messages never contain the request's credentials.
/// </summary>
public sealed class TrackerHttp : IDisposable
{
    public const int MaxResponseBytes = 16 * 1024 * 1024;
    private readonly HttpClient http;
    private readonly TrackerUrlGuard guard;
    private readonly IOptionsMonitor<TrackerImportOptions> options;
    private readonly TrackerImportTestHooks hooks;

    public TrackerHttp(TrackerUrlGuard guard, IOptionsMonitor<TrackerImportOptions> options, TrackerImportTestHooks hooks)
    {
        (this.guard, this.options, this.hooks) = (guard, options, hooks);
        http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = ConnectAsync,
        }) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = MaxResponseBytes };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ByteBench-Importer/1.0");
    }

    public string? Validate(string? url) => guard.Validate(url);

    /// <summary>
    /// GETs <paramref name="url"/> and parses the JSON answer. <paramref name="configure"/> adds the credentials. Throws
    /// <see cref="SourceException"/> with a readable message for every failure (401/403 as an auth failure, 404, rate limits, 5xx, timeouts).
    /// </summary>
    public Task<TrackerResponse> GetJsonAsync(string url, Action<HttpRequestMessage> configure, CancellationToken ct, string what = "the source") =>
        SendJsonAsync(HttpMethod.Get, url, null, configure, ct, what);

    public async Task<TrackerResponse> SendJsonAsync(HttpMethod method, string url, object? body, Action<HttpRequestMessage> configure, CancellationToken ct, string what = "the source")
    {
        if (guard.Validate(url.Split('?')[0]) is { } invalid) throw new SourceException(invalid);
        for (var attempt = 0; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.CurrentValue.RequestTimeoutSeconds, 1, 300)));
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            configure(request);
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var status = (int)response.StatusCode;
                if (status is 429 || (status == 403 && IsRateLimited(response)))
                {
                    var wait = RetryAfter(response);
                    var limit = TimeSpan.FromSeconds(Math.Max(0, options.CurrentValue.MaxRateLimitWaitSeconds));
                    if (attempt >= 4 || wait == null || wait > limit)
                        throw new SourceException($"{Cap(what)} is rate limiting the import" +
                            (wait is { } w ? $"; try again in about {Math.Ceiling(w.TotalMinutes)} minute(s)." : "; try again later."));
                    await (hooks.Delay ?? Task.Delay)(wait.Value + TimeSpan.FromMilliseconds(250), ct);
                    continue;
                }
                if (status == 401) throw new SourceException($"{Cap(what)} rejected the credentials (wrong, expired or missing token).", authFailure: true);
                if (status == 403) throw new SourceException($"{Cap(what)} says the token isn't allowed to read this (check the token's scopes and the project's access).", authFailure: true);
                if (status == 404) throw new SourceException($"{Cap(what)} says this doesn't exist, or the token can't see it (check the project name and the token).");
                if (status is >= 300 and < 400)
                    throw new SourceException($"{Cap(what)} redirected ({status}); redirects aren't followed. Use the project's current address.");
                if (status >= 500 && attempt < 2) { await (hooks.Delay ?? Task.Delay)(TimeSpan.FromSeconds(1 << attempt), ct); continue; }
                if (status < 200 || status >= 300) throw new SourceException($"{Cap(what)} answered HTTP {status}.");
                var text = await ReadAsync(response, timeout.Token);
                if (string.IsNullOrWhiteSpace(text)) return new TrackerResponse(default, response.Headers);
                try { using var doc = JsonDocument.Parse(text); return new TrackerResponse(doc.RootElement.Clone(), response.Headers); }
                catch (JsonException) { throw new SourceException($"{Cap(what)}'s answer wasn't JSON (is the address the right site?)."); }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new SourceException($"{Cap(what)} didn't answer within {options.CurrentValue.RequestTimeoutSeconds} seconds.");
            }
            catch (HttpRequestException ex) when (Find<SourceException>(ex) is { } blocked) { throw blocked; }
            catch (HttpRequestException ex)
            {
                throw new SourceException($"Couldn't reach {what}: " + Truncate(ex.InnerException?.Message ?? ex.Message, 200));
            }
        }
    }

    private static bool IsRateLimited(HttpResponseMessage r) =>
        r.Headers.TryGetValues("X-RateLimit-Remaining", out var rem) && rem.FirstOrDefault() == "0" || r.Headers.Contains("Retry-After");

    private static TimeSpan? RetryAfter(HttpResponseMessage r)
    {
        if (r.Headers.RetryAfter is { } ra)
        {
            var t = ra.Delta ?? (ra.Date is { } d ? d - DateTimeOffset.UtcNow : TimeSpan.Zero);
            return t > TimeSpan.Zero ? t : TimeSpan.Zero;
        }
        if (r.Headers.TryGetValues("X-RateLimit-Reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out var epoch))
        {
            var t = DateTimeOffset.FromUnixTimeSeconds(epoch) - DateTimeOffset.UtcNow;
            return t > TimeSpan.Zero ? t : TimeSpan.Zero;
        }
        if (r.Headers.TryGetValues("RateLimit-Reset", out var reset2) && long.TryParse(reset2.FirstOrDefault(), out var e2))
        {
            var t = DateTimeOffset.FromUnixTimeSeconds(e2) - DateTimeOffset.UtcNow;
            return t > TimeSpan.Zero ? t : TimeSpan.Zero;
        }
        return null;
    }

    private static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > MaxResponseBytes) throw new HttpRequestException("The answer was too large.");
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

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public void Dispose() => http.Dispose();
}
