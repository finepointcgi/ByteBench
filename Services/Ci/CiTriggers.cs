using ByteBench.Data;
using ByteBench.Services.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Ci;

/// <summary>
/// Remembers each repository's branch tips (singleton) so that after a push, or a merge in the UI, the branches that
/// actually moved can be found: <see cref="IGitPushListener"/> isn't told which refs a push updated. Primed for every
/// repository at startup; a repository created later starts empty, so its first push treats every branch as new.
/// </summary>
public class CiRefTracker
{
    private readonly Dictionary<int, Dictionary<string, string>> known = [];
    private readonly Lock gate = new();

    public void Prime(int repositoryId, IEnumerable<BranchInfo> branches)
    {
        lock (gate) known[repositoryId] = Tips(branches);
    }

    /// <summary>Records the current tips and returns the branches that are new or moved since last time (deletions are ignored).</summary>
    public List<(string Branch, string Sha)> Update(int repositoryId, IEnumerable<BranchInfo> branches) => UpdateWithDeleted(repositoryId, branches).Moved;

    /// <summary>Like <see cref="Update"/>, and also the branches that were known before and are gone now (for <c>environment.on_stop</c>).</summary>
    public (List<(string Branch, string Sha)> Moved, List<string> Deleted) UpdateWithDeleted(int repositoryId, IEnumerable<BranchInfo> branches)
    {
        var now = Tips(branches);
        lock (gate)
        {
            var before = known.GetValueOrDefault(repositoryId) ?? [];
            known[repositoryId] = now;
            return (now.Where(kv => before.GetValueOrDefault(kv.Key) != kv.Value).Select(kv => (kv.Key, kv.Value)).ToList(),
                before.Keys.Where(k => !now.ContainsKey(k)).ToList());
        }
    }

    private static Dictionary<string, string> Tips(IEnumerable<BranchInfo> branches) =>
        branches.Where(b => b.Tip != null).ToDictionary(b => b.Name, b => b.Tip!.Sha);
}

