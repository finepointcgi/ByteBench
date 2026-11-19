using GitBench.Data;
using GitBench.Services.Audit;
using GitBench.Services.Ci;
using GitBench.Services.Git;
using GitBench.Services.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.MergeQueue;

/// <summary>
/// Processes merge queues (scoped; run acting as the system). Everything is derived from the database and the bare
/// repository on each pass, so a pass can run at any time, any number of times, and after a restart:
/// <list type="number">
/// <item>Entries whose speculative commit is already on the target (a merge that was interrupted) are recorded as merged.</item>
/// <item>Entries whose PR was closed, turned into a draft or got new commits are dropped (the author is told about a push).</item>
/// <item>Walking the queue in order, each of the first <see cref="MergeQueueConfig.MaxBatch"/> entries must sit on the commit
/// before it (the target tip for the first, the previous entry's speculative commit after that). Entries that do keep
/// their build; their pipeline result is read (passed, failed → ejected, timed out → ejected). Entries that don't (the target
/// moved, an entry ahead left, the order changed, a pipeline was interrupted by a restart) are rebuilt: their old pipeline is
/// canceled, a new speculative commit is written to <c>refs/merge-queue/{branch}/{id}</c> and a new pipeline started.</item>
/// <item>The furthest passed entry merges, together with every entry ahead of it (its commit contains them): the target is
/// fast-forwarded to its commit only if it hasn't moved, each PR is closed as merged by whoever queued it, and the usual push
/// listeners (webhooks, CI, smart commits, …) run for the ref update.</item>
/// </list>
/// </summary>
public sealed class MergeQueueEngine(IDbContextFactory<AppDbContext> dbFactory, RepoStorage storage, BranchProtectionService protection,
    PullRequestService prs, MergeGate gate, CiTriggerService triggers, CiQueue ciQueue, NotificationService notifications, AuditLog audit,
    LiveEvents live, MergeQueueSignal signal, IServiceScopeFactory scopes, ILogger<MergeQueueEngine> log)
{
    private sealed record Dropped(int PullRequestId, int? ActorId, string Reason, bool Notify);

    /// <summary>One pass over every branch with active entries. Serialized with every other pass.</summary>
    public async Task ProcessAllAsync(CancellationToken ct = default)
    {
        await signal.ProcessLock.WaitAsync(ct);
        try
        {
            List<(int RepositoryId, string TargetBranch)> queues;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
                queues = (await db.MergeQueueEntries.Where(e => e.State == MergeQueueEntryState.Queued || e.State == MergeQueueEntryState.Testing || e.State == MergeQueueEntryState.Passed)
                    .Select(e => new { e.RepositoryId, e.TargetBranch }).Distinct().ToListAsync(ct))
                    .Select(x => (x.RepositoryId, x.TargetBranch)).ToList();
            foreach (var (repoId, branch) in queues)
            {
                try
                {
                    // Keep going while a pass merges something (the next entries may already have passed).
                    for (var i = 0; i < 20 && await ProcessBranchAsync(repoId, branch, ct); i++) { }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Merge queue of repository {RepositoryId} branch {Branch} failed", repoId, branch);
                }
            }
            await UpdateWatchAsync(ct);
        }
        finally { signal.ProcessLock.Release(); }
    }

    private async Task UpdateWatchAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var active = await db.MergeQueueEntries.Where(e => e.State == MergeQueueEntryState.Queued || e.State == MergeQueueEntryState.Testing || e.State == MergeQueueEntryState.Passed)
            .Select(e => new { e.PullRequestId, e.PipelineId }).ToListAsync(ct);
        signal.Watch(active.Where(a => a.PipelineId != null).Select(a => a.PipelineId!.Value), active.Select(a => a.PullRequestId));
    }

    /// <summary>One pass over one branch's queue. True if it merged something.</summary>
    private async Task<bool> ProcessBranchAsync(int repositoryId, string branch, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var repo = await db.Repositories.AsNoTracking().Include(r => r.Project).FirstOrDefaultAsync(r => r.Id == repositoryId, ct);
        if (repo == null) return false;
        var path = storage.PathFor(repo.Project.Key, repo.Slug);
        var entries = await db.MergeQueueEntries
            .Include(e => e.PullRequest).ThenInclude(p => p.Repository).ThenInclude(r => r.Project)
            .Include(e => e.EnqueuedBy)
            .Where(e => e.RepositoryId == repositoryId && e.TargetBranch == branch
                        && (e.State == MergeQueueEntryState.Queued || e.State == MergeQueueEntryState.Testing || e.State == MergeQueueEntryState.Passed))
            .OrderBy(e => e.Position).ThenBy(e => e.Id).AsSplitQuery().ToListAsync(ct);
        // Pipelines are read untracked: entries only ever change their PipelineId.
        var pipelineIds = entries.Where(e => e.PipelineId != null).Select(e => e.PipelineId!.Value).ToList();
        var pipelines = await db.Pipelines.AsNoTracking().Where(p => pipelineIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var dropped = new List<Dropped>();
        var now = DateTime.UtcNow;
        var merged = false;

        var config = MergeQueueConfig.For(await protection.RulesForAsync(repositoryId), branch);
        var tip = await Task.Run(() => GitOps.ResolveSha(path, "refs/heads/" + branch), ct);
        string? dropAll = config == null ? $"The merge queue of '{branch}' was turned off."
            : repo.IsArchived ? "The repository was archived."
            : tip == null ? $"The target branch '{branch}' no longer exists." : null;
        if (dropAll != null)
        {
            foreach (var e in entries) await DropAsync(e, MergeQueueEntryState.Removed, dropAll, null, dropped, path);
            entries.Clear();
        }

        // 1. Already on the target (e.g. the server stopped between moving the branch and recording the merge).
        if (tip != null)
        {
            foreach (var e in entries.ToList())
            {
                if (e.SpeculativeSha == null || e.BaseSha == null || !await Task.Run(() => GitOps.IsAncestor(path, e.SpeculativeSha, tip), ct)) continue;
                await RecordMergedAsync(db, e, repo, path, now, ct);
                entries.Remove(e);
                merged = true;
            }
        }

        // 2. PRs that changed under the queue.
        foreach (var e in entries.ToList())
        {
            var pr = e.PullRequest;
            string? reason = null;
            var notify = true;
            if (pr.State != PullRequestState.Open) { reason = pr.State == PullRequestState.Merged ? "The pull request was merged outside the queue." : "The pull request was closed."; notify = false; }
            else if (pr.IsDraft) reason = "The pull request was converted to a draft.";
            else
            {
                var head = await Task.Run(() => GitOps.BranchExists(path, pr.SourceRef) ? GitOps.ResolveSha(path, pr.SourceRef) : null, ct);
                if (head == null) reason = $"The source branch '{pr.SourceLabel}' no longer exists.";
                else if (head != e.HeadSha) reason = "New commits were pushed to the pull request while it was queued. Add it to the queue again when it's ready.";
            }
            if (reason == null) continue;
            await DropAsync(e, MergeQueueEntryState.Removed, reason, null, dropped, path, notify);
            entries.Remove(e);
        }

        // 3. Walk the train: keep consistent builds, rebuild the rest, read pipeline results.
        var chain = new List<MergeQueueEntry>();
        var expected = tip;
        for (var i = 0; i < entries.Count && config != null && expected != null; i++)
        {
            var e = entries[i];
            if (chain.Count >= config.MaxBatch)
            {
                if (e.State != MergeQueueEntryState.Queued) await ResetAsync(e, path, "Waiting for a place in the train.");
                continue;
            }
            var consistent = e.State is MergeQueueEntryState.Testing or MergeQueueEntryState.Passed && e.BaseSha == expected && e.SpeculativeSha != null;
            if (consistent && e.State == MergeQueueEntryState.Testing)
            {
                switch (Evaluate(e, e.PipelineId is { } pid ? pipelines.GetValueOrDefault(pid) : null, config, now))
                {
                    case (Verdict.Passed, _):
                        e.State = MergeQueueEntryState.Passed;
                        e.Message = null;
                        e.UpdatedAt = now;
                        break;
                    case (Verdict.Failed, var why):
                        await DropAsync(e, MergeQueueEntryState.Failed, why!, null, dropped, path);
                        continue;
                    case (Verdict.Retry, _):
                        consistent = false;
                        break;
                }
            }
            if (!consistent)
            {
                await ResetAsync(e, path, null);
                if (await BuildAsync(e, repo, branch, expected, config, now, ct) is { } failure)
                {
                    await DropAsync(e, MergeQueueEntryState.Failed, failure, null, dropped, path);
                    continue;
                }
            }
            chain.Add(e);
            expected = e.SpeculativeSha;
        }
        await db.SaveChangesAsync(ct);

        // 4. Merge the furthest passed entry and everything ahead of it.
        var k = chain.FindLastIndex(e => e.State == MergeQueueEntryState.Passed);
        for (var j = 0; j <= k; j++)
        {
            var g = await gate.EvaluateAsync(chain[j].PullRequestId, chain[j].EnqueuedById, ct);
            if (!g.Blocked || g.CanBypass) continue;
            await DropAsync(chain[j], MergeQueueEntryState.Failed, "It can no longer be merged: " + string.Join("; ", g.Blockers) + ".", null, dropped, path);
            k = -1; // the entries behind it are rebuilt on the next pass
            break;
        }
        if (k >= 0 && tip != null)
        {
            var head = chain[k];
            var newTip = head.SpeculativeSha!;
            var moved = await Task.Run(() => GitOps.FastForwardBranch(path, branch, tip, newTip,
                $"merge queue: merged {string.Join(", ", chain.Take(k + 1).Select(c => "#" + c.PullRequest.Number))}"), ct);
            if (moved)
            {
                for (var j = 0; j <= k; j++) await RecordMergedAsync(db, chain[j], repo, path, now, ct, batch: k + 1);
                merged = true;
                await RunPushListenersAsync(repositoryId, head.EnqueuedById, branch, tip, newTip);
            }
        }
        await db.SaveChangesAsync(ct);
        await CleanupRefsAsync(db, path, repositoryId, branch, ct);

        foreach (var d in dropped)
        {
            try { await notifications.OnMergeQueueRemovedAsync(d.PullRequestId, d.ActorId, d.Reason); }
            catch (Exception ex) { log.LogWarning(ex, "Could not notify about merge queue removal of PR {Id}", d.PullRequestId); }
        }
        live.Publish(new MergeQueueChanged(repositoryId, branch));
        return merged;
    }

    private enum Verdict { Pending, Passed, Failed, Retry }

    private static (Verdict, string?) Evaluate(MergeQueueEntry e, Pipeline? p, MergeQueueConfig config, DateTime now)
    {
        if (p == null) return config.RequireCi ? (Verdict.Retry, null) : (Verdict.Passed, null);
        switch (p.Status)
        {
            case PipelineStatus.Passed: return (Verdict.Passed, null);
            case PipelineStatus.Error when p.Error?.StartsWith("Interrupted by a server restart", StringComparison.Ordinal) == true:
                return (Verdict.Retry, null);
            case PipelineStatus.Failed: return (Verdict.Failed, $"Pipeline #{p.Id} failed on the merge of this pull request with the ones ahead of it.");
            case PipelineStatus.Error: return (Verdict.Failed, $"Pipeline #{p.Id} could not run: {p.Error}");
            case PipelineStatus.Canceled: return (Verdict.Failed, $"Pipeline #{p.Id} was canceled.");
            case PipelineStatus.Skipped: return (Verdict.Failed, $"Pipeline #{p.Id} was skipped.");
        }
        if (e.TestingSince is { } since && now - since > TimeSpan.FromMinutes(config.TimeoutMinutes))
            return (Verdict.Failed, $"Pipeline #{p.Id} didn't finish within {config.TimeoutMinutes} minutes.");
        return (Verdict.Pending, null);
    }

    /// <summary>Builds the entry's speculative commit on <paramref name="baseSha"/> and starts its pipeline. Null on success, else why it failed.</summary>
    private async Task<string?> BuildAsync(MergeQueueEntry e, Repository repo, string branch, string baseSha, MergeQueueConfig config, DateTime now, CancellationToken ct)
    {
        var refName = GitOps.MergeQueueRef(branch, e.Id);
        var outcome = await prs.BuildQueuedMergeAsync(e.PullRequest, e.EnqueuedBy, e.Strategy, baseSha, e.HeadSha, refName);
        e.Builds++;
        e.UpdatedAt = now;
        if (!outcome.Success)
            return outcome.Error + (outcome.ConflictPaths.Count > 0 ? " Conflicting files: " + string.Join(", ", outcome.ConflictPaths.Take(10)) : "");
        e.BaseSha = baseSha;
        e.SpeculativeSha = outcome.MergeCommitSha;
        e.TestingSince = now;
        e.Message = null;
        if (!config.RequireCi) { e.State = MergeQueueEntryState.Passed; return null; }
        if (!triggers.Enabled) return "CI is turned off on this server, but the merge queue requires a passing pipeline.";
        var def = await triggers.LoadDefinitionAsync(repo, outcome.MergeCommitSha!);
        if (def == null) return $"The merge queue requires a passing pipeline, but there is no {CiFile.FileName} on the merged result.";
        try
        {
            var pr = e.PullRequest;
            var id = await triggers.CreateAsync(repo, refName, outcome.MergeCommitSha!, PipelineTrigger.MergeQueue, e.EnqueuedById, pr.Id, def,
                dedupe: false, ct, forkPullRequest: pr.IsCrossFork, approvedById: pr.IsCrossFork ? e.EnqueuedById : null, rulesBranch: branch);
            if (id == null) return "No pipeline could be started for the merged result.";
            e.PipelineId = id;
            e.State = MergeQueueEntryState.Testing;
            return null;
        }
        catch (InvalidOperationException ex) { return ex.Message; }
    }

    /// <summary>Throws away the entry's build (pipeline canceled, ref deleted): it is built again from scratch.</summary>
    private async Task ResetAsync(MergeQueueEntry e, string path, string? message)
    {
        await StopBuildAsync(e, path);
        e.State = MergeQueueEntryState.Queued;
        e.BaseSha = e.SpeculativeSha = null;
        e.PipelineId = null;
        e.TestingSince = null;
        e.Message = message;
        e.UpdatedAt = DateTime.UtcNow;
    }

    private async Task StopBuildAsync(MergeQueueEntry e, string path)
    {
        if (e.PipelineId is { } pid) await MergeQueuePipelines.CancelAsync(dbFactory, ciQueue, live, pid);
        await Task.Run(() => GitOps.DeleteRef(path, GitOps.MergeQueueRef(e.TargetBranch, e.Id)));
    }

    /// <summary>Ends the entry (Failed or Removed): its pipeline is canceled if still running (but kept for reference) and the PR gets an activity line.</summary>
    private async Task DropAsync(MergeQueueEntry e, MergeQueueEntryState state, string reason, int? actorId, List<Dropped> dropped, string path, bool notify = true)
    {
        await StopBuildAsync(e, path);
        e.State = state;
        e.Message = reason.Length > 500 ? reason[..499] + "…" : reason;
        e.RemovedById = actorId;
        e.FinishedAt = e.UpdatedAt = DateTime.UtcNow;
        if (e.PullRequest.State == PullRequestState.Open)
            await prs.LogQueueActivityAsync(e.PullRequestId, e.EnqueuedById, $"queued this pull request, but the merge queue removed it: {reason}");
        await audit.RecordAsync(state == MergeQueueEntryState.Failed ? "merge_queue.failed" : "merge_queue.removed", "pull_request", e.PullRequestId,
            e.PullRequest.Repository.ProjectId, new { repository = e.PullRequest.Repository.Slug, e.PullRequest.Number, e.TargetBranch, reason, e.PipelineId }, actorId);
        if (notify) dropped.Add(new Dropped(e.PullRequestId, actorId, e.Message, true));
        log.LogInformation("Merge queue {Branch}: PR #{Number} {State}: {Reason}", e.TargetBranch, e.PullRequest.Number, state, reason);
    }

    private async Task RecordMergedAsync(AppDbContext db, MergeQueueEntry e, Repository repo, string path, DateTime now, CancellationToken ct, int batch = 1)
    {
        e.State = MergeQueueEntryState.Merged;
        e.FinishedAt = e.UpdatedAt = now;
        e.Message = batch > 1 ? $"Merged in a batch of {batch}." : null;
        await db.SaveChangesAsync(ct);
        // Merged as part of a batch before its own pipeline finished: that pipeline is no longer needed.
        if (e.PipelineId is { } pid) await MergeQueuePipelines.CancelAsync(dbFactory, ciQueue, live, pid);
        await Task.Run(() => GitOps.DeleteRef(path, GitOps.MergeQueueRef(e.TargetBranch, e.Id)), ct);
        await prs.CompleteQueuedMergeAsync(e.PullRequestId, e.EnqueuedById, e.Strategy, e.SpeculativeSha!, e.HeadSha, e.BaseSha!);
        await audit.RecordAsync("merge_queue.merged", "pull_request", e.PullRequestId, repo.ProjectId,
            new { repository = repo.Slug, e.PullRequest.Number, e.TargetBranch, commit = e.SpeculativeSha, strategy = e.Strategy.ToString().ToLowerInvariant(), e.PipelineId, batch },
            e.EnqueuedById);
    }

    /// <summary>Runs every <see cref="IGitPushListener"/> for the target's move, as a git push by the enqueuer would (webhooks, CI, smart commits…).</summary>
    private async Task RunPushListenersAsync(int repositoryId, int userId, string branch, string before, string after)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ProjectAccess>().ActAs(userId);
            scope.ServiceProvider.GetRequiredService<PushCapture>().SetRefs([WebhookPushEvents.Branch(branch, before, after)]);
            await GitRepoLookup.NotifyPushListenersAsync(scope.ServiceProvider, log, repositoryId, userId);
        }
        catch (Exception ex) { log.LogError(ex, "Push listeners after a merge queue merge into {Branch} failed", branch); }
    }

    /// <summary>Deletes leftover speculative refs of this branch that no active entry owns.</summary>
    private async Task CleanupRefsAsync(AppDbContext db, string path, int repositoryId, string branch, CancellationToken ct)
    {
        var prefix = GitOps.MergeQueueRefPrefix + branch + "/";
        var refs = await Task.Run(() => GitOps.ListRefs(path, prefix), ct);
        if (refs.Count == 0) return;
        var keep = (await db.MergeQueueEntries.Where(e => e.RepositoryId == repositoryId && e.TargetBranch == branch
                        && (e.State == MergeQueueEntryState.Testing || e.State == MergeQueueEntryState.Passed))
                    .Select(e => e.Id).ToListAsync(ct)).Select(id => GitOps.MergeQueueRef(branch, id)).ToHashSet();
        foreach (var name in refs.Keys.Where(n => !keep.Contains(n) && !n[prefix.Length..].Contains('/')))
            await Task.Run(() => GitOps.DeleteRef(path, name), ct);
    }
}
