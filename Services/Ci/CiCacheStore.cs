using GitBench.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GitBench.Services.Ci;

/// <summary>
/// Job caches (singleton, for the runner). A cache is a zip of the job's <c>cache.paths</c>, stored per repository,
/// resolved key and <b>scope</b>: pipelines on protected branches use the protected scope, all other pipelines (other
/// branches, pull requests) the unprotected one. The scopes never mix, so a branch anyone with PushCode can write to
/// can't plant files (cache poisoning) that a protected branch's pipeline (with protected secrets) would restore.
/// Restored before the first step, saved after the job passes, and evicted least-recently-used past
/// <see cref="CiOptions.CacheBytesPerRepository"/>.
/// </summary>
public class CiCacheStore(IDbContextFactory<AppDbContext> dbFactory, CiStorage storage, IOptionsMonitor<CiOptions> options,
    ILogger<CiCacheStore> log)
{
    private long MaxBytes => Math.Max(1024, options.CurrentValue.MaxCacheBytes);

    /// <summary>
    /// Whether <paramref name="p"/> uses the protected cache scope: only when its ref is a protected branch and it is not a
    /// fork pull request's pipeline (a fork's code must never read or write the protected scope, whatever its ref is called).
    /// </summary>
    public static bool UsesProtectedScope(Pipeline p, bool refIsProtected) => refIsProtected && !p.IsForkPullRequest;

    /// <summary>The entry a pipeline may restore: same repository, key and scope only.</summary>
    public async Task<CiCacheEntry?> FindAsync(int repositoryId, string key, bool protectedBranch)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.CiCacheEntries.AsNoTracking()
            .FirstOrDefaultAsync(c => c.RepositoryId == repositoryId && c.IsProtected == protectedBranch && c.Key == key);
    }

    /// <summary>Restores the cache for <paramref name="key"/> into the workspace. Returns log notes.</summary>
    public async Task<List<string>> RestoreAsync(int repositoryId, string key, bool protectedBranch, string workspace)
    {
        var scope = protectedBranch ? "protected" : "unprotected";
        var entry = await FindAsync(repositoryId, key, protectedBranch);
        var path = entry == null ? null : storage.CachePath(entry.StoredName);
        if (entry == null || !File.Exists(path)) return [$"[gitbench] Cache '{key}' ({scope} branches): not found."];
        var r = await Task.Run(() => CiArtifacts.ExtractSafely(path, workspace, MaxBytes));
        await using (var db = await dbFactory.CreateDbContextAsync())
            await db.CiCacheEntries.Where(c => c.Id == entry.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.LastUsedAt, DateTime.UtcNow));
        return [$"[gitbench] Restored cache '{key}' ({scope} branches): {r.FileCount} file(s).", .. r.Notes.Select(n => "[gitbench] Cache: " + n)];
    }

    /// <summary>Saves the workspace's cache paths under <paramref name="key"/>, replacing the previous cache, then evicts old ones.</summary>
    public async Task<List<string>> SaveAsync(int repositoryId, string key, bool protectedBranch, string workspace, IReadOnlyList<string> paths)
    {
        var stored = CiStorage.NewStoredName(repositoryId);
        var path = storage.CachePath(stored);
        CiCollectResult r;
        try { r = await Task.Run(() => CiArtifacts.CollectToZip(workspace, paths, path, MaxBytes)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not save cache {Key} of repository {Repo}", key, repositoryId);
            return [$"[gitbench] Could not save cache '{key}': {ex.Message}"];
        }
        var notes = r.Notes.Select(n => "[gitbench] Cache: " + n).ToList();
        if (!r.Ok) { notes.Add($"[gitbench] Cache '{key}' not saved: {r.Error}."); return notes; }
        if (r.FileCount == 0) { notes.Add($"[gitbench] Cache '{key}' not saved: no files matched {string.Join(", ", paths)}."); return notes; }

        string? replaced = null;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var now = DateTime.UtcNow;
            var entry = await db.CiCacheEntries.FirstOrDefaultAsync(c => c.RepositoryId == repositoryId && c.IsProtected == protectedBranch && c.Key == key);
            if (entry == null)
            {
                entry = new CiCacheEntry { RepositoryId = repositoryId, Key = key, IsProtected = protectedBranch };
                db.CiCacheEntries.Add(entry);
            }
            else replaced = entry.StoredName;
            entry.StoredName = stored;
            entry.SizeBytes = r.ZipBytes;
            entry.CreatedAt = entry.LastUsedAt = now;
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                // Another job saved the same key at the same moment: keep theirs.
                CiArtifacts.TryDelete(path);
                return [.. notes, $"[gitbench] Cache '{key}' was saved by another job at the same time; kept that one."];
            }
        }
        if (replaced != null) CiArtifacts.TryDelete(storage.CachePath(replaced));
        notes.Add($"[gitbench] Saved cache '{key}' ({(protectedBranch ? "protected" : "unprotected")} branches): {r.FileCount} file(s), {CiArtifacts.Format(r.ZipBytes)}.");
        await TrimAsync(repositoryId);
        return notes;
    }

    /// <summary>Evicts the repository's least recently used caches until it is within <see cref="CiOptions.CacheBytesPerRepository"/>.</summary>
    public async Task<int> TrimAsync(int repositoryId)
    {
        var limit = options.CurrentValue.CacheBytesPerRepository;
        await using var db = await dbFactory.CreateDbContextAsync();
        var entries = await db.CiCacheEntries.Where(c => c.RepositoryId == repositoryId).OrderByDescending(c => c.LastUsedAt).ToListAsync();
        long total = 0;
        var evict = new List<CiCacheEntry>();
        foreach (var e in entries)
        {
            total += e.SizeBytes;
            if (total > limit) evict.Add(e);
        }
        if (evict.Count == 0) return 0;
        db.CiCacheEntries.RemoveRange(evict);
        await db.SaveChangesAsync();
        foreach (var e in evict) CiArtifacts.TryDelete(storage.CachePath(e.StoredName));
        return evict.Count;
    }

    /// <summary>Removes cache files no row refers to (left by deleted repositories or interrupted saves).</summary>
    public async Task CleanupAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var known = (await db.CiCacheEntries.Select(c => c.StoredName).ToListAsync(ct)).ToHashSet();
        await Task.Run(() => CiArtifactStore.DeleteOrphans(storage.CacheRoot, known), ct);
    }
}
