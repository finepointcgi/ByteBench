using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ByteBench.Services.Hosting.Operations;

/// <summary>Writes the Prometheus text exposition format (version 0.0.4). HELP and TYPE lines are written once per metric name.</summary>
public sealed class PrometheusWriter
{
    private readonly StringBuilder sb = new();
    private readonly HashSet<string> declared = new(StringComparer.Ordinal);

    public void Declare(string name, string type, string help)
    {
        if (!declared.Add(name)) return;
        sb.Append("# HELP ").Append(name).Append(' ').Append(help.Replace("\\", "\\\\").Replace("\n", "\\n")).Append('\n');
        sb.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
    }

    public void Sample(string name, double value, params (string Key, string Value)[] labels) => SampleRaw(name, value, labels);

    public void Sample(string name, long value, params (string Key, string Value)[] labels) => SampleRaw(name, value, labels);

    private void SampleRaw(string name, double value, (string Key, string Value)[] labels)
    {
        sb.Append(name);
        if (labels.Length > 0)
        {
            sb.Append('{');
            for (var i = 0; i < labels.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(labels[i].Key).Append("=\"").Append(Escape(labels[i].Value)).Append('"');
            }
            sb.Append('}');
        }
        sb.Append(' ').Append(Format(value)).Append('\n');
    }

    public static string Format(double v) =>
        double.IsPositiveInfinity(v) ? "+Inf" : double.IsNegativeInfinity(v) ? "-Inf" : double.IsNaN(v) ? "NaN" : v.ToString("R", CultureInfo.InvariantCulture);

    public static string Escape(string v) => v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    public void Append(PrometheusWriter other)
    {
        // Declarations already written by an earlier collector are not repeated.
        foreach (var line in other.sb.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ', 4);
                if (parts.Length >= 3 && !declaredLines.Add(parts[1] + " " + parts[2])) continue;
            }
            sb.Append(line).Append('\n');
        }
    }

    private readonly HashSet<string> declaredLines = new(StringComparer.Ordinal);

    public override string ToString() => sb.ToString();
}

/// <summary>One source of metrics. A collector that throws is skipped and counted in <c>bytebench_scrape_collector_errors_total</c>.</summary>
public interface IMetricCollector
{
    /// <summary>A short fixed name (it becomes the <c>collector</c> label).</summary>
    string Name { get; }
    Task CollectAsync(PrometheusWriter w, CancellationToken ct);
}

/// <summary>Maps a request path to one of a fixed set of groups, so no path, id, project key or user name reaches a label.</summary>
public static class RouteGroups
{
    public static readonly string[] All = ["web", "api", "git", "lfs", "registry", "pages", "mcp", "scim", "saml", "oauth", "other"];

    public static string Of(PathString path)
    {
        var p = path.Value ?? "/";
        if (p.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || p.Equals("/api", StringComparison.OrdinalIgnoreCase)) return "api";
        if (p.StartsWith("/git/", StringComparison.OrdinalIgnoreCase))
            return p.Contains(".git/info/lfs", StringComparison.OrdinalIgnoreCase) || p.Contains("/objects/batch", StringComparison.OrdinalIgnoreCase) ? "lfs" : "git";
        if (p.StartsWith("/packages/", StringComparison.OrdinalIgnoreCase) || p.StartsWith("/v2/", StringComparison.OrdinalIgnoreCase) || p.Equals("/v2", StringComparison.OrdinalIgnoreCase)) return "registry";
        if (p.StartsWith("/pages/", StringComparison.OrdinalIgnoreCase)) return "pages";
        if (p.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase)) return "mcp";
        if (p.StartsWith("/scim/", StringComparison.OrdinalIgnoreCase)) return "scim";
        if (p.StartsWith("/saml/", StringComparison.OrdinalIgnoreCase)) return "saml";
        if (p.StartsWith("/oauth/", StringComparison.OrdinalIgnoreCase) || p.StartsWith("/.well-known/oauth", StringComparison.OrdinalIgnoreCase)) return "oauth";
        if (p.StartsWith("/health", StringComparison.OrdinalIgnoreCase) || p.StartsWith("/readyz", StringComparison.OrdinalIgnoreCase) || p.StartsWith("/metrics", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("/ci/", StringComparison.OrdinalIgnoreCase) || p.StartsWith("/integrations/", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("/service-desk/", StringComparison.OrdinalIgnoreCase) || p.StartsWith("/.well-known/", StringComparison.OrdinalIgnoreCase)) return "other";
        return "web";
    }

    public static string MethodClass(string method) => method switch
    {
        "GET" or "HEAD" or "OPTIONS" => "read",
        "POST" or "PUT" or "PATCH" or "DELETE" => "write",
        _ => "other",
    };

    public static string StatusClass(int status) => status is >= 100 and < 600 ? $"{status / 100}xx" : "other";
}

/// <summary>Request counters and a duration histogram, keyed only by method class, route group and status class.</summary>
public sealed class HttpMetrics
{
    public static readonly double[] Buckets = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];

