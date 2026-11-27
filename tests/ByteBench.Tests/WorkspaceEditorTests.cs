using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ByteBench.Cli;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Tests;

/// <summary>Connecting a local editor (VS Code Remote-SSH, JetBrains Gateway): the admin switch, key validation, the container setup and the WebSocket tunnel.</summary>
public class WorkspaceEditorTests(WorkspaceApp app) : IClassFixture<WorkspaceApp>
{
    private static byte[] Be32(int n) => [(byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n];

    private static string Ed25519Key(string comment = "me@laptop")
    {
        var type = Encoding.ASCII.GetBytes("ssh-ed25519");
        var blob = Be32(type.Length).Concat(type).Concat(Be32(32)).Concat(RandomNumberGenerator.GetBytes(32)).ToArray();
        return $"ssh-ed25519 {Convert.ToBase64String(blob)} {comment}";
    }

    private static string RsaKey(int bits)
    {
        var type = Encoding.ASCII.GetBytes("ssh-rsa");
        var n = new byte[] { 0 }.Concat(RandomNumberGenerator.GetBytes(bits / 8)).ToArray();
        n[1] |= 0x80;
        var e = new byte[] { 1, 0, 1 };
        var blob = Be32(type.Length).Concat(type).Concat(Be32(e.Length)).Concat(e).Concat(Be32(n.Length)).Concat(n).ToArray();
        return "ssh-rsa " + Convert.ToBase64String(blob);
    }

    private async Task SetEditorAsync(bool on)
    {
        var admin = app.UserId("alice");
        await using var s = app.Scope(admin);
        await s.Get<WorkspaceEditorService>().SetEnabledAsync(on, admin);
    }

    private async Task<(string User, Workspace Ws)> RunningAsync()
    {
        var user = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer));
        return (user, await app.StartAsync(user, repo));
    }

    private static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

    [Fact]
    public void Public_keys_are_validated_strictly_and_rebuilt_without_options_or_comments()
    {
        var ok = WorkspaceEditorService.ParsePublicKey(Ed25519Key("dev@box"));
        Assert.Equal("ssh-ed25519", ok.Type);
        Assert.StartsWith("SHA256:", ok.Fingerprint);
        Assert.DoesNotContain("dev@box", ok.Canonical);
        Assert.Equal(2, ok.Canonical.Split(' ').Length);

        var good = Ed25519Key();
        foreach (var bad in new[]
                 {
                     "", "   ", "not a key", "ssh-ed25519", "ssh-ed25519 !!!notbase64!!!",
                     $"command=\"touch /tmp/pwned\" {good}",                 // authorized_keys options are never accepted
                     $"from=\"*\",no-pty {good}",
                     good + "\n" + good,                                     // a second line
                     good + "\rcommand=\"x\" " + good,
                     "-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY-----",
                     "ssh-rsa " + good.Split(' ')[1],                        // blob says ed25519, line says rsa
                     RsaKey(1024),                                           // too short
                     "ssh-dss AAAAB3NzaC1kc3MAAACBAP",                       // unsupported type
                     "ssh-ed25519 " + new string('A', 5000),
                 })
            Assert.Throws<InvalidOperationException>(() => WorkspaceEditorService.ParsePublicKey(bad));
        WorkspaceEditorService.ParsePublicKey(RsaKey(3072));
    }

    [Fact]
    public async Task It_is_off_by_default_and_only_an_admin_can_switch_it_on_which_is_audited()
    {
        var (user, ws) = await RunningAsync();
        await using (var s = app.Scope(app.UserId(user)))
        {
            Assert.False(await s.Get<WorkspaceEditorService>().IsEnabledAsync());
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<WorkspaceEditorService>().ConnectAsync(ws.PublicId, app.UserId(user), Ed25519Key(), "http://x"));
            Assert.Contains("turned off", ex.Message);
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<WorkspaceEditorService>().SetEnabledAsync(true, app.UserId(user)));
        }
        Assert.DoesNotContain(app.Backend.Execs, e => e.ContainerId == ws.ContainerId && e.Spec.Command.Any(a => a.Contains("sshd")));
        await SetEditorAsync(true);
        try
        {
            await using var s = app.Scope(app.UserId(user));
            Assert.True(await s.Get<WorkspaceEditorService>().IsEnabledAsync());
        }
        finally { await SetEditorAsync(false); }
        await using var db = app.Db();
        Assert.Contains(await db.AuditEvents.Where(a => a.Action == "workspace.admin_editor_switch").ToListAsync(), a => a.Details!.Contains("true"));
    }

    [Fact]
    public async Task Connecting_installs_an_expiring_canonical_key_starts_sshd_and_returns_instructions_without_secrets()
    {
        var (user, ws) = await RunningAsync();
        app.Backend.ExecScripts.Add((s => s.Command.Any(a => a.Contains("BB_HOSTKEY")), _ => new ExecResult(0, "BB_HOSTKEY SHA256:abcDEF123+/\n")));
        await SetEditorAsync(true);
        try
        {
            var key = Ed25519Key("evil\" command=\"x");
            await using var s = app.Scope(app.UserId(user));
            var c = await s.Get<WorkspaceEditorService>().ConnectAsync(ws.PublicId, app.UserId(user), key, "https://bb.example.com");

            Assert.Equal("coder", c.User);
            Assert.Equal("/workspace/code", c.Directory);
            Assert.Equal($"wss://bb.example.com/api/v1/workspaces/{ws.PublicId}/editor/tunnel", c.TunnelUrl);
            Assert.Equal($"bytebench workspace tunnel {ws.PublicId}", c.TunnelCommand);
            Assert.Contains($"ProxyCommand bytebench workspace tunnel {ws.PublicId}", c.SshConfig);
            Assert.Contains($"Host {c.Alias}", c.SshConfig);
            Assert.Equal("SHA256:abcDEF123+/", c.HostKeyFingerprint);
            Assert.True(c.KeyExpiresAt > DateTimeOffset.UtcNow.AddHours(7) && c.KeyExpiresAt <= DateTimeOffset.UtcNow.AddHours(8).AddMinutes(1));
            Assert.Contains(c.VsCodeSteps, x => x.Contains("Remote - SSH"));
            Assert.Contains(c.JetBrainsSteps, x => x.Contains("Gateway"));
            // Nothing secret comes back: no connection token, no git token, no private key.
            var json = JsonSerializer.Serialize(c);
            Assert.DoesNotContain("gbw_", json);
            Assert.DoesNotContain(app.Backend.ConnectionToken(ws.ContainerId)!, json);
            Assert.DoesNotContain("PRIVATE", json);

            // The line written into authorized_keys is rebuilt: an expiry, the key, a fixed comment, and nothing from the pasted line's comment.
            var install = app.Backend.Execs.Last(e => e.ContainerId == ws.ContainerId && e.Spec.Stdin?.Contains("expiry-time=") == true).Spec;
            var line = install.Stdin!.TrimEnd('\n');
            Assert.Single(line.Split('\n'));
            Assert.Matches(@"^expiry-time=""\d{14}Z"" ssh-ed25519 [A-Za-z0-9+/=]+ bytebench-workspace$", line);
            Assert.DoesNotContain("evil", line);
            Assert.DoesNotContain(key.Split(' ')[1], install.Command[2]); // the key travels on stdin and as a plain argument, never inside the script text
            // sshd is started as the unprivileged user, loopback only, key login only.
            var start = app.Backend.Execs.Last(e => e.ContainerId == ws.ContainerId && e.Spec.Command.Any(a => a.Contains("/usr/sbin/sshd"))).Spec;
            Assert.Equal("coder", start.User);
            var script = start.Command.First(a => a.Contains("sshd_config"));
            Assert.Contains("ListenAddress 127.0.0.1", script);
            Assert.Contains("PasswordAuthentication no", script);
            Assert.Contains("AuthenticationMethods publickey", script);
            Assert.Contains("AllowUsers coder", script);
            Assert.Contains("PermitRootLogin no", script);

            await using var db = app.Db();
            var audit = await db.AuditEvents.FirstAsync(a => a.Action == "workspace.editor_key_added" && a.TargetId == ws.PublicId);
            Assert.Contains("SHA256:", audit.Details);
        }
        finally { await SetEditorAsync(false); app.Backend.ExecScripts.Clear(); }
    }

    [Fact]
    public async Task A_stranger_cannot_connect_to_or_tunnel_into_someone_elses_workspace()
    {
        var (owner, ws) = await RunningAsync();
        var stranger = await app.NewUserAsync();
        await SetEditorAsync(true);
        try
        {
            await using var s = app.Scope(app.UserId(stranger));
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<WorkspaceEditorService>().ConnectAsync(ws.PublicId, app.UserId(stranger), Ed25519Key(), "http://x"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<WorkspaceEditorService>().OpenTunnelAsync(ws.PublicId, app.UserId(stranger), CancellationToken.None));

            using var client = await app.ApiClientAsync(stranger);
            client.BaseAddress = app.ServerAddress;
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/workspaces/{ws.PublicId}/editor")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/v1/workspaces/{ws.PublicId}/editor/connect", Json(new { publicKey = Ed25519Key() }))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/workspaces/{ws.PublicId}/editor/tunnel")).StatusCode);
            Assert.NotNull(owner);
        }
        finally { await SetEditorAsync(false); }
        Assert.DoesNotContain(app.Backend.Execs, e => e.ContainerId == ws.ContainerId && e.Spec.Stdin?.Contains("expiry-time=") == true);
    }

    [Fact]
    public async Task The_rest_endpoints_report_status_and_connect_and_refuse_when_off_or_not_running()
    {
        var (user, ws) = await RunningAsync();
        using var client = await app.ApiClientAsync(user);
        client.BaseAddress = app.ServerAddress;
        var off = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/workspaces/{ws.PublicId}/editor")).RootElement;
        Assert.False(off.GetProperty("enabled").GetBoolean());
        Assert.Contains("turned off", off.GetProperty("problem").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync($"/api/v1/workspaces/{ws.PublicId}/editor/connect", Json(new { publicKey = Ed25519Key() }))).StatusCode);

        await SetEditorAsync(true);
        try
        {
            var on = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/workspaces/{ws.PublicId}/editor")).RootElement;
            Assert.True(on.GetProperty("enabled").GetBoolean());
            Assert.Equal($"bytebench workspace tunnel {ws.PublicId}", on.GetProperty("tunnelCommand").GetString());
            var bad = await client.PostAsync($"/api/v1/workspaces/{ws.PublicId}/editor/connect", Json(new { publicKey = "command=\"x\" " + Ed25519Key() }));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
            var good = await client.PostAsync($"/api/v1/workspaces/{ws.PublicId}/editor/connect", Json(new { publicKey = Ed25519Key() }));
            Assert.Equal(HttpStatusCode.OK, good.StatusCode);
            var body = JsonDocument.Parse(await good.Content.ReadAsStringAsync()).RootElement;
            Assert.StartsWith("SHA256:", body.GetProperty("keyFingerprint").GetString());
            Assert.Contains("ProxyCommand", body.GetProperty("sshConfig").GetString());

            await using (var s = app.Scope(app.UserId(user))) await s.Get<WorkspaceService>().StopAsync(ws.PublicId, app.UserId(user));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync($"/api/v1/workspaces/{ws.PublicId}/editor/connect", Json(new { publicKey = Ed25519Key() }))).StatusCode);
        }
        finally { await SetEditorAsync(false); }
    }

    private Uri TunnelUri(string publicId) =>
        new UriBuilder(app.ServerAddress) { Scheme = "ws", Path = $"/api/v1/workspaces/{publicId}/editor/tunnel" }.Uri;

    private static async Task<string> ReadAsync(ClientWebSocket ws, int minLength)
    {
        var sb = new StringBuilder();
        var buf = new byte[4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (sb.Length < minLength)
        {
            var r = await ws.ReceiveAsync(buf, cts.Token);
            if (r.MessageType == WebSocketMessageType.Close) break;
            sb.Append(Encoding.ASCII.GetString(buf, 0, r.Count));
        }
        return sb.ToString();
    }

    [Fact]
    public async Task The_tunnel_relays_bytes_to_the_container_ssh_port_for_the_owners_token_only()
    {
        var (user, ws) = await RunningAsync();
        app.Backend.ServeRaw(WorkspaceEditorService.SshPort, "SSH-2.0-BBFake\r\n");
        await SetEditorAsync(true);
        try
        {
            // The owner's token with write_api: the SSH banner comes through and bytes echo back.
            var token = await app.CreateTokenAsync(user, TokenScopes.ReadApi | TokenScopes.WriteApi);
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
            await socket.ConnectAsync(TunnelUri(ws.PublicId), CancellationToken.None);
            Assert.StartsWith("SSH-2.0-BBFake", await ReadAsync(socket, 16));
            await socket.SendAsync(Encoding.ASCII.GetBytes("SSH-2.0-client\r\n"), WebSocketMessageType.Binary, true, CancellationToken.None);
            Assert.Contains("SSH-2.0-client", await ReadAsync(socket, 16));
            Assert.Contains(app.Backend.Connections, c => c.Port == WorkspaceEditorService.SshPort);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

            // A read-only token, no token, and someone else's token don't get in.
            await AssertRefusedAsync(ws.PublicId, await app.CreateTokenAsync(user, TokenScopes.ReadApi), HttpStatusCode.Forbidden);
            await AssertRefusedAsync(ws.PublicId, null, HttpStatusCode.Unauthorized);
            var stranger = await app.NewUserAsync();
            await AssertRefusedAsync(ws.PublicId, await app.CreateTokenAsync(stranger, TokenScopes.ReadApi | TokenScopes.WriteApi), HttpStatusCode.NotFound);

            // A browser session (cookie) can't open it: a web page could otherwise open a shell for the signed-in user (WebSockets ignore CORS).
            using var browser = await app.LoginClientAsync(user);
            var viaCookie = await browser.GetAsync($"/api/v1/workspaces/{ws.PublicId}/editor/tunnel");
            Assert.Equal(HttpStatusCode.Forbidden, viaCookie.StatusCode);

            // A plain GET with a good token says how to use it.
            using var plain = await app.ApiClientAsync(user);
            plain.BaseAddress = app.ServerAddress;
            Assert.Equal(HttpStatusCode.BadRequest, (await plain.GetAsync($"/api/v1/workspaces/{ws.PublicId}/editor/tunnel")).StatusCode);
        }
        finally { await SetEditorAsync(false); }
    }

    private async Task AssertRefusedAsync(string publicId, string? token, HttpStatusCode expected)
    {
        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        if (token != null) socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        var ex = await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(TunnelUri(publicId), CancellationToken.None));
        Assert.Equal(expected, socket.HttpStatusCode);
        Assert.NotNull(ex);
    }

    [Fact]
    public async Task The_tunnel_is_refused_while_the_switch_is_off_and_after_the_workspace_stops()
    {
        var (user, ws) = await RunningAsync();
        app.Backend.ServeRaw(WorkspaceEditorService.SshPort, "SSH-2.0-BBFake\r\n");
        var token = await app.CreateTokenAsync(user, TokenScopes.ReadApi | TokenScopes.WriteApi);
        await AssertRefusedAsync(ws.PublicId, token, HttpStatusCode.UnprocessableEntity);
        await SetEditorAsync(true);
        try
        {
            await using (var s = app.Scope(app.UserId(user))) await s.Get<WorkspaceService>().StopAsync(ws.PublicId, app.UserId(user));
            await AssertRefusedAsync(ws.PublicId, token, HttpStatusCode.UnprocessableEntity);
        }
        finally { await SetEditorAsync(false); }
    }

    [Fact]
    public void Tunnels_per_workspace_are_capped_and_released()
    {
        var t = new WorkspaceEditorTunnels();
        var leases = Enumerable.Range(0, WorkspaceEditorTunnels.MaxPerWorkspace).Select(_ => t.TryAcquire(7)).ToList();
        Assert.All(leases, Assert.NotNull);
        Assert.Null(t.TryAcquire(7));
        Assert.Equal(WorkspaceEditorTunnels.MaxPerWorkspace, t.Count(7));
        Assert.NotNull(t.TryAcquire(8)); // another workspace is unaffected
        leases[0]!.Dispose();
        leases[0]!.Dispose(); // idempotent
        Assert.Equal(WorkspaceEditorTunnels.MaxPerWorkspace - 1, t.Count(7));
        Assert.NotNull(t.TryAcquire(7));
    }

    [Fact]
    public async Task The_cli_tunnel_command_pipes_stdin_and_stdout_through_the_tunnel()
    {
        var (user, ws) = await RunningAsync();
        app.Backend.ServeRaw(WorkspaceEditorService.SshPort, "SSH-2.0-BBFake\r\n");
        await SetEditorAsync(true);
        try
        {
            var token = await app.CreateTokenAsync(user, TokenScopes.ReadApi | TokenScopes.WriteApi);
            var input = new System.IO.Pipelines.Pipe();
            var output = new System.IO.Pipelines.Pipe();
            var env = new CliEnvironment
            {
                Out = new StringWriter(), Err = new StringWriter(), InputRedirected = true,
                GetVariable = n => n switch { "BYTEBENCH_SERVER" => app.ServerAddress.ToString(), "BYTEBENCH_TOKEN" => token, _ => null },
                OpenRawInput = () => input.Reader.AsStream(), OpenRawOutput = () => output.Writer.AsStream(),
            };
            var run = Task.Run(() => new CliApp(env).RunAsync(["workspace", "tunnel", ws.PublicId]));

            var reader = output.Reader.AsStream();
            var buf = new byte[256];
            var got = new StringBuilder();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!got.ToString().Contains("SSH-2.0-BBFake")) got.Append(Encoding.ASCII.GetString(buf, 0, await reader.ReadAsync(buf, cts.Token)));
            await input.Writer.AsStream().WriteAsync(Encoding.ASCII.GetBytes("hello-from-ssh\n"), cts.Token);
            while (!got.ToString().Contains("hello-from-ssh")) got.Append(Encoding.ASCII.GetString(buf, 0, await reader.ReadAsync(buf, cts.Token)));
            await input.Writer.CompleteAsync(); // ssh exits
            Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("", env.Err.ToString());
        }
        finally { await SetEditorAsync(false); }
    }

    [Fact]
    public async Task The_cli_connect_command_registers_the_key_and_prints_the_ssh_config_and_refuses_a_private_key()
    {
        var (user, ws) = await RunningAsync();
        await SetEditorAsync(true);
        var dir = Directory.CreateTempSubdirectory("bb-cli-key").FullName;
        try
        {
            var token = await app.CreateTokenAsync(user, TokenScopes.ReadApi | TokenScopes.WriteApi);
            var pub = Path.Combine(dir, "id_ed25519.pub");
            await File.WriteAllTextAsync(pub, Ed25519Key() + "\n");
            var priv = Path.Combine(dir, "id_ed25519");
            await File.WriteAllTextAsync(priv, "-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY-----\n");
            CliEnvironment Env(StringWriter o, StringWriter e) => new()
            {
                Out = o, Err = e,
                GetVariable = n => n switch { "BYTEBENCH_SERVER" => app.ServerAddress.ToString(), "BYTEBENCH_TOKEN" => token, _ => null },
            };

            var o1 = new StringWriter(); var e1 = new StringWriter();
            Assert.Equal(0, await new CliApp(Env(o1, e1)).RunAsync(["workspace", "connect", ws.PublicId, "--key", pub]));
            Assert.Contains("ProxyCommand bytebench workspace tunnel " + ws.PublicId, o1.ToString());
            Assert.Contains("Remote-SSH", o1.ToString());

            var o2 = new StringWriter(); var e2 = new StringWriter();
            Assert.Equal(2, await new CliApp(Env(o2, e2)).RunAsync(["workspace", "connect", ws.PublicId, "--key", priv]));
            Assert.Contains("private key", e2.ToString());
            Assert.Equal(2, await new CliApp(Env(new StringWriter(), new StringWriter())).RunAsync(["workspace", "connect", "not-an-id"]));
        }
        finally { await SetEditorAsync(false); Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_tunnel_address_uses_ws_and_wss()
    {
        Assert.Equal("wss://bb.example.com/api/v1/workspaces/abc/editor/tunnel", CliApp.TunnelUri(new Uri("https://bb.example.com/"), "abc").ToString());
        Assert.Equal("ws://127.0.0.1:5108/api/v1/workspaces/abc/editor/tunnel", CliApp.TunnelUri(new Uri("http://127.0.0.1:5108/"), "abc").ToString());
    }
}
