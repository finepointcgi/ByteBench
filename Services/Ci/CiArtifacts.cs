using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GitBench.Services.Ci;

/// <summary>What <see cref="CiArtifacts.CollectToZip"/> did.</summary>
public sealed record CiCollectResult(int FileCount, long UncompressedBytes, long ZipBytes, IReadOnlyList<string> Notes, string? Error)
{
    public bool Ok => Error == null;
}

/// <summary>What <see cref="CiArtifacts.ExtractSafely"/> did.</summary>
public sealed record CiExtractResult(int FileCount, long Bytes, IReadOnlyList<string> Notes);

/// <summary>
/// File handling for artifacts and caches (no database): matching <c>paths</c> patterns in a job's workspace, zipping
/// them without ever following a symbolic link, and extracting a zip into a workspace without writing outside it
/// (no <c>..</c>, no absolute names, and never through a symlink the repository or a step created).
/// </summary>
public static partial class CiArtifacts
{
    public const int MaxFiles = 100_000;

    /// <summary>Null if <paramref name="pattern"/> can be used in <c>paths</c>, else why not (to follow "path 'x' ").</summary>
    public static string? ValidatePattern(string pattern)
    {
        if (pattern.Length > 200) return "is too long (max 200 characters).";
        if (pattern.Any(char.IsControl) || pattern.Contains('\\')) return "contains invalid characters.";
        if (pattern.StartsWith('/') || pattern.StartsWith('~') || (pattern.Length > 1 && pattern[1] == ':'))
            return "must be relative to the workspace.";
        var segments = pattern.TrimEnd('/').Split('/');
        if (segments.Any(s => s is "" or "." or "..")) return "must be relative to the workspace, without '.', '..' or empty segments.";
        return null;
    }

    /// <summary>
    /// True if <paramref name="relativePath"/> (a file, '/'-separated) is selected by <paramref name="pattern"/>: the
    /// pattern matches the file itself or one of its parent folders (so <c>dist/</c> takes everything under dist).
    /// <c>*</c> and <c>?</c> stay within one path segment; <c>**</c> crosses segments.
    /// </summary>
    public static bool Matches(string pattern, string relativePath)
    {
        var p = pattern.TrimEnd('/');
        if (CiFile.GlobMatch(p, relativePath)) return !pattern.EndsWith('/'); // "dist/" only names a folder
        for (var i = relativePath.IndexOf('/'); i >= 0; i = relativePath.IndexOf('/', i + 1))
            if (CiFile.GlobMatch(p, relativePath[..i])) return true;
        return false;
    }

