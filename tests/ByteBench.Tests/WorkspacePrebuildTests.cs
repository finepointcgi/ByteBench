using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Git;
using ByteBench.Services.Webhooks;
using ByteBench.Services.Workspaces;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

public class PrebuildConfigTests
{
    private static WorkspaceProvisionPlan Plan(string yaml, bool allowImages = false)
    {
        var result = WorkspaceEnvFile.Parse(WorkspaceEnvFile.Path, yaml);
        return WorkspacePlanner.Build(new WorkspaceEnvLoad(result, null, null), new WorkspaceImage(new FakeWorkspaceBackend(), new StaticOptions(new WorkspaceOptions { Image = "base:1" }), new FakeHost(), Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkspaceImage>.Instance), allowImages);
    }

    [Fact]
    public void The_hash_covers_the_image_the_setup_commands_and_submodules_and_nothing_else()
    {
        var one = WorkspacePrebuildService.HashOf(Plan("setup:\n  - npm ci\n"));
        Assert.Equal(one, WorkspacePrebuildService.HashOf(Plan("# comment\nsetup:\n  - npm ci\nports: [3000]\nenv: {A: b}\ntasks:\n  dev: npm start\n"))); // tasks, ports, variables don't matter
        Assert.NotEqual(one, WorkspacePrebuildService.HashOf(Plan("setup:\n  - npm ci\n  - make\n")));
        Assert.NotEqual(one, WorkspacePrebuildService.HashOf(Plan("setup:\n  - npm ci\nsubmodules: false\n")));
        Assert.NotEqual(one, WorkspacePrebuildService.HashOf(Plan("toolchain: node@22\nsetup:\n  - npm ci\n"))); // another image
    }

    [Fact]
    public void Services_are_not_part_of_a_prebuild()
    {
        var plain = WorkspacePrebuildService.HashOf(Plan("setup:\n  - make\n"));
        var withService = WorkspacePrebuildService.HashOf(Plan("setup:\n  - make\nservices:\n  db:\n    image: postgres:16\n    ports: [5432]\n"));
        Assert.Equal(plain, withService);
    }

    private sealed class StaticOptions(WorkspaceOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<WorkspaceOptions>
    {
        public WorkspaceOptions CurrentValue => value;
        public WorkspaceOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<WorkspaceOptions, string?> listener) => null;
    }

    private sealed class FakeHost : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = "/nonexistent";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Theory]
    [InlineData("main", null, "main", true)]
    [InlineData("dev", null, "main", false)]
    [InlineData("main", "release/*, dev", "release/1.2", true)]
    [InlineData("main", "release/*, dev", "dev", true)]
    [InlineData("main", "release/*, dev", "main", false)]
    [InlineData("main", "release/*", "xrelease/1", false)]
    public void Which_branches_are_prebuilt(string defaultBranch, string? configured, string pushed, bool expected) =>
        Assert.Equal(expected, WorkspacePrebuildManager.BranchMatches(configured, defaultBranch, pushed));
}

/// <summary>Prebuilds end to end on the fake Docker backend: push → build → workspace starts from it → cleanup. The real Docker is never touched.</summary>
public class WorkspacePrebuildTests(WorkspaceApp app) : IClassFixture<WorkspaceApp>
{
    private const string Env = "setup:\n  - npm ci\n  - make build\n";

    private WorkspacePrebuildService Service => app.Services.GetRequiredService<WorkspacePrebuildService>();

    private async Task<(Repository Repo, string Dev)> RepoAsync(string? yaml = Env, bool enable = true, string branches = "")
    {
        var dev = await app.NewUserAsync("pbdev");
        var repo = await app.RepoWithMembersAsync((dev, ProjectRole.Developer));
        if (yaml != null) app.Commit(repo, "main", ".bytebench/workspace.yml", yaml);
        if (enable) await SetAsync(repo, "alice", true, branches);
        return (repo, dev);
    }

    private async Task SetAsync(Repository repo, string user, bool enabled, string branches = "", int keepLast = 3, int days = 14)
    {
        await using var s = app.Scope(app.UserId(user));
        await s.Get<WorkspacePrebuildManager>().SetSettingsAsync(app.ProjectKey(repo.ProjectId), repo.Slug, enabled, branches, keepLast, days, app.UserId(user));
    }

