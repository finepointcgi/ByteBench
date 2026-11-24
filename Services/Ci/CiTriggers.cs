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
    public List<(string Branch, string Sha)> Update(int repositoryId, IEnumerable<BranchInfo> branches)
    {
        var now = Tips(branches);
        lock (gate)
        {
            var before = known.GetValueOrDefault(repositoryId) ?? [];
            known[repositoryId] = now;
            return now.Where(kv => before.GetValueOrDefault(kv.Key) != kv.Value).Select(kv => (kv.Key, kv.Value)).ToList();
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
    LiveEvents live, IServiceScopeFactory scopes, IOptionsMonitor<CiOptions> options, ILogger<CiTriggerService> log)
{
    // Serializes the "already ran for this commit and branch?" check with the insert.
    private static readonly SemaphoreSlim CreateGate = new(1, 1);

    public bool Enabled => options.CurrentValue.Enabled;

    /// <summary>The pipeline file at a commit: null if there is none, otherwise its parse result.</summary>
    public async Task<CiParseResult?> LoadDefinitionAsync(Repository repo, string sha)
    {
        var path = storage.PathFor(repo.Project.Key, repo.Slug);
        var blob = await Task.Run(() => CiFile.GetBlob(path, sha));
        if (blob == null) return null;
        if (blob.TooLarge || blob.IsBinary || blob.Text == null) return new CiParseResult(null, $"{CiFile.FileName}: the file is too large or not text.");
        return CiFile.Parse(blob.Text);
    }

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
        var moved = refs.Update(repositoryId, branches);
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

        var def = await LoadDefinitionAsync(pr.Repository, head);
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
        var (jobs, jobsError) = def.Definition?.ForBranch(rulesBranch ?? branch) ?? ([], null);
        if (def.Definition != null && jobsError == null && jobs.Count == 0)
        {
            if (dedupe) return null;
            throw new InvalidOperationException($"No job in {CiFile.FileName} runs on branch '{rulesBranch ?? branch}' (see the jobs' rules).");
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
        if (p.Status == PipelineStatus.Queued && !p.AwaitingApproval) queue.Enqueue(p.Id);
        else await CiRunner.NotifyFailedAsync(scopes, p, log);
        return p.Id;
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

/// <summary>Runs pipelines for the branches a push updated.</summary>
public class CiPushListener(CiTriggerService triggers) : IGitPushListener
{
    public Task OnPushAsync(int repositoryId, int userId, CancellationToken ct) => triggers.SyncRepositoryAsync(repositoryId, userId, ct);
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