    /// <summary>
    /// The regular files under <paramref name="root"/> selected by <paramref name="patterns"/>, as '/'-separated
    /// relative paths, sorted. Symbolic links (to files or folders) are never followed or returned; each one that
    /// would have matched is reported in <paramref name="skippedLinks"/>.
    /// </summary>
    public static List<string> FindFiles(string root, IReadOnlyList<string> patterns, List<string>? skippedLinks = null)
    {
        var result = new List<string>();
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(root) || IsLink(root)) return result;
        var options = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = true, ReturnSpecialDirectories = false };
        var stack = new Stack<(string Dir, string Rel)>();
        stack.Push((root, ""));
        while (stack.Count > 0 && result.Count < MaxFiles)
        {
            var (dir, rel) = stack.Pop();
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
            {
                var r = rel.Length == 0 ? entry.Name : rel + "/" + entry.Name;
                if (entry.LinkTarget != null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    if (skippedLinks != null && patterns.Any(p => Matches(p, r) || CiFile.GlobMatch(p.TrimEnd('/'), r))) skippedLinks.Add(r);
                    continue;
                }
                if (entry is DirectoryInfo) stack.Push((entry.FullName, r));
                else if (entry is FileInfo && patterns.Any(p => Matches(p, r))) result.Add(r);
            }
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>
    /// Zips the files of <paramref name="root"/> selected by <paramref name="patterns"/> into <paramref name="zipPath"/>
    /// (Unix permission bits kept). Fails without writing anything when their total size exceeds
    /// <paramref name="maxBytes"/>, or with no zip when nothing matched.
    /// </summary>
    public static CiCollectResult CollectToZip(string root, IReadOnlyList<string> patterns, string zipPath, long maxBytes)
    {
        var notes = new List<string>();
        var links = new List<string>();
        var files = FindFiles(root, patterns, links);
        foreach (var l in links.Take(20)) notes.Add($"skipped symbolic link {l}");
        if (links.Count > 20) notes.Add($"skipped {links.Count - 20} more symbolic links");
        if (files.Count == 0) return new(0, 0, 0, notes, null);
        if (files.Count >= MaxFiles) return new(0, 0, 0, notes, $"more than {MaxFiles} files matched");

        root = Path.GetFullPath(root);
        long total = 0;
        foreach (var f in files) total += new FileInfo(Path.Combine(root, f)).Length;
        if (total > maxBytes) return new(0, total, 0, notes, $"{Format(total)} matched, over the {Format(maxBytes)} limit");

        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        var count = 0;
        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var f in files)
                {
                    var full = Path.Combine(root, f);
                    FileStream src;
                    try { src = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read); }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        notes.Add($"could not read {f}: {ex.Message}");
                        continue;
                    }
                    using (src)
                    {
                        var entry = zip.CreateEntry(f, CompressionLevel.Fastest);
                        if (!OperatingSystem.IsWindows())
                            entry.ExternalAttributes = ((int)File.GetUnixFileMode(full) & 0x1FF | 0x8000) << 16; // S_IFREG | rwx bits
                        entry.LastWriteTime = File.GetLastWriteTime(full);
                        using var dst = entry.Open();
                        src.CopyTo(dst);
                    }
                    count++;
                }
            }
        }
        catch
        {
            TryDelete(zipPath);
            throw;
        }
        var size = new FileInfo(zipPath).Length;
        if (size > maxBytes)
        {
            TryDelete(zipPath);
            return new(0, total, size, notes, $"the archive is {Format(size)}, over the {Format(maxBytes)} limit");
        }
        return new(count, total, size, notes, null);
    }

    /// <summary>
    /// Extracts <paramref name="zipPath"/> into <paramref name="root"/>, skipping (with a note) any entry whose name is
    /// absolute or has <c>..</c>, whose target or one of its parent folders is a symbolic link, or that would replace a
    /// folder. Stops at <paramref name="maxBytes"/> extracted bytes. Only the rwx permission bits are restored.
    /// </summary>
    public static CiExtractResult ExtractSafely(string zipPath, string root, long maxBytes)
    {
        var notes = new List<string>();
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var rootPrefix = root + Path.DirectorySeparatorChar;
        var count = 0;
        long bytes = 0;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) continue; // folder entry
            if (ValidatePattern(name) != null || name.Contains('*') || name.Contains('?'))
            {
                notes.Add($"skipped {Shorten(name)}: not a safe relative path");
                continue;
            }
            var dest = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!dest.StartsWith(rootPrefix, StringComparison.Ordinal))
            {
                notes.Add($"skipped {Shorten(name)}: outside the workspace");
                continue;
            }
            if (PathThroughLink(root, dest) is { } link)
            {
                notes.Add($"skipped {Shorten(name)}: {link} is a symbolic link");
                continue;
            }
            if (Directory.Exists(dest))
            {
                notes.Add($"skipped {Shorten(name)}: a folder with that name exists");
                continue;
            }
            if (bytes + entry.Length > maxBytes)
            {
                notes.Add($"stopped extracting: over the {Format(maxBytes)} limit");
                break;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (File.Exists(dest)) File.Delete(dest);
            using (var src = entry.Open())
            using (var dst = new FileStream(dest, FileMode.CreateNew, FileAccess.Write))
                bytes += CopyLimited(src, dst, maxBytes - bytes);
            var mode = (entry.ExternalAttributes >> 16) & 0x1FF;
            if (!OperatingSystem.IsWindows() && mode != 0) File.SetUnixFileMode(dest, (UnixFileMode)mode | UnixFileMode.UserRead | UnixFileMode.UserWrite);
            count++;
        }
        return new(count, bytes, notes);
    }

    /// <summary>The '/'-separated file names in a zip, with their sizes (for browsing artifacts).</summary>
    public static List<(string Path, long Size)> ListZip(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.Where(e => !e.FullName.EndsWith('/')).Select(e => (e.FullName.Replace('\\', '/'), e.Length)).ToList();
    }

    /// <summary>The first existing component between <paramref name="root"/> (exclusive) and <paramref name="dest"/> that is a symlink.</summary>
    private static string? PathThroughLink(string root, string dest)
    {
        var rel = dest[(root.Length + 1)..];
        var current = root;
        foreach (var part in rel.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if (IsLink(current)) return Path.GetRelativePath(root, current).Replace('\\', '/');
            if (!File.Exists(current) && !Directory.Exists(current)) return null; // nothing further exists
        }
        return null;
    }

    /// <summary>True for a symbolic link (or other reparse point), dangling or not.</summary>
    private static bool IsLink(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.LinkTarget != null || (info.Exists || Directory.Exists(path)) && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static long CopyLimited(Stream src, Stream dst, long limit)
    {
        var buffer = new byte[81920];
        long total = 0;
        int n;
        while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += n;
            if (total > limit) throw new InvalidDataException("The archive is larger than it claims.");
            dst.Write(buffer, 0, n);
        }
        return total;
    }

    public static string Format(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };

    private static string Shorten(string s) => s.Length > 120 ? s[..119] + "…" : s;

    internal static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Cache keys: literal text (letters, digits, <c>.</c>, <c>_</c>, <c>-</c>) plus any number of
