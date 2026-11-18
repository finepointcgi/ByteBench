using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GitBench.Data;
using GitBench.Services;
using GitBench.Services.Ci;
using GitBench.Services.Forks;
using GitBench.Services.Git;
using GitBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GitBench.Tests;

/// <summary>Forks, "Sync fork" and pull requests between forks.</summary>
public class ForkTests(TestApp app) : IClassFixture<TestApp>
{
    private string PathOf(Repository repo) => app.Services.GetRequiredService<RepoStorage>().PathFor(app.ProjectKey(repo.ProjectId), repo.Slug);

    /// <summary>carol's upstream project (Internal: everyone can view it) with dan as Developer, and bob's own project to fork into.</summary>
    private async Task<(Repository Upstream, Project BobsProject)> SetupAsync(ProjectVisibility visibility = ProjectVisibility.Internal)
    {
        var upstreamProject = await app.CreateProjectAsync("carol", visibility, ("dan", ProjectRole.Developer));
        var upstream = await app.CreateRepoAsync(upstreamProject.Id, "carol");
        app.Commit(upstream, "main", "app.txt", "one\ntwo\nthree\n", message: "Add app");
        var bobs = await app.CreateProjectAsync("bob");
        return (upstream, bobs);
    }

    private async Task<Repository> ForkAsync(Repository upstream, Project into, string actor = "bob", string? slug = null)
    {
        await using var s = app.Scope(app.UserId(actor));
        var fork = await s.Get<ForkService>().ForkAsync(upstream, into.Id, slug, null, app.User(actor));
        return (await s.Get<RepositoryService>().GetRepositoryAsync(into.Key, fork.Slug))!;
    }

    private async Task<PullRequest> CrossForkPrAsync(Repository fork, string branch, Repository upstream, string actor = "bob")
    {
        await using var s = app.Scope(app.UserId(actor));
        return await s.Get<ForkService>().CreatePullRequestAsync(fork, branch, upstream, "main", "From my fork", null, app.UserId(actor));
    }

    private async Task<PullRequest> LoadAsync(Repository repo, int number)
    {
        await using var s = app.Scope(system: true);
        return (await s.Get<PullRequestService>().GetAsync(app.ProjectKey(repo.ProjectId), repo.Slug, number))!;
    }

    [Fact]
    public async Task Fork_creates_an_independent_working_repository()
    {
        var (upstream, bobs) = await SetupAsync();
        var fork = await ForkAsync(upstream, bobs);

        Assert.Equal(upstream.Id, fork.ForkedFromId);
        Assert.Equal(upstream.DefaultBranch, fork.DefaultBranch);
        var path = PathOf(fork);
        Assert.Equal(GitOps.ResolveSha(PathOf(upstream), "main"), GitOps.ResolveSha(path, "main"));
        Assert.False(File.Exists(Path.Combine(path, "objects", "info", "alternates"))); // no --shared: deleting upstream is safe
        app.Commit(fork, "feature/x", "x.txt", "x"); // writable
        Assert.True(GitOps.BranchExists(path, "feature/x"));
        Assert.False(GitOps.BranchExists(PathOf(upstream), "feature/x"));

        await using var s = app.Scope(app.UserId("bob"));
        var info = await s.Get<ForkService>().GetInfoAsync(upstream);
        Assert.Equal(1, info.ForkCount);
        Assert.Equal(upstream.Id, (await s.Get<ForkService>().GetInfoAsync(fork)).Parent?.Id);
        Assert.Single(await s.Get<ForkService>().ListForksAsync(upstream));
    }

