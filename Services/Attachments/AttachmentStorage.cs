using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.StaticFiles;

namespace GitBench.Services.Attachments;

/// <summary>
/// Where attachment bytes live: <c>{Root}/{storedName}</c>, where storedName is a random 32-hex-character id (the user's
/// filename is never part of a path). Root comes from config <c>Attachments:Root</c>, default <c>{ContentRoot}/attachments</c>.
/// Limits: <c>Attachments:MaxBytes</c> (default 25 MB) per file and <c>Attachments:MaxPerIssue</c> (default 50).
/// </summary>
public sealed class AttachmentStorage
{
    public const long DefaultMaxBytes = 25L * 1024 * 1024;
    public const int DefaultMaxPerIssue = 50;

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private static readonly HashSet<string> InlineImageTypes = new(StringComparer.OrdinalIgnoreCase)
        { "image/png", "image/jpeg", "image/gif", "image/webp" };

    private readonly ILogger<AttachmentStorage> log;

    public AttachmentStorage(IConfiguration config, IWebHostEnvironment env, ILogger<AttachmentStorage> log)
    {
        this.log = log;
        Root = Path.GetFullPath(config["Attachments:Root"] ?? Path.Combine(env.ContentRootPath, "attachments"));
        MaxBytes = config.GetValue<long?>("Attachments:MaxBytes") is > 0 and var mb ? mb : DefaultMaxBytes;
        MaxPerIssue = config.GetValue<int?>("Attachments:MaxPerIssue") is > 0 and var mp ? mp : DefaultMaxPerIssue;
    }

    public string Root { get; }
    public long MaxBytes { get; }
    public int MaxPerIssue { get; }

    /// <summary>png, jpeg, gif and webp: the only types ever served inline (and only when the bytes matched at upload).</summary>
    public static bool IsInlineImage(string contentType) => InlineImageTypes.Contains(contentType);

    public static bool IsValidStoredName(string name) =>
        name.Length == 32 && name.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public string PathFor(string storedName) =>
        IsValidStoredName(storedName) ? Path.Combine(Root, storedName) : throw new ArgumentException("Invalid stored attachment name.", nameof(storedName));

    /// <summary>
    /// Streams <paramref name="content"/> to a new file (never buffering it whole), hashing as it goes, and sniffs the
    /// first bytes for a raster image type. Throws <see cref="AttachmentTooLargeException"/> past <see cref="MaxBytes"/>.
    /// The caller owns the returned file and must <see cref="Delete"/> it if it can't record it.
    /// <paramref name="maxBytes"/> overrides the limit (release assets have their own).
    /// </summary>
    public async Task<StoredFile> SaveAsync(Stream content, string fileName, CancellationToken ct = default, long? maxBytes = null)
    {
        var limit = maxBytes ?? MaxBytes;
        Directory.CreateDirectory(Root);
        var storedName = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var path = PathFor(storedName);
        var tmp = path + ".part";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var head = new byte[16];
        var headLen = 0;
        long size = 0;
        try
        {
            await using (var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int n;
                while ((n = await content.ReadAsync(buffer, ct)) > 0)
                {
                    size += n;
                    if (size > limit) throw new AttachmentTooLargeException(limit);
                    if (headLen < head.Length)
                    {
                        var take = Math.Min(n, head.Length - headLen);
                        Array.Copy(buffer, 0, head, headLen, take);
                        headLen += take;
                    }
                    hash.AppendData(buffer, 0, n);
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                }
            }
            File.Move(tmp, path);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
        var sniffed = SniffImage(head.AsSpan(0, headLen));
        return new StoredFile(storedName, size, Convert.ToHexStringLower(hash.GetHashAndReset()), sniffed ?? TypeFromExtension(fileName));
    }

    /// <summary>Removes files (after their rows were deleted). Missing files and I/O errors are logged, never thrown.</summary>
    public void Delete(IEnumerable<string> storedNames)
    {
        foreach (var name in storedNames)
        {
            if (!IsValidStoredName(name)) continue;
            TryDelete(Path.Combine(Root, name));
        }
    }

    private void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.LogWarning(ex, "Could not delete attachment file {Path}", path); }
    }

    /// <summary>A safe display name: no directories, control characters or reserved characters; at most 255 characters.</summary>
    public static string SanitizeFileName(string? name)
    {
        name = (name ?? "").Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(char.IsControl(c) || c is '"' or '<' or '>' or '|' or ':' or '*' or '?' ? '_' : c);
        var result = sb.ToString().Trim().Trim('.');
        if (result.Length == 0) result = "file";
        if (result.Length > 255)
        {
            var ext = Path.GetExtension(result);
            result = result[..(255 - Math.Min(ext.Length, 20))] + (ext.Length <= 20 ? ext : "");
        }
        return result;
    }

    /// <summary>Content type from the extension, except that raster image types (served inline) must come from sniffing.</summary>
    private static string TypeFromExtension(string fileName) =>
        ContentTypes.TryGetContentType(fileName, out var type) && !IsInlineImage(type) ? type : "application/octet-stream";

    private static string? SniffImage(ReadOnlySpan<byte> h)
    {
        if (h.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return "image/png";
        if (h.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])) return "image/jpeg";
        if (h.StartsWith("GIF87a"u8) || h.StartsWith("GIF89a"u8)) return "image/gif";
        if (h.Length >= 12 && h.StartsWith("RIFF"u8) && h[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }
}

public sealed record StoredFile(string StoredName, long Size, string Sha256, string ContentType);

public sealed class AttachmentTooLargeException(long max)
    : InvalidOperationException($"Files can be at most {AttachmentFormat.Size(max)}.");

public static class AttachmentFormat
{
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    /// <summary>Relative download URL: <c>/attachments/{id}/{fileName}</c>.</summary>
    public static string Url(int id, string fileName) => $"/attachments/{id}/{Uri.EscapeDataString(fileName)}";

    /// <summary>Backslash-escapes Markdown punctuation so a filename renders as plain text (link text, activity entries).</summary>
    public static string EscapeMarkdown(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '(' or ')' or '!' or '|' or '~' or '<' or '>' or '@')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary><c>![name](url)</c> for images, <c>[name](url)</c> otherwise.</summary>
    public static string MarkdownLink(int id, string fileName, string contentType) =>
        (AttachmentStorage.IsInlineImage(contentType) ? "!" : "") + $"[{EscapeMarkdown(fileName)}]({Url(id, fileName)})";

    /// <summary>A small emoji for a file type, for the attachments panel.</summary>
    public static string Icon(string fileName, string contentType)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (contentType.StartsWith("image/")) return "🖼️";
        return ext switch
        {
            ".pdf" => "📕",
            ".zip" or ".gz" or ".tgz" or ".7z" or ".rar" or ".tar" => "🗜️",
            ".txt" or ".md" or ".log" => "📄",
            ".csv" or ".xlsx" or ".xls" => "📊",
            ".doc" or ".docx" or ".odt" => "📝",
            ".json" or ".xml" or ".yml" or ".yaml" or ".cs" or ".js" or ".ts" or ".py" or ".html" or ".css" or ".sql" => "🧾",
            ".mp4" or ".mov" or ".webm" => "🎞️",
            ".mp3" or ".wav" or ".ogg" => "🎵",
            _ => "📎",
        };
    }
}
