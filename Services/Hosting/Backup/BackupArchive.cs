using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace GitBench.Services.Hosting.Backup;

/// <summary>
/// Writes one <c>gitbench-backup-&lt;UTC time&gt;.tar.gz</c>: the database (consistent snapshot, taken first), every storage
/// folder (<c>repos/</c>, <c>attachments/</c>, <c>packages/</c>, <c>lfs/</c>, <c>pages/</c>, <c>keys/</c>), and last
/// <c>manifest.json</c> with the SHA-256 of each file. Alongside it goes <c>.sha256</c> (the archive's own hash).
/// </summary>
/// <remarks>
/// The database snapshot comes first, so every commit a database row points to is already in the repositories when they are
/// copied afterwards (git writes objects before moving refs; a push that lands during the copy is simply not in the backup).
/// Git's <c>*.lock</c> files, symbolic links and the backup folder itself are skipped.
/// </remarks>
public static class BackupArchive
{
    public const string NamePrefix = "gitbench-backup-";
    public const string Extension = ".tar.gz";

    public static bool IsBackupName(string name) =>
        name.StartsWith(NamePrefix, StringComparison.Ordinal) && name.EndsWith(Extension, StringComparison.Ordinal)
        && name.IndexOfAny(['/', '\\']) < 0 && !name.Contains("..", StringComparison.Ordinal);

    public static async Task<BackupResult> CreateAsync(BackupLayout layout, BackupOptions options, string? destinationDir, Action<string>? log, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var dir = Path.GetFullPath(destinationDir ?? layout.BackupDirectory);
        Directory.CreateDirectory(dir);
        var name = $"{NamePrefix}{started:yyyyMMdd-HHmmss}{Extension}";
        var finalPath = Path.Combine(dir, name);
        if (File.Exists(finalPath)) { name = $"{NamePrefix}{started:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}{Extension}"; finalPath = Path.Combine(dir, name); }
        var partial = finalPath + ".partial";
        var work = Path.Combine(dir, ".work-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            var manifest = new BackupManifest
            {
                AppVersion = BackupManifest.CurrentAppVersion(), CreatedAtUtc = started, Host = Environment.MachineName,
                Provider = layout.Provider.ToString(), DatabaseEntry = DatabaseBackup.EntryFor(layout.Provider),
            };

            // 1. Database snapshot (before any file is read)
            var dbFile = Path.Combine(work, Path.GetFileName(manifest.DatabaseEntry));
            log?.Invoke($"Database ({layout.Provider}): taking a consistent snapshot");
            manifest.Migration = await DatabaseBackup.DumpAsync(layout, options, dbFile, ct);

            string sha256;
            long dataBytes = 0;
            await using (var raw = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
            await using (var hashing = new HashingWriteStream(raw))
            {
                await using (var gzip = new GZipStream(hashing, CompressionLevel.Fastest, leaveOpen: true))
                await using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
                {
                    await AddDirectoryEntryAsync(tar, "db", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, ct);
                    await AddFileAsync(tar, manifest, manifest.DatabaseEntry, dbFile, ct);
                    dataBytes += new FileInfo(dbFile).Length;

                    // 2. Storage folders
                    var skip = layout.BackupDirectory;
                    foreach (var section in layout.Sections)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!Directory.Exists(section.Path)) { log?.Invoke($"{section.Name}: {section.Path} does not exist, skipped"); continue; }
                        var info = manifest.Sections[section.Name] = new ManifestSection();
                        log?.Invoke($"{section.Name}: copying {section.Path}");
                        await AddDirectoryEntryAsync(tar, section.Name, DirMode(section.Path), ct);
                        await AddTreeAsync(tar, manifest, section.Name, section.Path, section.Path, [skip, dir], info, log, ct);
                        dataBytes += info.Bytes;
                    }

                    // 3. Manifest last: it lists what really went in.
                    var json = Encoding.UTF8.GetBytes(manifest.ToJson());
                    var entry = new PaxTarEntry(TarEntryType.RegularFile, BackupManifest.FileName)
                    {
                        DataStream = new MemoryStream(json), Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite, ModificationTime = started,
                    };
                    await tar.WriteEntryAsync(entry, ct);
                }
                await hashing.FlushAsync(ct);
                sha256 = hashing.Finish();
            }
            File.Move(partial, finalPath, overwrite: false);
            await File.WriteAllTextAsync(finalPath + ".sha256", $"{sha256}  {name}\n", ct);
            var bytes = new FileInfo(finalPath).Length;
            log?.Invoke($"Wrote {finalPath} ({bytes:N0} bytes, {manifest.Files.Count} files)");
            return new BackupResult(finalPath, bytes, manifest.Files.Count, dataBytes, sha256, DateTime.UtcNow - started, manifest.Migration);
        }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch (IOException) { }
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static UnixFileMode DirMode(string path)
    {
        try { return OperatingSystem.IsWindows() ? (UnixFileMode)0b111_101_101 : File.GetUnixFileMode(path); }
        catch (IOException) { return (UnixFileMode)0b111_101_101; }
    }

