using ByteBench.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Workspaces;

/// <summary>
/// Keeps workspaces from outliving their welcome. At startup it removes every workspace container this instance left behind and marks
/// the rows stopped (their tokens revoked): workspaces don't survive a restart. Then, every <c>ReaperIntervalSeconds</c>: stops idle
/// workspaces and those past their maximum lifetime, fails starts that hung, marks workspaces whose container vanished, and removes
/// orphan containers (labelled for this instance but without a live row).
/// </summary>
public sealed class WorkspaceReaper(IDbContextFactory<AppDbContext> dbFactory, IWorkspaceBackend backend, WorkspaceRuntime runtime,
    WorkspaceLifecycle lifecycle, IOptionsMonitor<WorkspaceOptions> options, ILogger<WorkspaceReaper> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            WorkerPulse.Report("workspace-reaper", true);
            try { await CleanAtStartupAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Workspace startup cleanup failed"); }
            runtime.ReaperLastRun = DateTime.UtcNow;
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, options.CurrentValue.ReaperIntervalSeconds)), stoppingToken);
                try { WorkerPulse.Tick("workspace-reaper"); await SweepAsync(stoppingToken); runtime.ReaperLastRun = DateTime.UtcNow; WorkerPulse.Ok("workspace-reaper"); }
                catch (Exception ex) when (ex is not OperationCanceledException) { WorkerPulse.Fail("workspace-reaper", ex); log.LogWarning(ex, "Workspace sweep failed"); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>A graceful shutdown ends every workspace too (their files go with them), so nothing keeps running unattended without ByteBench.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            await CleanAtStartupAsync(cts.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { log.LogDebug(ex, "Workspace cleanup at shutdown did not finish"); }
    }

    /// <summary>After a restart nothing is live: remove this instance's containers and close the rows (tokens revoked).</summary>
    public async Task CleanAtStartupAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var stale = await db.Workspaces.Where(w => w.State == WorkspaceState.Starting || w.State == WorkspaceState.Running || w.State == WorkspaceState.Stopping).ToListAsync(ct);
        foreach (var w in stale)
        {
            w.State = WorkspaceState.Stopped;
            w.StoppedAt = DateTime.UtcNow;
            w.StopReason = "server restarted";
            w.GitTokenHash = null;
        }
        if (stale.Count > 0) await db.SaveChangesAsync(ct);
        if (!await backend.IsAvailableAsync(ct)) return;
        var removed = 0;
        foreach (var c in await backend.ListManagedAsync(ct))
        {
            await backend.RemoveAsync(c.ContainerId, ct);
            removed++;
        }
        // Sidecar services and their private networks go too; the volumes stay (a stopped workspace can be resumed with its dependencies).
        foreach (var w in stale) await backend.RemoveAuxiliaryAsync(w.Id, ct);
        foreach (var id in runtime.SessionIds) runtime.FindSession(id)?.Shutdown();
        if (stale.Count > 0 || removed > 0) log.LogInformation("Workspaces: {Rows} workspace(s) closed and {Containers} container(s) removed (startup or shutdown cleanup)", stale.Count, removed);
    }

    /// <summary>One pass of the periodic checks (public so tests can run it without a timer).</summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var live = await db.Workspaces.Where(w => w.State == WorkspaceState.Starting || w.State == WorkspaceState.Running).ToListAsync(ct);

        foreach (var w in live)
        {
            if (runtime.Find(w.Id) is { } l && l.LastActivity > w.LastActivityAt) w.LastActivityAt = l.LastActivity; // activity lives in memory
        }
        await db.SaveChangesAsync(ct);

        foreach (var w in live)
        {
            if (w.State == WorkspaceState.Running && w.ExpiresAt <= now) await lifecycle.StopAsync(w.Id, "maximum lifetime reached", null);
            else if (w.State == WorkspaceState.Running && w.LastActivityAt.AddMinutes(Math.Max(1, w.IdleTimeoutMinutes)) <= now) await lifecycle.StopAsync(w.Id, "idle timeout", null);
            else if (w.State == WorkspaceState.Starting && w.CreatedAt.AddSeconds(Math.Max(60, options.CurrentValue.StartupTimeoutSeconds) * 2) <= now)
                await lifecycle.StopAsync(w.Id, "start did not finish", null);
        }

        if (!await backend.IsAvailableAsync(ct)) return;
        var containers = await backend.ListManagedAsync(ct);
        var liveNow = await db.Workspaces.AsNoTracking().Where(w => w.State == WorkspaceState.Starting || w.State == WorkspaceState.Running)
            .Select(w => new { w.Id, w.ContainerId, w.State }).ToListAsync(ct);
        var liveIds = liveNow.Select(w => w.Id).ToHashSet();
        foreach (var c in containers)
        {
            if (c.WorkspaceId is { } id && liveIds.Contains(id)) continue;
            log.LogInformation("Removing orphan workspace container {Id}", c.ContainerId[..Math.Min(12, c.ContainerId.Length)]);
            await backend.RemoveAsync(c.ContainerId, ct);
        }
        var present = containers.Select(c => c.ContainerId).ToHashSet();
        foreach (var w in liveNow.Where(w => w.State == WorkspaceState.Running && w.ContainerId != null && !present.Contains(w.ContainerId)))
            await lifecycle.StopAsync(w.Id, "container disappeared", null);

        await SweepVolumesAsync(db, now, ct);
        PruneSessions(db, now);
    }

    /// <summary>
    /// Volumes of stopped workspaces live for <c>Workspaces:VolumeRetentionDays</c>, then they are deleted (the workspace can no longer be resumed).
    /// A volume that belongs to no workspace (its row is gone: the repository or the user was deleted) is removed too.
    /// </summary>
    private async Task SweepVolumesAsync(AppDbContext db, DateTime now, CancellationToken ct)
    {
        var days = Math.Max(1, options.CurrentValue.VolumeRetentionDays);
        var expired = await db.Workspaces.Where(w => w.VolumesKept && (w.State == WorkspaceState.Stopped || w.State == WorkspaceState.Failed) && w.StoppedAt != null && w.StoppedAt < now.AddDays(-days)).ToListAsync(ct);
        foreach (var w in expired)
        {
            await lifecycle.RemoveVolumesQuietlyAsync(w.PublicId);
            w.VolumesKept = false;
            log.LogInformation("Removed the volumes of workspace {Id} (stopped more than {Days} days ago)", w.PublicId, days);
        }
        if (expired.Count > 0) await db.SaveChangesAsync(ct);

        // A start creates its volumes before it records them: anything still starting or running counts as known.
        var known = await db.Workspaces.AsNoTracking().Where(w => w.VolumesKept || w.State == WorkspaceState.Starting || w.State == WorkspaceState.Running || w.State == WorkspaceState.Stopping)
            .Select(w => w.Id).ToListAsync(ct);
        var knownSet = known.ToHashSet();
        var orphans = (await backend.ListVolumesAsync(ct)).Where(v => v.WorkspaceId is not { } id || !knownSet.Contains(id)).Select(v => v.Name).ToList();
        if (orphans.Count > 0)
        {
            log.LogInformation("Removing {Count} workspace volume(s) that belong to no workspace", orphans.Count);
            await backend.RemoveVolumesAsync(orphans, ct);
        }
    }

    /// <summary>Logs and task output of workspaces that stopped more than a day ago are dropped from memory.</summary>
    private void PruneSessions(AppDbContext db, DateTime now)
    {
        foreach (var id in runtime.SessionIds)
        {
            if (runtime.Find(id) != null) continue;
            var session = runtime.FindSession(id);
            if (session != null && session.CreatedAt < now.AddHours(-24) && !db.Workspaces.Any(w => w.Id == id && (w.State == WorkspaceState.Starting || w.State == WorkspaceState.Running)))
                runtime.DropSession(id);
        }
    }
}
