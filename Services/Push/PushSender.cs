using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Push;

public enum PushOutcome { Delivered, Gone, RateLimited, Failed }

/// <param name="RetryAfter">For <see cref="PushOutcome.RateLimited"/>: how long to wait (from <c>Retry-After</c>, default 1 minute, at most 1 hour).</param>
public sealed record PushResult(PushOutcome Outcome, int? StatusCode, string? Error = null, TimeSpan? RetryAfter = null);

/// <summary>
/// Sends one encrypted Web Push message (RFC 8030) to a subscription endpoint: <c>TTL</c>, <c>Urgency: normal</c>,
/// <c>Content-Encoding: aes128gcm</c> and VAPID authorization. Connections go only to addresses <see cref="PushEndpointGuard"/>
/// accepted at connect time (pinned: DNS rebinding can't swap them), no proxy, no redirects, no cookies, a 10 second timeout,
/// and at most <see cref="MaxResponseBytes"/> of the answer is read (and ignored).
/// </summary>
public sealed class PushSender : IDisposable
{
    public const int MaxResponseBytes = 4096;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(1);

    private readonly HttpClient http;
    private readonly PushEndpointGuard guard;
    private readonly VapidKeys vapid;
    private readonly IOptionsMonitor<PushOptions> options;
    private readonly TimeProvider clock;

    public PushSender(PushEndpointGuard guard, VapidKeys vapid, IOptionsMonitor<PushOptions> options, TimeProvider clock)
    {
        (this.guard, this.vapid, this.options, this.clock) = (guard, vapid, options, clock);
        http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
            ConnectTimeout = Timeout, PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = ConnectAsync,
        }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ByteBench-Push/1.0");
    }

    /// <summary>Encrypts <paramref name="payload"/> for the subscription keys and posts it. Never throws for delivery problems.</summary>
    public async Task<PushResult> SendAsync(string endpoint, byte[] p256dh, byte[] auth, byte[] payload, CancellationToken ct)
    {
        if (guard.Validate(endpoint) is { } invalid) return new(PushOutcome.Failed, null, invalid);
        var uri = new Uri(endpoint);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            var body = WebPushCrypto.Encrypt(payload, p256dh, auth);
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentEncoding.Add("aes128gcm");
            request.Headers.TryAddWithoutValidation("TTL", Math.Clamp(options.CurrentValue.TtlSeconds, 0, 2419200).ToString());
            request.Headers.TryAddWithoutValidation("Urgency", "normal");
            request.Headers.TryAddWithoutValidation("Authorization", await vapid.AuthorizationAsync(uri, clock.GetUtcNow(), timeout.Token));

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            await DrainAsync(response, timeout.Token);
            var status = (int)response.StatusCode;
            if (status is >= 200 and < 300) return new(PushOutcome.Delivered, status);
            if (status is 404 or 410) return new(PushOutcome.Gone, status, "The subscription has expired or was removed.");
            if (status == 429) return new(PushOutcome.RateLimited, status, "Rate limited by the push service.", RetryAfter(response));
            return new(PushOutcome.Failed, status, $"The push service answered HTTP {status}.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(PushOutcome.Failed, null, $"Timed out after {Timeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException ex)
        {
            var blocked = ex.InnerException as PushBlockedException ?? ex.InnerException?.InnerException as PushBlockedException;
            return new(PushOutcome.Failed, null, blocked?.Message ?? "Could not connect to the push service.");
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return new(PushOutcome.Failed, null, "The subscription's keys are invalid.");
        }
    }

    private TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        var wait = ra?.Delta ?? (ra?.Date is { } d ? d - clock.GetUtcNow() : TimeSpan.FromMinutes(1));
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        return wait > MaxRetryAfter ? MaxRetryAfter : wait;
    }

    private static async Task DrainAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[1024];
        var total = 0;
        int n;
        while (total < MaxResponseBytes && (n = await stream.ReadAsync(buffer, ct)) > 0) total += n;
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await guard.ResolveAsync(context.DnsEndPoint.Host, ct);
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                last = ex;
                if (ex is OperationCanceledException) throw;
            }
        }
        throw new HttpRequestException($"Could not connect to {context.DnsEndPoint.Host}.", last);
    }

    public void Dispose() => http.Dispose();
}
