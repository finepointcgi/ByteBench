using ByteBench.Data;
using ByteBench.Services.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Mirroring;

/// <summary>
/// Runs pull mirrors when they are due (every <c>Mirroring:PollSeconds</c> it syncs the enabled pull mirrors whose
/// <see cref="RepositoryMirror.NextSyncAt"/> has passed, one at a time) and the push mirrors queued in
/// <see cref="MirrorQueue"/>. Turned off with <c>Mirroring:Enabled=false</c> (syncs then only happen on demand).
/// </summary>
public sealed class MirrorWorker(IDbContextFactory<AppDbContext> dbFactory, MirrorSyncRunner runner, MirrorQueue queue,
    IOptionsMonitor<MirrorOptions> options, ILogger<MirrorWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.CurrentValue.Enabled)
        {
            log.LogInformation("Background mirror syncs are turned off (Mirroring:Enabled=false)");
            return;
        }
        await Task.WhenAll(PollAsync(stoppingToken), DrainQueueAsync(stoppingToken));
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                List<int> due;
                await using (var db = await dbFactory.CreateDbContextAsync(ct))
                {
                    var now = DateTime.UtcNow;
                    due = await db.RepositoryMirrors
                        .Where(m => m.Direction == MirrorDirection.Pull && m.Enabled && (m.NextSyncAt == null || m.NextSyncAt <= now))
                        .OrderBy(m => m.NextSyncAt).Select(m => m.Id).Take(20).ToListAsync(ct);
                }
                foreach (var id in due)
                {
                    if (ct.IsCancellationRequested) break;
                    await runner.SyncAsync(id, wait: false, ct); // already running (e.g. "Sync now"): skip; it reschedules itself
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Pull mirror poll failed"); }

            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, options.CurrentValue.PollSeconds)), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task DrainQueueAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var id in queue.ReadAllAsync(ct))
            {
                try { await runner.SyncAsync(id, wait: true, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { log.LogError(ex, "Push mirror {Id} failed", id); }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}

/// <summary>After a successful push (HTTP or SSH), queues the repository's enabled push mirrors.</summary>
public sealed class MirrorPushListener(IDbContextFactory<AppDbContext> dbFactory, MirrorQueue queue) : IGitPushListener
{
    public async Task OnPushAsync(int repositoryId, int userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var id in await db.RepositoryMirrors
                     .Where(m => m.RepositoryId == repositoryId && m.Direction == MirrorDirection.Push && m.Enabled)
                     .Select(m => m.Id).ToListAsync(ct))
            queue.Enqueue(id);
    }
}

public static class MirroringServiceCollectionExtensions
{
    /// <summary>Pull and push mirrors: settings, the URL guard, the sync runner and its background service.</summary>
    public static IServiceCollection AddMirroring(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<MirrorOptions>(config.GetSection("Mirroring"));
        services.AddSingleton<MirrorUrlGuard>();
        services.AddSingleton<MirrorCredentials>();
        services.AddSingleton<MirrorQueue>();
        services.AddSingleton<MirrorSyncRunner>();
        services.AddHostedService<MirrorWorker>();
        services.AddScoped<MirrorService>();
        services.AddScoped<IGitPushListener, MirrorPushListener>();
        return services;
    }
}
