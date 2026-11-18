using GitBench.Data;
using GitBench.Services.Git;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services;

/// <summary>
/// Pull requests from forks (<see cref="PullRequest.IsCrossFork"/>). The fork's branch is fetched into the target
/// repository at <see cref="PullRequest.SourceRef"/>, so the rest of the service (diffs, threads, merge checks, merges)
/// works on the target repository alone. Creating one needs CreatePullRequest in the fork's project and ViewProject in
/// the target's (the point of a fork: contributors without write access). Its author may then comment on, edit,
/// decline and reopen it with ViewProject alone. Everything else (reviews, merging) needs the usual target roles.
/// </summary>
public partial class PullRequestService
{
    /// <summary>
    /// Opens a pull request from <paramref name="sourceBranch"/> of <paramref name="source"/> into
    /// <paramref name="targetBranch"/> of <paramref name="target"/>. Callers check the fork network and permissions
    /// (<see cref="Forks.ForkService.CreatePullRequestAsync"/>). Both repositories need Project loaded.
    /// </summary>
    internal async Task<PullRequest> CreateCrossForkAsync(Repository target, Repository source, string sourceBranch, string targetBranch,
        string title, string? description, int authorId, CompareResult cmp, bool draft, IEnumerable<int>? reviewerIds)
    {
        title = title.Trim();
        if (title.Length is 0 or > 200) throw new InvalidOperationException("Title is required (max 200 characters).");

        PullRequest pr;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            if (await db.PullRequests.AnyAsync(p => p.RepositoryId == target.Id && p.State == PullRequestState.Open && p.IsCrossFork
                                                    && p.SourceRepositoryId == source.Id && p.SourceBranch == sourceBranch && p.TargetBranch == targetBranch))
                throw new InvalidOperationException($"There is already an open pull request from '{source.Project.Key}/{source.Slug}:{sourceBranch}' into '{targetBranch}'.");
            var number = (await db.PullRequests.Where(p => p.RepositoryId == target.Id).MaxAsync(p => (int?)p.Number) ?? 0) + 1;
            pr = new PullRequest
            {
                RepositoryId = target.Id, Number = number, Title = title,
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                SourceBranch = sourceBranch, TargetBranch = targetBranch, AuthorId = authorId, IsDraft = draft,
                SourceRepositoryId = source.Id, IsCrossFork = true,
            };
            pr.Activities.Add(new PullRequestActivity
            {
                ActorId = authorId,
                Text = (draft ? "opened this pull request as a draft" : "opened this pull request") + $" from the fork `{source.Project.Key}/{source.Slug}`",
            });
            db.PullRequests.Add(pr);
            await db.SaveChangesAsync();

            // The PR's head lives in the target repository from now on; nothing is kept if this fails.
            var head = await Task.Run(() => GitOps.FetchBranchInto(repos.PathOf(target), repos.PathOf(source), sourceBranch, pr.SourceRef));
            if (head == null) throw new InvalidOperationException($"Branch '{sourceBranch}' does not exist in {source.Project.Key}/{source.Slug}.");
            await tx.CommitAsync();
        }

        await notifications.OnPullRequestCreatedAsync(pr.Id, authorId);
        await reviewers.AddOnCreateAsync(pr.Id, authorId, reviewerIds, await CodeOwnerReviewersAsync(target, targetBranch, cmp.Files, authorId));
        await webhooks.PullRequestOpenedAsync(pr.Id, authorId);
        aiReviewQueue.Enqueue(pr.Id);
        return pr;
    }

    /// <summary>
    /// Fetches a fork PR's source branch into its <c>refs/pull/{n}/head</c> again. Returns the head (null if the fork or its
    /// branch is gone: the ref then keeps the last commits) and whether it moved.
    /// </summary>
    public async Task<(string? Head, bool Moved)> RefreshForkHeadAsync(PullRequest pr)
    {
        if (!pr.IsCrossFork || pr.SourceRepositoryId is not { } sid) return (null, false);
        await using var db = await dbFactory.CreateDbContextAsync();
        var source = await db.Repositories.AsNoTracking().Include(r => r.Project).FirstOrDefaultAsync(r => r.Id == sid);
        var target = pr.Repository?.Project != null ? pr.Repository
            : await db.Repositories.AsNoTracking().Include(r => r.Project).FirstAsync(r => r.Id == pr.RepositoryId);
        if (source == null) return (null, false);
        var (from, into) = (repos.PathOf(source), repos.PathOf(target));
        return await Task.Run(() =>
        {
            if (!GitOps.Exists(from)) return ((string?)null, false);
            var before = GitOps.ResolveSha(into, pr.SourceRef);
            var after = GitOps.FetchBranchInto(into, from, pr.SourceBranch, pr.SourceRef);
            return (after, after != null && after != before);
        });
    }

    /// <summary>A fork PR's author may comment on, edit, decline and reopen it with ViewProject in the target project.</summary>
    private async Task<bool> IsForkAuthorAsync(int prId, int actorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var info = await db.PullRequests.Where(p => p.Id == prId).Select(p => new { p.IsCrossFork, p.AuthorId, p.Repository.ProjectId }).FirstOrDefaultAsync();
        return info is { IsCrossFork: true } && info.AuthorId == actorId && await access.CanAsync(info.ProjectId, Permission.ViewProject, actorId);
    }

    /// <summary>
    /// Whether the user may comment on this PR: Comment in its project, or they wrote this fork PR and can view the project.
    /// </summary>
    public async Task<bool> CanCommentAsync(PullRequest pr, int userId) =>
        await access.CanAsync(pr.Repository.ProjectId, Permission.Comment, userId)
        || (pr.IsCrossFork && pr.AuthorId == userId && await access.CanAsync(pr.Repository.ProjectId, Permission.ViewProject, userId));

    private async Task RequireCommentOnPrAsync(int prId, int actorId)
    {
        if (await IsForkAuthorAsync(prId, actorId)) return;
        await access.RequireForPullRequestAsync(prId, Permission.Comment, actorId);
    }

    /// <summary>The fork a cross-fork PR comes from (with Project), or null (same-repository PR, or the fork was deleted).</summary>
    private async Task<Repository?> SourceRepositoryOfAsync(PullRequest pr)
    {
        if (!pr.IsCrossFork || pr.SourceRepositoryId is not { } sid) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Repositories.AsNoTracking().Include(r => r.Project).FirstOrDefaultAsync(r => r.Id == sid);
    }
}
