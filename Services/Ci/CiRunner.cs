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
}

/// <summary>
/// Background worker that runs queued pipelines. Pipelines run concurrently; jobs across all pipelines share
/// <see cref="CiOptions.MaxConcurrentJobs"/> slots. Within a pipeline a job starts once every job it <c>needs</c> has
/// passed, and is skipped if one of them didn't. Each job gets a fresh export of the commit's tree, and its steps run
/// one after another in Docker containers that mount it at <c>/workspace</c>; the first failing step fails the job.
/// Logs stream into the database about once a second, with a <see cref="PipelineChanged"/> event each time.
/// </summary>
public class CiRunner(CiQueue queue, CiDocker docker, CiRefTracker refs, IDbContextFactory<AppDbContext> dbFactory,
    RepoStorage storage, LiveEvents live, IServiceScopeFactory scopes, IOptionsMonitor<CiOptions> options,
    Webhooks.WebhookDispatcher webhooks, Api.PublicBaseUrl baseUrl, ILogger<CiRunner> log, Automation.AutomationQueue automation) : BackgroundService
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

    private async Task RunPipelineAsync(int id, CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        queue.Register(id, cts); // before leaving Queued, so a cancel can always find us (see PipelineService.CancelAsync)
        try
        {
            Pipeline? p;
            await using (var db = await dbFactory.CreateDbContextAsync(stoppingToken))
            {
                var started = await db.Pipelines.Where(x => x.Id == id && x.Status == PipelineStatus.Queued && !x.AwaitingApproval)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, PipelineStatus.Running)
                        .SetProperty(x => x.StartedAt, DateTime.UtcNow), stoppingToken);
                if (started == 0) return; // canceled while queued, or already handled
                p = await db.Pipelines.AsNoTracking().Include(x => x.Repository).ThenInclude(r => r.Project)
                    .Include(x => x.Jobs).ThenInclude(j => j.Steps)
                    .FirstAsync(x => x.Id == id, stoppingToken);
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
            using (var scope = scopes.CreateScope())
                variables = await scope.ServiceProvider.GetRequiredService<CiVariableService>().ResolveAsync(p.Repository.ProjectId, p.RepositoryId, p.Ref, p.IsForkPullRequest);
            var status = p.Jobs.ToDictionary(j => j.Name, j => PipelineStatus.Queued);
            var pending = p.Jobs.OrderBy(j => j.Order).ToList();
            var runningJobs = new Dictionary<Task<PipelineStatus>, PipelineJob>();

            while (pending.Count > 0 || runningJobs.Count > 0)
            {
                var before = pending.Count;
                foreach (var job in pending.ToList())
                {
                    var needs = job.NeedsList.Select(n => status.GetValueOrDefault(n, PipelineStatus.Passed)).ToList();
                    if (cts.IsCancellationRequested || needs.Any(s => IsDone(s) && s != PipelineStatus.Passed))
                    {
                        var final = cts.IsCancellationRequested ? PipelineStatus.Canceled : PipelineStatus.Skipped;
                        await SetJobAsync(job.Id, final, finished: true, steps: final);
                        status[job.Name] = final;
                        pending.Remove(job);
                        Publish(p);
                    }
                    else if (needs.All(s => s == PipelineStatus.Passed))
                    {
                        pending.Remove(job);
                        status[job.Name] = PipelineStatus.Running;
                        runningJobs[RunJobAsync(p, job, repoPath, variables, cts.Token)] = job;
                    }
                }
                if (runningJobs.Count == 0)
                {
                    if (pending.Count == 0) break;
                    if (pending.Count == before)
                    {
                        // No progress possible (can't happen for a validated file, which has no cycles): give up on the rest.
                        foreach (var job in pending) { await SetJobAsync(job.Id, PipelineStatus.Skipped, finished: true, steps: PipelineStatus.Skipped); status[job.Name] = PipelineStatus.Skipped; }
                        break;
                    }
                    continue; // newly skipped jobs may unblock (skip) more pending ones
                }
                var done = await Task.WhenAny(runningJobs.Keys);
                status[runningJobs[done].Name] = await done;
                runningJobs.Remove(done);
            }

            var result = cts.IsCancellationRequested ? PipelineStatus.Canceled
                : status.Values.All(s => s == PipelineStatus.Passed) ? PipelineStatus.Passed
                : PipelineStatus.Failed;
            await FinishPipelineAsync(id, result, stoppingToken.IsCancellationRequested ? "Interrupted by a server shutdown." : null);
        }
        finally { queue.Unregister(id); }
    }

    private async Task<PipelineStatus> RunJobAsync(Pipeline p, PipelineJob job, string repoPath, CiResolvedVariables variables, CancellationToken ct)
    {
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
        var result = PipelineStatus.Passed;
        try
        {
            await SetJobAsync(job.Id, PipelineStatus.Running, started: true);
            Publish(p);

            try
            {
                await Task.Run(() => CiWorkspace.Export(repoPath, p.CommitSha, workspace), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Could not export {Sha} for pipeline {Id}", p.CommitSha, p.Id);
                await FailStepAsync(steps[0], $"Could not check out commit {p.CommitSha[..7]}: {ex.Message}");
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
                result = await RunStepAsync(p, job, step, workspace, variables, timeout, ct);
                Publish(p);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = PipelineStatus.Canceled;
            await CancelUnfinishedStepsAsync(job.Id);
        }
        finally
        {
            await SetJobAsync(job.Id, result, finished: true);
            Publish(p);
            jobSlots.Release();
            await DeleteWorkspaceAsync(workspace, job.Image);
        }
        return result;
    }

    private async Task<PipelineStatus> RunStepAsync(Pipeline p, PipelineJob job, PipelineStep step, string workspace,
        CiResolvedVariables variables, CancellationTokenSource timeout, CancellationToken canceled)
    {
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

        var envFile = variables.Values.Count > 0 ? WriteEnvFile(step.Id, variables.Values) : null;
        var spec = new CiContainerSpec($"gitbench-ci-{step.Id}", job.Image, step.Run, workspace, new Dictionary<string, string>
        {
            ["CI"] = "true",
            ["GITBENCH_CI"] = "true",
            ["GITBENCH_PIPELINE_ID"] = p.Id.ToString(),
            ["GITBENCH_JOB"] = job.Name,
            ["GITBENCH_REF"] = p.Ref,
            ["GITBENCH_COMMIT_SHA"] = p.CommitSha,
            ["GITBENCH_REPOSITORY"] = $"{p.Repository.Project.Key}/{p.Repository.Slug}",
        }, envFile);

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
        if (status is PipelineStatus.Failed or PipelineStatus.Error) await NotifyFailedAsync(p);
        RaiseWebhook(p);
        if (status is PipelineStatus.Passed or PipelineStatus.Failed or PipelineStatus.Error) // automation: "pipeline passed/failed" triggers
            automation.Enqueue(new(Automation.AutomationEventKind.PipelineCompleted, p.TriggeredById ?? 0) { PipelineId = p.Id, PipelinePassed = status == PipelineStatus.Passed });
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
