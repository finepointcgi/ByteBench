using GitBench.Data;
using GitBench.Services;
using GitBench.Services.Ci;
using GitBench.Services.Git;
using GitBench.Services.MergeQueue;
using GitBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GitBench.Tests;

/// <summary>
/// CI on, but its runner removed: pipelines stay Queued until a test finishes them (<see cref="MergeQueueHarness.FinishPipelineAsync"/>),
/// the fake runner. The merge queue's background processor is off (tests run passes themselves) unless <see cref="Processor"/>;
/// with <see cref="SharedRoot"/>, the database and repositories live there (to restart "the same server").
/// </summary>
public class MergeQueueApp : TestApp
{
    protected virtual string? SharedRoot => null;
    protected virtual bool Processor => false;

    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        var (sharedRoot, processor) = (SharedRoot, Processor);
        settings["Ci:Enabled"] = "true";
        settings["Ci:SchedulesEnabled"] = "false";
        settings["MergeQueue:Enabled"] = processor ? "true" : "false";
        settings["MergeQueue:PollSeconds"] = "1";
        if (sharedRoot == null) return;
        settings["ConnectionStrings:Default"] = $"Data Source={Path.Combine(sharedRoot, "gitbench.db")}";
        settings["Git:RepositoryRoot"] = Path.Combine(sharedRoot, "repos");
        settings["Attachments:Root"] = Path.Combine(sharedRoot, "attachments");
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        foreach (var d in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(CiRunner)).ToList())
            services.Remove(d);
    }
}

/// <summary>A <see cref="MergeQueueApp"/> on a given data folder, optionally with the background processor running.</summary>
public sealed class MergeQueueRestartApp(string root, bool processor) : MergeQueueApp
{
    protected override string? SharedRoot => root;
    protected override bool Processor => processor;
}

/// <summary>Setup and fake-runner helpers shared by the merge queue tests.</summary>
public sealed class MergeQueueHarness(TestApp app)
{
    public const string CiYaml = """
        jobs:
          test: { steps: [ { run: "true" } ] }
        """;

    public Repository Repo { get; private set; } = null!;
    public string RepoPath => app.Services.GetRequiredService<RepoStorage>().PathFor(app.ProjectKey(Repo.ProjectId), Repo.Slug);

    /// <summary>A project (alice Owner, carol Maintainer, bob Developer), a repo with a pipeline file and a queue on main.</summary>
    public async Task SetupAsync(int maxBatch = 1, bool requireCi = true, MergeStrategy? strategy = null, int requiredApprovals = 0)
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("carol", ProjectRole.Maintainer), ("bob", ProjectRole.Developer));
        Repo = await app.CreateRepoAsync(project.Id, "alice");
        app.Commit(Repo, "main", CiFile.FileName, CiYaml);
        await using var s = app.Scope(app.UserId("alice"));
        await s.Get<BranchProtectionService>().SaveAsync(Repo.Id, new BranchProtectionRule
        {
            Pattern = "main", MergeQueueEnabled = true, MergeQueueMaxBatch = maxBatch, MergeQueueRequireCi = requireCi,
            MergeQueueStrategy = strategy, RequiredApprovals = requiredApprovals,
        }, app.UserId("alice"));
    }

    /// <summary>A branch with one new file, and bob's PR from it into main.</summary>
    public async Task<PullRequest> PullRequestAsync(string name)
    {
        app.Commit(Repo, name, name + ".txt", "content of " + name, from: "main", message: "Add " + name);
        await using var s = app.Scope(app.UserId("bob"));
        return await s.Get<PullRequestService>().CreateAsync(Repo, "Add " + name, null, name, "main", app.UserId("bob"));
    }

    public async Task<MergeQueueEntry> EnqueueAsync(PullRequest pr, string as_ = "carol")
    {
        await using var s = app.Scope(app.UserId(as_));
        return await s.Get<MergeQueueService>().EnqueueAsync(pr.Id, app.UserId(as_));
    }

    public async Task ProcessAsync()
    {
        await using var s = app.Scope(system: true);
        await s.Get<MergeQueueEngine>().ProcessAllAsync();
    }

    public MergeQueueEntry Entry(PullRequest pr)
    {
        using var db = app.Db();
        return db.MergeQueueEntries.AsNoTracking().Include(e => e.Pipeline).Where(e => e.PullRequestId == pr.Id).OrderByDescending(e => e.Id).First();
    }

    public PullRequest Reload(PullRequest pr)
    {
        using var db = app.Db();
        return db.PullRequests.AsNoTracking().First(p => p.Id == pr.Id);
    }

    public PipelineStatus PipelineStatusOf(int id)
    {
        using var db = app.Db();
        return db.Pipelines.Where(p => p.Id == id).Select(p => p.Status).First();
    }

    public string MainTip => GitOps.ResolveSha(RepoPath, "refs/heads/main")!;

    /// <summary>The fake runner: finishes a pipeline with <paramref name="status"/> and announces it like the real runner does.</summary>
    public async Task FinishPipelineAsync(int pipelineId, PipelineStatus status, string? error = null)
    {
        await using (var db = app.Db())
        {
            var now = DateTime.UtcNow;
            await db.Pipelines.Where(p => p.Id == pipelineId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, status)
                .SetProperty(p => p.FinishedAt, now).SetProperty(p => p.Error, error));
            await db.PipelineJobs.Where(j => j.PipelineId == pipelineId).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, status));
        }
        app.Services.GetRequiredService<LiveEvents>().Publish(new PipelineChanged(pipelineId, Repo.Id));
    }
}

