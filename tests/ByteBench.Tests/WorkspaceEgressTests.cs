using System.Net;
using System.Net.Sockets;
using System.Text;
using ByteBench.Data;
using ByteBench.Services.Workspaces;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

/// <summary>The egress rules: parsing, the narrowing-only merge of admin policy and repository policy (pure), then the enforcement path on the fake backend.</summary>
public class WorkspaceEgressPolicyTests
{
    private static EffectiveEgress Admin(string mode, params string[] entries) => WorkspaceEgressPolicy.Admin(mode, string.Join("\n", entries), "public-only");

    private static List<string> Rules(EffectiveEgress e) => e.Rules.Select(r => r.ToString()).OrderBy(x => x).ToList();

    [Fact]
    public void Entries_parse_hosts_ports_wildcards_and_presets_and_reject_the_rest()
    {
        var errors = new List<string>();
        var rules = WorkspaceEgressPolicy.ParseEntries(["Example.COM", "api.example.com:8443", "cdn.example.com:*", "*.corp.example.com", "@pypi", "10.1.2.3:5000"], errors);
        Assert.Empty(errors);
        Assert.Contains(new EgressRule("example.com", 0), rules);
        Assert.Contains(new EgressRule("api.example.com", 8443), rules);
        Assert.Contains(new EgressRule("cdn.example.com", EgressRule.AnyPort), rules);
        Assert.Contains(new EgressRule("*.corp.example.com", 0), rules);
        Assert.Contains(new EgressRule("pypi.org", 0), rules);
        Assert.Contains(new EgressRule("10.1.2.3", 5000), rules);

        foreach (var bad in new[] { "https://example.com", "example.com/path", "exa mple.com", "*.com", "*", "example.com:99999", "example.com:abc", "-bad.example.com", "@nosuchpreset", "a..b" })
        {
            var e = new List<string>();
            WorkspaceEgressPolicy.ParseEntries([bad], e);
            Assert.NotEmpty(e);
        }
    }

    [Fact]
    public void Rules_match_hosts_and_ports()
    {
        var web = new EgressRule("registry.npmjs.org", 0);
        Assert.True(web.Matches("registry.npmjs.org", 443));
        Assert.True(web.Matches("REGISTRY.npmjs.org.", 80));
        Assert.False(web.Matches("registry.npmjs.org", 22));
        Assert.False(web.Matches("evil-registry.npmjs.org", 443));
        var wild = new EgressRule("*.nuget.org", 0);
        Assert.True(wild.Matches("api.nuget.org", 443));
        Assert.True(wild.Matches("a.b.nuget.org", 443));
        Assert.False(wild.Matches("nuget.org", 443));
        Assert.False(wild.Matches("evilnuget.org", 443));
        Assert.True(new EgressRule("git.example.com", EgressRule.AnyPort).Matches("git.example.com", 22));
        Assert.True(new EgressRule("git.example.com", 2222).Matches("git.example.com", 2222));
        Assert.False(new EgressRule("git.example.com", 2222).Matches("git.example.com", 443));
    }

    [Fact]
    public void The_defaults_cover_git_hosts_and_every_package_registry()
    {
        var policy = WorkspaceEgressPolicy.Admin("allowlist", null, "public-only");
        foreach (var host in new[] { "github.com", "registry.npmjs.org", "api.nuget.org", "pypi.org", "files.pythonhosted.org", "crates.io", "static.crates.io", "proxy.golang.org", "repo.maven.apache.org" })
            Assert.True(policy.Allows(host, 443), host);
        Assert.False(policy.Allows("example.org", 443));
        Assert.False(policy.Allows("registry.npmjs.org", 22));
    }