    private async Task PushAsync(Repository repo, string branch, string? sha = null, string user = "alice")
    {
        sha ??= GitOps.ResolveSha(app.Services.GetRequiredService<RepoStorage>().PathFor(app.ProjectKey(repo.ProjectId), repo.Slug), branch)!;
        await using var s = app.Scope(app.UserId(user));
        s.Get<PushCapture>().SetRefs([new PushedRef(PushedRef.ZeroSha, sha, "refs/heads/" + branch)]);
        foreach (var l in s.Get<IEnumerable<IGitPushListener>>().OfType<PrebuildPushListener>()) await l.OnPushAsync(repo.Id, app.UserId(user), CancellationToken.None);
        await Service.WhenIdleAsync();
    }

    private async Task<List<WorkspacePrebuild>> PrebuildsAsync(Repository repo)
    {
        await using var db = app.Db();
        return await db.WorkspacePrebuilds.AsNoTracking().Where(p => p.RepositoryId == repo.Id).OrderBy(p => p.Id).ToListAsync();
    }

    private List<FakeWorkspaceBackend.ExecCall> ExecsOf(string containerId) => app.Backend.Execs.Where(e => e.ContainerId == containerId).ToList();

    private string BuildContainerOf(WorkspacePrebuild p) => app.Backend.Commits.Single(c => c.Tag == p.ImageTag).ContainerId;

    [Fact]
    public async Task A_push_to_the_default_branch_builds_a_prebuild_and_new_workspaces_start_from_it()
    {
        var (repo, dev) = await RepoAsync();
        await PushAsync(repo, "main");

        var pb = Assert.Single(await PrebuildsAsync(repo));
        Assert.Equal(PrebuildState.Ready, pb.State);
        Assert.Equal("main", pb.Ref);
        Assert.Equal("push", pb.Trigger);
        Assert.StartsWith("bytebench-prebuild:", pb.ImageTag);
        Assert.Equal(40, pb.CommitSha!.Length);
        Assert.Null(pb.GitTokenHash);
        Assert.Contains("npm ci", pb.Log);
        Assert.Contains("make build", pb.Log);
        Assert.Contains(ExecsOf(BuildContainerOf(pb)), e => e.Spec.Command.Contains(WorkspaceTasks.SetupMarker)); // so the workspace finds its setup up to date
        Assert.Contains(pb.ImageTag!, app.Backend.PrebuildImages.Keys);
        var container = BuildContainerOf(pb);
        Assert.Contains(container, app.Backend.Removed); // the build container is gone, the snapshot stays
        Assert.Contains(ExecsOf(container), e => e.Spec.Command.SequenceEqual(["sh", "-c", "npm ci"]) && e.Spec.WorkDir == "/workspace/code");
        Assert.Contains(app.Backend.Started, s => s.WorkspaceId == -pb.Id); // the build container carries the prebuild's id, negated, so the reaper can tell it apart

        var ws = await app.StartAsync(dev, repo);
        Assert.Equal(WorkspaceState.Running, ws.State);
        Assert.Equal(pb.ImageTag, ws.Image);
        await using var db = app.Db();
        Assert.Equal(pb.Id, (await db.WorkspaceAllocations.FirstAsync(a => a.WorkspaceId == ws.Id)).PrebuildId);
        var execs = ExecsOf(ws.ContainerId!);
        Assert.Contains(execs, e => e.Spec.Command.Any(a => a.Contains("git reset -q --hard"))); // the seeded checkout is moved to the requested ref
        Assert.Contains(execs, e => e.Spec.Command.Any(a => a.Contains("--no-checkout"))); // (the normal clone script runs first: it only fetches into an existing checkout)
        Assert.Contains(app.Backend.Ready, id => id == ws.ContainerId);
    }