public class MergeQueueTests(MergeQueueApp app) : IClassFixture<MergeQueueApp>
{
    private MergeQueueHarness H() => new(app);

    [Fact]
    public async Task Entries_are_tested_and_merged_in_order()
    {
        var h = H();
        await h.SetupAsync(maxBatch: 1);
        var pr1 = await h.PullRequestAsync("one");
        var pr2 = await h.PullRequestAsync("two");
        await h.EnqueueAsync(pr1);
        await h.EnqueueAsync(pr2);
        var tip = h.MainTip;

        await h.ProcessAsync();
        var e1 = h.Entry(pr1);
        var e2 = h.Entry(pr2);
        Assert.Equal(MergeQueueEntryState.Testing, e1.State);
        Assert.Equal(tip, e1.BaseSha);
        Assert.Equal(PipelineTrigger.MergeQueue, e1.Pipeline!.Trigger);
        Assert.Equal(GitOps.MergeQueueRef("main", e1.Id), e1.Pipeline.Ref);
        Assert.Equal(e1.SpeculativeSha, GitOps.ResolveSha(h.RepoPath, GitOps.MergeQueueRef("main", e1.Id)));
        Assert.Equal(MergeQueueEntryState.Queued, e2.State); // train length 1: waits its turn
        Assert.Equal(tip, h.MainTip); // nothing merged yet

        await h.FinishPipelineAsync(e1.PipelineId!.Value, PipelineStatus.Passed);
        await h.ProcessAsync();
        e1 = h.Entry(pr1);
        Assert.Equal(MergeQueueEntryState.Merged, e1.State);
        Assert.Equal(e1.SpeculativeSha, h.MainTip); // fast-forwarded to the tested commit
        var merged1 = h.Reload(pr1);
        Assert.Equal(PullRequestState.Merged, merged1.State);
        Assert.Equal(app.UserId("carol"), merged1.MergedById); // merged as the enqueuer
        Assert.Equal(e1.SpeculativeSha, merged1.MergeCommitSha);
        Assert.Null(GitOps.ResolveSha(h.RepoPath, GitOps.MergeQueueRef("main", e1.Id))); // ref cleaned up

        e2 = h.Entry(pr2);
        Assert.Equal(MergeQueueEntryState.Testing, e2.State);
        Assert.Equal(e1.SpeculativeSha, e2.BaseSha);
        await h.FinishPipelineAsync(e2.PipelineId!.Value, PipelineStatus.Passed);
        await h.ProcessAsync();
        Assert.Equal(PullRequestState.Merged, h.Reload(pr2).State);
        Assert.Equal(h.Entry(pr2).SpeculativeSha, h.MainTip);
        Assert.NotNull(GitOps.GetBlob(h.RepoPath, "main", "one.txt"));
        Assert.NotNull(GitOps.GetBlob(h.RepoPath, "main", "two.txt"));

        await using var db = app.Db();
        Assert.True(await db.AuditEvents.AnyAsync(a => a.Action == "merge_queue.merged" && a.TargetId == pr1.Id.ToString() && a.ActorId == app.UserId("carol")));
        Assert.True(await db.PullRequestActivities.AnyAsync(a => a.PullRequestId == pr1.Id && a.Text.Contains("through the merge queue")));
    }

