using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Editor.Ai;
using ByteBench.Services.Workspaces;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ByteBench.Tests;

/// <summary>The app on Kestrel with workspaces on a fake Docker backend and a scripted AI model.</summary>
public class WorkspaceApp : KestrelApp
{
    public FakeWorkspaceBackend Backend { get; } = new();
    public FakeEditorChatModel Model { get; } = new();

    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["Workspaces:Image"] = "test-image:1";
        settings["Workspaces:BaseDomain"] = "localhost";
        settings["Workspaces:MaxTotal"] = "1000";
        settings["Workspaces:MaxPerUser"] = "2";
        settings["Workspaces:ReaperIntervalSeconds"] = "3600"; // sweeps are driven by the tests
        settings["Workspaces:GitBaseUrl"] = "http://127.0.0.1:9";
        settings["Workspaces:Ai:RequestsPerMinute"] = "1000";
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IWorkspaceBackend>(Backend);
        services.AddSingleton<IEditorChatModel>(Model);
    }

    public WorkspaceRuntime Runtime => Services.GetRequiredService<WorkspaceRuntime>();
    public Task WhenIdleAsync() => Runtime.WhenIdleAsync();

    public string Origin => $"http://127.0.0.1:{ServerAddress.Port}/";

    private static int counter;

    /// <summary>A project with a repository and these members (alice owns it); returns the repository.</summary>
    public async Task<Repository> RepoWithMembersAsync(params (string User, ProjectRole Role)[] members)
    {
        var project = await CreateProjectAsync("alice", ProjectVisibility.Private, members);
        return await CreateRepoAsync(project.Id, "alice", "code");
    }

    public async Task<string> NewUserAsync(string prefix = "wsu")
    {
        var name = prefix + Interlocked.Increment(ref counter);
        await CreateUserAsync(name);
        return name;
    }

    public async Task<Workspace> StartAsync(string user, Repository repo, string? reference = "main", string? source = null)
    {
        await using var s = Scope(UserId(user));
        var ws = await s.Get<WorkspaceService>().StartAsync(ProjectKey(repo.ProjectId), repo.Slug, reference, source, UserId(user));
        await WhenIdleAsync();
        return await ReloadAsync(ws.PublicId);
    }

    public async Task<Workspace> ReloadAsync(string publicId)
    {
        await using var db = Db();
        return await db.Workspaces.AsNoTracking().FirstAsync(w => w.PublicId == publicId);
    }

    public async Task<string> TicketUrlAsync(string user, string publicId, int? port = null)
    {
        await using var s = Scope(UserId(user));
        return (await s.Get<WorkspaceService>().EntryUrlAsync(publicId, UserId(user), new Uri(Origin), port))!;
    }

    /// <summary>A client with a cookie jar, signed in through the real login form (on the real Kestrel address).</summary>
    public async Task<HttpClient> LoginClientAsync(string user)
    {
        var client = new HttpClient(new HttpClientHandler { UseCookies = true, AllowAutoRedirect = false }) { BaseAddress = ServerAddress };
        var page = await client.GetStringAsync("/account/login");
        var token = System.Text.RegularExpressions.Regex.Match(page, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        var response = await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "login", ["__RequestVerificationToken"] = token, ["Input.UserNameOrEmail"] = user, ["Input.Password"] = SeedData.DemoPassword,
        }));
        if ((int)response.StatusCode is not (302 or 303)) throw new InvalidOperationException($"Sign-in as {user} failed: {(int)response.StatusCode}");
        return client;
    }

    public HttpClient Http()
    {
        var handler = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false };
        return new HttpClient(handler) { BaseAddress = ServerAddress };
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) Backend.Dispose();
    }
}

