using ByteBench.Data;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Sso.Ldap;

/// <summary>
/// Runs <see cref="LdapAuthService.SyncAllAsync"/> every <c>Authentication:Ldap:Sync:IntervalMinutes</c> (off at 0), and
/// on demand from <c>/admin/authentication</c>. Only one sync runs at a time.
/// </summary>
public sealed class LdapSyncJob(IServiceScopeFactory scopes, IOptionsMonitor<LdapOptions> options, ILogger<LdapSyncJob> log) : BackgroundService
{
    private readonly SemaphoreSlim running = new(1, 1);

    /// <summary>Runs a sync now (as <paramref name="actorId"/> for the audit log, else as the system). Null if one is already running.</summary>
    public async Task<DirectorySyncRun?> RunNowAsync(string trigger, int? actorId = null, CancellationToken ct = default)
    {
        if (!await running.WaitAsync(0, ct)) return null; // one is already running
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var access = scope.ServiceProvider.GetRequiredService<ProjectAccess>();
            if (actorId is { } id) access.ActAs(id);
            else access.ActAsSystem();
            return await scope.ServiceProvider.GetRequiredService<LdapAuthService>().SyncAllAsync(trigger, ct);
        }
        finally { running.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var o = options.CurrentValue;
            var minutes = o.Enabled ? o.Sync.IntervalMinutes : 0;
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(minutes > 0 ? minutes : 5), stoppingToken);
                if (minutes > 0 && options.CurrentValue.Enabled) await RunNowAsync("scheduled", ct: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                log.LogError(ex, "LDAP sync job failed");
            }
        }
    }
}
