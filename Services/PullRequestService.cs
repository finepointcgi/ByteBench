using ByteBench.Data;
using ByteBench.Services.Git;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services;

public record PullRequestCounts(int Open, int Merged, int Declined);

/// <summary>Pull requests: lifecycle (open → merged/declined → reopened), comments, reviews and issue linking.</summary>
public partial class PullRequestService(IDbContextFactory<AppDbContext> dbFactory, RepositoryService repos, ProjectAccess access, NotificationService notifications, Ai.AiReviewQueue aiReviewQueue,
    MergeGate gate, ReviewerService reviewers, BranchProtectionService protection, Webhooks.WebhookNotifier webhooks,
    Audit.AuditLog audit, Webhooks.WebhookPushEvents pushEvents, CodeOwnersService codeOwners, IssueService issues)
{
    // ---------- Queries ----------

    public async Task<List<PullRequest>> ListAsync(int projectId, PullRequestState? state, int? repositoryId = null)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.PullRequests.Include(p => p.Repository).ThenInclude(r => r.Project)
            .Include(p => p.Author).Include(p => p.Reviews).ThenInclude(r => r.User)
            .Include(p => p.SourceRepository).ThenInclude(r => r!.Project)
            .Where(p => p.Repository.ProjectId == projectId);
        if (state is { } s) q = q.Where(p => p.State == s);
        if (repositoryId is { } rid) q = q.Where(p => p.RepositoryId == rid);
        var list = await q.AsSplitQuery().ToListAsync();
        return list.OrderByDescending(p => p.UpdatedAt).ToList();
    }

    public async Task<Dictionary<int, int>> CommentCountsAsync(IEnumerable<int> prIds)
    {
        var ids = prIds.ToList();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.PullRequestComments.Where(c => ids.Contains(c.PullRequestId))
            .GroupBy(c => c.PullRequestId).Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N);
    }

    public async Task<PullRequestCounts> CountAsync(int projectId, int? repositoryId = null)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return new(0, 0, 0);
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.PullRequests.Where(p => p.Repository.ProjectId == projectId);
        if (repositoryId is { } rid) q = q.Where(p => p.RepositoryId == rid);
        var g = await q.GroupBy(p => p.State).Select(x => new { x.Key, N = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        return new(g.GetValueOrDefault(PullRequestState.Open), g.GetValueOrDefault(PullRequestState.Merged), g.GetValueOrDefault(PullRequestState.Declined));
    }

    /// <summary>The pull request, or null if it doesn't exist or the acting user can't view its project.</summary>
    public async Task<PullRequest?> GetAsync(string projectKey, string slug, int number)
    {
        var visible = await access.VisibleProjectIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var key = projectKey.ToUpperInvariant();
        var s = slug.ToLowerInvariant();
        return await db.PullRequests.Where(p => visible.Contains(p.Repository.ProjectId))
            .Include(p => p.Repository).ThenInclude(r => r.Project)
            .Include(p => p.Author).Include(p => p.MergedBy)
            .Include(p => p.Comments).ThenInclude(c => c.Author)
            .Include(p => p.Reviews).ThenInclude(r => r.User)
            .Include(p => p.Activities).ThenInclude(a => a.Actor)
            .Include(p => p.SourceRepository).ThenInclude(r => r!.Project)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Repository.Project.Key == key && p.Repository.Slug == s && p.Number == number);
    }

    /// <summary>
    /// The PR's diff and commits. Open PRs compare the live branches; merged/declined PRs use the tips captured
    /// at close time so the history survives branch deletion. Null if the commits are no longer available.
    /// </summary>
    public Task<CompareResult?> GetChangesAsync(PullRequest pr) => repos.Git(pr.Repository, path =>
    {
        if (pr.State != PullRequestState.Open && pr.SourceCommitSha != null && pr.TargetCommitSha != null)
            return GitOps.Compare(path, pr.TargetCommitSha, pr.SourceCommitSha);
        return GitOps.Compare(path, pr.TargetBranch, pr.SourceRef);
    });

    public Task<MergeStatus> CheckMergeAsync(PullRequest pr) =>
        repos.Git(pr.Repository, path => GitOps.CheckMerge(path, pr.SourceRef, pr.TargetBranch));

    /// <summary>Issues of the PR's project referenced by its title, source branch or commit messages.</summary>
    public async Task<List<Issue>> GetLinkedIssuesAsync(PullRequest pr, IEnumerable<CommitInfo>? commits = null)
    {
        var key = pr.Repository.Project.Key;
        var texts = new List<string?> { pr.Title, pr.Description, pr.SourceBranch };
        if (commits != null) texts.AddRange(commits.Select(c => c.Message));
        var numbers = IssueKeys.Numbers(key, texts).ToList();
        if (numbers.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Issues.Include(i => i.Project).Include(i => i.Assignee).Include(i => i.WorkflowStatus)
            .Where(i => i.ProjectId == pr.Repository.ProjectId && numbers.Contains(i.Number))
            .OrderBy(i => i.Number).ToListAsync();
    }

    // ---------- Lifecycle ----------

    public async Task<PullRequest> CreateAsync(Repository repo, string title, string? description, string source, string target, int authorId,
        IEnumerable<int>? reviewerIds = null, bool draft = false)
    {
        await access.RequireAsync(repo.ProjectId, Permission.CreatePullRequest, authorId);
        await RepositorySettingsService.EnsureNotArchivedAsync(dbFactory, repo.Id);
        title = title.Trim();
        if (title.Length is 0 or > 200) throw new InvalidOperationException("Title is required (max 200 characters).");
        if (source == target) throw new InvalidOperationException("Source and target must be different branches.");

        var cmp = await repos.Git(repo, path =>
        {
            if (!GitOps.BranchExists(path, source)) throw new InvalidOperationException($"Branch '{source}' does not exist.");
            if (!GitOps.BranchExists(path, target)) throw new InvalidOperationException($"Branch '{target}' does not exist.");
            return GitOps.Compare(path, target, source);
        });
        if (cmp == null || cmp.Commits.Count == 0)
            throw new InvalidOperationException($"'{source}' has no commits that aren't already in '{target}'.");

        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        if (await db.PullRequests.AnyAsync(p => p.RepositoryId == repo.Id && p.State == PullRequestState.Open && !p.IsCrossFork
                                                && p.SourceBranch == source && p.TargetBranch == target))
            throw new InvalidOperationException($"There is already an open pull request from '{source}' into '{target}'.");

        var number = (await db.PullRequests.Where(p => p.RepositoryId == repo.Id).MaxAsync(p => (int?)p.Number) ?? 0) + 1;
        var pr = new PullRequest
        {
            RepositoryId = repo.Id, Number = number, Title = title,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            SourceBranch = source, TargetBranch = target, AuthorId = authorId, IsDraft = draft,
        };
        pr.Activities.Add(new PullRequestActivity { ActorId = authorId, Text = draft ? "opened this pull request as a draft" : "opened this pull request" });
        db.PullRequests.Add(pr);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        await notifications.OnPullRequestCreatedAsync(pr.Id, authorId);
        await reviewers.AddOnCreateAsync(pr.Id, authorId, reviewerIds, await CodeOwnerReviewersAsync(repo, target, cmp.Files, authorId));
        await webhooks.PullRequestOpenedAsync(pr.Id, authorId);
        aiReviewQueue.Enqueue(pr.Id);
        return pr;
    }

    public async Task UpdateAsync(int prId, int actorId, string title, string? description)
    {
        await RequireAuthorOrMergerAsync(prId, actorId);
        title = title.Trim();
        if (title.Length is 0 or > 200) throw new InvalidOperationException("Title is required (max 200 characters).");
        await using var db = await dbFactory.CreateDbContextAsync();
        var pr = await db.PullRequests.FindAsync(prId) ?? throw new InvalidOperationException("Pull request not found.");
        var oldDescription = pr.Description;
        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (pr.Title != title) Log(db, pr, actorId, $"changed the title from ~~{pr.Title}~~ to **{title}**");
        else if (pr.Description != description) Log(db, pr, actorId, "updated the description");
        pr.Title = title;
        pr.Description = description;
        await db.SaveChangesAsync();
        await notifications.OnPullRequestUpdatedAsync(prId, actorId, oldDescription);
        await webhooks.PullRequestUpdatedAsync(prId, actorId, "edited");
    }

    /// <summary>Merges the PR inside the bare repo; on success records state, activity and linked-issue activity.</summary>
    public Task<MergeOutcome> MergeAsync(int prId, User actor, bool deleteSourceBranch, bool allowFastForward) =>
        MergeAsync(prId, actor, new MergeRequest(MergeStrategy.Merge, deleteSourceBranch, allowFastForward));

    /// <summary>Merges the PR with <see cref="MergeRequest.Strategy"/> (which the repository must allow).</summary>
    public async Task<MergeOutcome> MergeAsync(int prId, User actor, MergeRequest request)
    {
        var deleteSourceBranch = request.DeleteSourceBranch;
        await access.RequireForPullRequestAsync(prId, Permission.MergePullRequest, actor.Id);
        if (deleteSourceBranch) await access.RequireForPullRequestAsync(prId, Permission.DeleteBranch, actor.Id);
        await using var db = await dbFactory.CreateDbContextAsync();
        var pr = await db.PullRequests.Include(p => p.Repository).ThenInclude(r => r.Project)
            .FirstOrDefaultAsync(p => p.Id == prId) ?? throw new InvalidOperationException("Pull request not found.");
        if (pr.State != PullRequestState.Open) throw new InvalidOperationException("Only open pull requests can be merged.");
        RepositorySettingsService.EnsureNotArchived(pr.Repository);
        if (deleteSourceBranch && pr.IsCrossFork) throw new InvalidOperationException("The source branch is in a fork, so it can't be deleted from here.");
        if (!MergeStrategySettings.Allowed(pr.Repository).HasFlag(MergeStrategySettings.Flag(request.Strategy)))
            throw new InvalidOperationException($"This repository doesn't allow {MergeStrategySettings.Label(request.Strategy).ToLowerInvariant()} merges.");
        // A merge queue on the target branch: merging goes through the queue (unless the actor bypasses the rule).
        await EnsureNoMergeQueueAsync(pr, actor.Id);
        // Branch protection: approvals, change requests and required checks (throws with the reasons when blocked).
        var bypassed = await gate.EnsureCanMergeAsync(prId, actor.Id, noBypass: request.NoBypass);
        if (request.ExpectedHeadSha != null
            && await repos.Git(pr.Repository, path => GitOps.ResolveSha(path, pr.SourceRef)) != request.ExpectedHeadSha)
            throw new InvalidOperationException("The pull request's source branch changed while it was being merged. Nothing was merged.");
        if (deleteSourceBranch && pr.SourceBranch != pr.Repository.DefaultBranch)
            await protection.EnsureAllowedAsync(pr.Repository, pr.SourceBranch, RefChangeKind.Delete, actor.Id);

        // Collect commit messages before merging: afterwards the commits are reachable from the target too.
        var before = await GetChangesAsync(pr);
        var outcome = await RunMergeAsync(pr, actor, request, before);
        if (!outcome.Success) return outcome;

        var now = DateTime.UtcNow;
        pr.State = PullRequestState.Merged;
        pr.MergedById = actor.Id;
        pr.MergeCommitSha = outcome.MergeCommitSha;
        pr.SourceCommitSha = outcome.SourceSha;
        pr.TargetCommitSha = outcome.TargetSha;
        pr.ClosedAt = now;
        pr.MergeStrategy = request.Strategy;
        Log(db, pr, actor.Id, MergedActivity(request.Strategy, outcome));
        if (bypassed) Log(db, pr, actor.Id, "bypassed branch protection on `" + pr.TargetBranch + "` to merge", now.AddMilliseconds(-1));
        if (bypassed) await audit.RecordAsync("pull_request.merged_bypassing_protection", "pull_request", pr.Id, pr.Repository.ProjectId, new { repository = pr.Repository.Slug, pr.Number, pr.TargetBranch, outcome.MergeCommitSha, strategy = request.Strategy.ToString().ToLowerInvariant() }, actor.Id);

        // The merge moved the target branch (and may delete the source branch): report them like a git push would.
        var pushed = new List<Webhooks.PushedRef> { Webhooks.WebhookPushEvents.Branch(pr.TargetBranch, outcome.TargetSha, outcome.MergeCommitSha) };
        if (deleteSourceBranch && await DeleteSourceBranchAsync(db, pr, actor.Id, outcome.SourceSha!, now) is { } deleted) pushed.Add(deleted);

        await NoteMergeOnLinkedIssuesAsync(db, pr, actor.Id, before?.Commits, now);

        await db.SaveChangesAsync();
        await CloseIssuesNamedByMergeAsync(pr, before?.Commits, actor.Id);
        await notifications.OnPullRequestMergedAsync(prId, actor.Id);
        await pushEvents.SendAsync(pr.RepositoryId, pushed, actor.Id);
        await webhooks.PullRequestMergedAsync(prId, actor.Id);
        return outcome;
    }

    /// <summary>
    /// Deletes the merged PR's source branch (never the default branch) and logs it; shared by direct and merge queue merges.
    /// The ref change to report as a push, or null when nothing was deleted (default branch, already gone).
    /// </summary>
    private async Task<Webhooks.PushedRef?> DeleteSourceBranchAsync(AppDbContext db, PullRequest pr, int actorId, string sourceSha, DateTime now)
    {
        if (pr.SourceBranch == pr.Repository.DefaultBranch) return null;
        try
        {
            await repos.Git(pr.Repository, path => GitOps.DeleteBranch(path, pr.SourceBranch, pr.Repository.DefaultBranch));
            Log(db, pr, actorId, $"deleted the source branch `{pr.SourceBranch}`", now.AddMilliseconds(1));
            return Webhooks.WebhookPushEvents.Branch(pr.SourceBranch, sourceSha, null);
        }
        catch (InvalidOperationException) { return null; /* already gone: nothing to do */ }
    }

    public async Task DeclineAsync(int prId, int actorId)
    {
        await RequireAuthorOrMergerAsync(prId, actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var pr = await db.PullRequests.Include(p => p.Repository).ThenInclude(r => r.Project)
            .FirstOrDefaultAsync(p => p.Id == prId) ?? throw new InvalidOperationException("Pull request not found.");
        if (pr.State != PullRequestState.Open) throw new InvalidOperationException("Only open pull requests can be declined.");
        // Snapshot the tips so the diff stays viewable even if a branch is deleted later.
        (pr.SourceCommitSha, pr.TargetCommitSha) = await repos.Git(pr.Repository, path =>
            (GitOps.ResolveSha(path, pr.SourceRef), GitOps.ResolveSha(path, pr.TargetBranch)));
        pr.State = PullRequestState.Declined;
        pr.ClosedAt = DateTime.UtcNow;
        Log(db, pr, actorId, "declined this pull request");
        await db.SaveChangesAsync();
        await notifications.OnPullRequestDeclinedAsync(prId, actorId);
        await webhooks.PullRequestDeclinedAsync(prId, actorId);
    }

    public async Task ReopenAsync(int prId, int actorId)
    {
        await RequireAuthorOrMergerAsync(prId, actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var pr = await db.PullRequests.Include(p => p.Repository).ThenInclude(r => r.Project)
            .FirstOrDefaultAsync(p => p.Id == prId) ?? throw new InvalidOperationException("Pull request not found.");
        if (pr.State != PullRequestState.Declined) throw new InvalidOperationException("Only declined pull requests can be reopened.");
        // A fork PR's head is fetched from the fork again (refused if the fork or its branch is gone).
        if (pr.IsCrossFork && (await RefreshForkHeadAsync(pr)).Head == null)
            throw new InvalidOperationException("The fork or its source branch no longer exists.");
        var ok = await repos.Git(pr.Repository, path =>
            GitOps.BranchExists(path, pr.SourceRef) && GitOps.BranchExists(path, pr.TargetBranch));
        if (!ok) throw new InvalidOperationException("The source or target branch no longer exists.");
        if (await db.PullRequests.AnyAsync(p => p.Id != prId && p.RepositoryId == pr.RepositoryId && p.State == PullRequestState.Open
                                                && p.IsCrossFork == pr.IsCrossFork && p.SourceRepositoryId == pr.SourceRepositoryId
                                                && p.SourceBranch == pr.SourceBranch && p.TargetBranch == pr.TargetBranch))
            throw new InvalidOperationException("Another open pull request already covers these branches.");
        pr.State = PullRequestState.Open;
        pr.ClosedAt = null;
        pr.SourceCommitSha = pr.TargetCommitSha = null;
        Log(db, pr, actorId, "reopened this pull request");
        await db.SaveChangesAsync();
        await notifications.OnPullRequestReopenedAsync(prId, actorId);
        await webhooks.PullRequestUpdatedAsync(prId, actorId, "reopened");
    }

    // ---------- Comments & reviews ----------

    /// <param name="parentId">Reply to this comment's thread (position and anchors are copied from the thread's first comment).</param>
    /// <param name="startLine">For a multi-line inline comment: its first new-side line (<paramref name="line"/> is the last).</param>
    public async Task<PullRequestComment> AddCommentAsync(int prId, int authorId, string body, string? filePath = null, int? line = null, int? oldLine = null,
        int? parentId = null, int? startLine = null)
    {
        await RequireCommentOnPrAsync(prId, authorId);
        if (string.IsNullOrWhiteSpace(body)) throw new InvalidOperationException("Comment can't be empty.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var c = new PullRequestComment
        {
            PullRequestId = prId, AuthorId = authorId, Body = body.Trim(),
            FilePath = filePath, Line = filePath == null ? null : line, OldLine = filePath == null ? null : oldLine,
        };
        await AnchorAsync(db, c, parentId, startLine);
        db.PullRequestComments.Add(c);
        await db.PullRequests.Where(p => p.Id == prId).ExecuteUpdateAsync(s => s.SetProperty(p => p.UpdatedAt, DateTime.UtcNow));
        await db.SaveChangesAsync();
        await notifications.OnPullRequestCommentedAsync(prId, c.Id, authorId, c.Body);
        await webhooks.PullRequestCommentedAsync(prId, c.Id, authorId);
        return c;
    }

    public async Task EditCommentAsync(int commentId, int authorId, string body)
    {
        await RequireCommentAsync(commentId, authorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.PullRequestComments.Where(c => c.Id == commentId && c.AuthorId == authorId).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.Body, body.Trim()).SetProperty(c => c.EditedAt, DateTime.UtcNow));
        await notifications.OnPullRequestCommentChangedAsync(await db.PullRequestComments.Where(c => c.Id == commentId).Select(c => (int?)c.PullRequestId).FirstOrDefaultAsync());
    }

    public async Task DeleteCommentAsync(int commentId, int authorId)
    {
        await RequireCommentAsync(commentId, authorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var prId = await db.PullRequestComments.Where(c => c.Id == commentId && c.AuthorId == authorId).Select(c => (int?)c.PullRequestId).FirstOrDefaultAsync();
        await db.PullRequestComments.Where(c => c.Id == commentId && c.AuthorId == authorId).ExecuteDeleteAsync();
        await notifications.OnPullRequestCommentChangedAsync(prId);
    }

    /// <summary>Sets (or with null, withdraws) the user's review verdict. Authors can't review their own PR.</summary>
    public async Task SetReviewAsync(int prId, int userId, ReviewState? state)
    {
        await access.RequireForPullRequestAsync(prId, Permission.ReviewPullRequest, userId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var pr = await db.PullRequests.Include(p => p.Reviews).Include(p => p.Repository).ThenInclude(r => r.Project)
                     .FirstOrDefaultAsync(p => p.Id == prId)
                 ?? throw new InvalidOperationException("Pull request not found.");
        if (pr.AuthorId == userId) throw new InvalidOperationException("You can't review your own pull request.");
        if (pr.State != PullRequestState.Open) throw new InvalidOperationException("Only open pull requests can be reviewed.");

        // The verdict applies to the current head; branch protection can ignore approvals of older heads.
        var head = await repos.Git(pr.Repository, path => GitOps.ResolveSha(path, pr.SourceRef));
        var existing = pr.Reviews.FirstOrDefault(r => r.UserId == userId);
        if (existing?.State == state && (state == null || existing?.CommitSha == head)) return;
        string text;
        if (state == null)
        {
            if (existing == null) return;
            db.PullRequestReviews.Remove(existing);
            text = existing.State == ReviewState.Approved ? "removed their approval" : "withdrew their request for changes";
        }
        else
        {
            if (existing == null) db.PullRequestReviews.Add(new PullRequestReview { PullRequestId = prId, UserId = userId, State = state.Value, CommitSha = head });
            else { existing.State = state.Value; existing.CreatedAt = DateTime.UtcNow; existing.CommitSha = head; }
            text = state == ReviewState.Approved ? "approved this pull request" : "requested changes";
        }
        Log(db, pr, userId, text);
        await db.SaveChangesAsync();
        await notifications.OnPullRequestReviewedAsync(prId, userId, state);
        await webhooks.PullRequestUpdatedAsync(prId, userId, state switch { ReviewState.Approved => "approved", ReviewState.ChangesRequested => "changes_requested", _ => "review_dismissed" });
    }

    // ---------- Permission helpers ----------

    /// <summary>The PR's author may edit/decline/reopen it (with CreatePullRequest); anyone else needs MergePullRequest.</summary>
    private async Task RequireAuthorOrMergerAsync(int prId, int actorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var authorId = await db.PullRequests.Where(p => p.Id == prId).Select(p => (int?)p.AuthorId).FirstOrDefaultAsync();
        if (authorId == actorId && await IsForkAuthorAsync(prId, actorId)) return;
        await access.RequireForPullRequestAsync(prId,
            authorId == actorId ? Permission.CreatePullRequest : Permission.MergePullRequest, actorId);
    }

    private async Task RequireCommentAsync(int commentId, int actorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var prId = await db.PullRequestComments.Where(c => c.Id == commentId).Select(c => (int?)c.PullRequestId).FirstOrDefaultAsync();
        await RequireCommentOnPrAsync(prId ?? 0, actorId);
    }

    private static void Log(AppDbContext db, PullRequest pr, int actorId, string text, DateTime? at = null)
    {
        var when = at ?? DateTime.UtcNow;
        db.PullRequestActivities.Add(new PullRequestActivity { PullRequestId = pr.Id, ActorId = actorId, Text = text, CreatedAt = when });
        pr.UpdatedAt = when;
    }
}
