using System.Text.Json;
using System.Text.Json.Nodes;
using GitBench.Data;
using GitBench.Services.Audit;
using GitBench.Services.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Packages;

/// <summary>A row of the Packages page.</summary>
public sealed record PackageSummary(int Id, PackageType Type, string Name, int VersionCount, string? LatestVersion, long Size, DateTime UpdatedAt);

/// <summary>Registry storage used by one project.</summary>
public sealed record PackageUsage(int ProjectId, string ProjectKey, string ProjectName, int Packages, int Versions, long Bytes);

/// <summary>
/// The package and container registry's data: settings, listings, deletes, cleanup policy, storage usage, and the audit
/// events and webhooks (<c>package.published</c>, <c>package.deleted</c>) of every change. UI calls are checked with
/// <see cref="ProjectAccess"/> (view: ViewProject; delete and settings: DeletePackages). The protocol endpoints
/// (NuGet, npm, OCI) authorize with <see cref="RegistryAuth"/> first and then use the <c>…Core</c>/<c>Record…</c> methods.
/// </summary>
public sealed class PackageService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, PackageStorage storage,
    AuditLog audit, WebhookDispatcher webhooks, Api.PublicBaseUrl baseUrl, ILogger<PackageService> log)
{
    public const int MaxKeepLatest = 1000;

    // ---------------------------------------------------------------- settings

    public async Task<ProjectPackageSettings> GetSettingsAsync(int projectId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ProjectPackageSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == projectId)
               ?? new ProjectPackageSettings { ProjectId = projectId };
    }

    public async Task<bool> IsEnabledAsync(int projectId) => (await GetSettingsAsync(projectId)).Enabled;

    /// <summary>Turns the registry on or off and sets the cleanup policy (DeletePackages). Turning it off deletes nothing.</summary>
    public async Task SaveSettingsAsync(int projectId, bool enabled, int? keepLatest)
    {
        await access.RequireAsync(projectId, Permission.DeletePackages);
        if (keepLatest is < 1 or > MaxKeepLatest) throw new InvalidOperationException($"Keep between 1 and {MaxKeepLatest} versions, or turn the cleanup policy off.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.ProjectPackageSettings.FirstOrDefaultAsync(s => s.ProjectId == projectId);
        if (row == null) db.ProjectPackageSettings.Add(row = new ProjectPackageSettings { ProjectId = projectId });
        var before = new { row.Enabled, row.KeepLatest };
        row.Enabled = enabled;
        row.KeepLatest = keepLatest;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync("package.settings_changed", "project", projectId, projectId, new { before, after = new { enabled, keepLatest } });
        if (keepLatest != null && keepLatest != before.KeepLatest)
            foreach (var id in await db.Packages.Where(p => p.ProjectId == projectId).Select(p => p.Id).ToListAsync())
                await ApplyCleanupPolicyAsync(id, await access.ResolveUserIdAsync());
    }

    // ---------------------------------------------------------------- reads (UI)

    public async Task<List<PackageSummary>> ListAsync(int projectId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.Packages.AsNoTracking().Where(p => p.ProjectId == projectId)
            .Select(p => new
            {
                p.Id, p.Type, p.Name, p.UpdatedAt,
                Versions = p.Versions.Count,
                Latest = p.Versions.OrderByDescending(v => v.UpdatedAt).Select(v => v.Version).FirstOrDefault(),
            }).ToListAsync();
        var sizes = await PackageBlobsAsync(db, projectId);
        return rows.Select(r => new PackageSummary(r.Id, r.Type, r.Name, r.Versions, r.Latest,
                sizes.TryGetValue(r.Id, out var blobs) ? blobs.Values.Sum() : 0, r.UpdatedAt))
            .OrderByDescending(r => r.UpdatedAt).ToList();
    }

    /// <summary>A package with its versions (newest first), their files and publishers; null if not found or not visible.</summary>
    public async Task<Package?> GetAsync(int projectId, int packageId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        var p = await db.Packages.AsNoTracking().AsSplitQuery()
            .Include(x => x.Versions).ThenInclude(v => v.Files)
            .Include(x => x.Versions).ThenInclude(v => v.PublishedBy)
            .FirstOrDefaultAsync(x => x.Id == packageId && x.ProjectId == projectId);
        if (p != null) p.Versions = [.. p.Versions.OrderByDescending(v => v.UpdatedAt)];
        return p;
    }

    /// <summary>npm dist-tags of a package (tag → version).</summary>
    public async Task<Dictionary<string, string>> DistTagsAsync(int packageId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.PackageDistTags.AsNoTracking().Where(t => t.PackageId == packageId).ToDictionaryAsync(t => t.Tag, t => t.Version);
    }

    // ---------------------------------------------------------------- storage usage

    /// <summary>Per package of the project: the distinct blobs it uses (sha → size).</summary>
    private static async Task<Dictionary<int, Dictionary<string, long>>> PackageBlobsAsync(AppDbContext db, int? projectId)
    {
        var files = await db.PackageFiles.AsNoTracking()
            .Join(db.PackageVersions, f => f.PackageVersionId, v => v.Id, (f, v) => new { v.PackageId, f.Sha256, f.Size })
            .Join(db.Packages, x => x.PackageId, p => p.Id, (x, p) => new { x.PackageId, p.ProjectId, x.Sha256, x.Size })
            .Where(x => projectId == null || x.ProjectId == projectId).ToListAsync();
        var blobs = await db.ContainerBlobs.AsNoTracking()
            .Where(b => projectId == null || b.Package.ProjectId == projectId)
            .Select(b => new { b.PackageId, b.Package.ProjectId, b.Sha256, b.Size }).ToListAsync();
        var result = new Dictionary<int, Dictionary<string, long>>();
        foreach (var x in files.Select(f => (f.PackageId, f.Sha256, f.Size)).Concat(blobs.Select(b => (b.PackageId, b.Sha256, b.Size))))
        {
            if (!result.TryGetValue(x.PackageId, out var set)) result[x.PackageId] = set = new(StringComparer.Ordinal);
            set[x.Sha256] = x.Size;
        }
        return result;
    }

    /// <summary>Bytes the project's registry uses (each distinct blob once).</summary>
    public async Task<long> ProjectUsageAsync(int projectId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return 0;
        await using var db = await dbFactory.CreateDbContextAsync();
        return (await PackageBlobsAsync(db, projectId)).Values.SelectMany(s => s).DistinctBy(kv => kv.Key).Sum(kv => kv.Value);
    }

    /// <summary>Usage of every project with packages, largest first (site admins only), and the bytes on disk.</summary>
    public async Task<(List<PackageUsage> Projects, long DiskBytes)> SiteUsageAsync()
    {
        if (!await access.IsAdminAsync()) throw new ForbiddenException("Only site administrators can see registry storage.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var blobs = await PackageBlobsAsync(db, null);
        var packages = await db.Packages.AsNoTracking()
            .Select(p => new { p.Id, p.ProjectId, p.Project.Key, p.Project.Name, Versions = p.Versions.Count }).ToListAsync();
        var usage = packages.GroupBy(p => (p.ProjectId, p.Key, p.Name)).Select(g => new PackageUsage(g.Key.ProjectId, g.Key.Key, g.Key.Name,
                g.Count(), g.Sum(p => p.Versions),
                g.SelectMany(p => blobs.TryGetValue(p.Id, out var s) ? s : []).DistinctBy(kv => kv.Key).Sum(kv => kv.Value)))
            .OrderByDescending(u => u.Bytes).ToList();
        return (usage, storage.DiskBytes());
    }

    // ---------------------------------------------------------------- deletes (UI)

    public async Task DeleteVersionAsync(int projectId, int versionId)
    {
        await access.RequireAsync(projectId, Permission.DeletePackages);
        var actor = await access.ResolveUserIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var v = await db.PackageVersions.Include(x => x.Package).FirstOrDefaultAsync(x => x.Id == versionId && x.Package.ProjectId == projectId)
                ?? throw new InvalidOperationException("That version no longer exists.");
        await DeleteVersionCoreAsync(v.PackageId, v.Id, actor, null);
    }

    public async Task DeletePackageAsync(int projectId, int packageId)
    {
        await access.RequireAsync(projectId, Permission.DeletePackages);
        if (!await DeletePackageCoreAsync(projectId, packageId, await access.ResolveUserIdAsync(), null))
            throw new InvalidOperationException("That package no longer exists.");
    }

    // ---------------------------------------------------------------- protocol helpers (caller has authorized)

    public static string Normalize(PackageType type, string name) => type == PackageType.Npm ? name : name.ToLowerInvariant();

    /// <summary>The project's package, created if missing (a concurrent create of the same package is tolerated).</summary>
    public async Task<Package> GetOrCreatePackageAsync(int projectId, PackageType type, string name)
    {
        var normalized = Normalize(type, name);
        await using var db = await dbFactory.CreateDbContextAsync();
        var p = await db.Packages.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Type == type && x.NormalizedName == normalized);
        if (p != null) return p;
        p = new Package { ProjectId = projectId, Type = type, Name = name, NormalizedName = normalized };
        db.Packages.Add(p);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            await using var again = await dbFactory.CreateDbContextAsync();
            return await again.Packages.FirstAsync(x => x.ProjectId == projectId && x.Type == type && x.NormalizedName == normalized);
        }
        return p;
    }

    public async Task<Package?> FindPackageAsync(int projectId, PackageType type, string name)
    {
        var normalized = Normalize(type, name);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Packages.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Type == type && x.NormalizedName == normalized);
    }

    /// <summary>After a version (or image tag) was published: audit, webhook, then the cleanup policy.</summary>
    public async Task RecordPublishedAsync(Project project, Package package, PackageVersion version, RegistryIdentity who)
    {
        await audit.RecordAsync("package.published", "package", package.Id, project.Id,
            new { type = package.Type.ToString(), package = package.Name, version = version.Version, size = version.Size, pipelineId = who.Job?.PipelineId },
            actorId: who.ActorId, anonymous: who.ActorId == null);
        RaiseWebhook(project, package, version.Version, version.Size, WebhookEventNames.PackagePublished, who);
        await ApplyCleanupPolicyAsync(package.Id, who.ActorId);
    }

    private void RaiseWebhook(Project project, Package package, string? version, long size, string eventName, RegistryIdentity? who)
    {
        try
        {
            var url = baseUrl.Value;
            webhooks.Enqueue(project.Id, eventName, new
            {
                package = new
                {
                    id = package.Id, name = package.Name, type = package.Type.ToString().ToLowerInvariant(), version, size,
                    url = url + PackageUrls.Detail(project.Key, package.Id),
                    pipeline_id = who?.Job?.PipelineId,
                },
            }, who?.ActorId); // the envelope adds project and sender
        }
        catch (Exception ex) { log.LogError(ex, "Could not queue {Event} for package {Id}", eventName, package.Id); }
    }

    /// <summary>
    /// Deletes a version (or image tag) with its files; for a tag, also the manifest if no other tag uses it. Records
    /// the delete. Returns false when it was already gone.
    /// </summary>
    public async Task<bool> DeleteVersionCoreAsync(int packageId, int versionId, int? actorId, RegistryIdentity? who)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var v = await db.PackageVersions.Include(x => x.Files).Include(x => x.Package).ThenInclude(p => p.Project)
            .FirstOrDefaultAsync(x => x.Id == versionId && x.PackageId == packageId);
        if (v == null) return false;
        var shas = v.Files.Select(f => f.Sha256).ToList();
        db.PackageVersions.Remove(v);
        await db.PackageDistTags.Where(t => t.PackageId == packageId && t.Version == v.Version).ExecuteDeleteAsync();
        await db.SaveChangesAsync();
        await storage.DeleteUnreferencedAsync(shas);
        if (PackageStorage.HexOfDigest(v.ManifestDigest) is { } hex) await ReleaseManifestIfUnusedAsync(packageId, hex);
        await RecordDeletedAsync(v.Package.Project, v.Package, v.Version, actorId, who);
        return true;
    }

    public async Task<bool> DeletePackageCoreAsync(int projectId, int packageId, int? actorId, RegistryIdentity? who)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var p = await db.Packages.Include(x => x.Project).FirstOrDefaultAsync(x => x.Id == packageId && x.ProjectId == projectId);
        if (p == null) return false;
        var shas = await db.PackageFiles.Where(f => db.PackageVersions.Any(v => v.Id == f.PackageVersionId && v.PackageId == packageId))
            .Select(f => f.Sha256).ToListAsync();
        shas.AddRange(await db.ContainerBlobs.Where(b => b.PackageId == packageId).Select(b => b.Sha256).ToListAsync());
        db.Packages.Remove(p);
        await db.SaveChangesAsync();
        await storage.DeleteUnreferencedAsync(shas);
        await RecordDeletedAsync(p.Project, p, null, actorId, who);
        return true;
    }

    private async Task RecordDeletedAsync(Project project, Package package, string? version, int? actorId, RegistryIdentity? who)
    {
        actorId ??= who?.ActorId;
        await audit.RecordAsync(version == null ? "package.deleted" : "package.version_deleted", "package", package.Id, project.Id,
            new { type = package.Type.ToString(), package = package.Name, version, pipelineId = who?.Job?.PipelineId },
            actorId: actorId, anonymous: actorId == null);
        RaiseWebhook(project, package, version, 0, WebhookEventNames.PackageDeleted, who);
    }

    // ---------------------------------------------------------------- containers

    /// <summary>
    /// After a tag was deleted or moved: deletes the manifest it pointed to (and the layers only it used) when no tag
    /// points to it any more and no other manifest of the repository (an image index) references it.
    /// </summary>
    public async Task ReleaseManifestIfUnusedAsync(int packageId, string manifestSha)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (await db.PackageVersions.AnyAsync(v => v.PackageId == packageId && v.ManifestDigest == "sha256:" + manifestSha)) return;
            foreach (var m in await db.ContainerBlobs.Where(b => b.PackageId == packageId && b.IsManifest && b.Sha256 != manifestSha).Select(b => b.Sha256).ToListAsync())
                if (storage.Exists(m) && OciManifest.References(await File.ReadAllBytesAsync(storage.PathFor(m))).Contains(manifestSha)) return;
            await storage.DeleteUnreferencedAsync(await DeleteManifestCoreAsync(packageId, manifestSha));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            log.LogWarning(ex, "Could not release manifest {Sha} of package {Id}", manifestSha, packageId);
        }
    }

    /// <summary>Records that a blob or manifest belongs to an image repository (idempotent).</summary>
    public async Task LinkBlobAsync(int packageId, StoredBlob blob, bool isManifest, string? mediaType)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.ContainerBlobs.FirstOrDefaultAsync(b => b.PackageId == packageId && b.Sha256 == blob.Sha256);
        if (row != null)
        {
            if (isManifest && (!row.IsManifest || row.MediaType != mediaType)) { row.IsManifest = true; row.MediaType = mediaType; await db.SaveChangesAsync(); }
            return;
        }
        db.ContainerBlobs.Add(new ContainerBlob { PackageId = packageId, Sha256 = blob.Sha256, Size = blob.Size, IsManifest = isManifest, MediaType = mediaType });
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { /* a concurrent push linked it: fine */ }
    }

    /// <summary>
    /// Removes a manifest from an image repository: every tag pointing at it, its row, and the layers and configs no
    /// remaining manifest of the repository references (older than an hour, so a push in progress keeps its blobs).
    /// Returns the SHA-256s whose rows were removed (for <see cref="PackageStorage.DeleteUnreferencedAsync"/>).
    /// </summary>
    public async Task<List<string>> DeleteManifestCoreAsync(int packageId, string manifestSha)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var digest = "sha256:" + manifestSha;
        await db.PackageVersions.Where(v => v.PackageId == packageId && v.ManifestDigest == digest).ExecuteDeleteAsync();
        await db.ContainerBlobs.Where(b => b.PackageId == packageId && b.Sha256 == manifestSha && b.IsManifest).ExecuteDeleteAsync();
        var removed = new List<string> { manifestSha };

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in await db.ContainerBlobs.Where(b => b.PackageId == packageId && b.IsManifest).Select(b => b.Sha256).ToListAsync())
        {
            referenced.Add(m);
            if (!storage.Exists(m)) continue;
            try { referenced.UnionWith(OciManifest.References(await File.ReadAllBytesAsync(storage.PathFor(m)))); }
            catch (Exception ex) when (ex is IOException or JsonException) { log.LogWarning(ex, "Could not read manifest {Sha}", m); return removed; }
        }
        var cutoff = DateTime.UtcNow.AddHours(-1);
        var orphans = await db.ContainerBlobs.Where(b => b.PackageId == packageId && !b.IsManifest && b.CreatedAt < cutoff)
            .Select(b => new { b.Id, b.Sha256 }).ToListAsync();
        var gone = orphans.Where(o => !referenced.Contains(o.Sha256)).ToList();
        if (gone.Count > 0)
        {
            var ids = gone.Select(o => o.Id).ToList();
            await db.ContainerBlobs.Where(b => ids.Contains(b.Id)).ExecuteDeleteAsync();
            removed.AddRange(gone.Select(o => o.Sha256));
        }
        return removed;
    }

    // ---------------------------------------------------------------- cleanup policy

    /// <summary>
    /// With a cleanup policy (keep the N latest), deletes the older versions or tags of the package. npm versions that a
    /// dist-tag points to are kept, and so is the container tag <c>latest</c>.
    /// </summary>
    public async Task ApplyCleanupPolicyAsync(int packageId, int? actorId)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var projectId = await db.Packages.Where(p => p.Id == packageId).Select(p => (int?)p.ProjectId).FirstOrDefaultAsync();
            if (projectId == null || await db.ProjectPackageSettings.Where(s => s.ProjectId == projectId).Select(s => s.KeepLatest).FirstOrDefaultAsync() is not { } keep)
                return;
            var pinned = await db.PackageDistTags.Where(t => t.PackageId == packageId).Select(t => t.Version).ToListAsync();
            pinned.Add("latest");
            var old = await db.PackageVersions.Where(v => v.PackageId == packageId).OrderByDescending(v => v.UpdatedAt).ThenByDescending(v => v.Id)
                .Skip(keep).Select(v => new { v.Id, v.Version }).ToListAsync();
            foreach (var v in old.Where(v => !pinned.Contains(v.Version)))
                await DeleteVersionCoreAsync(packageId, v.Id, actorId, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Cleanup policy failed for package {Id}", packageId);
        }
    }
}

