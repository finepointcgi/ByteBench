using System.Collections.Concurrent;
using System.Threading.Channels;
using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Ai.Triage;

/// <summary>Published on <see cref="LiveEvents"/> when a triage run changes status or one of its suggestions is accepted or dismissed.</summary>
public sealed record TriageChanged(int ProjectId, int IssueId) : LiveEvent;

/// <summary>
/// In-memory queue of triage runs waiting for the model (singleton). The run row (status Queued) is the durable record;
/// this only wakes <see cref="TriageWorker"/>. Runs execute one at a time, so a burst of new issues can never fan out into
/// parallel model calls.
/// </summary>
public class TriageQueue
{
    private readonly Channel<int> channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });
    public ChannelReader<int> Reader => channel.Reader;
    public void Enqueue(int runId) => channel.Writer.TryWrite(runId);
}

/// <summary>Background worker: drains <see cref="TriageQueue"/> one run at a time as trusted system work.</summary>
public class TriageWorker(TriageQueue queue, IServiceScopeFactory scopes, IDbContextFactory<AppDbContext> dbFactory, ILogger<TriageWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            // Runs interrupted by a restart: running ones failed, queued ones start again.
            await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
            var stale = await db.IssueTriageRuns.Where(r => r.Status == TriageRunStatus.Running || r.Status == TriageRunStatus.Queued).ToListAsync(stoppingToken);
            foreach (var r in stale.Where(r => r.Status == TriageRunStatus.Running))
            {
                r.Status = TriageRunStatus.Failed;
                r.Error = "Interrupted by a server restart. Ask for suggestions again.";
                r.CompletedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(stoppingToken);
            foreach (var r in stale.Where(r => r.Status == TriageRunStatus.Queued)) queue.Enqueue(r.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "AI triage worker startup failed"); }

        try
        {
            await foreach (var runId in queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<TriageRunner>().RunAsync(runId, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { log.LogError(ex, "AI triage run {RunId} failed", runId); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

/// <summary>
/// Queues triage for newly created issues. Issues are created in several places (web, REST API, service desk, import) and all
/// of them publish <see cref="IssueChanged"/>, so this listens there: an issue created in the last couple of minutes, in a
/// project with triage on, and without a run yet, gets one. Nothing about issue creation itself changes or waits.
/// </summary>
public class TriageCreationListener(LiveEvents live, IServiceScopeFactory scopes, ILogger<TriageCreationListener> log) : IHostedService
{
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<int, DateTime> seen = new();
    private IDisposable? subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        subscription = live.Subscribe<IssueChanged>(OnIssueChanged);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        subscription?.Dispose();
        return Task.CompletedTask;
    }

    private async Task OnIssueChanged(IssueChanged e)
    {
        try
        {
            foreach (var old in seen.Where(kv => kv.Value < DateTime.UtcNow - FreshFor).Select(kv => kv.Key).ToList()) seen.TryRemove(old, out _);
            if (!seen.TryAdd(e.IssueId, DateTime.UtcNow)) return;
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<TriageService>().QueueForNewIssueAsync(e.IssueId, FreshFor);
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not queue AI triage for issue {IssueId}", e.IssueId); }
    }
}

public static class TriageServiceCollectionExtensions
{
    /// <summary>Registers AI issue triage: model, options, service, runner, queue, worker and the new-issue listener.</summary>
    public static IServiceCollection AddAiTriage(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<TriageOptions>(config.GetSection("Ai:Triage"));
        services.AddSingleton<ITriageModel, AiClientTriageModel>();
        services.AddSingleton<TriageQueue>();
        services.AddScoped<TriageService>();
        services.AddScoped<TriageRunner>();
        services.AddHostedService<TriageWorker>();
        services.AddHostedService<TriageCreationListener>();
        return services;
    }
}