    [Fact]
    public async Task A_green_entry_merges_the_whole_batch_ahead_of_it()
    {
        var h = H();
        await h.SetupAsync(maxBatch: 3, strategy: MergeStrategy.Squash);
        var prs = new[] { await h.PullRequestAsync("b1"), await h.PullRequestAsync("b2"), await h.PullRequestAsync("b3") };
        foreach (var pr in prs) await h.EnqueueAsync(pr);

        await h.ProcessAsync();
        var entries = prs.Select(h.Entry).ToList();
        Assert.All(entries, e => Assert.Equal(MergeQueueEntryState.Testing, e.State));
        Assert.Equal(h.MainTip, entries[0].BaseSha);
        Assert.Equal(entries[0].SpeculativeSha, entries[1].BaseSha); // target + all PRs ahead + this PR
        Assert.Equal(entries[1].SpeculativeSha, entries[2].BaseSha);
        Assert.All(entries, e => Assert.Equal(MergeStrategy.Squash, e.Strategy));

        await h.FinishPipelineAsync(entries[2].PipelineId!.Value, PipelineStatus.Passed);
        await h.ProcessAsync();
        Assert.All(prs, pr => Assert.Equal(PullRequestState.Merged, h.Reload(pr).State));
        Assert.Equal(entries[2].SpeculativeSha, h.MainTip);
        // The pipelines of the entries merged along with it are no longer needed.
        Assert.Equal(PipelineStatus.Canceled, h.PipelineStatusOf(entries[0].PipelineId!.Value));
        // Squash: one commit per PR, linear.
        var log = GitOps.GetCommit(h.RepoPath, h.MainTip)!;
        Assert.Single(log.ParentShas);
    }

    [Fact]
    public async Task A_failing_entry_is_ejected_and_the_ones_behind_it_are_rebuilt()
    {
        var h = H();
        await h.SetupAsync(maxBatch: 3);
        var pr1 = await h.PullRequestAsync("f1");
        var pr2 = await h.PullRequestAsync("f2");
        var pr3 = await h.PullRequestAsync("f3");
        foreach (var pr in new[] { pr1, pr2, pr3 }) await h.EnqueueAsync(pr);
        await h.ProcessAsync();
        var (e1, e2, e3) = (h.Entry(pr1), h.Entry(pr2), h.Entry(pr3));

        await h.FinishPipelineAsync(e2.PipelineId!.Value, PipelineStatus.Failed);
        await h.ProcessAsync();

        var failed = h.Entry(pr2);
        Assert.Equal(MergeQueueEntryState.Failed, failed.State);
        Assert.Contains($"Pipeline #{e2.PipelineId} failed", failed.Message);
        Assert.Equal(PullRequestState.Open, h.Reload(pr2).State);
        await using (var db = app.Db())
            Assert.True(await db.Notifications.AnyAsync(n => n.UserId == app.UserId("bob") && n.Kind == NotificationKind.MergeQueueRemoved && n.Url.EndsWith("/" + pr2.Number)));

        var kept = h.Entry(pr1);
        Assert.Equal(e1.PipelineId, kept.PipelineId); // ahead of the failure: untouched
        var rebuilt = h.Entry(pr3);
        Assert.Equal(MergeQueueEntryState.Testing, rebuilt.State);
        Assert.Equal(e1.SpeculativeSha, rebuilt.BaseSha); // now built without pr2
        Assert.NotEqual(e3.PipelineId, rebuilt.PipelineId);
        Assert.Equal(PipelineStatus.Canceled, h.PipelineStatusOf(e3.PipelineId!.Value));
        Assert.Equal(2, rebuilt.Builds);

        await h.FinishPipelineAsync(rebuilt.PipelineId!.Value, PipelineStatus.Passed);
        await h.ProcessAsync();
        Assert.Equal(PullRequestState.Merged, h.Reload(pr1).State);
        Assert.Equal(PullRequestState.Merged, h.Reload(pr3).State);
        Assert.Null(GitOps.GetBlob(h.RepoPath, "main", "f2.txt"));
    }

