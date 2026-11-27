using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Web;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Workspaces;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ByteBench.Tests;

/// <summary>An outbound connector that never touches the network: every allowed target "connects" to the fake upstream, one name is refused like a private address.</summary>
public sealed class FakeEgressConnector(Func<int> upstreamPort) : IEgressConnector
{
    public List<(string Host, int Port, bool AllowPrivate)> Calls { get; } = [];

    public async Task<Stream> ConnectAsync(string host, int port, bool allowPrivate, CancellationToken ct)
    {
        lock (Calls) Calls.Add((host, port, allowPrivate));
        if (host == "internal.example.com") throw new EgressDeniedException($"{host} resolves to a non-public address (10.0.0.5).");
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, upstreamPort(), ct);
        return client.GetStream();
    }
}

/// <summary>Workspaces on a fake Docker backend and a fake outbound connector.</summary>
public class SharingApp : WorkspaceApp
{
    public FakeEgressConnector Connector { get; private set; } = null!;

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        Connector = new FakeEgressConnector(() => Backend.UpstreamPort);
        services.AddSingleton<IEgressConnector>(Connector);
    }
}

/// <summary>Sharing a workspace (collaborator and viewer), its audit trail and its network policy.</summary>
public class WorkspaceSharingTests(SharingApp app) : IClassFixture<SharingApp>
{
    private int Port => app.ServerAddress.Port;

    private sealed record Setup(string Owner, string Collaborator, string Viewer, Workspace Ws, Repository Repo);

    private async Task<Setup> SetupAsync()
    {
        var owner = await app.NewUserAsync();
        var collaborator = await app.NewUserAsync();
        var viewer = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((owner, ProjectRole.Developer), (collaborator, ProjectRole.Developer), (viewer, ProjectRole.Viewer));
        return new Setup(owner, collaborator, viewer, await app.StartAsync(owner, repo), repo);
    }

    private async Task<ShareInfo> InviteAsync(Setup s, string user, WorkspaceShareRole role)
    {
        await using var scope = app.Scope(app.UserId(s.Owner));
        return await scope.Get<WorkspaceSharingService>().InviteAsync(s.Ws.PublicId, app.UserId(s.Owner), user, role);
    }

    private async Task<string> SetModeAsync(string mode)
    {
        var admin = app.UserId("alice");
        await using var scope = app.Scope(admin);
        var svc = scope.Get<WorkspaceSharingService>();
        var previous = await svc.GetModeAsync();
        await svc.SetModeAsync(mode, admin);
        return previous;
    }

    private static string Ticket(string url) => HttpUtility.ParseQueryString(new Uri(url).Query)["ticket"]!;

