using ByteBench.Data;
using ByteBench.Services.Git;
using Microsoft.Extensions.Caching.Memory;

namespace ByteBench.Services.Code;

/// <summary>Blame limits. Config section <c>Blame</c>.</summary>
public sealed class BlameOptions
{
    /// <summary>Files larger than this aren't blamed (the page and API say so).</summary>
    public long MaxFileBytes { get; set; } = GitOps.MaxBlobBytes;
    /// <summary>Files with more lines than this aren't blamed.</summary>
    public int MaxLines { get; set; } = 20_000;
    /// <summary>Approximate size, in characters of file text, of the blame result cache.</summary>
    public long CacheChars { get; set; } = 32L * 1024 * 1024;
}

/// <summary>Process-wide cache of blame results, keyed by (repository path, commit sha, file path). A commit never changes, so entries never go stale.</summary>
public sealed class BlameCache
{
    private readonly MemoryCache cache;
    public BlameCache(Microsoft.Extensions.Options.IOptions<BlameOptions> options) =>
        cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = Math.Max(1024 * 1024, options.Value.CacheChars) });

    public static string Key(string repoPath, string sha, string filePath) => repoPath + "\n" + sha + "\n" + filePath;

    public bool TryGet(string key, out BlameData? data) => cache.TryGetValue(key, out data);

    public void Set(string key, BlameData data) =>
        cache.Set(key, data, new MemoryCacheEntryOptions { Size = 1024 + data.Lines.Sum(l => l.Length + 8L) + data.Commits.Count * 512L, SlidingExpiration = TimeSpan.FromHours(1) });
}

/// <summary>
/// Blame for the code browser and the REST API. Needs the same access as the file view: the repository must be visible
/// to the acting user (<see cref="RepositoryService.GetRepositoryAsync"/>), otherwise nothing is returned.
/// </summary>
public sealed class BlameService(RepositoryService repos, BlameCache cache, Microsoft.Extensions.Options.IOptions<BlameOptions> options)
{
    /// <summary>
    /// Blames <paramref name="filePath"/> at <paramref name="rev"/> (null: the default branch). The rev is resolved to a commit first, so a
    /// branch that moves gets a fresh result while a sha is served from the cache. Never throws for unknown refs or files (status NotFound).
    /// </summary>
    public async Task<BlameData> BlameAsync(Repository repo, string? rev, string filePath)
    {
        var path = repos.PathOf(repo);
        rev = string.IsNullOrWhiteSpace(rev) ? repo.DefaultBranch : rev;
        filePath = (filePath ?? "").Trim('/');
        var o = options.Value;
        return await Task.Run(() =>
        {
            if (!GitOps.Exists(path)) return new BlameData(BlameStatus.NotFound, null, filePath, 0, [], [], []);
            var sha = GitOps.ResolveSha(path, rev);
            if (sha == null) return new BlameData(BlameStatus.NotFound, null, filePath, 0, [], [], []);
            var key = BlameCache.Key(path, sha, filePath);
            if (cache.TryGet(key, out var hit) && hit != null) return hit;
            var data = GitOps.Blame(path, sha, filePath, o.MaxFileBytes, o.MaxLines);
            if (data.Status == BlameStatus.Ok) cache.Set(key, data);
            return data;
        });
    }
}

public static class BlameServiceCollectionExtensions
{
    public static IServiceCollection AddBlame(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<BlameOptions>(config.GetSection("Blame"));
        services.AddSingleton<BlameCache>();
        services.AddScoped<BlameService>();
        return services;
    }
}
