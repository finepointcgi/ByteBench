using System.Collections.Concurrent;
using System.Threading.Channels;
using ByteBench.Data;
using ByteBench.Services.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Ai;

/// <summary>Why a review was requested. Manual requests bypass the repository's automatic-trigger mode (not its on/off switch).</summary>
public record AiReviewRequest(string Trigger, int? UserId = null)
{
    public const string Created = "created", Push = "push", Manual = "manual";
    public bool IsManual => Trigger == Manual;
}

/// <summary>
/// In-memory queue of pull requests waiting for AI review (singleton). Automatic requests for the same PR coalesce; a manual
/// request wins over an automatic one. Whether a request becomes a review at all (repository settings, backend, limits) is
/// decided later, by <see cref="AiReviewService.CreateQueuedAsync"/>. <see cref="AiReviewWorker"/> drains it, then runs the
/// queued <see cref="AiReview"/> rows one at a time.
/// </summary>
public class AiReviewQueue(IOptions<AiReviewOptions> options, ILogger<AiReviewQueue> log)
{
    private readonly Channel<int> channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<int> ready = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<int, AiReviewRequest> pending = new();
    private readonly ConcurrentDictionary<int, CancellationTokenSource> debounces = new();

    /// <summary>How long to wait after a push before reviewing, so a burst of pushes is reviewed once.</summary>
    public TimeSpan PushDebounce { get; set; } = TimeSpan.FromSeconds(Math.Max(0, options.Value.PushDebounceSeconds));

    /// <summary>Raised (on a background thread) with the PR id whenever a review of that PR changes status.</summary>
    public event Action<int>? Changed;

    /// <summary>Pull requests with an automatic request waiting to be turned into a review.</summary>
    public ChannelReader<int> Reader => channel.Reader;

    /// <summary>Ids of queued <see cref="AiReview"/> rows the worker should run.</summary>
    public ChannelReader<int> ReadyReader => ready.Reader;

    /// <summary>Automatic review of a newly created PR (does nothing unless the repository's settings ask for it).</summary>
    public bool Enqueue(int pullRequestId) => Enqueue(pullRequestId, new AiReviewRequest(AiReviewRequest.Created));

    public bool Enqueue(int pullRequestId, AiReviewRequest request)
    {
        pending.AddOrUpdate(pullRequestId, request, (_, old) => old.IsManual ? old : request);
        channel.Writer.TryWrite(pullRequestId);
        log.LogDebug("AI review requested for PR {Id} ({Trigger})", pullRequestId, request.Trigger);
        return true;
    }

    /// <summary>Hands a queued review row to the worker.</summary>
    public void Ready(int reviewId) => ready.Writer.TryWrite(reviewId);

    /// <summary>Enqueues after <see cref="PushDebounce"/>; another push to the same PR restarts the wait.</summary>
    public bool EnqueueDebounced(int pullRequestId)
    {
        var cts = new CancellationTokenSource();
        if (debounces.TryGetValue(pullRequestId, out var previous)) { previous.Cancel(); previous.Dispose(); }
        debounces[pullRequestId] = cts;
        _ = Task.Delay(PushDebounce, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            debounces.TryRemove(new KeyValuePair<int, CancellationTokenSource>(pullRequestId, cts));
            Enqueue(pullRequestId, new AiReviewRequest(AiReviewRequest.Push));
        }, TaskScheduler.Default);
        NotifyChanged(pullRequestId);
        return true;
    }

    /// <summary>True while a push-triggered review is waiting out its debounce.</summary>
    public bool IsWaiting(int pullRequestId) => debounces.ContainsKey(pullRequestId);

    /// <summary>Removes and returns the pending request for a PR read from <see cref="Reader"/> (null if already taken).</summary>
    public AiReviewRequest? Take(int pullRequestId) => pending.TryRemove(pullRequestId, out var r) ? r : null;

    public void NotifyChanged(int pullRequestId)
    {
        try { Changed?.Invoke(pullRequestId); }
        catch (Exception ex) { log.LogWarning(ex, "AI review change handler failed"); }
    }
}

