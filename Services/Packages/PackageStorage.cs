using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Packages;

/// <summary>An upload that doesn't match its digest, is over the size limit, or is malformed.</summary>
public sealed class PackageUploadException(int status, string message, string code = "BLOB_UPLOAD_INVALID") : Exception(message)
{
    public int Status { get; } = status;
    /// <summary>The OCI error code (<c>DIGEST_INVALID</c>, <c>SIZE_INVALID</c>, …) for the container registry.</summary>
    public string Code { get; } = code;
}

/// <summary>The hashes of a stored blob.</summary>
public sealed record StoredBlob(string Sha256, long Size, string Sha1, string Sha512Base64);

/// <summary>An in-progress chunked container upload (<c>POST /v2/…/blobs/uploads/</c>).</summary>
public sealed record UploadSession(string Id, int ProjectId, string Repository, string Owner, DateTime StartedAt);

/// <summary>
/// The registry's content-addressed blob store: NuGet packages, npm tarballs, container layers, configs and manifests
/// all live once at <c>{Root}/blobs/sha256/{hex[0..2]}/{hex}</c>, shared by every package that has the same bytes. Which
/// package has which blob is in the database (<see cref="PackageFile"/>, <see cref="ContainerBlob"/>). Root is
/// <c>Packages:Root</c>, default <c>{ContentRoot}/packages</c> (next to <c>repos</c> and <c>lfs</c>); one blob is at most
/// <c>Packages:MaxBlobBytes</c> (default 5 GB). Writes go to a temporary file, are hashed while streaming and are moved
/// into place only when the digest matches. Chunked container uploads are files under <c>{Root}/uploads</c>.
/// </summary>
public sealed partial class PackageStorage
{
    public const long DefaultMaxBlobBytes = 5L * 1024 * 1024 * 1024;

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HexPattern();

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex UploadIdPattern();

    private readonly IDbContextFactory<AppDbContext> dbFactory;
    private readonly ILogger<PackageStorage> log;

    public PackageStorage(IConfiguration config, IWebHostEnvironment env, IDbContextFactory<AppDbContext> dbFactory, ILogger<PackageStorage> log)
    {
        this.dbFactory = dbFactory;
        this.log = log;
        Root = Path.GetFullPath(config["Packages:Root"] ?? Path.Combine(env.ContentRootPath, "packages"));
        MaxBlobBytes = config.GetValue<long?>("Packages:MaxBlobBytes") is > 0 and var max ? max : DefaultMaxBlobBytes;
    }

    public string Root { get; }
    public long MaxBlobBytes { get; }

    private string BlobRoot => Path.Combine(Root, "blobs", "sha256");
    private string UploadRoot => Path.Combine(Root, "uploads");
    private string TmpRoot => Path.Combine(Root, "tmp");

    public static bool IsValidSha256(string? hex) => hex != null && HexPattern().IsMatch(hex);

    /// <summary>The hex of a <c>sha256:…</c> digest, or null if it isn't one.</summary>
    public static string? HexOfDigest(string? digest) =>
        digest != null && digest.StartsWith("sha256:", StringComparison.Ordinal) && IsValidSha256(digest[7..]) ? digest[7..] : null;

    public string PathFor(string sha256) =>
        IsValidSha256(sha256) ? Path.Combine(BlobRoot, sha256[..2], sha256) : throw new ArgumentException("Invalid SHA-256.", nameof(sha256));

    public bool Exists(string sha256) => IsValidSha256(sha256) && File.Exists(PathFor(sha256));