public class WorkspaceStartTests(WorkspaceApp app) : IClassFixture<WorkspaceApp>
{
    [Fact]
    public async Task Start_provisions_in_order_and_ends_running()
    {
        var user = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer));
        var ws = await app.StartAsync(user, repo, "main", "pr:4");

        Assert.Equal(WorkspaceState.Running, ws.State);
        Assert.True(ws.CanPush);
        Assert.Equal("pr:4", ws.Source);
        Assert.NotNull(ws.StartedAt);
        Assert.Equal(40, ws.CommitSha!.Length);
        var spec = app.Backend.Started.Last(s => s.WorkspaceId == ws.Id);
        Assert.Equal("test-image:1", spec.Image);
        Assert.Equal("/workspace/code", spec.Env["BB_WORKDIR"]);
        Assert.Contains(app.Backend.EgressApplied, p => p.Mode == "public-only" && p.Allow.Count == 1 && p.Allow[0] == "127.0.0.1:9");

        // The network policy and credentials are in place before the IDE is allowed to start (MarkReady is last).
        var calls = app.Backend.Execs.Where(e => e.ContainerId == ws.ContainerId).Select(e => string.Join(' ', e.Spec.Command)).ToList();
        Assert.Contains(calls, c => c.Contains("git clone"));
        Assert.Contains(calls, c => c.Contains("/tmp/.bb-token"));
        Assert.Contains(ws.ContainerId!, app.Backend.Ready);
        Assert.True(WorkspaceTokens.Hash(app.Backend.ConnectionToken(ws.ContainerId)!).SequenceEqual(ws.ConnectionTokenHash));
        Assert.NotNull(ws.GitTokenHash);
    }

    [Fact]
    public async Task The_git_token_only_goes_through_stdin_never_a_command_line_or_the_environment()
    {
        var user = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer));
        var ws = await app.StartAsync(user, repo);
        var token = app.Backend.GitToken();
        Assert.StartsWith("gbw_", token);
        foreach (var e in app.Backend.Execs.Where(e => e.ContainerId == ws.ContainerId))
        {
            Assert.DoesNotContain(e.Spec.Command, a => a.Contains(token!));
            Assert.DoesNotContain(e.Spec.Env ?? new Dictionary<string, string>(), kv => kv.Value.Contains(token!));
        }
        Assert.DoesNotContain(app.Backend.Started.Last().Env, kv => kv.Value.Contains(token!));
    }

    [Fact]
    public async Task A_failed_checkout_marks_the_workspace_failed_removes_the_container_and_revokes_the_token()
    {
        var user = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer));
        app.Backend.FailClone = true;
        try
        {
            var ws = await app.StartAsync(user, repo);
            Assert.Equal(WorkspaceState.Failed, ws.State);
            Assert.Contains("Checking out the repository failed", ws.Error);
            Assert.DoesNotContain("gbw_", ws.Error);
            Assert.Null(ws.GitTokenHash);
            Assert.Contains(app.Backend.Removed, id => id == ws.ContainerId);
            await using var db = app.Db();
            Assert.Contains(await db.AuditEvents.Where(a => a.Action == "workspace.failed").ToListAsync(), a => a.TargetId == ws.PublicId);
        }
        finally { app.Backend.FailClone = false; }
    }

    [Fact]
    public async Task Users_without_access_get_no_workspace_and_no_hint_the_repository_exists()
    {
        var outsider = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync();
        await using var s = app.Scope(app.UserId(outsider));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.Get<WorkspaceService>().StartAsync(app.ProjectKey(repo.ProjectId), repo.Slug, "main", null, app.UserId(outsider)));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task Invalid_unknown_and_option_like_refs_are_refused()
    {
        var user = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer));
        await using var s = app.Scope(app.UserId(user));
        var svc = s.Get<WorkspaceService>();
        foreach (var bad in new[] { "--upload-pack=x", "-b", "a..b", "x;rm -rf /", "nonexistent-branch", "a b" })
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(app.ProjectKey(repo.ProjectId), repo.Slug, bad, null, app.UserId(user)));
    }

    [Fact]
    public async Task Per_user_cap_is_enforced_and_freed_on_stop()
    {
        var user = await app.NewUserAsync();
        var other = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer), (other, ProjectRole.Developer));
        var a = await app.StartAsync(user, repo);
        await app.StartAsync(user, repo);
        await using var s = app.Scope(app.UserId(user));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.Get<WorkspaceService>().StartAsync(app.ProjectKey(repo.ProjectId), repo.Slug, "main", null, app.UserId(user)));
        Assert.Contains("limit", ex.Message);
        await app.StartAsync(other, repo); // another user is unaffected
        await s.Get<WorkspaceService>().StopAsync(a.PublicId, app.UserId(user));
        await app.StartAsync(user, repo);
    }

    [Fact]
    public async Task The_project_switch_turns_workspaces_off_for_that_project_only()
    {
        var user = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer));
        var owner = app.UserId("alice");
        await using (var s = app.Scope(owner))
            await s.Get<WorkspaceService>().SetProjectSettingsAsync(repo.ProjectId, false, true, owner);
        await using (var s = app.Scope(app.UserId(user)))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                s.Get<WorkspaceService>().StartAsync(app.ProjectKey(repo.ProjectId), repo.Slug, "main", null, app.UserId(user)));
            Assert.Contains("project", ex.Message);
            // Only maintainers may flip it.
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<WorkspaceService>().SetProjectSettingsAsync(repo.ProjectId, true, true, app.UserId(user)));
        }
    }

    [Fact]
    public async Task The_admin_switch_refuses_new_workspaces_only_admins_can_flip_it_and_it_is_audited()
    {
        var user = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer));
        var admin = app.UserId("alice");
        await using (var s = app.Scope(app.UserId(user)))
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<WorkspaceService>().SetEnabledAsync(false, app.UserId(user)));
        await using (var s = app.Scope(admin)) await s.Get<WorkspaceService>().SetEnabledAsync(false, admin);
        try
        {
            await using var s = app.Scope(app.UserId(user));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                s.Get<WorkspaceService>().StartAsync(app.ProjectKey(repo.ProjectId), repo.Slug, "main", null, app.UserId(user)));
            Assert.Contains("turned off", ex.Message);
        }
        finally { await using var s = app.Scope(admin); await s.Get<WorkspaceService>().SetEnabledAsync(true, admin); }
        await using var db = app.Db();
        Assert.Contains(await db.AuditEvents.Where(a => a.Action == "workspace.admin_switch").ToListAsync(), a => a.Details!.Contains("false"));
    }

    [Fact]
    public async Task Without_docker_or_the_image_start_explains_and_the_image_is_built_on_first_use()
    {
        var user = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer));
        app.Backend.DockerAvailable = false;
        try
        {
            await using var s = app.Scope(app.UserId(user));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                s.Get<WorkspaceService>().StartAsync(app.ProjectKey(repo.ProjectId), repo.Slug, "main", null, app.UserId(user)));
            Assert.Contains("Docker isn't available", ex.Message);
        }
        finally { app.Backend.DockerAvailable = true; }

        app.Backend.ImageExists = false;
        var image = app.Services.GetRequiredService<WorkspaceImage>();
        await image.EnsureAsync();
        await image.WhenIdleAsync();
        Assert.Equal(WorkspaceImageState.Ready, image.State);
        Assert.True(app.Backend.ImageExists);
    }

    [Fact]
    public async Task Stop_removes_the_container_revokes_tokens_and_cuts_access_at_once()
    {
        var user = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer));
        var ws = await app.StartAsync(user, repo);
        await using (var s = app.Scope(app.UserId(user))) await s.Get<WorkspaceService>().StopAsync(ws.PublicId, app.UserId(user));
        var after = await app.ReloadAsync(ws.PublicId);
        Assert.Equal(WorkspaceState.Stopped, after.State);
        Assert.Null(after.GitTokenHash);
        Assert.Contains(ws.ContainerId!, app.Backend.Removed);
        Assert.Null(app.Runtime.Find(ws.PublicId));
        Assert.Null(await app.TicketUrlAsync(user, ws.PublicId));
        await using var db = app.Db();
        Assert.Contains(await db.AuditEvents.Where(a => a.Action == "workspace.stopped").ToListAsync(), a => a.TargetId == ws.PublicId);
    }

    [Fact]
    public async Task Listening_ports_are_parsed_from_proc_net_tcp()
    {
        const string tcp = """
              sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
               0: 00000000:0BB8 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 15163 1 000
               1: 0100007F:1F40 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 18320 1 000
               2: 0100007F:D314 0100007F:0BB8 06 00000000:00000000 03:000001C1 00000000     0        0 0 3 000
               3: 0100007F:B105 00000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 15043 1 000
               4: 0100007F:1F40 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 18321 1 000
            """;
        Assert.Equal([8000], WorkspaceService.ParseListeningPorts(tcp, 3000)); // not the IDE port, not ephemeral, no duplicates, only LISTEN
    }

    [Fact]
    public void Docker_run_arguments_are_locked_down()
    {
        var backend = new DockerWorkspaceBackend(new StaticOptions(new WorkspaceOptions()), Microsoft.Extensions.Logging.Abstractions.NullLogger<DockerWorkspaceBackend>.Instance);
        var args = backend.RunArguments(new WorkspaceContainerSpec(7, "bb-ws-x", "img:1", "bytebench-workspaces", "host.docker.internal", 3000,
            new Dictionary<string, string> { ["BB_WORKDIR"] = "/workspace/x" }, new WorkspaceLimits("4g", "2", 2048, "10g")));
        string After(string flag) => args[args.ToList().IndexOf(flag) + 1];
        Assert.Equal("1000:1000", After("--user"));
        Assert.Equal("ALL", After("--cap-drop"));
        Assert.Equal("no-new-privileges", After("--security-opt"));
        Assert.Equal("127.0.0.1::3000", After("-p")); // loopback only
        Assert.Equal("4g", After("--memory"));
        Assert.Equal("4g", After("--memory-swap"));
        Assert.Equal("2", After("--cpus"));
        Assert.Equal("2048", After("--pids-limit"));
        Assert.Equal("size=10g", After("--storage-opt"));
        Assert.Equal("bytebench-workspaces", After("--network"));
        Assert.DoesNotContain("--privileged", args);
        Assert.DoesNotContain("-v", args);
        Assert.DoesNotContain("--volume", args);
        Assert.DoesNotContain("--mount", args);
        Assert.DoesNotContain("--pid", args);
        Assert.DoesNotContain("--cap-add", args);
        Assert.DoesNotContain(args, a => a.Contains("docker.sock"));
        Assert.DoesNotContain("--publish-all", args);
        Assert.Equal("img:1", args[^1]);
    }

    private sealed class StaticOptions(WorkspaceOptions value) : IOptionsMonitor<WorkspaceOptions>
    {
        public WorkspaceOptions CurrentValue => value;
        public WorkspaceOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<WorkspaceOptions, string?> listener) => null;
    }
}

