using System.Threading.Channels;
using ByteBench.Data;
using ByteBench.Services.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.AutoMerge;

/// <summary>Wakes <see cref="AutoMergeProcessor"/> (coalesced: one pending wake is enough).</summary>
public sealed class AutoMergeSignal
{
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Wake() => wake.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await wake.Reader.ReadAsync(cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }
}

/// <summary>
/// Hosted worker for "merge when ready": evaluates armed pull requests once at startup, whenever something that can make a pull
/// request mergeable happens (a review or comment, a finished pipeline, a push to the repository, a changed protection rule,
/// another merge into the same project) and every <see cref="AutoMergeOptions.SweepSeconds"/> as a safety sweep.
/// </summary>
public sealed class AutoMergeProcessor(AutoMergeSignal signal, LiveEvents live, IServiceScopeFactory scopes, IDbContextFactory<AppDbContext> dbFactory,
    IOptionsMonitor<AutoMergeOptions> options, ILogger<AutoMergeProcessor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        WorkerPulse.Report("auto-merge", options.CurrentValue.Enabled);
        if (!options.CurrentValue.Enabled) return;
        using var prs = live.Subscribe<PullRequestChanged>(async e => { if (await AnyArmedAsync(a => a.PullRequest.Repository.ProjectId == e.ProjectId)) signal.Wake(); });
        using var pipelines = live.Subscribe<PipelineChanged>(OnPipelineChangedAsync);
        using var rules = live.Subscribe<BranchProtectionChanged>(async e => { if (await AnyArmedAsync(a => a.PullRequest.RepositoryId == e.RepositoryId)) signal.Wake(); });
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    WorkerPulse.Tick("auto-merge");
                    await using var scope = scopes.CreateAsyncScope();
                    scope.ServiceProvider.GetRequiredService<ProjectAccess>().ActAsSystem();
                    await scope.ServiceProvider.GetRequiredService<AutoMergeService>().ProcessAllAsync(stoppingToken);
                    WorkerPulse.Ok("auto-merge");
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { WorkerPulse.Fail("auto-merge", ex); log.LogError(ex, "Auto-merge processing failed"); }
                await signal.WaitAsync(TimeSpan.FromSeconds(Math.Max(1, options.CurrentValue.SweepSeconds)), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    // Pipelines publish a change for every log flush: only a finished pipeline of a repository with armed pull requests matters.
    private async Task OnPipelineChangedAsync(PipelineChanged e)
    {
        if (!await AnyArmedAsync(a => a.PullRequest.RepositoryId == e.RepositoryId)) return;
        await using var db = await dbFactory.CreateDbContextAsync();
        var status = await db.Pipelines.Where(p => p.Id == e.PipelineId).Select(p => (PipelineStatus?)p.Status).FirstOrDefaultAsync();
        if (status is not (PipelineStatus.Queued or PipelineStatus.Running)) signal.Wake();
    }

    private async Task<bool> AnyArmedAsync(System.Linq.Expressions.Expression<Func<PullRequestAutoMerge, bool>> where)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            return await db.PullRequestAutoMerges.Where(a => a.State == AutoMergeState.Armed).AnyAsync(where);
        }
        catch (Exception ex) { log.LogDebug(ex, "Auto-merge wake check failed"); return false; }
    }
}

/// <summary>After a push: new commits on an armed pull request's branch by someone else cancel it; then the worker re-evaluates.</summary>
public sealed class AutoMergePushListener(AutoMergeService autoMerge) : IGitPushListener
{
    public Task OnPushAsync(int repositoryId, int userId, CancellationToken ct) => autoMerge.OnPushAsync(repositoryId, userId, ct);
}

public static class AutoMergeServiceCollectionExtensions
{
    /// <summary>Registers "merge when ready": options (section <c>AutoMerge</c>), the service, the push listener and the background worker.</summary>
    public static IServiceCollection AddAutoMerge(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AutoMergeOptions>(config.GetSection("AutoMerge"));
        services.AddSingleton<AutoMergeSignal>();
        services.AddScoped<AutoMergeService>();
        services.AddScoped<IGitPushListener, AutoMergePushListener>();
        services.AddHostedService<AutoMergeProcessor>();
        return services;
    }
}
