using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Hosting.Operations;
using ByteBench.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ByteBench.Tests;

/// <summary>Metrics on, a token, an allow-list of 10.1.0.0/16, a fake remote address (X-Test-Remote) and a collector that always throws.</summary>
public class OpsApp : TestApp
{
    public const string Token = "test-metrics-token-0123456789";

    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["Metrics:Enabled"] = "true";
        settings["Metrics:Token"] = Token;
        settings["Metrics:AllowedNetworks:0"] = "10.1.0.0/16";
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IMetricCollector, ThrowingCollector>();
        services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, FakeRemoteFilter>();
    }

    private sealed class ThrowingCollector : IMetricCollector
    {
        public string Name => "broken";
        public Task CollectAsync(PrometheusWriter w, CancellationToken ct)
        {
            w.Declare("bytebench_half_written", "gauge", "never shown");
            w.Sample("bytebench_half_written", 1);
            throw new InvalidOperationException("Password=hunter2 secret");
        }
    }

    private sealed class FakeRemoteFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, n) =>
            {
                if (ctx.Request.Headers.TryGetValue("X-Test-Remote", out var v) && IPAddress.TryParse(v.ToString(), out var ip))
                    ctx.Connection.RemoteIpAddress = ip;
                return n(ctx);
            });
            next(app);
        };
    }
}

/// <summary>Metrics left at their default (off).</summary>
public class OpsDefaultApp : TestApp { }

/// <summary>Needs an absurd amount of free disk, so readiness fails on "disk" only.</summary>
public class OpsLowDiskApp : TestApp
{
    protected override void AddSettings(IDictionary<string, string?> settings) => settings["Health:MinFreeDiskMb"] = "999999999999";
}

public class HealthEndpointTests(OpsDefaultApp app) : IClassFixture<OpsDefaultApp>
{
    [Fact]
    public async Task Healthz_is_ok_without_cookies_and_not_cached()
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        var r = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("ok", await r.Content.ReadAsStringAsync());
        Assert.Contains("no-store", r.Headers.CacheControl!.ToString());
        Assert.False(r.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Readyz_reports_every_check_ok()
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        var r = await client.GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("no-store", r.Headers.CacheControl!.ToString());
        Assert.False(r.Headers.Contains("Set-Cookie"));
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        var names = doc.RootElement.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(["database", "migrations", "storage", "disk", "keys"], names);
        Assert.All(doc.RootElement.GetProperty("checks").EnumerateArray(), c => Assert.Equal("ok", c.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task Failing_database_is_reported_by_name_only()
    {
        await using var s = app.Scope();
        var probe = new ReadinessProbe(new ThrowingFactory(), s.Get<RepoStorage>(), s.Get<IDataProtectionProvider>(),
            s.Get<Microsoft.Extensions.Configuration.IConfiguration>(), NullLogger<ReadinessProbe>.Instance);
        var checks = await probe.RunAsync(CancellationToken.None);
        Assert.False(checks.Single(c => c.Name == "database").Ok);
        Assert.False(checks.Single(c => c.Name == "migrations").Ok);
        Assert.True(checks.Single(c => c.Name == "storage").Ok);
        Assert.True(checks.Single(c => c.Name == "keys").Ok);
    }

    private sealed class ThrowingFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => throw new InvalidOperationException("Host=db;Password=hunter2 is down");
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct = default) => throw new InvalidOperationException("Host=db;Password=hunter2 is down");
    }
}

public class HealthLowDiskTests(OpsLowDiskApp app) : IClassFixture<OpsLowDiskApp>
{
    [Fact]
    public async Task Low_disk_gives_503_naming_only_the_failed_check()
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var r = await client.GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("unavailable", doc.RootElement.GetProperty("status").GetString());
        var failed = doc.RootElement.GetProperty("checks").EnumerateArray().Where(c => c.GetProperty("status").GetString() == "failed")
            .Select(c => c.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(["disk"], failed);
        Assert.DoesNotContain(app.DataRoot, body);
        Assert.DoesNotContain("Exception", body);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode); // liveness has no dependencies
    }
}

