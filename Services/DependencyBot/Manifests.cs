using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ByteBench.Services.DependencyBot;

public static class Ecosystems
{
    public const string Npm = "npm", NuGet = "nuget", Pip = "pip", Go = "go";
    public static readonly string[] All = [Npm, NuGet, Pip, Go];

    public static string Label(string ecosystem) => ecosystem switch { Npm => "npm", NuGet => "NuGet", Pip => "pip (PyPI)", Go => "Go", _ => ecosystem };

    /// <summary>Which ecosystem a file belongs to, or null if the bot doesn't read it.</summary>
    public static string? OfManifest(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (name == "package.json") return Npm;
        if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)) return NuGet;
        if (name == "pyproject.toml" || (name.StartsWith("requirements", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))) return Pip;
        if (name == "go.mod") return Go;
        return null;
    }

    /// <summary>The lockfile next to a manifest that the bot does not regenerate, if the repository has one.</summary>
    public static string? LockFileNote(string ecosystem, string manifestPath, ISet<string> allPaths)
    {
        var dir = manifestPath.Contains('/') ? manifestPath[..(manifestPath.LastIndexOf('/') + 1)] : "";
        string[] candidates = ecosystem switch
        {
            Npm => ["package-lock.json", "npm-shrinkwrap.json", "yarn.lock", "pnpm-lock.yaml"],
            NuGet => ["packages.lock.json"],
            Pip => ["poetry.lock", "Pipfile.lock", "pdm.lock"],
            Go => ["go.sum"],
            _ => [],
        };
        var found = candidates.FirstOrDefault(c => allPaths.Contains(dir + c));
        return found == null ? null : dir + found;
    }
}

/// <summary>
/// One direct dependency found in a manifest. <see cref="Start"/>/<see cref="Length"/> is the exact text (in the manifest's
/// decoded text) that holds the version; <see cref="Prefix"/> (<c>^</c>, <c>~</c>, <c>&gt;=</c>) stays in front of the new version.
/// </summary>
public sealed record Dependency(string Ecosystem, string Manifest, string Name, string Constraint, string Current, string Prefix, string Group, int Start, int Length);

public static partial class ManifestParser
{
    /// <summary>The text of a manifest as bytes (kept exactly, BOM and line endings included) → string.</summary>
    public static string Decode(byte[] bytes) => Encoding.UTF8.GetString(bytes);
    public static byte[] Encode(string text) => Encoding.UTF8.GetBytes(text);

    public static List<Dependency> Parse(string ecosystem, string path, string text)
    {
        try
        {
            return ecosystem switch
            {
                Ecosystems.Npm => ParseNpm(path, text),
                Ecosystems.NuGet => ParseNuGet(path, text),
                Ecosystems.Pip => path.EndsWith(".toml", StringComparison.OrdinalIgnoreCase) ? ParsePyproject(path, text) : ParseRequirements(path, text),
                Ecosystems.Go => ParseGoMod(path, text),
                _ => [],
            };
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException) { return []; }
    }

    /// <summary>The manifest text with the dependency's version replaced by <paramref name="newVersion"/> (the prefix is kept). Throws if the text no longer matches.</summary>
    public static string Replace(string text, Dependency dep, string newVersion)
    {
        if (dep.Start < 0 || dep.Start + dep.Length > text.Length || text.Substring(dep.Start, dep.Length) != dep.Constraint)
            throw new InvalidOperationException($"{dep.Manifest} changed since the check: {dep.Name} is no longer where it was.");
        return text[..dep.Start] + dep.Prefix + newVersion + text[(dep.Start + dep.Length)..];
    }

    // ----- npm -----

    private static readonly string[] NpmSections = ["dependencies", "devDependencies", "optionalDependencies"];

    [GeneratedRegex(@"^(\^|~|>=|=)?v?(\d+(?:\.\d+){0,2}(?:-[0-9A-Za-z.\-]+)?)$")]
    private static partial Regex NpmSimple();