/// <summary>
/// Creates pipelines (scoped). Automatic triggers (pushes, merges, pull requests) go through here without permission
/// checks, because the push or PR action itself was authorized; <see cref="PipelineService"/> checks permissions
/// before calling <see cref="CreateAsync"/> for manual runs. A commit runs at most once per branch automatically.
/// </summary>
public class CiTriggerService(IDbContextFactory<AppDbContext> dbFactory, RepoStorage storage, CiQueue queue, CiRefTracker refs,
    LiveEvents live, IServiceScopeFactory scopes, IOptionsMonitor<CiOptions> options, ILogger<CiTriggerService> log,
    CiRemoteIncludes remoteIncludes, CiWebhookEvents ciWebhooks)
{
    // Serializes the "already ran for this commit and branch?" check with the insert.
    private static readonly SemaphoreSlim CreateGate = new(1, 1);

    public bool Enabled => options.CurrentValue.Enabled;

    /// <summary>The pipeline file at a commit: null if there is none, otherwise its parse result.</summary>
    public async Task<CiParseResult?> LoadDefinitionAsync(Repository repo, string sha, bool forkPullRequest = false)
    {
        var path = storage.PathFor(repo.Project.Key, repo.Slug);
        var blob = await Task.Run(() => CiFile.GetBlob(path, sha));
        if (blob == null) return null;
        if (blob.TooLarge || blob.IsBinary || blob.Text == null) return new CiParseResult(null, $"{CiFile.FileName}: the file is too large or not text.");
        // Parsing may read included files (git objects of this commit, or remote URLs when the admin allows them): keep it off the caller's thread.
        return await Task.Run(() => CiFile.Parse(blob.Text, IncludesFor(path, sha, allowRemote: !forkPullRequest)));
    }

    /// <summary>The reader of <c>include:</c> files for the commit <paramref name="rev"/> of the repository at <paramref name="repoPath"/>.</summary>
    public ICiIncludeProvider IncludesFor(string repoPath, string rev, bool allowRemote = true) => new CiIncludeProvider(repoPath, rev, remoteIncludes, allowRemote);

    /// <summary>
    /// After a push (or a merge done in ByteBench): starts a pipeline for every branch whose tip moved and that matches
    /// the file's <c>on.branches</c>, or that is the source of an open PR when <c>on.pull_requests</c> is on.
    /// </summary>
    public async Task SyncRepositoryAsync(int repositoryId, int? userId, CancellationToken ct = default)
    {
        if (!Enabled) return;
        var repo = await LoadRepoAsync(repositoryId, ct);
        if (repo == null) return;
        var path = storage.PathFor(repo.Project.Key, repo.Slug);
        var branches = await Task.Run(() => GitOps.ListBranches(path, repo.DefaultBranch), ct);
        var (moved, deleted) = refs.UpdateWithDeleted(repositoryId, branches);
        foreach (var gone in deleted) await StopEnvironmentsAsync(repo, gone, ct); // environment.on_stop
        if (moved.Count == 0) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Fork PRs' heads aren't branches of this repository (their pipelines start in OnPullRequestChangedAsync).
        var openPrs = await db.PullRequests.Where(p => p.RepositoryId == repositoryId && p.State == PullRequestState.Open && !p.IsCrossFork)
            .Select(p => new { p.Id, p.SourceBranch }).ToListAsync(ct);

        foreach (var (branch, sha) in moved)
        {
            var def = await LoadDefinitionAsync(repo, sha);
            if (def == null) continue;
            var prId = openPrs.FirstOrDefault(p => p.SourceBranch == branch)?.Id;
            var trigger = PipelineTrigger.Push;
            if (def.Definition is { } d)
            {
                var forBranch = d.On.MatchesBranch(branch);
                var forPr = prId != null && d.On.PullRequests;
                if (!forBranch && !forPr) continue;
                if (!forBranch) trigger = PipelineTrigger.PullRequest;
                if (!forPr) prId = null;
            }
            await CreateAsync(repo, branch, sha, trigger, userId, prId, def, dedupe: true, ct);
        }
    }

    /// <summary>
    /// After a push: starts a pipeline for each tag it created or moved, if the file asks for tag pipelines
    /// (<see cref="CiDefinition.RunsOnTag"/>). A tag whose commit has no pipeline file starts nothing.
    /// </summary>
    public async Task OnTagsPushedAsync(int repositoryId, int? userId, IEnumerable<string> tags, CancellationToken ct = default)
    {
        if (!Enabled) return;
        var list = tags.Where(t => GitOps.IsValidTagName(t) && t.Length <= CiRef.MaxTagLength).Distinct().ToList();
        if (list.Count == 0) return;
        var repo = await LoadRepoAsync(repositoryId, ct);
        if (repo == null) return;
        foreach (var tag in list) await OnTagAsync(repo, tag, userId, ct);
    }

    /// <summary>A tag was created in ByteBench (a release or the Tags page): the same as a pushed tag.</summary>
    public async Task OnTagCreatedAsync(int repositoryId, string tag, int? userId, CancellationToken ct = default) =>
        await OnTagsPushedAsync(repositoryId, userId, [tag], ct);

    private async Task OnTagAsync(Repository repo, string tag, int? userId, CancellationToken ct)
    {
        var path = storage.PathFor(repo.Project.Key, repo.Slug);
        var sha = await Task.Run(() => GitOps.TagExists(path, tag) ? GitOps.ResolveSha(path, CiRef.ForTag(tag)) : null, ct);
        if (sha == null) return;
        var def = await LoadDefinitionAsync(repo, sha);
        if (def == null) return;
        if (def.Definition is { } d && !d.RunsOnTag(tag)) return;
        await CreateAsync(repo, CiRef.ForTag(tag), sha, PipelineTrigger.Tag, userId, null, def, dedupe: true, ct);
    }

    /// <summary>
    /// A PR was opened or changed: makes sure its source head has a pipeline (if the file asks for PR pipelines), and
    /// after a merge picks up the moved target branch.
    /// </summary>
    public async Task OnPullRequestChangedAsync(int pullRequestId, CancellationToken ct = default)
    {
        if (!Enabled) return;
        PullRequest? pr;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            pr = await db.PullRequests.AsNoTracking().Include(p => p.Repository).ThenInclude(r => r.Project)
                .FirstOrDefaultAsync(p => p.Id == pullRequestId, ct);
        if (pr == null) return;

        if (pr.State != PullRequestState.Open && !pr.IsCrossFork) await StopEnvironmentsAsync(pr.Repository, pr.SourceBranch, ct); // environment.on_stop
        if (pr.State == PullRequestState.Merged)
        {
            await SyncRepositoryAsync(pr.RepositoryId, pr.MergedById, ct);
            return;
        }
        if (pr.State != PullRequestState.Open) return;

        var path = storage.PathFor(pr.Repository.Project.Key, pr.Repository.Slug);
        // A fork PR's head is refs/pull/{n}/head: its pipeline gets no CI variables and waits for a Maintainer's approval.
        var source = pr.SourceRef;
        if (await Task.Run(() => GitOps.BranchExists(path, source) ? GitOps.ResolveSha(path, source) : null, ct) is not { } head) return;
        if (await AttachToExistingAsync(pr.RepositoryId, source, head, pr.Id, ct)) return;

        var def = await LoadDefinitionAsync(pr.Repository, head, forkPullRequest: pr.IsCrossFork);
        if (def == null || def.Definition is { On.PullRequests: false }) return;
        await CreateAsync(pr.Repository, source, head, PipelineTrigger.PullRequest, pr.AuthorId, pr.Id, def, dedupe: true, ct, forkPullRequest: pr.IsCrossFork);
    }

    /// <summary>
    /// Creates a pipeline for <paramref name="sha"/> on <paramref name="branch"/>: Queued (and enqueued) when the file
    /// is valid, Error with the parse message otherwise. With <paramref name="dedupe"/>, nothing is created if the
    /// commit already has a pipeline on that branch (the PR link is added to it instead). Returns the new id, or null.
    /// A <paramref name="forkPullRequest"/> pipeline never gets CI variables and, unless <paramref name="approvedById"/> is
    /// given, waits (Queued, not enqueued) until a Maintainer approves it (<see cref="PipelineService.ApproveAsync"/>).
    /// </summary>
    public async Task<int?> CreateAsync(Repository repo, string branch, string sha, PipelineTrigger trigger, int? userId,
        int? pullRequestId, CiParseResult def, bool dedupe, CancellationToken ct = default, bool forkPullRequest = false, int? approvedById = null,
        int? scheduleId = null, IReadOnlyDictionary<string, string>? variables = null, string? rulesBranch = null)
    {
        var path = storage.PathFor(repo.Project.Key, repo.Slug);
        var commit = await Task.Run(() => GitOps.GetCommit(path, sha), ct);

        // rules/only: the jobs that run on this branch. None at all: no pipeline (an error for manual runs).
        // A merge queue pipeline (Ref refs/merge-queue/...) picks its jobs as if it ran on the target branch (rulesBranch).
        // Tag pipelines (Ref refs/tags/{tag}) match the rules' tags and `if: $CI_COMMIT_TAG` conditions.
        var ruleContext = CiRuleContext.For(rulesBranch ?? branch, CiRunner.SourceName(trigger), forkPullRequest ? null : variables);
        var (jobs, jobsError) = def.Definition?.ForRef(ruleContext) ?? ([], null);
        if (def.Definition != null && jobsError == null && jobs.Count == 0)
        {
            if (dedupe) return null;
            throw new InvalidOperationException($"No job in {CiFile.FileName} runs on {ruleContext.Describe} (see the jobs' rules).");
        }

        Pipeline p;
        await CreateGate.WaitAsync(ct);
        try
        {
            if (dedupe && await AttachToExistingAsync(repo.Id, branch, sha, pullRequestId, ct)) return null;

            p = new Pipeline
            {
                RepositoryId = repo.Id, Ref = branch, CommitSha = sha, PullRequestId = pullRequestId, Trigger = trigger,
                TriggeredById = userId, CommitMessage = Truncate(commit?.MessageShort, 200), CreatedAt = DateTime.UtcNow,
                IsForkPullRequest = forkPullRequest, AwaitingApproval = forkPullRequest && approvedById == null,
                ApprovedById = forkPullRequest ? approvedById : null, ApprovedAt = forkPullRequest && approvedById != null ? DateTime.UtcNow : null,
                // A fork PR pipeline never carries extra variables (it gets no CI or schedule variables of the upstream).
                ScheduleId = scheduleId, VariablesJson = forkPullRequest ? null : CiJobExtras.SerializeVariables(variables),
            };
            if (def.Definition != null && jobsError == null)
            {
                var order = 0;
                foreach (var j in jobs)
                {
                    var job = new PipelineJob
                    {
                        Name = j.Name, Image = j.Image, Needs = string.Join(",", j.Needs), Order = order++,
                        IsManual = j.When == CiJobWhen.Manual, AllowFailure = j.AllowFailure,
                        EnvironmentName = j.Environment?.Name, EnvironmentUrl = j.Environment?.Url, EnvironmentAction = j.Environment?.Action, ExtrasJson = CiJobExtras.Serialize(j),
                    };
                    var n = 0;
                    foreach (var s in j.Steps) job.Steps.Add(new PipelineStep { Order = n++, Name = s.Name, Run = s.Run });
                    p.Jobs.Add(job);
                }
            }
            else
            {
                p.Status = PipelineStatus.Error;
                p.Error = jobsError ?? def.Error;
                p.FinishedAt = p.CreatedAt;
            }

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.Pipelines.Add(p);
            await db.SaveChangesAsync(ct);
        }
        finally { CreateGate.Release(); }

        p.Repository = repo;
        log.LogInformation("Pipeline {Id} created for {Key}/{Slug} {Branch} @ {Sha} ({Trigger}): {Status}",
            p.Id, repo.Project.Key, repo.Slug, branch, sha[..7], trigger, p.Status);
        live.Publish(new PipelineChanged(p.Id, repo.Id));
        ciWebhooks.PipelineCreated(p);
        if (p.Status == PipelineStatus.Queued && !p.AwaitingApproval) queue.Enqueue(p.Id);
        else await CiRunner.NotifyFailedAsync(scopes, p, log);
        return p.Id;
    }

    /// <summary>
    /// <c>environment: { on_stop: job }</c>: a branch was deleted, or its pull request merged or declined. For each environment the
    /// branch's pipelines deployed (newest pipeline first), the named stop job of that pipeline is run if it is waiting
    /// (manual) and the environment isn't stopped yet. A stop job that needs a protected environment's approval still waits for it.
    /// </summary>
    public async Task StopEnvironmentsAsync(Repository repo, string branch, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var pipelines = await db.Pipelines.AsNoTracking().Include(p => p.Jobs)
                .Where(p => p.RepositoryId == repo.Id && p.Ref == branch).OrderByDescending(p => p.Id).Take(20).ToListAsync(ct);
            var stopped = await db.CiEnvironments.AsNoTracking().Where(e => e.RepositoryId == repo.Id && e.StoppedAt != null).Select(e => e.Name).ToListAsync(ct);
            var handled = new HashSet<string>(stopped);
            foreach (var p in pipelines)
                foreach (var deploy in p.Jobs.Where(j => j.EnvironmentName != null && j.EnvironmentAction != "stop" && j.Status == PipelineStatus.Passed))
                {
                    if (CiJobExtras.Parse(deploy.ExtrasJson).OnStop is not { } stopName || !handled.Add(deploy.EnvironmentName!)) continue;
                    var stop = p.Jobs.FirstOrDefault(j => j.Name == stopName);
                    if (stop is not { Status: PipelineStatus.Manual, RequiresApproval: false }) continue;
                    var n = await db.PipelineJobs.Where(j => j.Id == stop.Id && j.Status == PipelineStatus.Manual)
                        .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, PipelineStatus.Queued).SetProperty(j => j.PlayedAt, DateTime.UtcNow), ct);
                    if (n == 0) continue;
                    log.LogInformation("Stopping environment '{Env}' of {Key}/{Slug}: branch '{Branch}' is gone, running job '{Job}' of pipeline {Pipeline}",
                        deploy.EnvironmentName, repo.Project.Key, repo.Slug, branch, stop.Name, p.Id);
                    if (!queue.Wake(p.Id))
                    {
                        var requeued = await db.Pipelines.Where(x => x.Id == p.Id && x.Status != PipelineStatus.Queued && x.Status != PipelineStatus.Running)
                            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, PipelineStatus.Queued).SetProperty(x => x.Error, (string?)null), ct);
                        if (requeued > 0) queue.Enqueue(p.Id);
                    }
                    live.Publish(new PipelineChanged(p.Id, repo.Id));
                }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Could not stop environments of branch {Branch}", branch); }
    }

    /// <summary>True if the commit already has a pipeline on the branch; links it to the PR if it wasn't yet.</summary>
    private async Task<bool> AttachToExistingAsync(int repositoryId, string branch, string sha, int? pullRequestId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Pipelines.Where(x => x.RepositoryId == repositoryId && x.Ref == branch && x.CommitSha == sha)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        if (existing == null) return false;
        if (pullRequestId != null && existing.PullRequestId == null)
        {
            existing.PullRequestId = pullRequestId;
            await db.SaveChangesAsync(ct);
            live.Publish(new PipelineChanged(existing.Id, repositoryId));
        }
        return true;
    }

    private async Task<Repository?> LoadRepoAsync(int repositoryId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Repositories.AsNoTracking().Include(r => r.Project).FirstOrDefaultAsync(r => r.Id == repositoryId, ct);
    }

    private static string? Truncate(string? s, int max) => s == null || s.Length <= max ? s : s[..(max - 1)] + "…";
}