public class MetricsDisabledTests(OpsDefaultApp app) : IClassFixture<OpsDefaultApp>
{
    [Fact]
    public async Task Disabled_by_default_is_404_even_with_a_token()
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/metrics")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "anything");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/metrics")).StatusCode);
    }
}

public class MetricsTests(OpsApp app) : IClassFixture<OpsApp>
{
    private HttpClient Client() => app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    private static HttpRequestMessage Scrape(string? token = null, string? remote = null, string? forwardedFor = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (remote != null) req.Headers.Add("X-Test-Remote", remote);
        if (forwardedFor != null) req.Headers.Add("X-Forwarded-For", forwardedFor);
        return req;
    }

    [Fact]
    public async Task Missing_or_wrong_token_is_401()
    {
        var c = Client();
        var none = await c.SendAsync(Scrape());
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Contains("no-store", none.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Scrape("wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Scrape(OpsApp.Token + "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Scrape(remote: "192.168.1.5"))).StatusCode); // outside the allow-list
    }

    [Fact]
    public async Task Network_allow_list_admits_without_a_token()
    {
        var c = Client();
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Scrape(remote: "10.1.200.3"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Scrape(remote: "10.2.0.1"))).StatusCode);
        // A surviving X-Forwarded-For means the remote address is a proxy's: the allow-list no longer vouches for the client.
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Scrape(remote: "10.1.200.3", forwardedFor: "203.0.113.9"))).StatusCode);
    }

    [Fact]
    public async Task Right_token_returns_parseable_prometheus_text()
    {
        var r = await Client().SendAsync(Scrape(OpsApp.Token));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.StartsWith("text/plain", r.Content.Headers.ContentType!.ToString());
        Assert.Contains("version=0.0.4", r.Content.Headers.ContentType!.ToString());
        Assert.Contains("no-store", r.Headers.CacheControl!.ToString());
        var samples = PromText.Parse(await r.Content.ReadAsStringAsync());
        Assert.Contains(samples, s => s.Name == "bytebench_build_info" && s.Labels.ContainsKey("version"));
        Assert.Contains(samples, s => s.Name == "bytebench_db_up" && s.Value == 1);
        Assert.Contains(samples, s => s.Name == "bytebench_process_uptime_seconds");
        Assert.Contains(samples, s => s.Name == "bytebench_queue_depth" && s.Labels["queue"] == "ci_pipelines");
        Assert.Contains(samples, s => s.Name == "bytebench_users" && s.Value > 0);
    }

    [Fact]
    public async Task Labels_never_carry_paths_keys_or_user_names()
    {
        var web = await app.CookieClientAsync("alice");
        await web.GetAsync("/p/WEB/issues");
        await web.GetAsync("/u/alice");
        var api = await app.ApiClientAsync("alice");
        await api.GetAsync("/api/v1/projects/WEB");
        await Client().GetAsync("/git/WEB/nope.git/info/refs?service=git-upload-pack");
        await Client().GetAsync("/git/WEB/nope.git/info/lfs/objects/batch");

        var text = await (await Client().SendAsync(Scrape(OpsApp.Token))).Content.ReadAsStringAsync();
        var samples = PromText.Parse(text);
        var http = samples.Where(s => s.Name == "bytebench_http_requests_total").ToList();
        Assert.NotEmpty(http);
        var groups = new HashSet<string>(RouteGroups.All);
        Assert.All(http, s =>
        {
            Assert.Equal(["method_class", "route_group", "status_class"], s.Labels.Keys.OrderBy(k => k).ToArray());
            Assert.Contains(s.Labels["route_group"], groups);
            Assert.Matches("^[1-5]xx$", s.Labels["status_class"]);
        });
        Assert.Contains(http, s => s.Labels["route_group"] == "api");
        Assert.Contains(http, s => s.Labels["route_group"] == "git");
        Assert.Contains(http, s => s.Labels["route_group"] == "lfs");
        Assert.Contains(samples, s => s.Name == "bytebench_http_request_duration_seconds_bucket" && s.Labels["le"] == "+Inf");
        Assert.DoesNotContain("WEB", text);
        Assert.DoesNotContain("alice", text);
        Assert.DoesNotContain("/p/", text);
        Assert.DoesNotContain("nope", text);
    }

    [Fact]
    public async Task A_failing_collector_is_skipped_and_counted_without_leaking()
    {
        var r = await Client().SendAsync(Scrape(OpsApp.Token));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var text = await r.Content.ReadAsStringAsync();
        var samples = PromText.Parse(text);
        Assert.Contains(samples, s => s.Name == "bytebench_scrape_collector_errors_total" && s.Labels["collector"] == "broken" && s.Value >= 1);
        Assert.Contains(samples, s => s.Name == "bytebench_scrape_collector_errors_total" && s.Labels["collector"] == "process" && s.Value == 0);
        Assert.Contains(samples, s => s.Name == "bytebench_db_up"); // the others still ran
        Assert.DoesNotContain("half_written", text);
        Assert.DoesNotContain("hunter2", text);
    }

    [Fact]
    public async Task Worker_registry_shows_up_in_the_scrape()
    {
        WorkerRegistry.Instance.Report("ops-test-worker", true);
        WorkerRegistry.Instance.Tick("ops-test-worker");
        WorkerRegistry.Instance.Failure("ops-test-worker", new InvalidOperationException("boom"));
        var samples = PromText.Parse(await (await Client().SendAsync(Scrape(OpsApp.Token))).Content.ReadAsStringAsync());
        Assert.Contains(samples, s => s.Name == "bytebench_worker_last_run_timestamp_seconds" && s.Labels["worker"] == "ops-test-worker" && s.Value > 1_700_000_000);
        Assert.Contains(samples, s => s.Name == "bytebench_worker_errors_total" && s.Labels["worker"] == "ops-test-worker" && s.Value >= 1);
    }

    [Fact]
    public void Network_matching_handles_cidr_single_addresses_ipv6_and_garbage()
    {
        Assert.True(MetricsEndpoint.InAllowedNetwork(IPAddress.Parse("127.0.0.1"), ["127.0.0.0/8"]));
        Assert.True(MetricsEndpoint.InAllowedNetwork(IPAddress.Parse("::ffff:127.0.0.1"), ["127.0.0.0/8"]));
        Assert.True(MetricsEndpoint.InAllowedNetwork(IPAddress.IPv6Loopback, ["::1/128"]));
        Assert.True(MetricsEndpoint.InAllowedNetwork(IPAddress.Parse("192.168.4.77"), ["192.168.4.64/26"]));
        Assert.False(MetricsEndpoint.InAllowedNetwork(IPAddress.Parse("192.168.4.200"), ["192.168.4.64/26"]));
        Assert.True(MetricsEndpoint.InAllowedNetwork(IPAddress.Parse("10.0.0.9"), ["10.0.0.9"]));
        Assert.False(MetricsEndpoint.InAllowedNetwork(null, ["0.0.0.0/0"]));
        Assert.False(MetricsEndpoint.InAllowedNetwork(IPAddress.Parse("10.0.0.9"), ["nonsense", "10.0.0.0/99", ""]));
        Assert.False(MetricsEndpoint.InAllowedNetwork(IPAddress.Parse("10.0.0.9"), ["::1/128"]));
    }

    [Fact]
    public void Route_groups_are_a_fixed_set()
    {
        Assert.Equal("api", RouteGroups.Of("/api/v1/projects/ABC"));
        Assert.Equal("git", RouteGroups.Of("/git/ABC/repo.git/info/refs"));
        Assert.Equal("lfs", RouteGroups.Of("/git/ABC/repo.git/info/lfs/objects/batch"));
        Assert.Equal("registry", RouteGroups.Of("/packages/ABC/nuget/index.json"));
        Assert.Equal("registry", RouteGroups.Of("/v2/"));
        Assert.Equal("pages", RouteGroups.Of("/pages/abc/site"));
        Assert.Equal("mcp", RouteGroups.Of("/mcp"));
        Assert.Equal("scim", RouteGroups.Of("/scim/v2/Users"));
        Assert.Equal("saml", RouteGroups.Of("/saml/acs"));
        Assert.Equal("oauth", RouteGroups.Of("/oauth/token"));
        Assert.Equal("other", RouteGroups.Of("/healthz"));
        Assert.Equal("web", RouteGroups.Of("/p/ABC/issues/12"));
        Assert.All(new[] { "/", "/x/y", "/api/v1/z", "/git/a/b" }, p => Assert.Contains(RouteGroups.Of(p), RouteGroups.All));
    }
}