/// <c>{{ hashFiles('pattern', ...) }}</c>, which is replaced by a hash of the matching workspace files' names and contents.
/// </summary>
public static partial class CiCacheKey
{
    [GeneratedRegex(@"\{\{\s*hashFiles\(\s*(?<args>[^)]*?)\s*\)\s*\}\}")]
    private static partial Regex HashFilesPattern();

    [GeneratedRegex(@"^\s*(?:'(?<a>[^']*)'|""(?<a>[^""]*)"")\s*(?:,|$)")]
    private static partial Regex ArgPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]*$")]
    private static partial Regex LiteralPattern();

    /// <summary>Null if <paramref name="template"/> is a valid key, else why not.</summary>
    public static string? Validate(string template)
    {
        if (template.Length > 200) return "is too long (max 200 characters).";
        var literal = HashFilesPattern().Replace(template, "");
        if (literal.Contains("{{") || literal.Contains("}}")) return "only {{ hashFiles('file', ...) }} can be used inside {{ }}.";
        if (!LiteralPattern().IsMatch(literal)) return "use letters, digits, '.', '_' and '-' outside {{ }}.";
        foreach (Match m in HashFilesPattern().Matches(template))
        {
            if (ParseArgs(m.Groups["args"].Value) is not { } args) return "hashFiles() takes one or more quoted file patterns.";
            foreach (var a in args)
                if (CiArtifacts.ValidatePattern(a) is { } e) return $"hashFiles pattern '{a}' {e}";
        }
        if (template.Trim().Length == 0) return "is empty.";
        return null;
    }

    /// <summary>The key for a workspace: each hashFiles() becomes the first 20 hex digits of a SHA-256 over the matching files.</summary>
    public static string Resolve(string template, string workspace) =>
        HashFilesPattern().Replace(template, m => HashFiles(workspace, ParseArgs(m.Groups["args"].Value) ?? []));

    /// <summary>A hash of the names and contents of the files matching <paramref name="patterns"/> (symlinks ignored).</summary>
    public static string HashFiles(string workspace, IReadOnlyList<string> patterns)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var f in CiArtifacts.FindFiles(workspace, patterns))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(f + "\0"));
            using var s = File.OpenRead(Path.Combine(workspace, f));
            sha.AppendData(SHA256.HashData(s));
        }
        return Convert.ToHexString(sha.GetHashAndReset())[..20].ToLowerInvariant();
    }

    private static List<string>? ParseArgs(string text)
    {
        var args = new List<string>();
        var rest = text.Trim();
        while (rest.Length > 0)
        {
            var m = ArgPattern().Match(rest);
            if (!m.Success || m.Groups["a"].Value.Trim().Length == 0) return null;
            args.Add(m.Groups["a"].Value.Trim());
            rest = rest[m.Length..].Trim();
        }
        return args.Count == 0 ? null : args;
    }
}
