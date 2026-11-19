using ByteBench.Api;
using ByteBench.Data;
using ByteBench.Services.Audit;
using ByteBench.Services.Ci;
using ByteBench.Services.Git;
using ByteBench.Services.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Pages;

/// <summary>One deployment to make (see <see cref="PagesDeployer.DeployZipAsync"/>).</summary>
public sealed record PagesDeployRequest(int RepositoryId, string Slot, string Ref, string CommitSha, string ZipPath,
    int? PipelineId = null, int? JobId = null, int? UserId = null, TimeSpan? ReviewExpireIn = null, bool CreateSite = true);

/// <summary>
/// Deploys and removes site versions for CI and the settings page (singleton, no permission checks; callers decide who may).
/// <para>
/// <b>Atomic switch.</b> A deployment is unpacked into <c>{Root}/{repositoryId}/.staging-*</c>, renamed (one directory move,
/// atomic on the same volume) to its final 32-hex folder, and only then does one database save make it the current version
/// of its slot (and the previous one not current). Readers resolve the current row per request, so they see the old site or
/// the new one, never a half-written one. A failed deployment leaves the live site untouched and removes its staging folder.
/// </para>
/// </summary>
public class PagesDeployer(IDbContextFactory<AppDbContext> dbFactory, PagesStorage storage, IOptionsMonitor<PagesOptions> options,
    IServiceScopeFactory scopes, WebhookDispatcher webhooks, PagesLinks links, ILogger<PagesDeployer> log)
{
    private PagesOptions Options => options.CurrentValue;

    /// <summary>
    /// Called by the CI runner after a pages job passed: decides the slot from the pipeline's branch, packs the job's folder
    /// and deploys it. Returns log lines, and false when the job should fail (e.g. the folder is missing or over a limit).
    /// </summary>
    public async Task<(bool Ok, List<string> Notes)> DeployFromWorkspaceAsync(Pipeline p, PipelineJob job, string workspace, CiPagesDefinition def)
    {
        var notes = new List<string>();
        const string pre = "[bytebench] Pages: ";
        if (!Options.Enabled) { notes.Add(pre + "Pages is turned off on this server; nothing was published."); return (true, notes); }
        if (p.IsForkPullRequest) { notes.Add(pre + "not published: pipelines of fork pull requests never publish sites."); return (true, notes); }

        await using var db = await dbFactory.CreateDbContextAsync();
        var repo = await db.Repositories.AsNoTracking().Include(r => r.Project).FirstOrDefaultAsync(r => r.Id == p.RepositoryId);
        if (repo == null) return (true, notes);
        var site = await db.PagesSites.AsNoTracking().FirstOrDefaultAsync(s => s.RepositoryId == repo.Id);
        if (site is { Enabled: false }) { notes.Add(pre + "the site is disabled in the repository's Pages settings; nothing was published."); return (true, notes); }

        string slot;
        TimeSpan? expire = null;
        if (p.PullRequestId == null && p.Ref == repo.DefaultBranch) slot = "";
        else if (def.Review && Options.ReviewSites && site?.ReviewSites != false)
        {
            slot = PagesSlots.ForRef(p.Ref);
            expire = def.ReviewExpireIn ?? TimeSpan.FromDays(Math.Clamp(site?.ReviewExpireDays ?? 7, 1, 365));
        }
        else
        {
            notes.Add(pre + $"not published: '{p.Ref}' is not the default branch ('{repo.DefaultBranch}')"
                + (def.Review ? (Options.ReviewSites ? ", and review sites are off for this repository." : ", and review sites are off on this server.") : "; add 'review: true' to the pages job to publish review sites."));
            return (true, notes);
        }

        var source = ResolveFolder(workspace, def.Path);
        if (source == null) { notes.Add(pre + $"folder '{def.Path}' was not found in the workspace (or is a symbolic link)."); return (false, notes); }

        var tmp = Path.Combine(storage.RepositoryDirectory(repo.Id), $".pack-{Guid.NewGuid():N}.zip");
        try
        {
            var limits = Options.Limits;
            var collected = await Task.Run(() => CiArtifacts.CollectToZip(source, ["**"], tmp, limits.MaxBytes));
            notes.AddRange(collected.Notes.Select(n => pre + n));
            if (!collected.Ok) { notes.Add(pre + $"not published: {collected.Error}."); return (false, notes); }
            if (collected.FileCount == 0) { notes.Add(pre + $"not published: '{def.Path}' has no files."); return (false, notes); }
            var d = await DeployZipAsync(new PagesDeployRequest(repo.Id, slot, p.Ref, p.CommitSha, tmp, p.Id, job.Id,
                job.PlayedById ?? p.TriggeredById, expire));
            notes.Add(pre + $"published version {d.Version}{(slot.Length > 0 ? $" of review site '{slot}'" : "")}: {d.FileCount} file(s), {CiArtifacts.Format(d.SizeBytes)} at {links.SiteUrl(repo, slot)}"
                + (d.ExpiresAt is { } e ? $" (expires {e:yyyy-MM-dd HH:mm} UTC)." : "."));
            return (true, notes);
        }
        catch (PagesException ex) { notes.Add(pre + $"not published: {ex.Message}"); return (false, notes); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DbUpdateException)
        {
            log.LogWarning(ex, "Pages deployment of job {Job} failed", job.Id);
            notes.Add(pre + $"not published: {ex.Message}");
            return (false, notes);
        }
        finally { CiArtifacts.TryDelete(tmp); }
    }

    /// <summary>The folder inside the workspace, or null if it doesn't exist or it or one of its parents is a symbolic link.</summary>
    public static string? ResolveFolder(string workspace, string relative)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        var current = root;
        foreach (var part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (new DirectoryInfo(current).LinkTarget != null || new FileInfo(current).LinkTarget != null) return null;
        }
        var full = Path.GetFullPath(current);
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && Directory.Exists(full) ? full : null;
    }

    /// <summary>
    /// Unpacks <paramref name="req"/>.ZipPath safely, stores it as the next version of the slot and makes it current. Throws
    /// <see cref="PagesException"/> for an unsafe or too large archive, or when the site doesn't exist / is disabled.
    /// </summary>
    public async Task<PagesDeployment> DeployZipAsync(PagesDeployRequest req)
    {
        if (req.Slot.Length > 0 && !PagesSlots.IsValid(req.Slot)) throw new PagesException("Invalid review site name.");
        var repoDir = storage.RepositoryDirectory(req.RepositoryId);
        Directory.CreateDirectory(repoDir);
        var staging = Path.Combine(repoDir, ".staging-" + Guid.NewGuid().ToString("N"));
        var finalName = PagesStorage.NewDirectoryName();
        var finalPath = storage.DeploymentPath(req.RepositoryId, finalName);
        PagesExtractResult extracted;
        try
        {
            extracted = await Task.Run(() => PagesExtractor.ExtractZip(req.ZipPath, staging, Options.Limits));
            if (extracted.FileCount == 0) throw new PagesException("The site has no files.");
            Directory.Move(staging, finalPath);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }

        PagesDeployment deployment;
        PagesSite site;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            site = await db.PagesSites.FirstOrDefaultAsync(s => s.RepositoryId == req.RepositoryId)
                ?? (req.CreateSite ? db.PagesSites.Add(new PagesSite { RepositoryId = req.RepositoryId }).Entity : throw new PagesException("The repository has no site."));
            if (!site.Enabled) throw new PagesException("The site is disabled.");
            if (site.Id == 0) await db.SaveChangesAsync();
            var now = DateTime.UtcNow;
            for (var attempt = 0; ; attempt++)
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                var version = await db.PagesDeployments.Where(d => d.SiteId == site.Id && d.Slot == req.Slot).Select(d => (int?)d.Version).MaxAsync() ?? 0;
                await db.PagesDeployments.Where(d => d.SiteId == site.Id && d.Slot == req.Slot && d.IsCurrent)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsCurrent, false));
                deployment = new PagesDeployment
                {
                    SiteId = site.Id, Slot = req.Slot, Version = version + 1, Ref = req.Ref, CommitSha = req.CommitSha, PipelineId = req.PipelineId,
                    JobId = req.JobId, DeployedById = req.UserId, Directory = finalName, FileCount = extracted.FileCount, SizeBytes = extracted.Bytes,
                    CreatedAt = now, ActivatedAt = now, IsCurrent = true, ExpiresAt = req.ReviewExpireIn is { } e && req.Slot.Length > 0 ? now + e : null,
                };
                db.PagesDeployments.Add(deployment);
                try { await db.SaveChangesAsync(); await tx.CommitAsync(); break; }
                catch (DbUpdateException) when (attempt < 3) { db.Entry(deployment).State = EntityState.Detached; }
            }
            site.UpdatedAt = now;
            await db.SaveChangesAsync();
        }
        catch
        {
            TryDeleteDirectory(finalPath);
            throw;
        }

        await PruneAsync(site.Id, req.RepositoryId, req.Slot);
        await RaiseAsync(deployment, rolledBack: false);
        return deployment;
    }

    /// <summary>Makes an earlier deployment of the site the current one of its slot again (instant: it is still on disk).</summary>
    public async Task<PagesDeployment> RollbackAsync(int siteId, int deploymentId, int? userId)
    {
        PagesDeployment d;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            d = await db.PagesDeployments.Include(x => x.Site).FirstOrDefaultAsync(x => x.Id == deploymentId && x.SiteId == siteId)
                ?? throw new InvalidOperationException("That deployment does not exist.");
            if (d.IsCurrent) throw new InvalidOperationException("That version is already live.");
            if (d.ExpiresAt is { } e && e < DateTime.UtcNow) throw new InvalidOperationException("That review site has expired.");
            if (!Directory.Exists(storage.DeploymentPath(d.Site.RepositoryId, d.Directory)))
                throw new InvalidOperationException("The files of that version are gone.");
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.PagesDeployments.Where(x => x.SiteId == siteId && x.Slot == d.Slot && x.IsCurrent)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsCurrent, false));
            d.IsCurrent = true;
            d.IsRollback = true;
            d.ActivatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        await RaiseAsync(d, rolledBack: true, userId);
        return d;
    }

    /// <summary>Deletes every deployment (or only those of <paramref name="slot"/>) and their files; the site's settings stay.</summary>
    public async Task<int> DeleteContentAsync(int siteId, string? slot = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var site = await db.PagesSites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == siteId);
        if (site == null) return 0;
        var rows = await db.PagesDeployments.Where(d => d.SiteId == siteId && (slot == null || d.Slot == slot)).ToListAsync();
        db.PagesDeployments.RemoveRange(rows);
        await db.SaveChangesAsync();
        foreach (var d in rows) TryDeleteDirectory(storage.DeploymentPath(site.RepositoryId, d.Directory));
        return rows.Count;
    }

    /// <summary>Deletes versions of a slot beyond the retention (the current one is never deleted).</summary>
    private async Task PruneAsync(int siteId, int repositoryId, string slot)
    {
        try
        {
            var keep = slot.Length == 0 ? Math.Max(1, Options.KeepVersions) : 2;
            await using var db = await dbFactory.CreateDbContextAsync();
            var old = (await db.PagesDeployments.Where(d => d.SiteId == siteId && d.Slot == slot).OrderByDescending(d => d.Version).ToListAsync())
                .Skip(keep).Where(d => !d.IsCurrent).ToList();
            if (old.Count == 0) return;
            db.PagesDeployments.RemoveRange(old);
            await db.SaveChangesAsync();
            foreach (var d in old) TryDeleteDirectory(storage.DeploymentPath(repositoryId, d.Directory));
        }
        catch (Exception ex) when (ex is IOException or DbUpdateException) { log.LogWarning(ex, "Could not prune old Pages versions of site {Site}", siteId); }
    }

    /// <summary>Removes expired review sites and folders no row refers to (left by failed deployments or deleted repositories).</summary>
    public async Task<int> CleanupAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        var expiredSlots = await db.PagesDeployments.Where(d => d.IsCurrent && d.Slot != "" && d.ExpiresAt != null && d.ExpiresAt < now)
            .Select(d => new { d.SiteId, d.Slot, d.Site.RepositoryId }).ToListAsync(ct);
        var removed = 0;
        foreach (var s in expiredSlots)
        {
            var rows = await db.PagesDeployments.Where(d => d.SiteId == s.SiteId && d.Slot == s.Slot).ToListAsync(ct);
            db.PagesDeployments.RemoveRange(rows);
            await db.SaveChangesAsync(ct);
            foreach (var d in rows) TryDeleteDirectory(storage.DeploymentPath(s.RepositoryId, d.Directory));
            removed += rows.Count;
        }

        var root = storage.Root;
        if (Directory.Exists(root))
        {
            var known = (await db.PagesDeployments.Select(d => new { d.Site.RepositoryId, d.Directory }).ToListAsync(ct))
                .Select(d => $"{d.RepositoryId}/{d.Directory}").ToHashSet();
            var repoIds = (await db.Repositories.Select(r => r.Id).ToListAsync(ct)).ToHashSet();
            var cutoff = now.AddHours(-1);
            foreach (var repoDir in Directory.EnumerateDirectories(root))
            {
                if (!int.TryParse(Path.GetFileName(repoDir), out var rid)) continue;
                foreach (var entry in Directory.EnumerateFileSystemEntries(repoDir))
                {
                    var name = Path.GetFileName(entry);
                    if (Directory.GetLastWriteTimeUtc(entry) >= cutoff) continue; // maybe being deployed right now
                    if (!repoIds.Contains(rid) || name.StartsWith('.') || PagesStorage.IsValidDirectory(name) && !known.Contains($"{rid}/{name}"))
                    {
                        if (Directory.Exists(entry)) TryDeleteDirectory(entry); else CiArtifacts.TryDelete(entry);
                    }
                }
                if (!repoIds.Contains(rid)) TryDeleteDirectory(repoDir);
            }
        }
        return removed;
    }

    /// <summary>Audit event, <c>pages.deployed</c> webhook and a notification to whoever triggered the pipeline.</summary>
    private async Task RaiseAsync(PagesDeployment d, bool rolledBack, int? actorId = null)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var site = await db.PagesSites.AsNoTracking().FirstAsync(s => s.Id == d.SiteId);
            var repo = await db.Repositories.AsNoTracking().Include(r => r.Project).FirstAsync(r => r.Id == site.RepositoryId);
            var url = links.SiteUrl(repo, d.Slot);
            var by = actorId ?? d.DeployedById;
            webhooks.Enqueue(repo.ProjectId, WebhookEventNames.PagesDeployed, new
            {
                site = new { url, visibility = site.Visibility.ToString().ToLowerInvariant(), slot = d.Slot },
                deployment = new
                {
                    id = d.Id, version = d.Version, slot = d.Slot, @ref = d.Ref, commitSha = d.CommitSha, fileCount = d.FileCount, sizeBytes = d.SizeBytes,
                    isRollback = rolledBack, pipelineId = d.PipelineId, jobId = d.JobId, createdAt = d.CreatedAt, expiresAt = d.ExpiresAt,
                },
                repository = new { id = repo.Id, slug = repo.Slug, name = repo.Name },
            }, by);

            using var scope = scopes.CreateScope();
            var audit = scope.ServiceProvider.GetRequiredService<AuditLog>();
            await audit.RecordAsync(rolledBack ? "pages.rolled_back" : "pages.deployed", "repository", repo.Id, repo.ProjectId,
                new { repository = repo.Slug, version = d.Version, slot = d.Slot, @ref = d.Ref, commit = d.CommitSha, files = d.FileCount, bytes = d.SizeBytes, pipelineId = d.PipelineId },
                actorId: by, anonymous: by == null);

            if (!rolledBack && by is { } user)
            {
                var access = scope.ServiceProvider.GetRequiredService<ProjectAccess>();
                access.ActAsSystem();
                await scope.ServiceProvider.GetRequiredService<NotificationService>().OnDeploymentAsync(repo.ProjectId, NotificationKind.PagesDeployed, [user], null,
                    $"{repo.Slug}: version {d.Version}{(d.Slot.Length > 0 ? $" of review site {d.Slot}" : "")} is live", RepoUrls.Base(repo) + "/settings/pages");
            }
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not raise pages.deployed for deployment {Id}", d.Id); }
    }

    public static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