    [Fact]
    public async Task Secrets_never_reach_the_snapshot_and_the_repositorys_own_commands_run_without_a_credential()
    {
        var (repo, _) = await RepoAsync();
        await PushAsync(repo, "main");
        var pb = Assert.Single(await PrebuildsAsync(repo));
        var container = BuildContainerOf(pb);
        var started = app.Backend.Started.Single(s => s.WorkspaceId == -pb.Id);
        Assert.All(started.Env.Keys, k => Assert.StartsWith("BB_", k)); // nothing but BB_* in the build container's environment
        Assert.False(started.PublishIdePort);

        var execs = ExecsOf(container);
        int Index(Func<ExecSpec, bool> f) => execs.FindIndex(e => f(e.Spec));
        var credentialWritten = Index(e => e.Stdin?.Contains("gbw_") == true);
        var firstSetup = Index(e => e.Command.SequenceEqual(["sh", "-c", "npm ci"]));
        var lastSetup = Index(e => e.Command.SequenceEqual(["sh", "-c", "make build"]));
        var scrubs = execs.Select((e, i) => (e, i)).Where(x => x.e.Spec.Command.Any(a => a.Contains("rm -f \"$HOME/.git-credentials\""))).Select(x => x.i).ToList();
        Assert.True(credentialWritten >= 0 && credentialWritten < firstSetup);
        Assert.Contains(scrubs, i => i > credentialWritten && i < firstSetup); // deleted before the repository's commands run
        Assert.Contains(scrubs, i => i > lastSetup); // and again before the snapshot
        Assert.Equal(execs.Count - 1, scrubs.Max()); // the last thing done in the container before the snapshot
        Assert.All(execs.Skip(firstSetup), e => { Assert.Null(e.Spec.Stdin); Assert.DoesNotContain("gbw_", string.Join(' ', e.Spec.Command)); Assert.All(e.Spec.Env?.Keys ?? [], k => Assert.Contains(k, WorkspaceTasks.CacheEnvironment.Keys)); }); // only dependency-cache locations
        Assert.DoesNotContain("gbw_", pb.Log);
        var token = Regex.Match(execs[credentialWritten].Spec.Stdin!, "gbw_[A-Za-z0-9_-]+").Value;
        Assert.DoesNotContain(token, pb.Log);
        Assert.Null(await WorkspaceGitAuth.ValidateAsync(app.Services, app.UserId("alice"), token)); // the token dies with the build
    }

    [Fact]
    public async Task The_build_token_reads_only_its_own_repository_while_building_and_the_reaper_leaves_the_build_alone()
    {
        var (repo, _) = await RepoAsync();
        var seen = new List<string>();
        (Func<ExecSpec, bool>, Func<ExecSpec, ExecResult>) probe = (s => s.Command.SequenceEqual(["sh", "-c", "npm ci"]), s =>
        {
            // The most recent credential written into any container is this build's (tests in a class run one after another).
            var credential = app.Backend.Execs.Select(e => e.Spec.Stdin).Last(x => x?.Contains("gbw_") == true)!;
            var token = Regex.Match(credential, "gbw_[A-Za-z0-9_-]+").Value;
            var v = WorkspaceGitAuth.ValidateAsync(app.Services, app.UserId("alice"), token).GetAwaiter().GetResult();
            seen.Add(v is { } ok ? $"{ok.RepositoryId}:{ok.Scopes}" : "invalid");
            app.Services.GetRequiredService<WorkspaceReaper>().SweepAsync(CancellationToken.None).GetAwaiter().GetResult(); // a sweep in the middle of the build
            return new ExecResult(0, "ok");
        });
        app.Backend.ExecScripts.Add(probe);
        try { await PushAsync(repo, "main"); }
        finally { app.Backend.ExecScripts.Remove(probe); }
        Assert.Equal([$"{repo.Id}:{TokenScopes.ReadRepository}"], seen); // read-only, this repository only
        Assert.Equal(PrebuildState.Ready, Assert.Single(await PrebuildsAsync(repo)).State); // the sweep did not remove the running build as an orphan
    }

    [Fact]
    public async Task Only_the_configured_branches_and_only_with_an_environment_file_are_prebuilt()
    {
        var (repo, _) = await RepoAsync(Env, branches: "release/*");
        app.Commit(repo, "release/1", "README.md", "r", from: "main");
        app.Commit(repo, "feature/x", "README.md", "f", from: "main");
        await PushAsync(repo, "main"); // not in the list
        await PushAsync(repo, "feature/x");
        Assert.Empty(await PrebuildsAsync(repo));
        await PushAsync(repo, "release/1");
        Assert.Equal(["release/1"], (await PrebuildsAsync(repo)).Select(p => p.Ref));

        var (bare, _) = await RepoAsync(null); // no .bytebench/workspace.yml
        await PushAsync(bare, "main");
        Assert.Empty(await PrebuildsAsync(bare));

        var (off, _) = await RepoAsync(Env, enable: false); // switched off
        await PushAsync(off, "main");
        Assert.Empty(await PrebuildsAsync(off));
    }

