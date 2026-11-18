using GitBench.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GitBench.Services.Ci;

/// <summary>
/// Artifacts for the runner and the cleanup service (singleton, no permission checks): saving a job's
/// <c>artifacts.paths</c> as a zip after it runs, extracting the artifacts of a job's <c>needs</c> into its workspace,
/// and deleting expired ones. Reads for the UI go through <see cref="PipelineService"/>.
/// </summary>
public class CiArtifactStore(IDbContextFactory<AppDbContext> dbFactory, CiStorage storage, IOptionsMonitor<CiOptions> options,
    ILogger<CiArtifactStore> log)
{
    private long MaxBytes => Math.Max(1024, options.CurrentValue.MaxArtifactBytes);

    /// <summary>Whether artifacts are collected for a job that ended <paramref name="passed"/> or not.</summary>
    public static bool ShouldCollect(CiArtifactsWhen when, bool passed) =>
        when == CiArtifactsWhen.Always || (when == CiArtifactsWhen.OnSuccess) == passed;

    /// <summary>
    /// Zips the job's artifact paths and records them (replacing the job's previous artifacts). Returns log notes, and
    /// false when the artifacts could not be saved (e.g. over the size limit).
    /// </summary>
    public async Task<(bool Ok, List<string> Notes)> SaveAsync(Pipeline p, PipelineJob job, string workspace, CiArtifactsDefinition def)
    {
        var notes = new List<string>();
        var stored = CiStorage.NewStoredName(p.RepositoryId);
        var path = storage.ArtifactPath(stored);
        CiCollectResult r;
        try { r = await Task.Run(() => CiArtifacts.CollectToZip(workspace, def.Paths, path, MaxBytes)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log.LogWarning(ex, "Could not collect artifacts of job {Id}", job.Id);
            return (false, [$"[gitbench] Could not save artifacts: {ex.Message}"]);
        }
        notes.AddRange(r.Notes.Select(n => "[gitbench] Artifacts: " + n));
        if (!r.Ok) { notes.Add($"[gitbench] Artifacts not saved: {r.Error}."); return (false, notes); }
        if (r.FileCount == 0)
        {
            notes.Add($"[gitbench] Artifacts: no files matched {string.Join(", ", def.Paths)}.");
            return (true, notes);
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        await DeleteForJobAsync(db, job.Id);
        var now = DateTime.UtcNow;
        db.PipelineArtifacts.Add(new PipelineArtifact
        {
            JobId = job.Id, PipelineId = p.Id, RepositoryId = p.RepositoryId, StoredName = stored, SizeBytes = r.ZipBytes,
            FileCount = r.FileCount, CreatedAt = now, ExpiresAt = def.ExpireIn is { } e ? now + e : null,
        });
        await db.SaveChangesAsync();
        notes.Add($"[gitbench] Saved artifacts: {r.FileCount} file(s), {CiArtifacts.Format(r.ZipBytes)}"
            + (def.ExpireIn is { } ex2 ? $", expiring {(now + ex2):yyyy-MM-dd HH:mm} UTC." : ", never expiring."));
        return (true, notes);
    }

    /// <summary>Extracts the artifacts of the jobs <paramref name="job"/> needs into its workspace. Returns log notes.</summary>
    public async Task<List<string>> RestoreNeedsAsync(Pipeline p, PipelineJob job, string workspace)
    {
        var notes = new List<string>();
        var needs = job.NeedsList.ToList();
        if (needs.Count == 0) return notes;
        List<(string Job, PipelineArtifact A)> found;
        await using (var db = await dbFactory.CreateDbContextAsync())
            found = (await db.PipelineArtifacts.AsNoTracking().Include(a => a.Job)
                .Where(a => a.PipelineId == p.Id && needs.Contains(a.Job.Name)).ToListAsync())
                .Select(a => (a.Job.Name, a)).ToList();
        foreach (var (name, a) in found.OrderBy(x => needs.IndexOf(x.Job)))
        {
            var zip = storage.ArtifactPath(a.StoredName);
            if (!File.Exists(zip)) { notes.Add($"[gitbench] Artifacts of '{name}' are gone (expired or deleted)."); continue; }
            var r = await Task.Run(() => CiArtifacts.ExtractSafely(zip, workspace, MaxBytes));
            notes.Add($"[gitbench] Downloaded artifacts of '{name}': {r.FileCount} file(s).");
            notes.AddRange(r.Notes.Select(n => $"[gitbench] Artifacts of '{name}': {n}"));
        }
        return notes;
    }

    /// <summary>Deletes a job's artifacts (file and row), e.g. before it is retried.</summary>
    public async Task DeleteForJobAsync(int jobId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await DeleteForJobAsync(db, jobId);
    }

    private async Task DeleteForJobAsync(AppDbContext db, int jobId)
    {
        var old = await db.PipelineArtifacts.Where(a => a.JobId == jobId).ToListAsync();
        if (old.Count == 0) return;
        db.PipelineArtifacts.RemoveRange(old);
        await db.SaveChangesAsync();
        foreach (var a in old) CiArtifacts.TryDelete(storage.ArtifactPath(a.StoredName));
    }

    /// <summary>Deletes expired artifacts that aren't kept, and zip files no row refers to (left by deleted pipelines or repositories).</summary>
    public async Task<int> CleanupAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        var expired = await db.PipelineArtifacts.Where(a => a.ExpiresAt != null && a.ExpiresAt < now && !a.Keep).Take(1000).ToListAsync(ct);
        if (expired.Count > 0)
        {
            db.PipelineArtifacts.RemoveRange(expired);
            await db.SaveChangesAsync(ct);
            foreach (var a in expired) CiArtifacts.TryDelete(storage.ArtifactPath(a.StoredName));
            log.LogInformation("Deleted {Count} expired CI artifact(s)", expired.Count);
        }
        var known = (await db.PipelineArtifacts.Select(a => a.StoredName).ToListAsync(ct)).ToHashSet();
        await Task.Run(() => DeleteOrphans(storage.ArtifactsRoot, known), ct);
        return expired.Count;
    }

    /// <summary>Removes zips under <paramref name="root"/> that no row names and that are over an hour old (so in-flight saves survive).</summary>
    internal static void DeleteOrphans(string root, HashSet<string> known)
    {
        if (!Directory.Exists(root)) return;
        var cutoff = DateTime.UtcNow.AddHours(-1);
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.zip"))
            {
                var name = Path.GetFileName(dir) + "/" + Path.GetFileName(f);
                if (!known.Contains(name) && File.GetLastWriteTimeUtc(f) < cutoff) CiArtifacts.TryDelete(f);
            }
        }
    }
}
