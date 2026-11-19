using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace ByteBench.Services.Hosting.Backup;

public sealed record RestoreResult(BackupManifest Manifest, int Files, long Bytes, IReadOnlyList<string> Sections);

/// <summary>
/// Restores an archive made by <see cref="BackupArchive"/>. Nothing in the installation is touched until the whole archive has
/// been unpacked into a staging folder and checked:
/// <list type="number">
/// <item>entries must be plain files and folders under <c>db/</c> or a known section (no <c>..</c>, no links);</item>
/// <item><c>manifest.json</c> must be there, of a known format, from the same database provider, and not newer than this
/// build's migrations (a backup from a newer ByteBench is refused: upgrade first);</item>
/// <item>every file must match the manifest's size and SHA-256, and nothing may be missing or extra;</item>
/// <item>the installation must be empty (no users or projects in the database, nothing in the storage folders) unless
/// <c>force</c> is set, which replaces the contents of each folder and the database.</item>
/// </list>
/// Stop ByteBench before restoring. Migrations of newer builds than the backup apply at the next start.
/// </summary>
public static class BackupRestorer
{
    public static async Task<RestoreResult> RestoreAsync(string archivePath, BackupLayout layout, BackupOptions options, bool force,
        Action<string>? log, CancellationToken ct)
    {
        if (!File.Exists(archivePath)) throw new BackupException($"{archivePath} does not exist.");
        var stagingParent = options.StagingDirectory is { Length: > 0 } sd ? Path.GetFullPath(sd) : Path.GetTempPath();
        Directory.CreateDirectory(stagingParent);
        var staging = Path.Combine(stagingParent, "bytebench-restore-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(staging);
        try
        {
            // 1. Unpack and verify, touching nothing else.
            log?.Invoke($"Reading {archivePath}");
            var (manifest, seen) = await ExtractAsync(archivePath, staging, ct);
            Verify(manifest, seen);
            CheckCompatible(manifest, layout);
            log?.Invoke($"Archive is valid: ByteBench {manifest.AppVersion}, {manifest.Provider}, {manifest.Files.Count} files, made {manifest.CreatedAtUtc:u}");

            // 2. Is anything in the way?
            var targets = layout.Sections.Where(s => manifest.Sections.ContainsKey(s.Name) && Directory.Exists(Path.Combine(staging, s.Name))).ToList();
            var missing = manifest.Sections.Keys.Where(k => layout.Sections.All(s => s.Name != k) && manifest.Sections[k].Files > 0).ToList();
            if (missing.Count > 0)
                throw new BackupException($"The archive has {string.Join(", ", missing)}, which this configuration doesn't back up or restore " +
                    "(Backup:IncludeKeys / Backup:IncludeCiArtifacts). Set them to match, or restore on a configuration that includes them.");
            var inTheWay = new List<string>();
            if (await DatabaseBackup.HasDataAsync(layout, ct)) inTheWay.Add("the database already has users or projects");
            foreach (var s in targets)
                if (Directory.Exists(s.Path) && Directory.EnumerateFileSystemEntries(s.Path).Any()) inTheWay.Add($"{s.Path} is not empty");
            if (inTheWay.Count > 0 && !force)
                throw new BackupException("Refusing to overwrite a non-empty installation: " + string.Join("; ", inTheWay) +
                    ". Restore into a clean installation, or pass --force to replace its contents.");

            // 3. Replace.
            log?.Invoke("Restoring the database");
            await DatabaseBackup.RestoreAsync(layout, options, Path.Combine(staging, manifest.DatabaseEntry.Replace('/', Path.DirectorySeparatorChar)), ct);
            foreach (var s in targets)
            {
                var from = Path.Combine(staging, s.Name);
                log?.Invoke($"Restoring {s.Name} into {s.Path}");
                Directory.CreateDirectory(s.Path);
                foreach (var child in Directory.EnumerateFileSystemEntries(s.Path)) DeletePath(child);
                MoveChildren(from, s.Path);
            }
            var total = manifest.Files.Sum(f => f.Size);
            return new RestoreResult(manifest, manifest.Files.Count, total, targets.Select(t => t.Name).ToList());
        }
        finally
        {
            try { DeleteTree(staging); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ------------------------------------------------------------------ unpacking

    private static async Task<(BackupManifest, Dictionary<string, (long Size, string Sha)>)> ExtractAsync(string archivePath, string staging, CancellationToken ct)
    {
        BackupManifest? manifest = null;
        var seen = new Dictionary<string, (long Size, string Sha)>(StringComparer.Ordinal);
        var dirModes = new List<(string Path, UnixFileMode Mode)>();
        await using var file = File.OpenRead(archivePath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (await tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
        {
            var name = entry.Name.TrimEnd('/');
            if (entry.EntryType is TarEntryType.GlobalExtendedAttributes) continue;
            if (name == BackupManifest.FileName && entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
            {
                using var ms = new MemoryStream();
                await entry.DataStream!.CopyToAsync(ms, ct);
                manifest = BackupManifest.FromJson(System.Text.Encoding.UTF8.GetString(ms.ToArray()));
                continue;
            }
            CheckName(name);
            var target = Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar));
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(target);
                    dirModes.Add((target, entry.Mode));
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                {
                    if (!name.Contains('/')) throw new BackupException($"Unexpected file '{name}' in the archive.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long size = 0;
                    await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920))
                    {
                        var buffer = new byte[81920];
                        int n;
                        while ((n = await entry.DataStream!.ReadAsync(buffer, ct)) > 0)
                        {
                            sha.AppendData(buffer, 0, n);
                            await output.WriteAsync(buffer.AsMemory(0, n), ct);
                            size += n;
                        }
                    }
                    seen[name] = (size, Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant());
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, entry.Mode & (UnixFileMode)0b111_111_111 | UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    break;
                }
                default:
                    throw new BackupException($"'{entry.Name}' is a {entry.EntryType}; backups only contain plain files and folders.");
            }
        }
        if (manifest == null) throw new BackupException("This is not a ByteBench backup: manifest.json is missing (an incomplete or damaged archive?).");
        // Folder modes last (a read-only folder would block unpacking into it).
        if (!OperatingSystem.IsWindows())
            foreach (var (path, mode) in dirModes.AsEnumerable().Reverse())
                File.SetUnixFileMode(path, mode & (UnixFileMode)0b111_111_111 | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return (manifest, seen);
    }

    private static void CheckName(string name)
    {
        if (name.Length == 0 || name.StartsWith('/') || name.Contains('\\') || name.Contains(':') || name.Contains('\0'))
            throw new BackupException($"Unsafe path '{name}' in the archive.");
        var parts = name.Split('/');
        if (parts.Any(p => p is "" or "." or "..")) throw new BackupException($"Unsafe path '{name}' in the archive.");
        if (parts[0] != "db" && !BackupLayout.KnownSections.Contains(parts[0])) throw new BackupException($"Unexpected entry '{name}' in the archive.");
    }

    private static void Verify(BackupManifest m, Dictionary<string, (long Size, string Sha)> seen)
    {
        if (m.Format < 1 || m.Format > BackupManifest.CurrentFormat)
            throw new BackupException($"The archive has format {m.Format}; this ByteBench reads format {BackupManifest.CurrentFormat}. It was made by a newer version: upgrade ByteBench first.");
        if (m.App != "ByteBench" && m.App != "GitBench") throw new BackupException("This is not a ByteBench backup.");
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in m.Files)
        {
            CheckName(f.Path);
            if (!listed.Add(f.Path)) throw new BackupException($"manifest.json lists {f.Path} twice.");
            if (!seen.TryGetValue(f.Path, out var actual)) throw new BackupException($"{f.Path} is listed in the manifest but missing from the archive (truncated?).");
            if (actual.Size != f.Size || !string.Equals(actual.Sha, f.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new BackupException($"{f.Path} is corrupt: its size or SHA-256 doesn't match the manifest.");
        }
        var extra = seen.Keys.FirstOrDefault(k => !listed.Contains(k));
        if (extra != null) throw new BackupException($"{extra} is in the archive but not in the manifest.");
        if (!listed.Contains(m.DatabaseEntry)) throw new BackupException("The archive has no database.");
    }

    private static void CheckCompatible(BackupManifest m, BackupLayout layout)
    {
        if (!string.Equals(m.Provider, layout.Provider.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new BackupException($"The backup is of a {m.Provider} database but Database:Provider is {layout.Provider}. " +
                "A backup only restores into the same provider; use a database export/import tool to move between SQLite and Postgres.");
        var known = DatabaseBackup.KnownLatestMigration(layout.Provider);
        if (m.Migration.Length > 0 && string.CompareOrdinal(m.Migration, known) > 0)
            throw new BackupException($"The backup's database is at migration {m.Migration}, newer than this ByteBench ({known}). " +
                $"It was made by ByteBench {m.AppVersion}; run that version or newer to restore it.");
    }

    // ------------------------------------------------------------------ file moves

    private static void MoveChildren(string from, string to)
    {
        foreach (var child in Directory.EnumerateFileSystemEntries(from))
        {
            var target = Path.Combine(to, Path.GetFileName(child));
            try
            {
                if (Directory.Exists(child)) Directory.Move(child, target); else File.Move(child, target);
            }
            catch (IOException) // another volume (the usual case in Docker): copy instead
            {
                if (Directory.Exists(child)) CopyTree(child, target); else File.Copy(child, target);
            }
        }
        if (!OperatingSystem.IsWindows())
            try { File.SetUnixFileMode(to, File.GetUnixFileMode(from)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(to, File.GetUnixFileMode(from));
        foreach (var child in Directory.EnumerateFileSystemEntries(from))
        {
            var target = Path.Combine(to, Path.GetFileName(child));
            if (Directory.Exists(child)) CopyTree(child, target);
            else
            {
                File.Copy(child, target);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(child));
            }
        }
    }

    private static void DeletePath(string path)
    {
        if (Directory.Exists(path)) DeleteTree(path); else { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
    }

    private static void DeleteTree(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); // git marks packs read-only
        Directory.Delete(dir, recursive: true);
    }
}
