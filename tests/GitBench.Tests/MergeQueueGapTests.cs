using GitBench.Data;
using GitBench.Services;
using GitBench.Services.Ci;
using GitBench.Services.Git;
using GitBench.Services.MergeQueue;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GitBench.Tests;

/// <summary>
/// Merge queue follow-ups: protected CI variables for queue pipelines (and who may get them), pipelines waiting on a manual
/// job ("waiting for approval", own timeout), and deleting the source branch after a queue merge.
/// </summary>
public class MergeQueueGapTests(MergeQueueApp app) : IClassFixture<MergeQueueApp>
{
    private MergeQueueHarness H() => new(app);

    private async Task SetManualAsync(MergeQueueHarness h, int pipelineId)
    {
        await using var db = app.Db();
        await db.Pipelines.Where(p => p.Id == pipelineId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PipelineStatus.Manual));
        await db.PipelineJobs.Where(j => j.PipelineId == pipelineId).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, PipelineStatus.Manual));
        app.Services.GetRequiredService<LiveEvents>().Publish(new PipelineChanged(pipelineId, h.Repo.Id));
    }

    // ---------------------------------------------------------------- protected variables

    private async Task<string> ResolveBranchAsync(int pipelineId)
    {
        await using var db = app.Db();
        var p = await db.Pipelines.AsNoTracking().FirstAsync(x => x.Id == pipelineId);
        await using var s = app.Scope(system: true);
        return await s.Get<CiVariableService>().VariableBranchAsync(p);
    }

    [Fact]
    public async Task A_queue_pipeline_gets_the_target_branchs_protected_variables_only_when_the_enqueuer_could_merge()
    {
        var h = H();
        await h.SetupAsync(maxBatch: 1);
        await using (var s = app.Scope(app.UserId("alice")))
            await s.Get<CiVariableService>().SaveAsync(h.Repo.ProjectId, null, null, "DEPLOY_KEY", "s3cret", isProtected: true);

        var pr = await h.PullRequestAsync("vars");
        await h.EnqueueAsync(pr, "carol");
        await h.ProcessAsync();
        var entry = h.Entry(pr);
        Assert.Equal(MergeQueueEntryState.Testing, entry.State);
        var pipelineId = entry.PipelineId!.Value;

        // The queue ref isn't a protected branch, but its pipeline counts as the target ("main") for variables.
        await using (var db = app.Db())
            Assert.StartsWith("refs/merge-queue/main/", (await db.Pipelines.AsNoTracking().FirstAsync(p => p.Id == pipelineId)).Ref);
        Assert.Equal("main", await ResolveBranchAsync(pipelineId));
        await using (var s = app.Scope(system: true))
        {
            var vars = s.Get<CiVariableService>();
            Assert.Equal("s3cret", (await vars.ResolveAsync(h.Repo.ProjectId, h.Repo.Id, await ResolveBranchAsync(pipelineId)))
                .Values.GetValueOrDefault("DEPLOY_KEY"));
            // A normal pipeline on the unprotected source branch still gets none of the protected ones.
            Assert.DoesNotContain("DEPLOY_KEY", (await vars.ResolveAsync(h.Repo.ProjectId, h.Repo.Id, "vars")).Values.Keys);
        }

        // The enqueuer lost the right to merge (demoted to Viewer): no protected variables any more.
        await using (var db = app.Db())
            await db.ProjectMembers.Where(m => m.ProjectId == h.Repo.ProjectId && m.UserId == app.UserId("carol"))
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, ProjectRole.Viewer));
        Assert.StartsWith("refs/merge-queue/main/", await ResolveBranchAsync(pipelineId));
    }

    [Fact]
    public async Task A_queue_pipeline_of_a_fork_pull_request_or_a_finished_entry_never_gets_protected_variables()
    {
        var h = H();
        await h.SetupAsync(maxBatch: 1);
        var pr = await h.PullRequestAsync("forkish");
        await h.EnqueueAsync(pr, "carol");
        await h.ProcessAsync();
        var pipelineId = h.Entry(pr).PipelineId!.Value;
        Assert.Equal("main", await ResolveBranchAsync(pipelineId));

        // The pipeline is flagged as a fork PR pipeline (as one for a cross-fork PR would be): its own ref, always.
        await using (var db = app.Db())
            await db.Pipelines.Where(p => p.Id == pipelineId).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsForkPullRequest, true));
        Assert.StartsWith("refs/merge-queue/", await ResolveBranchAsync(pipelineId));
        await using (var db = app.Db())
            await db.Pipelines.Where(p => p.Id == pipelineId).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsForkPullRequest, false));
        Assert.Equal("main", await ResolveBranchAsync(pipelineId));

        // The PR is from a fork (flag on the PR): no.
        await using (var db = app.Db())
            await db.PullRequests.Where(p => p.Id == pr.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsCrossFork, true));
        Assert.StartsWith("refs/merge-queue/", await ResolveBranchAsync(pipelineId));
        await using (var db = app.Db())
            await db.PullRequests.Where(p => p.Id == pr.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsCrossFork, false));

        // Once the entry left the queue, a stale pipeline gets nothing special.
        await using (var db = app.Db())
            await db.MergeQueueEntries.Where(e => e.PullRequestId == pr.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.State, MergeQueueEntryState.Removed));
        Assert.StartsWith("refs/merge-queue/", await ResolveBranchAsync(pipelineId));

        // An ordinary pipeline on a push is untouched.
        await using (var db = app.Db())
        {
            var other = new Pipeline { RepositoryId = h.Repo.Id, Ref = "feature", CommitSha = new string('a', 40), Trigger = PipelineTrigger.Push };
            Assert.Equal("feature", await new Func<Task<string>>(async () =>
            {
                await using var s = app.Scope(system: true);
                return await s.Get<CiVariableService>().VariableBranchAsync(other);
            })());
        }
    }

    // ---------------------------------------------------------------- waiting for approval

    [Fact]
    public async Task A_pipeline_waiting_on_a_manual_job_waits_for_approval_and_is_not_ejected_by_the_pipeline_timeout()
    {
        var h = H();
        await h.SetupAsync(maxBatch: 1);
        var pr = await h.PullRequestAsync("approval");
        await h.EnqueueAsync(pr);
        await h.ProcessAsync();
        var pipelineId = h.Entry(pr).PipelineId!.Value;

        await SetManualAsync(h, pipelineId);
        // It has been "testing" for far longer than the 60 minute pipeline timeout.
        await using (var db = app.Db())
            await db.MergeQueueEntries.Where(e => e.PullRequestId == pr.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.TestingSince, DateTime.UtcNow.AddHours(-5)));
        await h.ProcessAsync();

        var waiting = h.Entry(pr);
        Assert.Equal(MergeQueueEntryState.Testing, waiting.State);
        Assert.True(waiting.AwaitingApproval);
        Assert.NotNull(waiting.AwaitingApprovalSince);
        Assert.Contains("Waiting for approval", waiting.Message);
        Assert.Contains("1440 minutes", waiting.Message); // the default approval timeout is shown

        // Approved: the job runs, the wait doesn't count against the pipeline timeout, and the entry passes and merges.
        await using (var db = app.Db())
        {
            await db.Pipelines.Where(p => p.Id == pipelineId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PipelineStatus.Running));
            await db.PipelineJobs.Where(j => j.PipelineId == pipelineId).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, PipelineStatus.Running));
        }
        await h.ProcessAsync();
        var running = h.Entry(pr);
        Assert.Equal(MergeQueueEntryState.Testing, running.State);
        Assert.False(running.AwaitingApproval);
        Assert.Null(running.Message);
        Assert.True(DateTime.UtcNow - running.TestingSince < TimeSpan.FromMinutes(5));

        await h.FinishPipelineAsync(pipelineId, PipelineStatus.Passed);
        await h.ProcessAsync();
        Assert.Equal(MergeQueueEntryState.Merged, h.Entry(pr).State);
        Assert.Equal(PullRequestState.Merged, h.Reload(pr).State);
    }

    [Fact]
    public async Task An_unapproved_pipeline_is_ejected_after_the_configurable_approval_timeout()
    {
        var h = H();
        await h.SetupAsync(maxBatch: 1);
        // A 30 minute approval timeout on the rule.
        await using (var s = app.Scope(app.UserId("alice")))
        {
            var rule = (await s.Get<BranchProtectionService>().ListAsync(h.Repo.Id)).Single();
            rule.MergeQueueApprovalTimeoutMinutes = 30;
            await s.Get<BranchProtectionService>().SaveAsync(h.Repo.Id, rule, app.UserId("alice"));
            rule.MergeQueueApprovalTimeoutMinutes = 0;
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<BranchProtectionService>().SaveAsync(h.Repo.Id, rule, app.UserId("alice")));
        }
        var pr = await h.PullRequestAsync("slow");
        await h.EnqueueAsync(pr);
        await h.ProcessAsync();
        var pipelineId = h.Entry(pr).PipelineId!.Value;
        await SetManualAsync(h, pipelineId);
        await h.ProcessAsync();
        Assert.True(h.Entry(pr).AwaitingApproval);
        Assert.Contains("30 minutes", h.Entry(pr).Message);

        await using (var db = app.Db())
            await db.MergeQueueEntries.Where(e => e.PullRequestId == pr.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.AwaitingApprovalSince, DateTime.UtcNow.AddMinutes(-31)));
        await h.ProcessAsync();
        var gone = h.Entry(pr);
        Assert.Equal(MergeQueueEntryState.Failed, gone.State);
        Assert.Contains("wasn't approved within 30 minutes", gone.Message);
    }

    // ---------------------------------------------------------------- delete source branch

    [Fact]
    public async Task The_source_branch_is_deleted_after_a_queue_merge_when_asked()
    {
        var h = H();
        await h.SetupAsync(maxBatch: 1, requireCi: false);
        var keep = await h.PullRequestAsync("keep-me");
        var drop = await h.PullRequestAsync("drop-me");

        await using (var s = app.Scope(app.UserId("carol")))
        {
            await s.Get<MergeQueueService>().EnqueueAsync(keep.Id, app.UserId("carol"));
            var entry = await s.Get<MergeQueueService>().EnqueueAsync(drop.Id, app.UserId("carol"), deleteSourceBranch: true);
            Assert.True(entry.DeleteSourceBranch);
        }
        // Developers may not delete branches: asking for it is refused up front.
        var third = await h.PullRequestAsync("dev-asks");
        await using (var s = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => s.Get<MergeQueueService>().EnqueueAsync(third.Id, app.UserId("bob"), deleteSourceBranch: true));

        await h.ProcessAsync();
        await h.ProcessAsync();
        Assert.Equal(PullRequestState.Merged, h.Reload(keep).State);
        Assert.Equal(PullRequestState.Merged, h.Reload(drop).State);
        Assert.True(GitOps.BranchExists(h.RepoPath, "keep-me"));
        Assert.False(GitOps.BranchExists(h.RepoPath, "drop-me"));
        await using var db = app.Db();
        Assert.True(await db.PullRequestActivities.AnyAsync(a => a.PullRequestId == drop.Id && a.Text.Contains("deleted the source branch")));
    }
}
