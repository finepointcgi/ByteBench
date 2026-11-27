namespace ByteBench.Desktop.Core;

/// <summary>
/// API tokens per profile. The app implements this over the OS secure store (Keychain on macOS, DPAPI-backed storage on Windows).
/// Implementations must NOT fall back to plain text: if no secure store exists, <see cref="Available"/> is false and Set returns false.
/// </summary>
public interface ITokenStore
{
    bool Available { get; }
    Task<string?> GetAsync(string profileId, CancellationToken ct = default);
    Task<bool> SetAsync(string profileId, string token, CancellationToken ct = default);
    Task DeleteAsync(string profileId, CancellationToken ct = default);
}

/// <summary>Memory-only store (tests, and the fallback when a platform has no secure store: the token then lasts for this run only).</summary>
public sealed class InMemoryTokenStore : ITokenStore
{
    private readonly Dictionary<string, string> _map = [];
    public bool Available => true;

    public Task<string?> GetAsync(string profileId, CancellationToken ct = default)
    {
        lock (_map) return Task.FromResult(_map.GetValueOrDefault(profileId));
    }

    public Task<bool> SetAsync(string profileId, string token, CancellationToken ct = default)
    {
        var t = token.Trim();
        if (t.Length == 0) return Task.FromResult(false);
        lock (_map) _map[profileId] = t;
        return Task.FromResult(true);
    }

    public Task DeleteAsync(string profileId, CancellationToken ct = default)
    {
        lock (_map) _map.Remove(profileId);
        return Task.CompletedTask;
    }
}
