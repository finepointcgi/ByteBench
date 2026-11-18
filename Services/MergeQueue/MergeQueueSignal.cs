using System.Collections.Immutable;
using System.Threading.Channels;
using GitBench.Data;
using GitBench.Services.Ci;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.MergeQueue;

/// <summary>
/// Singleton coordination for the merge queue: wakes <see cref="MergeQueueProcessor"/>, serializes processing (one pass at a
/// time across the app, whoever runs it) and remembers which pipelines and pull requests the active entries depend on, so
/// unrelated events don't wake the processor.
/// </summary>
public sealed class MergeQueueSignal
{
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private ImmutableHashSet<int> pipelines = [];
    private ImmutableHashSet<int> pullRequests = [];

    /// <summary>Held for the whole of a processing pass.</summary>
    public SemaphoreSlim ProcessLock { get; } = new(1, 1);

    /// <summary>Asks for a processing pass soon.</summary>
    public void Wake() => wake.Writer.TryWrite(true);

    /// <summary>Waits until <see cref="Wake"/> is called or <paramref name="timeout"/> passes.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await wake.Reader.ReadAsync(cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    internal void Watch(IEnumerable<int> pipelineIds, IEnumerable<int> pullRequestIds)
    {
        pipelines = [.. pipelineIds];
        pullRequests = [.. pullRequestIds];
    }

    public bool WatchesPipeline(int id) => pipelines.Contains(id);
    public bool WatchesPullRequest(int id) => pullRequests.Contains(id);
}

/// <summary>Cancels merge queue pipelines without a user (the queue's own clean-up), like <see cref="PipelineService.CancelAsync"/>.</summary>
internal static class MergeQueuePipelines
{
    public static async Task CancelAsync(IDbContextFactory<AppDbContext> dbFactory, CiQueue queue, LiveEvents live, int pipelineId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var p = await db.Pipelines.AsNoTracking().Where(x => x.Id == pipelineId).Select(x => new { x.RepositoryId, x.Status }).FirstOrDefaultAsync();
        if (p == null || p.Status is not (PipelineStatus.Queued or PipelineStatus.Running or PipelineStatus.Manual)) return;
        var now = DateTime.UtcNow;
        var canceled = await db.Pipelines.Where(x => x.Id == pipelineId && (x.Status == PipelineStatus.Queued || x.Status == PipelineStatus.Manual))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, PipelineStatus.Canceled).SetProperty(x => x.FinishedAt, now)
                .SetProperty(x => x.Error, "Canceled by the merge queue."));
        if (canceled > 0)
        {
            await db.PipelineSteps.Where(s => s.Job.PipelineId == pipelineId && s.Status == PipelineStatus.Queued)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, PipelineStatus.Canceled).SetProperty(x => x.FinishedAt, now));
            await db.PipelineJobs.Where(j => j.PipelineId == pipelineId && (j.Status == PipelineStatus.Queued || j.Status == PipelineStatus.Manual))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, PipelineStatus.Canceled).SetProperty(x => x.FinishedAt, now));
            live.Publish(new PipelineChanged(pipelineId, p.RepositoryId));
            return;
        }
        queue.TryCancelRunning(pipelineId);
    }
}