public class WorkspaceCapApp : WorkspaceApp
{
    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        base.AddSettings(settings);
        settings["Workspaces:MaxTotal"] = "2";
        settings["Workspaces:MaxPerUser"] = "5";
    }
}

public class WorkspaceGlobalCapTests(WorkspaceCapApp app) : IClassFixture<WorkspaceCapApp>
{
    [Fact]
    public async Task The_server_wide_cap_applies_across_users()
    {
        var a = await app.NewUserAsync();
        var b = await app.NewUserAsync();
        var c = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((a, ProjectRole.Developer), (b, ProjectRole.Developer), (c, ProjectRole.Developer));
        await app.StartAsync(a, repo);
        await app.StartAsync(b, repo);
        await using var s = app.Scope(app.UserId(c));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.Get<WorkspaceService>().StartAsync(app.ProjectKey(repo.ProjectId), repo.Slug, "main", null, app.UserId(c)));
        Assert.Contains("limit", ex.Message);
    }
}

public class WorkspaceAuthorizationTests(WorkspaceApp app) : IClassFixture<WorkspaceApp>
{
    private async Task<(string Owner, string Other, Workspace Ws, Repository Repo)> SetupAsync()
    {
        var owner = await app.NewUserAsync();
        var other = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((owner, ProjectRole.Developer), (other, ProjectRole.Developer)); // same project access: only ownership differs
        return (owner, other, await app.StartAsync(owner, repo), repo);
    }

