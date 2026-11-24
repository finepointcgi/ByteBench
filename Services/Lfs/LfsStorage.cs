using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Lfs;

/// <summary>An upload that doesn't match its oid or is over the limit.</summary>
public sealed class LfsUploadException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Where Git LFS objects live: <c>{Root}/{oid[0..2]}/{oid[2..4]}/{oid}</c>, content-addressed by SHA-256 and shared by every
/// repository that uploaded the same content (which repository has which object is in <see cref="LfsObject"/>). Root is
/// <c>Lfs:Root</c>, default <c>{ContentRoot}/lfs</c>; objects are at most <c>Lfs:MaxObjectBytes</c> (default 2 GB).
/// Uploads are written to a temporary file, hashed while streaming and moved into place only when the SHA-256 matches.
/// </summary>
public sealed partial class LfsStorage
{
    public const long DefaultMaxObjectBytes = 2L * 1024 * 1024 * 1024;

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex OidPattern();

    private readonly IDbContextFactory<AppDbContext> dbFactory;
    private readonly ILogger<LfsStorage> log;

    public LfsStorage(IConfiguration config, IWebHostEnvironment env, IDbContextFactory<AppDbContext> dbFactory, ILogger<LfsStorage> log)
    {
        this.dbFactory = dbFactory;
        this.log = log;
        Root = Path.GetFullPath(config["Lfs:Root"] ?? Path.Combine(env.ContentRootPath, "lfs"));
        MaxObjectBytes = config.GetValue<long?>("Lfs:MaxObjectBytes") is > 0 and var max ? max : DefaultMaxObjectBytes;
    }

    public string Root { get; }
    public long MaxObjectBytes { get; }

    public static bool IsValidOid(string? oid) => oid != null && OidPattern().IsMatch(oid);

    public string PathFor(string oid) =>
        IsValidOid(oid) ? Path.Combine(Root, oid[..2], oid[2..4], oid) : throw new ArgumentException("Invalid LFS oid.", nameof(oid));

    public bool Exists(string oid, long? size = null)
    {
        var f = new FileInfo(PathFor(oid));
        return f.Exists && (size == null || f.Length == size);
    }

    /// <summary>True when the repository has the object (it was uploaded to it) and its file is present.</summary>
    public async Task<bool> RepositoryHasAsync(int repositoryId, string oid)
    {
        if (!IsValidOid(oid)) return false;
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.LfsObjects.AnyAsync(o => o.RepositoryId == repositoryId && o.Oid == oid) && Exists(oid);
    }