    [Fact]
    public async Task Pushing_to_a_queued_pull_request_drops_it()
    {
        var h = H();
        await h.SetupAsync(maxBatch: 2);
        var pr1 = await h.PullRequestAsync("u1");
        var pr2 = await h.PullRequestAsync("u2");
        await h.EnqueueAsync(pr1);
        await h.EnqueueAsync(pr2);
        await h.ProcessAsync();
        var oldPipeline = h.Entry(pr1).PipelineId!.Value;

        app.Commit(h.Repo, "u1", "u1.txt", "changed", from: null, message: "More work");
        await h.ProcessAsync();

        var dropped = h.Entry(pr1);
        Assert.Equal(MergeQueueEntryState.Removed, dropped.State);
        Assert.Contains("New commits were pushed", dropped.Message);
        Assert.Equal(PipelineStatus.Canceled, h.PipelineStatusOf(oldPipeline));
        Assert.Equal(h.MainTip, h.Entry(pr2).BaseSha); // pr2 moved to the front
        await using var db = app.Db();
        Assert.True(await db.Notifications.AnyAsync(n => n.UserId == app.UserId("bob") && n.Kind == NotificationKind.MergeQueueRemoved));
    }

    [Fact]
    public async Task A_direct_push_to_the_target_rebuilds_the_queue()
    {
        var h = H();
        await h.SetupAsync(maxBatch: 2);
        var pr = await h.PullRequestAsync("d1");
        await h.EnqueueAsync(pr);
        await h.ProcessAsync();
        var before = h.Entry(pr);

        app.Commit(h.Repo, "main", "hotfix.txt", "urgent", message: "Hotfix");
        await h.ProcessAsync();

        var after = h.Entry(pr);
        Assert.Equal(MergeQueueEntryState.Testing, after.State);
        Assert.Equal(h.MainTip, after.BaseSha);
        Assert.NotEqual(before.PipelineId, after.PipelineId);
        Assert.Equal(PipelineStatus.Canceled, h.PipelineStatusOf(before.PipelineId!.Value));
    }

