using System.Text.RegularExpressions;

namespace ByteBench.Services.Wiki;

/// <summary>
/// Wiki page paths. A page is the Markdown file <c>{path}.md</c> in the wiki repository; its path uses <c>/</c> for folders
/// and <c>-</c> for spaces (<c>guides/Getting-started</c>), and its title is the last segment with dashes shown as spaces.
/// <c>home</c> is the home page and <c>_sidebar</c> replaces the generated page tree; <c>uploads/</c> holds attachments.
/// </summary>
public static partial class WikiPaths
{
    public const string Home = "home";
    public const string Sidebar = "_sidebar";
    public const string UploadsFolder = "uploads";
    public const string Extension = ".md";
    public const int MaxSegments = 8;
    public const int MaxSegmentLength = 100;

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9._()+,'-]*$")]
    private static partial Regex SegmentPattern();

    /// <summary>
    /// The canonical page path for user input ("Getting started", "/guides/setup.md", "a\b"), or null when it isn't a
    /// valid page path (empty, too deep, "..", odd characters, or inside <c>uploads/</c>).
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Replace('\\', '/').Trim().Trim('/');
        if (s.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) s = s[..^Extension.Length];
        var parts = s.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Regex.Replace(p.Trim(), @"\s+", "-")).ToList();
        if (parts.Count is 0 or > MaxSegments) return null;
        foreach (var p in parts)
            if (p.Length > MaxSegmentLength || p is "." or ".." || p.Contains("..") || p.EndsWith('.') || !SegmentPattern().IsMatch(p)) return null;
        if (string.Equals(parts[0], UploadsFolder, StringComparison.OrdinalIgnoreCase)) return null;
        return string.Join('/', parts);
    }

    /// <summary>The page's file in the repository.</summary>
    public static string FileOf(string pagePath) => pagePath + Extension;

    /// <summary>The page path of a repository file, or null when it isn't a wiki page (not <c>.md</c>, or an upload).</summary>
    public static string? PageOfFile(string file) =>
        file.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) && !file.StartsWith(UploadsFolder + "/", StringComparison.OrdinalIgnoreCase)
            ? file[..^Extension.Length] : null;

    /// <summary>"guides/Getting-started" → "Getting started".</summary>
    public static string TitleOf(string pagePath)
    {
        var name = pagePath[(pagePath.LastIndexOf('/') + 1)..];
        return name.Equals(Home, StringComparison.OrdinalIgnoreCase) && !pagePath.Contains('/') ? "Home" : name.Replace('-', ' ');
    }

    /// <summary>The folder part ("" at the root).</summary>
    public static string DirOf(string pagePath) => pagePath.LastIndexOf('/') is var i and >= 0 ? pagePath[..i] : "";

    /// <summary>The app URL of a page (each segment escaped).</summary>
    public static string Url(string projectKey, string? pagePath, string? query = null) =>
        $"/p/{projectKey}/wiki" + (string.IsNullOrEmpty(pagePath) ? "" : "/" + Escape(pagePath)) + (query is { Length: > 0 } ? "?" + query : "");

    /// <summary>The URL an uploaded file (or any non-page file of the wiki) is served from.</summary>
    public static string FileUrl(string projectKey, string filePath) => $"/wiki-files/{projectKey}/{Escape(filePath)}";

    private static string Escape(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    /// <summary>
    /// Resolves a relative link target against <paramref name="baseDir"/> ("./x", "../y"); null when it climbs above the root.
    /// </summary>
    public static string? Combine(string baseDir, string relative)
    {
        var stack = new List<string>(baseDir.Length == 0 ? [] : baseDir.Split('/'));
        foreach (var part in relative.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (stack.Count == 0) return null;
                stack.RemoveAt(stack.Count - 1);
            }
            else stack.Add(part);
        }
        return string.Join('/', stack);
    }

    /// <summary>A safe file name for an upload: letters, digits, '.', '-' and '_' only (others become '-').</summary>
    public static string UploadName(string fileName)
    {
        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        name = Regex.Replace(name, "[^A-Za-z0-9._-]+", "-").Trim('-', '.');
        if (name.Length == 0) name = "file";
        return name.Length > 80 ? name[^80..] : name;
    }
}