    [Fact]
    public void A_repository_can_narrow_to_none_or_to_a_subset_but_never_widen()
    {
        var admin = Admin("allowlist", "@npm", "*.example.com", "git.example.com:2222");

        // none always wins
        Assert.Equal("none", WorkspaceEgressPolicy.Narrow(admin, WorkspaceEgressPolicy.ParseRepoSpec("egress: none")).Mode);
        // a subset of the admin's list is kept; everything else is dropped with a warning
        var narrowed = WorkspaceEgressPolicy.Narrow(admin, WorkspaceEgressPolicy.ParseRepoSpec("""
            egress:
              allow:
                - registry.npmjs.org
                - api.example.com
                - git.example.com:2222
                - git.example.com:22
                - evil.org
                - "*.org"
                - "@pypi"
            """));
        Assert.Equal("allowlist", narrowed.Mode);
        Assert.True(narrowed.RepoNarrowed);
        Assert.Equal(["api.example.com", "git.example.com:2222", "registry.npmjs.org"], Rules(narrowed));
        Assert.Contains(narrowed.Warnings, w => w.Contains("evil.org"));
        Assert.Contains(narrowed.Warnings, w => w.Contains("git.example.com:22"));
        Assert.Contains(narrowed.Warnings, w => w.Contains("pypi.org"));
        Assert.False(narrowed.Allows("evil.org", 443));
        Assert.False(narrowed.Allows("github.com", 443));
        Assert.True(narrowed.Allows("api.example.com", 443));

        // a wildcard under the admin's wildcard is fine, the apex is not covered by it
        var sub = WorkspaceEgressPolicy.Narrow(admin, WorkspaceEgressPolicy.ParseRepoSpec("egress: [\"*.internal.example.com\", example.com]"));
        Assert.Equal(["*.internal.example.com"], Rules(sub));
        // a wider port is refused
        Assert.Empty(WorkspaceEgressPolicy.Narrow(admin, WorkspaceEgressPolicy.ParseRepoSpec("egress: [\"registry.npmjs.org:*\"]")).Rules);
    }

    [Fact]
    public void A_repository_cannot_loosen_a_stricter_admin_policy()
    {
        var none = Admin("none");
        var widened = WorkspaceEgressPolicy.Narrow(none, WorkspaceEgressPolicy.ParseRepoSpec("egress:\n  mode: open"));
        Assert.Equal("none", widened.Mode);
        Assert.Contains(widened.Warnings, w => w.Contains("wider"));
        Assert.Equal("none", WorkspaceEgressPolicy.Narrow(none, WorkspaceEgressPolicy.ParseRepoSpec("egress: [github.com]")).Mode);

        var allowlist = Admin("allowlist", "@npm");
        var loosened = WorkspaceEgressPolicy.Narrow(allowlist, WorkspaceEgressPolicy.ParseRepoSpec("egress: { mode: public-only }"));
        Assert.Equal("allowlist", loosened.Mode);
        Assert.Equal(allowlist.Rules, loosened.Rules);
        Assert.Equal("allowlist", WorkspaceEgressPolicy.Narrow(allowlist, WorkspaceEgressPolicy.ParseRepoSpec("egress: open")).Mode);
    }

    [Fact]
    public void From_an_open_or_public_only_policy_a_repository_may_narrow_to_a_list_or_none()
    {
        foreach (var mode in new[] { "open", "public-only" })
        {
            var admin = Admin(mode);
            var list = WorkspaceEgressPolicy.Narrow(admin, WorkspaceEgressPolicy.ParseRepoSpec("egress: [example.com]"));
            Assert.Equal("allowlist", list.Mode);
            Assert.Equal(["example.com"], Rules(list));
            Assert.Equal("none", WorkspaceEgressPolicy.Narrow(admin, WorkspaceEgressPolicy.ParseRepoSpec("egress: none")).Mode);
            Assert.Equal(mode, WorkspaceEgressPolicy.Narrow(admin, WorkspaceEgressPolicy.ParseRepoSpec("egress: " + mode)).Mode); // same strictness: unchanged
            Assert.Equal(mode, WorkspaceEgressPolicy.Narrow(admin, null).Mode);
        }
    }