    public Stream OpenRead(string sha256) =>
        new FileStream(PathFor(sha256), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

    /// <summary>
    /// Streams <paramref name="content"/> into the store. With <paramref name="expectedSha256"/>, throws (400
    /// <c>DIGEST_INVALID</c>) when the content doesn't match; past <see cref="MaxBlobBytes"/> throws 413.
    /// </summary>
    public async Task<StoredBlob> SaveAsync(Stream content, string? expectedSha256, CancellationToken ct)
    {
        Directory.CreateDirectory(TmpRoot);
        var tmp = Path.Combine(TmpRoot, Guid.NewGuid().ToString("N"));
        try
        {
            await using (var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await CopyLimitedAsync(content, file, 0, ct);
            return await CommitAsync(tmp, expectedSha256, ct);
        }
        finally { TryDelete(tmp); }
    }

    /// <summary>Stores bytes already in memory (manifests).</summary>
    public Task<StoredBlob> SaveAsync(byte[] content, CancellationToken ct) => SaveAsync(new MemoryStream(content, writable: false), null, ct);

    /// <summary>Hashes the finished temporary file <paramref name="tmp"/> and moves it into the store.</summary>
    private async Task<StoredBlob> CommitAsync(string tmp, string? expectedSha256, CancellationToken ct)
    {
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        long size = 0;
        await using (var file = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            int n;
            while ((n = await file.ReadAsync(buffer, ct)) > 0)
            {
                size += n;
                sha256.AppendData(buffer, 0, n);
                sha1.AppendData(buffer, 0, n);
                sha512.AppendData(buffer, 0, n);
            }
        }
        var hex = Convert.ToHexStringLower(sha256.GetHashAndReset());
        if (expectedSha256 != null && hex != expectedSha256)
            throw new PackageUploadException(400, $"The content's digest is sha256:{hex}, not sha256:{expectedSha256}.", "DIGEST_INVALID");
        var final = PathFor(hex);
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        if (!File.Exists(final)) File.Move(tmp, final, overwrite: true);
        else File.SetLastWriteTimeUtc(final, DateTime.UtcNow); // keep it out of a running sweep's grace window
        return new StoredBlob(hex, size, Convert.ToHexStringLower(sha1.GetHashAndReset()), Convert.ToBase64String(sha512.GetHashAndReset()));
    }

    private async Task<long> CopyLimitedAsync(Stream from, Stream to, long already, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = already;
        int n;
        while ((n = await from.ReadAsync(buffer, ct)) > 0)
        {
            total += n;
            if (total > MaxBlobBytes) throw new PackageUploadException(413, $"The blob is larger than the limit of {MaxBlobBytes} bytes.", "SIZE_INVALID");
            await to.WriteAsync(buffer.AsMemory(0, n), ct);
        }
        return total;
    }

    // ---------------------------------------------------------------- chunked uploads

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string UploadPath(string id) =>
        UploadIdPattern().IsMatch(id) ? Path.Combine(UploadRoot, id) : throw new ArgumentException("Invalid upload id.", nameof(id));

    public UploadSession StartUpload(int projectId, string repository, string owner)
    {
        Directory.CreateDirectory(UploadRoot);
        var session = new UploadSession(Guid.NewGuid().ToString("N"), projectId, repository, owner, DateTime.UtcNow);
        File.WriteAllBytes(UploadPath(session.Id), []);
        File.WriteAllText(UploadPath(session.Id) + ".json", JsonSerializer.Serialize(session, Json));
        return session;
    }

    public UploadSession? GetUpload(string id)
    {
        if (!UploadIdPattern().IsMatch(id)) return null;
        var meta = UploadPath(id) + ".json";
        if (!File.Exists(meta) || !File.Exists(UploadPath(id))) return null;
        try { return JsonSerializer.Deserialize<UploadSession>(File.ReadAllText(meta), Json); }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    public long UploadedBytes(string id) => new FileInfo(UploadPath(id)).Length;

    /// <summary>
    /// Appends a chunk. <paramref name="start"/> (from <c>Content-Range</c>) must equal the bytes so far; returns the new
    /// total. Throws 416 when it doesn't.
    /// </summary>
    public async Task<long> AppendUploadAsync(string id, Stream content, long? start, CancellationToken ct)
    {
        var path = UploadPath(id);
        var current = new FileInfo(path).Length;
        if (start is { } s && s != current)
            throw new PackageUploadException(416, $"The chunk starts at {s}, but {current} bytes have been uploaded.", "BLOB_UPLOAD_INVALID");
        await using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        return await CopyLimitedAsync(content, file, current, ct);
    }

    /// <summary>Finishes an upload: hashes it, checks it against <paramref name="expectedSha256"/> and stores it.</summary>
    public async Task<StoredBlob> CompleteUploadAsync(string id, string expectedSha256, CancellationToken ct)
    {
        var path = UploadPath(id);
        var tmp = Path.Combine(TmpRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TmpRoot);
        File.Move(path, tmp);
        TryDelete(path + ".json");
        try { return await CommitAsync(tmp, expectedSha256, ct); }
        finally { TryDelete(tmp); }
    }

    public void CancelUpload(string id)
    {
        TryDelete(UploadPath(id));
        TryDelete(UploadPath(id) + ".json");
    }

    // ---------------------------------------------------------------- cleanup

    /// <summary>
    /// After rows were deleted: removes the blobs of <paramref name="sha256s"/> that no package references any more.
    /// Failures are logged, never thrown.
    /// </summary>
    public async Task DeleteUnreferencedAsync(IReadOnlyCollection<string> sha256s)
    {
        if (sha256s.Count == 0) return;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var distinct = sha256s.Distinct().Where(IsValidSha256).ToList();
            var used = await ReferencedAsync(db, distinct, CancellationToken.None);
            foreach (var sha in distinct.Where(s => !used.Contains(s)))
                TryDelete(PathFor(sha));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log.LogError(ex, "Could not delete unreferenced package blobs");
        }
    }

    private static async Task<HashSet<string>> ReferencedAsync(AppDbContext db, List<string> candidates, CancellationToken ct)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in candidates.Chunk(500))
        {
            used.UnionWith(await db.PackageFiles.Where(f => chunk.Contains(f.Sha256)).Select(f => f.Sha256).ToListAsync(ct));
            used.UnionWith(await db.ContainerBlobs.Where(b => chunk.Contains(b.Sha256)).Select(b => b.Sha256).ToListAsync(ct));
        }
        return used;
    }

