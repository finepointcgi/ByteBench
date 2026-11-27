using System.Globalization;

namespace ByteBench.Desktop.Core;

/// <summary>
/// Append-only text log of web view problems (failed or blocked loads, HTTP errors, page script errors) so a blank page can be explained.
/// One file, rotated to <c>.1</c> past a size limit. Never throws: logging must not break the app. URLs are logged without query or
/// fragment (<see cref="RedactUrl"/>) because a workspace entry URL carries a one-time ticket.
/// </summary>
public sealed class DiagnosticsLog(string path, long maxBytes = 512 * 1024)
{
    private readonly object _gate = new();
    public string Path { get; } = path;

    /// <summary>Process-wide instance set at startup; null in tests and before startup.</summary>
    public static DiagnosticsLog? Default { get; set; }

    public static string RedactUrl(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "(none)";
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var u)) return raw.Length > 80 ? raw[..80] + "..." : raw;
        return u.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped) + (u.Query.Length > 1 ? "?(query hidden)" : "");
    }

    public void Append(string level, string message, DateTimeOffset? now = null)
    {
        var line = $"{(now ?? DateTimeOffset.Now).ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)} {level,-5} {message.ReplaceLineEndings(" ")}{Environment.NewLine}";
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
                var info = new FileInfo(Path);
                if (info.Exists && info.Length > maxBytes) File.Move(Path, Path + ".1", overwrite: true);
                File.AppendAllText(Path, line);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* a log that cannot be written is not worth a crash */ }
    }

    public void Info(string message) => Append("INFO", message);
    public void Error(string message) => Append("ERROR", message);
}
