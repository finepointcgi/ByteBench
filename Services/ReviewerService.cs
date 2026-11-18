using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services;

public enum ReviewerStatus { Pending, Approved, ChangesRequested }

/// <summary>An assigned reviewer, or someone who reviewed without being asked, and where their review stands.</summary>
public sealed record ReviewerEntry(User User, ReviewerStatus Status, bool Assigned, bool Stale);

/// <summary>
/// Reviewers assigned to pull requests. Candidates are project members with <see cref="Permission.ReviewPullRequest"/>
/// (never the PR's author). The PR's author (with CreatePullRequest) or a Maintainer+ may change them. Added reviewers
/// are notified (<see cref="NotificationKind.ReviewRequested"/>) and start watching the PR.
/// </summary>
public class ReviewerService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, NotificationService notifications,
    BranchProtectionService protection)
{
    /// <summary>Project members who may review pull requests, by display name.</summary>
    public async Task<List<User>> CandidatesAsync(int projectId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var minimum = ProjectAccess.MinimumRole(Permission.ReviewPullRequest);
        return await db.ProjectMembers.Where(m => m.ProjectId == projectId && m.Role >= minimum)
            .Select(m => m.User).OrderBy(u => u.DisplayName).ToListAsync();
    }

    public async Task<List<PullRequestReviewer>> GetAsync(int pullRequestId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.PullRequestReviewers.Include(r => r.User).Where(r => r.PullRequestId == pullRequestId)
            .OrderBy(r => r.CreatedAt).ToListAsync();
    }

    /// <summary>Assigned reviewers with their status, then anyone else who left a verdict.</summary>
    public async Task<List<ReviewerEntry>> EntriesAsync(PullRequest pr, string? headSha)
    {
        var assigned = await GetAsync(pr.Id);
        var dismiss = (await protection.ForBranchAsync(pr.RepositoryId, pr.TargetBranch)).DismissStaleApprovals;
        ReviewerEntry Entry(User u, bool isAssigned)
        {
            var review = pr.Reviews.FirstOrDefault(r => r.UserId == u.Id);
            var stale = review?.State == ReviewState.Approved && dismiss && headSha != null && review.CommitSha != null && review.CommitSha != headSha;
            var status = review == null || stale ? ReviewerStatus.Pending
                : review.State == ReviewState.Approved ? ReviewerStatus.Approved : ReviewerStatus.ChangesRequested;
            return new ReviewerEntry(u, status, isAssigned, stale);
        }
        var list = assigned.Select(a => Entry(a.User, true)).ToList();
        list.AddRange(pr.Reviews.Where(r => assigned.All(a => a.UserId != r.UserId)).OrderBy(r => r.CreatedAt).Select(r => Entry(r.User, false)));
        return list;
    }

    /// <summary>Ids of the project's open PRs where <paramref name="userId"/> is an assigned reviewer who hasn't given a verdict yet.</summary>
    public async Task<HashSet<int>> AwaitingReviewAsync(int projectId, int userId, int? repositoryId = null)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var ids = await db.PullRequestReviewers
            .Where(r => r.UserId == userId)
            .Join(db.PullRequests, r => r.PullRequestId, p => p.Id, (r, p) => p)
            .Where(p => p.Repository.ProjectId == projectId && p.State == PullRequestState.Open
                        && (repositoryId == null || p.RepositoryId == repositoryId)
                        && !p.Reviews.Any(v => v.UserId == userId))
            .Select(p => p.Id).ToListAsync();
        return [.. ids];
    }

    /// <summary>Replaces the PR's reviewers with <paramref name="userIds"/>.</summary>
    public async Task SetAsync(int pullRequestId, int actorId, IEnumerable<int> userIds)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var pr = await db.PullRequests.Include(p => p.Repository).FirstOrDefaultAsync(p => p.Id == pullRequestId)
                 ?? throw new InvalidOperationException("Pull request not found.");
        await access.RequireAsync(pr.Repository.ProjectId,
            pr.AuthorId == actorId ? Permission.CreatePullRequest : Permission.MergePullRequest, actorId);
        if (pr.State != PullRequestState.Open) throw new InvalidOperationException("Reviewers can only be changed on open pull requests.");
        await ApplyAsync(db, pr, actorId, userIds.ToHashSet(), removeOthers: true);
    }

    /// <summary>
    /// On PR creation: the reviewers chosen by the author plus the default reviewers of matching branch rules, plus
    /// <paramref name="codeOwners"/> (CODEOWNERS owners of the changed files) who can review; other owners are skipped.
    /// </summary>
    internal async Task AddOnCreateAsync(int pullRequestId, int actorId, IEnumerable<int>? chosen, IEnumerable<int>? codeOwners = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var pr = await db.PullRequests.Include(p => p.Repository).FirstAsync(p => p.Id == pullRequestId);
        var wanted = (chosen ?? []).ToHashSet();
        foreach (var rule in (await protection.ForBranchAsync(pr.RepositoryId, pr.TargetBranch)).Rules)
            wanted.UnionWith(rule.DefaultReviewerIds);
        foreach (var owner in codeOwners ?? [])
            if (await db.ProjectMembers.AnyAsync(m => m.ProjectId == pr.Repository.ProjectId && m.UserId == owner)
                && await access.CanAsync(pr.Repository.ProjectId, Permission.ReviewPullRequest, owner))
                wanted.Add(owner);
        if (wanted.Count > 0) await ApplyAsync(db, pr, actorId, wanted, removeOthers: false);
    }

    private async Task ApplyAsync(AppDbContext db, PullRequest pr, int actorId, HashSet<int> wanted, bool removeOthers)
    {
        wanted.Remove(pr.AuthorId);
        var valid = new HashSet<int>();
        foreach (var uid in wanted)
        {
            if (!await db.ProjectMembers.AnyAsync(m => m.ProjectId == pr.Repository.ProjectId && m.UserId == uid)
                || !await access.CanAsync(pr.Repository.ProjectId, Permission.ReviewPullRequest, uid))
                throw new InvalidOperationException("Reviewers must be project members who can review pull requests.");
            valid.Add(uid);
        }

        var current = await db.PullRequestReviewers.Where(r => r.PullRequestId == pr.Id).ToListAsync();
        var added = valid.Where(id => current.All(c => c.UserId != id)).ToList();
        var removed = removeOthers ? current.Where(c => !valid.Contains(c.UserId)).ToList() : [];
        if (added.Count == 0 && removed.Count == 0) return;

        var touched = added.Concat(removed.Select(r => r.UserId)).ToList();
        var names = await db.Users.Where(u => touched.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => "@" + u.UserName);
        var now = DateTime.UtcNow;
        foreach (var id in added) db.PullRequestReviewers.Add(new PullRequestReviewer { PullRequestId = pr.Id, UserId = id, AddedById = actorId, CreatedAt = now });
        db.PullRequestReviewers.RemoveRange(removed);
        if (added.Count > 0)
            db.PullRequestActivities.Add(new PullRequestActivity { PullRequestId = pr.Id, ActorId = actorId, CreatedAt = now,
                Text = "requested a review from " + string.Join(", ", added.Select(id => names[id])) });
        if (removed.Count > 0)
            db.PullRequestActivities.Add(new PullRequestActivity { PullRequestId = pr.Id, ActorId = actorId, CreatedAt = now.AddMilliseconds(1),
                Text = "removed " + string.Join(", ", removed.Select(r => names[r.UserId])) + " from the reviewers" });
        pr.UpdatedAt = now;
        await db.SaveChangesAsync();
        // Drafts don't ask anyone for a review yet: PullRequestService.SetDraftAsync notifies them when it's marked ready.
        await notifications.OnReviewersAddedAsync(pr.Id, actorId, pr.IsDraft ? [] : added);
    }
}
