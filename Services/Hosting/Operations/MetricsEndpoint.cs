using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Hosting.Operations;

/// <summary><c>Metrics:*</c>. Off by default.</summary>
public sealed class MetricsOptions
{
    public bool Enabled { get; set; }
    /// <summary>Bearer token for scrapers (from configuration or user secrets; never logged). Empty: only the network allow-list admits.</summary>
    public string? Token { get; set; }
    /// <summary>CIDR ranges (or single addresses) admitted without a token. Default: loopback only.</summary>
    public string[] AllowedNetworks { get; set; } = ["127.0.0.0/8", "::1/128"];
}

public sealed class MetricsScrape(IEnumerable<IMetricCollector> collectors, ILogger<MetricsScrape> log)
{
    private readonly ConcurrentErrors errors = new();

    private sealed class ConcurrentErrors
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> map = new();
        public void Add(string name) => map.AddOrUpdate(name, 1, (_, n) => n + 1);
        public long Get(string name) => map.TryGetValue(name, out var n) ? n : 0;
    }

    /// <summary>Runs every collector; one that throws or takes over 5 s is skipped and counted.</summary>
    public async Task<string> RenderAsync(CancellationToken ct)
    {
        var w = new PrometheusWriter();
        foreach (var c in collectors)
        {
            var part = new PrometheusWriter(); // a collector that fails halfway leaves nothing behind
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                await c.CollectAsync(part, cts.Token);
                w.Append(part);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                errors.Add(c.Name);
                log.LogWarning("Metrics collector {Collector} failed ({Type})", c.Name, ex.GetType().Name);
            }
        }
        w.Declare("bytebench_scrape_collector_errors_total", "counter", "Collectors skipped during a scrape because they failed, by collector.");
        foreach (var c in collectors) w.Sample("bytebench_scrape_collector_errors_total", errors.Get(c.Name), ("collector", c.Name));
        return w.ToString();
    }
}

public static class MetricsEndpoint
{
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    public static IEndpointRouteBuilder MapByteBenchMetrics(this IEndpointRouteBuilder app)
    {
        app.MapGet("/metrics", async (HttpContext ctx, IOptionsMonitor<MetricsOptions> options, MetricsScrape scrape) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            if (ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>() is { } pages) pages.Enabled = false;
            var o = options.CurrentValue;
            if (!o.Enabled) return Results.StatusCode(StatusCodes.Status404NotFound);
            if (!Authorized(ctx, o))
            {
                ctx.Response.Headers.WWWAuthenticate = "Bearer";
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }
            var text = await scrape.RenderAsync(ctx.RequestAborted);
            return Results.Text(text, ContentType);
        }).AllowAnonymous().DisableRateLimiting().ExcludeFromDescription();
        return app;
    }

    public static bool Authorized(HttpContext ctx, MetricsOptions o)
    {
        if (!string.IsNullOrEmpty(o.Token) && ctx.Request.Headers.Authorization.ToString() is { Length: > 7 } auth
            && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && TokenEquals(auth[7..].Trim(), o.Token)) return true;
        // An X-Forwarded-For that survived the forwarded-headers middleware names hops we do not trust: the remote address then
        // says nothing about the real client, so the network allow-list cannot admit the request (the token still can).
        if (ctx.Request.Headers.ContainsKey("X-Forwarded-For")) return false;
        return InAllowedNetwork(ctx.Connection.RemoteIpAddress, o.AllowedNetworks);
    }

    public static bool TokenEquals(string presented, string expected) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(presented)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    public static bool InAllowedNetwork(IPAddress? address, IEnumerable<string> networks)
    {
        if (address == null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        foreach (var n in networks)
        {
            var text = n.Trim();
            if (text.Length == 0) continue;
            var slash = text.IndexOf('/');
            var ipText = slash < 0 ? text : text[..slash];
            if (!IPAddress.TryParse(ipText, out var net)) continue;
            if (net.IsIPv4MappedToIPv6) net = net.MapToIPv4();
            var bits = net.GetAddressBytes().Length * 8;
            var prefix = slash < 0 ? bits : int.TryParse(text[(slash + 1)..], out var p) && p >= 0 && p <= bits ? p : -1;
            if (prefix < 0 || net.AddressFamily != address.AddressFamily) continue;
            if (Matches(address.GetAddressBytes(), net.GetAddressBytes(), prefix)) return true;
        }
        return false;
    }

    private static bool Matches(byte[] a, byte[] b, int prefix)
    {
        var full = prefix / 8;
        for (var i = 0; i < full; i++) if (a[i] != b[i]) return false;
        var rest = prefix % 8;
        if (rest == 0) return true;
        var mask = (byte)(0xFF << (8 - rest));
        return (a[full] & mask) == (b[full] & mask);
    }
}
