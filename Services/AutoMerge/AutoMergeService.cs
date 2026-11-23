using ByteBench.Data;
using ByteBench.Services.Audit;
using ByteBench.Services.Git;
using ByteBench.Services.MergeQueue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.AutoMerge;

/// <summary>Configuration section <c>AutoMerge</c>.</summary>
public class AutoMergeOptions
{
    /// <summary>Run the background worker (tests turn it off and call <see cref="AutoMergeService.ProcessAllAsync"/> themselves).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>How often armed pull requests are re-checked without a triggering event (the safety sweep).</summary>
    public int SweepSeconds { get; set; } = 30;
    /// <summary>A merge attempt that claimed a request and then vanished (crash) can be taken over after this many minutes.</summary>
    public int LeaseMinutes { get; set; } = 10;
}

/// <summary>Branch protection rules of a repository were saved or deleted (wakes auto-merge: a requirement may have been relaxed).</summary>
public sealed record BranchProtectionChanged(int RepositoryId) : LiveEvent;

/// <summary>What one evaluation of an armed pull request did.</summary>
public enum AutoMergeOutcome
{
    /// <summary>Nothing armed (or another worker is merging it right now).</summary>
    Idle,
    /// <summary>A requirement is not satisfied yet; it stays armed.</summary>
    Waiting,
    /// <summary>Cancelled (see the reason on the row).</summary>
    Cancelled,
    /// <summary>Merged.</summary>
    Merged,
    /// <summary>Added to the target's merge queue instead of merging directly.</summary>
    Queued,
}

