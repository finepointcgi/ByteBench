namespace ByteBench.Services.Ci;

/// <summary>
/// <c>resource_group:</c> (singleton). Jobs of one repository that name the same group run one at a time, across pipelines, in
/// the order they asked: a deployment that is still running holds the next one back. The lock lives in this server's memory,
/// which is where every job runs; a restart ends all jobs, so nothing is left held.
/// </summary>
public sealed class CiResourceGroups
{
    private sealed class Gate { public readonly SemaphoreSlim Lock = new(1, 1); public int Users; }

    private readonly Dictionary<string, Gate> gates = [];
    private readonly Lock mutex = new();

    /// <summary>Waits for the group, then returns a handle that releases it when disposed. Throws if <paramref name="ct"/> is canceled while waiting.</summary>
    public async Task<IDisposable> AcquireAsync(int repositoryId, string group, CancellationToken ct)
    {
        var key = repositoryId + "/" + group;
        Gate gate;
        lock (mutex)
        {
            if (!gates.TryGetValue(key, out gate!)) gates[key] = gate = new Gate();
            gate.Users++;
        }
        try { await gate.Lock.WaitAsync(ct); }
        catch
        {
            Leave(key, gate);
            throw;
        }
        return new Handle(() => { gate.Lock.Release(); Leave(key, gate); });
    }

    /// <summary>True if a job holds the group now (for tests and the UI).</summary>
    public bool IsHeld(int repositoryId, string group)
    {
        lock (mutex) return gates.TryGetValue(repositoryId + "/" + group, out var g) && g.Lock.CurrentCount == 0;
    }

    private void Leave(string key, Gate gate)
    {
        lock (mutex)
            if (--gate.Users == 0) gates.Remove(key);
    }

    private sealed class Handle(Action release) : IDisposable
    {
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) release();
        }
    }
}
