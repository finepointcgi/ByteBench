using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.DependencyBot;

/// <summary>What a registry knows about one package: the usable versions (not yanked, deprecated or unlisted) and where to read about a release.</summary>
public sealed record PackageInfo(IReadOnlyList<DepVersion> Versions, Func<DepVersion, string?> ReleaseUrl);

/// <summary>
/// Looks packages up in the public registries' JSON APIs (npm registry, NuGet v3 flat container, PyPI JSON API, Go proxy)
/// through <see cref="RegistryHttp"/>. The package name comes from a manifest, so it is validated and escaped as one path
/// segment: it can never change the host or the path prefix of the configured base URL. Private registries are not supported.
/// </summary>
public sealed partial class RegistryClient(RegistryHttp http, IOptionsMonitor<DependencyBotOptions> options)
{
    public async Task<PackageInfo> GetAsync(string ecosystem, string name, CancellationToken ct) => ecosystem switch
    {
        Ecosystems.Npm => await NpmAsync(name, ct),
        Ecosystems.NuGet => await NuGetAsync(name, ct),
        Ecosystems.Pip => await PyPiAsync(name, ct),
        Ecosystems.Go => await GoAsync(name, ct),
        _ => throw new RegistryException("Unsupported ecosystem."),
    };

    private static string Base(string url) => url.EndsWith('/') ? url : url + "/";

    private static JsonDocument ParseJson(string text)
    {
        try { return JsonDocument.Parse(text); }
        catch (JsonException) { throw new RegistryException("The registry's answer wasn't JSON (is the registry address right?)."); }
    }

    private static List<DepVersion> ParseAll(IEnumerable<string> texts) =>
        texts.Select(DepVersion.Parse).Where(v => v != null).Select(v => v!).ToList();

    // ----- npm -----

    private async Task<PackageInfo> NpmAsync(string name, CancellationToken ct)
    {
        if (!ManifestParser.NpmName(name)) throw new RegistryException("The package name isn't valid.");
        var url = Base(options.CurrentValue.Registries.Npm) + name.Replace("/", "%2F");
        using var doc = ParseJson(await http.GetStringAsync(url, "application/vnd.npm.install-v1+json, application/json;q=0.8", ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Object)
            throw new RegistryException("The registry's answer has no versions.");
        var usable = new List<string>();
        foreach (var v in versions.EnumerateObject())
        {
            if (v.Value.ValueKind == JsonValueKind.Object && v.Value.TryGetProperty("deprecated", out var dep) && dep.ValueKind == JsonValueKind.String && dep.GetString()!.Length > 0) continue;
            usable.Add(v.Name);
        }
        var home = HttpsOnly(doc.RootElement.TryGetProperty("homepage", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null)
                   ?? RepositoryUrl(doc.RootElement);
        return new PackageInfo(ParseAll(usable), v => home != null && home.Contains("github.com", StringComparison.Ordinal) ? home.TrimEnd('/') + "/releases/tag/v" + v.Text : home);
    }

    private static string? RepositoryUrl(JsonElement root)
    {
        if (!root.TryGetProperty("repository", out var r)) return null;
        var url = r.ValueKind == JsonValueKind.String ? r.GetString() : r.ValueKind == JsonValueKind.Object && r.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        if (url == null) return null;
        url = Regex.Replace(url, @"^git\+", "");
        url = Regex.Replace(url, @"^git://", "https://");
        url = Regex.Replace(url, @"^ssh://git@", "https://");
        url = Regex.Replace(url, @"\.git$", "");
        return HttpsOnly(url);
    }

    /// <summary>Only http(s) URLs from a registry are ever shown as links.</summary>
    private static string? HttpsOnly(string? url) =>
        url != null && url.Length <= 500 && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && !url.Any(char.IsWhiteSpace) ? url : null;

    // ----- NuGet -----

    private async Task<PackageInfo> NuGetAsync(string name, CancellationToken ct)
    {
        if (!ManifestParser.NuGetName(name)) throw new RegistryException("The package name isn't valid.");
        var id = name.ToLowerInvariant();
        using var doc = ParseJson(await http.GetStringAsync(Base(options.CurrentValue.Registries.NuGet) + Uri.EscapeDataString(id) + "/index.json", "application/json", ct));
        if (!doc.RootElement.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array)
            throw new RegistryException("The registry's answer has no versions.");
        var list = versions.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!);
        return new PackageInfo(ParseAll(list), v => $"https://www.nuget.org/packages/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(v.Text)}");
    }

    // ----- PyPI -----

    private async Task<PackageInfo> PyPiAsync(string name, CancellationToken ct)
    {
        if (!PipName().IsMatch(name)) throw new RegistryException("The package name isn't valid.");
        var normalized = Regex.Replace(name.ToLowerInvariant(), @"[-_.]+", "-");
        using var doc = ParseJson(await http.GetStringAsync(Base(options.CurrentValue.Registries.PyPI) + Uri.EscapeDataString(normalized) + "/json", "application/json", ct));
        if (!doc.RootElement.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Object)
            throw new RegistryException("The registry's answer has no releases.");
        var usable = new List<string>();
        foreach (var r in releases.EnumerateObject())
        {
            if (r.Value.ValueKind != JsonValueKind.Array || r.Value.GetArrayLength() == 0) continue;
            // Yanked releases (every file yanked) are not offered.
            if (r.Value.EnumerateArray().All(f => f.ValueKind == JsonValueKind.Object && f.TryGetProperty("yanked", out var y) && y.ValueKind == JsonValueKind.True)) continue;
            usable.Add(r.Name);
        }
        string? notes = null;
        if (doc.RootElement.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            if (info.TryGetProperty("project_urls", out var urls) && urls.ValueKind == JsonValueKind.Object)
                notes = urls.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String && Regex.IsMatch(p.Name, "change|release|history|news", RegexOptions.IgnoreCase))
                    .Select(p => HttpsOnly(p.Value.GetString())).FirstOrDefault(u => u != null);
            notes ??= HttpsOnly(info.TryGetProperty("project_url", out var pu) && pu.ValueKind == JsonValueKind.String ? pu.GetString() : null);
        }
        return new PackageInfo(ParseAll(usable), _ => notes);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex PipName();

    // ----- Go -----

    private async Task<PackageInfo> GoAsync(string module, CancellationToken ct)
    {
        if (!ManifestParser.GoModule(module)) throw new RegistryException("The module path isn't valid.");
        // The proxy protocol escapes upper-case letters as "!" + lower-case.
        var escaped = Regex.Replace(module, "[A-Z]", m => "!" + char.ToLowerInvariant(m.Value[0]));
        var url = Base(options.CurrentValue.Registries.Go) + string.Join("/", escaped.Split('/').Select(Uri.EscapeDataString)) + "/@v/list";
        var text = await http.GetStringAsync(url, "text/plain", ct);
        var versions = ParseAll(text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l.StartsWith('v')));
        var github = Regex.Match(module, @"^github\.com/([^/]+)/([^/]+)");
        return new PackageInfo(versions, v => github.Success ? $"https://github.com/{github.Groups[1].Value}/{github.Groups[2].Value}/releases/tag/{v.Text}" : null);
    }
}