    [Fact]
    public async Task Other_users_cannot_see_stop_restart_open_or_commit_in_a_workspace()
    {
        var (owner, other, ws, _) = await SetupAsync();
        await using var s = app.Scope(app.UserId(other));
        var svc = s.Get<WorkspaceService>();
        var id = app.UserId(other);
        Assert.Null(await svc.GetMineAsync(ws.PublicId, id));
        Assert.DoesNotContain(await svc.ListMineAsync(id), w => w.Workspace.PublicId == ws.PublicId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StopAsync(ws.PublicId, id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RestartAsync(ws.PublicId, id));
        Assert.Null(await svc.EntryUrlAsync(ws.PublicId, id, new Uri(app.Origin)));
        Assert.Null(await svc.EntryUrlAsync(ws.PublicId, id, new Uri(app.Origin), 3000));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ListeningPortsAsync(ws.PublicId, id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.GitStatusAsync(ws.PublicId, id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CommitAsync(ws.PublicId, id, "x", true, "main"));
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.AdminStopAsync(ws.PublicId, id));
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.ListAllAsync(id));
        Assert.Equal(WorkspaceState.Running, (await app.ReloadAsync(ws.PublicId)).State);
        await using var ownerScope = app.Scope(app.UserId(owner));
        Assert.NotNull(await ownerScope.Get<WorkspaceService>().GetMineAsync(ws.PublicId, app.UserId(owner)));
    }