    /// <summary>
    /// Streams <paramref name="content"/> into the store as <paramref name="oid"/>. Throws <see cref="LfsUploadException"/>
    /// (422) when the SHA-256 doesn't match, or (413) past <see cref="MaxObjectBytes"/>. Returns the size.
    /// </summary>
    public async Task<long> SaveAsync(string oid, Stream content, CancellationToken ct)
    {
        var final = PathFor(oid);
        var tmpDir = Path.Combine(Root, "tmp");
        Directory.CreateDirectory(tmpDir);
        var tmp = Path.Combine(tmpDir, Guid.NewGuid().ToString("N"));
        long size = 0;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int n;
                while ((n = await content.ReadAsync(buffer, ct)) > 0)
                {
                    size += n;
                    if (size > MaxObjectBytes) throw new LfsUploadException(413, $"The object is larger than the limit of {MaxObjectBytes} bytes.");
                    hash.AppendData(buffer, 0, n);
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                }
            }
            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (actual != oid) throw new LfsUploadException(422, $"The content's SHA-256 is {actual}, not {oid}.");
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            if (!File.Exists(final)) File.Move(tmp, final, overwrite: true);
            return size;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
        }
    }

    /// <summary>
    /// After repository rows were deleted: removes the files of <paramref name="oids"/> that no repository references any
    /// more. Failures are logged, never thrown.
    /// </summary>
    public async Task DeleteUnreferencedAsync(IReadOnlyCollection<string> oids)
    {
        if (oids.Count == 0) return;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var stillUsed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var chunk in oids.Distinct().Chunk(500))
                stillUsed.UnionWith(await db.LfsObjects.Where(o => chunk.Contains(o.Oid)).Select(o => o.Oid).Distinct().ToListAsync());
            var removed = 0;
            foreach (var oid in oids.Distinct().Where(o => IsValidOid(o) && !stillUsed.Contains(o)))
            {
                var path = PathFor(oid);
                if (!File.Exists(path)) continue;
                File.Delete(path);
                removed++;
            }
            if (removed > 0) log.LogInformation("Deleted {Count} unreferenced LFS objects", removed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogError(ex, "Could not delete unreferenced LFS objects");
        }
    }

    /// <summary>
    /// Deletes stored objects no repository references (e.g. after a whole project was deleted) and stale temporary
    /// files. Files younger than <paramref name="grace"/> are kept, so an upload in progress is never touched.
    /// </summary>
    public async Task<int> SweepAsync(TimeSpan grace, CancellationToken ct)
    {
        if (!Directory.Exists(Root)) return 0;
        var cutoff = DateTime.UtcNow - grace;
        var candidates = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (info.LastWriteTimeUtc > cutoff) continue;
            if (Path.GetFileName(Path.GetDirectoryName(file)) == "tmp") { TryDelete(file); continue; }
            if (IsValidOid(info.Name) && file == PathFor(info.Name)) candidates.Add(info.Name);
        }
        if (candidates.Count == 0) return 0;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var deleted = 0;
        foreach (var chunk in candidates.Chunk(500))
        {
            var used = (await db.LfsObjects.Where(o => chunk.Contains(o.Oid)).Select(o => o.Oid).ToListAsync(ct)).ToHashSet();
            foreach (var oid in chunk.Where(o => !used.Contains(o)))
                if (TryDelete(PathFor(oid))) deleted++;
        }
        if (deleted > 0) log.LogInformation("LFS sweep deleted {Count} unreferenced objects", deleted);
        return deleted;
    }

    private bool TryDelete(string file)
    {
        try { File.Delete(file); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.LogWarning(ex, "Could not delete {File}", file); return false; }
    }
}

/// <summary>Runs <see cref="LfsStorage.SweepAsync"/> an hour after startup and then daily.</summary>
public sealed class LfsSweeper(LfsStorage storage, ILogger<LfsSweeper> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            WorkerPulse.Report("lfs-sweeper", true);
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try { WorkerPulse.Tick("lfs-sweeper"); await storage.SweepAsync(TimeSpan.FromDays(1), stoppingToken); WorkerPulse.Ok("lfs-sweeper"); }
                catch (Exception ex) when (ex is not OperationCanceledException) { WorkerPulse.Fail("lfs-sweeper", ex); log.LogError(ex, "LFS sweep failed"); }
                await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}

/// <summary>
/// A Git LFS pointer file: <c>version https://git-lfs.github.com/spec/v1</c>, <c>oid sha256:&lt;64 hex&gt;</c> and
/// <c>size &lt;bytes&gt;</c> (at most 1 KB in all).
/// </summary>
public sealed record LfsPointer(string Oid, long Size)
{
    public const string VersionLine = "version https://git-lfs.github.com/spec/v1";

    /// <summary>The pointer in <paramref name="text"/>, or null if it isn't one.</summary>
    public static LfsPointer? TryParse(string? text)
    {
        if (text == null || text.Length > 1024 || !text.StartsWith(VersionLine + "\n", StringComparison.Ordinal)) return null;
        string? oid = null;
        long? size = null;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("oid sha256:", StringComparison.Ordinal)) oid = line["oid sha256:".Length..].Trim();
            else if (line.StartsWith("size ", StringComparison.Ordinal) && long.TryParse(line[5..].Trim(), System.Globalization.NumberStyles.None,
                         System.Globalization.CultureInfo.InvariantCulture, out var s)) size = s;
        }
        return LfsStorage.IsValidOid(oid) && size is { } n ? new LfsPointer(oid!, n) : null;
    }
}

public static class LfsServiceCollectionExtensions
{
    /// <summary>Git LFS: object storage (<c>Lfs:Root</c>) and the daily sweep of unreferenced objects.</summary>
    public static IServiceCollection AddGitLfs(this IServiceCollection services)
    {
        services.AddSingleton<LfsStorage>();
        services.AddHostedService<LfsSweeper>();
        return services;
    }
}
