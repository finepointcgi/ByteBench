using System.Text.RegularExpressions;
using System.Xml;
using ByteBench.Services.Git;

namespace ByteBench.Services.DependencyBot;

/// <summary>
/// Proposes registry addresses from the repository's own configuration files. It only reads text (nothing is executed, no variable is
/// expanded: any value with <c>${...}</c>, <c>$(...)</c> or <c>%...%</c> is ignored), never reads a token, password or auth line, drops any
/// address that carries a user name, password or query, and is bounded in file count and size.
/// </summary>
public static partial class RegistryDetector
{
    public const int MaxFiles = 40;
    public const int MaxFileBytes = 64 * 1024;
    public const int MaxProposals = 30;

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", "vendor", ".git", "bin", "obj", "third_party", "bower_components", ".venv", "venv", "site-packages" };

    /// <summary>The configuration files of <paramref name="branch"/>, as (path, text).</summary>
    public static List<(string Path, string Text)> ReadConfigFiles(string repoPath, string branch)
    {
        var result = new List<(string, string)>();
        if (!GitOps.Exists(repoPath) || GitOps.ResolveSha(repoPath, branch) is not { } sha) return result;
        foreach (var (file, size) in GitOps.ListFiles(repoPath, sha) ?? [])
        {
            if (result.Count >= MaxFiles) break;
            if (!IsConfig(file) || size > MaxFileBytes || file.Split('/').SkipLast(1).Any(SkippedDirectories.Contains)) continue;
            var bytes = GitOps.ReadFileBytes(repoPath, sha, file, MaxFileBytes);
            if (bytes != null) result.Add((file, ManifestParser.Decode(bytes)));
        }
        return result;
    }

    public static bool IsConfig(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        return name is ".npmrc" or "pip.conf" or "pyproject.toml" || name.Equals("nuget.config", StringComparison.OrdinalIgnoreCase)
               || (name.StartsWith("requirements", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
    }

    public static List<DetectedRegistry> Detect(IEnumerable<(string Path, string Text)> files)
    {
        var found = new List<DetectedRegistry>();
        void Add(string ecosystem, string scope, string? url, string source)
        {
            url = Clean(url);
            if (url == null || found.Count >= MaxProposals) return;
            // npm and pip have one registry per scope/ecosystem: the first mention wins. NuGet lists every distinct source.
            if (found.Any(f => f.Ecosystem == ecosystem && (ecosystem == Ecosystems.NuGet ? f.Url.Equals(url, StringComparison.OrdinalIgnoreCase) : f.Scope == scope))) return;
            found.Add(new DetectedRegistry(ecosystem, scope, url, source));
        }

        foreach (var (path, text) in files)
        {
            var name = path[(path.LastIndexOf('/') + 1)..];
            if (name == ".npmrc")
                foreach (var line in Lines(text))
                {
                    var m = NpmrcRegistry().Match(line);
                    if (m.Success) Add(Ecosystems.Npm, m.Groups["scope"].Success ? m.Groups["scope"].Value.ToLowerInvariant() : "", m.Groups["url"].Value, path);
                }
            else if (name.Equals("nuget.config", StringComparison.OrdinalIgnoreCase))
                foreach (var url in NuGetSources(text)) Add(Ecosystems.NuGet, "", url, path);
            else if (name == "pip.conf")
                foreach (var line in Lines(text))
                {
                    var m = PipConfIndex().Match(line);
                    if (m.Success) Add(Ecosystems.Pip, "", m.Groups["url"].Value, path);
                }
            else if (name == "pyproject.toml")
                foreach (var url in PyprojectIndexes(text)) Add(Ecosystems.Pip, "", url, path);
            else
                foreach (var line in Lines(text))
                {
                    var m = RequirementsIndex().Match(line);
                    if (m.Success) Add(Ecosystems.Pip, "", m.Groups["url"].Value, path);
                }
        }
        return found;
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && l.Length < 1000 && l[0] != '#' && l[0] != ';');

    /// <summary>The address if it is a plain http(s) URL without variables, user info, query or fragment; else null.</summary>
    private static string? Clean(string? value)
    {
        if (value == null) return null;
        value = value.Trim().Trim('"', '\'');
        if (value.Length is 0 or > 500 || value.Contains("${") || value.Contains("$(") || Regex.IsMatch(value, "%[^%]+%") || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || string.IsNullOrEmpty(u.Host)) return null;
        if (!string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment)) return null;
        return value;
    }

    private static List<string> NuGetSources(string text)
    {
        var urls = new List<string>();
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxFileBytes * 2 });
            var inSources = false;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName.Equals("packageSources", StringComparison.OrdinalIgnoreCase)) inSources = !reader.IsEmptyElement;
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName.Equals("packageSources", StringComparison.OrdinalIgnoreCase)) inSources = false;
                else if (inSources && reader.NodeType == XmlNodeType.Element && reader.LocalName.Equals("add", StringComparison.OrdinalIgnoreCase)
                         && reader.GetAttribute("value") is { } value) urls.Add(value);
            }
        }
        catch (XmlException) { /* a broken file proposes what was read so far */ }
        return urls;
    }

    private static List<string> PyprojectIndexes(string text)
    {
        var urls = new List<string>();
        string section = "";
        foreach (var line in Lines(text))
        {
            if (line[0] == '[') { section = line.TrimEnd(); continue; }
            var interesting = section is "[[tool.uv.index]]" or "[[tool.poetry.source]]" or "[tool.uv]" or "[tool.pdm.source]" or "[[tool.pdm.source]]";
            if (!interesting) continue;
            var m = TomlUrl().Match(line);
            if (m.Success && (m.Groups["key"].Value == "url" || section == "[tool.uv]")) urls.Add(m.Groups["url"].Value);
        }
        return urls;
    }

    [GeneratedRegex(@"^(?:(?<scope>@[A-Za-z0-9._~-]+):)?registry\s*=\s*(?<url>\S+)\s*$")]
    private static partial Regex NpmrcRegistry();

    [GeneratedRegex(@"^index-url\s*[=:]\s*(?<url>\S+)\s*$")]
    private static partial Regex PipConfIndex();

    [GeneratedRegex(@"^(?:--index-url|-i)(?:\s+|=)(?<url>\S+)\s*$")]
    private static partial Regex RequirementsIndex();

    [GeneratedRegex(@"^(?<key>url|index-url)\s*=\s*[""'](?<url>[^""']+)[""']")]
    private static partial Regex TomlUrl();
}
