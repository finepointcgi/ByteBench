using System.IO.Compression;
using ByteBench.Data;
using ByteBench.Services.Attachments;
using ByteBench.Services.Audit;
using ByteBench.Services.Ci;
using ByteBench.Services.Git;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Releases;

public sealed record AttachResult(int Attached, IReadOnlyList<string> Names, IReadOnlyList<string> Notes);

/// <summary>
/// Copies files out of the CI artifacts of the pipeline(s) that built a release's commit onto the release as assets, when the
/// path inside the artifact zip matches one of the repository's <see cref="ReleaseAutoAttachRule"/> patterns. There are no
/// pipelines on tags: "the pipeline of a tag" is every passed pipeline whose commit is the tag's commit, on any branch.
/// Runs when a release is published and when a matching pipeline passes later. No permission checks here (callers do them);
/// the zip is read entry by entry, never extracted to disk, with the release asset size limit applied to the real bytes read.
/// </summary>
public class ReleaseAutoAttachService(IDbContextFactory<AppDbContext> dbFactory, RepositoryService repos, CiStorage ciStorage,
    AttachmentStorage files, IConfiguration config, AuditLog audit, ILogger<ReleaseAutoAttachService> log)
{
    public const int MaxPatterns = 20;
    public const int MaxEntriesScanned = 20_000;
    public const long MaxBytesPerAttach = 4L * 1024 * 1024 * 1024;

    private long MaxAssetBytes => config.GetValue<long?>("Releases:MaxAssetBytes") is > 0 and var v ? v : ReleaseService.DefaultMaxAssetBytes;

    public static List<string> ParsePatterns(string? text) =>
        (text ?? "").Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Replace('\\', '/')).Where(p => p.Length > 0 && CiArtifacts.ValidatePattern(p) == null).Distinct().Take(MaxPatterns).ToList();

    public async Task<ReleaseAutoAttachRule?> GetRuleAsync(int repositoryId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ReleaseAutoAttachRules.AsNoTracking().FirstOrDefaultAsync(r => r.RepositoryId == repositoryId);
    }

    /// <summary>Saves the rule (no permission check). Invalid patterns are an error rather than silently dropped.</summary>
    public async Task SaveRuleAsync(int repositoryId, bool enabled, string? patterns)
    {
        var lines = (patterns ?? "").Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => p.Replace('\\', '/')).ToList();
        if (lines.Count > MaxPatterns) throw new InvalidOperationException($"At most {MaxPatterns} patterns.");
        foreach (var l in lines)
            if (CiArtifacts.ValidatePattern(l) is { } problem) throw new InvalidOperationException($"Pattern '{l}' {problem}");
        await using var db = await dbFactory.CreateDbContextAsync();
        var rule = await db.ReleaseAutoAttachRules.FirstOrDefaultAsync(r => r.RepositoryId == repositoryId);
        if (rule == null) db.ReleaseAutoAttachRules.Add(rule = new ReleaseAutoAttachRule { RepositoryId = repositoryId });
        rule.Enabled = enabled;
        rule.Patterns = string.Join("\n", lines.Distinct());
        rule.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>The commit a release's tag points at, or null if the tag is gone.</summary>
    public Task<string?> TagShaAsync(Repository repo, string tag) =>
        repos.Git(repo, p => GitOps.IsValidTagName(tag) && GitOps.Exists(p) && GitOps.TagExists(p, tag) ? GitOps.ResolveSha(p, "refs/tags/" + tag) : null);

    /// <summary>Attaches matching artifact files to the release. <paramref name="manual"/> ignores the rule's Enabled switch (the Attach now button).</summary>
    public async Task<AttachResult> AttachAsync(Repository repo, int releaseId, int? actorId, bool manual = false, CancellationToken ct = default)
    {
        var notes = new List<string>();
        var names = new List<string>();
        var rule = await GetRuleAsync(repo.Id);
        if (rule == null || (!rule.Enabled && !manual)) return new(0, names, notes);
        var patterns = ParsePatterns(rule.Patterns);
        if (patterns.Count == 0) { notes.Add("No patterns are configured."); return new(0, names, notes); }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var release = await db.Releases.Include(r => r.Assets).FirstOrDefaultAsync(r => r.Id == releaseId && r.RepositoryId == repo.Id, ct);
        if (release == null) return new(0, names, notes);
        var sha = await TagShaAsync(repo, release.TagName);
        if (sha == null) { notes.Add($"Tag '{release.TagName}' no longer exists."); return new(0, names, notes); }

        var artifacts = await db.PipelineArtifacts.AsNoTracking().Include(a => a.Job)
            .Where(a => a.RepositoryId == repo.Id && db.Pipelines.Any(p => p.Id == a.PipelineId && p.CommitSha == sha && p.Status == PipelineStatus.Passed))
            .OrderByDescending(a => a.PipelineId).ThenBy(a => a.Job.Name).ToListAsync(ct);
        if (artifacts.Count == 0) { notes.Add("No passed pipeline with artifacts was found for the tagged commit."); return new(0, names, notes); }

        var taken = release.Assets.Select(a => a.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<ReleaseAsset>();
        long total = 0;
        var uploader = actorId ?? release.AuthorId;
        foreach (var art in artifacts)
        {
            var zipPath = ciStorage.ArtifactPath(art.StoredName);
            if (!File.Exists(zipPath)) { notes.Add($"Artifacts of job '{art.Job.Name}' (pipeline #{art.PipelineId}) are gone."); continue; }
            ZipArchive zip;
            try { zip = ZipFile.OpenRead(zipPath); }
            catch (Exception ex) when (ex is InvalidDataException or IOException) { notes.Add($"Artifacts of job '{art.Job.Name}' could not be read."); continue; }
            using (zip)
            {
                var scanned = 0;
                foreach (var entry in zip.Entries)
                {
                    if (++scanned > MaxEntriesScanned) { notes.Add("Stopped scanning a very large artifact."); break; }
                    if (entry.FullName.EndsWith('/') || entry.Name.Length == 0) continue;
                    var path = entry.FullName.Replace('\\', '/');
                    if (path.StartsWith('/') || path.Split('/').Any(s => s == "..")) continue;
                    if (!patterns.Any(p => CiArtifacts.Matches(p, path))) continue;
                    var name = AttachmentStorage.SanitizeFileName(entry.Name);
                    if (taken.Contains(name)) { notes.Add($"'{name}' skipped: the release already has an asset with that name."); continue; }
                    if (release.Assets.Count + added.Count >= ReleaseService.MaxAssetsPerRelease) { notes.Add($"Stopped: a release can have at most {ReleaseService.MaxAssetsPerRelease} assets."); goto done; }
                    if (total + Math.Max(0, entry.Length) > MaxBytesPerAttach) { notes.Add("Stopped: too much data to attach in one go."); goto done; }
                    StoredFile stored;
                    try
                    {
                        await using var stream = entry.Open();
                        stored = await files.SaveAsync(stream, name, ct, MaxAssetBytes);
                    }
                    catch (AttachmentTooLargeException) { notes.Add($"'{name}' skipped: larger than the release asset limit."); continue; }
                    catch (InvalidDataException) { notes.Add($"'{name}' skipped: damaged entry."); continue; }
                    total += stored.Size;
                    taken.Add(name);
                    added.Add(new ReleaseAsset
                    {
                        ReleaseId = release.Id, StoredName = stored.StoredName, FileName = name, ContentType = stored.ContentType, Size = stored.Size,
                        Sha256 = stored.Sha256, UploaderId = uploader, CreatedAt = DateTime.UtcNow,
                    });
                    names.Add(name);
                }
            }
        }
        done:
        if (added.Count > 0)
        {
            try
            {
                db.ReleaseAssets.AddRange(added);
                release.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch
            {
                files.Delete(added.Select(a => a.StoredName));
                throw;
            }
            await audit.RecordAsync("release.assets_attached", "release", release.Id, repo.ProjectId,
                new { repository = repo.Slug, tag = release.TagName, count = added.Count, names, source = "ci-artifacts" }, uploader);
        }
        return new(added.Count, names, notes);
    }

    /// <summary>A pipeline passed: attach its artifacts to any release whose tag is the pipeline's commit. Never throws.</summary>
    public async Task OnPipelineFinishedAsync(int pipelineId)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var p = await db.Pipelines.AsNoTracking().Include(x => x.Repository).ThenInclude(r => r.Project).FirstOrDefaultAsync(x => x.Id == pipelineId);
            if (p == null || p.Status != PipelineStatus.Passed) return;
            var rule = await GetRuleAsync(p.RepositoryId);
            if (rule is not { Enabled: true }) return;
            var releases = await db.Releases.AsNoTracking().Where(r => r.RepositoryId == p.RepositoryId).OrderByDescending(r => r.Id).Take(30).ToListAsync();
            foreach (var r in releases)
                if (await TagShaAsync(p.Repository, r.TagName) == p.CommitSha)
                    await AttachAsync(p.Repository, r.Id, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Attaching artifacts of pipeline {Id} to releases failed", pipelineId);
        }
    }
}
