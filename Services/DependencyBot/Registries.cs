using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.DependencyBot;

/// <summary>What a registry knows about one package: the usable versions (not yanked, deprecated or unlisted) and where to read about a release.</summary>
public sealed record PackageInfo(IReadOnlyList<DepVersion> Versions, Func<DepVersion, string?> ReleaseUrl);

/// <summary>
/// Looks packages up in the registries' JSON APIs (npm registry, NuGet v3 flat container, PyPI JSON API or PEP 691 simple index, Go proxy)
/// through <see cref="RegistryHttp"/>: the public ones from <c>DependencyBot:Registries</c>, or the repository's private ones
/// (<see cref="RegistrySet"/>) with their credential. The package name comes from a manifest, so it is validated and escaped as one path
/// segment: it can never change the host or the path prefix of the configured base URL. URLs named inside a registry's answer
/// (npm <c>dist.tarball</c>, PyPI file URLs) are never fetched; a NuGet service index may only name resource URLs on the credential's own origin.
/// </summary>
public sealed partial class RegistryClient(RegistryHttp http, IOptionsMonitor<DependencyBotOptions> options)
{
    public Task<PackageInfo> GetAsync(string ecosystem, string name, CancellationToken ct) => GetAsync(ecosystem, name, RegistrySet.Empty, ct);

    /// <summary>Looks the package up in the repository's private registries (<paramref name="set"/>), else in the public one.</summary>
    public async Task<PackageInfo> GetAsync(string ecosystem, string name, RegistrySet set, CancellationToken ct) => ecosystem switch
    {
        Ecosystems.Npm => await NpmAsync(name, set, ct),
        Ecosystems.NuGet => await NuGetAsync(name, set, ct),
        Ecosystems.Pip => await PyPiAsync(name, set, ct),
        Ecosystems.Go => await GoAsync(name, set, ct),
        _ => throw new RegistryException("Unsupported ecosystem."),
    };

    private static string Base(string url) => url.EndsWith('/') ? url : url + "/";

    private static void EnsureUsable(RegistryEndpoint? endpoint)
    {
        if (endpoint?.BrokenReason is { } broken) throw new RegistryException(broken, fatalForRegistry: true);
    }

    private static JsonDocument ParseJson(string text)
    {
        try { return JsonDocument.Parse(text); }
        catch (JsonException) { throw new RegistryException("The registry's answer wasn't JSON (is the registry address right?)."); }
    }

    private static List<DepVersion> ParseAll(IEnumerable<string> texts) =>
        texts.Select(DepVersion.Parse).Where(v => v != null).Select(v => v!).ToList();

    // ----- npm -----

