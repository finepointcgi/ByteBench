using System.IO.Compression;

namespace GitBench.Services.Pages;

/// <summary>A deployment was refused (unsafe or too large); the message says why.</summary>
public sealed class PagesException(string message) : Exception(message);

public sealed record PagesLimits(long MaxBytes, int MaxFiles, long MaxFileBytes);

public sealed record PagesExtractResult(int FileCount, long Bytes);

/// <summary>
/// Unpacks a site zip into a fresh folder. Strict on purpose: it refuses the whole archive (instead of skipping entries) when
/// any entry has an unsafe name (absolute, drive letter, <c>..</c>, empty segments, control characters), is a symbolic link or
/// any other non-regular file, repeats a name (also differing only in case), makes a folder and a file share a name, or
/// when the declared or actual sizes pass the limits (so a zip bomb that lies about sizes still stops). Nothing outside
/// <c>destination</c> is ever written, no permission bits are restored, and nothing is created through an existing link.
/// </summary>
public static class PagesExtractor
{
    public const int MaxNameLength = 240;

    /// <summary>Null if <paramref name="name"/> (with '/' separators, no trailing '/') is an acceptable site-relative file name, else why not.</summary>
    public static string? ValidateName(string name)
    {
        if (name.Length == 0) return "empty name";
        if (name.Length > MaxNameLength) return "name too long";
        if (name.StartsWith('/') || name.StartsWith('~') || name.Length > 1 && name[1] == ':') return "absolute path";
        if (name.Any(char.IsControl)) return "control characters in name";
        foreach (var seg in name.Split('/'))
        {
            if (seg.Length == 0) return "empty path segment";
            if (seg is "." or "..") return "'.' or '..' in path";
            if (seg.Length > 200) return "path segment too long";
            if (seg.EndsWith('.') || seg.EndsWith(' ')) return "path segment ends with '.' or a space";
        }
        return null;
    }

    public static PagesExtractResult ExtractZip(string zipPath, string destination, PagesLimits limits)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var rootPrefix = root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        if (new DirectoryInfo(root).LinkTarget != null) throw new PagesException("The deployment folder is a symbolic link.");

        ZipArchive zip;
        try { zip = ZipFile.OpenRead(zipPath); }
        catch (InvalidDataException) { throw new PagesException("The archive is not a valid zip file."); }
        using (zip)
        {
            if (zip.Entries.Count > limits.MaxFiles * 2L + 1000) throw new PagesException("The archive has too many entries.");

            // Pass 1: names, types, duplicates and declared sizes, before anything is written.
            var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long declared = 0;
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                var isDir = name.EndsWith('/') || unixType == 0x4000;
                name = name.TrimEnd('/');
                if (name.Length == 0 && isDir) continue;
                if (ValidateName(name) is { } bad) throw new PagesException($"Unsafe entry '{Shorten(entry.FullName)}': {bad}.");
                if (unixType == 0xA000) throw new PagesException($"'{Shorten(name)}' is a symbolic link; sites may only contain regular files.");
                if (unixType != 0 && unixType != 0x8000 && unixType != 0x4000)
                    throw new PagesException($"'{Shorten(name)}' is not a regular file.");

                var parts = name.Split('/');
                for (var i = 1; i < parts.Length; i++) // parent folders
                {
                    var parent = string.Join('/', parts[..i]);
                    if (files.ContainsKey(parent)) throw new PagesException($"'{Shorten(parent)}' is both a file and a folder.");
                    dirs.Add(parent);
                }
                if (isDir)
                {
                    if (files.ContainsKey(name)) throw new PagesException($"'{Shorten(name)}' is both a file and a folder.");
                    dirs.Add(name);
                    continue;
                }
                if (dirs.Contains(name)) throw new PagesException($"'{Shorten(name)}' is both a file and a folder.");
                if (!files.TryAdd(name, entry)) throw new PagesException($"'{Shorten(name)}' appears more than once.");
                if (entry.Length > limits.MaxFileBytes) throw new PagesException($"'{Shorten(name)}' is larger than the {Format(limits.MaxFileBytes)} file limit.");
                declared += entry.Length;
                if (files.Count > limits.MaxFiles) throw new PagesException($"The site has more than {limits.MaxFiles} files.");
                if (declared > limits.MaxBytes) throw new PagesException($"The site is larger than the {Format(limits.MaxBytes)} limit.");
            }

            // Pass 2: write, counting real bytes.
            long total = 0;
            foreach (var d in dirs.OrderBy(d => d, StringComparer.Ordinal))
                Directory.CreateDirectory(SafeDestination(root, rootPrefix, d));
            foreach (var (name, entry) in files)
            {
                var dest = SafeDestination(root, rootPrefix, name);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using var src = entry.Open();
                using var dst = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long fileBytes = 0;
                int n;
                while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    fileBytes += n;
                    total += n;
                    if (fileBytes > limits.MaxFileBytes || total > limits.MaxBytes)
                        throw new PagesException($"'{Shorten(name)}' is larger than the archive claimed, or the site passes the {Format(limits.MaxBytes)} limit.");
                    dst.Write(buffer, 0, n);
                }
            }
            return new PagesExtractResult(files.Count, total);
        }
    }

    private static string SafeDestination(string root, string rootPrefix, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(rootPrefix, StringComparison.Ordinal)) throw new PagesException($"'{Shorten(relative)}' would be written outside the site folder.");
        // Nothing here is created through a link: the folder is fresh, but check anyway.
        var current = root;
        foreach (var part in full[rootPrefix.Length..].Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && new FileInfo(current).LinkTarget != null)
                throw new PagesException($"'{Shorten(relative)}' goes through a symbolic link.");
        }
        return full;
    }

    public static string Format(long bytes) => Ci.CiArtifacts.Format(bytes);
    private static string Shorten(string s) => s.Length > 120 ? s[..119] + "…" : s;
}
