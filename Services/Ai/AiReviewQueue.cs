using System.Collections.Concurrent;
using System.Threading.Channels;
using GitBench.Data;
using GitBench.Services.Git;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Ai;

/// <summary>Why a review was requested. Manual requests bypass the per-project toggle and the "same head" check.</summary>
public record AiReviewRequest(string Trigger, int? UserId = null)
{
    public const string Created = "created", Push = "push", Manual = "manual";
    public bool IsManual => Trigger == Manual;
}

/// <summary>
/// In-memory queue of pull requests waiting for AI review (singleton). Requests for the same PR coalesce; a manual
/// request wins over an automatic one. Does nothing when AI isn't configured. <see cref="AiReviewWorker"/> drains it.
/// </summary>
public class AiReviewQueue(AiClient ai, ILogger<AiReviewQueue> log)
{
    private readonly Channel<int> channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<int, AiReviewRequest> pending = new();
    private readonly ConcurrentDictionary<int, CancellationTokenSource> debounces = new();

    /// <summary>How long to wait after a push before reviewing, so a burst of pushes is reviewed once.</summary>
    public TimeSpan PushDebounce { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Raised (on a background thread) with the PR id whenever a review of that PR changes status.</summary>
    public event Action<int>? Changed;

    public ChannelReader<int> Reader => channel.Reader;

    /// <summary>Automatic review of a newly created PR. Returns false (and enqueues nothing) when AI isn't configured.</summary>
    public bool Enqueue(int pullRequestId) => Enqueue(pullRequestId, new AiReviewRequest(AiReviewRequest.Created));

    public bool Enqueue(int pullRequestId, AiReviewRequest request)
    {
        if (!ai.IsConfigured) return false;
        pending.AddOrUpdate(pullRequestId, request, (_, old) => old.IsManual ? old : request);
        channel.Writer.TryWrite(pullRequestId);
        log.LogInformation("AI review requested for PR {Id} ({Trigger})", pullRequestId, request.Trigger);
        return true;
    }

    /// <summary>"Ask GitBench AI to review" / "Re-run AI review".</summary>
    public bool EnqueueManual(int pullRequestId, int userId) =>
        Enqueue(pullRequestId, new AiReviewRequest(AiReviewRequest.Manual, userId));

    /// <summary>Enqueues after <see cref="PushDebounce"/>; another push to the same PR restarts the wait.</summary>
    public bool EnqueueDebounced(int pullRequestId)
    {
        if (!ai.IsConfigured) return false;
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
/// Background worker: creates the bot account at startup, turns queued requests into <see cref="AiReview"/> rows, and
/// runs them one at a time. A failing review is logged and marked Failed with its error, which the PR page shows.
/// </summary>
public class AiReviewWorker(AiReviewQueue queue, IServiceScopeFactory scopes, IDbContextFactory<AppDbContext> dbFactory,
    ILogger<AiReviewWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var work = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
            await AiBot.EnsureAsync(db, stoppingToken);
            // Reviews interrupted by a restart: running ones failed, queued ones run again.
            var stale = await db.AiReviews.Where(r => r.Status == AiReviewStatus.Running || r.Status == AiReviewStatus.Queued).ToListAsync(stoppingToken);
            foreach (var r in stale.Where(r => r.Status == AiReviewStatus.Running))
            {
                r.Status = AiReviewStatus.Failed;
                r.Error = "Interrupted by a server restart. Re-run the review.";
                r.CompletedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(stoppingToken);
            foreach (var r in stale.Where(r => r.Status == AiReviewStatus.Queued)) work.Writer.TryWrite(r.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "AI review worker startup failed"); }

        var intake = IntakeAsync(work.Writer, stoppingToken);
        try
        {
            await foreach (var reviewId in work.Reader.ReadAllAsync(stoppingToken))
            {
                using var scope = scopes.CreateScope();
                // No signed-in user here: run as trusted system work, vouching only for the bot account as comment author.
                await using (var botDb = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(stoppingToken))
                    scope.ServiceProvider.GetRequiredService<ProjectAccess>().ActAsSystem(await AiBot.EnsureAsync(botDb, stoppingToken));
                var svc = scope.ServiceProvider.GetRequiredService<AiReviewService>();
                try
                {
                    await svc.RunAsync(reviewId, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    log.LogError(ex, "AI review {ReviewId} failed", reviewId);
                    try { await svc.MarkFailedAsync(reviewId, ex.Message); }
                    catch (Exception ex2) { log.LogError(ex2, "Could not record failure of AI review {ReviewId}", reviewId); }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        await intake;
    }

    private async Task IntakeAsync(ChannelWriter<int> work, CancellationToken ct)
    {
        try
        {
            await foreach (var prId in queue.Reader.ReadAllAsync(ct))
            {
                if (queue.Take(prId) is not { } request) continue; // coalesced with an earlier write
                try
                {
                    using var scope = scopes.CreateScope();
                    var id = await scope.ServiceProvider.GetRequiredService<AiReviewService>().CreateQueuedAsync(prId, request, ct);
                    if (id is { } reviewId) work.TryWrite(reviewId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Could not queue AI review for PR {Id}", prId); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}

/// <summary>After a push, queues (debounced) AI review of open PRs whose source branch tip moved since their last review.</summary>
public class AiReviewPushListener(IDbContextFactory<AppDbContext> dbFactory, RepoStorage storage, AiClient ai, AiReviewQueue queue)
    : IGitPushListener
{
    public async Task OnPushAsync(int repositoryId, int userId, CancellationToken ct)
    {
        if (!ai.IsConfigured) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var repo = await db.Repositories.Include(r => r.Project).FirstOrDefaultAsync(r => r.Id == repositoryId, ct);
        if (repo == null || !repo.Project.AiReviewEnabled) return;
        var open = await db.PullRequests.Where(p => p.RepositoryId == repositoryId && p.State == PullRequestState.Open)
            .Select(p => new { p.Id, p.SourceBranch, p.IsCrossFork, p.Number }).ToListAsync(ct);
        if (open.Count == 0) return;

        var path = storage.PathFor(repo.Project.Key, repo.Slug);
        var tips = await Task.Run(() => open.ToDictionary(p => p.Id, p => GitOps.ResolveSha(path, p.IsCrossFork ? PullRequest.PullHeadRef(p.Number) : p.SourceBranch)), ct);
        foreach (var p in open)
        {
            if (tips[p.Id] is not { } tip) continue;
            var lastHead = await db.AiReviews.Where(r => r.PullRequestId == p.Id)
                .OrderByDescending(r => r.Id).Select(r => r.HeadSha).FirstOrDefaultAsync(ct);
            if (lastHead != tip) queue.EnqueueDebounced(p.Id);
        }
    }
}

public static class AiReviewServiceCollectionExtensions
{
    /// <summary>Registers AI code review: queue, service, background worker and push listener.</summary>
    public static IServiceCollection AddAiCodeReview(this IServiceCollection services)
    {
        services.AddSingleton<AiReviewQueue>();
        services.AddScoped<AiReviewService>();
        services.AddHostedService<AiReviewWorker>();
        services.AddScoped<IGitPushListener, AiReviewPushListener>();
        return services;
    }
}