    private HttpRequestMessage Req(string host, string path, string? cookie = null)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, path);
        r.Headers.Host = host;
        if (cookie != null) r.Headers.TryAddWithoutValidation("Cookie", cookie);
        return r;
    }

    private async Task<string> SignInAsync(HttpClient http, string user, string publicId)
    {
        var url = await app.TicketUrlAsync(user, publicId);
        var response = await http.SendAsync(Req(new Uri(url).Authority, new Uri(url).PathAndQuery));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }

    // ---------- sharing ----------

    private static IEnumerable<FakeWorkspaceBackend.ExecCall> AsCollaborators(FakeWorkspaceBackend backend, string? containerId) =>
        backend.Execs.Where(e => e.ContainerId == containerId && e.Spec.User.StartsWith("bbc", StringComparison.Ordinal));

    /// <summary>The connection token the collaborator's own IDE server was started with (it goes in on stdin, to the starter script, as their OS user).</summary>
    private static string CollaboratorToken(FakeWorkspaceBackend backend, string? containerId, int slot) =>
        AsCollaborators(backend, containerId).Last(e => e.Spec.User == ExecSpec.CollaboratorUser(slot) && e.Spec.Command[0] == WorkspaceCollaborators.StartCommand).Spec.Stdin!;

    [Fact]
    public async Task A_collaborator_gets_their_own_ticket_session_and_ide_server_with_their_own_token()
    {
        var s = await SetupAsync();
        await InviteAsync(s, s.Collaborator, WorkspaceShareRole.Collaborator);
        using var http = app.Http();
        var ownerCookie = await SignInAsync(http, s.Owner, s.Ws.PublicId);
        var collabCookie = await SignInAsync(http, s.Collaborator, s.Ws.PublicId);
        Assert.NotEqual(ownerCookie, collabCookie);
        var host = $"ide-{s.Ws.PublicId}.localhost:{Port}";
        var ownerToken = app.Backend.ConnectionToken(s.Ws.ContainerId)!;
        var collabToken = CollaboratorToken(app.Backend, s.Ws.ContainerId, 1);
        Assert.NotEqual(ownerToken, collabToken);

        var connectionsBefore = app.Backend.Connections.Count;
        var ownerSeen = await (await http.SendAsync(Req(host, "/echo", ownerCookie))).Content.ReadAsStringAsync();
        Assert.Contains($"vscode-tkn={ownerToken}", ownerSeen);
        Assert.Equal(WorkspaceLifecycle.IdePort, app.Backend.Connections[connectionsBefore].Port); // the owner reaches the owner's server

        var collabResponse = await http.SendAsync(Req(host, "/echo", collabCookie));
        Assert.Equal(HttpStatusCode.OK, collabResponse.StatusCode);
        var collabSeen = await collabResponse.Content.ReadAsStringAsync();
        Assert.Contains($"vscode-tkn={collabToken}", collabSeen); // their own token...
        Assert.DoesNotContain(ownerToken, collabSeen); // ...never the owner's
        Assert.Equal(CollaboratorSession.BasePort + 1, app.Backend.Connections[connectionsBefore + 1].Port); // and their own server, not port 13370

        // The ticket redemption of each person is in the audit trail with their role.
        await using var db = app.Db();
        var redeemed = await db.AuditEvents.Where(a => a.Action == "workspace.ticket_redeemed" && a.TargetId == s.Ws.PublicId).ToListAsync();
        Assert.Contains(redeemed, a => a.ActorId == app.UserId(s.Collaborator) && a.Details!.Contains("Collaborator"));
        Assert.Contains(redeemed, a => a.ActorId == app.UserId(s.Owner) && a.Details!.Contains("Owner"));
        // Both show up as connected to the owner.
        await using var scope = app.Scope(app.UserId(s.Owner));
        var shares = await scope.Get<WorkspaceSharingService>().ListAsync(s.Ws.PublicId, app.UserId(s.Owner));
        Assert.True(shares.Single(x => x.UserName == s.Collaborator).Connected);
        var connected = await scope.Get<WorkspaceSharingService>().ConnectedAsync(s.Ws.PublicId, app.UserId(s.Owner));
        Assert.Contains(connected, c => c.UserName == s.Owner && c.Role == WorkspaceRole.Owner);
        Assert.Contains(connected, c => c.UserName == s.Collaborator && c.Role == WorkspaceRole.Collaborator);
    }

    [Fact]
    public async Task A_collaborator_cannot_use_a_preview_url_to_reach_the_owners_ide_or_another_collaborators_ide()
    {
        var s = await SetupAsync();
        await InviteAsync(s, s.Collaborator, WorkspaceShareRole.Collaborator);
        var collabId = app.UserId(s.Collaborator);
        await using (var scope = app.Scope(collabId))
        {
            var ws = scope.Get<WorkspaceService>();
            foreach (var port in new[] { WorkspaceLifecycle.IdePort, CollaboratorSession.BasePort + 1, CollaboratorSession.BasePort + 2 })
                Assert.Null(await ws.EntryUrlAsync(s.Ws.PublicId, collabId, new Uri(app.Origin), port));
            Assert.NotNull(await ws.EntryUrlAsync(s.Ws.PublicId, collabId, new Uri(app.Origin), 3000)); // an app's port is fine
        }
        // Even a preview ticket minted behind the service's back reaches nothing: the proxy checks the port too.
        var live = app.Runtime.Find(s.Ws.PublicId)!;
        using var http = app.Http();
        var before = app.Backend.Server.Requests.Count;
        foreach (var port in new[] { WorkspaceLifecycle.IdePort, CollaboratorSession.BasePort + 1 })
        {
            var ticket = app.Runtime.IssueTicket(live.Id, collabId, WorkspaceUrls.PreviewAudience(port), app.Origin.TrimEnd('/'));
            var host = $"p{port}-{s.Ws.PublicId}.localhost:{Port}";
            var entered = await http.SendAsync(Req(host, $"/_bb/enter?ticket={ticket}&next=%2F"));
            Assert.Equal(HttpStatusCode.Redirect, entered.StatusCode);
            var cookie = entered.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
            Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(Req(host, "/echo", cookie))).StatusCode);
        }
        Assert.Equal(before, app.Backend.Server.Requests.Count);
    }

    [Fact]
    public async Task A_viewer_never_gets_an_ide_session_not_from_the_service_and_not_from_a_forged_ticket()
    {
        var s = await SetupAsync();
        await InviteAsync(s, s.Viewer, WorkspaceShareRole.Viewer);
        var viewerId = app.UserId(s.Viewer);
        await using (var scope = app.Scope(viewerId))
        {
            Assert.Null(await scope.Get<WorkspaceService>().EntryUrlAsync(s.Ws.PublicId, viewerId, new Uri(app.Origin)));
            Assert.Null(await scope.Get<WorkspaceService>().EntryUrlAsync(s.Ws.PublicId, viewerId, new Uri(app.Origin), 3000));
        }
        // Even a ticket minted behind the service's back is refused: the proxy checks the role, not just the ticket.
        var live = app.Runtime.Find(s.Ws.PublicId)!;
        var forged = app.Runtime.IssueTicket(live.Id, viewerId, WorkspaceUrls.IdeAudience, app.Origin.TrimEnd('/'));
        using var http = app.Http();
        var before = app.Backend.Server.Requests.Count;
        var r = await http.SendAsync(Req($"ide-{s.Ws.PublicId}.localhost:{Port}", $"/_bb/enter?ticket={forged}&next=%2F"));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal(before, app.Backend.Server.Requests.Count);
    }

    [Fact]
    public async Task A_downgrade_to_viewer_ends_the_ide_session_at_once()
    {
        var s = await SetupAsync();
        await InviteAsync(s, s.Collaborator, WorkspaceShareRole.Collaborator);
        using var http = app.Http();
        var cookie = await SignInAsync(http, s.Collaborator, s.Ws.PublicId);
        var host = $"ide-{s.Ws.PublicId}.localhost:{Port}";
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Req(host, "/echo", cookie))).StatusCode);
        await InviteAsync(s, s.Collaborator, WorkspaceShareRole.Viewer);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Req(host, "/echo", cookie))).StatusCode);
    }

    [Fact]
    public async Task Revoking_cuts_the_session_the_open_websocket_and_future_tickets()
    {
        var s = await SetupAsync();
        await InviteAsync(s, s.Collaborator, WorkspaceShareRole.Collaborator);
        using var http = app.Http();
        var cookie = await SignInAsync(http, s.Collaborator, s.Ws.PublicId);
        var host = $"ide-{s.Ws.PublicId}.localhost:{Port}";
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Req(host, "/echo", cookie))).StatusCode);

        // An open websocket (the IDE's connection) is held while the owner revokes.
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, Port);
        var stream = tcp.GetStream();
        var upgrade = $"GET /ws HTTP/1.1\r\nHost: {host}\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nCookie: {cookie}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(upgrade));
        var buf = new byte[4096];
        var n = await stream.ReadAsync(buf);
        Assert.Contains("101", Encoding.ASCII.GetString(buf, 0, n));
        await stream.WriteAsync(Encoding.ASCII.GetBytes("ping"));
        Assert.Equal(4, await stream.ReadAsync(buf));

        await using (var scope = app.Scope(app.UserId(s.Owner)))
            await scope.Get<WorkspaceSharingService>().RevokeAsync(s.Ws.PublicId, app.UserId(s.Owner), app.UserId(s.Collaborator));

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Req(host, "/echo", cookie))).StatusCode);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal(0, await stream.ReadAsync(buf, cts.Token)); // the tunnel was closed by ByteBench
        await using var s2 = app.Scope(app.UserId(s.Collaborator));
        Assert.Null(await s2.Get<WorkspaceService>().EntryUrlAsync(s.Ws.PublicId, app.UserId(s.Collaborator), new Uri(app.Origin)));
        await using var db = app.Db();
        var row = await db.WorkspaceShares.SingleAsync(x => x.WorkspaceId == s.Ws.Id && x.UserId == app.UserId(s.Collaborator));
        Assert.NotNull(row.EndedAt);
        Assert.Contains(await db.AuditEvents.Where(a => a.Action == "workspace.share_ended" && a.TargetId == s.Ws.PublicId).ToListAsync(), a => a.Details!.Contains("revoked"));
    }

    [Fact]
    public async Task Stopping_the_workspace_ends_all_sharing()
    {
        var s = await SetupAsync();
        await InviteAsync(s, s.Collaborator, WorkspaceShareRole.Collaborator);
        await InviteAsync(s, s.Viewer, WorkspaceShareRole.Viewer);
        await using (var scope = app.Scope(app.UserId(s.Owner)))
            await scope.Get<WorkspaceService>().StopAsync(s.Ws.PublicId, app.UserId(s.Owner));
        await using var db = app.Db();
        var rows = await db.WorkspaceShares.Where(x => x.WorkspaceId == s.Ws.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => { Assert.NotNull(r.EndedAt); Assert.Equal("workspace stopped", r.EndReason); });
        Assert.Equal(2, await db.AuditEvents.CountAsync(a => a.Action == "workspace.share_ended" && a.TargetId == s.Ws.PublicId));
        await using var scope2 = app.Scope(app.UserId(s.Viewer));
        Assert.Empty(await scope2.Get<WorkspaceSharingService>().SharedWithMeAsync(app.UserId(s.Viewer)));
    }

    [Fact]
    public async Task Only_people_who_can_view_the_repository_and_by_default_project_members_can_be_invited()
    {
        var owner = await app.NewUserAsync();
        var stranger = await app.NewUserAsync();
        var privateRepo = await app.RepoWithMembersAsync((owner, ProjectRole.Developer));
        var ws = await app.StartAsync(owner, privateRepo);
        await using var scope = app.Scope(app.UserId(owner));
        var sharing = scope.Get<WorkspaceSharingService>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sharing.InviteAsync(ws.PublicId, app.UserId(owner), stranger, WorkspaceShareRole.Viewer));
        Assert.Contains("can't view", ex.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sharing.InviteAsync(ws.PublicId, app.UserId(owner), "nobody-like-this", WorkspaceShareRole.Viewer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sharing.InviteAsync(ws.PublicId, app.UserId(owner), owner, WorkspaceShareRole.Viewer));

        // An internal project: everyone can view it, but only members are invited unless an admin allows more.
        var internalProject = await app.CreateProjectAsync("alice", ProjectVisibility.Internal, (owner, ProjectRole.Developer));
        var internalRepo = await app.CreateRepoAsync(internalProject.Id, "alice");
        var ws2 = await app.StartAsync(owner, internalRepo);
        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(() => sharing.InviteAsync(ws2.PublicId, app.UserId(owner), stranger, WorkspaceShareRole.Viewer));
        Assert.Contains("isn't a member", ex2.Message);
        var previous = await SetModeAsync(WorkspaceSharingMode.Viewers);
        try { await sharing.InviteAsync(ws2.PublicId, app.UserId(owner), stranger, WorkspaceShareRole.Viewer); }
        finally { await SetModeAsync(previous); }
    }

    [Fact]
    public async Task Only_the_owner_can_invite_revoke_or_list()
    {
        var s = await SetupAsync();
        await InviteAsync(s, s.Viewer, WorkspaceShareRole.Viewer);
        await using var scope = app.Scope(app.UserId(s.Collaborator));
        var sharing = scope.Get<WorkspaceSharingService>();
        var id = app.UserId(s.Collaborator);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sharing.InviteAsync(s.Ws.PublicId, id, s.Viewer, WorkspaceShareRole.Collaborator));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sharing.ListAsync(s.Ws.PublicId, id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sharing.RevokeAsync(s.Ws.PublicId, id, app.UserId(s.Viewer)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sharing.EndSharingAsync(s.Ws.PublicId, id));
    }

    [Fact]
    public async Task The_invitee_is_notified_and_sees_the_workspace_under_shared_with_me_until_they_leave()
    {
        var s = await SetupAsync();
        await InviteAsync(s, s.Collaborator, WorkspaceShareRole.Collaborator);
        await using (var db = app.Db())
        {
            var n = await db.Notifications.SingleAsync(x => x.UserId == app.UserId(s.Collaborator) && x.Kind == NotificationKind.WorkspaceShared);
            Assert.Equal($"/workspaces/shared/{s.Ws.PublicId}", n.Url);
            Assert.Contains(s.Owner, n.Excerpt);
        }
        await using var scope = app.Scope(app.UserId(s.Collaborator));
        var sharing = scope.Get<WorkspaceSharingService>();
        var mine = await sharing.SharedWithMeAsync(app.UserId(s.Collaborator));
        Assert.Equal(WorkspaceShareRole.Collaborator, mine.Single(w => w.Workspace.PublicId == s.Ws.PublicId).Role);
        Assert.NotNull(await sharing.GetSharedAsync(s.Ws.PublicId, app.UserId(s.Collaborator)));
        Assert.Null(await sharing.GetSharedAsync(s.Ws.PublicId, app.UserId(s.Viewer))); // not invited
        await sharing.LeaveAsync(s.Ws.PublicId, app.UserId(s.Collaborator));
        Assert.Null(await sharing.GetSharedAsync(s.Ws.PublicId, app.UserId(s.Collaborator)));
    }

    [Fact]
    public async Task The_admin_switch_turns_sharing_off_and_ends_what_is_active()
    {
        var s = await SetupAsync();
        await InviteAsync(s, s.Collaborator, WorkspaceShareRole.Collaborator);
        var outsider = await app.NewUserAsync();
        await using (var scope = app.Scope(app.UserId(outsider)))
            await Assert.ThrowsAsync<ForbiddenException>(() => scope.Get<WorkspaceSharingService>().SetModeAsync(WorkspaceSharingMode.Off, app.UserId(outsider)));
        var previous = await SetModeAsync(WorkspaceSharingMode.Off);
        try
        {
            await using var db = app.Db();
            Assert.NotNull((await db.WorkspaceShares.SingleAsync(x => x.WorkspaceId == s.Ws.Id)).EndedAt);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => InviteAsync(s, s.Viewer, WorkspaceShareRole.Viewer));
            Assert.Contains("turned off", ex.Message);
            Assert.Contains(await db.AuditEvents.Where(a => a.Action == "workspace.admin_sharing").ToListAsync(), a => a.Details!.Contains("off"));
        }
        finally { await SetModeAsync(previous); }
    }

    [Fact]
    public async Task The_viewers_window_reads_files_as_the_unprivileged_user_refuses_traversal_and_runs_only_fixed_commands()
    {
        var s = await SetupAsync();
        await InviteAsync(s, s.Viewer, WorkspaceShareRole.Viewer);
        app.Backend.ExecScripts.Insert(0, (spec => spec.User == "bbai" && spec.Command.Any(a => a.Contains("find \"$p\"")), _ => new ExecResult(0, "d\t4096\tsrc\nf\t12\tREADME.md\n")));
        app.Backend.ExecScripts.Insert(0, (spec => spec.User == "bbai" && spec.Command.Any(a => a.Contains("head -c \"$max\"")), _ => new ExecResult(0, "# hello\n")));
        app.Backend.ExecScripts.Insert(0, (spec => spec.Command.Contains("tmux") && spec.Command.Contains("list-sessions"), _ => new ExecResult(0, "main\n")));
        app.Backend.ExecScripts.Insert(0, (spec => spec.Command.Contains("tmux") && spec.Command.Contains("capture-pane"), _ => new ExecResult(0, "$ npm test\nok\n")));
        var viewerId = app.UserId(s.Viewer);
        await using var scope = app.Scope(viewerId);
        var sharing = scope.Get<WorkspaceSharingService>();

        var entries = await sharing.ListDirectoryAsync(s.Ws.PublicId, viewerId, "");
        Assert.Equal(["src", "README.md"], entries.Select(e => e.Name));
        Assert.True(entries[0].IsDirectory);
        Assert.Equal("# hello\n", (await sharing.ReadFileAsync(s.Ws.PublicId, viewerId, "README.md")).Text);
        Assert.Equal(["main"], await sharing.TerminalSessionsAsync(s.Ws.PublicId, viewerId));
        Assert.Contains("npm test", await sharing.TerminalOutputAsync(s.Ws.PublicId, viewerId, "main"));

        foreach (var bad in new[] { "../etc/passwd", "a/../../b", "src\\..\\x", "a\0b", new string('a', 600) })
            await Assert.ThrowsAsync<InvalidOperationException>(() => sharing.ReadFileAsync(s.Ws.PublicId, viewerId, bad));
        foreach (var bad in new[] { "main; rm -rf /", "$(id)", "a b", "" })
            await Assert.ThrowsAsync<InvalidOperationException>(() => sharing.TerminalOutputAsync(s.Ws.PublicId, viewerId, bad));

        // What ran inside the container for the viewer: fixed scripts with the path as an argument (never in the script), file reads as the unprivileged user,
        // tmux only to list and capture; no keys sent, no shell with viewer input.
        var calls = app.Backend.Execs.Where(e => e.ContainerId == s.Ws.ContainerId).Select(e => e.Spec).ToList();
        var fileCalls = calls.Where(c => c.Command.Any(a => a.Contains("realpath"))).ToList();
        Assert.NotEmpty(fileCalls);
        Assert.All(fileCalls, c => { Assert.Equal("bbai", c.User); Assert.DoesNotContain("..", string.Join(' ', c.Command)); });
        var tmux = calls.Where(c => c.Command.Contains("tmux")).Select(c => c.Command[1]).Distinct().ToList();
        Assert.All(tmux, t => Assert.Contains(t, new[] { "list-sessions", "capture-pane" }));
        Assert.DoesNotContain(calls, c => c.Command.Contains("send-keys"));

        // Someone who wasn't invited gets nothing, and neither does a viewer after being removed.
        var stranger = await app.NewUserAsync();
        await using var sc = app.Scope(app.UserId(stranger));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sc.Get<WorkspaceSharingService>().ListDirectoryAsync(s.Ws.PublicId, app.UserId(stranger), ""));
        await using (var ownerScope = app.Scope(app.UserId(s.Owner)))
            await ownerScope.Get<WorkspaceSharingService>().EndSharingAsync(s.Ws.PublicId, app.UserId(s.Owner));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sharing.ReadFileAsync(s.Ws.PublicId, viewerId, "README.md"));
    }

    [Fact]
    public async Task The_rest_api_invites_lists_and_removes_and_hides_it_from_others()
    {
        var s = await SetupAsync();
        using var owner = await app.ApiClientAsync(s.Owner);
        using var other = await app.ApiClientAsync(s.Collaborator);
        owner.BaseAddress = other.BaseAddress = app.ServerAddress;
        var json = (string body) => new StringContent(body, Encoding.UTF8, "application/json");

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync($"/api/v1/workspaces/{s.Ws.PublicId}/shares", json($$"""{"user":"{{s.Viewer}}","role":"admin"}"""))).StatusCode);
        var created = await owner.PostAsync($"/api/v1/workspaces/{s.Ws.PublicId}/shares", json($$"""{"user":"{{s.Viewer}}","role":"viewer"}"""));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var list = JsonDocument.Parse(await owner.GetStringAsync($"/api/v1/workspaces/{s.Ws.PublicId}/shares")).RootElement;
        Assert.Equal("viewer", list.EnumerateArray().Single().GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/workspaces/{s.Ws.PublicId}/shares")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/v1/workspaces/{s.Ws.PublicId}/shares", json($$"""{"user":"{{s.Viewer}}","role":"viewer"}"""))).StatusCode);

        using var viewer = await app.ApiClientAsync(s.Viewer);
        viewer.BaseAddress = app.ServerAddress;
        Assert.Contains(s.Ws.PublicId, await viewer.GetStringAsync("/api/v1/workspaces/shared"));

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/workspaces/{s.Ws.PublicId}/shares/{s.Viewer}")).StatusCode);
        Assert.DoesNotContain(s.Ws.PublicId, await viewer.GetStringAsync("/api/v1/workspaces/shared"));
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"/api/v1/workspaces/{s.Ws.PublicId}/shares/{s.Viewer}")).StatusCode);
    }

    private static string TokenOf(string credentialLine) => credentialLine.Trim()[(credentialLine.IndexOf(':', credentialLine.IndexOf("://") + 3) + 1)..credentialLine.IndexOf('@')];

    [Fact]
    public async Task A_collaborator_has_no_git_credential_unless_the_owner_allowed_pushing_and_then_only_their_own_and_the_owners_token_stays_the_owners()
    {
        var s = await SetupAsync();
        var ownerCredential = app.Backend.Execs.Where(e => e.ContainerId == s.Ws.ContainerId && e.Spec.User == "coder").Select(e => e.Spec.Stdin).First(x => x != null && x.Contains("gbw_"))!;
        var ownerToken = TokenOf(ownerCredential);
        var ownerId = app.UserId(s.Owner);
        var collabId = app.UserId(s.Collaborator);
        async Task<TokenScopes?> ScopesAsync(int user, string token) => (await WorkspaceGitAuth.ValidateAsync(app.Services, user, token))?.Scopes;
        Assert.True((await ScopesAsync(ownerId, ownerToken))!.Value.HasFlag(TokenScopes.WriteRepository));

        // No "may push": the collaborator's home gets git settings and no credential, and the owner's token is not touched (they can't read it any more).
        await InviteAsync(s, s.Collaborator, WorkspaceShareRole.Collaborator);
        Assert.DoesNotContain(AsCollaborators(app.Backend, s.Ws.ContainerId), e => e.Spec.Stdin != null && e.Spec.Stdin.Contains("gbw_"));
        var prepare = AsCollaborators(app.Backend, s.Ws.ContainerId).Last(e => e.Spec.Command.Contains(WorkspaceCollaborators.PrepareScript));
        Assert.Equal("0", prepare.Spec.Command[^1]); // "no credential"
        Assert.Null(prepare.Spec.Stdin);
        Assert.True((await ScopesAsync(ownerId, ownerToken))!.Value.HasFlag(TokenScopes.WriteRepository)); // not paused: collaborators can't read it
        Assert.Null(await ScopesAsync(collabId, ownerToken)); // and it is the owner's alone

        // "May push": their own credential, in their own home, with their own user name; it is read/write for this repository only.
        await using (var scope = app.Scope(ownerId))
            await scope.Get<WorkspaceSharingService>().InviteAsync(s.Ws.PublicId, ownerId, s.Collaborator, WorkspaceShareRole.Collaborator, allowPush: true);
        var withCredential = AsCollaborators(app.Backend, s.Ws.ContainerId).Last(e => e.Spec.Command.Contains(WorkspaceCollaborators.PrepareScript));
        Assert.Equal("bbc1", withCredential.Spec.User);
        Assert.Equal("1", withCredential.Spec.Command[^1]);
        Assert.StartsWith($"http://{s.Collaborator}:gbw_", withCredential.Spec.Stdin);
        var collabToken = TokenOf(withCredential.Spec.Stdin!);
        Assert.NotEqual(ownerToken, collabToken);
        var granted = await WorkspaceGitAuth.ValidateAsync(app.Services, collabId, collabToken);
        Assert.NotNull(granted);
        Assert.True(granted!.Value.Scopes.HasFlag(TokenScopes.WriteRepository));
        Assert.Equal(s.Repo.Id, granted.Value.RepositoryId);
        Assert.Null(await ScopesAsync(ownerId, collabToken)); // only for the user it was issued to
        Assert.Null(await ScopesAsync(app.UserId(s.Viewer), collabToken));

        // Taking "may push" away invalidates that token at once (and a new grant would mint another one).
        await using (var scope = app.Scope(ownerId))
            await scope.Get<WorkspaceSharingService>().InviteAsync(s.Ws.PublicId, ownerId, s.Collaborator, WorkspaceShareRole.Collaborator);
        Assert.Null(await ScopesAsync(collabId, collabToken));
        Assert.Equal("0", AsCollaborators(app.Backend, s.Ws.ContainerId).Last(e => e.Spec.Command.Contains(WorkspaceCollaborators.PrepareScript)).Spec.Command[^1]);

        await using (var scope = app.Scope(ownerId))
            await scope.Get<WorkspaceSharingService>().InviteAsync(s.Ws.PublicId, ownerId, s.Collaborator, WorkspaceShareRole.Collaborator, allowPush: true);
        var second = TokenOf(AsCollaborators(app.Backend, s.Ws.ContainerId).Last(e => e.Spec.Command.Contains(WorkspaceCollaborators.PrepareScript)).Spec.Stdin!);
        Assert.NotEqual(collabToken, second);
        Assert.Null(await ScopesAsync(collabId, collabToken));
        Assert.NotNull(await ScopesAsync(collabId, second));

        // Revoking ends the token, the processes and the home of that OS user.
        await using (var scope = app.Scope(ownerId))
            await scope.Get<WorkspaceSharingService>().RevokeAsync(s.Ws.PublicId, ownerId, collabId);
        Assert.Null(await ScopesAsync(collabId, second));
        Assert.True((await ScopesAsync(ownerId, ownerToken))!.Value.HasFlag(TokenScopes.WriteRepository));
        var cleanup = AsCollaborators(app.Backend, s.Ws.ContainerId).Where(e => e.Spec.User == "bbc1").Select(e => e.Spec.Command[^1]).ToList();
        Assert.Contains(WorkspaceCollaborators.KillScript, cleanup);
        Assert.Contains(WorkspaceCollaborators.WipeScript, cleanup);
    }

    [Fact]
    public async Task Everything_secret_is_written_as_the_owner_never_to_a_collaborators_user_and_the_home_is_made_private()
    {
        var s = await SetupAsync();
        await InviteAsync(s, s.Collaborator, WorkspaceShareRole.Collaborator);
        var ownerToken = app.Backend.ConnectionToken(s.Ws.ContainerId)!;
        var ownerExecs = app.Backend.Execs.Where(e => e.ContainerId == s.Ws.ContainerId && !e.Spec.User.StartsWith("bbc", StringComparison.Ordinal)).ToList();
        // The owner's secret files exist, written as the owner (mode 600 in a home that is mode 700)...
        Assert.Contains(ownerExecs, e => e.Spec.User == "coder" && e.Spec.Command.Any(a => a.Contains("$HOME/.bb-env")));
        Assert.Contains(ownerExecs, e => e.Spec.User == "coder" && e.Spec.Command.Any(a => a.Contains("$HOME/.git-credentials")));
        Assert.Contains(ownerExecs, e => e.Spec.Command.SequenceEqual(["chmod", "700", "/home/coder"]));
        Assert.Contains(ownerExecs, e => e.Spec.Command.Any(a => a.Contains("umask 077; cat > /tmp/.bb-token")));
        // ...and nothing of it ever went through a collaborator's user, nor does any of their commands touch the owner's files.
        var theirs = AsCollaborators(app.Backend, s.Ws.ContainerId).ToList();
        Assert.NotEmpty(theirs);
        Assert.All(theirs, e =>
        {
            Assert.DoesNotContain(ownerToken, e.Spec.Stdin ?? "");
            Assert.DoesNotContain("gbw_", e.Spec.Stdin ?? "");
            Assert.DoesNotContain("/home/coder", string.Join(' ', e.Spec.Command));
            Assert.DoesNotContain("/tmp/.bb-token", string.Join(' ', e.Spec.Command));
        });
        // The collaborator's user is a numbered slot, never coder (uid 1000) or root.
        Assert.All(theirs, e => Assert.Matches(@"^20(0[1-9]|10):1000$", ExecSpec.UserSpec(e.Spec.User)));
        Assert.Equal("1000:1000", ExecSpec.UserSpec("coder"));
        Assert.Equal("1001:1000", ExecSpec.UserSpec("bbai"));
        Assert.Equal("1000:1000", ExecSpec.UserSpec("bbc11")); // never an unknown or out-of-range uid
        Assert.Equal("1000:1000", ExecSpec.UserSpec("root"));
    }

    [Fact]
    public async Task Collaborators_get_separate_slots_the_slot_is_wiped_and_reused_and_they_run_out_at_the_limit()
    {
        var s = await SetupAsync();
        var second = await app.NewUserAsync();
        await using (var scope = app.Scope(app.UserId("alice")))
            await scope.Get<MemberService>().AddMemberAsync(s.Repo.ProjectId, second, ProjectRole.Developer);
        await InviteAsync(s, s.Collaborator, WorkspaceShareRole.Collaborator);
        await InviteAsync(s, second, WorkspaceShareRole.Collaborator);
        var live = app.Runtime.Find(s.Ws.PublicId)!;
        var a = app.Runtime.Access.CollaboratorOf(live.Id, app.UserId(s.Collaborator))!;
        var b = app.Runtime.Access.CollaboratorOf(live.Id, app.UserId(second))!;
        Assert.NotEqual(a.Slot, b.Slot);
        Assert.NotEqual(a.Port, b.Port);
        Assert.NotEqual(a.ConnectionToken, b.ConnectionToken); // neither can open the other's IDE
        // Re-inviting is idempotent: the same slot and token.
        await InviteAsync(s, s.Collaborator, WorkspaceShareRole.Collaborator);
        Assert.Equal(a.Slot, app.Runtime.Access.CollaboratorOf(live.Id, app.UserId(s.Collaborator))!.Slot);
        Assert.Equal(a.ConnectionToken, app.Runtime.Access.CollaboratorOf(live.Id, app.UserId(s.Collaborator))!.ConnectionToken);
        // Leaving frees the slot (after the wipe) and a newcomer takes it, with a fresh token.
        await using (var scope = app.Scope(app.UserId(s.Collaborator)))
            await scope.Get<WorkspaceSharingService>().LeaveAsync(s.Ws.PublicId, app.UserId(s.Collaborator));
        Assert.Null(app.Runtime.Access.CollaboratorOf(live.Id, app.UserId(s.Collaborator)));
        var all = app.Backend.Execs.Where(e => e.ContainerId == s.Ws.ContainerId && e.Spec.User == ExecSpec.CollaboratorUser(a.Slot)).Select(e => e.Spec.Command[^1]).ToList();
        Assert.True(all.LastIndexOf(WorkspaceCollaborators.WipeScript) > all.LastIndexOf(WorkspaceCollaborators.KillScript));
        var third = await app.NewUserAsync();
        await using (var scope = app.Scope(app.UserId("alice")))
            await scope.Get<MemberService>().AddMemberAsync(s.Repo.ProjectId, third, ProjectRole.Developer);
        await InviteAsync(s, third, WorkspaceShareRole.Collaborator);
        var c = app.Runtime.Access.CollaboratorOf(live.Id, app.UserId(third))!;
        Assert.Equal(a.Slot, c.Slot);
        Assert.NotEqual(a.ConnectionToken, c.ConnectionToken);
    }

    [Fact]
    public async Task A_restart_of_the_container_starts_the_collaborators_ide_servers_again_with_the_same_tokens()
    {
        var s = await SetupAsync();
        await InviteAsync(s, s.Collaborator, WorkspaceShareRole.Collaborator);
        var starts = () => AsCollaborators(app.Backend, s.Ws.ContainerId).Count(e => e.Spec.Command[0] == WorkspaceCollaborators.StartCommand);
        var before = starts();
        var token = CollaboratorToken(app.Backend, s.Ws.ContainerId, 1);
        await app.Services.GetRequiredService<WorkspaceLifecycle>().RestartAsync(s.Ws.Id, app.UserId(s.Owner));
        Assert.Equal(before + 1, starts());
        Assert.Equal(token, CollaboratorToken(app.Backend, s.Ws.ContainerId, 1));
        Assert.Equal(WorkspaceRole.Collaborator, app.Runtime.Access.RoleOf(s.Ws.Id, app.UserId(s.Owner), app.UserId(s.Collaborator)));
    }

    [Fact]
    public async Task A_collaborator_whose_ide_cannot_be_started_is_not_invited()
    {
        var s = await SetupAsync();
        app.Backend.ExecScripts.Insert(0, (spec => spec.Command.Count > 0 && spec.Command[0] == WorkspaceCollaborators.StartCommand, _ => new ExecResult(1, "bb-collab-ide: the IDE did not come up")));
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => InviteAsync(s, s.Collaborator, WorkspaceShareRole.Collaborator));
            Assert.Contains("separate user", ex.Message);
        }
        finally { app.Backend.ExecScripts.RemoveAt(0); }
        Assert.Equal(WorkspaceRole.None, app.Runtime.Access.RoleOf(s.Ws.Id, app.UserId(s.Owner), app.UserId(s.Collaborator)));
        Assert.Null(app.Runtime.Access.CollaboratorOf(s.Ws.Id, app.UserId(s.Collaborator)));
        await using var scope = app.Scope(app.UserId(s.Collaborator));
        Assert.Null(await scope.Get<WorkspaceService>().EntryUrlAsync(s.Ws.PublicId, app.UserId(s.Collaborator), new Uri(app.Origin)));
    }

    [Fact]
    public async Task The_image_keeps_the_owners_home_private_and_gives_collaborators_users_of_their_own()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ByteBench.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var docker = Path.Combine(dir!.FullName, "docker", "workspace");
        var dockerfile = await File.ReadAllTextAsync(Path.Combine(docker, "Dockerfile"));
        Assert.Contains("chmod 700 /home/coder", dockerfile);
        Assert.Contains("useradd -m -u $((2000 + i)) -g 1000", dockerfile); // bbc1..bbc10: uid 2001..2010, group coder
        Assert.Contains("bb-collab-ide.sh /usr/local/bin/bb-collab-ide", dockerfile);
        Assert.Contains("core.sharedRepository group", dockerfile);
        var script = await File.ReadAllTextAsync(Path.Combine(docker, "bb-collab-ide.sh"));
        Assert.Contains("--host 127.0.0.1", script); // never reachable from outside the container
        Assert.Contains("--connection-token-file", script);
        Assert.Contains("umask 077; cat > \"$HOME/.bb-token\"", script);
        Assert.DoesNotContain("/tmp/.bb-token", script); // the owner's token file is never read
        var entry = await File.ReadAllTextAsync(Path.Combine(docker, "bb-entrypoint.sh"));
        Assert.Contains("chmod 700 \"$HOME\"", entry);
    }

    [Fact]
    public async Task Viewers_never_see_environment_files_keys_or_secret_folders()
    {
        var s = await SetupAsync();
        await InviteAsync(s, s.Viewer, WorkspaceShareRole.Viewer);
        app.Backend.ExecScripts.Insert(0, (spec => spec.User == "bbai" && spec.Command.Any(a => a.Contains("find \"$p\"")), _ => new ExecResult(0, "f\t5\t.env\nf\t5\t.env.local\nf\t5\tserver.pem\nf\t5\tid_rsa\nd\t0\tsecrets\nf\t3\tapp.cs\n")));
        var viewerId = app.UserId(s.Viewer);
        await using var scope = app.Scope(viewerId);
        var sharing = scope.Get<WorkspaceSharingService>();
        Assert.Equal(["app.cs"], (await sharing.ListDirectoryAsync(s.Ws.PublicId, viewerId, "")).Select(e => e.Name));
        foreach (var path in new[] { ".env", "sub/.env.production", "certs/server.pem", ".ssh/id_ed25519", "secrets/db.txt", ".bb-env", "x/.git-credentials" })
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sharing.ReadFileAsync(s.Ws.PublicId, viewerId, path));
            Assert.Contains("viewers", ex.Message);
        }
        // the container-side check (after symlinks are resolved) carries the same deny-list
        Assert.All(app.Backend.Execs.Where(e => e.Spec.User == "bbai" && e.Spec.Command.Any(a => a.Contains("realpath"))), e => Assert.Contains("*/.env", string.Join(' ', e.Spec.Command)));
        Assert.True(WorkspaceSharingService.IsHiddenFromViewers("ID_RSA"));
        Assert.False(WorkspaceSharingService.IsHiddenFromViewers("environment.cs"));
    }

    [Fact]
    public async Task Viewers_and_collaborators_cannot_run_tasks_or_reach_the_environment()
    {
        var s = await SetupAsync();
        await InviteAsync(s, s.Collaborator, WorkspaceShareRole.Collaborator);
        await InviteAsync(s, s.Viewer, WorkspaceShareRole.Viewer);
        foreach (var user in new[] { s.Collaborator, s.Viewer })
        {
            await using var scope = app.Scope(app.UserId(user));
            var svc = scope.Get<WorkspaceService>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RunTaskAsync(s.Ws.PublicId, app.UserId(user), "test"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StopTaskAsync(s.Ws.PublicId, app.UserId(user), "test"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.GetEnvironmentAsync(s.Ws.PublicId, app.UserId(user)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StopAsync(s.Ws.PublicId, app.UserId(user)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CommitAsync(s.Ws.PublicId, app.UserId(user), "x", false, null));
        }
    }

    // ---------- audit ----------

    [Fact]
    public async Task The_audit_trail_covers_the_lifecycle_shares_and_ticket_redemptions_for_the_owner_and_admins_only()
    {
        var s = await SetupAsync();
        await InviteAsync(s, s.Collaborator, WorkspaceShareRole.Collaborator);
        using var http = app.Http();
        await SignInAsync(http, s.Owner, s.Ws.PublicId);
        await using (var scope = app.Scope(app.UserId(s.Owner)))
            await scope.Get<WorkspaceService>().StopAsync(s.Ws.PublicId, app.UserId(s.Owner));

        await using var ownerScope = app.Scope(app.UserId(s.Owner));
        var history = await ownerScope.Get<WorkspaceAuditService>().HistoryAsync(s.Ws.PublicId, app.UserId(s.Owner));
        var actions = history.Events.Select(e => e.Action).ToList();
        foreach (var expected in new[] { "workspace.created", "workspace.started", "workspace.shared", "workspace.ticket_redeemed", "workspace.share_ended", "workspace.stopped" })
            Assert.Contains(expected, actions);
        Assert.True(history.Events.SequenceEqual(history.Events.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id))); // newest first

        // Anyone else is told there is no such workspace; admins may read it.
        await using var other = app.Scope(app.UserId(s.Collaborator));
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.Get<WorkspaceAuditService>().HistoryAsync(s.Ws.PublicId, app.UserId(s.Collaborator)));
        await using var admin = app.Scope(app.UserId("alice"));
        Assert.NotEmpty((await admin.Get<WorkspaceAuditService>().HistoryAsync(s.Ws.PublicId, app.UserId("alice"))).Events);
    }

    [Fact]
    public async Task The_admin_search_filters_by_workspace_actor_action_text_and_date_and_is_admin_only()
    {
        var s = await SetupAsync();
        await InviteAsync(s, s.Viewer, WorkspaceShareRole.Viewer);
        var admin = app.UserId("alice");
        await using var scope = app.Scope(admin);
        var audit = scope.Get<WorkspaceAuditService>();

        var all = await audit.SearchAsync(admin, new WorkspaceAuditQuery { Workspace = s.Ws.PublicId });
        Assert.All(all.Events, e => Assert.Equal(s.Ws.PublicId, e.TargetId));
        Assert.All(all.Events, e => Assert.StartsWith("workspace.", e.Action));
        var shared = await audit.SearchAsync(admin, new WorkspaceAuditQuery { Workspace = s.Ws.PublicId, Action = "workspace.share" });
        Assert.Equal(["workspace.shared"], shared.Events.Select(e => e.Action));
        var byActor = await audit.SearchAsync(admin, new WorkspaceAuditQuery { Workspace = s.Ws.PublicId, Actor = s.Owner });
        Assert.NotEmpty(byActor.Events);
        Assert.All(byActor.Events, e => Assert.Equal(s.Owner, e.ActorName));
        var byText = await audit.SearchAsync(admin, new WorkspaceAuditQuery { Text = s.Viewer });
        Assert.Contains(byText.Events, e => e.Action == "workspace.shared");
        Assert.Empty((await audit.SearchAsync(admin, new WorkspaceAuditQuery { Workspace = s.Ws.PublicId, ToUtc = DateTime.UtcNow.AddDays(-1) })).Events);
        Assert.Single((await audit.SearchAsync(admin, new WorkspaceAuditQuery { Workspace = s.Ws.PublicId, PageSize = 1 })).Events);
        Assert.Contains("workspace.shared", await audit.ActionNamesAsync(admin));

        var outsider = app.UserId(s.Collaborator);
        await using var os = app.Scope(outsider);
        await Assert.ThrowsAsync<ForbiddenException>(() => os.Get<WorkspaceAuditService>().SearchAsync(outsider, new WorkspaceAuditQuery()));
    }

    [Fact]
    public async Task Secrets_are_recorded_by_name_never_by_value()
    {
        var s = await SetupAsync();
        var audit = app.Services.GetRequiredService<WorkspaceAudit>();
        await audit.RecordAsync(WorkspaceAuditActions.SecretCreated, s.Ws.PublicId, s.Ws.ProjectId, app.UserId(s.Owner),
            new { names = new[] { "DATABASE_URL" }, value = "hunter2", secret = "hunter2", token = "hunter2" });
        await audit.RecordAsync(WorkspaceAuditActions.SecretInjected, s.Ws.PublicId, s.Ws.ProjectId, app.UserId(s.Owner), new { names = new[] { "DATABASE_URL", "API_KEY" }, count = 2 });
        await using var db = app.Db();
        var rows = await db.AuditEvents.Where(a => a.TargetId == s.Ws.PublicId && a.Action.Contains("secret")).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.DoesNotContain("hunter2", r.Details));
        Assert.Contains(rows, r => r.Details!.Contains("DATABASE_URL"));
    }

    [Fact]
    public async Task Retention_deletes_old_workspace_events_only_and_the_export_is_csv_safe_and_audited()
    {
        var s = await SetupAsync();
        var admin = app.UserId("alice");
        await using (var db = app.Db())
        {
            db.AuditEvents.AddRange(
                new AuditEvent { CreatedAt = DateTime.UtcNow.AddDays(-40), Action = "workspace.started", TargetType = "workspace", TargetId = "oldworkspace0000000000" },
                new AuditEvent { CreatedAt = DateTime.UtcNow.AddDays(-40), Action = "auth.sign_in", TargetType = "user", TargetId = "1" },
                new AuditEvent { CreatedAt = DateTime.UtcNow.AddDays(-2), Action = "workspace.stopped", TargetType = "workspace", TargetId = "recentworkspace00000", ActorName = "=cmd|' /C calc'!A0" });
            await db.SaveChangesAsync();
        }
        await using (var scope = app.Scope(admin))
        {
            var audit = scope.Get<WorkspaceAuditService>();
            await Assert.ThrowsAsync<ForbiddenException>(() => scope.Get<WorkspaceAuditService>().SetRetentionDaysAsync(30, app.UserId(s.Collaborator)));
            await audit.SetRetentionDaysAsync(30, admin);
        }
        try
        {
            var maintenance = app.Services.GetServices<IHostedService>().OfType<WorkspaceMaintenanceService>().Single();
            await maintenance.RunOnceAsync(CancellationToken.None, forceRetention: true);
            await using var db = app.Db();
            Assert.False(await db.AuditEvents.AnyAsync(a => a.TargetId == "oldworkspace0000000000"));
            Assert.True(await db.AuditEvents.AnyAsync(a => a.Action == "auth.sign_in" && a.CreatedAt < DateTime.UtcNow.AddDays(-30))); // other events are the site setting's business
            Assert.True(await db.AuditEvents.AnyAsync(a => a.TargetId == "recentworkspace00000"));
            Assert.Contains(await db.AuditEvents.Where(a => a.Action == "workspace.admin_audit_retention").ToListAsync(), a => a.Details!.Contains("30"));

            using var client = await app.LoginClientAsync("alice");
            var csv = await client.GetAsync("/admin/workspaces/audit/export.csv?action=workspace.stopped");
            Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
            Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
            var text = await csv.Content.ReadAsStringAsync();
            Assert.StartsWith("time_utc,actor_id,actor,ip,user_agent,action", text);
            Assert.Contains("'=cmd", text); // formulas are neutralised
            Assert.Contains("recentworkspace00000", text);
            Assert.True(await db.AuditEvents.AnyAsync(a => a.Action == "workspace.audit_exported"));

            using var asOwner = await app.LoginClientAsync(s.Owner);
            Assert.Equal(HttpStatusCode.Forbidden, (await asOwner.GetAsync("/admin/workspaces/audit/export.csv")).StatusCode);
            var mine = await asOwner.GetAsync($"/workspaces/{s.Ws.PublicId}/audit/export.csv");
            Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
            Assert.Contains("workspace.started", await mine.Content.ReadAsStringAsync());
            using var asOther = await app.LoginClientAsync(s.Collaborator);
            Assert.Equal(HttpStatusCode.NotFound, (await asOther.GetAsync($"/workspaces/{s.Ws.PublicId}/audit/export.csv")).StatusCode);
        }
        finally { await using var scope = app.Scope(admin); await scope.Get<WorkspaceAuditService>().SetRetentionDaysAsync(0, admin); }
    }

    [Fact]
    public async Task The_audit_rest_endpoints_page_filter_and_hide_other_workspaces()
    {
        var s = await SetupAsync();
        await InviteAsync(s, s.Viewer, WorkspaceShareRole.Viewer);
        using var owner = await app.ApiClientAsync(s.Owner);
        using var other = await app.ApiClientAsync(s.Collaborator);
        using var admin = await app.ApiClientAsync("alice");
        owner.BaseAddress = other.BaseAddress = admin.BaseAddress = app.ServerAddress;
        var page = JsonDocument.Parse(await owner.GetStringAsync($"/api/v1/workspaces/{s.Ws.PublicId}/audit?action=workspace.share&size=5")).RootElement;
        Assert.Equal("workspace.shared", page.GetProperty("items")[0].GetProperty("action").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/workspaces/{s.Ws.PublicId}/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/v1/admin/workspaces/audit")).StatusCode);
        var all = JsonDocument.Parse(await admin.GetStringAsync($"/api/v1/admin/workspaces/audit?workspace={s.Ws.PublicId}&actor={s.Owner}")).RootElement;
        Assert.True(all.GetProperty("total").GetInt32() >= 2);
    }
}