    [Fact]
    public void A_broken_repository_file_changes_nothing_and_says_why()
    {
        var admin = Admin("allowlist", "@npm");
        foreach (var yaml in new[] { "egress: [", "egress:\n  mode: sideways", "egress:\n  frobnicate: 1", "egress: 42" })
        {
            var spec = WorkspaceEgressPolicy.ParseRepoSpec(yaml);
            var result = WorkspaceEgressPolicy.Narrow(admin, spec);
            Assert.Equal("allowlist", result.Mode);
            Assert.Equal(admin.Rules, result.Rules);
            Assert.Contains(result.Warnings, w => w.StartsWith("Repository egress:"));
        }
        Assert.Null(WorkspaceEgressPolicy.ParseRepoSpec("image: node:20\ntasks: {}"));
        Assert.Null(WorkspaceEgressPolicy.ParseRepoSpec(""));
    }

    [Fact]
    public void The_environment_file_parser_validates_egress_strictly_and_keeps_the_rest()
    {
        var ok = WorkspaceEnvFile.ParseYaml("ports: [3000]\negress:\n  allow: [registry.npmjs.org, \"@pypi\"]\n");
        Assert.True(ok.Ok);
        Assert.Equal("allowlist", ok.Environment!.Egress!.Mode);
        Assert.Equal(3, ok.Environment.Egress.Allow!.Count);
        Assert.Equal("none", WorkspaceEnvFile.ParseYaml("egress: none").Environment!.Egress!.Mode);
        Assert.Null(WorkspaceEnvFile.ParseYaml("ports: [3000]").Environment!.Egress);
        foreach (var bad in new[] { "egress: sideways", "egress:\n  allow: [\"https://x.com\"]", "egress:\n  frob: 1", "egress: 42", "egress:\n  allow: [\"*.com\"]" })
        {
            var r = WorkspaceEnvFile.ParseYaml(bad);
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.StartsWith("egress:"));
        }
    }

    [Fact]
    public void Only_public_addresses_are_ever_connected_to()
    {
        foreach (var ip in new[] { "127.0.0.1", "10.0.0.5", "172.16.3.4", "172.31.255.1", "192.168.1.1", "169.254.169.254", "100.64.0.1", "0.0.0.0", "224.0.0.1", "::1", "fe80::1", "fc00::1", "::ffff:10.0.0.1" })
            Assert.False(DnsEgressConnector.IsPublic(IPAddress.Parse(ip)), ip);
        foreach (var ip in new[] { "8.8.8.8", "140.82.112.3", "172.32.0.1", "2606:4700::1111" })
            Assert.True(DnsEgressConnector.IsPublic(IPAddress.Parse(ip)), ip);
    }
}

public class WorkspaceEgressEnforcementTests(SharingApp app) : IClassFixture<SharingApp>
{
    private async Task<T> WithAdminPolicyAsync<T>(string mode, string entries, Func<Task<T>> act)
    {
        var admin = app.UserId("alice");
        await using var scope = app.Scope(admin);
        var svc = scope.Get<WorkspaceEgressAdmin>();
        Assert.Empty(await svc.SetAsync(mode, entries, admin));
        try { return await act(); }
        finally { Assert.Empty(await svc.SetAsync("public-only", "@git", admin)); }
    }

    private static (string Host, int Port, string Token) ProxyOf(WorkspaceContainerSpec spec)
    {
        var uri = new Uri(spec.Env["HTTPS_PROXY"]);
        return (uri.Host, uri.Port, uri.UserInfo.Split(':')[1]);
    }

    private static string Auth(string token) => "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("bytebench:" + token)) + "\r\n";

