using System.Collections.Concurrent;

namespace ByteBench.Services.ServiceDesk;

/// <summary>
/// In-memory sliding one-hour windows, keyed by sender address (<c>mail:</c>) or client IP (<c>form:</c>). Resets on
/// restart, which is fine for its purpose: stopping a mail loop or a form flood from filing hundreds of issues.
/// </summary>
public sealed class ServiceDeskRateLimiter(TimeProvider clock)
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> hits = new();

    /// <summary>Records a hit and returns true, or returns false (recording nothing) when <paramref name="key"/> already had <paramref name="limit"/> this hour.</summary>
    public bool TryAcquire(string key, int limit)
    {
        if (limit <= 0) return true;
        var now = clock.GetUtcNow();
        if (hits.Count > 10_000) Prune(now);
        var q = hits.GetOrAdd(key.ToLowerInvariant(), _ => new Queue<DateTimeOffset>());
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() >= Window) q.Dequeue();
            if (q.Count >= limit) return false;
            q.Enqueue(now);
            return true;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, q) in hits)
            lock (q)
                if (q.Count == 0 || now - q.Last() >= Window) hits.TryRemove(key, out _);
    }
}