public class WorkerRegistryTests
{
    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Registry_tracks_ticks_successes_and_failures()
    {
        var time = new FakeTime(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var reg = new WorkerRegistry(time);
        reg.Report("mirror", true);
        reg.Report("backup", false);
        time.Now = time.Now.AddSeconds(10);
        reg.Tick("mirror");
        time.Now = time.Now.AddSeconds(5);
        reg.Success("mirror");
        time.Now = time.Now.AddSeconds(5);
        reg.Failure("mirror", new IOException("disk gone"));

        var mirror = reg.Get("mirror")!;
        Assert.True(mirror.Enabled);
        Assert.Equal(1, mirror.Ticks);
        Assert.Equal(1, mirror.Errors);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 15, DateTimeKind.Utc), mirror.LastSuccessUtc);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 20, DateTimeKind.Utc), mirror.LastErrorUtc);
        Assert.Equal("IOException: disk gone", mirror.LastError);
        Assert.False(reg.Get("backup")!.Enabled);
        Assert.Null(reg.Get("backup")!.LastTickUtc);
        Assert.Equal(["backup", "mirror"], reg.Snapshot().Select(s => s.Name).ToArray());
        reg.Tick("never-reported"); // a worker that skipped Report still appears
        Assert.NotNull(reg.Get("never-reported"));
    }

    [Fact]
    public void Error_text_is_short_and_scrubbed()
    {
        var token = "ghp_" + new string('a', 36);
        var ex = new InvalidOperationException($"Failed https://user:pw123@host/x?token=abc123 with Bearer {new string('Z', 40)} and password=hunter2 {token}\nsecond line with stack");
        var text = WorkerRegistry.Describe(ex);
        Assert.StartsWith("InvalidOperationException:", text);
        Assert.DoesNotContain("pw123", text);
        Assert.DoesNotContain("abc123", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain(token, text);
        Assert.DoesNotContain("second line", text);
        Assert.True(WorkerRegistry.Describe(new Exception(new string('x', 500) + " y y")).Length < 230);
    }

    [Fact]
    public void Overview_lists_catalogue_workers_even_when_not_started_and_shows_queue_depth()
    {
        var reg = new WorkerRegistry();
        reg.Report("ci-runner", true);
        reg.Success("ci-runner");
        reg.Report("custom-thing", false);
        var q = QueueDepths.Empty with { CiQueued = 3, CiRunning = 1 };
        var rows = WorkersOverview.Build(reg.Snapshot(), q);
        var ci = rows.Single(r => r.Id == "ci-runner");
        Assert.True(ci.Reported && ci.Enabled);
        Assert.Equal("3 queued, 1 running", ci.Queue);
        Assert.NotNull(ci.LastSuccessUtc);
        var mirror = rows.Single(r => r.Id == "mirror");
        Assert.False(mirror.Reported);
        Assert.Equal("0 due", mirror.Queue);
        Assert.Contains(rows, r => r.Id == "custom-thing" && !r.Known && !r.Enabled);
    }

    [Fact]
    public async Task Ttl_cache_loads_once_per_window()
    {
        var calls = 0;
        var time = new FakeTime(DateTimeOffset.UnixEpoch);
        var cache = new TtlCache<int>(TimeSpan.FromSeconds(15), _ => Task.FromResult(++calls), time);
        Assert.Equal(1, await cache.GetAsync());
        Assert.Equal(1, await cache.GetAsync());
        time.Now = time.Now.AddSeconds(16);
        Assert.Equal(2, await cache.GetAsync());
    }
}