    [Fact]
    public async Task Canceled_and_timed_out_pipelines_eject_their_entry()
    {
        var h = H();
        await h.SetupAsync(maxBatch: 2);
        var pr1 = await h.PullRequestAsync("c1");
        var pr2 = await h.PullRequestAsync("c2");
        await h.EnqueueAsync(pr1);
        await h.EnqueueAsync(pr2);
        await h.ProcessAsync();

        await h.FinishPipelineAsync(h.Entry(pr1).PipelineId!.Value, PipelineStatus.Canceled);
        await h.ProcessAsync();
        Assert.Equal(MergeQueueEntryState.Failed, h.Entry(pr1).State);
        Assert.Contains("canceled", h.Entry(pr1).Message);

        await using (var db = app.Db()) // the pipeline has been running for longer than the timeout (60 min)
            await db.MergeQueueEntries.Where(e => e.PullRequestId == pr2.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.TestingSince, DateTime.UtcNow.AddHours(-2)));
        var pid = h.Entry(pr2).PipelineId!.Value;
        await h.ProcessAsync();
        Assert.Equal(MergeQueueEntryState.Failed, h.Entry(pr2).State);
        Assert.Contains("didn't finish within 60 minutes", h.Entry(pr2).Message);
        Assert.Equal(PipelineStatus.Canceled, h.PipelineStatusOf(pid));
    }

    [Fact]
    public async Task Permissions_dequeue_and_jump_to_front()
    {
        var h = H();
        await h.SetupAsync(maxBatch: 2);
        var pr1 = await h.PullRequestAsync("p1");
        var pr2 = await h.PullRequestAsync("p2");
        var pr3 = await h.PullRequestAsync("p3");

        // Developers can't add to the queue, and the merge button is replaced by the queue.
        await Assert.ThrowsAsync<ForbiddenException>(() => h.EnqueueAsync(pr1, "bob"));
        await using (var s = app.Scope(app.UserId("carol")))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<PullRequestService>().MergeAsync(pr1.Id, app.User("carol"), new MergeRequest()));
            Assert.Contains("merge queue", ex.Message);
        }
        await h.EnqueueAsync(pr1);
        await h.EnqueueAsync(pr2);
        await h.EnqueueAsync(pr3);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.EnqueueAsync(pr1)); // already queued
        await h.ProcessAsync();

        // Jump to the front: Maintainers only.
        await using (var s = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<MergeQueueService>().MoveToFrontAsync(pr3.Id, app.UserId("bob")));
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<MergeQueueService>().MoveToFrontAsync(pr3.Id, app.UserId("carol"));
        await h.ProcessAsync();
        Assert.Equal(h.MainTip, h.Entry(pr3).BaseSha);
        Assert.Equal(h.Entry(pr3).SpeculativeSha, h.Entry(pr1).BaseSha);
        Assert.Equal(MergeQueueEntryState.Queued, h.Entry(pr2).State); // pushed out of the train (length 2)

        // The author can take their own PR out of the queue.
        await using (var s = app.Scope(app.UserId("bob")))
        {
            var status = await s.Get<MergeQueueService>().StatusAsync(pr1.Id, app.UserId("bob"));
            Assert.Equal(2, status.Position);
            await s.Get<MergeQueueService>().DequeueAsync(pr1.Id, app.UserId("bob"));
        }
        Assert.Equal(MergeQueueEntryState.Removed, h.Entry(pr1).State);
        await h.ProcessAsync();
        Assert.Equal(h.Entry(pr3).SpeculativeSha, h.Entry(pr2).BaseSha);
    }

    [Fact]
    public async Task Other_merge_checks_must_pass_before_queueing()
    {
        var h = H();
        await h.SetupAsync(requiredApprovals: 1);
        var pr = await h.PullRequestAsync("a1");
        var ex = await Assert.ThrowsAsync<BranchProtectedException>(() => h.EnqueueAsync(pr));
        Assert.Contains("approval", ex.Message);
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<PullRequestService>().SetReviewAsync(pr.Id, app.UserId("carol"), ReviewState.Approved);
        await h.EnqueueAsync(pr);
    }

    [Fact]
    public async Task Without_required_CI_entries_merge_once_built()
    {
        var h = H();
        await h.SetupAsync(maxBatch: 2, requireCi: false, strategy: MergeStrategy.Rebase);
        var pr1 = await h.PullRequestAsync("n1");
        var pr2 = await h.PullRequestAsync("n2");
        await h.EnqueueAsync(pr1);
        await h.EnqueueAsync(pr2);
        await h.ProcessAsync();
        Assert.Equal(PullRequestState.Merged, h.Reload(pr1).State);
        Assert.Equal(PullRequestState.Merged, h.Reload(pr2).State);
        Assert.Null(h.Entry(pr2).PipelineId);
        Assert.Equal(MergeStrategy.Rebase, h.Reload(pr2).MergeStrategy);
    }

    [Fact]
    public async Task The_api_adds_lists_and_removes_entries()
    {
        var h = H();
        await h.SetupAsync(maxBatch: 2);
        var pr = await h.PullRequestAsync("api1");
        var key = app.ProjectKey(h.Repo.ProjectId);
        var url = $"/api/v1/projects/{key}/repos/{h.Repo.Slug}/pull-requests/{pr.Number}/merge-queue";

        using var bob = await app.ApiClientAsync("bob");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await bob.PostAsync(url, null)).StatusCode);
        using var carol = await app.ApiClientAsync("carol");
        var created = await carol.PostAsync(url, null);
        Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);
        Assert.Contains("\"state\":\"queued\"", await created.Content.ReadAsStringAsync());
        await h.ProcessAsync();

        var queue = await carol.GetStringAsync($"/api/v1/projects/{key}/repos/{h.Repo.Slug}/merge-queue?branch=main");
        Assert.Contains("\"state\":\"testing\"", queue);
        Assert.Contains("\"position\":1", queue);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await bob.DeleteAsync(url)).StatusCode); // the author may remove it
        Assert.Equal(MergeQueueEntryState.Removed, h.Entry(pr).State);
    }

    [Fact]
    public async Task Conflicts_with_the_entries_ahead_eject_the_entry()
    {
        var h = H();
        await h.SetupAsync(maxBatch: 2);
        app.Commit(h.Repo, "x1", "same.txt", "one", from: "main");
        app.Commit(h.Repo, "x2", "same.txt", "two", from: "main");
        PullRequest pr1, pr2;
        await using (var s = app.Scope(app.UserId("bob")))
        {
            pr1 = await s.Get<PullRequestService>().CreateAsync(h.Repo, "x1", null, "x1", "main", app.UserId("bob"));
            pr2 = await s.Get<PullRequestService>().CreateAsync(h.Repo, "x2", null, "x2", "main", app.UserId("bob"));
        }
        await h.EnqueueAsync(pr1);
        await h.EnqueueAsync(pr2); // each merges cleanly on its own
        await h.ProcessAsync();
        Assert.Equal(MergeQueueEntryState.Testing, h.Entry(pr1).State);
        Assert.Equal(MergeQueueEntryState.Failed, h.Entry(pr2).State);
        Assert.Contains("same.txt", h.Entry(pr2).Message);
    }
}

