using ByteBench.Data;
using ByteBench.Services.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.MergeQueue;

/// <summary>
/// Hosted service that runs <see cref="MergeQueueEngine.ProcessAllAsync"/>: once at startup (which picks up every queue
/// persisted in the database, so a restart resumes where it stopped), whenever <see cref="MergeQueueSignal"/> is woken
/// (queue actions, a watched pipeline finishing, a watched PR changing, a push to a repository with a queue) and every
/// <see cref="MergeQueueOptions.PollSeconds"/> otherwise (timeouts, missed events).
/// </summary>
public sealed class MergeQueueProcessor(MergeQueueSignal signal, LiveEvents live, IServiceScopeFactory scopes,
    IDbContextFactory<AppDbContext> dbFactory, IOptionsMonitor<MergeQueueOptions> options, ILogger<MergeQueueProcessor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        if (!options.CurrentValue.Enabled) return;
        using var pipelines = live.Subscribe<PipelineChanged>(OnPipelineChangedAsync);
        using var prs = live.Subscribe<PullRequestChanged>(e =>
        {
            if (signal.WatchesPullRequest(e.PullRequestId)) signal.Wake();
            return Task.CompletedTask;
        });
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    scope.ServiceProvider.GetRequiredService<ProjectAccess>().ActAsSystem();
                    await scope.ServiceProvider.GetRequiredService<MergeQueueEngine>().ProcessAllAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Merge queue processing failed"); }
                await signal.WaitAsync(TimeSpan.FromSeconds(Math.Max(1, options.CurrentValue.PollSeconds)), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    // Pipelines publish a change for every log flush: only a finished pipeline of an active entry matters.
    private async Task OnPipelineChangedAsync(PipelineChanged e)
    {
        if (!signal.WatchesPipeline(e.PipelineId)) return;
        await using var db = await dbFactory.CreateDbContextAsync();
        var status = await db.Pipelines.Where(p => p.Id == e.PipelineId).Select(p => (PipelineStatus?)p.Status).FirstOrDefaultAsync();
        if (status is not (PipelineStatus.Queued or PipelineStatus.Running)) signal.Wake();
    }
}

/// <summary>After a push to a repository with active queue entries: wakes the queue (target moved → rebuild; PR updated → drop).</summary>
public sealed class MergeQueuePushListener(IDbContextFactory<AppDbContext> dbFactory, MergeQueueSignal signal) : IGitPushListener
{
    public async Task OnPushAsync(int repositoryId, int userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.MergeQueueEntries.AnyAsync(e => e.RepositoryId == repositoryId
                && (e.State == MergeQueueEntryState.Queued || e.State == MergeQueueEntryState.Testing || e.State == MergeQueueEntryState.Passed), ct))
            signal.Wake();
    }
}

public static class MergeQueueServiceCollectionExtensions
{
    /// <summary>Registers the merge queue: options (section <c>MergeQueue</c>), services, the push listener and the background processor.</summary>
    public static IServiceCollection AddMergeQueue(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<MergeQueueOptions>(config.GetSection("MergeQueue"));
        services.AddSingleton<MergeQueueSignal>();
        services.AddScoped<MergeQueueService>();
        services.AddScoped<MergeQueueEngine>();
        services.AddScoped<IGitPushListener, MergeQueuePushListener>();
        services.AddHostedService<MergeQueueProcessor>();
        return services;
    }
}