    private async Task<PackageInfo> NpmAsync(string name, RegistrySet set, CancellationToken ct)
    {
        if (!ManifestParser.NpmName(name)) throw new RegistryException("The package name isn't valid.");
        var endpoint = set.NpmFor(name);
        EnsureUsable(endpoint);
        var url = Base(endpoint?.Url ?? options.CurrentValue.Registries.Npm) + name.Replace("/", "%2F");
        using var doc = ParseJson(await http.GetStringAsync(url, "application/vnd.npm.install-v1+json, application/json;q=0.8", ct, endpoint?.Auth));
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

    private async Task<PackageInfo> NuGetAsync(string name, RegistrySet set, CancellationToken ct)
    {
        if (!ManifestParser.NuGetName(name)) throw new RegistryException("The package name isn't valid.");
        var id = name.ToLowerInvariant();
        if (set.NuGet.Count == 0)
            return NuGetInfo(name, await FlatContainerAsync(options.CurrentValue.Registries.NuGet, null, id, ct), publicSite: true);

        // Sources are asked in order and the first one that knows the package wins. A source that fails (other than "not found") is skipped
        // with its error remembered; a rejected credential or an unreachable registry ends the lookup.
        RegistryException? firstError = null;
        foreach (var source in set.NuGet)
        {
            try
            {
                EnsureUsable(source);
                var baseUrl = await ResolveNuGetBaseAsync(source, set, ct);
                var auth = source.Auth;
                if (auth != null && RegistryOrigin.Of(baseUrl) is { } baseOrigin && baseOrigin != auth.Origin) auth = auth.Rebind(baseOrigin); // allowed by ResolveNuGetBaseAsync
                return NuGetInfo(name, await FlatContainerAsync(baseUrl, auth, id, ct), publicSite: false);
            }
            catch (RegistryException ex) when (ex.NotFound) { }
            catch (RegistryException ex) when (!ex.FatalForRegistry) { firstError ??= ex; }
        }
        throw firstError ?? new RegistryException("None of the NuGet sources knows this package.", notFound: true);
    }

    private async Task<JsonDocument> FlatContainerAsync(string baseUrl, RegistryAuth? auth, string id, CancellationToken ct) =>
        ParseJson(await http.GetStringAsync(Base(baseUrl) + Uri.EscapeDataString(id) + "/index.json", "application/json", ct, auth));

    private static PackageInfo NuGetInfo(string name, JsonDocument doc, bool publicSite)
    {
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array)
                throw new RegistryException("The registry's answer has no versions.");
            var list = versions.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!);
            return new PackageInfo(ParseAll(list), publicSite ? v => $"https://www.nuget.org/packages/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(v.Text)}" : _ => null);
        }
    }

    /// <summary>
    /// A source address ending in <c>.json</c> is a NuGet v3 service index: its <c>PackageBaseAddress/3.0.0</c> resource is the flat container.
    /// Any other address is taken as the flat container itself. With a credential, the resource URL must have the source's own origin (or one
    /// listed as an additional origin); a foreign one is refused rather than fetched.
    /// </summary>
    internal async Task<string> ResolveNuGetBaseAsync(RegistryEndpoint source, RegistrySet set, CancellationToken ct)
    {
        if (!source.Url.Split('?')[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return source.Url;
        if (set.NuGetBases.TryGetValue(source.Url, out var cached)) return cached;
        if (set.NuGetFailures.TryGetValue(source.Url, out var failed)) throw failed;
        try
        {
            using var doc = ParseJson(await http.GetStringAsync(source.Url, "application/json", ct, source.Auth));
            string? resource = null;
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Array)
                foreach (var r in resources.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("@id", out var rid) || rid.ValueKind != JsonValueKind.String) continue;
                    var type = r.TryGetProperty("@type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    if (type != null && type.StartsWith("PackageBaseAddress/3.0.0", StringComparison.OrdinalIgnoreCase)) { resource = rid.GetString(); break; }
                }
            if (resource == null) throw new RegistryException("The NuGet service index has no PackageBaseAddress/3.0.0 resource (is the source address a v3 index.json?).");
            if (!Uri.TryCreate(resource, UriKind.Absolute, out var resourceUri) || resourceUri.Scheme is not ("http" or "https"))
                throw new RegistryException("The NuGet service index names an invalid package address.");
            var origin = RegistryOrigin.Of(resourceUri);
            if (source.Auth != null && origin != source.Auth.Origin && !source.ExtraOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
                throw new RegistryException($"The NuGet service index points to another address ({resourceUri.Host}); the credential is only sent to {new Uri(source.Auth.Origin).Host}. Add that origin under additional origins if you trust it.");
            return set.NuGetBases[source.Url] = resource;
        }
        catch (RegistryException ex) when (!ex.FatalForRegistry && !ex.NotFound)
        {
            set.NuGetFailures[source.Url] = ex;
            throw;
        }
    }

    // ----- PyPI -----

    /// <summary>A registry address whose last path segment is <c>simple</c> is a PEP 691 simple index; anything else is the PyPI JSON API (<c>{base}/{name}/json</c>).</summary>
    internal static bool IsSimpleIndex(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.AbsolutePath.TrimEnd('/').EndsWith("/simple", StringComparison.OrdinalIgnoreCase);

    private async Task<PackageInfo> PyPiAsync(string name, RegistrySet set, CancellationToken ct)
    {
        if (!PipName().IsMatch(name)) throw new RegistryException("The package name isn't valid.");
        var normalized = Regex.Replace(name.ToLowerInvariant(), @"[-_.]+", "-");
        var endpoint = set.Pip;
        EnsureUsable(endpoint);
        var baseUrl = Base(endpoint?.Url ?? options.CurrentValue.Registries.PyPI);
        if (endpoint != null && IsSimpleIndex(endpoint.Url))
        {
            using var simple = ParseJson(await http.GetStringAsync(baseUrl + Uri.EscapeDataString(normalized) + "/", "application/vnd.pypi.simple.v1+json, application/json;q=0.5", ct, endpoint.Auth));
            return new PackageInfo(ParseAll(SimpleVersions(simple.RootElement, normalized)), _ => null);
        }
        using var doc = ParseJson(await http.GetStringAsync(baseUrl + Uri.EscapeDataString(normalized) + "/json", "application/json", ct, endpoint?.Auth));
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

    /// <summary>
    /// The versions of a PEP 691 project page: read from the file names (sdist <c>name-1.2.3.tar.gz</c>, wheel <c>name-1.2.3-py3-none-any.whl</c>),
    /// plus the PEP 700 <c>versions</c> list when present; a version whose files are all yanked is left out. File URLs are never fetched.
    /// </summary>
    private static List<string> SimpleVersions(JsonElement root, string normalized)
    {
        var byVersion = new Dictionary<string, bool>(StringComparer.Ordinal); // version -> every file yanked
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            foreach (var f in files.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.Object || !f.TryGetProperty("filename", out var fn) || fn.ValueKind != JsonValueKind.String) continue;
                var version = VersionOfFile(fn.GetString()!, normalized);
                if (version == null) continue;
                var yanked = f.TryGetProperty("yanked", out var y) && (y.ValueKind == JsonValueKind.True || y.ValueKind == JsonValueKind.String);
                byVersion[version] = byVersion.TryGetValue(version, out var all) ? all && yanked : yanked;
            }
        var result = byVersion.Where(p => !p.Value).Select(p => p.Key).ToList();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("versions", out var listed) && listed.ValueKind == JsonValueKind.Array)
            foreach (var v in listed.EnumerateArray())
                if (v.ValueKind == JsonValueKind.String && !byVersion.ContainsKey(v.GetString()!)) result.Add(v.GetString()!);
        return result;
    }

    private static string? VersionOfFile(string filename, string normalized)
    {
        var whl = Regex.Match(filename, @"^(?<n>[^-]+)-(?<v>[^-]+)-.+\.whl$");
        if (whl.Success && Regex.Replace(whl.Groups["n"].Value.ToLowerInvariant(), @"[-_.]+", "-") == normalized) return whl.Groups["v"].Value;
        var sdist = Regex.Match(filename, @"^(?<n>.+?)-(?<v>\d[^-]*)\.(tar\.gz|zip|tar\.bz2|tgz)$");
        if (sdist.Success && Regex.Replace(sdist.Groups["n"].Value.ToLowerInvariant(), @"[-_.]+", "-") == normalized) return sdist.Groups["v"].Value;
        return null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex PipName();

    // ----- Go -----

    private async Task<PackageInfo> GoAsync(string module, RegistrySet set, CancellationToken ct)
    {
        if (!ManifestParser.GoModule(module)) throw new RegistryException("The module path isn't valid.");
        var endpoint = set.Go;
        EnsureUsable(endpoint);
        // The proxy protocol escapes upper-case letters as "!" + lower-case.
        var escaped = Regex.Replace(module, "[A-Z]", m => "!" + char.ToLowerInvariant(m.Value[0]));
        var url = Base(endpoint?.Url ?? options.CurrentValue.Registries.Go) + string.Join("/", escaped.Split('/').Select(Uri.EscapeDataString)) + "/@v/list";
        var text = await http.GetStringAsync(url, "text/plain", ct, endpoint?.Auth);
        var versions = ParseAll(text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l.StartsWith('v')));
        var github = Regex.Match(module, @"^github\.com/([^/]+)/([^/]+)");
        return new PackageInfo(versions, v => github.Success ? $"https://github.com/{github.Groups[1].Value}/{github.Groups[2].Value}/releases/tag/{v.Text}" : null);
    }
}
