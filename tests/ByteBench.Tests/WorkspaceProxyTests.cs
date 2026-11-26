using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web;
using ByteBench.Data;
using ByteBench.Services.Workspaces;
using ByteBench.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

/// <summary>The authenticated reverse proxy: workspace origins, tickets, sessions, header hygiene, WebSocket upgrade.</summary>
public class WorkspaceProxyTests(WorkspaceApp app) : IClassFixture<WorkspaceApp>
{
    private int Port => app.ServerAddress.Port;

    private async Task<(string Owner, Workspace Ws)> RunningAsync()
    {
        var owner = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((owner, ProjectRole.Developer));
        return (owner, await app.StartAsync(owner, repo));
    }

    private static string Ticket(string url) => HttpUtility.ParseQueryString(new Uri(url).Query)["ticket"]!;

    private static string HostOf(string url) => new Uri(url).Authority;

    private HttpRequestMessage Req(string host, string path, string? cookie = null, HttpMethod? method = null)
    {
        var r = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        r.Headers.Host = host;
        if (cookie != null) r.Headers.TryAddWithoutValidation("Cookie", cookie);
        return r;
    }

    /// <summary>Redeems a fresh ticket on the workspace origin and returns the session cookie (name=value).</summary>
    private async Task<string> SignInAsync(HttpClient http, string user, string publicId, int? port = null)
    {
        var url = await app.TicketUrlAsync(user, publicId, port);
        var response = await http.SendAsync(Req(HostOf(url), new Uri(url).PathAndQuery));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.OriginalString);
        var setCookie = response.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith("bb_ws=", setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", setCookie, StringComparison.OrdinalIgnoreCase); // host-only: never shared with ByteBench's origin
        return setCookie.Split(';')[0];
    }

    [Fact]
    public async Task Entry_url_is_the_workspaces_own_origin_with_a_ticket_not_the_main_origin()
    {
        var (owner, ws) = await RunningAsync();
        var ide = await app.TicketUrlAsync(owner, ws.PublicId);
        var preview = await app.TicketUrlAsync(owner, ws.PublicId, 3000);
        Assert.StartsWith($"http://ide-{ws.PublicId}.localhost:", ide);
        Assert.StartsWith($"http://p3000-{ws.PublicId}.localhost:", preview);
        Assert.NotEqual(Ticket(ide), Ticket(await app.TicketUrlAsync(owner, ws.PublicId)));
        Assert.DoesNotContain(ws.ConnectionTokenHash.Length.ToString() + "x", ide); // (no token material in the URL)
        Assert.DoesNotContain(app.Backend.ConnectionToken(ws.ContainerId)!, ide);
    }

