using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ByteBench.Services.Ci;

/// <summary>
/// In-memory queue of pipeline ids waiting to run (singleton), plus the cancellation handles of running pipelines.
/// <see cref="CiRunner"/> drains it. Queued pipelines are also persisted (status Queued), so a restart requeues them.
/// </summary>
public class CiQueue
{
    private readonly Channel<int> channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<int, CancellationTokenSource> running = new();

    public ChannelReader<int> Reader => channel.Reader;

    public void Enqueue(int pipelineId) => channel.Writer.TryWrite(pipelineId);

    private readonly ConcurrentDictionary<int, SemaphoreSlim> wakes = new();

    internal void Register(int pipelineId, CancellationTokenSource cts) => running[pipelineId] = cts;
    internal void Unregister(int pipelineId)
    {
        running.TryRemove(pipelineId, out _);
        wakes.TryRemove(pipelineId, out _);
    }

    /// <summary>Completes when <see cref="Wake"/> is called for the pipeline (a job was played or retried while it runs).</summary>
    internal Task WaitForWakeAsync(int pipelineId, CancellationToken ct) =>
        wakes.GetOrAdd(pipelineId, _ => new SemaphoreSlim(0)).WaitAsync(ct);

    /// <summary>Tells a running pipeline to re-read its jobs. False if it isn't running here.</summary>
    public bool Wake(int pipelineId)
    {
        if (!running.ContainsKey(pipelineId)) return false;
        wakes.GetOrAdd(pipelineId, _ => new SemaphoreSlim(0)).Release();
        return true;
    }

    /// <summary>Signals a running pipeline to stop (its containers are killed). False if it isn't running here.</summary>
    public bool TryCancelRunning(int pipelineId)
    {
        if (!running.TryGetValue(pipelineId, out var cts)) return false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { return false; }
        return true;
    }
}