    [Fact]
    public async Task A_site_admin_can_stop_any_workspace_but_gets_no_way_into_it()
    {
        var (_, _, ws, _) = await SetupAsync();
        var admin = app.UserId("alice");
        await using var s = app.Scope(admin);
        var svc = s.Get<WorkspaceService>();
        Assert.Contains(await svc.ListAllAsync(admin), w => w.Workspace.PublicId == ws.PublicId);
        Assert.Null(await svc.GetMineAsync(ws.PublicId, admin));
        Assert.Null(await svc.EntryUrlAsync(ws.PublicId, admin, new Uri(app.Origin)));
        await svc.AdminStopAsync(ws.PublicId, admin);
        Assert.Equal(WorkspaceState.Stopped, (await app.ReloadAsync(ws.PublicId)).State);
        await using var db = app.Db();
        var evt = await db.AuditEvents.FirstAsync(a => a.Action == "workspace.stopped" && a.TargetId == ws.PublicId);
        Assert.Contains("admin", evt.Details);
    }

    [Fact]
    public async Task The_api_hides_other_users_workspaces_and_only_lists_your_own()
    {
        var (owner, other, ws, repo) = await SetupAsync();
        using var otherClient = await app.ApiClientAsync(other);
        using var ownerClient = await app.ApiClientAsync(owner);
        otherClient.BaseAddress = ownerClient.BaseAddress = app.ServerAddress;
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/v1/workspaces/{ws.PublicId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.PostAsync($"/api/v1/workspaces/{ws.PublicId}/stop", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.PostAsync($"/api/v1/workspaces/{ws.PublicId}/restart", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
        var list = await otherClient.GetStringAsync("/api/v1/workspaces");
        Assert.DoesNotContain(ws.PublicId, list);

        var mine = JsonDocument.Parse(await ownerClient.GetStringAsync("/api/v1/workspaces")).RootElement;
        Assert.Contains(mine.EnumerateArray(), e => e.GetProperty("id").GetString() == ws.PublicId && e.GetProperty("state").GetString() == "Running");
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.GetAsync($"/api/v1/workspaces/{ws.PublicId}")).StatusCode);
        // The response has no secret and no IDE link.
        var one = await ownerClient.GetStringAsync($"/api/v1/workspaces/{ws.PublicId}");
        Assert.DoesNotContain("gbw_", one);
        Assert.DoesNotContain(app.Backend.ConnectionToken(ws.ContainerId)!, one);

        // Starting on a repository you can't see is a 404, not an explanation.
        var stranger = await app.NewUserAsync();
        using var strangerClient = await app.ApiClientAsync(stranger);
        strangerClient.BaseAddress = app.ServerAddress;
        var denied = await strangerClient.PostAsync($"/api/v1/projects/{app.ProjectKey(repo.ProjectId)}/repos/{repo.Slug}/workspaces", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    [Fact]
    public async Task The_api_starts_and_stops_a_workspace()
    {
        var owner = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((owner, ProjectRole.Developer));
        using var client = await app.ApiClientAsync(owner);
        client.BaseAddress = app.ServerAddress;
        var started = await client.PostAsync($"/api/v1/projects/{app.ProjectKey(repo.ProjectId)}/repos/{repo.Slug}/workspaces", new StringContent("""{"ref":"main","source":"issue:X-1"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var id = JsonDocument.Parse(await started.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        await app.WhenIdleAsync();
        var stopped = await client.PostAsync($"/api/v1/workspaces/{id}/stop", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);
        Assert.Equal("Stopped", JsonDocument.Parse(await stopped.Content.ReadAsStringAsync()).RootElement.GetProperty("state").GetString());
    }
}
