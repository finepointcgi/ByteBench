using System.Collections.Concurrent;

namespace ByteBench.Services.Accounts;

/// <summary>
/// A small in-memory sliding-window counter for the anonymous account flows (reset and confirmation emails). It is always
/// on, independent of <c>RateLimiting:Enabled</c>, and per process: a restart forgets the counts.
/// </summary>
public sealed class AccountThrottle(TimeProvider? time = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> hits = new();
    private int sweeps;

    /// <summary>Counts one hit for <paramref name="key"/>; false when it already had <paramref name="limit"/> in the last <paramref name="window"/>.</summary>
    public bool TryHit(string key, int limit, TimeSpan window)
    {
        if (limit <= 0) return true;
        var now = clock.GetUtcNow();
        var q = hits.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        bool allowed;
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() >= window) q.Dequeue();
            allowed = q.Count < limit;
            if (allowed) q.Enqueue(now);
        }
        if (Interlocked.Increment(ref sweeps) % 500 == 0) Sweep(now, window);
        return allowed;
    }

    private void Sweep(DateTimeOffset now, TimeSpan window)
    {
        foreach (var (key, q) in hits)
        {
            bool empty;
            lock (q) { while (q.Count > 0 && now - q.Peek() >= window) q.Dequeue(); empty = q.Count == 0; }
            if (empty) hits.TryRemove(key, out _);
        }
    }
}
