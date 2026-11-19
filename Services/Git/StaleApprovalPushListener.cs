using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Git;

/// <summary>
/// After a push: for each open PR whose source branch moved and whose target branch has a rule with
/// "dismiss approvals when new commits are pushed", removes approvals given on an older head and notes it in the PR.
/// </summary>
public class StaleApprovalPushListener(IDbContextFactory<AppDbContext> dbFactory, RepoStorage storage,
    BranchProtectionService protection, LiveEvents live) : IGitPushListener
{
    public async Task OnPushAsync(int repositoryId, int userId, CancellationToken ct)
    {
        var rules = await protection.RulesForAsync(repositoryId);
        if (!rules.Any(r => r.DismissStaleApprovals)) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var repo = await db.Repositories.Include(r => r.Project).FirstOrDefaultAsync(r => r.Id == repositoryId, ct);
        if (repo == null) return;
        var open = await db.PullRequests.Include(p => p.Reviews)
            .Where(p => p.RepositoryId == repositoryId && p.State == PullRequestState.Open).ToListAsync(ct);
        var path = storage.PathFor(repo.Project.Key, repo.Slug);

        foreach (var pr in open)
        {
            var p = BranchProtectionService.ApplicableTo(rules, pr.TargetBranch, isAdmin: false);
            if (!p.DismissStaleApprovals) continue;
            var head = await Task.Run(() => GitOps.ResolveSha(path, pr.SourceRef), ct);
            if (head == null) continue;
            var stale = pr.Reviews.Where(r => r.State == ReviewState.Approved && r.CommitSha != null && r.CommitSha != head).ToList();
            if (stale.Count == 0) continue;

            db.PullRequestReviews.RemoveRange(stale);
            var now = DateTime.UtcNow;
            db.PullRequestActivities.Add(new PullRequestActivity
            {
                PullRequestId = pr.Id, ActorId = userId, CreatedAt = now,
                Text = $"pushed new commits; {stale.Count} approval{(stale.Count == 1 ? " was" : "s were")} dismissed (`{pr.TargetBranch}` requires approvals of the latest changes)",
            });
            pr.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            live.Publish(new PullRequestChanged(pr.Id, repo.ProjectId));
        }
    }
}