/// <summary>Reading OCI / Docker manifests: which digests one references and how big the image is.</summary>
public static class OciManifest
{
    public const string DockerV2 = "application/vnd.docker.distribution.manifest.v2+json";
    public const string DockerList = "application/vnd.docker.distribution.manifest.list.v2+json";
    public const string OciManifestV1 = "application/vnd.oci.image.manifest.v1+json";
    public const string OciIndex = "application/vnd.oci.image.index.v1+json";
    public static readonly string[] Supported = [DockerV2, DockerList, OciManifestV1, OciIndex];

    public static bool IsIndex(string mediaType) => mediaType is DockerList or OciIndex;

    /// <summary>The descriptors (config, layers, child manifests, subject) of a manifest: (sha256 hex, size, is manifest).</summary>
    public static List<(string Sha256, long Size, bool IsManifest)> Descriptors(byte[] manifest)
    {
        var root = JsonNode.Parse(manifest) as JsonObject ?? throw new JsonException("The manifest is not a JSON object.");
        var result = new List<(string, long, bool)>();
        void Add(JsonNode? d, bool isManifest)
        {
            if (d is not JsonObject o) return;
            var hex = PackageStorage.HexOfDigest(o["digest"]?.GetValue<string>()) ?? throw new JsonException("A descriptor has an invalid digest.");
            result.Add((hex, o["size"]?.GetValue<long>() ?? 0, isManifest));
        }
        Add(root["config"], false);
        if (root["layers"] is JsonArray layers) foreach (var l in layers) Add(l, false);
        if (root["manifests"] is JsonArray manifests) foreach (var m in manifests) Add(m, true);
        return result;
    }

    public static IEnumerable<string> References(byte[] manifest) => Descriptors(manifest).Select(d => d.Sha256);
}
