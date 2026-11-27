using System.Collections.Concurrent;
using ByteBench.Data;
using ByteBench.Services.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Workspaces;

/// <summary>
/// The wake-up signal for the run worker and the cancellation handles of the runs in flight (singleton). The queue itself is the database:
/// runs in state Queued, oldest first.
/// </summary>
public sealed class AiRunQueue
{
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly ConcurrentDictionary<int, CancellationTokenSource> running = new();

    /// <summary>Wakes the worker (a run was requested or finished).</summary>
    public void Signal()
    {
        try { signal.Release(); }
        catch (SemaphoreFullException) { /* already signalled */ }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => signal.WaitAsync(timeout, ct);

    /// <summary>The cancellation source of a run that is starting; <see cref="Cancel"/> trips it.</summary>
    public CancellationTokenSource Begin(int runId)
    {
        var cts = new CancellationTokenSource();
        running[runId] = cts;
        return cts;
    }

    public void End(int runId)
    {
        if (running.TryRemove(runId, out var cts)) cts.Dispose();
    }

    public void Cancel(int runId)
    {
        if (running.TryGetValue(runId, out var cts))
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* finished meanwhile */ }
        }
    }

    public int InFlight => running.Count;
}

/// <summary>
/// Starts queued AI runs while the concurrency cap and the workspace limits allow, oldest first, and wakes up when a run is requested or ends.
/// After a restart, runs that were in flight are failed (their workspaces were removed with the restart; each issue gets a comment) and queued ones go on.
/// </summary>
public sealed class AiRunWorker(AiRunQueue queue, AiRunExecutor executor, IDbContextFactory<AppDbContext> dbFactory, IOptionsMonitor<WorkspaceOptions> options,
    ILogger<AiRunWorker> log) : BackgroundService
{
    private readonly List<Task> inFlight = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            WorkerPulse.Report("ai-runs", options.CurrentValue.AiRuns.Enabled);
            try { await executor.RecoverAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "AI run recovery failed"); }
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    WorkerPulse.Tick("ai-runs");
                    await TickAsync(stoppingToken);
                    WorkerPulse.Ok("ai-runs");
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { WorkerPulse.Fail("ai-runs", ex); log.LogWarning(ex, "AI run worker tick failed"); }
                await queue.WaitAsync(TimeSpan.FromSeconds(Math.Max(1, options.CurrentValue.AiRuns.PollSeconds)), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>Starts as many queued runs as the limits allow; returns how many it started.</summary>
    public async Task<int> TickAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!o.AiRuns.Enabled) return 0;
        var started = 0;
        while (!ct.IsCancellationRequested)
        {
            int? claimed;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                var active = await db.AiRuns.CountAsync(r => r.Status == AiRunStatus.Starting || r.Status == AiRunStatus.Running, ct);
                if (active >= Math.Max(1, o.AiRuns.MaxConcurrent)) break;
                // The runs hold ordinary workspaces, so the per-user and server workspace limits apply to them too: wait for room, don't fail.
                var botId = await AiBot.EnsureAsync(db, ct);
                var live = await db.Workspaces.Where(w => w.State == WorkspaceState.Starting || w.State == WorkspaceState.Running || w.State == WorkspaceState.Stopping)
                    .Select(w => w.OwnerId).ToListAsync(ct);
                if (live.Count >= o.MaxTotal || live.Count(id => id == botId) >= o.MaxPerUser) break;

                var next = await db.AiRuns.Where(r => r.Status == AiRunStatus.Queued).OrderBy(r => r.Id).Select(r => (int?)r.Id).FirstOrDefaultAsync(ct);
                if (next is not { } id) break;
                var now = DateTime.UtcNow;
                var n = await db.AiRuns.Where(r => r.Id == id && r.Status == AiRunStatus.Queued)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, AiRunStatus.Starting).SetProperty(r => r.StartedAt, now), ct);
                claimed = n == 1 ? id : null;
            }
            if (claimed is not { } runId) continue;
            started++;
            var task = Task.Run(() => executor.ExecuteAsync(runId), CancellationToken.None);
            lock (inFlight) { inFlight.RemoveAll(t => t.IsCompleted); inFlight.Add(task); }
            _ = task.ContinueWith(_ => queue.Signal(), TaskScheduler.Default);
        }
        return started;
    }

    /// <summary>Completes when every run this worker started has finished (tests).</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (inFlight) pending = inFlight.Where(t => !t.IsCompleted).ToArray();
            if (pending.Length == 0) return;
            await Task.WhenAll(pending);
        }
    }
}