/// <summary>
/// "Merge when ready": arm a pull request and ByteBench merges it as soon as every requirement is met. The decision uses the SAME
/// <see cref="MergeGate"/> and merge code (<see cref="PullRequestService.MergeAsync(int, User, MergeRequest)"/>) as a manual merge,
/// never with an admin bypass, and the person who armed it is the merger. If the target uses a merge queue the pull request is
/// queued instead. One attempt per pull request at a time: a worker claims the request with a conditional update first.
/// <para>
/// It cancels itself (timeline entry, audit event and a notification to the person who armed it) when someone else pushes new
/// commits, the person loses merge permission or is deactivated, the pull request is closed or retargeted, or a blocking check
/// fails. Evaluated by <see cref="AutoMergeProcessor"/> on events and every <see cref="AutoMergeOptions.SweepSeconds"/>.
/// </para>
/// </summary>
public class AutoMergeService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, PullRequestService prs, MergeGate gate,
    MergeQueueService queues, BranchProtectionService protection, RepositoryService repos, AuditLog audit, NotificationService notifications,
    LiveEvents live, AutoMergeSignal signal, IOptions<AutoMergeOptions> options, ILogger<AutoMergeService> log)
{
    private TimeSpan Lease => TimeSpan.FromMinutes(Math.Max(1, options.Value.LeaseMinutes));

    // ---------- Queries ----------

    /// <summary>The pull request's auto-merge row (any state), or null if it was never armed.</summary>
    public async Task<PullRequestAutoMerge?> GetAsync(int pullRequestId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.PullRequestAutoMerges.AsNoTracking().Include(a => a.ArmedBy).FirstOrDefaultAsync(a => a.PullRequestId == pullRequestId);
    }

    /// <summary>Ids of the given pull requests that are armed right now (for list chips).</summary>
    public async Task<HashSet<int>> ArmedAmongAsync(IEnumerable<int> pullRequestIds)
    {
        var ids = pullRequestIds.ToList();
        await using var db = await dbFactory.CreateDbContextAsync();
        return (await db.PullRequestAutoMerges.Where(a => ids.Contains(a.PullRequestId) && (a.State == AutoMergeState.Armed || a.State == AutoMergeState.Merging))
            .Select(a => a.PullRequestId).ToListAsync()).ToHashSet();
    }

    // ---------- Arm / cancel ----------

    /// <summary>
    /// Arms (or re-arms with new settings) "merge when ready" for an open pull request. Needs <see cref="Permission.MergePullRequest"/>
    /// (and DeleteBranch when the source branch should be deleted). Armed pull requests are checked right away.
    /// </summary>
    public async Task<PullRequestAutoMerge> ArmAsync(int pullRequestId, int actorId, MergeRequest request, CancellationToken ct = default)
    {
        await access.RequireForPullRequestAsync(pullRequestId, Permission.MergePullRequest, actorId);
        if (request.DeleteSourceBranch) await access.RequireForPullRequestAsync(pullRequestId, Permission.DeleteBranch, actorId);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pr = await db.PullRequests.Include(p => p.Repository).ThenInclude(r => r.Project).FirstOrDefaultAsync(p => p.Id == pullRequestId, ct)
                 ?? throw new InvalidOperationException("Pull request not found.");
        if (pr.State != PullRequestState.Open) throw new InvalidOperationException("Only open pull requests can be set to merge when ready.");
        RepositorySettingsService.EnsureNotArchived(pr.Repository);
        if (request.DeleteSourceBranch && pr.IsCrossFork) throw new InvalidOperationException("The source branch is in a fork, so it can't be deleted from here.");
        if (!MergeStrategySettings.Allowed(pr.Repository).HasFlag(MergeStrategySettings.Flag(request.Strategy)))
            throw new InvalidOperationException($"This repository doesn't allow {MergeStrategySettings.Label(request.Strategy).ToLowerInvariant()} merges.");
        var head = await repos.Git(pr.Repository, path => GitOps.ResolveSha(path, pr.SourceRef))
                   ?? throw new InvalidOperationException("The source branch no longer exists.");

        var row = await db.PullRequestAutoMerges.FirstOrDefaultAsync(a => a.PullRequestId == pullRequestId, ct);
        if (row?.State == AutoMergeState.Merging) throw new InvalidOperationException("This pull request is being merged right now.");
        if (row == null) db.PullRequestAutoMerges.Add(row = new PullRequestAutoMerge { PullRequestId = pullRequestId });
        row.ArmedById = actorId;
        row.Strategy = request.Strategy;
        row.DeleteSourceBranch = request.DeleteSourceBranch;
        row.ArmedHeadSha = head;
        row.ArmedTargetBranch = pr.TargetBranch;
        row.State = AutoMergeState.Armed;
        row.ArmedAt = DateTime.UtcNow;
        row.ClaimedAt = row.FinishedAt = null;
        row.CancelReason = row.WaitingFor = null;
        row.LastCheckedAt = null;
        var now = DateTime.UtcNow;
        db.PullRequestActivities.Add(new PullRequestActivity
        {
            PullRequestId = pr.Id, ActorId = actorId, CreatedAt = now,
            Text = $"enabled auto-merge ({MergeStrategySettings.Label(request.Strategy).ToLowerInvariant()}{(request.DeleteSourceBranch ? ", then delete the source branch" : "")}): " +
                   "this pull request will be merged as soon as every requirement is met",
        });
        pr.UpdatedAt = now;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw new InvalidOperationException("Auto-merge was just set by someone else. Reload the page."); }
        await audit.RecordAsync("pull_request.auto_merge_armed", "pull_request", pr.Id, pr.Repository.ProjectId,
            new { repository = pr.Repository.Slug, pr.Number, pr.TargetBranch, strategy = request.Strategy.ToString().ToLowerInvariant(), deleteSourceBranch = request.DeleteSourceBranch, head = head[..7] }, actorId);
        live.Publish(new PullRequestChanged(pr.Id, pr.Repository.ProjectId));
        signal.Wake();
        return row;
    }

    /// <summary>
    /// Cancels the request. The person who armed it, or anyone who may merge. False if nothing was armed. The armer is
    /// notified when somebody else cancels.
    /// </summary>
    public async Task<bool> CancelAsync(int pullRequestId, int actorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.PullRequestAutoMerges.AsNoTracking().FirstOrDefaultAsync(a => a.PullRequestId == pullRequestId);
        if (row == null || row.State != AutoMergeState.Armed) return false;
        if (row.ArmedById != actorId) await access.RequireForPullRequestAsync(pullRequestId, Permission.MergePullRequest, actorId);
        else await access.RequireForPullRequestAsync(pullRequestId, Permission.ViewProject, actorId);
        var name = await db.Users.Where(u => u.Id == actorId).Select(u => u.DisplayName).FirstAsync();
        return await CancelCoreAsync(pullRequestId, actorId, "cancelled by " + name, byActor: true);
    }

    /// <summary>Cancels an armed request (conditional on it still being Armed). Notifies the armer unless they did it themselves.</summary>
    private async Task<bool> CancelCoreAsync(int pullRequestId, int? actorId, string reason, bool byActor = false, bool notify = true)
    {
        reason = Trim(reason, 300);
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        var changed = await db.PullRequestAutoMerges.Where(a => a.PullRequestId == pullRequestId && a.State == AutoMergeState.Armed)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, AutoMergeState.Cancelled).SetProperty(a => a.FinishedAt, now)
                .SetProperty(a => a.CancelReason, reason).SetProperty(a => a.WaitingFor, (string?)null));
        if (changed == 0) return false;
        var row = await db.PullRequestAutoMerges.AsNoTracking().FirstAsync(a => a.PullRequestId == pullRequestId);
        var pr = await db.PullRequests.Include(p => p.Repository).FirstAsync(p => p.Id == pullRequestId);
        db.PullRequestActivities.Add(new PullRequestActivity
        {
            PullRequestId = pr.Id, ActorId = actorId ?? row.ArmedById, CreatedAt = now,
            Text = byActor ? "cancelled auto-merge" : "auto-merge was cancelled: " + reason,
        });
        pr.UpdatedAt = now;
        await db.SaveChangesAsync();
        await audit.RecordAsync("pull_request.auto_merge_cancelled", "pull_request", pr.Id, pr.Repository.ProjectId,
            new { repository = pr.Repository.Slug, pr.Number, reason, armedById = row.ArmedById, byUser = byActor }, actorId ?? row.ArmedById);
        if (notify) await notifications.OnAutoMergeCancelledAsync(pr.Id, row.ArmedById, byActor ? actorId : null, reason);
        else live.Publish(new PullRequestChanged(pr.Id, pr.Repository.ProjectId));
        return true;
    }

    // ---------- Evaluation ----------

    /// <summary>Evaluates every armed pull request (oldest first) and merges the ones that are ready. Returns how many were merged or queued.</summary>
    public async Task<int> ProcessAllAsync(CancellationToken ct = default)
    {
        List<int> ids;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var stale = DateTime.UtcNow - Lease;
            ids = await db.PullRequestAutoMerges
                .Where(a => a.State == AutoMergeState.Armed || (a.State == AutoMergeState.Merging && a.ClaimedAt < stale))
                .OrderBy(a => a.ArmedAt).Select(a => a.PullRequestId).ToListAsync(ct);
        }
        var done = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await ProcessAsync(id, ct) is AutoMergeOutcome.Merged or AutoMergeOutcome.Queued) done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Auto-merge of pull request {Id} failed", id); }
        }
        return done;
    }

    /// <summary>Evaluates one pull request's request now (see the class remarks for the rules).</summary>
    public async Task<AutoMergeOutcome> ProcessAsync(int pullRequestId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var am = await db.PullRequestAutoMerges.AsNoTracking().Include(a => a.ArmedBy)
            .Include(a => a.PullRequest).ThenInclude(p => p.Repository).ThenInclude(r => r.Project)
            .FirstOrDefaultAsync(a => a.PullRequestId == pullRequestId, ct);
        if (am == null) return AutoMergeOutcome.Idle;
        var stale = am.State == AutoMergeState.Merging && am.ClaimedAt < DateTime.UtcNow - Lease;
        if (am.State != AutoMergeState.Armed && !stale) return AutoMergeOutcome.Idle;
        var pr = am.PullRequest;
        var armer = am.ArmedBy;
        var projectId = pr.Repository.ProjectId;

        // The pull request left the "open" state.
        if (pr.State != PullRequestState.Open)
        {
            if (pr.State == PullRequestState.Merged && am.State == AutoMergeState.Merging && pr.MergedById == am.ArmedById)
            {
                await FinishMergedAsync(am, pr, queued: false, pr.MergeCommitSha); // a worker merged it and died before recording that
                return AutoMergeOutcome.Merged;
            }
            await CancelAnyAsync(am, pr.State == PullRequestState.Merged ? "the pull request was merged by someone else" : "the pull request was closed",
                notify: pr.State != PullRequestState.Merged);
            return AutoMergeOutcome.Cancelled;
        }
        if (pr.TargetBranch != am.ArmedTargetBranch)
            return await CancelAndReportAsync(am, $"the pull request was retargeted from {am.ArmedTargetBranch} to {pr.TargetBranch}");
        if (armer.DeactivatedAt != null || armer.LockoutEnd > DateTimeOffset.UtcNow)
            return await CancelAndReportAsync(am, $"{armer.DisplayName}'s account was deactivated or locked");
        if (!await access.CanAsync(projectId, Permission.MergePullRequest, armer.Id))
            return await CancelAndReportAsync(am, $"{armer.DisplayName} no longer has permission to merge");
        if (am.DeleteSourceBranch && !await access.CanAsync(projectId, Permission.DeleteBranch, armer.Id))
            return await CancelAndReportAsync(am, $"{armer.DisplayName} no longer has permission to delete branches");
        if (pr.Repository.IsArchived) return await WaitAsync(am, "the repository is archived");

        var head = await repos.Git(pr.Repository, path => GitOps.ResolveSha(path, pr.SourceRef));
        if (head == null) return await CancelAndReportAsync(am, "the source branch no longer exists");
        if (head != am.ArmedHeadSha && await ReconcileHeadAsync(am, pr, armer, head, null, ct) is { } cancelled) return cancelled;

        // The same gate as a manual merge, evaluated for the person who armed it. An admin's bypass never counts here.
        var result = await gate.EvaluateAsync(pr.Id, armer.Id, ct);
        var failed = result.External.Where(c => c.State == MergeCheckState.Failed
            && result.Blockers.Any(b => b.Contains($"check \"{c.Name}\" is failed", StringComparison.Ordinal))).ToList();
        if (failed.Count > 0) return await CancelAndReportAsync(am, $"check \"{failed[0].Name}\" failed");
        if (result.Blocked) return await WaitAsync(am, string.Join("; ", result.Blockers));

        var status = await prs.CheckMergeAsync(pr);
        switch (status.Check)
        {
            case MergeCheck.Clean: break;
            case MergeCheck.Conflicts: return await WaitAsync(am, "the branch has conflicts with " + pr.TargetBranch);
            case MergeCheck.UpToDate: return await CancelAndReportAsync(am, "there is nothing left to merge");
            default: return await CancelAndReportAsync(am, status.Message ?? "the source or target branch no longer exists");
        }

        // One attempt at a time: claim the request. A stale claim (a worker that died) can be taken over.
        var now = DateTime.UtcNow;
        var cutoff = now - Lease;
        var claimed = await db.PullRequestAutoMerges.Where(a => a.Id == am.Id && (a.State == AutoMergeState.Armed || (a.State == AutoMergeState.Merging && a.ClaimedAt < cutoff)))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, AutoMergeState.Merging).SetProperty(a => a.ClaimedAt, now), ct);
        if (claimed == 0) return AutoMergeOutcome.Idle;

        try
        {
            // A merge queue on the target: queue it (the rule never lets an auto-merge skip the queue).
            var rules = await protection.RulesForAsync(pr.RepositoryId);
            if (rules.Any(r => r.MergeQueueEnabled)
                && MergeQueueConfig.For(BranchProtectionService.ApplicableTo(rules, pr.TargetBranch, isAdmin: false).Rules, pr.TargetBranch) != null)
            {
                try { await queues.EnqueueAsync(pr.Id, armer.Id, am.DeleteSourceBranch); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("already in the merge queue", StringComparison.Ordinal)) { }
                await FinishMergedAsync(am, pr, queued: true, null);
                return AutoMergeOutcome.Queued;
            }

            var outcome = await prs.MergeAsync(pr.Id, armer, new MergeRequest(am.Strategy, am.DeleteSourceBranch, false, null, NoBypass: true, ExpectedHeadSha: head));
            if (!outcome.Success) return await ReleaseAsync(am, outcome.Error ?? "the pull request can't be merged");
            await FinishMergedAsync(am, pr, queued: false, outcome.MergeCommitSha);
            return AutoMergeOutcome.Merged;
        }
        catch (ForbiddenException)
        {
            await ReleaseAsync(am, "permission denied");
            return await CancelAndReportAsync(am, $"{armer.DisplayName} no longer has permission to merge");
        }
        catch (Exception ex) when (ex is InvalidOperationException && ex is not OperationCanceledException)
        {
            // Branch protection or a changed pull request refused it between the evaluation and the merge: keep waiting.
            log.LogInformation("Auto-merge of pull request {Id} not merged: {Message}", pr.Id, ex.Message);
            return await ReleaseAsync(am, ex.Message);
        }
        catch
        {
            await ReleaseAsync(am, "the merge failed unexpectedly");
            throw;
        }
    }

    /// <summary>
    /// A push moved the source branch. New commits by someone other than the person who armed it cancel the request; their own
    /// commits are accepted. Called after every push to the repository (with the pusher) and, when the pusher isn't known,
    /// from the sweep (judging by the new commits' authors).
    /// </summary>
    public async Task OnPushAsync(int repositoryId, int pusherId, CancellationToken ct = default)
    {
        List<PullRequestAutoMerge> rows;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            rows = await db.PullRequestAutoMerges.AsNoTracking().Include(a => a.ArmedBy)
                .Include(a => a.PullRequest).ThenInclude(p => p.Repository).ThenInclude(r => r.Project)
                .Where(a => a.State == AutoMergeState.Armed && a.PullRequest.RepositoryId == repositoryId && a.PullRequest.State == PullRequestState.Open).ToListAsync(ct);
        foreach (var am in rows)
        {
            var head = await repos.Git(am.PullRequest.Repository, path => GitOps.ResolveSha(path, am.PullRequest.SourceRef));
            if (head != null && head != am.ArmedHeadSha) await ReconcileHeadAsync(am, am.PullRequest, am.ArmedBy, head, pusherId, ct);
        }
        if (rows.Count > 0) signal.Wake();
    }

    private async Task<AutoMergeOutcome?> ReconcileHeadAsync(PullRequestAutoMerge am, PullRequest pr, User armer, string head, int? pusherId, CancellationToken ct)
    {
        string? by = null;
        if (pusherId == armer.Id) { }
        else if (pusherId is { } other)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            by = await db.Users.Where(u => u.Id == other).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "someone else";
        }
        else
        {
            // Pusher unknown (a sweep noticed it): judge by the new commits' authors.
            var commits = am.ArmedHeadSha == null ? null
                : (await repos.Git(pr.Repository, path => GitOps.Compare(path, am.ArmedHeadSha, head)))?.Commits;
            var foreign = commits?.FirstOrDefault(c => !string.Equals(c.AuthorEmail, armer.Email, StringComparison.OrdinalIgnoreCase));
            if (commits == null || foreign != null) by = foreign?.AuthorName ?? "someone else";
        }
        if (by != null)
        {
            await CancelAnyAsync(am, $"new commits were pushed by {by} after auto-merge was set", notify: true);
            return AutoMergeOutcome.Cancelled;
        }
        await using var db2 = await dbFactory.CreateDbContextAsync(ct);
        await db2.PullRequestAutoMerges.Where(a => a.Id == am.Id && a.State == AutoMergeState.Armed)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ArmedHeadSha, head), ct);
        return null;
    }

    // ---------- Outcomes ----------

    private async Task<AutoMergeOutcome> CancelAndReportAsync(PullRequestAutoMerge am, string reason)
    {
        await CancelAnyAsync(am, reason, notify: true);
        return AutoMergeOutcome.Cancelled;
    }

    /// <summary>Cancels from Armed, or from a stale Merging claim.</summary>
    private async Task CancelAnyAsync(PullRequestAutoMerge am, string reason, bool notify)
    {
        if (am.State == AutoMergeState.Merging)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            await db.PullRequestAutoMerges.Where(a => a.Id == am.Id && a.State == AutoMergeState.Merging)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, AutoMergeState.Armed));
        }
        await CancelCoreAsync(am.PullRequestId, null, reason, notify: notify);
    }

    private async Task<AutoMergeOutcome> WaitAsync(PullRequestAutoMerge am, string waitingFor)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var text = Trim(waitingFor, 500);
        var now = DateTime.UtcNow;
        var changed = am.WaitingFor != text;
        await db.PullRequestAutoMerges.Where(a => a.Id == am.Id && (a.State == AutoMergeState.Armed || a.State == AutoMergeState.Merging))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.WaitingFor, text).SetProperty(a => a.LastCheckedAt, now)
                .SetProperty(a => a.State, AutoMergeState.Armed).SetProperty(a => a.ClaimedAt, (DateTime?)null));
        if (changed) live.Publish(new PullRequestChanged(am.PullRequestId, am.PullRequest.Repository.ProjectId));
        return AutoMergeOutcome.Waiting;
    }

    /// <summary>Gives the claim back (back to Armed) with a note why it did not merge.</summary>
    private async Task<AutoMergeOutcome> ReleaseAsync(PullRequestAutoMerge am, string why)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var text = Trim(why, 500);
        await db.PullRequestAutoMerges.Where(a => a.Id == am.Id && a.State == AutoMergeState.Merging)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, AutoMergeState.Armed).SetProperty(a => a.ClaimedAt, (DateTime?)null)
                .SetProperty(a => a.WaitingFor, text).SetProperty(a => a.LastCheckedAt, DateTime.UtcNow));
        return AutoMergeOutcome.Waiting;
    }

    private async Task FinishMergedAsync(PullRequestAutoMerge am, PullRequest pr, bool queued, string? mergeSha)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        var changed = await db.PullRequestAutoMerges.Where(a => a.Id == am.Id && a.State == AutoMergeState.Merging)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, AutoMergeState.Merged).SetProperty(a => a.FinishedAt, now)
                .SetProperty(a => a.WaitingFor, (string?)null));
        if (changed == 0) return;
        var tracked = await db.PullRequests.FirstAsync(p => p.Id == pr.Id);
        db.PullRequestActivities.Add(new PullRequestActivity
        {
            PullRequestId = pr.Id, ActorId = am.ArmedById, CreatedAt = now.AddMilliseconds(2),
            Text = queued ? $"auto-merge added this pull request to the merge queue of `{pr.TargetBranch}`"
                          : $"auto-merge merged this pull request (set by {am.ArmedBy.DisplayName})",
        });
        tracked.UpdatedAt = now;
        await db.SaveChangesAsync();
        await audit.RecordAsync("pull_request.auto_merge_merged", "pull_request", pr.Id, pr.Repository.ProjectId,
            new { repository = pr.Repository.Slug, pr.Number, pr.TargetBranch, strategy = am.Strategy.ToString().ToLowerInvariant(), queued, mergeCommit = mergeSha, armedById = am.ArmedById }, am.ArmedById);
        live.Publish(new PullRequestChanged(pr.Id, pr.Repository.ProjectId));
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