    private static async Task<(string Head, NetworkStream Stream, TcpClient Client)> RawAsync(int port, string request)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        return (await ReadHeadAsync(stream), stream, client);
    }

    private static async Task<string> ReadHeadAsync(NetworkStream stream)
    {
        var sb = new StringBuilder();
        var b = new byte[1];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!sb.ToString().EndsWith("\r\n\r\n"))
        {
            if (await stream.ReadAsync(b, cts.Token) == 0) break;
            sb.Append((char)b[0]);
        }
        return sb.ToString();
    }

    private async Task<Workspace> StartAsync(string? repoYaml = null)
    {
        var user = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer));
        if (repoYaml != null) app.Commit(repo, "main", ".bytebench/workspace.yml", repoYaml);
        return await app.StartAsync(user, repo);
    }

    [Fact]
    public async Task The_default_policy_starts_exactly_as_before_without_a_proxy()
    {
        var ws = await StartAsync();
        Assert.Equal("public-only", ws.EgressMode);
        var spec = app.Backend.Started.Last(s => s.WorkspaceId == ws.Id);
        Assert.DoesNotContain("HTTPS_PROXY", spec.Env.Keys);
        await using var db = app.Db();
        var record = await db.WorkspaceEgressRecords.SingleAsync(r => r.WorkspaceId == ws.Id);
        Assert.Equal("public-only", record.Mode);
        Assert.False(record.RepoNarrowed);
    }

    [Fact]
    public async Task An_allowlist_workspace_firewalls_everything_but_git_and_the_proxy_and_points_its_tools_at_the_proxy()
    {
        var ws = await WithAdminPolicyAsync("allowlist", "@npm\nexample.com", () => StartAsync());
        Assert.Equal("allowlist", ws.EgressMode);
        var spec = app.Backend.Started.Last(s => s.WorkspaceId == ws.Id);
        var (host, port, token) = ProxyOf(spec);
        Assert.Equal("127.0.0.1", host);
        Assert.Equal(spec.Env["HTTP_PROXY"], spec.Env["http_proxy"]);
        Assert.Contains("127.0.0.1", spec.Env["NO_PROXY"]); // the git endpoint is reached directly

        var policy = app.Backend.EgressApplied.Last();
        Assert.Equal("none", policy.Mode); // the helper's "nothing but the allowed ip:port pairs"
        Assert.Equal(["127.0.0.1:9", $"127.0.0.1:{port}"], policy.Allow);
        Assert.DoesNotContain(app.Backend.Execs, e => e.Spec.Stdin?.Contains(token) == true); // the proxy secret only lives in the container's environment

        await using var db = app.Db();
        var record = await db.WorkspaceEgressRecords.SingleAsync(r => r.WorkspaceId == ws.Id);
        Assert.Equal("allowlist", record.Mode);
        Assert.Contains("registry.npmjs.org", record.RulesJson);
        Assert.Contains("example.com", record.RulesJson);
    }

    [Fact]
    public async Task The_proxy_tunnels_allowed_hosts_and_blocks_the_rest_and_the_block_is_logged_and_audited()
    {
        var ws = await WithAdminPolicyAsync("allowlist", "registry.npmjs.org\ninternal.example.com", () => StartAsync());
        var spec = app.Backend.Started.Last(s => s.WorkspaceId == ws.Id);
        var (_, port, token) = ProxyOf(spec);

        // allowed: CONNECT on 443 becomes a tunnel to the (fake) upstream
        var (head, stream, client) = await RawAsync(port, $"CONNECT registry.npmjs.org:443 HTTP/1.1\r\nHost: registry.npmjs.org:443\r\n{Auth(token)}\r\n");
        using (client)
        {
            Assert.StartsWith("HTTP/1.1 200", head);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /echo HTTP/1.1\r\nHost: registry.npmjs.org\r\nConnection: close\r\n\r\n"));
            var buf = new byte[8192];
            var n = await stream.ReadAsync(buf);
            Assert.Contains("200 OK", Encoding.ASCII.GetString(buf, 0, n));
        }
        Assert.Contains(app.Connector.Calls, c => c.Host == "registry.npmjs.org" && c.Port == 443 && !c.AllowPrivate);

        // plain http through the proxy: absolute-form is rewritten to origin-form, proxy headers dropped
        var (head2, stream2, client2) = await RawAsync(port, $"GET http://registry.npmjs.org:80/echo HTTP/1.1\r\nHost: registry.npmjs.org\r\n{Auth(token)}Proxy-Connection: keep-alive\r\n\r\n");
        using (client2)
        {
            Assert.StartsWith("HTTP/1.1 200", head2);
            var rest = new StringBuilder();
            var b = new byte[8192];
            using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            // The echo upstream keeps the connection open, so read until the echoed request head (which ends with the header the proxy adds) has arrived.
            while (!rest.ToString().Contains("Connection: close"))
            {
                var n = await stream2.ReadAsync(b, readTimeout.Token);
                if (n == 0) break;
                rest.Append(Encoding.ASCII.GetString(b, 0, n));
            }
            Assert.Contains("GET /echo HTTP/1.1", rest.ToString());
            Assert.DoesNotContain("Proxy-", rest.ToString());
        }

        // blocked: not in the list, wrong port, a private-resolving allowed name, a bare IP
        foreach (var target in new[] { "evil.example.org:443", "registry.npmjs.org:22", "1.2.3.4:443" })
        {
            var (h, _, c) = await RawAsync(port, $"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n{Auth(token)}\r\n");
            using (c) Assert.StartsWith("HTTP/1.1 403", h);
        }
        var (hPrivate, _, cPrivate) = await RawAsync(port, $"CONNECT internal.example.com:443 HTTP/1.1\r\nHost: internal.example.com:443\r\n{Auth(token)}\r\n");
        using (cPrivate) Assert.StartsWith("HTTP/1.1 403", hPrivate);

        // no or wrong credentials: nothing is even considered
        var callsBefore = app.Connector.Calls.Count;
        var (h407, _, c407) = await RawAsync(port, "CONNECT registry.npmjs.org:443 HTTP/1.1\r\nHost: registry.npmjs.org:443\r\n\r\n");
        using (c407) Assert.StartsWith("HTTP/1.1 407", h407);
        var (hBad, _, cBad) = await RawAsync(port, $"CONNECT registry.npmjs.org:443 HTTP/1.1\r\nHost: x\r\n{Auth("not-the-token")}\r\n");
        using (cBad) Assert.StartsWith("HTTP/1.1 407", hBad);
        Assert.Equal(callsBefore, app.Connector.Calls.Count);

        // the log the UI shows, the counters, and the audit trail (each distinct destination once)
        var egress = app.Services.GetRequiredService<WorkspaceEgress>();
        var blocked = egress.Blocked(ws.PublicId);
        Assert.Contains(blocked, b => b.Host == "evil.example.org" && b.Port == 443 && b.Count == 1);
        Assert.Contains(blocked, b => b.Host == "internal.example.com" && b.Reason.Contains("non-public"));
        Assert.Equal(4, egress.Counts(ws.Id).Blocked);
        Assert.Equal(2, egress.Counts(ws.Id).Allowed);
        var (h2, _, c2) = await RawAsync(port, $"CONNECT evil.example.org:443 HTTP/1.1\r\nHost: x\r\n{Auth(token)}\r\n");
        using (c2) Assert.StartsWith("HTTP/1.1 403", h2);
        Assert.Equal(2, egress.Blocked(ws.PublicId).Single(b => b.Host == "evil.example.org").Count);
        await Task.Delay(500);
        await using var db = app.Db();
        var events = await db.AuditEvents.Where(a => a.Action == "workspace.egress_blocked" && a.TargetId == ws.PublicId).ToListAsync();
        Assert.Equal(4, events.Count);
        Assert.Contains(events, e => e.Details!.Contains("evil.example.org"));

        // the owner's view of it
        await using var scope = app.Scope(ws.OwnerId);
        var view = await scope.Get<WorkspaceEgressReader>().GetAsync(ws);
        Assert.Equal("allowlist", view.Mode);
        Assert.Equal(4 + 1, view.BlockedTotal);
        Assert.Equal(4, view.Blocked.Count);
    }

    [Fact]
    public async Task Stopping_the_workspace_revokes_its_proxy_credential()
    {
        var ws = await WithAdminPolicyAsync("allowlist", "registry.npmjs.org", () => StartAsync());
        var (_, port, token) = ProxyOf(app.Backend.Started.Last(s => s.WorkspaceId == ws.Id));
        var (ok, _, c1) = await RawAsync(port, $"CONNECT registry.npmjs.org:443 HTTP/1.1\r\nHost: x\r\n{Auth(token)}\r\n");
        using (c1) Assert.StartsWith("HTTP/1.1 200", ok);
        await using (var scope = app.Scope(ws.OwnerId))
            await scope.Get<WorkspaceService>().StopAsync(ws.PublicId, ws.OwnerId);
        var (denied, _, c2) = await RawAsync(port, $"CONNECT registry.npmjs.org:443 HTTP/1.1\r\nHost: x\r\n{Auth(token)}\r\n");
        using (c2) Assert.StartsWith("HTTP/1.1 407", denied);
    }

    [Fact]
    public async Task A_repository_file_narrows_the_policy_at_start_and_cannot_widen_it()
    {
        var yaml = """
            ports: [3000]
            egress:
              allow:
                - registry.npmjs.org
                - evil.example.org
            """;
        var ws = await WithAdminPolicyAsync("allowlist", "@npm\n@pypi", () => StartAsync(yaml));
        Assert.Equal("allowlist", ws.EgressMode);
        await using var scope = app.Scope(ws.OwnerId);
        var view = await scope.Get<WorkspaceEgressReader>().GetAsync(ws);
        Assert.True(view.RepoNarrowed);
        Assert.Equal(["registry.npmjs.org"], view.Rules);
        Assert.Contains(view.Warnings, w => w.Contains("evil.example.org"));
        var (_, port, token) = ProxyOf(app.Backend.Started.Last(s => s.WorkspaceId == ws.Id));
        var (h, _, c) = await RawAsync(port, $"CONNECT pypi.org:443 HTTP/1.1\r\nHost: x\r\n{Auth(token)}\r\n"); // admin allows it, the repository narrowed it away
        using (c) Assert.StartsWith("HTTP/1.1 403", h);

        // a repository asking for "none" under an open server policy gets it; asking for "open" under an allow-list gets nothing more
        var none = await WithAdminPolicyAsync("open", "", () => StartAsync("egress: none"));
        Assert.Equal("none", none.EgressMode);
        Assert.Equal("none", app.Backend.EgressApplied.Last().Mode);
        var stillAllowlist = await WithAdminPolicyAsync("allowlist", "@npm", () => StartAsync("egress:\n  mode: open"));
        Assert.Equal("allowlist", stillAllowlist.EgressMode);
    }

    [Fact]
    public async Task Only_admins_set_the_policy_and_bad_entries_are_refused_and_changes_are_audited()
    {
        var user = await app.NewUserAsync();
        await using (var scope = app.Scope(app.UserId(user)))
            await Assert.ThrowsAsync<ByteBench.Services.ForbiddenException>(() => scope.Get<WorkspaceEgressAdmin>().SetAsync("none", "", app.UserId(user)));
        var admin = app.UserId("alice");
        await using var s = app.Scope(admin);
        var svc = s.Get<WorkspaceEgressAdmin>();
        var errors = await svc.SetAsync("allowlist", "https://nope.example.com\n*.com\n@missing", admin);
        Assert.Equal(3, errors.Count);
        Assert.Equal("public-only", (await svc.GetAsync()).Mode); // nothing saved
        Assert.Empty(await svc.SetAsync("allowlist", "good.example.com:8080", admin));
        try
        {
            await using var db = app.Db();
            Assert.Contains(await db.AuditEvents.Where(a => a.Action == "workspace.admin_egress").ToListAsync(), a => a.Details!.Contains("good.example.com:8080"));
        }
        finally { await svc.SetAsync("public-only", "@git", admin); }
    }
}