/// <summary>Queue state survives a restart: the new server's processor picks up the persisted queue and finishes the job.</summary>
public class MergeQueueRestartTests
{
    [Fact]
    public async Task The_queue_resumes_after_a_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), "gitbench-tests", "mq-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            int prId, entryId, oldPipeline;
            using (var first = new MergeQueueRestartApp(root, processor: false))
            {
                var h = new MergeQueueHarness(first);
                await h.SetupAsync(maxBatch: 2);
                var pr = await h.PullRequestAsync("r1");
                var entry = await h.EnqueueAsync(pr);
                await h.ProcessAsync();
                (prId, entryId, oldPipeline) = (pr.Id, entry.Id, h.Entry(pr).PipelineId!.Value);
                // The server stops while the pipeline runs; on startup CI marks such pipelines as interrupted.
                await using var db = first.Db();
                await db.Pipelines.Where(p => p.Id == oldPipeline).ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, PipelineStatus.Error).SetProperty(p => p.Error, "Interrupted by a server restart. Re-run the pipeline."));
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            using var second = new MergeQueueRestartApp(root, processor: true);
            _ = second.Services; // start the host (and the processor)
            MergeQueueEntry Load()
            {
                using var db = second.Db();
                return db.MergeQueueEntries.AsNoTracking().First(e => e.Id == entryId);
            }
            var rebuilt = await WaitForAsync(Load, e => e.PipelineId != null && e.PipelineId != oldPipeline);
            Assert.Equal(MergeQueueEntryState.Testing, rebuilt.State); // retried, not ejected

            await using (var db = second.Db())
                await db.Pipelines.Where(p => p.Id == rebuilt.PipelineId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PipelineStatus.Passed));
            second.Services.GetRequiredService<LiveEvents>().Publish(new PipelineChanged(rebuilt.PipelineId!.Value, rebuilt.RepositoryId));
            var done = await WaitForAsync(Load, e => e.State == MergeQueueEntryState.Merged);
            await using (var db = second.Db())
                Assert.Equal(PullRequestState.Merged, (await db.PullRequests.FirstAsync(p => p.Id == prId)).State);
            Assert.NotNull(done.FinishedAt);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { TestApp.DeleteDirectory(root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<T> WaitForAsync<T>(Func<T> load, Func<T, bool> until)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var value = load();
            if (until(value)) return value;
            Assert.True(DateTime.UtcNow < deadline, "The merge queue didn't get there in time.");
            await Task.Delay(200);
        }
    }
}