    private readonly ConcurrentDictionary<(string Method, string Group, string Status), long> requests = new();
    private readonly ConcurrentDictionary<string, Histogram> durations = new();

    private sealed class Histogram
    {
        public readonly long[] Counts = new long[Buckets.Length + 1]; // the last slot is +Inf
        public long Total;
        public double Sum;
        public readonly object Gate = new();
    }

    public void Observe(string method, string group, int status, double seconds, bool timed)
    {
        requests.AddOrUpdate((RouteGroups.MethodClass(method), group, RouteGroups.StatusClass(status)), 1, (_, n) => n + 1);
        if (!timed) return;
        var h = durations.GetOrAdd(group, _ => new Histogram());
        var i = Array.FindIndex(Buckets, b => seconds <= b);
        lock (h.Gate) { h.Counts[i < 0 ? Buckets.Length : i]++; h.Total++; h.Sum += seconds; }
    }

    public void Write(PrometheusWriter w)
    {
        w.Declare("bytebench_http_requests_total", "counter", "HTTP requests by method class (read/write/other), route group and status class.");
        foreach (var (k, n) in requests.OrderBy(x => x.Key.Group).ThenBy(x => x.Key.Method).ThenBy(x => x.Key.Status))
            w.Sample("bytebench_http_requests_total", n, ("method_class", k.Method), ("route_group", k.Group), ("status_class", k.Status));

        w.Declare("bytebench_http_request_duration_seconds", "histogram", "HTTP request duration in seconds by route group (WebSocket and circuit requests excluded).");
        foreach (var (group, h) in durations.OrderBy(x => x.Key))
        {
            long[] counts; long total; double sum;
            lock (h.Gate) { counts = (long[])h.Counts.Clone(); total = h.Total; sum = h.Sum; }
            long cumulative = 0;
            for (var i = 0; i < Buckets.Length; i++)
            {
                cumulative += counts[i];
                w.Sample("bytebench_http_request_duration_seconds_bucket", cumulative, ("route_group", group), ("le", PrometheusWriter.Format(Buckets[i])));
            }
            w.Sample("bytebench_http_request_duration_seconds_bucket", total, ("route_group", group), ("le", "+Inf"));
            w.Sample("bytebench_http_request_duration_seconds_sum", sum, ("route_group", group));
            w.Sample("bytebench_http_request_duration_seconds_count", total, ("route_group", group));
        }
    }
}

/// <summary>Counts every request (also the ones that end in an exception) by route group. Runs early in the pipeline.</summary>
public sealed class HttpMetricsMiddleware(RequestDelegate next, HttpMetrics metrics, Microsoft.Extensions.Options.IOptionsMonitor<MetricsOptions> options)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!options.CurrentValue.Enabled) { await next(ctx); return; }
        var start = Stopwatch.GetTimestamp();
        var failed = false;
        try { await next(ctx); }
        catch { failed = true; throw; }
        finally
        {
            var timed = !ctx.WebSockets.IsWebSocketRequest && !ctx.Request.Path.StartsWithSegments("/_blazor");
            metrics.Observe(ctx.Request.Method, RouteGroups.Of(ctx.Request.Path), failed ? 500 : ctx.Response.StatusCode,
                Stopwatch.GetElapsedTime(start).TotalSeconds, timed);
        }
    }
}