/// <summary>Runs pipelines for the branches and tags a push updated.</summary>
public class CiPushListener(CiTriggerService triggers, Webhooks.PushCapture capture) : IGitPushListener
{
    public async Task OnPushAsync(int repositoryId, int userId, CancellationToken ct)
    {
        await triggers.SyncRepositoryAsync(repositoryId, userId, ct);
        // Tags the push created or moved (deletions start nothing).
        var tags = capture.Refs.Where(r => !r.Deleted && CiRef.IsTag(r.Ref)).Select(r => CiRef.TagName(r.Ref)).ToList();
        if (tags.Count > 0) await triggers.OnTagsPushedAsync(repositoryId, userId, tags, ct);
    }
}

/// <summary>
/// Watches <see cref="PullRequestChanged"/> events (published when a PR is opened, updated or merged) and hands them to
/// <see cref="CiTriggerService.OnPullRequestChangedAsync"/> one at a time, so <c>PullRequestService</c> needs no CI hooks.
/// </summary>
public class CiPullRequestWatcher(LiveEvents live, IServiceScopeFactory scopes, ILogger<CiPullRequestWatcher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WorkerPulse.Report("ci-pr-watcher", true);
        var work = System.Threading.Channels.Channel.CreateUnbounded<int>(new() { SingleReader = true });
        using var sub = live.Subscribe<PullRequestChanged>(e => { work.Writer.TryWrite(e.PullRequestId); return Task.CompletedTask; });
        try
        {
            await foreach (var prId in work.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    WorkerPulse.Tick("ci-pr-watcher");
                    using var scope = scopes.CreateScope();
                    scope.ServiceProvider.GetRequiredService<ProjectAccess>().ActAsSystem();
                    await scope.ServiceProvider.GetRequiredService<CiTriggerService>().OnPullRequestChangedAsync(prId, stoppingToken);
                    WorkerPulse.Ok("ci-pr-watcher");
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { WorkerPulse.Fail("ci-pr-watcher", ex); log.LogError(ex, "CI trigger for PR {Id} failed", prId); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