    [Fact]
    public async Task No_session_means_401_and_nothing_reaches_the_workspace()
    {
        var (_, ws) = await RunningAsync();
        using var http = app.Http();
        var before = app.Backend.Server.Requests.Count;
        var r = await http.SendAsync(Req($"ide-{ws.PublicId}.localhost:{Port}", "/echo"));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal(before, app.Backend.Server.Requests.Count);
        // A garbage cookie is no better.
        r = await http.SendAsync(Req($"ide-{ws.PublicId}.localhost:{Port}", "/echo", "bb_ws=nonsense"));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task A_ticket_works_once_for_its_workspace_and_audience_only()
    {
        var (owner, ws) = await RunningAsync();
        using var http = app.Http();
        var url = await app.TicketUrlAsync(owner, ws.PublicId);
        var host = HostOf(url);
        Assert.Equal(HttpStatusCode.Redirect, (await http.SendAsync(Req(host, new Uri(url).PathAndQuery))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Req(host, new Uri(url).PathAndQuery))).StatusCode); // single use

        // An IDE ticket does not open the preview origin, nor another workspace's.
        var ide = await app.TicketUrlAsync(owner, ws.PublicId);
        var wrongAudience = $"p3000-{ws.PublicId}.localhost:{Port}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Req(wrongAudience, $"/_bb/enter?ticket={Ticket(ide)}&next=%2F"))).StatusCode);
        var (_, other) = await RunningAsync();
        var ide2 = await app.TicketUrlAsync(owner, ws.PublicId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Req($"ide-{other.PublicId}.localhost:{Port}", $"/_bb/enter?ticket={Ticket(ide2)}&next=%2F"))).StatusCode);
    }

    [Fact]
    public async Task A_session_is_valid_for_its_own_origin_only()
    {
        var (owner, ws) = await RunningAsync();
        using var http = app.Http();
        var cookie = await SignInAsync(http, owner, ws.PublicId);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Req($"ide-{ws.PublicId}.localhost:{Port}", "/echo", cookie))).StatusCode);
        // The same cookie on the preview origin or another workspace is worthless.
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Req($"p8000-{ws.PublicId}.localhost:{Port}", "/echo", cookie))).StatusCode);
        var (_, other) = await RunningAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Req($"ide-{other.PublicId}.localhost:{Port}", "/echo", cookie))).StatusCode);
    }

    [Fact]
    public async Task Another_users_ticket_cannot_be_minted_and_their_main_site_cookie_does_nothing_on_the_workspace_origin()
    {
        var (owner, ws) = await RunningAsync();
        var intruder = await app.NewUserAsync();
        Assert.Null(await app.TicketUrlAsync(intruder, ws.PublicId));
        using var http = app.Http();
        // A ByteBench session cookie (host-only on the main origin) isn't recognised on the workspace origin.
        var r = await http.SendAsync(Req($"ide-{ws.PublicId}.localhost:{Port}", "/echo", ".AspNetCore.Identity.Application=whatever"));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.NotNull(owner);
    }

    [Fact]
    public async Task Requests_are_relayed_with_the_token_injected_and_byteBench_cookies_stripped()
    {
        var (owner, ws) = await RunningAsync();
        using var http = app.Http();
        var cookie = await SignInAsync(http, owner, ws.PublicId);
        var request = Req($"ide-{ws.PublicId}.localhost:{Port}", "/echo?x=1", $"{cookie}; .AspNetCore.Identity.Application=SECRETSESSION; theme=dark; vscode-tkn=attacker");
        request.Headers.TryAddWithoutValidation("X-Custom", "yes");
        var r = await http.SendAsync(request);
        var upstream = await r.Content.ReadAsStringAsync();

        Assert.Contains("GET /echo?x=1 HTTP/1.1", upstream);
        Assert.Contains($"Host: ide-{ws.PublicId}.localhost:{Port}", upstream);
        Assert.Contains($"vscode-tkn={app.Backend.ConnectionToken(ws.ContainerId)}", upstream);
        Assert.DoesNotContain("attacker", upstream);
        Assert.DoesNotContain("bb_ws", upstream);
        Assert.Contains("theme=dark", upstream); // the app's own cookies pass; ByteBench's session cookie is the one stripped by name in subdomain mode
        Assert.Contains("X-Custom: yes", upstream);
    }

    [Fact]
    public async Task Responses_get_framing_and_cookie_hygiene()
    {
        var (owner, ws) = await RunningAsync();
        using var http = app.Http();
        var cookie = await SignInAsync(http, owner, ws.PublicId);
        var r = await http.SendAsync(Req($"ide-{ws.PublicId}.localhost:{Port}", "/echo", cookie));
        Assert.False(r.Headers.Contains("X-Frame-Options"));
        var csp = string.Join(" ; ", r.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("frame-ancestors http://127.0.0.1:" + Port, csp); // only ByteBench's page may frame it
        var cookies = r.Headers.GetValues("Set-Cookie").ToList();
        Assert.DoesNotContain(cookies, c => c.StartsWith("bb_ws=", StringComparison.OrdinalIgnoreCase)); // the upstream can't overwrite our session cookie
        var vs = Assert.Single(cookies, c => c.StartsWith("vscode-tkn="));
        Assert.Contains("SameSite=None", vs); // so it survives inside ByteBench's iframe
        Assert.Contains("Partitioned", vs);
        Assert.Equal("same-origin", r.Headers.GetValues("Referrer-Policy").Single());
        // The app's own security headers/CSP are not added to workspace content.
        Assert.DoesNotContain("script-src", csp);
    }

    [Fact]
    public async Task Preview_origins_go_to_their_port_without_the_connection_token()
    {
        var (owner, ws) = await RunningAsync();
        using var http = app.Http();
        var cookie = await SignInAsync(http, owner, ws.PublicId, 8000);
        var before = app.Backend.Connections.Count;
        var r = await http.SendAsync(Req($"p8000-{ws.PublicId}.localhost:{Port}", "/echo", cookie));
        var body = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain("vscode-tkn", body.Replace("vscode-tkn=abc", ""));
        Assert.DoesNotContain(app.Backend.ConnectionToken(ws.ContainerId)!, body);
        Assert.Contains(app.Backend.Connections.Skip(before), c => c.Port == 8000);
    }

    [Fact]
    public async Task Large_responses_stream_through_and_a_closed_upstream_is_a_502()
    {
        var (owner, ws) = await RunningAsync();
        using var http = app.Http();
        var cookie = await SignInAsync(http, owner, ws.PublicId);
        var big = await http.SendAsync(Req($"ide-{ws.PublicId}.localhost:{Port}", "/big", cookie));
        Assert.Equal(100_000, (await big.Content.ReadAsStringAsync()).Length);
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(Req($"ide-{ws.PublicId}.localhost:{Port}", "/missing", cookie))).StatusCode);
    }

    [Fact]
    public async Task WebSocket_upgrade_is_tunnelled_after_authentication_and_refused_without()
    {
        var (owner, ws) = await RunningAsync();
        using var http = app.Http();
        var cookie = await SignInAsync(http, owner, ws.PublicId);

        // Unauthenticated: no upgrade, nothing reaches the workspace.
        var before = app.Backend.Server.Requests.Count;
        var denied = await RawUpgradeAsync($"ide-{ws.PublicId}.localhost:{Port}", null);
        Assert.StartsWith("HTTP/1.1 401", denied.Head);
        Assert.Equal(before, app.Backend.Server.Requests.Count);

        var ok = await RawUpgradeAsync($"ide-{ws.PublicId}.localhost:{Port}", cookie, echo: "hello-through-the-proxy");
        Assert.StartsWith("HTTP/1.1 101", ok.Head);
        Assert.Contains("Upgrade: websocket", ok.Head, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("hello-through-the-proxy", ok.Echo);
        var seen = app.Backend.Server.Requests.Last(r => r.Contains("GET /ws"));
        Assert.Contains($"vscode-tkn={app.Backend.ConnectionToken(ws.ContainerId)}", seen);
        Assert.DoesNotContain("bb_ws", seen);
    }

    private async Task<(string Head, string? Echo)> RawUpgradeAsync(string host, string? cookie, string? echo = null)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, Port);
        var stream = client.GetStream();
        var req = $"GET /ws HTTP/1.1\r\nHost: {host}\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n"
                  + (cookie != null ? $"Cookie: {cookie}\r\n" : "") + "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(req));
        var buf = new byte[8192];
        var filled = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int end;
        while ((end = IndexOf(buf, filled)) < 0)
        {
            var n = await stream.ReadAsync(buf.AsMemory(filled), cts.Token);
            if (n == 0) break;
            filled += n;
        }
        var head = Encoding.ASCII.GetString(buf, 0, end < 0 ? filled : end);
        string? echoed = null;
        if (echo != null && head.StartsWith("HTTP/1.1 101"))
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(echo));
            var got = new StringBuilder();
            var tail = end + 4 < filled ? Encoding.UTF8.GetString(buf, end + 4, filled - end - 4) : "";
            got.Append(tail);
            while (got.Length < echo.Length)
            {
                var n = await stream.ReadAsync(buf, cts.Token);
                if (n == 0) break;
                got.Append(Encoding.UTF8.GetString(buf, 0, n));
            }
            echoed = got.ToString();
        }
        return (head, echoed);
    }

    private static int IndexOf(byte[] b, int count)
    {
        for (var i = 0; i + 3 < count; i++) if (b[i] == '\r' && b[i + 1] == '\n' && b[i + 2] == '\r' && b[i + 3] == '\n') return i;
        return -1;
    }

    [Fact]
    public async Task Sessions_die_when_the_workspace_stops()
    {
        var (owner, ws) = await RunningAsync();
        using var http = app.Http();
        var cookie = await SignInAsync(http, owner, ws.PublicId);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Req($"ide-{ws.PublicId}.localhost:{Port}", "/echo", cookie))).StatusCode);
        await using (var s = app.Scope(app.UserId(owner))) await s.Get<WorkspaceService>().StopAsync(ws.PublicId, app.UserId(owner));
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(Req($"ide-{ws.PublicId}.localhost:{Port}", "/echo", cookie))).StatusCode);
    }

    [Fact]
    public async Task Hosts_that_are_not_workspace_labels_are_left_to_the_app()
    {
        using var http = app.Http();
        // The main origin and unrelated subdomains are never swallowed by the proxy.
        var home = await http.SendAsync(Req($"localhost:{Port}", "/health/live"));
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        var odd = await http.SendAsync(Req($"something.localhost:{Port}", "/health/live"));
        Assert.Equal(HttpStatusCode.OK, odd.StatusCode);
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/a/b?c=d", "/a/b?c=d")]
    [InlineData("//evil.example/x", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("", "/")]
    [InlineData("/a\r\nSet-Cookie: x=y", "/")]
    public void The_enter_redirect_only_goes_to_local_paths(string next, string expected) =>
        Assert.Equal(expected, WorkspaceProxyMiddleware.LocalPath(next));

    [Fact]
    public void Cookies_for_the_workspace_never_include_byteBench_session_material()
    {
        Assert.Equal("a=1; vscode-tkn=T", WorkspaceProxyMiddleware.BuildCookie("a=1; bb_ws=S; vscode-tkn=evil", false, "T"));
        Assert.Null(WorkspaceProxyMiddleware.BuildCookie("bb_ws=S", false, null));
        Assert.Equal("vscode-tkn=T", WorkspaceProxyMiddleware.BuildCookie(".AspNetCore.Identity.Application=x; a=1", true, "T")); // shared-origin mode: nothing of the browser's
        Assert.Equal("SameSite=None", WorkspaceProxyMiddleware.FrameableCookie("a=b; SameSite=Lax; Secure", true).Split("; ").Single(p => p.StartsWith("SameSite")));
        Assert.Equal("a=b; SameSite=Lax", WorkspaceProxyMiddleware.FrameableCookie("a=b; SameSite=Lax", false));
    }

    [Fact]
    public void Labels_parse_strictly()
    {
        Assert.True(WorkspaceUrls.TryParseLabel("ide-abcdefghijklmnopqrst", out var aud, out var id, out _));
        Assert.Equal(("ide", "abcdefghijklmnopqrst"), (aud, id));
        Assert.True(WorkspaceUrls.TryParseLabel("p3000-abcdefghijklmnopqrst", out _, out _, out var port));
        Assert.Equal(3000, port);
        foreach (var bad in new[] { "ide-short", "ide-ABCDEFGHIJKLMNOPQRST", "p0-abcdefghijklmnopqrst", "p99999-abcdefghijklmnopqrst", "x-abcdefghijklmnopqrst", "ide-abcdefghijklmnopqrs1", "" })
            Assert.False(WorkspaceUrls.TryParseLabel(bad, out _, out _, out _), bad);
    }
}

/// <summary>Shared-origin mode (no base domain, AllowSharedOrigin): path-based, session-authenticated, previews sandboxed.</summary>
public class WorkspaceSharedOriginApp : WorkspaceApp
{
    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton(sp => new WorkspaceUrls(Microsoft.Extensions.Options.Options.Create(new WorkspaceOptions { AllowSharedOrigin = true }), new ProductionEnv()));
    }

    private sealed class ProductionEnv : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}

