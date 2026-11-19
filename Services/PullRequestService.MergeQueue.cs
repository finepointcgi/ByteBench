using GitBench.Data;
using GitBench.Services.Git;
using GitBench.Services.MergeQueue;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services;

/// <summary>The pieces of merging that the merge queue (Services/MergeQueue) shares with a normal merge.</summary>
public partial class PullRequestService
{
    /// <summary>Jira-style: notes the merge on each issue the PR links to (not saved yet).</summary>
    private async Task NoteMergeOnLinkedIssuesAsync(AppDbContext db, PullRequest pr, int actorId, IEnumerable<CommitInfo>? commits, DateTime now)
    {
        var linked = await GetLinkedIssuesAsync(pr, commits);
        var link = $"[{pr.Repository.Slug}#{pr.Number}](/p/{pr.Repository.Project.Key}/repos/{pr.Repository.Slug}/pull-requests/{pr.Number})";
        foreach (var issue in linked)
        {
            db.Activities.Add(new Activity { IssueId = issue.Id, ActorId = actorId, Text = $"merged pull request **{link}** into `{pr.TargetBranch}`" });
            await db.Issues.Where(i => i.Id == issue.Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.UpdatedAt, now));
        }
    }

    /// <summary>Refuses a direct merge into a branch whose merge queue applies to the actor (admins may bypass a rule that allows it).</summary>
    private async Task EnsureNoMergeQueueAsync(PullRequest pr, int actorId)
    {
        var rules = await protection.RulesForAsync(pr.RepositoryId);
        if (!rules.Any(r => r.MergeQueueEnabled)) return;
        var admin = await protection.IsBypassAdminAsync(pr.Repository.ProjectId, actorId);
        if (MergeQueueConfig.For(BranchProtectionService.ApplicableTo(rules, pr.TargetBranch, admin).Rules, pr.TargetBranch) != null)
            throw new InvalidOperationException($"'{pr.TargetBranch}' uses a merge queue: add the pull request to the merge queue instead of merging it directly.");
    }

    /// <summary>
    /// Builds the speculative commit for a queue entry: <paramref name="headSha"/> merged onto <paramref name="baseSha"/> with
    /// <paramref name="strategy"/> and the same messages and identities a direct merge by <paramref name="actor"/> would use.
    /// </summary>
    internal async Task<MergeOutcome> BuildQueuedMergeAsync(PullRequest pr, User actor, MergeStrategy strategy, string baseSha, string headSha, string refName)
    {
        var who = RepositoryService.IdentityOf(actor);
        string message;
        GitIdentity author = who;
        if (strategy == MergeStrategy.Squash)
        {
            var commits = (await repos.Git(pr.Repository, path => GitOps.Compare(path, baseSha, headSha)))?.Commits;
            await using var db = await dbFactory.CreateDbContextAsync();
            author = RepositoryService.IdentityOf(await db.Users.AsNoTracking().FirstAsync(u => u.Id == pr.AuthorId));
            message = DefaultSquashMessage(pr, commits);
            var trailers = CoAuthorTrailers(commits, author);
            trailers.Add("Merge-Strategy: squash");
            message += "\n\n" + string.Join("\n", trailers);
        }
        else
        {
            var from = pr.IsCrossFork ? await ForkSourceLabelAsync(pr) : pr.SourceBranch;
            message = $"Merge pull request #{pr.Number} from {from} into {pr.TargetBranch}\n\n{pr.Title}\n\nMerge-Strategy: merge";
        }
        return await repos.Git(pr.Repository, path => GitOps.BuildSpeculativeMerge(path, baseSha, headSha, strategy, message, author, who, refName));
    }

    /// <summary>
    /// Records a merge the queue made (the target already points at or past <paramref name="mergeSha"/>): the PR is closed
    /// as merged by <paramref name="actorId"/> (who queued it), with activity, linked-issue notes, notifications and the
    /// <c>pull_request.merged</c> webhook, exactly like a direct merge. False if the PR wasn't open any more.
    /// </summary>
    internal async Task<bool> CompleteQueuedMergeAsync(int prId, int actorId, MergeStrategy strategy, string mergeSha, string headSha, string baseSha,
        bool deleteSourceBranch = false)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var pr = await db.PullRequests.Include(p => p.Repository).ThenInclude(r => r.Project).FirstOrDefaultAsync(p => p.Id == prId);
        if (pr == null || pr.State != PullRequestState.Open) return false;
        var commits = (await repos.Git(pr.Repository, path => GitOps.Compare(path, baseSha, headSha)))?.Commits;

        var now = DateTime.UtcNow;
        pr.State = PullRequestState.Merged;
        pr.MergedById = actorId;
        pr.MergeCommitSha = mergeSha;
        pr.SourceCommitSha = headSha;
        pr.TargetCommitSha = baseSha;
        pr.ClosedAt = now;
        pr.MergeStrategy = strategy;
        Log(db, pr, actorId, MergedActivity(strategy, new MergeOutcome(true, null, mergeSha, false, headSha, baseSha, [])) + " through the merge queue");
        await NoteMergeOnLinkedIssuesAsync(db, pr, actorId, commits, now);

        // "Delete the source branch" chosen when it was queued: the same behaviour as a direct merge, re-checked now (the
        // enqueuer's permission or the branch rules may have changed while it waited). Never for a fork's branch.
        Webhooks.PushedRef? deleted = null;
        if (deleteSourceBranch && !pr.IsCrossFork && pr.SourceBranch != pr.Repository.DefaultBranch)
        {
            try
            {
                await access.RequireForPullRequestAsync(prId, Permission.DeleteBranch, actorId);
                await protection.EnsureAllowedAsync(pr.Repository, pr.SourceBranch, RefChangeKind.Delete, actorId);
                deleted = await DeleteSourceBranchAsync(db, pr, actorId, headSha, now);
            }
            catch (InvalidOperationException ex) // includes ForbiddenException and BranchProtectedException
            {
                Log(db, pr, actorId, $"couldn't delete the source branch `{pr.SourceBranch}`: {ex.Message}", now.AddMilliseconds(1));
            }
        }
        await db.SaveChangesAsync();
        await notifications.OnPullRequestMergedAsync(prId, actorId);
        if (deleted != null) await pushEvents.SendAsync(pr.RepositoryId, [deleted], actorId);
        await webhooks.PullRequestMergedAsync(prId, actorId);
        return true;
    }

    /// <summary>Adds a line to the PR's activity (merge queue events) and refreshes open pages.</summary>
    internal async Task LogQueueActivityAsync(int prId, int actorId, string text)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var pr = await db.PullRequests.FirstOrDefaultAsync(p => p.Id == prId);
        if (pr == null) return;
        Log(db, pr, actorId, text);
        await db.SaveChangesAsync();
        await notifications.OnPullRequestCommentChangedAsync(prId); // live refresh only
    }
}
