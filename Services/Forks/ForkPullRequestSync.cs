using ByteBench.Data;
using ByteBench.Services.Git;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Forks;

/// <summary>
/// Keeps fork PRs' <c>refs/pull/{n}/head</c> in their target repositories in step with the forks' branches (scoped).
/// Called after a push to a fork (<see cref="ForkPushListener"/>) and after "Sync fork". For each PR whose head moved it
/// notes the push in the PR, dismisses stale approvals, re-queues the AI review, raises <c>pull_request.updated</c>
/// (action <c>synchronize</c>) and publishes <see cref="PullRequestChanged"/>, which starts the PR's pipeline (held for
/// approval, see <see cref="Ci.CiTriggerService.OnPullRequestChangedAsync"/>).
/// </summary>
public class ForkPullRequestSync(IDbContextFactory<AppDbContext> dbFactory, PullRequestService prs, LiveEvents live,
    Webhooks.WebhookNotifier webhooks, Ai.AiReviewQueue aiQueue, IServiceProvider services, ILogger<ForkPullRequestSync> log)
{
    /// <summary>Updates every open PR whose source branch lives in <paramref name="sourceRepositoryId"/>. Returns the PRs that moved.</summary>
    public async Task<List<int>> OnSourcePushedAsync(int sourceRepositoryId, int userId, CancellationToken ct = default)
    {
        List<PullRequest> open;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            open = await db.PullRequests.AsNoTracking().Include(p => p.Repository).ThenInclude(r => r.Project)
                .Where(p => p.SourceRepositoryId == sourceRepositoryId && p.IsCrossFork && p.State == PullRequestState.Open)
                .ToListAsync(ct);
        var moved = new List<int>();
        foreach (var pr in open)
        {
            try
            {
                var (head, changed) = await prs.RefreshForkHeadAsync(pr);
                if (!changed || head == null) continue;
                moved.Add(pr.Id);
                await using (var db = await dbFactory.CreateDbContextAsync(ct))
                {
                    var now = DateTime.UtcNow;
                    db.PullRequestActivities.Add(new PullRequestActivity
                    {
                        PullRequestId = pr.Id, ActorId = userId, CreatedAt = now,
                        Text = $"pushed to the fork's `{pr.SourceBranch}` (now `{head[..7]}`)",
                    });
                    await db.PullRequests.Where(p => p.Id == pr.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.UpdatedAt, now), ct);
                    await db.SaveChangesAsync(ct);
                }
                // Stale approvals of the target's protected branches, exactly as for a push to the target itself.
                await ActivatorUtilities.CreateInstance<StaleApprovalPushListener>(services).OnPushAsync(pr.RepositoryId, userId, ct);
                aiQueue.EnqueueDebounced(pr.Id);
                await webhooks.PullRequestUpdatedAsync(pr.Id, userId, "synchronize");
                live.Publish(new PullRequestChanged(pr.Id, pr.Repository.ProjectId));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Could not update the head of fork pull request {Id}", pr.Id);
            }
        }
        return moved;
    }
}

/// <summary>A push to a repository moves the heads of the fork PRs whose source branches live in it.</summary>
public class ForkPushListener(ForkPullRequestSync sync) : IGitPushListener
{
    public Task OnPushAsync(int repositoryId, int userId, CancellationToken ct) => sync.OnSourcePushedAsync(repositoryId, userId, ct);
}
