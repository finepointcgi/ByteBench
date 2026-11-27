using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Editor.Ai;
using ByteBench.Services.Git;
using ByteBench.Services.Workspaces;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

/// <summary>
/// Repository environments end to end on the fake Docker backend: image per toolchain, setup once, volumes that outlive the container, services,
/// tasks, secrets, preferences and the REST endpoints. (The real Docker run is in the verification notes.)
/// </summary>
public class WorkspaceEnvironmentLifecycleTests(WorkspaceApp app) : IClassFixture<WorkspaceApp>
{
    private static readonly GitIdentity Me = new("Test", "test@example.com");

    private void Commit(Repository repo, string path, string? content)
    {
        var dir = app.Services.GetRequiredService<RepoStorage>().PathFor(app.ProjectKey(repo.ProjectId), repo.Slug);
        GitOps.CommitFiles(dir, "main", new Dictionary<string, string?> { [path] = content }, "environment", Me, DateTimeOffset.UtcNow);
    }

    private async Task<(string User, Repository Repo)> RepoAsync(string yaml, ProjectRole role = ProjectRole.Developer)
    {
        var user = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, role));
        Commit(repo, WorkspaceEnvFile.Path, yaml);
        return (user, repo);
    }

    private WorkspaceSession Session(Workspace ws) => app.Runtime.Session(ws.Id);

    private static async Task UntilAsync(Func<bool> condition, string what = "condition")
    {
        for (var i = 0; i < 600 && !condition(); i++) await Task.Delay(50); // a ceiling for a loaded machine, not a pause: it returns as soon as the condition holds
        Assert.True(condition(), "Timed out waiting for " + what);
    }

    private static string CommandOf(FakeWorkspaceBackend.ExecCall e) => string.Join(' ', e.Spec.Command);

    // ---------- images and volumes ----------

    [Fact]
    public async Task A_toolchain_gets_its_own_image_built_once_from_the_pinned_base()
    {
        app.Backend.Missing.Add("test-image:1-node22");
        var (user, repo) = await RepoAsync("toolchain: node@22\n");
        var a = await app.StartAsync(user, repo);
        Assert.Equal(WorkspaceState.Running, a.State);
        Assert.Equal("test-image:1-node22", a.Image);
        var build = Assert.Single(app.Backend.Built, b => b.Image == "test-image:1-node22");
        Assert.Equal("node:22-bookworm", build.Args!["BASE_IMAGE"]);
        Assert.Equal("test-image:1-node22", app.Backend.Started.Last(s => s.WorkspaceId == a.Id).Image);
        Assert.Contains("toolchain node@22", a.EnvironmentSummary);
        Assert.Contains("Building the workspace image", Session(a).ProvisionLog.Text());

        var user2 = await app.NewUserAsync();
        await using (var s = app.Scope(app.UserId("alice"))) await s.Get<MemberService>().AddMemberAsync(repo.ProjectId, user2, ProjectRole.Developer);
        var b2 = await app.StartAsync(user2, repo);
        Assert.Equal("test-image:1-node22", b2.Image);
        Assert.Single(app.Backend.Built, b => b.Image == "test-image:1-node22"); // cached
    }

    [Fact]
    public async Task The_container_mounts_a_project_volume_and_a_cache_volume_and_nothing_else()
    {
        var (user, repo) = await RepoAsync("setup: echo hi\n");
        var ws = await app.StartAsync(user, repo);
        var (work, cache) = WorkspaceLifecycle.VolumeNames(ws.PublicId);
        var spec = app.Backend.Started.Last(s => s.WorkspaceId == ws.Id);
        Assert.Equal([new WorkspaceMount(work, "/workspace"), new WorkspaceMount(cache, "/home/coder/.bbcache")], spec.Mounts);
        Assert.Contains(work, app.Backend.Volumes);
        Assert.Contains(cache, app.Backend.Volumes);
        Assert.True(ws.VolumesKept);
        // The dependency caches are redirected into the cache volume through the environment file.
        var env = app.Backend.Execs.Where(e => e.ContainerId == ws.ContainerId).Select(e => e.Spec.Stdin).First(s => s != null && s.Contains("NUGET_PACKAGES"));
        Assert.Contains("export npm_config_cache='/home/coder/.bbcache/npm'", env);
        Assert.Contains("export GOMODCACHE='/home/coder/.bbcache/go/mod'", env);
    }

    [Fact]
    public async Task Stopping_keeps_the_volumes_and_resume_brings_the_workspace_back_without_running_setup_again()
    {
        var marker = new Dictionary<string, string>();
        app.Backend.ExecScripts.Add((s => s.Command.Any(a => a.Contains("cat \"$1\" 2>/dev/null || true")), s => new ExecResult(0, marker.GetValueOrDefault("hash", ""))));
        app.Backend.ExecScripts.Add((s => s.Command.Any(a => a.Contains("printf '%s' \"$2\"")), s => { marker["hash"] = s.Command[^1]; return new ExecResult(0, ""); }));
        app.Backend.ExecScripts.Add((s => s.Command.Count > 0 && s.Command[^1] == "npm ci --fake-install", s => new ExecResult(0, "added 12 packages")));
        var (user, repo) = await RepoAsync("setup: npm ci --fake-install\n");
        var ws = await app.StartAsync(user, repo);
        await app.Runtime.WhenSetupSettledAsync(ws.Id); // the setup run provisioning started: awaited, not polled for
        Assert.Equal(SetupState.Succeeded, Session(ws).Setup);
        Assert.Contains("$ npm ci --fake-install", Session(ws).SetupLog.Text());
        Assert.Contains("added 12 packages", Session(ws).SetupLog.Text());
        Assert.Equal(WorkspaceEnvFile.ParseYaml("setup: npm ci --fake-install\n").Environment!.SetupHash, marker["hash"]);

        await using (var s = app.Scope(app.UserId(user))) await s.Get<WorkspaceService>().StopAsync(ws.PublicId, app.UserId(user));
        var stopped = await app.ReloadAsync(ws.PublicId);
        Assert.Equal(WorkspaceState.Stopped, stopped.State);
        Assert.True(stopped.VolumesKept);
        Assert.Contains(WorkspaceLifecycle.VolumeNames(ws.PublicId).Work, app.Backend.Volumes); // survived the container
        Assert.Contains(ws.ContainerId!, app.Backend.Removed);
        Assert.Contains(ws.Id, app.Backend.AuxiliaryRemoved);

        await using (var s = app.Scope(app.UserId(user))) await s.Get<WorkspaceService>().ResumeAsync(ws.PublicId, app.UserId(user));
        await app.WhenIdleAsync(); // provisioning has finished (and has started the setup check)...
        await app.Runtime.WhenSetupSettledAsync(ws.Id); // ...whose outcome is awaited, so UpToDate is never read while it is still Pending
        var resumed = await app.ReloadAsync(ws.PublicId);
        Assert.Equal(WorkspaceState.Running, resumed.State);
        Assert.NotEqual(ws.ContainerId, resumed.ContainerId);
        Assert.Equal(SetupState.UpToDate, Session(ws).Setup);
        Assert.Single(app.Backend.Streams, e => e.Spec.Command[^1] == "npm ci --fake-install"); // not run a second time
        Assert.Equal(ws.PublicId, (await app.ReloadAsync(ws.PublicId)).PublicId);
        // A resumed container mounts the same volumes.
        Assert.Equal(app.Backend.Started.First(s => s.WorkspaceId == ws.Id).Mounts, app.Backend.Started.Last(s => s.WorkspaceId == ws.Id).Mounts);

        // Change the setup block: the hash changes and setup runs again after the next resume.
        Commit(repo, WorkspaceEnvFile.Path, "setup: npm ci --fake-install --other\n");
        app.Backend.ExecScripts.Add((s => s.Command.Count > 0 && s.Command[^1] == "npm ci --fake-install --other", s => new ExecResult(0, "again")));
        await using (var s = app.Scope(app.UserId(user)))
        {
            await s.Get<WorkspaceService>().StopAsync(ws.PublicId, app.UserId(user));
            await s.Get<WorkspaceService>().ResumeAsync(ws.PublicId, app.UserId(user));
        }
        await app.WhenIdleAsync();
        await app.Runtime.WhenSetupSettledAsync(ws.Id);
        Assert.Equal(SetupState.Succeeded, Session(ws).Setup);
        Assert.Contains(app.Backend.Streams, e => e.Spec.Command[^1] == "npm ci --fake-install --other");
    }

    [Fact]
    public async Task A_failing_setup_is_shown_and_the_workspace_still_opens()
    {
        app.Backend.ExecScripts.Add((s => s.Command.Count > 0 && s.Command[^1] == "make boom", s => new ExecResult(2, "make: *** [boom] Error 1")));
        var (user, repo) = await RepoAsync("setup:\n  - echo first\n  - make boom\n  - echo never\n");
        var ws = await app.StartAsync(user, repo);
        Assert.Equal(WorkspaceState.Running, ws.State);
        await app.Runtime.WhenSetupSettledAsync(ws.Id);
        Assert.Equal(SetupState.Failed, Session(ws).Setup);
        var session = Session(ws);
        Assert.Contains("Setup command 2 of 3 failed (exit code 2): make boom", session.SetupMessage);
        Assert.Contains("make: *** [boom] Error 1", session.SetupLog.Text());
        Assert.DoesNotContain(app.Backend.Streams, e => e.Spec.Command[^1] == "echo never");

        await using var scope = app.Scope(app.UserId(user));
        var view = await scope.Get<WorkspaceService>().GetEnvironmentAsync(ws.PublicId, app.UserId(user));
        Assert.Equal("Failed", view.SetupState);
        Assert.Contains("make: *** [boom] Error 1", view.SetupLog);
    }

    [Fact]
    public async Task Deleting_a_workspace_removes_its_volumes_and_row_and_only_the_owner_can()
    {
        var (user, repo) = await RepoAsync("setup: echo hi\n");
        var ws = await app.StartAsync(user, repo);
        var stranger = await app.NewUserAsync();
        await using (var s = app.Scope(app.UserId(stranger)))
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<WorkspaceService>().DeleteAsync(ws.PublicId, app.UserId(stranger)));
        Assert.Contains(WorkspaceLifecycle.VolumeNames(ws.PublicId).Work, app.Backend.Volumes);

        await using (var s = app.Scope(app.UserId(user))) await s.Get<WorkspaceService>().DeleteAsync(ws.PublicId, app.UserId(user));
        var (work, cache) = WorkspaceLifecycle.VolumeNames(ws.PublicId);
        Assert.DoesNotContain(work, app.Backend.Volumes);
        Assert.DoesNotContain(cache, app.Backend.Volumes);
        Assert.Contains(ws.ContainerId!, app.Backend.Removed);
        await using var db = app.Db();
        Assert.False(await db.Workspaces.AnyAsync(w => w.PublicId == ws.PublicId));
    }

    [Fact]
    public async Task The_reaper_deletes_old_volumes_and_orphans_but_not_recent_or_running_ones()
    {
        var (user, repo) = await RepoAsync("setup: echo hi\n");
        var old = await app.StartAsync(user, repo);
        await using (var s = app.Scope(app.UserId(user))) await s.Get<WorkspaceService>().StopAsync(old.PublicId, app.UserId(user));
        var recent = await app.StartAsync(user, repo);
        await using (var s = app.Scope(app.UserId(user))) await s.Get<WorkspaceService>().StopAsync(recent.PublicId, app.UserId(user));
        var running = await app.StartAsync(user, repo);
        await using (var db = app.Db())
            await db.Workspaces.Where(w => w.Id == old.Id).ExecuteUpdateAsync(s => s.SetProperty(w => w.StoppedAt, DateTime.UtcNow.AddDays(-30)));
        await app.Backend.CreateVolumeAsync("bb-wsv-x-orphan-work", 987654, default);

        await app.Services.GetRequiredService<WorkspaceReaper>().SweepAsync(default);

        Assert.DoesNotContain(WorkspaceLifecycle.VolumeNames(old.PublicId).Work, app.Backend.Volumes);
        Assert.False((await app.ReloadAsync(old.PublicId)).VolumesKept);
        Assert.Contains(WorkspaceLifecycle.VolumeNames(recent.PublicId).Work, app.Backend.Volumes);
        Assert.Contains(WorkspaceLifecycle.VolumeNames(running.PublicId).Work, app.Backend.Volumes);
        Assert.DoesNotContain("bb-wsv-x-orphan-work", app.Backend.Volumes);
        await using var s2 = app.Scope(app.UserId(user));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s2.Get<WorkspaceService>().ResumeAsync(old.PublicId, app.UserId(user))); // its files are gone
    }

    [Fact]
    public async Task The_admin_page_lists_volumes_with_their_size_and_only_for_admins()
    {
        var (user, repo) = await RepoAsync("setup: echo hi\n");
        var ws = await app.StartAsync(user, repo);
        app.Backend.VolumeSizes[WorkspaceLifecycle.VolumeNames(ws.PublicId).Work] = 123_456;
        await using var admin = app.Scope(app.UserId("alice"));
        var usage = await admin.Get<WorkspaceService>().GetDiskUsageAsync(app.UserId("alice"));
        Assert.Contains(usage, u => u.WorkspaceId == ws.Id && u.SizeBytes == 123_456 && u.Owner == user);
        await using var normal = app.Scope(app.UserId(user));
        await Assert.ThrowsAsync<ForbiddenException>(() => normal.Get<WorkspaceService>().GetDiskUsageAsync(app.UserId(user)));
    }

    // ---------- bad and unusual files ----------

    [Fact]
    public async Task A_file_that_fails_validation_is_reported_and_the_workspace_opens_with_the_default_setup()
    {
        var (user, repo) = await RepoAsync("toolchain: cobol\nports: [80]\n");
        var ws = await app.StartAsync(user, repo);
        Assert.Equal(WorkspaceState.Running, ws.State);
        Assert.Equal("test-image:1", ws.Image);
        await using var scope = app.Scope(app.UserId(user));
        var view = await scope.Get<WorkspaceService>().GetEnvironmentAsync(ws.PublicId, app.UserId(user));
        Assert.Contains(view.Warnings, w => w.Contains("not a toolchain ByteBench knows"));
        Assert.Contains(view.Warnings, w => w.Contains("was not used"));
        Assert.Equal("None", view.SetupState);
    }

    [Fact]
    public async Task A_repository_chosen_base_image_needs_the_admin_switch()
    {
        var (user, repo) = await RepoAsync("image: ghcr.io/acme/dev:1\nsetup: echo hi\n");
        var off = await app.StartAsync(user, repo);
        Assert.Equal("test-image:1", off.Image);
        await using (var s = app.Scope(app.UserId(user)))
        {
            var view = await s.Get<WorkspaceService>().GetEnvironmentAsync(off.PublicId, app.UserId(user));
            Assert.Contains(view.Warnings, w => w.Contains("hasn't allowed images chosen by repositories"));
        }
        Assert.DoesNotContain(app.Backend.Built, b => b.Args?.GetValueOrDefault("BASE_IMAGE") == "ghcr.io/acme/dev:1");
        await using (var s = app.Scope(app.UserId(user)))
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<WorkspaceService>().SetCustomImagesAllowedAsync(true, app.UserId(user)));

        await using (var s = app.Scope(app.UserId("alice"))) await s.Get<WorkspaceService>().SetCustomImagesAllowedAsync(true, app.UserId("alice"));
        try
        {
            await using (var s = app.Scope(app.UserId(user))) await s.Get<WorkspaceService>().StopAsync(off.PublicId, app.UserId(user));
            app.Backend.Missing.Add(app.Services.GetRequiredService<WorkspaceImage>().TagFor("ghcr.io/acme/dev:1"));
            var on = await app.StartAsync(user, repo);
            Assert.StartsWith("test-image:1-img-", on.Image);
            Assert.Contains(app.Backend.Built, b => b.Image == on.Image && b.Args!["BASE_IMAGE"] == "ghcr.io/acme/dev:1");
        }
        finally { await using var s = app.Scope(app.UserId("alice")); await s.Get<WorkspaceService>().SetCustomImagesAllowedAsync(false, app.UserId("alice")); }
    }

    [Fact]
    public async Task A_devcontainer_json_is_used_when_there_is_no_workspace_file_and_its_ignored_parts_are_visible()
    {
        var user = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer));
        Commit(repo, ".devcontainer/devcontainer.json", "{ \"forwardPorts\": [4000], \"postCreateCommand\": \"echo created\", \"features\": { \"x\": {} }, \"containerEnv\": { \"A\": \"b\" } }");
        var ws = await app.StartAsync(user, repo);
        await using var scope = app.Scope(app.UserId(user));
        var view = await scope.Get<WorkspaceService>().GetEnvironmentAsync(ws.PublicId, app.UserId(user));
        Assert.Equal(".devcontainer/devcontainer.json", view.Source);
        Assert.Contains(4000, view.Ports);
        Assert.Contains(view.Warnings, w => w.Contains("features") && w.Contains("doesn't install"));
        await app.Runtime.WhenSetupSettledAsync(ws.Id);
        Assert.Equal(SetupState.Succeeded, Session(ws).Setup);
        Assert.Contains(app.Backend.Streams, e => e.Spec.Command[^1] == "echo created");
        Assert.Contains(app.Backend.Execs.Select(e => e.Spec.Stdin), s => s != null && s.Contains("export A='b'"));
    }

    [Fact]
    public async Task The_workspace_file_wins_over_a_devcontainer()
    {
        var (user, repo) = await RepoAsync("ports: [4100]\n");
        Commit(repo, ".devcontainer/devcontainer.json", "{ \"forwardPorts\": [4200] }");
        var ws = await app.StartAsync(user, repo);
        await using var scope = app.Scope(app.UserId(user));
        var view = await scope.Get<WorkspaceService>().GetEnvironmentAsync(ws.PublicId, app.UserId(user));
        Assert.Equal([4100], view.Ports);
        Assert.Equal(WorkspaceEnvFile.Path, view.Source);
    }

    [Fact]
    public async Task The_start_page_preview_suggests_a_file_for_repositories_without_one()
    {
        var user = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer));
        Commit(repo, "package.json", "{ \"scripts\": { \"start\": \"node server.js\" } }");
        await using var scope = app.Scope(app.UserId(user));
        var preview = await scope.Get<WorkspaceService>().PreviewEnvironmentAsync(app.ProjectKey(repo.ProjectId), repo.Slug, "main", app.UserId(user));
        Assert.NotNull(preview);
        Assert.False(preview!.Value.Load.HasFile);
        Assert.Equal("node@22", preview.Value.Load.Suggestion!.Toolchain);
        Assert.Contains("toolchain: node@22", preview.Value.Load.SuggestedYaml);
        Assert.True(WorkspaceEnvFile.ParseYaml(preview.Value.Load.SuggestedYaml).Ok);
        // Not visible to someone with no access.
        var outsider = await app.NewUserAsync();
        await using var other = app.Scope(app.UserId(outsider));
        Assert.Null(await other.Get<WorkspaceService>().PreviewEnvironmentAsync(app.ProjectKey(repo.ProjectId), repo.Slug, "main", app.UserId(outsider)));
    }

    // ---------- services ----------

    [Fact]
    public async Task Services_run_on_a_private_network_and_the_workspace_may_reach_only_them()
    {
        var (user, repo) = await RepoAsync("services:\n  db:\n    image: postgres:16\n    env:\n      POSTGRES_PASSWORD: dev\n  cache: redis:7\n");
        var ws = await app.StartAsync(user, repo);
        Assert.Equal(WorkspaceState.Running, ws.State);
        var net = Assert.Single(app.Backend.Networks, n => n.WorkspaceId == ws.Id);
        Assert.Contains(app.Backend.Connected, c => c.Container == ws.ContainerId && c.Network == net.Network);
        var db = Assert.Single(app.Backend.Sidecars, s => s.WorkspaceId == ws.Id && s.Alias == "db");
        Assert.Equal(net.Network, db.Network);
        Assert.Equal("dev", db.Env["POSTGRES_PASSWORD"]);
        var egress = app.Backend.EgressApplied.Last(p => p.Allow.Any(a => a.EndsWith(":5432")));
        Assert.Contains(egress.Allow, a => a.EndsWith(":6379"));
        Assert.Contains(egress.Allow, a => a == "127.0.0.1:9"); // ByteBench's git endpoint stays
        Assert.Equal(egress.Allow.Where(a => a.StartsWith("10.99.")).Order(), Session(ws).EgressAllow.Order());
        Assert.Contains(app.Backend.Execs, e => e.ContainerId == ws.ContainerId && CommandOf(e).Contains("/dev/tcp") && e.Spec.Command.Contains("db") && e.Spec.Command.Contains("5432"));
        Assert.Contains(await Services(user, ws), x => x.StartsWith("db (postgres:16)"));
        await using (var s = app.Scope(app.UserId(user))) await s.Get<WorkspaceService>().StopAsync(ws.PublicId, app.UserId(user));
        Assert.Contains(ws.Id, app.Backend.AuxiliaryRemoved);
    }

    private async Task<IReadOnlyList<string>> Services(string user, Workspace ws)
    {
        await using var scope = app.Scope(app.UserId(user));
        return (await scope.Get<WorkspaceService>().GetEnvironmentAsync(ws.PublicId, app.UserId(user))).Services;
    }

    [Fact]
    public async Task A_service_that_never_accepts_connections_fails_the_start_with_a_clear_message()
    {
        app.Backend.ExecScripts.Add((s => s.Command.Any(a => a.Contains("/dev/tcp")) && s.Command.Contains("dead"), s => new ExecResult(1, "")));
        var (user, repo) = await RepoAsync("services:\n  dead:\n    image: someapp:1\n    ports: [7777]\n");
        var ws = await app.StartAsync(user, repo);
        Assert.Equal(WorkspaceState.Failed, ws.State);
        Assert.Contains("Service 'dead' did not accept connections on port 7777", ws.Error);
        Assert.False(ws.VolumesKept); // a start that never got going leaves nothing behind
        Assert.DoesNotContain(WorkspaceLifecycle.VolumeNames(ws.PublicId).Work, app.Backend.Volumes);
    }

    // ---------- tasks ----------

    private const string TaskFile = """
        tasks:
          run:
            command: npm run dev
            port: 3000
            preview: true
          test: npm test
          fail: exit 3
        ports: [8080]
        """;

    private static void ScriptTasks(FakeWorkspaceBackend backend)
    {
        // A server task prints a line and runs until its docker client is cancelled; the stop script succeeds.
        backend.StreamScripts.Add((s => s.Command[^1] == "npm run dev", async (_, onLine, ct) =>
        {
            onLine("server listening on 3000");
            await Task.Delay(Timeout.Infinite, ct);
            return new ExecResult(0, "");
        }));
        backend.StreamScripts.Add((s => s.Command[^1] == "npm test", (_, onLine, _) => { onLine("2 passed"); return Task.FromResult(new ExecResult(0, "2 passed")); }));
        backend.StreamScripts.Add((s => s.Command[^1] == "exit 3", (_, onLine, _) => { onLine("bad things"); return Task.FromResult(new ExecResult(3, "bad things")); }));
    }

    [Fact]
    public async Task Tasks_run_stream_their_output_report_their_exit_and_can_be_stopped()
    {
        ScriptTasks(app.Backend);
        var (user, repo) = await RepoAsync(TaskFile);
        var ws = await app.StartAsync(user, repo);
        var id = app.UserId(user);
        await using var scope = app.Scope(id);
        var svc = scope.Get<WorkspaceService>();

        var listed = await svc.ListTasksAsync(ws.PublicId, id);
        Assert.Equal(["run", "test", "fail"], listed.Select(t => t.Name));
        Assert.All(listed, t => Assert.Equal("Idle", t.State));

        var test = await svc.RunTaskAsync(ws.PublicId, id, "test");
        await UntilAsync(() => Session(ws).Tasks["test"].State == TaskState.Succeeded, "test to finish");
        var (lines, next, info) = await svc.TaskLogAsync(ws.PublicId, id, "test");
        Assert.Contains("$ npm test", lines);
        Assert.Contains("2 passed", lines);
        Assert.Equal("Succeeded", info.State);
        Assert.Equal(0, info.ExitCode);
        Assert.DoesNotContain("2 passed", (await svc.TaskLogAsync(ws.PublicId, id, "test", next)).Lines); // only what is new

        await svc.RunTaskAsync(ws.PublicId, id, "fail");
        await UntilAsync(() => Session(ws).Tasks["fail"].State == TaskState.Failed, "fail to fail");
        Assert.Equal(3, (await svc.ListTasksAsync(ws.PublicId, id)).Single(t => t.Name == "fail").ExitCode);

        var run = await svc.RunTaskAsync(ws.PublicId, id, "run");
        Assert.Equal("Running", run.State);
        Assert.Equal(3000, run.Port);
        Assert.True(run.Preview);
        await UntilAsync(() => Session(ws).Tasks["run"].Log.Text().Contains("server listening on 3000"), "server output");
        Assert.Equal("Running", (await svc.RunTaskAsync(ws.PublicId, id, "run")).State); // a second Run doesn't start a second copy
        Assert.Single(app.Backend.Streams, e => e.Spec.Command[^1] == "npm run dev" && e.ContainerId == ws.ContainerId);

        var stopped = await svc.StopTaskAsync(ws.PublicId, id, "run");
        Assert.Equal("Stopped", stopped.State);
        Assert.Contains(app.Backend.Execs, e => e.ContainerId == ws.ContainerId && e.Spec.Command.Any(a => a.Contains("kill_tree"))); // the process tree is ended inside the container
        Assert.Contains("[task stopped]", Session(ws).Tasks["run"].Log.Text());

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RunTaskAsync(ws.PublicId, id, "nope"));
        var stranger = await app.NewUserAsync();
        await using var other = app.Scope(app.UserId(stranger));
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.Get<WorkspaceService>().RunTaskAsync(ws.PublicId, app.UserId(stranger), "test"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.Get<WorkspaceService>().TaskLogAsync(ws.PublicId, app.UserId(stranger), "test"));
    }

    [Fact]
    public async Task Tasks_run_the_command_as_one_argument_to_the_containers_shell_never_through_the_host()
    {
        ScriptTasks(app.Backend);
        const string nasty = "echo \"$(id)\" ; `whoami` && rm -rf / # '";
        app.Backend.StreamScripts.Add((s => s.Command[^1] == nasty, (_, onLine, _) => { onLine("ran"); return Task.FromResult(new ExecResult(0, "ran")); }));
        var (user, repo) = await RepoAsync("tasks:\n  nasty:\n    command: " + JsonSerializer.Serialize(nasty) + "\n");
        var ws = await app.StartAsync(user, repo);
        await using var scope = app.Scope(app.UserId(user));
        await scope.Get<WorkspaceService>().RunTaskAsync(ws.PublicId, app.UserId(user), "nasty");
        await UntilAsync(() => Session(ws).Tasks["nasty"].State == TaskState.Succeeded, "the task");
        var call = app.Backend.Streams.Last(e => e.Spec.Command[^1] == nasty);
        Assert.Equal("sh", call.Spec.Command[0]);
        Assert.Equal("-c", call.Spec.Command[1]);
        Assert.Equal("coder", call.Spec.User);
        Assert.Equal("/workspace/" + repo.Slug, call.Spec.WorkDir);
        Assert.Null(call.Spec.Env); // nothing goes through docker -e
    }

    [Fact]
    public async Task A_task_cannot_start_while_setup_runs_and_the_limit_on_running_tasks_holds()
    {
        ScriptTasks(app.Backend);
        var gate = new TaskCompletionSource();
        app.Backend.StreamScripts.Add((s => s.Command[^1] == "slow install", async (_, _, ct) => { await gate.Task.WaitAsync(ct); return new ExecResult(0, ""); }));
        var (user, repo) = await RepoAsync("setup: slow install\ntasks:\n  test: npm test\n");
        var ws = await app.StartAsync(user, repo);
        await using var scope = app.Scope(app.UserId(user));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Get<WorkspaceService>().RunTaskAsync(ws.PublicId, app.UserId(user), "test"));
        Assert.Contains("Setup is still running", ex.Message);
        gate.SetResult();
        await app.Runtime.WhenSetupSettledAsync(ws.Id);
        Assert.Equal(SetupState.Succeeded, Session(ws).Setup);
        await scope.Get<WorkspaceService>().RunTaskAsync(ws.PublicId, app.UserId(user), "test");
    }

    [Fact]
    public async Task Stopping_the_workspace_ends_its_tasks()
    {
        ScriptTasks(app.Backend);
        var (user, repo) = await RepoAsync(TaskFile);
        var ws = await app.StartAsync(user, repo);
        await using var scope = app.Scope(app.UserId(user));
        await scope.Get<WorkspaceService>().RunTaskAsync(ws.PublicId, app.UserId(user), "run");
        await UntilAsync(() => Session(ws).Tasks["run"].Log.Text().Contains("listening"), "the server");
        await scope.Get<WorkspaceService>().StopAsync(ws.PublicId, app.UserId(user));
        await UntilAsync(() => Session(ws).Tasks["run"].State == TaskState.Stopped, "the task to end");
    }

    [Fact]
    public async Task Reloading_the_environment_picks_up_an_edited_file_from_the_checkout()
    {
        var (user, repo) = await RepoAsync("tasks:\n  a: echo a\n");
        var ws = await app.StartAsync(user, repo);
        app.Backend.ExecScripts.Add((s => s.Command.Count > 3 && s.Command[2].Contains("[ -f \"$1\" ] && cat") && s.Command[3] == "sh" && s.Command[^1] == WorkspaceEnvFile.Path,
            s => new ExecResult(0, "tasks:\n  a: echo a\n  b: echo b\n")));
        await using var scope = app.Scope(app.UserId(user));
        Assert.Empty(await scope.Get<WorkspaceService>().ReloadEnvironmentAsync(ws.PublicId, app.UserId(user)));
        Assert.Equal(["a", "b"], (await scope.Get<WorkspaceService>().ListTasksAsync(ws.PublicId, app.UserId(user))).Select(t => t.Name));
        app.Backend.ExecScripts.Insert(0, (s => s.Command.Count > 3 && s.Command[2].Contains("[ -f \"$1\" ] && cat") && s.Command[^1] == WorkspaceEnvFile.Path, s => new ExecResult(0, "tasks: nope")));
        var errors = await scope.Get<WorkspaceService>().ReloadEnvironmentAsync(ws.PublicId, app.UserId(user));
        Assert.Contains(errors, e => e.Contains("tasks:"));
    }

    // ---------- secrets ----------

    [Fact]
    public async Task Secrets_reach_the_container_as_environment_variables_without_touching_a_command_line_or_the_logs()
    {
        ScriptTasks(app.Backend);
        const string userSecret = "usr-sEcret-7788", repoSecret = "repo-sEcret-9911";
        app.Backend.StreamScripts.Add((s => s.Command[^1] == "print secrets", (_, onLine, _) =>
        {
            onLine($"token={userSecret} and {repoSecret}");
            return Task.FromResult(new ExecResult(0, $"token={userSecret}"));
        }));
        var user = await app.NewUserAsync();
        var maintainer = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer), (maintainer, ProjectRole.Maintainer));
        Commit(repo, WorkspaceEnvFile.Path, "tasks:\n  show: print secrets\nenv:\n  PLAIN: visible\n");
        await using (var s = app.Scope(app.UserId(user))) await s.Get<WorkspaceSecretService>().SaveMineAsync(app.UserId(user), "MY_TOKEN", userSecret);
        await using (var s = app.Scope(app.UserId(maintainer))) await s.Get<WorkspaceSecretService>().SaveForRepositoryAsync(repo.Id, app.UserId(maintainer), "DEPLOY_KEY", repoSecret);

        var ws = await app.StartAsync(user, repo);
        var calls = app.Backend.Execs.Where(e => e.ContainerId == ws.ContainerId).ToList();
        // The values appear in exactly one place: the stdin of the command that writes the mode-600 environment file.
        var carriers = calls.Where(e => e.Spec.Stdin?.Contains(userSecret) == true || e.Spec.Stdin?.Contains(repoSecret) == true).ToList();
        var carrier = Assert.Single(carriers);
        Assert.Contains("export MY_TOKEN='usr-sEcret-7788'", carrier.Spec.Stdin);
        Assert.Contains("export DEPLOY_KEY='repo-sEcret-9911'", carrier.Spec.Stdin);
        Assert.Contains("export PLAIN='visible'", carrier.Spec.Stdin);
        Assert.Contains("umask 077", string.Join(' ', carrier.Spec.Command));
        Assert.DoesNotContain(calls, e => e.Spec.Command.Any(a => a.Contains(userSecret) || a.Contains(repoSecret)));
        Assert.DoesNotContain(calls, e => e.Spec.Env?.Values.Any(v => v.Contains(userSecret) || v.Contains(repoSecret)) == true);
        Assert.DoesNotContain(app.Backend.Started, s => s.Env.Values.Any(v => v.Contains(userSecret) || v.Contains(repoSecret))); // not in `docker run -e` either
        Assert.Contains("MY_TOKEN", Session(ws).ProvisionLog.Text()); // names are listed...
        Assert.DoesNotContain(userSecret, Session(ws).ProvisionLog.Text()); // ...values never

        // Task output has them masked.
        await using (var s = app.Scope(app.UserId(user))) await s.Get<WorkspaceService>().RunTaskAsync(ws.PublicId, app.UserId(user), "show");
        await UntilAsync(() => Session(ws).Tasks["show"].State == TaskState.Succeeded, "the task");
        var log = Session(ws).Tasks["show"].Log.Text();
        Assert.Contains("token=*** and ***", log);
        Assert.DoesNotContain(userSecret, log);
        Assert.DoesNotContain(repoSecret, log);
        await using (var s = app.Scope(app.UserId(user)))
        {
            var view = await s.Get<WorkspaceService>().GetEnvironmentAsync(ws.PublicId, app.UserId(user));
            Assert.DoesNotContain(userSecret, JsonSerializer.Serialize(view));
        }

        // At rest: encrypted, and never in the audit log.
        await using var db = app.Db();
        var rows = await db.WorkspaceSecrets.AsNoTracking().Where(x => x.Name == "MY_TOKEN" || x.Name == "DEPLOY_KEY").ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => { Assert.NotEmpty(r.EncryptedValue); Assert.DoesNotContain("sEcret", r.EncryptedValue); });
        var audit = string.Join("\n", await db.AuditEvents.AsNoTracking().Select(a => a.Details).ToListAsync());
        Assert.DoesNotContain("sEcret", audit);
        Assert.Contains("workspace_secret", string.Join("\n", await db.AuditEvents.AsNoTracking().Select(a => a.Action).ToListAsync()));
    }

    [Fact]
    public async Task Secrets_are_private_to_their_user_and_repository_secrets_need_a_maintainer()
    {
        var alice = await app.NewUserAsync();
        var bob = await app.NewUserAsync();
        var dev = await app.NewUserAsync();
        var maint = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((dev, ProjectRole.Developer), (maint, ProjectRole.Maintainer));
        await using (var s = app.Scope(app.UserId(alice))) await s.Get<WorkspaceSecretService>().SaveMineAsync(app.UserId(alice), "ONLY_ALICE", "x1x1x1");
        await using (var s = app.Scope(app.UserId(bob)))
        {
            Assert.Empty(await s.Get<WorkspaceSecretService>().ListMineAsync(app.UserId(bob)));
            var id = (await app.Scope(app.UserId(alice)).Get<WorkspaceSecretService>().ListMineAsync(app.UserId(alice))).Single().Id;
            await s.Get<WorkspaceSecretService>().DeleteMineAsync(app.UserId(bob), id); // someone else's id: a no-op
        }
        await using (var s = app.Scope(app.UserId(alice))) Assert.Single(await s.Get<WorkspaceSecretService>().ListMineAsync(app.UserId(alice)));

        await using (var s = app.Scope(app.UserId(dev)))
        {
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<WorkspaceSecretService>().ListForRepositoryAsync(repo.Id, app.UserId(dev)));
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<WorkspaceSecretService>().SaveForRepositoryAsync(repo.Id, app.UserId(dev), "X", "y"));
        }
        var outsider = await app.NewUserAsync();
        await using (var s = app.Scope(app.UserId(outsider)))
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<WorkspaceSecretService>().ListForRepositoryAsync(repo.Id, app.UserId(outsider)));
        await using (var s = app.Scope(app.UserId(maint)))
        {
            var svc = s.Get<WorkspaceSecretService>();
            await svc.SaveForRepositoryAsync(repo.Id, app.UserId(maint), "REPO_ONE", "v-v-v-1");
            await svc.SaveForRepositoryAsync(repo.Id, app.UserId(maint), "REPO_ONE", ""); // keeps the value when only renewing
            var list = await svc.ListForRepositoryAsync(repo.Id, app.UserId(maint));
            Assert.Equal("REPO_ONE", Assert.Single(list).Name);
            await svc.DeleteForRepositoryAsync(repo.Id, app.UserId(maint), list[0].Id);
            Assert.Empty(await svc.ListForRepositoryAsync(repo.Id, app.UserId(maint)));
        }
    }

    [Theory]
    [InlineData("lower case", "not a valid variable name")]
    [InlineData("PATH", "reserved")]
    [InlineData("GIT_TOKEN", "reserved")]
    [InlineData("BB_X", "reserved")]
    [InlineData("1ABC", "not a valid variable name")]
    public async Task Secret_names_are_validated(string name, string expected)
    {
        var user = await app.NewUserAsync();
        await using var s = app.Scope(app.UserId(user));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<WorkspaceSecretService>().SaveMineAsync(app.UserId(user), name, "value"));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task Secret_values_have_a_size_cap_and_a_new_secret_needs_one()
    {
        var user = await app.NewUserAsync();
        await using var s = app.Scope(app.UserId(user));
        var svc = s.Get<WorkspaceSecretService>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SaveMineAsync(app.UserId(user), "BIG", new string('x', WorkspaceSecretService.MaxValueLength + 1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SaveMineAsync(app.UserId(user), "EMPTY", ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SaveMineAsync(app.UserId(user), "NUL", "a\0b"));
        await svc.SaveMineAsync(app.UserId(user), "MULTI", "line1\nline'2");
        Assert.Contains("MULTI", (await svc.ListMineAsync(app.UserId(user))).Select(x => x.Name));
    }

    [Fact]
    public async Task A_read_only_member_does_not_get_the_repository_secrets_but_keeps_their_own()
    {
        var viewer = await app.NewUserAsync();
        var maint = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((viewer, ProjectRole.Viewer), (maint, ProjectRole.Maintainer));
        await using (var s = app.Scope(app.UserId(maint))) await s.Get<WorkspaceSecretService>().SaveForRepositoryAsync(repo.Id, app.UserId(maint), "REPO_SECRET", "repo-value-1");
        await using (var s = app.Scope(app.UserId(viewer))) await s.Get<WorkspaceSecretService>().SaveMineAsync(app.UserId(viewer), "MINE", "mine-value-2");
        var ws = await app.StartAsync(viewer, repo);
        var env = app.Backend.Execs.Where(e => e.ContainerId == ws.ContainerId).Select(e => e.Spec.Stdin).First(x => x != null && x.Contains("NUGET_PACKAGES"));
        Assert.Contains("export MINE='mine-value-2'", env);
        Assert.DoesNotContain("REPO_SECRET", env);
    }

    [Fact]
    public async Task A_users_secret_overrides_the_repository_secret_of_the_same_name()
    {
        var dev = await app.NewUserAsync();
        var maint = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((dev, ProjectRole.Developer), (maint, ProjectRole.Maintainer));
        await using (var s = app.Scope(app.UserId(maint))) await s.Get<WorkspaceSecretService>().SaveForRepositoryAsync(repo.Id, app.UserId(maint), "API_KEY", "from-repository");
        await using (var s = app.Scope(app.UserId(dev))) await s.Get<WorkspaceSecretService>().SaveMineAsync(app.UserId(dev), "API_KEY", "from-user");
        var ws = await app.StartAsync(dev, repo);
        var env = app.Backend.Execs.Where(e => e.ContainerId == ws.ContainerId).Select(e => e.Spec.Stdin).First(x => x != null && x.Contains("NUGET_PACKAGES"));
        Assert.Contains("export API_KEY='from-user'", env);
        Assert.DoesNotContain("from-repository", env);
    }

    // ---------- preferences ----------

    [Fact]
    public async Task User_settings_and_the_shell_snippet_are_applied_to_the_users_own_workspaces_only()
    {
        var user = await app.NewUserAsync();
        var other = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer), (other, ProjectRole.Developer));
        await using (var s = app.Scope(app.UserId(user)))
            await s.Get<UserWorkspaceSettingsService>().SaveAsync(app.UserId(user), "{ // comment\n \"editor.fontSize\": 15, }", "alias gs='git status'");
        var mine = await app.StartAsync(user, repo);
        var theirs = await app.StartAsync(other, repo);
        var stdins = (Workspace w) => app.Backend.Execs.Where(e => e.ContainerId == w.ContainerId).Select(e => (Cmd: CommandOf(e), e.Spec.Stdin)).ToList();
        Assert.Contains(stdins(mine), x => x.Cmd.Contains("settings.json") && x.Stdin!.Contains("\"editor.fontSize\": 15"));
        Assert.Contains(stdins(mine), x => x.Cmd.Contains(".bb-user-rc") && x.Stdin == "alias gs='git status'");
        Assert.DoesNotContain(stdins(theirs), x => x.Cmd.Contains("settings.json") || x.Stdin?.Contains("alias gs") == true);
    }

    [Theory]
    [InlineData("{ not json", "aren't valid JSON")]
    [InlineData("[1, 2]", "must be a JSON object")]
    public async Task Invalid_settings_are_refused_and_both_fields_have_size_caps(string json, string expected)
    {
        var user = await app.NewUserAsync();
        await using var s = app.Scope(app.UserId(user));
        var svc = s.Get<UserWorkspaceSettingsService>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SaveAsync(app.UserId(user), json, ""));
        Assert.Contains(expected, ex.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SaveAsync(app.UserId(user), "{ \"a\": \"" + new string('x', UserWorkspaceSettingsService.MaxSettingsChars) + "\" }", ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SaveAsync(app.UserId(user), "", new string('x', UserWorkspaceSettingsService.MaxSnippetChars + 1)));
        await svc.SaveAsync(app.UserId(user), "", ""); // empty clears both
    }

    // ---------- REST ----------

    private async Task<HttpClient> ApiAsync(string user, TokenScopes scopes)
    {
        var http = app.Http();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await app.CreateTokenAsync(user, scopes));
        return http;
    }

    [Fact]
    public async Task The_task_endpoints_list_run_stop_and_read_logs_for_the_owner_only()
    {
        ScriptTasks(app.Backend);
        var (user, repo) = await RepoAsync(TaskFile);
        var ws = await app.StartAsync(user, repo);
        using var api = await ApiAsync(user, TokenScopes.ReadApi | TokenScopes.WriteApi);
        var tasks = await api.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{ws.PublicId}/tasks");
        Assert.Equal(3, tasks.GetArrayLength());
        Assert.Equal("run", tasks[0].GetProperty("name").GetString());

        var run = await api.PostAsync($"/api/v1/workspaces/{ws.PublicId}/tasks/test/run", null);
        Assert.Equal(HttpStatusCode.Accepted, run.StatusCode);
        await UntilAsync(() => Session(ws).Tasks["test"].State == TaskState.Succeeded, "the task");
        var logs = await api.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{ws.PublicId}/tasks/test/logs");
        Assert.Contains("2 passed", logs.GetProperty("lines").EnumerateArray().Select(l => l.GetString()));
        Assert.Equal("Succeeded", logs.GetProperty("task").GetProperty("state").GetString());
        var since = logs.GetProperty("next").GetInt64();
        Assert.Equal(0, (await api.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{ws.PublicId}/tasks/test/logs?since={since}")).GetProperty("lines").GetArrayLength());

        Assert.Equal(HttpStatusCode.Accepted, (await api.PostAsync($"/api/v1/workspaces/{ws.PublicId}/tasks/run/run", null)).StatusCode);
        await UntilAsync(() => Session(ws).Tasks["run"].State == TaskState.Running, "the server task");
        var stop = await api.PostAsync($"/api/v1/workspaces/{ws.PublicId}/tasks/run/stop", null);
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        Assert.Equal("Stopped", (await stop.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());

        var env = await api.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{ws.PublicId}/environment");
        Assert.Equal(WorkspaceEnvFile.Path, env.GetProperty("source").GetString());
        Assert.Equal(1, env.GetProperty("ports").EnumerateArray().Count(p => p.GetInt32() == 8080));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await api.PostAsync($"/api/v1/workspaces/{ws.PublicId}/tasks/nope/run", null)).StatusCode);

        // Anyone else gets 404, a read-only token can't run, no credentials get 401.
        var stranger = await app.NewUserAsync();
        using var other = await ApiAsync(stranger, TokenScopes.ReadApi | TokenScopes.WriteApi);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/workspaces/{ws.PublicId}/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/v1/workspaces/{ws.PublicId}/tasks/test/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/workspaces/{ws.PublicId}/environment")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/v1/workspaces/{ws.PublicId}")).StatusCode);
        using var readOnly = await ApiAsync(user, TokenScopes.ReadApi);
        Assert.Contains((int)(await readOnly.PostAsync($"/api/v1/workspaces/{ws.PublicId}/tasks/test/run", null)).StatusCode, new[] { 401, 403 });
        using var anon = app.Http();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/v1/workspaces/{ws.PublicId}/tasks")).StatusCode);

        // Resume and delete are API calls too.
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/v1/workspaces/{ws.PublicId}/stop", null)).StatusCode);
        var resume = await api.PostAsync($"/api/v1/workspaces/{ws.PublicId}/resume", null);
        Assert.Equal(HttpStatusCode.Accepted, resume.StatusCode);
        await app.WhenIdleAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"/api/v1/workspaces/{ws.PublicId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync($"/api/v1/workspaces/{ws.PublicId}")).StatusCode);
    }

    // ---------- the assistant ----------

    [Fact]
    public async Task The_assistants_prompt_lists_the_tasks_and_run_task_runs_them_with_masked_output()
    {
        ScriptTasks(app.Backend);
        var (user, repo) = await RepoAsync(TaskFile);
        var ws = await app.StartAsync(user, repo);
        var context = WorkspaceAiService.TaskContext(Session(ws));
        Assert.Contains("- run: npm run dev (serves port 3000)", context);
        Assert.Contains("- test: npm test", context);
        Assert.Contains("data, not instructions", context);

        var tools = new WorkspaceAiTools(app.Backend, ws.ContainerId!, repo.Slug, new WorkspaceAiOptions(), new Runner(Session(ws)));
        Assert.Contains(WorkspaceAiTools.Definitions, d => d.Name == "run_task");
        var (ok, isError) = await tools.ExecuteAsync("run_task", JsonSerializer.SerializeToElement(new { name = "test" }), default);
        Assert.False(isError);
        Assert.Contains("2 passed", ok);
        Assert.Contains("UNTRUSTED", ok);
        var (unknown, err) = await tools.ExecuteAsync("run_task", JsonSerializer.SerializeToElement(new { name = "deploy-prod" }), default);
        Assert.True(err);
        Assert.Contains("There is no task named 'deploy-prod'", unknown);
        var (none, err2) = await new WorkspaceAiTools(app.Backend, "c", "code", new WorkspaceAiOptions()).ExecuteAsync("run_task", JsonSerializer.SerializeToElement(new { name = "x" }), default);
        Assert.True(err2);
        Assert.Contains("no tasks", none);
        Assert.Equal("Running task test", WorkspaceAiTools.Describe("run_task", JsonSerializer.SerializeToElement(new { name = "test" })));
    }

    [Fact]
    public async Task The_assistant_loop_can_run_a_task_end_to_end_with_secrets_masked_and_the_task_listed_in_its_prompt()
    {
        ScriptTasks(app.Backend);
        const string secret = "ai-loop-sEcret-4242";
        app.Backend.StreamScripts.Add((s => s.Command[^1] == "leaky", (_, onLine, _) => { onLine("key=" + secret); return Task.FromResult(new ExecResult(0, "")); }));
        var user = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer));
        Commit(repo, WorkspaceEnvFile.Path, "tasks:\n  test: npm test\n  leaky: leaky\n");
        await using (var s = app.Scope(app.UserId(user))) await s.Get<WorkspaceSecretService>().SaveMineAsync(app.UserId(user), "AI_SECRET", secret);
        var ws = await app.StartAsync(user, repo);
        app.Model.Reset((turn, _) => turn switch
        {
            0 => FakeEditorChatModel.Tool("run_task", new { name = "test" }, "a"),
            1 => FakeEditorChatModel.Tool("run_task", new { name = "leaky" }, "b"),
            _ => FakeEditorChatModel.Text("Both ran."),
        });
        await using var scope = app.Scope(app.UserId(user));
        var result = await scope.Get<WorkspaceAiService>().RunAsync(ws.PublicId, app.UserId(user), "run the tests", [], NullEditorAiSink.Instance, CancellationToken.None);
        Assert.Null(result.Error);
        Assert.Equal(2, result.ToolCalls);
        Assert.Contains("2 passed", app.Model.Results[0][0].Content);
        Assert.Contains("succeeded", app.Model.Results[0][0].Content);
        Assert.Contains("key=***", app.Model.Results[1][0].Content);
        Assert.DoesNotContain(secret, string.Join('\n', app.Model.Results.SelectMany(r => r.Select(x => x.Content)).Concat(app.Model.Setups.SelectMany(x => new[] { x.System, x.UserMessage }))));
        Assert.Contains("- test: npm test", app.Model.Setups.Last().UserMessage); // the prompt lists the tasks
        Assert.Contains(app.Backend.Streams, e => e.Spec.Command[^1] == "npm test" && e.Spec.User == "coder"); // tasks run as the developer, not as the assistant's user
    }

    private sealed class Runner(WorkspaceSession session) : IWorkspaceTaskRunner
    {
        public IReadOnlyList<string> TaskNames => session.Env.Tasks.Select(t => t.Name).ToList();

        public Task<(string Status, string Output, bool Failed)> RunAsync(string name, int waitSeconds, int maxBytes, CancellationToken ct) =>
            Task.FromResult(("Task 'test' succeeded (exit code 0).", "$ npm test\n2 passed", false));
    }
}