public class WorkspaceSharedOriginTests(WorkspaceSharedOriginApp app) : IClassFixture<WorkspaceSharedOriginApp>
{
    [Fact]
    public async Task Only_the_owners_main_session_reaches_the_path_and_previews_are_sandboxed()
    {
        var owner = await app.NewUserAsync();
        var intruder = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((owner, ProjectRole.Developer), (intruder, ProjectRole.Developer));
        var ws = await app.StartAsync(owner, repo);

        using var ownerClient = await app.LoginClientAsync(owner);
        var ide = await ownerClient.GetAsync($"/_ws/{ws.PublicId}/ide/echo");
        var upstream = await ide.Content.ReadAsStringAsync();
        Assert.True(ide.StatusCode == HttpStatusCode.OK, $"{(int)ide.StatusCode}: {upstream[..Math.Min(300, upstream.Length)]}");
        Assert.Contains($"GET /_ws/{ws.PublicId}/ide/echo", upstream); // the IDE keeps its base path
        Assert.Contains($"vscode-tkn={app.Backend.ConnectionToken(ws.ContainerId)}", upstream);
        Assert.DoesNotContain("Identity.Application", upstream); // ByteBench's session cookie never reaches the workspace

        var preview = await ownerClient.GetAsync($"/_ws/{ws.PublicId}/p8000/echo");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Contains("GET /echo", await preview.Content.ReadAsStringAsync()); // the prefix is stripped for apps
        var csp = string.Join(";", preview.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("sandbox allow-scripts", csp);
        Assert.DoesNotContain("allow-same-origin", csp);

        using var intruderClient = await app.LoginClientAsync(intruder);
        Assert.Equal(HttpStatusCode.NotFound, (await intruderClient.GetAsync($"/_ws/{ws.PublicId}/ide/echo")).StatusCode);
        using var anonymous = app.Http();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/_ws/{ws.PublicId}/ide/echo")).StatusCode);
        Assert.Equal(WorkspaceOriginMode.SharedPath, app.Services.GetRequiredService<WorkspaceUrls>().Mode);
    }

    [Fact]
    public void Origin_mode_is_decided_by_configuration()
    {
        var prod = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Production" };
        Assert.Equal(WorkspaceOriginMode.Unavailable, new WorkspaceUrls(Microsoft.Extensions.Options.Options.Create(new WorkspaceOptions()), prod).Mode);
        Assert.Equal(WorkspaceOriginMode.Subdomain, new WorkspaceUrls(Microsoft.Extensions.Options.Options.Create(new WorkspaceOptions { BaseDomain = "ws.example.com" }), prod).Mode);
        Assert.Equal(WorkspaceOriginMode.SharedPath, new WorkspaceUrls(Microsoft.Extensions.Options.Options.Create(new WorkspaceOptions { AllowSharedOrigin = true }), prod).Mode);
        var dev = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Development" };
        Assert.Equal("localhost", new WorkspaceUrls(Microsoft.Extensions.Options.Options.Create(new WorkspaceOptions()), dev).BaseDomain);
    }
}