    /// <summary>
    /// Deletes blobs no package references (e.g. after a project was deleted), stale temporary files and abandoned
    /// uploads. Files younger than <paramref name="grace"/> are kept, so a push in progress is never touched.
    /// </summary>
    public async Task<int> SweepAsync(TimeSpan grace, CancellationToken ct)
    {
        if (!Directory.Exists(Root)) return 0;
        var cutoff = DateTime.UtcNow - grace;
        foreach (var dir in new[] { TmpRoot, UploadRoot }.Where(Directory.Exists))
            foreach (var file in Directory.EnumerateFiles(dir))
                if (File.GetLastWriteTimeUtc(file) < cutoff) TryDelete(file);
        if (!Directory.Exists(BlobRoot)) return 0;
        var candidates = Directory.EnumerateFiles(BlobRoot, "*", SearchOption.AllDirectories)
            .Where(f => File.GetLastWriteTimeUtc(f) < cutoff && IsValidSha256(Path.GetFileName(f)) && f == PathFor(Path.GetFileName(f)))
            .Select(f => Path.GetFileName(f)).ToList();
        if (candidates.Count == 0) return 0;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var used = await ReferencedAsync(db, candidates, ct);
        var deleted = candidates.Where(c => !used.Contains(c)).Count(c => TryDelete(PathFor(c)));
        if (deleted > 0) log.LogInformation("Package sweep deleted {Count} unreferenced blobs", deleted);
        return deleted;
    }

    /// <summary>Bytes of every stored blob (what the registry takes on disk).</summary>
    public long DiskBytes() =>
        Directory.Exists(BlobRoot) ? Directory.EnumerateFiles(BlobRoot, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;

    private bool TryDelete(string file)
    {
        try
        {
            if (!File.Exists(file)) return false;
            File.Delete(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.LogWarning(ex, "Could not delete {File}", file); return false; }
    }
}

/// <summary>Runs <see cref="PackageStorage.SweepAsync"/> an hour after startup and then daily.</summary>
public sealed class PackageSweeper(PackageStorage storage, ILogger<PackageSweeper> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await storage.SweepAsync(TimeSpan.FromDays(1), stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Package sweep failed"); }
                await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
