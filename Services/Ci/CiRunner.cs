using System.Text;
using GitBench.Data;
using GitBench.Services.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GitBench.Services.Ci;

/// <summary>Routes for the pipeline pages.</summary>
public static class PipelineUrls
{
    public static string List(Repository r) => RepoUrls.Base(r) + "/pipelines";
    public static string Detail(Repository r, int pipelineId) => $"{RepoUrls.Base(r)}/pipelines/{pipelineId}";
    public static string Detail(string projectKey, string slug, int pipelineId) => $"/p/{projectKey}/repos/{slug}/pipelines/{pipelineId}";
    public static string RawLog(Repository r, int pipelineId, int stepId) => $"{Detail(r, pipelineId)}/steps/{stepId}/log";
    public static string Schedules(Repository r) => RepoUrls.Base(r) + "/pipelines/schedules";
    public static string Environments(Repository r) => RepoUrls.Base(r) + "/environments";
}

/// <summary>
/// Background worker that runs queued pipelines. Pipelines run concurrently; jobs across all pipelines share
/// <see cref="CiOptions.MaxConcurrentJobs"/> slots. Within a pipeline, <see cref="CiJobGraph"/> decides: a job starts
/// once every job it <c>needs</c> has passed (or failed with <c>allow_failure</c>), and is skipped if one didn't; manual
/// jobs and jobs for protected environments wait in <see cref="PipelineStatus.Manual"/> until someone runs them
/// (<see cref="PipelineService.PlayJobAsync"/>), which resumes the pipeline. Each job gets a fresh export of the commit's
/// tree plus the artifacts of its needs and its restored cache, and its steps run one after another in Docker containers
/// that mount it at <c>/workspace</c>; the first failing step fails the job. Afterwards its cache and artifacts are saved.
/// Logs stream into the database about once a second, with a <see cref="PipelineChanged"/> event each time.
/// </summary>
public class CiRunner(CiQueue queue, CiDocker docker, CiRefTracker refs, IDbContextFactory<AppDbContext> dbFactory,
    RepoStorage storage, LiveEvents live, IServiceScopeFactory scopes, IOptionsMonitor<CiOptions> options,
    Webhooks.WebhookDispatcher webhooks, Api.PublicBaseUrl baseUrl, CiArtifactStore artifacts, CiCacheStore caches,
    CiDeploymentRecorder deployments, ILogger<CiRunner> log, Automation.AutomationQueue automation) : BackgroundService
{
    private SemaphoreSlim jobSlots = null!;
    private readonly List<Task> active = [];
    private readonly Lock activeGate = new();

    private CiOptions Options => options.CurrentValue;

    // Default: one folder per server instance (keyed by content root), so instances never clear each other's workspaces.
    private string WorkRoot => Path.GetFullPath(Options.WorkDirectory is { Length: > 0 } w
        ? w : Path.Combine(Path.GetTempPath(), "gitbench-ci", CiDocker.InstanceKey));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        jobSlots = new SemaphoreSlim(Math.Max(1, Options.MaxConcurrentJobs));
        try { await RecoverAsync(stoppingToken); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "CI startup recovery failed"); }

        try
        {
            await foreach (var id in queue.Reader.ReadAllAsync(stoppingToken))
            {
                var task = Task.Run(() => RunPipelineSafeAsync(id, stoppingToken), CancellationToken.None);
                lock (activeGate)
                {
                    active.RemoveAll(t => t.IsCompleted);
                    active.Add(task);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }

        Task[] remaining;
        lock (activeGate) remaining = [.. active];
        await Task.WhenAll(remaining);
    }

    /// <summary>
    /// After a restart: pipelines that were running are marked Error (their containers are gone), queued ones run
    /// again, leftover containers and workspaces are removed, and the branch tips of every repo are recorded.
    /// </summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var running = await db.Pipelines.Where(p => p.Status == PipelineStatus.Running)
                .Include(p => p.Jobs).ThenInclude(j => j.Steps).ToListAsync(ct);
            var now = DateTime.UtcNow;
            foreach (var p in running)
            {
                p.Status = PipelineStatus.Error;
                p.Error = "Interrupted by a server restart. Re-run the pipeline.";
                p.FinishedAt = now;
                foreach (var j in p.Jobs.Where(j => !IsDone(j.Status)))
                {
                    j.Status = PipelineStatus.Canceled;
                    j.FinishedAt = now;
                    foreach (var s in j.Steps.Where(s => !IsDone(s.Status))) { s.Status = PipelineStatus.Canceled; s.FinishedAt = now; }
                }
            }
            await db.SaveChangesAsync(ct);
            if (running.Count > 0) log.LogWarning("Marked {Count} interrupted pipeline(s) as Error", running.Count);

            foreach (var id in await db.Pipelines.Where(p => p.Status == PipelineStatus.Queued).OrderBy(p => p.Id).Select(p => p.Id).ToListAsync(ct))
                queue.Enqueue(id);

            var repos = await db.Repositories.Select(r => new { r.Id, r.Project.Key, r.Slug, r.DefaultBranch }).ToListAsync(ct);
            foreach (var r in repos)
            {
                var path = storage.PathFor(r.Key, r.Slug);
                if (!GitOps.Exists(path)) continue;
                try { refs.Prime(r.Id, GitOps.ListBranches(path, r.DefaultBranch)); }
                catch (Exception ex) { log.LogWarning(ex, "Could not read branches of {Key}/{Slug}", r.Key, r.Slug); }
            }
        }

        try { if (Directory.Exists(WorkRoot)) Directory.Delete(WorkRoot, recursive: true); }
        catch (Exception ex) { log.LogDebug(ex, "Could not clear CI work directory {Dir}", WorkRoot); }
        if (Options.Enabled && await docker.IsAvailableAsync(ct)) await docker.RemoveStaleContainersAsync(ct);
    }

    private async Task RunPipelineSafeAsync(int id, CancellationToken stoppingToken)
    {
        try { await RunPipelineAsync(id, stoppingToken); }
        catch (Exception ex)
        {
            log.LogError(ex, "Pipeline {Id} crashed", id);
            try { await FinishPipelineAsync(id, PipelineStatus.Error, "Internal error: " + ex.Message); }
            catch (Exception ex2) { log.LogError(ex2, "Could not record failure of pipeline {Id}", id); }
        }
    }

    /// <summary>What every job of one pipeline run shares.</summary>
    private sealed record RunContext(Pipeline Pipeline, string RepoPath, CiResolvedVariables Variables,
        IReadOnlyDictionary<string, string> PipelineVariables, bool ProtectedBranch);

    private async Task RunPipelineAsync(int id, CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        queue.Register(id, cts); // before leaving Queued, so a cancel can always find us (see PipelineService.CancelAsync)
        var finished = false;
        try
        {
            Pipeline? p;
            await using (var db = await dbFactory.CreateDbContextAsync(stoppingToken))
            {
                // A pipeline can run more than once: after a manual job is played or a job is retried, it is queued again.
                // A fork PR pipeline waiting for a Maintainer's approval never starts (PipelineService.ApproveAsync enqueues it).
                var now = DateTime.UtcNow;
                var started = await db.Pipelines.Where(x => x.Id == id && x.Status == PipelineStatus.Queued && !x.AwaitingApproval)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, PipelineStatus.Running)
                        .SetProperty(x => x.StartedAt, x => x.StartedAt ?? now)
                        .SetProperty(x => x.FinishedAt, (DateTime?)null), stoppingToken);
                if (started == 0) return; // canceled while queued, or already handled
                p = await db.Pipelines.AsNoTracking().Include(x => x.Repository).ThenInclude(r => r.Project)
                    .Include(x => x.Jobs).ThenInclude(j => j.Steps)
                    .AsSplitQuery().FirstAsync(x => x.Id == id, stoppingToken);
            }
            Publish(p);

            if (!await docker.IsAvailableAsync(stoppingToken))
            {
                await CancelUnfinishedAsync(id, PipelineStatus.Skipped);
                await FinishPipelineAsync(id, PipelineStatus.Error, "Docker is not available on the server.");
                return;
            }

            var repoPath = storage.PathFor(p.Repository.Project.Key, p.Repository.Slug);
            // CI secret variables (decrypted once per pipeline; protected ones only for protected branches).
            CiResolvedVariables variables;
            bool protectedBranch;
            using (var scope = scopes.CreateScope())
            {
                // A fork PR pipeline gets no CI variables, and its Ref (refs/pull/{n}/head) is never treated as protected.
                variables = await scope.ServiceProvider.GetRequiredService<CiVariableService>()
                    .ResolveAsync(p.Repository.ProjectId, p.RepositoryId, p.Ref, p.IsForkPullRequest);
                protectedBranch = CiCacheStore.UsesProtectedScope(p,
                    !p.IsForkPullRequest && (await scope.ServiceProvider.GetRequiredService<BranchProtectionService>().ForBranchAsync(p.RepositoryId, p.Ref)).IsProtected);
            }
            // Nor any extra (schedule) variables: those belong to the upstream.
            var run = new RunContext(p, repoPath, variables,
                p.IsForkPullRequest ? new Dictionary<string, string>() : CiJobExtras.ParseVariables(p.VariablesJson), protectedBranch);

            var protectedEnvs = await deployments.ProtectedNamesAsync(p.RepositoryId);
            var jobs = p.Jobs.ToDictionary(j => j.Name);
            var states = p.Jobs.OrderBy(j => j.Order).ToDictionary(j => j.Name, j => StateOf(j, protectedEnvs));
            var runningJobs = new Dictionary<Task<PipelineStatus>, PipelineJob>();
            Task? wake = null;

            while (true)
            {
                var changed = false;
                foreach (var (name, action) in CiJobGraph.Next(states.Values, cts.IsCancellationRequested))
                {
                    var job = jobs[name];
                    switch (action)
                    {
                        case CiJobAction.Start:
                            states[name] = states[name] with { Status = PipelineStatus.Running };
                            runningJobs[RunJobAsync(run, job, cts.Token)] = job;
                            break;
                        case CiJobAction.WaitForManual:
                            await SetJobManualAsync(job.Id, states[name].NeedsApproval);
                            states[name] = states[name] with { Status = PipelineStatus.Manual };
                            // A protected environment: record the deployment as waiting for approval and tell the approvers.
                            if (states[name].NeedsApproval) await deployments.BlockAsync(p, job);
                            changed = true;
                            break;
                        default:
                            var final = action == CiJobAction.Skip ? PipelineStatus.Skipped : PipelineStatus.Canceled;
                            await SetJobAsync(job.Id, final, finished: true, steps: final);
                            if (states[name].Status == PipelineStatus.Manual) await deployments.CancelWaitingAsync(job.Id);
                            states[name] = states[name] with { Status = final };
                            changed = true;
                            break;
                    }
                }
                if (changed) { Publish(p); continue; } // a newly skipped job may skip more
                if (runningJobs.Count == 0) break;

                if (cts.IsCancellationRequested) wake = null;
                else wake ??= queue.WaitForWakeAsync(id, cts.Token);
                var waitOn = runningJobs.Keys.Cast<Task>().ToList();
                if (wake != null) waitOn.Add(wake);
                var done = await Task.WhenAny(waitOn);
                if (done == wake)
                {
                    // A manual job was played or a job retried while we run: pick up the jobs that changed.
                    wake = null;
                    if (cts.IsCancellationRequested) continue;
                    protectedEnvs = await deployments.ProtectedNamesAsync(p.RepositoryId);
                    await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
                    var fresh = await db.PipelineJobs.AsNoTracking().Include(j => j.Steps).Where(j => j.PipelineId == id).ToListAsync(CancellationToken.None);
                    foreach (var j in fresh.Where(j => states.ContainsKey(j.Name) && states[j.Name].Status != PipelineStatus.Running))
                    {
                        jobs[j.Name] = j;
                        states[j.Name] = StateOf(j, protectedEnvs);
                    }
                    continue;
                }
                var finishedJob = (Task<PipelineStatus>)done;
                states[runningJobs[finishedJob].Name] = states[runningJobs[finishedJob].Name] with { Status = await finishedJob };
                runningJobs.Remove(finishedJob);
            }

            var result = CiJobGraph.Result(states.Values, cts.IsCancellationRequested);
            await FinishPipelineAsync(id, result, stoppingToken.IsCancellationRequested ? "Interrupted by a server shutdown." : null);
            finished = !cts.IsCancellationRequested;
        }
        finally { queue.Unregister(id); }

        // A job may have been played just as we finished (after our last look at the database): run it.
        if (finished) await ResumeIfPlayedAsync(id);
    }

    private static CiJobState StateOf(PipelineJob j, HashSet<string> protectedEnvs) =>
        new(j.Name, j.NeedsList.ToList(), j.Status, j.IsManual, j.AllowFailure,
            NeedsApproval: j.RequiresApproval || j.EnvironmentName != null && protectedEnvs.Contains(j.EnvironmentName),
            Played: j.PlayedAt != null);

    /// <summary>Queues a finished pipeline again if one of its jobs is ready to start (played or retried).</summary>
    private async Task ResumeIfPlayedAsync(int id)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var p = await db.Pipelines.AsNoTracking().Include(x => x.Jobs).FirstOrDefaultAsync(x => x.Id == id);
            if (p == null || !p.IsFinished) return;
            var protectedEnvs = await deployments.ProtectedNamesAsync(p.RepositoryId);
            var ready = CiJobGraph.Next(p.Jobs.Select(j => StateOf(j, protectedEnvs)).ToList(), canceled: false)
                .Any(a => a.Action == CiJobAction.Start);
            if (!ready) return;
            var requeued = await db.Pipelines.Where(x => x.Id == id && x.Status != PipelineStatus.Queued && x.Status != PipelineStatus.Running)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, PipelineStatus.Queued));
            if (requeued > 0) queue.Enqueue(id);
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not check pipeline {Id} for played jobs", id); }
    }

    /// <summary>The CI_PIPELINE_SOURCE value for a trigger.</summary>
    public static string SourceName(PipelineTrigger t) => t switch
    {
        PipelineTrigger.Push => "push",
        PipelineTrigger.PullRequest => "pull_request",
        PipelineTrigger.Manual => "manual",
        PipelineTrigger.Retry => "retry",
        PipelineTrigger.Schedule => "schedule",
        _ => t.ToString().ToLowerInvariant(),
    };

    private async Task<PipelineStatus> RunJobAsync(RunContext run, PipelineJob job, CancellationToken ct)
    {
        var (p, repoPath, variables) = (run.Pipeline, run.RepoPath, run.Variables);
        await Task.Yield();
        try { await jobSlots.WaitAsync(ct); }
        catch (OperationCanceledException)
        {
            await SetJobAsync(job.Id, PipelineStatus.Canceled, finished: true, steps: PipelineStatus.Canceled);
            Publish(p);
            return PipelineStatus.Canceled;
        }

        var workspace = Path.Combine(WorkRoot, $"p{p.Id}-j{job.Id}-{Guid.NewGuid().ToString("N")[..8]}");
        var steps = job.Steps.OrderBy(s => s.Order).ToList();
        var extras = CiJobExtras.Parse(job.ExtrasJson);
        var result = PipelineStatus.Passed;
        int? deploymentId = null;
        PipelineStep? lastStarted = null;
        try
        {
            await SetJobAsync(job.Id, PipelineStatus.Running, started: true);
            Publish(p);
            deploymentId = await deployments.StartAsync(p, job, job.PlayedById ?? p.TriggeredById);

            var preNotes = new List<string>();
            try
            {
                await Task.Run(() => CiWorkspace.Export(repoPath, p.CommitSha, workspace), ct);
                // Artifacts of the needed jobs, then the cache (after the checkout, so the key can hash its files).
                preNotes.AddRange(await artifacts.RestoreNeedsAsync(p, job, workspace));
                if (extras.Cache is { } cache)
                    preNotes.AddRange(await caches.RestoreAsync(p.RepositoryId, CiCacheKey.Resolve(cache.Key, workspace), run.ProtectedBranch, workspace));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Could not prepare the workspace of job {Job} in pipeline {Id}", job.Id, p.Id);
                await FailStepAsync(steps[0], $"Could not prepare the workspace for commit {p.CommitSha[..7]}: {ex.Message}");
                result = PipelineStatus.Failed;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, Options.JobTimeoutMinutes)));
            foreach (var step in steps)
            {
                if (result != PipelineStatus.Passed)
                {
                    if (step.Status == PipelineStatus.Queued)
                        await SetStepAsync(step.Id, ct.IsCancellationRequested ? PipelineStatus.Canceled : PipelineStatus.Skipped, finished: true);
                    continue;
                }
                lastStarted = step;
                result = await RunStepAsync(run, job, step, workspace, timeout, ct, step == steps[0] ? preNotes : []);
                Publish(p);
            }

            // Cache (on success) and artifacts (per artifacts.when); notes go to the last step's log.
            var postNotes = new List<string>();
            try
            {
                if (result == PipelineStatus.Passed && extras.Cache is { } c)
                    postNotes.AddRange(await caches.SaveAsync(p.RepositoryId, CiCacheKey.Resolve(c.Key, workspace), run.ProtectedBranch, workspace, c.Paths));
                if (extras.Artifacts is { } a && CiArtifactStore.ShouldCollect(a.When, result == PipelineStatus.Passed) && Directory.Exists(workspace))
                {
                    var (ok, notes) = await artifacts.SaveAsync(p, job, workspace, a);
                    postNotes.AddRange(notes);
                    if (!ok && result == PipelineStatus.Passed)
                    {
                        postNotes.Add("[gitbench] The job failed because its artifacts could not be saved.");
                        result = PipelineStatus.Failed;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Saving the cache or artifacts of job {Job} failed", job.Id);
                postNotes.Add($"[gitbench] Could not save the cache or artifacts: {ex.Message}");
                if (extras.Artifacts != null && result == PipelineStatus.Passed) result = PipelineStatus.Failed;
            }
            if (postNotes.Count > 0 && lastStarted != null) await AppendLogAsync(lastStarted.Id, postNotes, run.Variables);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = PipelineStatus.Canceled;
            await CancelUnfinishedStepsAsync(job.Id);
        }
        finally
        {
            await SetJobAsync(job.Id, result, finished: true);
            if (deploymentId is { } d) await deployments.FinishAsync(d, result);
            Publish(p);
            jobSlots.Release();
            await DeleteWorkspaceAsync(workspace, job.Image);
        }
        return result;
    }

    /// <summary>Adds lines to a finished step's log (masked like the rest).</summary>
    private async Task AppendLogAsync(int stepId, IEnumerable<string> lines, CiResolvedVariables variables)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var log = await db.PipelineStepLogs.FirstOrDefaultAsync(l => l.StepId == stepId);
        if (log == null) return;
        var masker = new CiSecretMasker(variables.Values.Values);
        log.Text = masker.Mask(log.Text + "\n" + string.Join("\n", lines));
        await db.SaveChangesAsync();
    }

    private async Task<PipelineStatus> RunStepAsync(RunContext run, PipelineJob job, PipelineStep step, string workspace,
        CancellationTokenSource timeout, CancellationToken canceled, IReadOnlyList<string> preNotes)
    {
        var (p, variables) = (run.Pipeline, run.Variables);
        await SetStepAsync(step.Id, PipelineStatus.Running, started: true);
        var buffer = new LogBuffer(Options.MaxLogChars, new CiSecretMasker(variables.Values.Values));
        await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
        {
            if (!await db.PipelineStepLogs.AnyAsync(l => l.StepId == step.Id))
            {
                db.PipelineStepLogs.Add(new PipelineStepLog { StepId = step.Id });
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
        buffer.Append($"$ {step.Run.TrimEnd().Replace("\n", "\n$ ")}");
        buffer.Append($"[gitbench] image {job.Image}");
        foreach (var note in variables.Notes) buffer.Append(note);
        if (variables.Values.Count > 0) buffer.Append($"[gitbench] CI variables: {string.Join(", ", variables.Values.Keys.Order(StringComparer.Ordinal))}");
        if (run.PipelineVariables.Count > 0) buffer.Append($"[gitbench] Pipeline variables: {string.Join(", ", run.PipelineVariables.Keys.Order(StringComparer.Ordinal))}");
        if (job.EnvironmentName != null) buffer.Append($"[gitbench] Deploying to environment '{job.EnvironmentName}'.");
        foreach (var note in preNotes) buffer.Append(note);
        Publish(p);

        using var flushStop = new CancellationTokenSource();
        var flusher = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(flushStop.Token))
                    if (buffer.TakeDirty()) { await FlushLogAsync(step.Id, buffer); Publish(p); }
            }
            catch (OperationCanceledException) { }
        });

        // Secret CI variables, then the pipeline's own (schedule) variables, which win on a clash.
        var fileValues = new Dictionary<string, string>(variables.Values, StringComparer.Ordinal);
        foreach (var (k, v) in run.PipelineVariables) fileValues[k] = v;
        var envFile = fileValues.Count > 0 ? WriteEnvFile(step.Id, fileValues) : null;
        var env = new Dictionary<string, string>
        {
            ["CI"] = "true",
            ["GITBENCH_CI"] = "true",
            ["GITBENCH_PIPELINE_ID"] = p.Id.ToString(),
            ["GITBENCH_JOB"] = job.Name,
            ["GITBENCH_REF"] = p.Ref,
            ["GITBENCH_COMMIT_SHA"] = p.CommitSha,
            ["GITBENCH_REPOSITORY"] = $"{p.Repository.Project.Key}/{p.Repository.Slug}",
            ["GITBENCH_PIPELINE_SOURCE"] = SourceName(p.Trigger),
            ["CI_PIPELINE_SOURCE"] = SourceName(p.Trigger),
        };
        if (job.EnvironmentName != null) env["CI_ENVIRONMENT_NAME"] = job.EnvironmentName;
        if (job.EnvironmentUrl != null) env["CI_ENVIRONMENT_URL"] = job.EnvironmentUrl;
        var spec = new CiContainerSpec($"gitbench-ci-{step.Id}", job.Image, step.Run, workspace, env, envFile);

        PipelineStatus status;
        int? exit = null;
        try
        {
            exit = await docker.RunStepAsync(spec, buffer.Append, timeout.Token);
            if (canceled.IsCancellationRequested)
            {
                status = PipelineStatus.Canceled;
                buffer.Append("[gitbench] Canceled.");
            }
            else if (timeout.IsCancellationRequested)
            {
                status = PipelineStatus.Failed;
                buffer.Append($"[gitbench] Job timed out after {Options.JobTimeoutMinutes} minutes; the container was killed.");
            }
            else
            {
                status = exit == 0 ? PipelineStatus.Passed : PipelineStatus.Failed;
                if (exit != 0) buffer.Append($"[gitbench] Step failed with exit code {exit}." + (exit == 125 ? " (Docker could not start the container.)" : ""));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Step {Id} could not run", step.Id);
            buffer.Append($"[gitbench] Could not run docker: {ex.Message}");
            status = PipelineStatus.Error;
        }
        finally
        {
            if (envFile != null) DeleteEnvFile(envFile);
            flushStop.Cancel();
            await flusher;
        }

        await FlushLogAsync(step.Id, buffer);
        await SetStepAsync(step.Id, status, finished: true, exitCode: exit);
        if (canceled.IsCancellationRequested) throw new OperationCanceledException(canceled);
        return status == PipelineStatus.Passed ? PipelineStatus.Passed : PipelineStatus.Failed;
    }

    /// <summary>
    /// Writes the CI variables to a temp file for <c>docker run --env-file</c>: outside the mounted workspace, created
    /// with mode 600 (owner only) inside a mode-700 folder, and deleted as soon as the step ends (and at startup, with
    /// the work directory). Values are single-line (enforced by <see cref="CiVariableService"/>), as the format requires.
    /// </summary>
    private string WriteEnvFile(int stepId, IReadOnlyDictionary<string, string> values)
    {
        var dir = Path.Combine(WorkRoot, "env");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(dir, $"step-{stepId}-{Guid.NewGuid():N}.env");
        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var w = new StreamWriter(path, new UTF8Encoding(false), fileOptions);
        foreach (var (name, value) in values)
            if (value.IndexOfAny(['\r', '\n']) < 0) w.Write($"{name}={value}\n");
        return path;
    }

    private void DeleteEnvFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { log.LogWarning(ex, "Could not delete CI env file {Path}", path); }
    }

    private async Task DeleteWorkspaceAsync(string workspace, string image)
    {
        if (!Directory.Exists(workspace)) return;
        try { Directory.Delete(workspace, recursive: true); return; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Files written by a root container can be undeletable for the server user (Linux): delete them in Docker.
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await docker.CleanWorkspaceAsync(workspace, image, cts.Token);
        }
        try { Directory.Delete(workspace, recursive: true); }
        catch (Exception ex) { log.LogWarning(ex, "Could not delete CI workspace {Dir}", workspace); }
    }

    // ----------------------------------------------------------------- persistence

    private static bool IsDone(PipelineStatus s) => s is not (PipelineStatus.Queued or PipelineStatus.Running);

    private async Task FinishPipelineAsync(int id, PipelineStatus status, string? error)
    {
        Pipeline? p;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            p = await db.Pipelines.Include(x => x.Repository).ThenInclude(r => r.Project).FirstOrDefaultAsync(x => x.Id == id);
            if (p == null) return;
            p.Status = status;
            p.FinishedAt = DateTime.UtcNow;
            p.StartedAt ??= p.FinishedAt;
            if (error != null) p.Error = error;
            await db.SaveChangesAsync();
        }
        Publish(p);
        log.LogInformation("Pipeline {Id} ({Key}/{Slug} {Ref}) finished: {Status}", id, p.Repository.Project.Key, p.Repository.Slug, p.Ref, status);
        if (status == PipelineStatus.Manual) return; // blocked on a manual job: not completed yet, so no webhook or notification
        if (status is PipelineStatus.Failed or PipelineStatus.Error) await NotifyFailedAsync(p);
        RaiseWebhook(p);
        if (status is PipelineStatus.Passed or PipelineStatus.Failed or PipelineStatus.Error) // automation: "pipeline passed/failed" triggers
            automation.Enqueue(new(Automation.AutomationEventKind.PipelineCompleted, p.TriggeredById ?? 0) { PipelineId = p.Id, PipelinePassed = status == PipelineStatus.Passed });
    }

    private async Task SetJobManualAsync(int jobId, bool requiresApproval)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.PipelineJobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, PipelineStatus.Manual).SetProperty(j => j.RequiresApproval, requiresApproval));
    }

    /// <summary>Sends the <c>pipeline.completed</c> webhook event for a finished pipeline.</summary>
    private void RaiseWebhook(Pipeline p)
    {
        try
        {
            webhooks.Enqueue(p.Repository.ProjectId, Webhooks.WebhookEventNames.PipelineCompleted, new
            {
                pipeline = new
                {
                    id = p.Id,
                    status = p.Status.ToString().ToLowerInvariant(),
                    @ref = p.Ref,
                    commitSha = p.CommitSha,
                    pullRequestId = p.PullRequestId,
                    trigger = p.Trigger.ToString().ToLowerInvariant(),
                    error = p.Error,
                    createdAt = p.CreatedAt,
                    startedAt = p.StartedAt,
                    finishedAt = p.FinishedAt,
                    url = baseUrl.Value + PipelineUrls.Detail(p.Repository, p.Id),
                },
                repository = new { id = p.Repository.Id, slug = p.Repository.Slug, name = p.Repository.Name },
            }, p.TriggeredById);
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not raise pipeline.completed webhook for pipeline {Id}", p.Id); }
    }

    /// <summary>Tells whoever triggered <paramref name="p"/> that it failed (also used for pipelines created in Error).</summary>
    internal static async Task NotifyFailedAsync(IServiceScopeFactory scopes, Pipeline p, ILogger log)
    {
        if (p.TriggeredById is not { } userId) return;
        try
        {
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<ProjectAccess>().ActAsSystem();
            var title = $"{p.Repository.Slug} pipeline #{p.Id} on {p.Ref} ({(p.Status == PipelineStatus.Error ? "error" : "failed")})";
            await scope.ServiceProvider.GetRequiredService<NotificationService>()
                .OnPipelineFailedAsync(p.Repository.ProjectId, userId, title, PipelineUrls.Detail(p.Repository, p.Id));
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not notify about failed pipeline {Id}", p.Id); }
    }

    private Task NotifyFailedAsync(Pipeline p) => NotifyFailedAsync(scopes, p, log);

    private async Task SetJobAsync(int jobId, PipelineStatus status, bool started = false, bool finished = false, PipelineStatus? steps = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        var job = await db.PipelineJobs.Include(j => j.Steps).FirstAsync(j => j.Id == jobId);
        job.Status = status;
        if (started) job.StartedAt = now;
        if (finished) job.FinishedAt = now;
        if (steps is { } s)
            foreach (var step in job.Steps.Where(x => !IsDone(x.Status))) { step.Status = s; step.FinishedAt = now; }
        await db.SaveChangesAsync();
    }

    private async Task SetStepAsync(int stepId, PipelineStatus status, bool started = false, bool finished = false, int? exitCode = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        var step = await db.PipelineSteps.FirstAsync(s => s.Id == stepId);
        step.Status = status;
        if (started) step.StartedAt = now;
        if (finished) step.FinishedAt = now;
        if (exitCode != null) step.ExitCode = exitCode;
        await db.SaveChangesAsync();
    }

    private async Task FailStepAsync(PipelineStep step, string message)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        var s = await db.PipelineSteps.Include(x => x.Log).FirstAsync(x => x.Id == step.Id);
        s.Status = PipelineStatus.Failed;
        s.StartedAt = s.FinishedAt = now;
        if (s.Log == null) s.Log = new PipelineStepLog { StepId = s.Id, Text = "[gitbench] " + message };
        else s.Log.Text += "\n[gitbench] " + message;
        await db.SaveChangesAsync();
        step.Status = PipelineStatus.Failed;
    }

    private async Task CancelUnfinishedStepsAsync(int jobId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.PipelineSteps.Where(s => s.JobId == jobId && (s.Status == PipelineStatus.Queued || s.Status == PipelineStatus.Running))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, PipelineStatus.Canceled).SetProperty(x => x.FinishedAt, DateTime.UtcNow));
    }

    private async Task CancelUnfinishedAsync(int pipelineId, PipelineStatus status)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        // Approved jobs that never got to run: their waiting deployments end too.
        var unfinished = await db.PipelineJobs.Where(j => j.PipelineId == pipelineId && (j.Status == PipelineStatus.Queued || j.Status == PipelineStatus.Running))
            .Select(j => j.Id).ToListAsync();
        foreach (var jobId in unfinished) await deployments.CancelWaitingAsync(jobId);
        await db.PipelineSteps.Where(s => s.Job.PipelineId == pipelineId && (s.Status == PipelineStatus.Queued || s.Status == PipelineStatus.Running))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status).SetProperty(x => x.FinishedAt, now));
        await db.PipelineJobs.Where(j => j.PipelineId == pipelineId && (j.Status == PipelineStatus.Queued || j.Status == PipelineStatus.Running))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status).SetProperty(x => x.FinishedAt, now));
    }

    private async Task FlushLogAsync(int stepId, LogBuffer buffer)
    {
        var (text, truncated) = buffer.Snapshot();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.PipelineStepLogs.Where(l => l.StepId == stepId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Text, text).SetProperty(l => l.Truncated, truncated));
    }

    private void Publish(Pipeline p) => live.Publish(new PipelineChanged(p.Id, p.RepositoryId));

    /// <summary>Thread-safe step log capped at a size; past the cap, output is dropped and a note is added once.</summary>
    private sealed class LogBuffer(int maxChars, CiSecretMasker masker)
    {
        private readonly StringBuilder sb = new();
        private readonly Lock gate = new();
        private bool truncated, dirty;

        public void Append(string line)
        {
            lock (gate)
            {
                if (truncated) return;
                line = StripAnsi(line);
                if (sb.Length + line.Length + 1 > maxChars)
                {
                    truncated = true;
                    sb.Append($"\n[gitbench] Log truncated: it exceeded {maxChars / 1024} KB. Later output was discarded.\n");
                }
                else
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(line);
                }
                dirty = true;
            }
        }

        public bool TakeDirty()
        {
            lock (gate) { var d = dirty; dirty = false; return d; }
        }

        public (string Text, bool Truncated) Snapshot()
        {
            // Masking the whole text (not each chunk) also catches a secret value that arrived in pieces.
            lock (gate) { dirty = false; return (masker.Mask(sb.ToString()), truncated); }
        }

        private static string StripAnsi(string s) =>
            s.Contains('\u001b') ? System.Text.RegularExpressions.Regex.Replace(s, @"\u001b\[[0-9;?]*[ -/]*[@-~]", "") : s;
    }
}
