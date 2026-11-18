using GitBench.Data;
using GitBench.Services.Audit;
using GitBench.Services.Ci;
using GitBench.Services.Git;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.MergeQueue;

/// <summary>A PR's place in its target's merge queue.</summary>
/// <param name="Config">The queue settings of the target branch (null: no queue there).</param>
/// <param name="AppliesToActor">The queue is mandatory for the acting user (false: an admin bypassing the rule may merge directly).</param>
/// <param name="Active">The PR's active entry, if it is queued.</param>
/// <param name="Position">1-based position of <see cref="Active"/> in the queue.</param>
/// <param name="Last">The PR's most recent finished entry (why it left the queue).</param>
public sealed record MergeQueueStatus(MergeQueueConfig? Config, bool AppliesToActor, MergeQueueEntry? Active, int Position, int QueueLength, MergeQueueEntry? Last)
{
    public static readonly MergeQueueStatus None = new(null, false, null, 0, 0, null);
}

/// <summary>A branch's queue: active entries in order, then recently finished ones (newest first).</summary>
public sealed record MergeQueueView(string Branch, MergeQueueConfig? Config, IReadOnlyList<MergeQueueEntry> Active, IReadOnlyList<MergeQueueEntry> Recent);

/// <summary>
/// The merge queue's user actions (add, remove, jump to the front) and queries. Permissions go through <see cref="ProjectAccess"/>:
/// adding needs <see cref="Permission.MergePullRequest"/> plus a PR that passes every other merge check; removing is for
/// the PR's author or a Maintainer; moving to the front needs <see cref="Permission.ManageRepositories"/>. The queue itself
/// is processed by <see cref="MergeQueueEngine"/>.
/// </summary>
public class MergeQueueService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, BranchProtectionService protection,
    MergeGate gate, PullRequestService prs, RepositoryService repos, AuditLog audit, LiveEvents live, MergeQueueSignal signal,
    NotificationService notifications, CiQueue ciQueue, RepoStorage storage)
{
    public const int RecentCount = 20;

    private static readonly MergeQueueEntryState[] ActiveStates = [MergeQueueEntryState.Queued, MergeQueueEntryState.Testing, MergeQueueEntryState.Passed];

    // ---------- Queries ----------

    /// <summary>The queue settings of <paramref name="branch"/> (null: no merge queue).</summary>
    public async Task<MergeQueueConfig?> ConfigForAsync(int repositoryId, string branch) =>
        MergeQueueConfig.For(await protection.RulesForAsync(repositoryId), branch);

    /// <summary>The PR's queue status for the acting user (<see cref="MergeQueueStatus.None"/> if they can't see it).</summary>
    public async Task<MergeQueueStatus> StatusAsync(int pullRequestId, int? actorId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var pr = await db.PullRequests.AsNoTracking().Include(p => p.Repository).FirstOrDefaultAsync(p => p.Id == pullRequestId);
        if (pr == null || !await access.CanAsync(pr.Repository.ProjectId, Permission.ViewProject, actorId)) return MergeQueueStatus.None;
        var rules = await protection.RulesForAsync(pr.RepositoryId);
        var config = MergeQueueConfig.For(rules, pr.TargetBranch);
        var applies = config != null && MergeQueueConfig.For(BranchProtectionService.ApplicableTo(rules, pr.TargetBranch,
            await protection.IsBypassAdminAsync(pr.Repository.ProjectId, actorId)).Rules, pr.TargetBranch) != null;
        var entries = await db.MergeQueueEntries.AsNoTracking().Include(e => e.Pipeline).Include(e => e.EnqueuedBy)
            .Where(e => e.PullRequestId == pullRequestId).OrderByDescending(e => e.Id).Take(10).ToListAsync();
        var active = entries.FirstOrDefault(e => e.IsActive);
        var last = entries.FirstOrDefault(e => !e.IsActive);
        var queue = await ActiveOrderAsync(db, pr.RepositoryId, pr.TargetBranch);
        var position = active == null ? 0 : queue.IndexOf(active.Id) + 1;
        return new MergeQueueStatus(config, applies, active, position, queue.Count, last);
    }

    /// <summary>The branch's queue, or null if the acting user can't view the repository.</summary>
    public async Task<MergeQueueView?> ViewAsync(Repository repo, string branch)
    {
        if (!await access.CanAsync(repo.ProjectId, Permission.ViewProject)) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        IQueryable<MergeQueueEntry> Q() => db.MergeQueueEntries.AsNoTracking()
            .Include(e => e.PullRequest).ThenInclude(p => p.Author).Include(e => e.EnqueuedBy).Include(e => e.Pipeline)
            .Where(e => e.RepositoryId == repo.Id && e.TargetBranch == branch);
        var active = await Q().Where(e => ActiveStates.Contains(e.State)).OrderBy(e => e.Position).ThenBy(e => e.Id).ToListAsync();
        var recent = await Q().Where(e => !ActiveStates.Contains(e.State)).OrderByDescending(e => e.FinishedAt).ThenByDescending(e => e.Id).Take(RecentCount).ToListAsync();
        return new MergeQueueView(branch, await ConfigForAsync(repo.Id, branch), active, recent);
    }

    /// <summary>Branches of the repository that have a merge queue (existing branches matched by a rule) or still have entries.</summary>
    public async Task<List<string>> BranchesAsync(Repository repo)
    {
        if (!await access.CanAsync(repo.ProjectId, Permission.ViewProject)) return [];
        var rules = (await protection.RulesForAsync(repo.Id)).Where(r => r.MergeQueueEnabled).ToList();
        var branches = rules.Count == 0 ? [] : (await repos.Git(repo, path => GitOps.ListBranches(path, repo.DefaultBranch)))
            .Select(b => b.Name).Where(b => MergeQueueConfig.For(rules, b) != null).ToList();
        await using var db = await dbFactory.CreateDbContextAsync();
        branches.AddRange(await db.MergeQueueEntries.Where(e => e.RepositoryId == repo.Id && ActiveStates.Contains(e.State)).Select(e => e.TargetBranch).Distinct().ToListAsync());
        return branches.Distinct().OrderBy(b => b != repo.DefaultBranch).ThenBy(b => b, StringComparer.Ordinal).ToList();
    }

    private static async Task<List<int>> ActiveOrderAsync(AppDbContext db, int repositoryId, string branch) =>
        await db.MergeQueueEntries.Where(e => e.RepositoryId == repositoryId && e.TargetBranch == branch && ActiveStates.Contains(e.State))
            .OrderBy(e => e.Position).ThenBy(e => e.Id).Select(e => e.Id).ToListAsync();

    // ---------- Actions ----------

    /// <summary>
    /// Adds the PR to its target's merge queue. It must be open, not a draft, merge cleanly and pass every merge check
    /// (branch protection; an admin bypass counts), and the acting user needs <see cref="Permission.MergePullRequest"/>.
    /// </summary>
    public async Task<MergeQueueEntry> EnqueueAsync(int pullRequestId, int actorId)
    {
        await access.RequireForPullRequestAsync(pullRequestId, Permission.MergePullRequest, actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var pr = await db.PullRequests.AsNoTracking().Include(p => p.Repository).ThenInclude(r => r.Project)
            .FirstOrDefaultAsync(p => p.Id == pullRequestId) ?? throw new InvalidOperationException("Pull request not found.");
        if (pr.State != PullRequestState.Open) throw new InvalidOperationException("Only open pull requests can be added to the merge queue.");
        if (pr.IsDraft) throw new InvalidOperationException("Draft pull requests can't be added to the merge queue. Mark it ready for review first.");
        RepositorySettingsService.EnsureNotArchived(pr.Repository);
        var config = await ConfigForAsync(pr.RepositoryId, pr.TargetBranch)
                     ?? throw new InvalidOperationException($"'{pr.TargetBranch}' doesn't use a merge queue.");
        if (await db.MergeQueueEntries.AnyAsync(e => e.PullRequestId == pullRequestId && ActiveStates.Contains(e.State)))
            throw new InvalidOperationException("This pull request is already in the merge queue.");

        var status = await repos.Git(pr.Repository, path => GitOps.CheckMerge(path, pr.SourceRef, pr.TargetBranch));
        if (status.Check != MergeCheck.Clean)
            throw new InvalidOperationException(status.Check == MergeCheck.Conflicts
                ? "This pull request has conflicts with its target. Resolve them before adding it to the merge queue."
                : status.Message ?? "This pull request can't be merged.");
        var result = await gate.EvaluateAsync(pullRequestId, actorId);
        if (result.Blocked && !result.CanBypass) throw new BranchProtectedException(result.BlockedMessage.Replace("can't be merged yet", "can't be added to the merge queue yet"));

        var head = await repos.Git(pr.Repository, path => GitOps.ResolveSha(path, pr.SourceRef))
                   ?? throw new InvalidOperationException("The source branch no longer exists.");
        var last = await db.MergeQueueEntries.Where(e => e.RepositoryId == pr.RepositoryId && e.TargetBranch == pr.TargetBranch && ActiveStates.Contains(e.State))
            .MaxAsync(e => (long?)e.Position) ?? 0;
        var entry = new MergeQueueEntry
        {
            RepositoryId = pr.RepositoryId, PullRequestId = pr.Id, TargetBranch = pr.TargetBranch, EnqueuedById = actorId,
            Position = last + 1, Strategy = config.StrategyFor(pr.Repository), HeadSha = head,
        };
        db.MergeQueueEntries.Add(entry);
        await db.SaveChangesAsync();

        await prs.LogQueueActivityAsync(pr.Id, actorId, $"added this pull request to the merge queue of `{pr.TargetBranch}`"
            + (result.Blocked ? " (bypassing branch protection)" : ""));
        await audit.RecordAsync(result.Blocked ? "merge_queue.enqueued_bypassing_protection" : "merge_queue.enqueued", "pull_request", pr.Id, pr.Repository.ProjectId,
            new { repository = pr.Repository.Slug, pr.Number, pr.TargetBranch, head, strategy = entry.Strategy.ToString().ToLowerInvariant() }, actorId);
        Changed(pr.RepositoryId, pr.TargetBranch);
        return entry;
    }

    /// <summary>Takes the PR out of the queue (its author, or anyone who may merge). Entries behind it are rebuilt.</summary>
    public async Task DequeueAsync(int pullRequestId, int actorId)
    {
        // Serialized with queue processing, so a pass in flight can't overwrite the removal.
        await signal.ProcessLock.WaitAsync();
        try { await DequeueCoreAsync(pullRequestId, actorId); }
        finally { signal.ProcessLock.Release(); }
        signal.Wake();
    }

    private async Task DequeueCoreAsync(int pullRequestId, int actorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var entry = await db.MergeQueueEntries.Include(e => e.PullRequest).ThenInclude(p => p.Repository)
            .FirstOrDefaultAsync(e => e.PullRequestId == pullRequestId && ActiveStates.Contains(e.State))
            ?? throw new InvalidOperationException("This pull request isn't in the merge queue.");
        var pr = entry.PullRequest;
        var isAuthor = pr.AuthorId == actorId && (pr.IsCrossFork || await access.CanAsync(pr.Repository.ProjectId, Permission.CreatePullRequest, actorId));
        if (!isAuthor) await access.RequireAsync(pr.Repository.ProjectId, Permission.MergePullRequest, actorId);

        if (entry.PipelineId is { } pid) await MergeQueuePipelines.CancelAsync(dbFactory, ciQueue, live, pid);
        var path = storage.PathFor(await db.Projects.Where(p => p.Id == pr.Repository.ProjectId).Select(p => p.Key).FirstAsync(), pr.Repository.Slug);
        await Task.Run(() => GitOps.DeleteRef(path, GitOps.MergeQueueRef(entry.TargetBranch, entry.Id)));
        var name = await db.Users.Where(u => u.Id == actorId).Select(u => u.DisplayName).FirstOrDefaultAsync();
        entry.State = MergeQueueEntryState.Removed;
        entry.Message = $"Removed by {name}.";
        entry.RemovedById = actorId;
        entry.FinishedAt = entry.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await prs.LogQueueActivityAsync(pr.Id, actorId, "removed this pull request from the merge queue");
        await audit.RecordAsync("merge_queue.removed", "pull_request", pr.Id, pr.Repository.ProjectId,
            new { repository = pr.Repository.Slug, pr.Number, entry.TargetBranch, reason = "removed by hand" }, actorId);
        await notifications.OnMergeQueueRemovedAsync(pr.Id, actorId, entry.Message);
        Changed(pr.RepositoryId, entry.TargetBranch);
    }

    /// <summary>Moves the PR to the front of its queue (Maintainers). Everything is rebuilt behind it.</summary>
    public async Task MoveToFrontAsync(int pullRequestId, int actorId)
    {
        await signal.ProcessLock.WaitAsync();
        try { await MoveToFrontCoreAsync(pullRequestId, actorId); }
        finally { signal.ProcessLock.Release(); }
        signal.Wake();
    }

    private async Task MoveToFrontCoreAsync(int pullRequestId, int actorId)
    {
        await access.RequireForPullRequestAsync(pullRequestId, Permission.ManageRepositories, actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var entry = await db.MergeQueueEntries.Include(e => e.PullRequest).ThenInclude(p => p.Repository)
            .FirstOrDefaultAsync(e => e.PullRequestId == pullRequestId && ActiveStates.Contains(e.State))
            ?? throw new InvalidOperationException("This pull request isn't in the merge queue.");
        var first = await db.MergeQueueEntries.Where(e => e.RepositoryId == entry.RepositoryId && e.TargetBranch == entry.TargetBranch && ActiveStates.Contains(e.State))
            .OrderBy(e => e.Position).ThenBy(e => e.Id).FirstAsync();
        if (first.Id == entry.Id) return;
        entry.Position = first.Position - 1;
        entry.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var pr = entry.PullRequest;
        await prs.LogQueueActivityAsync(pr.Id, actorId, $"moved this pull request to the front of the merge queue of `{entry.TargetBranch}`");
        await audit.RecordAsync("merge_queue.moved_to_front", "pull_request", pr.Id, pr.Repository.ProjectId,
            new { repository = pr.Repository.Slug, pr.Number, entry.TargetBranch }, actorId);
        Changed(pr.RepositoryId, entry.TargetBranch);
    }

    private void Changed(int repositoryId, string branch)
    {
        live.Publish(new MergeQueueChanged(repositoryId, branch));
        signal.Wake();
    }
}