    [Fact]
    public async Task Forking_needs_ManageRepositories_in_the_target_and_view_on_the_upstream()
    {
        var (upstream, bobs) = await SetupAsync(ProjectVisibility.Private);
        // bob can't see carol's private project.
        await using (var s = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ForkService>().ForkAsync(upstream, bobs.Id, null, null, app.User("bob")));

        // dan can see it (Developer) but is only a Developer in the project he'd fork into.
        var other = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("dan", ProjectRole.Developer));
        await using (var s = app.Scope(app.UserId("dan")))
        {
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ForkService>().ForkAsync(upstream, other.Id, null, null, app.User("dan")));
            Assert.DoesNotContain(await s.Get<ForkService>().TargetProjectsAsync(), p => p.Id == other.Id);
        }
        // Same slug twice in one project is refused; a different slug works.
        var carols = await app.CreateProjectAsync("carol");
        await ForkAsync(upstream, carols, "carol");
        await using (var s = app.Scope(app.UserId("carol")))
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<ForkService>().ForkAsync(upstream, carols.Id, null, null, app.User("carol")));
        Assert.Equal("code-2", (await ForkAsync(upstream, carols, "carol", "code-2")).Slug);
    }

    [Fact]
    public async Task Sync_fast_forwards_the_default_branch_or_reports_divergence()
    {
        var (upstream, bobs) = await SetupAsync();
        var fork = await ForkAsync(upstream, bobs);
        var newTip = app.Commit(upstream, "main", "new.txt", "new", message: "Upstream change");

        await using var s = app.Scope(app.UserId("bob"));
        var forks = s.Get<ForkService>();
        var r = await forks.SyncAsync(fork, app.UserId("bob"));
        Assert.Equal(ForkSyncState.FastForwarded, r.State);
        Assert.Equal(1, r.Behind);
        Assert.Equal(newTip, GitOps.ResolveSha(PathOf(fork), "main"));
        Assert.Equal(ForkSyncState.UpToDate, (await forks.SyncAsync(fork, app.UserId("bob"))).State);

        app.Commit(fork, "main", "mine.txt", "mine");
        Assert.Equal(ForkSyncState.Ahead, (await forks.SyncAsync(fork, app.UserId("bob"))).State);
        app.Commit(upstream, "main", "theirs.txt", "theirs");
        var before = GitOps.ResolveSha(PathOf(fork), "main");
        var diverged = await forks.SyncAsync(fork, app.UserId("bob"));
        Assert.Equal(ForkSyncState.Diverged, diverged.State);
        Assert.Equal((1, 1), (diverged.Ahead, diverged.Behind));
        Assert.Equal(before, GitOps.ResolveSha(PathOf(fork), "main"));
    }

    [Fact]
    public async Task Cross_fork_PR_diffs_follows_pushes_and_merges_into_upstream()
    {
        var (upstream, bobs) = await SetupAsync();
        var fork = await ForkAsync(upstream, bobs);
        app.Commit(fork, "feature/f", "feature.txt", "hello from the fork", message: "Add feature");

        var pr = await CrossForkPrAsync(fork, "feature/f", upstream);
        Assert.True(pr.IsCrossFork);
        Assert.Equal(fork.Id, pr.SourceRepositoryId);
        Assert.Equal("refs/pull/1/head", pr.SourceRef);
        Assert.Equal(GitOps.ResolveSha(PathOf(fork), "feature/f"), GitOps.ResolveSha(PathOf(upstream), pr.SourceRef));
        Assert.False(GitOps.BranchExists(PathOf(upstream), "feature/f")); // nothing written to upstream's branches

        // Listed with the upstream's PRs, labelled with the fork.
        await using (var s = app.Scope(app.UserId("carol")))
        {
            var listed = Assert.Single(await s.Get<PullRequestService>().ListAsync(upstream.ProjectId, PullRequestState.Open, upstream.Id));
            Assert.Equal($"{bobs.Key}/{fork.Slug}:feature/f", listed.SourceLabel);
        }

        var loaded = await LoadAsync(upstream, pr.Number);
        await using (var s = app.Scope(app.UserId("carol")))
        {
            var changes = await s.Get<PullRequestService>().GetChangesAsync(loaded);
            Assert.Equal("feature.txt", Assert.Single(changes!.Files).Path);
            Assert.Equal(MergeCheck.Clean, (await s.Get<PullRequestService>().CheckMergeAsync(loaded)).Check);
        }

        // bob is only a Viewer upstream, yet may comment on his own fork PR.
        await using (var s = app.Scope(app.UserId("bob")))
            await s.Get<PullRequestService>().AddCommentAsync(pr.Id, app.UserId("bob"), "Please take a look");

        // A push to the fork (here: a commit, then the push listeners) moves refs/pull/1/head.
        var pushed = app.Commit(fork, "feature/f", "feature2.txt", "more", from: null, message: "More");
        await using (var s = app.Scope(app.UserId("bob")))
            foreach (var l in s.Get<IEnumerable<IGitPushListener>>().OfType<ForkPushListener>())
                await l.OnPushAsync(fork.Id, app.UserId("bob"), CancellationToken.None);
        Assert.Equal(pushed, GitOps.ResolveSha(PathOf(upstream), pr.SourceRef));
        await using (var db = app.Db())
            Assert.True(await db.PullRequestActivities.AnyAsync(a => a.PullRequestId == pr.Id && a.Text.Contains("pushed to the fork")));

        // Merging needs Maintainer+ upstream (bob can't), and the merge commit lands on upstream's main.
        await using (var s = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<PullRequestService>().MergeAsync(pr.Id, app.User("bob"), false, false));
        await using (var s = app.Scope(app.UserId("carol")))
        {
            var outcome = await s.Get<PullRequestService>().MergeAsync(pr.Id, app.User("carol"), new MergeRequest(MergeStrategy.Squash));
            Assert.True(outcome.Success, outcome.Error);
        }
        Assert.NotNull(GitOps.GetBlob(PathOf(upstream), "main", "feature2.txt"));
        Assert.Equal(PullRequestState.Merged, (await LoadAsync(upstream, pr.Number)).State);
    }

    [Fact]
    public async Task Cross_fork_PR_works_when_upstream_already_has_the_commits()
    {
        // The branch came along with the fork: no objects need fetching, the PR ref must still be written.
        var (upstream, bobs) = await SetupAsync();
        var tip = app.Commit(upstream, "feature/shared", "shared.txt", "shared");
        var fork = await ForkAsync(upstream, bobs);
        var pr = await CrossForkPrAsync(fork, "feature/shared", upstream);
        Assert.Equal(tip, GitOps.ResolveSha(PathOf(upstream), pr.SourceRef));

        // Sync records every upstream branch under refs/upstream/*, even ones whose commits the fork already has.
        await using var s = app.Scope(app.UserId("bob"));
        var r = await s.Get<ForkService>().SyncAsync(fork, app.UserId("bob"));
        Assert.Equal(ForkSyncState.UpToDate, r.State);
        Assert.Equal(tip, GitOps.ResolveSha(PathOf(fork), "refs/upstream/feature/shared"));
    }

    [Fact]
    public async Task Suggestions_on_a_fork_PR_commit_to_the_fork_only_for_users_who_can_push_there()
    {
        var (upstream, bobs) = await SetupAsync();
        var fork = await ForkAsync(upstream, bobs);
        app.Commit(fork, "feature/s", "app.txt", "one\nTWO\nthree\n");
        var pr = await CrossForkPrAsync(fork, "feature/s", upstream);

        PullRequestComment c;
        await using (var s = app.Scope(app.UserId("dan")))
            c = await s.Get<PullRequestService>().AddCommentAsync(pr.Id, app.UserId("dan"), "```suggestion\ntwo\n```", "app.txt", line: 2);

        var loaded = await LoadAsync(upstream, pr.Number);
        await using (var s = app.Scope(app.UserId("carol"))) // Owner upstream, but can't push to bob's fork
        {
            Assert.False(await s.Get<PullRequestService>().CanApplySuggestionsAsync(loaded, app.UserId("carol")));
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<PullRequestService>().ApplySuggestionsAsync(pr.Id, app.User("carol"), [c.Id]));
        }
        await using (var s = app.Scope(app.UserId("bob")))
        {
            Assert.True(await s.Get<PullRequestService>().CanApplySuggestionsAsync(loaded, app.UserId("bob")));
            var sha = await s.Get<PullRequestService>().ApplySuggestionsAsync(pr.Id, app.User("bob"), [c.Id]);
            Assert.Equal(sha, GitOps.ResolveSha(PathOf(fork), "feature/s"));
            Assert.Equal(sha, GitOps.ResolveSha(PathOf(upstream), pr.SourceRef));
        }
    }

    [Fact]
    public async Task Deleting_the_fork_keeps_the_PR_and_deleting_the_upstream_keeps_the_fork()
    {
        var (upstream, bobs) = await SetupAsync();
        var fork = await ForkAsync(upstream, bobs);
        var head = app.Commit(fork, "feature/d", "d.txt", "d");
        var pr = await CrossForkPrAsync(fork, "feature/d", upstream);

        await using (var s = app.Scope(app.UserId("bob")))
            await s.Get<RepositorySettingsService>().DeleteAsync(fork.Id, fork.Name);
        var orphan = await LoadAsync(upstream, pr.Number);
        Assert.Equal(PullRequestState.Open, orphan.State);
        Assert.Null(orphan.SourceRepositoryId);
        Assert.Equal("unknown repository:feature/d", orphan.SourceLabel);
        await using (var s = app.Scope(app.UserId("carol")))
            Assert.Equal(head, (await s.Get<PullRequestService>().GetChangesAsync(orphan))!.HeadSha);

        // Upstream deleted: a second fork keeps working, unlinked.
        var fork2 = await ForkAsync(upstream, bobs, slug: "again");
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<RepositorySettingsService>().DeleteAsync(upstream.Id, upstream.Name);
        Assert.False(Directory.Exists(PathOf(upstream)));
        Assert.NotNull(GitOps.GetBlob(PathOf(fork2), "main", "app.txt"));
        app.Commit(fork2, "main", "after.txt", "still works", from: null);
        await using (var db = app.Db())
            Assert.Null((await db.Repositories.FirstAsync(r => r.Id == fork2.Id)).ForkedFromId);
    }

    [Fact]
    public async Task Fork_pipelines_get_no_CI_variables_even_on_protected_refs()
    {
        var (upstream, _) = await SetupAsync();
        await using var s = app.Scope(app.UserId("carol"));
        await s.Get<CiVariableService>().SaveAsync(upstream.ProjectId, null, null, "TOKEN", "secret", isProtected: false);
        await s.Get<CiVariableService>().SaveAsync(upstream.ProjectId, null, null, "DEPLOY", "secret", isProtected: true);
        await s.Get<BranchProtectionService>().SaveAsync(upstream.Id, new BranchProtectionRule { Pattern = "**" });

        var normal = await s.Get<CiVariableService>().ResolveAsync(upstream.ProjectId, upstream.Id, "refs/pull/1/head");
        Assert.Equal(2, normal.Values.Count); // what a ref matching "**" would get without the fork flag
        var forked = await s.Get<CiVariableService>().ResolveAsync(upstream.ProjectId, upstream.Id, "refs/pull/1/head", forkPullRequest: true);
        Assert.Empty(forked.Values);
    }

    [Fact]
    public async Task Cross_fork_PRs_need_the_same_fork_network()
    {
        var (upstream, bobs) = await SetupAsync();
        var stranger = await app.CreateRepoAsync(bobs.Id, "bob", "unrelated");
        app.Commit(stranger, "feature/u", "u.txt", "u");
        await using var s = app.Scope(app.UserId("bob"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.Get<ForkService>().CreatePullRequestAsync(stranger, "feature/u", upstream, "main", "Nope", null, app.UserId("bob")));
    }

    [Fact]
    public async Task Api_forks_lists_forks_and_opens_a_cross_fork_PR()
    {
        var (upstream, bobs) = await SetupAsync();
        var upKey = app.ProjectKey(upstream.ProjectId);
        var bob = await app.ApiClientAsync("bob");

        var created = await bob.PostAsJsonAsync($"/api/v1/projects/{upKey}/repos/{upstream.Slug}/forks", new { project = bobs.Key, slug = "my-fork" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var fork = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal($"{upKey}/{upstream.Slug}", fork.GetProperty("forkedFrom").GetString());

        var list = await bob.GetFromJsonAsync<JsonElement>($"/api/v1/projects/{upKey}/repos/{upstream.Slug}/forks");
        Assert.Equal("my-fork", Assert.Single(list.EnumerateArray()).GetProperty("repository").GetProperty("slug").GetString());

        await using (var s = app.Scope(app.UserId("bob")))
            app.Commit((await s.Get<RepositoryService>().GetRepositoryAsync(bobs.Key, "my-fork"))!, "feature/api", "api.txt", "api");
        var pr = await bob.PostAsJsonAsync($"/api/v1/projects/{upKey}/repos/{upstream.Slug}/pull-requests",
            new { title = "Via API", sourceBranch = "feature/api", targetBranch = "main", sourceRepository = $"{bobs.Key}/my-fork" });
        Assert.Equal(HttpStatusCode.Created, pr.StatusCode);
        var dto = await pr.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(dto.GetProperty("isCrossFork").GetBoolean());
        Assert.Equal($"{bobs.Key}/my-fork", dto.GetProperty("sourceRepository").GetString());

        // Forking into a project where bob isn't Maintainer+ is a 403.
        var notMine = await bob.PostAsJsonAsync($"/api/v1/projects/{upKey}/repos/{upstream.Slug}/forks", new { project = upKey, slug = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, notMine.StatusCode);
    }
}

/// <summary>Fork PR pipelines with CI turned on: held for a Maintainer's approval.</summary>
public class ForkPipelineTests(CiApp app) : IClassFixture<CiApp>
{
    [Fact]
    public async Task Fork_PR_pipeline_waits_for_a_maintainer_to_approve_and_run_it()
    {
        var upstreamProject = await app.CreateProjectAsync("carol", ProjectVisibility.Internal, ("dan", ProjectRole.Developer));
        var upstream = await app.CreateRepoAsync(upstreamProject.Id, "carol");
        var bobs = await app.CreateProjectAsync("bob");
        Repository fork;
        await using (var s = app.Scope(app.UserId("bob")))
        {
            var f = await s.Get<ForkService>().ForkAsync(upstream, bobs.Id, null, null, app.User("bob"));
            fork = (await s.Get<RepositoryService>().GetRepositoryAsync(bobs.Key, f.Slug))!;
        }
        app.Commit(fork, "feature/ci", ".gitbench-ci.yml", "jobs:\n  steal:\n    steps:\n      - run: env\n");
        PullRequest pr;
        await using (var s = app.Scope(app.UserId("bob")))
            pr = await s.Get<ForkService>().CreatePullRequestAsync(fork, "feature/ci", upstream, "main", "CI from fork", null, app.UserId("bob"));

        Pipeline? p = null;
        for (var i = 0; i < 100 && p == null; i++)
        {
            await using var db = app.Db();
            p = await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(x => x.PullRequestId == pr.Id);
            if (p == null) await Task.Delay(50);
        }
        Assert.NotNull(p);
        Assert.True(p.IsForkPullRequest);
        Assert.True(p.AwaitingApproval);
        Assert.Equal(PipelineStatus.Queued, p.Status);
        Assert.Equal("refs/pull/1/head", p.Ref);

        await using (var s = app.Scope(app.UserId("dan")))
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<PipelineService>().ApproveAsync(upstream, p.Id));
        await using (var s = app.Scope(app.UserId("carol")))
        {
            await s.Get<PipelineService>().ApproveAsync(upstream, p.Id);
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<PipelineService>().ApproveAsync(upstream, p.Id)); // once
        }
        await using (var db = app.Db())
        {
            var approved = await db.Pipelines.AsNoTracking().FirstAsync(x => x.Id == p.Id);
            Assert.False(approved.AwaitingApproval);
            Assert.Equal(app.UserId("carol"), approved.ApprovedById);
        }
    }
}