    private static List<Dependency> ParseNpm(string path, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        var reader = new Utf8JsonReader(bytes.AsSpan(bom), new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var result = new List<Dependency>();
        string? section = null;
        string? name = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.CurrentDepth == 1) section = NpmSections.Contains(reader.GetString()) ? reader.GetString() : null;
                else if (reader.CurrentDepth == 2 && section != null) name = reader.GetString();
            }
            else if (reader.TokenType == JsonTokenType.String && reader.CurrentDepth == 2 && section != null && name != null)
            {
                var value = reader.GetString() ?? "";
                var m = NpmSimple().Match(value.Trim());
                if (m.Success && !reader.ValueIsEscaped && value == value.Trim() && NpmName(name))
                {
                    var byteStart = bom + (int)reader.TokenStartIndex + 1; // after the opening quote
                    var start = Encoding.UTF8.GetCharCount(bytes, 0, byteStart);
                    result.Add(new Dependency(Ecosystems.Npm, path, name, value, m.Groups[2].Value, m.Groups[1].Value, section, start, value.Length));
                }
                name = null;
            }
            else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && reader.CurrentDepth == 2) name = null;
        }
        return result;
    }

    [GeneratedRegex(@"^(@[a-z0-9][a-z0-9._~-]*/)?[a-z0-9][a-z0-9._~-]*$")]
    private static partial Regex NpmNamePattern();
    public static bool NpmName(string name) => name.Length <= 214 && NpmNamePattern().IsMatch(name);

    // ----- NuGet -----

    [GeneratedRegex(@"<(PackageReference|PackageVersion)\b([^>]*?)(/?)>", RegexOptions.IgnoreCase)]
    private static partial Regex NuGetElement();
    [GeneratedRegex(@"\bInclude\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex NuGetInclude();
    [GeneratedRegex(@"\bVersion\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex NuGetVersionAttr();
    [GeneratedRegex(@"\G\s*<Version>([^<]*)</Version>", RegexOptions.IgnoreCase)]
    private static partial Regex NuGetVersionChild();
    [GeneratedRegex(@"^\d+(?:\.\d+){0,3}(?:-[0-9A-Za-z.\-]+)?$")]
    private static partial Regex NuGetSimple();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex NuGetNamePattern();
    public static bool NuGetName(string name) => name.Length <= 100 && NuGetNamePattern().IsMatch(name);

    private static List<Dependency> ParseNuGet(string path, string text)
    {
        var result = new List<Dependency>();
        foreach (Match el in NuGetElement().Matches(text))
        {
            var attrs = el.Groups[2].Value;
            var inc = NuGetInclude().Match(attrs);
            if (!inc.Success || !NuGetName(inc.Groups[1].Value)) continue;
            string value; int start;
            var attr = NuGetVersionAttr().Match(attrs);
            if (attr.Success) { value = attr.Groups[1].Value; start = el.Groups[2].Index + attr.Groups[1].Index; }
            else if (el.Groups[3].Value.Length == 0 && NuGetVersionChild().Match(text, el.Index + el.Length) is { Success: true } child)
            {
                value = child.Groups[1].Value; start = child.Groups[1].Index;
            }
            else continue;
            if (!NuGetSimple().IsMatch(value)) continue; // ranges, floating versions and $(properties) are left alone
            result.Add(new Dependency(Ecosystems.NuGet, path, inc.Groups[1].Value, value, value, "", el.Groups[1].Value.Equals("PackageVersion", StringComparison.OrdinalIgnoreCase) ? "central" : "project", start, value.Length));
        }
        return result;
    }

    // ----- pip -----

    [GeneratedRegex(@"^(?<name>[A-Za-z0-9][A-Za-z0-9._-]*)(?:\[[^\]]*\])?\s*==\s*(?<ver>[0-9][A-Za-z0-9.!+_-]*)(?=\s|;|#|\\|$)")]
    private static partial Regex PipLine();
    [GeneratedRegex(@"[""'](?<name>[A-Za-z0-9][A-Za-z0-9._-]*)(?:\[[^\]]*\])?\s*==\s*(?<ver>[0-9][A-Za-z0-9.!+_-]*)(?=[\s;,""'\)])")]
    private static partial Regex PyprojectPin();

    private static List<Dependency> ParseRequirements(string path, string text)
    {
        var result = new List<Dependency>();
        var offset = 0;
        foreach (var raw in text.Split('\n'))
        {
            var trimmed = raw.TrimStart();
            if (trimmed.Length > 0 && trimmed[0] != '#' && trimmed[0] != '-')
            {
                var m = PipLine().Match(trimmed);
                if (m.Success)
                {
                    var v = m.Groups["ver"];
                    result.Add(new Dependency(Ecosystems.Pip, path, m.Groups["name"].Value, v.Value, v.Value, "", "requirements", offset + (raw.Length - trimmed.Length) + v.Index, v.Length));
                }
            }
            offset += raw.Length + 1;
        }
        return result;
    }

    private static List<Dependency> ParsePyproject(string path, string text)
    {
        var result = new List<Dependency>();
        foreach (Match m in PyprojectPin().Matches(text))
        {
            var v = m.Groups["ver"];
            result.Add(new Dependency(Ecosystems.Pip, path, m.Groups["name"].Value, v.Value, v.Value, "", "pyproject", v.Index, v.Length));
        }
        return result;
    }

    // ----- Go -----

    [GeneratedRegex(@"^\s*(?:require\s+)?(?<mod>[A-Za-z0-9][^\s]*)\s+(?<ver>v[0-9][^\s]*)(?<rest>\s*//.*)?\s*$")]
    private static partial Regex GoRequire();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._~/-]*$")]
    private static partial Regex GoModulePattern();
    public static bool GoModule(string name) => name.Length <= 300 && GoModulePattern().IsMatch(name) && !name.Contains("..");

    private static List<Dependency> ParseGoMod(string path, string text)
    {
        var result = new List<Dependency>();
        var offset = 0;
        var inRequire = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var t = line.Trim();
            if (t.StartsWith("require", StringComparison.Ordinal) && t.EndsWith('(')) inRequire = true;
            else if (inRequire && t == ")") inRequire = false;
            else if ((inRequire && !t.StartsWith("//", StringComparison.Ordinal)) || t.StartsWith("require ", StringComparison.Ordinal))
            {
                var m = GoRequire().Match(line);
                if (m.Success && !m.Groups["rest"].Value.Contains("indirect", StringComparison.Ordinal) && GoModule(m.Groups["mod"].Value))
                {
                    var v = m.Groups["ver"];
                    result.Add(new Dependency(Ecosystems.Go, path, m.Groups["mod"].Value, v.Value, v.Value, "", "require", offset + v.Index, v.Length));
                }
            }
            offset += raw.Length + 1;
        }
        return result;
    }
}