/// <summary>
/// Background worker: creates the bot account at startup, turns automatic requests into <see cref="AiReview"/> rows, and
/// runs queued reviews one at a time. A failing review is logged and marked Failed with its error, which the PR page shows.
/// </summary>
public class AiReviewWorker(AiReviewQueue queue, IServiceScopeFactory scopes, IDbContextFactory<AppDbContext> dbFactory,
    ILogger<AiReviewWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
            await AiBot.EnsureAsync(db, stoppingToken);
            // Reviews interrupted by a restart: running ones failed, queued ones run again.
            var stale = await db.AiReviews.Where(r => r.Status == AiReviewStatus.Running || r.Status == AiReviewStatus.Queued).ToListAsync(stoppingToken);
            foreach (var r in stale.Where(r => r.Status == AiReviewStatus.Running))
            {
                r.Status = AiReviewStatus.Failed;
                r.Error = "Interrupted by a server restart. Review again.";
                r.CompletedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(stoppingToken);
            foreach (var r in stale.Where(r => r.Status == AiReviewStatus.Queued)) queue.Ready(r.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "AI review worker startup failed"); }

        WorkerPulse.Report("ai-review", true);
        var intake = IntakeAsync(stoppingToken);
        try
        {
            await foreach (var reviewId in queue.ReadyReader.ReadAllAsync(stoppingToken))
            {
                using var scope = scopes.CreateScope();
                // No signed-in user here: run as trusted system work, vouching only for the bot account as comment author.
                await using (var botDb = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(stoppingToken))
                    scope.ServiceProvider.GetRequiredService<ProjectAccess>().ActAsSystem(await AiBot.EnsureAsync(botDb, stoppingToken));
                var svc = scope.ServiceProvider.GetRequiredService<AiReviewService>();
                try
                {
                    WorkerPulse.Tick("ai-review");
                    await svc.RunAsync(reviewId, stoppingToken);
                    WorkerPulse.Ok("ai-review");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    WorkerPulse.Fail("ai-review", ex);
                    log.LogError(ex, "AI review {ReviewId} failed", reviewId);
                    try { await svc.MarkFailedAsync(reviewId, ex.Message); }
                    catch (Exception ex2) { log.LogError(ex2, "Could not record failure of AI review {ReviewId}", reviewId); }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        await intake;
    }

    private async Task IntakeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var prId in queue.Reader.ReadAllAsync(ct))
            {
                if (queue.Take(prId) is not { } request) continue; // coalesced with an earlier write
                try
                {
                    using var scope = scopes.CreateScope();
                    var intake = await scope.ServiceProvider.GetRequiredService<AiReviewService>().CreateQueuedAsync(prId, request, ct);
                    if (intake.ReviewId is { } reviewId) queue.Ready(reviewId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Could not queue AI review for PR {Id}", prId); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}

/// <summary>After a push, queues (debounced) AI review of open PRs whose source branch tip moved since their last review, for repositories set to review on push.</summary>
public class AiReviewPushListener(IDbContextFactory<AppDbContext> dbFactory, RepoStorage storage, AiReviewQueue queue)
    : IGitPushListener
{
    public async Task OnPushAsync(int repositoryId, int userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.RepositoryAiReviewSettings.AnyAsync(s => s.RepositoryId == repositoryId && s.Enabled && s.Mode == AiReviewMode.OnPush, ct)) return;
        var repo = await db.Repositories.Include(r => r.Project).FirstOrDefaultAsync(r => r.Id == repositoryId, ct);
        if (repo == null) return;
        var open = await db.PullRequests.Where(p => p.RepositoryId == repositoryId && p.State == PullRequestState.Open)
            .Select(p => new { p.Id, p.SourceBranch, p.IsCrossFork, p.Number }).ToListAsync(ct);
        if (open.Count == 0) return;

        var path = storage.PathFor(repo.Project.Key, repo.Slug);
        var tips = await Task.Run(() => open.ToDictionary(p => p.Id, p => GitOps.ResolveSha(path, p.IsCrossFork ? PullRequest.PullHeadRef(p.Number) : p.SourceBranch)), ct);
        foreach (var p in open)
        {
            if (tips[p.Id] is not { } tip) continue;
            var lastHead = await db.AiReviews.Where(r => r.PullRequestId == p.Id && r.Status != AiReviewStatus.Failed && r.Status != AiReviewStatus.Skipped)
                .OrderByDescending(r => r.Id).Select(r => r.HeadSha).FirstOrDefaultAsync(ct);
            if (lastHead != tip) queue.EnqueueDebounced(p.Id);
        }
    }
}

public static class AiReviewServiceCollectionExtensions
{
    /// <summary>Registers AI code review: settings, queue, service, background worker and push listener.</summary>
    public static IServiceCollection AddAiCodeReview(this IServiceCollection services, IConfiguration? config = null)
    {
        if (config != null) services.Configure<AiReviewOptions>(config.GetSection("Ai:Review"));
        else services.AddOptions<AiReviewOptions>();
        services.AddSingleton<AiReviewQueue>();
        services.AddScoped<AiReviewSettingsService>();
        services.AddScoped<AiReviewService>();
        services.AddHostedService<AiReviewWorker>();
        services.AddScoped<IGitPushListener, AiReviewPushListener>();
        return services;
    }
}