public class WorkersPageAuthorizationTests(OpsDefaultApp app) : IClassFixture<OpsDefaultApp>
{
    [Fact]
    public async Task Only_site_admins_see_the_workers_page()
    {
        var alice = await app.CookieClientAsync("alice"); // admin
        var page = await alice.GetStringAsync("/admin/system/workers");
        Assert.Contains("Workers and queues", page);
        Assert.Contains("wk-row-ci-runner", page);
        Assert.Contains("/admin/system/workers", await alice.GetStringAsync("/admin/system"));

        var bob = await app.CookieClientAsync("bob");
        var denied = await bob.GetStringAsync("/admin/system/workers");
        Assert.Contains("Only site administrators", denied);
        Assert.DoesNotContain("wk-row-", denied);

        var anonymous = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var anon = await anonymous.GetAsync("/admin/system/workers");
        Assert.DoesNotContain("wk-row-", await anon.Content.ReadAsStringAsync());
    }
}

/// <summary>A deliberately small parser for the text exposition format 0.0.4, strict enough to catch malformed output.</summary>
internal static class PromText
{
    internal sealed record Sample(string Name, Dictionary<string, string> Labels, double Value);

    private static readonly Regex Line = new(@"^([a-zA-Z_:][a-zA-Z0-9_:]*)(?:\{(.*)\})?\s+(\S+)$", RegexOptions.Compiled);
    private static readonly Regex Label = new(@"([a-zA-Z_][a-zA-Z0-9_]*)=""((?:[^""\\]|\\.)*)""\s*(?:,|$)", RegexOptions.Compiled);