    private static Task AddDirectoryEntryAsync(TarWriter tar, string name, UnixFileMode mode, CancellationToken ct) =>
        tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, name) { Mode = mode }, ct);

    private static async Task AddTreeAsync(TarWriter tar, BackupManifest manifest, string section, string root, string current,
        string[] skipTrees, ManifestSection info, Action<string>? log, CancellationToken ct)
    {
        IEnumerable<string> children;
        try { children = Directory.EnumerateFileSystemEntries(current).OrderBy(p => p, StringComparer.Ordinal).ToList(); }
        catch (DirectoryNotFoundException) { return; }
        foreach (var path in children)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            FileAttributes attrs;
            try { attrs = File.GetAttributes(path); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { continue; } // removed while we walk
            if ((attrs & FileAttributes.ReparsePoint) != 0) { log?.Invoke($"skipped link {path}"); continue; }
            var rel = section + "/" + Path.GetRelativePath(root, path).Replace('\\', '/');
            if ((attrs & FileAttributes.Directory) != 0)
            {
                if (skipTrees.Any(s => SamePath(s, path)) || name.StartsWith(".trash-", StringComparison.Ordinal)) continue;
                await AddDirectoryEntryAsync(tar, rel, DirMode(path), ct);
                await AddTreeAsync(tar, manifest, section, root, path, skipTrees, info, log, ct);
            }
            else
            {
                if (name.EndsWith(".lock", StringComparison.Ordinal) || name.EndsWith(".partial", StringComparison.Ordinal)) continue;
                try
                {
                    var size = await AddFileAsync(tar, manifest, rel, path, ct);
                    info.Files++;
                    info.Bytes += size;
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { /* vanished: a temp file */ }
            }
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static async Task<long> AddFileAsync(TarWriter tar, BackupManifest manifest, string entryName, string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
        var length = fs.Length; // a file that grows while we copy is cut at this length (git objects and packs never change in place)
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var data = new SnapshotStream(fs, length, sha);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName)
        {
            DataStream = data,
            Mode = OperatingSystem.IsWindows() ? (UnixFileMode)0b110_100_100 : File.GetUnixFileMode(path),
            ModificationTime = File.GetLastWriteTimeUtc(path),
        };
        await tar.WriteEntryAsync(entry, ct);
        if (data.Consumed != length) throw new BackupException($"{path} shrank while it was being copied.");
        manifest.Files.Add(new ManifestFile { Path = entryName, Size = length, Sha256 = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant() });
        return length;
    }

    /// <summary>A read-only view of the first <c>length</c> bytes of a file that hashes what it hands out.</summary>
    private sealed class SnapshotStream(FileStream inner, long length, IncrementalHash hash) : Stream
    {
        public long Consumed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true; // TarWriter asks for Length/Position; only forward reading is ever done
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => Consumed; set { if (value != Consumed) throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) =>
            (origin == SeekOrigin.Current ? Consumed + offset : origin == SeekOrigin.Begin ? offset : length + offset) == Consumed ? Consumed : throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Take(buffer.AsSpan(offset, count), inner.Read(buffer, offset, (int)Math.Min(count, length - Consumed)));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var want = (int)Math.Min(buffer.Length, length - Consumed);
            if (want <= 0) return 0;
            var n = await inner.ReadAsync(buffer[..want], ct);
            return Take(buffer.Span, n);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        private int Take(Span<byte> buffer, int n)
        {
            if (n > 0) { hash.AppendData(buffer[..n]); Consumed += n; }
            return n;
        }
    }

    /// <summary>Passes writes through to the archive file and hashes them (the sidecar <c>.sha256</c>).</summary>
    private sealed class HashingWriteStream(Stream inner) : Stream
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { hash.AppendData(buffer, offset, count); inner.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { hash.AppendData(buffer); inner.Write(buffer); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { hash.AppendData(buffer.Span); await inner.WriteAsync(buffer, ct); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public string Finish() => Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); await base.DisposeAsync(); }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