    [Fact]
    public async Task The_same_commit_is_not_built_twice_but_rebuild_now_forces_it_and_a_newer_push_supersedes_a_queued_build()
    {
        var (repo, _) = await RepoAsync();
        await PushAsync(repo, "main");
        await PushAsync(repo, "main"); // the same commit again
        Assert.Single(await PrebuildsAsync(repo));

        await using (var s = app.Scope(app.UserId("alice")))
            await s.Get<WorkspacePrebuildManager>().RebuildAsync(app.ProjectKey(repo.ProjectId), repo.Slug, null, app.UserId("alice"));
        await Service.WhenIdleAsync();
        var all = await PrebuildsAsync(repo);
        Assert.Equal(2, all.Count);
        Assert.Equal("manual", all[1].Trigger);

        // A build is running; two more pushes arrive: the older queued one never starts.
        using var gate = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        (Func<ExecSpec, bool>, Func<ExecSpec, ExecResult>) blocker = (s => s.Command.SequenceEqual(["sh", "-c", "make build"]), _ => { entered.Set(); gate.Wait(TimeSpan.FromSeconds(20)); return new ExecResult(0, "ok"); });
        app.Backend.ExecScripts.Add(blocker);
        try
        {
            var sha1 = app.Commit(repo, "main", "a.txt", "1");
            await using (var s = app.Scope(app.UserId("alice")))
            {
                s.Get<PushCapture>().SetRefs([new PushedRef(PushedRef.ZeroSha, sha1, "refs/heads/main")]);
                foreach (var l in s.Get<IEnumerable<IGitPushListener>>().OfType<PrebuildPushListener>()) await l.OnPushAsync(repo.Id, app.UserId("alice"), default);
            }
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(30))), "the worker never reached build #3"); // it is now blocked inside build #3
            var sha2 = app.Commit(repo, "main", "a.txt", "2");
            var sha3 = app.Commit(repo, "main", "a.txt", "3");
            await Service.EnqueueAsync(repo.Id, "main", sha2, "push", app.UserId("alice"));
            await Service.EnqueueAsync(repo.Id, "main", sha3, "push", app.UserId("alice"));
            gate.Set();
            await Service.WhenIdleAsync();
        }
        finally { gate.Set(); app.Backend.ExecScripts.Remove(blocker); }
        var states = (await PrebuildsAsync(repo)).Skip(2).Select(p => p.State).ToList();
        Assert.Equal([PrebuildState.Ready, PrebuildState.Superseded, PrebuildState.Ready], states);
    }

    [Fact]
    public async Task A_failing_setup_command_fails_the_prebuild_with_its_log_and_never_blocks_a_normal_start()
    {
        var (repo, dev) = await RepoAsync();
        (Func<ExecSpec, bool>, Func<ExecSpec, ExecResult>) fail = (s => s.Command.SequenceEqual(["sh", "-c", "make build"]), _ => new ExecResult(2, "make: *** boom"));
        app.Backend.ExecScripts.Add(fail);
        try { await PushAsync(repo, "main"); }
        finally { app.Backend.ExecScripts.Remove(fail); }
        var pb = Assert.Single(await PrebuildsAsync(repo));
        Assert.Equal(PrebuildState.Failed, pb.State);
        Assert.Contains("Setup command 2 failed (exit code 2)", pb.Error);
        Assert.Contains("make: *** boom", pb.Log);
        Assert.Contains("FAILED", pb.Log);
        Assert.Null(pb.ImageTag);
        Assert.Null(pb.GitTokenHash);
        Assert.DoesNotContain(app.Backend.Commits, c => c.Tag.EndsWith($"-p{pb.Id}"));
        var buildContainer = app.Backend.Execs.Last(e => e.Spec.Command.SequenceEqual(["sh", "-c", "make build"])).ContainerId;
        Assert.Contains(buildContainer, app.Backend.Removed); // the build container is removed even though the build failed

        var ws = await app.StartAsync(dev, repo); // a normal start
        Assert.Equal(WorkspaceState.Running, ws.State);
        Assert.Equal("test-image:1", ws.Image);
        Assert.Contains(ExecsOf(ws.ContainerId!), e => e.Spec.Command.Any(a => a.Contains("git clone")));
    }

    [Fact]
    public async Task A_snapshot_Docker_cannot_save_and_an_unusable_environment_file_are_failed_prebuilds_not_errors()
    {
        var (repo, _) = await RepoAsync();
        app.Backend.FailCommit = true;
        try { await PushAsync(repo, "main"); } finally { app.Backend.FailCommit = false; }
        Assert.Equal(PrebuildState.Failed, Assert.Single(await PrebuildsAsync(repo)).State);

        var (broken, _) = await RepoAsync("setup: {a: 1}");
        await PushAsync(broken, "main");
        var pb = Assert.Single(await PrebuildsAsync(broken));
        Assert.Equal(PrebuildState.Failed, pb.State);
        Assert.Contains("environment file has problems", pb.Error);
        Assert.DoesNotContain(app.Backend.Started, s => s.WorkspaceId == -pb.Id); // no container for a file that can't be read

        var (nodocker, _) = await RepoAsync();
        app.Backend.DockerAvailable = false;
        try { await PushAsync(nodocker, "main"); } finally { app.Backend.DockerAvailable = true; }
        Assert.Contains("Docker isn't available", Assert.Single(await PrebuildsAsync(nodocker)).Error);
    }

    [Fact]
    public async Task A_prebuild_is_ignored_when_the_configuration_changed_it_is_too_old_its_image_is_gone_or_the_base_image_changed()
    {
        var (repo, dev) = await RepoAsync();
        await PushAsync(repo, "main");
        var pb = Assert.Single(await PrebuildsAsync(repo));

        app.Commit(repo, "main", ".bytebench/workspace.yml", "setup:\n  - npm ci\n  - something new\n"); // setup changed since
        Assert.Equal("test-image:1", await ImageOfNewWorkspaceAsync(dev, repo));
        app.Commit(repo, "main", ".bytebench/workspace.yml", Env); // back to what was built
        Assert.Equal(pb.ImageTag, await ImageOfNewWorkspaceAsync(dev, repo));

        await Mutate(pb.Id, p => p.FinishedAt = DateTime.UtcNow.AddDays(-30)); // older than the 14 day retention
        Assert.Equal("test-image:1", await ImageOfNewWorkspaceAsync(dev, repo));
        await Mutate(pb.Id, p => p.FinishedAt = DateTime.UtcNow);

        await Mutate(pb.Id, p => p.BaseImage = "some-older-image:0"); // built on another workspace image
        Assert.Equal("test-image:1", await ImageOfNewWorkspaceAsync(dev, repo));
        await Mutate(pb.Id, p => p.BaseImage = "test-image:1");

        app.Backend.PrebuildImages.TryRemove(pb.ImageTag!, out _); // somebody ran docker image rm
        Assert.Equal("test-image:1", await ImageOfNewWorkspaceAsync(dev, repo));
        Assert.Equal(PrebuildState.Expired, (await PrebuildsAsync(repo)).Single(p => p.Id == pb.Id).State);
    }

    [Fact]
    public async Task A_prebuild_whose_image_disappears_between_the_start_request_and_the_container_falls_back_to_the_base_image()
    {
        var (repo, dev) = await RepoAsync();
        await PushAsync(repo, "main");
        var pb = Assert.Single(await PrebuildsAsync(repo));
        var user = app.UserId(dev);
        var gitToken = WorkspaceTokens.NewGitToken();
        int id;
        await using (var db = app.Db())
        {
            var ws = new Workspace
            {
                PublicId = WorkspaceTokens.NewPublicId(), OwnerId = user, RepositoryId = repo.Id, ProjectId = repo.ProjectId, Ref = "main", State = WorkspaceState.Starting,
                Image = pb.ImageTag!, ConnectionTokenHash = WorkspaceTokens.Hash("c"), GitTokenHash = WorkspaceTokens.Hash(gitToken), CanPush = true, ExpiresAt = DateTime.UtcNow.AddHours(1),
            };
            db.Workspaces.Add(ws);
            db.WorkspaceAllocations.Add(new WorkspaceAllocation { Workspace = ws, PrebuildId = pb.Id, CpuMillis = 2000, MemoryMb = 4096, DiskMb = 10240 });
            await db.SaveChangesAsync();
            id = ws.Id;
        }
        app.Backend.PrebuildImages.TryRemove(pb.ImageTag!, out _); // removed after the start request chose it
        var plan = WorkspacePlanner.Build(new WorkspaceEnvLoad(null, null, null), app.Services.GetRequiredService<WorkspaceImage>(), false);
        await app.Services.GetRequiredService<WorkspaceLifecycle>().ProvisionAsync(id, gitToken, WorkspaceTokens.NewConnectionToken(), plan);
        await using var check = app.Db();
        var after = await check.Workspaces.AsNoTracking().FirstAsync(w => w.Id == id);
        Assert.Equal(WorkspaceState.Running, after.State);
        Assert.Equal("test-image:1", after.Image);
        Assert.Null((await check.WorkspaceAllocations.AsNoTracking().FirstAsync(a => a.WorkspaceId == id)).PrebuildId);
        Assert.Contains(ExecsOf(after.ContainerId!), e => e.Spec.Command.Any(a => a.Contains("git clone")));
    }

    [Fact]
    public async Task Resuming_a_stopped_workspace_uses_its_own_volumes_never_a_newer_prebuild()
    {
        var (repo, dev) = await RepoAsync();
        await PushAsync(repo, "main");
        var first = Assert.Single(await PrebuildsAsync(repo));
        var ws = await app.StartAsync(dev, repo);
        Assert.Equal(first.ImageTag, ws.Image);
        await using (var s = app.Scope(app.UserId(dev))) await s.Get<WorkspaceService>().StopAsync(ws.PublicId, app.UserId(dev));
        await using (var s = app.Scope(app.UserId("alice"))) await s.Get<WorkspacePrebuildManager>().RebuildAsync(app.ProjectKey(repo.ProjectId), repo.Slug, null, app.UserId("alice"));
        await Service.WhenIdleAsync();
        Assert.Equal(2, (await PrebuildsAsync(repo)).Count);

        await using (var s = app.Scope(app.UserId(dev))) await s.Get<WorkspaceService>().ResumeAsync(ws.PublicId, app.UserId(dev));
        await app.WhenIdleAsync();
        var resumed = await app.ReloadAsync(ws.PublicId);
        Assert.Equal(WorkspaceState.Running, resumed.State);
        Assert.Equal("test-image:1", resumed.Image); // the base image over its existing volumes
        Assert.DoesNotContain(ExecsOf(resumed.ContainerId!), e => e.Spec.Command.Any(a => a.Contains("git reset -q --hard"))); // nothing was moved or reset under the user's work
    }

    [Fact]
    public async Task A_toolchain_prebuild_is_built_on_the_toolchain_image_and_only_used_for_it()
    {
        var (repo, dev) = await RepoAsync("toolchain: node@22\nsetup:\n  - npm ci\n");
        await PushAsync(repo, "main");
        var pb = Assert.Single(await PrebuildsAsync(repo));
        Assert.Equal(PrebuildState.Ready, pb.State);
        Assert.NotEqual("test-image:1", pb.BaseImage);
        Assert.Contains(app.Backend.Started, s => s.WorkspaceId == -pb.Id && s.Image == pb.BaseImage);
        Assert.Equal(pb.ImageTag, await ImageOfNewWorkspaceAsync(dev, repo));
        app.Commit(repo, "main", ".bytebench/workspace.yml", "toolchain: node@20\nsetup:\n  - npm ci\n");
        Assert.NotEqual(pb.ImageTag, await ImageOfNewWorkspaceAsync(dev, repo)); // another toolchain: not this prebuild
    }

    [Fact]
    public async Task Services_are_not_started_for_a_prebuild()
    {
        var (repo, _) = await RepoAsync("setup:\n  - make\nservices:\n  db:\n    image: postgres:16\n    ports: [5432]\n");
        var sidecarsBefore = app.Backend.Sidecars.Count;
        await PushAsync(repo, "main");
        Assert.Equal(PrebuildState.Ready, Assert.Single(await PrebuildsAsync(repo)).State);
        Assert.Equal(sidecarsBefore, app.Backend.Sidecars.Count);
    }

    [Fact]
    public async Task Cleanup_keeps_the_newest_per_branch_removes_the_rest_and_retries_images_still_in_use()
    {
        var (repo, _) = await RepoAsync();
        await SetAsync(repo, "alice", true, "", keepLast: 2);
        for (var i = 0; i < 4; i++)
        {
            app.Commit(repo, "main", "n.txt", i.ToString());
            await PushAsync(repo, "main");
        }
        var all = await PrebuildsAsync(repo);
        Assert.Equal(4, all.Count);
        Assert.Equal([PrebuildState.Expired, PrebuildState.Expired, PrebuildState.Ready, PrebuildState.Ready], all.Select(p => p.State));
        Assert.Equal(2, app.Backend.ImagesRemoved.Count(t => all.Any(p => p.Id == int.Parse(Regex.Match(t, @"-p(\d+)$").Groups[1].Value))));
        Assert.Null(all[0].ImageTag);

        var oldest = all[2];
        app.Backend.ImagesInUse.Add(oldest.ImageTag!); // a workspace still runs from it
        await SetAsync(repo, "alice", true, "", keepLast: 1);
        await using (var s = app.Scope(app.UserId("alice"))) await s.Get<WorkspacePrebuildManager>().CleanupAsync(app.ProjectKey(repo.ProjectId), repo.Slug, app.UserId("alice"));
        var now = (await PrebuildsAsync(repo)).Single(p => p.Id == oldest.Id);
        Assert.Equal(PrebuildState.Expired, now.State);
        Assert.NotNull(now.ImageTag); // kept so a later cleanup retries
        app.Backend.ImagesInUse.Clear();
        await Service.CleanupAsync(null, CancellationToken.None);
        Assert.Null((await PrebuildsAsync(repo)).Single(p => p.Id == oldest.Id).ImageTag);

        // Switching prebuilds off frees everything.
        await SetAsync(repo, "alice", false);
        await Service.CleanupAsync(repo.Id, CancellationToken.None);
        Assert.DoesNotContain(await PrebuildsAsync(repo), p => p.State == PrebuildState.Ready);
    }

    [Fact]
    public async Task Orphaned_images_and_old_history_are_cleaned_up()
    {
        var (repo, _) = await RepoAsync();
        app.Backend.PrebuildImages["bytebench-prebuild:deadbeef-r999-p999"] = 0; // no row knows it
        await Service.CleanupAsync(null, CancellationToken.None);
        Assert.Contains("bytebench-prebuild:deadbeef-r999-p999", app.Backend.ImagesRemoved);
        Assert.NotNull(repo);
    }

    [Fact]
    public async Task Permissions_developers_read_maintainers_change_and_outsiders_see_nothing()
    {
        var (repo, dev) = await RepoAsync();
        var key = app.ProjectKey(repo.ProjectId);
        await PushAsync(repo, "main");
        var outsider = await app.NewUserAsync();
        await using (var s = app.Scope(app.UserId(dev)))
        {
            var m = s.Get<WorkspacePrebuildManager>();
            Assert.Single(await m.ListAsync(key, repo.Slug, app.UserId(dev)));
            Assert.Contains("npm ci", (await m.GetAsync(key, repo.Slug, (await PrebuildsAsync(repo))[0].Id, app.UserId(dev)))!.Log);
            await Assert.ThrowsAsync<ForbiddenException>(() => m.SetSettingsAsync(key, repo.Slug, false, "", 3, 14, app.UserId(dev)));
            await Assert.ThrowsAsync<ForbiddenException>(() => m.RebuildAsync(key, repo.Slug, null, app.UserId(dev)));
            await Assert.ThrowsAsync<ForbiddenException>(() => m.CleanupAsync(key, repo.Slug, app.UserId(dev)));
        }
        await using (var s = app.Scope(app.UserId(outsider)))
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => s.Get<WorkspacePrebuildManager>().ListAsync(key, repo.Slug, app.UserId(outsider)));
        await using var admin = app.Scope(app.UserId("alice"));
        var am = admin.Get<WorkspacePrebuildManager>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => am.SetSettingsAsync(key, repo.Slug, true, "bad branch;;..", 3, 14, app.UserId("alice")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => am.SetSettingsAsync(key, repo.Slug, true, "", 0, 14, app.UserId("alice")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => am.SetSettingsAsync(key, repo.Slug, true, "", 3, 500, app.UserId("alice")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => am.RebuildAsync(key, repo.Slug, "nope", app.UserId("alice")));
        await using var db = app.Db();
        Assert.Contains(db.AuditEvents, a => a.Action == "workspace.prebuild_built");
    }

    [Fact]
    public async Task Rebuild_now_needs_prebuilds_on_and_an_environment_file()
    {
        var (off, _) = await RepoAsync(Env, enable: false);
        await using var s = app.Scope(app.UserId("alice"));
        var m = s.Get<WorkspacePrebuildManager>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => m.RebuildAsync(app.ProjectKey(off.ProjectId), off.Slug, null, app.UserId("alice")));
        Assert.Contains("off for this repository", ex.Message);
        var (bare, _) = await RepoAsync(null);
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => m.RebuildAsync(app.ProjectKey(bare.ProjectId), bare.Slug, null, app.UserId("alice")));
        Assert.Contains("nothing to prebuild", ex.Message);
    }

    [Fact]
    public async Task The_rest_api_manages_settings_history_logs_rebuilds_and_cleanup()
    {
        var (repo, dev) = await RepoAsync(enable: false);
        var key = app.ProjectKey(repo.ProjectId);
        var baseUrl = $"/api/v1/projects/{key}/repos/{repo.Slug}";
        using var admin = await app.ApiClientAsync("alice");
        using var developer = await app.ApiClientAsync(dev);
        admin.BaseAddress = developer.BaseAddress = app.ServerAddress;
        static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

        var put = await admin.PutAsync(baseUrl + "/prebuild-settings", Json(new { enabled = true, branches = new[] { "main", "release/*" }, keepLast = 2, retentionDays = 7 }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var settings = JsonDocument.Parse(await put.Content.ReadAsStringAsync()).RootElement;
        Assert.True(settings.GetProperty("enabled").GetBoolean());
        Assert.Equal(2, settings.GetProperty("keepLast").GetInt32());
        Assert.Equal(["main", "release/*"], settings.GetProperty("branches").EnumerateArray().Select(b => b.GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await developer.PutAsync(baseUrl + "/prebuild-settings", Json(new { enabled = false }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await developer.GetAsync(baseUrl + "/prebuild-settings")).StatusCode);

        var rebuild = await admin.PostAsync(baseUrl + "/prebuilds", Json(new { branch = "main" }));
        Assert.Equal(HttpStatusCode.Accepted, rebuild.StatusCode);
        await Service.WhenIdleAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await developer.PostAsync(baseUrl + "/prebuilds", Json(new { }))).StatusCode);

        var list = JsonDocument.Parse(await developer.GetStringAsync(baseUrl + "/prebuilds")).RootElement;
        var first = list[0];
        Assert.Equal("Ready", first.GetProperty("state").GetString());
        Assert.True(first.GetProperty("usable").GetBoolean());
        Assert.True(!first.TryGetProperty("log", out var listLog) || listLog.ValueKind == JsonValueKind.Null); // the list has no logs
        var one = JsonDocument.Parse(await developer.GetStringAsync($"{baseUrl}/prebuilds/{first.GetProperty("id").GetInt32()}")).RootElement;
        Assert.Contains("npm ci", one.GetProperty("log").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await developer.GetAsync($"{baseUrl}/prebuilds/999999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await developer.GetAsync($"/api/v1/projects/{key}/repos/nope/prebuilds")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync(baseUrl + "/prebuilds/cleanup", Json(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{baseUrl}/prebuilds/{first.GetProperty("id").GetInt32()}")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.DeleteAsync($"{baseUrl}/prebuilds/999999")).StatusCode);
        Assert.Empty(JsonDocument.Parse(await developer.GetStringAsync(baseUrl + "/prebuilds")).RootElement.EnumerateArray());
    }

    /// <summary>Starts a workspace, reports the image it was started from and stops it again (the per-user cap is 2).</summary>
    private async Task<string> ImageOfNewWorkspaceAsync(string user, Repository repo)
    {
        var ws = await app.StartAsync(user, repo);
        await using var s = app.Scope(app.UserId(user));
        await s.Get<WorkspaceService>().StopAsync(ws.PublicId, app.UserId(user));
        return ws.Image;
    }

    private async Task Mutate(int id, Action<WorkspacePrebuild> change)
    {
        await using var db = app.Db();
        var p = await db.WorkspacePrebuilds.FirstAsync(x => x.Id == id);
        change(p);
        await db.SaveChangesAsync();
    }
}