    public static List<Sample> Parse(string text)
    {
        var types = new Dictionary<string, string>();
        var helps = new HashSet<string>();
        var samples = new List<Sample>();
        foreach (var raw in text.Split('\n'))
        {
            if (raw.Length == 0) continue;
            if (raw.StartsWith("# HELP ")) { helps.Add(raw.Split(' ', 4)[2]); continue; }
            if (raw.StartsWith("# TYPE "))
            {
                var p = raw.Split(' ', 4);
                Assert.Contains(p[3], new[] { "counter", "gauge", "histogram", "summary", "untyped" });
                Assert.True(types.TryAdd(p[2], p[3]), $"duplicate TYPE for {p[2]}");
                continue;
            }
            Assert.False(raw.StartsWith("#"), $"unknown comment: {raw}");
            var m = Line.Match(raw);
            Assert.True(m.Success, $"not a sample line: {raw}");
            var labels = new Dictionary<string, string>();
            var body = m.Groups[2].Value;
            var consumed = 0;
            foreach (Match l in Label.Matches(body)) { labels[l.Groups[1].Value] = l.Groups[2].Value; consumed += l.Length; }
            Assert.Equal(body.Length, consumed);
            var value = m.Groups[3].Value;
            var number = value == "+Inf" ? double.PositiveInfinity : double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            var name = m.Groups[1].Value;
            Assert.StartsWith("bytebench_", name);
            var family = Regex.Replace(name, "_(bucket|sum|count)$", "");
            Assert.True(types.ContainsKey(name) || types.ContainsKey(family), $"no TYPE for {name}");
            Assert.True(helps.Contains(name) || helps.Contains(family), $"no HELP for {name}");
            samples.Add(new(name, labels, number));
        }
        return samples;
    }
}
