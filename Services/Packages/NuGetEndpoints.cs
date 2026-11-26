using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ByteBench.Data;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace ByteBench.Services.Packages;

/// <summary>
/// A NuGet v3 feed per project at <c>/packages/{KEY}/nuget/index.json</c>:
/// <list type="bullet">
/// <item><c>PUT /packages/{KEY}/nuget</c>: <c>dotnet nuget push</c> (multipart, <c>X-NuGet-ApiKey</c> = a personal access
/// token or CI job token). 201, or 409 when the version exists.</item>
/// <item><c>DELETE /packages/{KEY}/nuget/{id}/{version}</c>: <c>dotnet nuget delete</c> (Maintainer).</item>
/// <item>PackageBaseAddress (<c>v3/flatcontainer/{id}/index.json</c>, <c>.nupkg</c>, <c>.nuspec</c>), registrations
/// (<c>v3/registration/{id}/index.json</c> with inline pages, and leaves) and search (<c>query</c>) for restore.</item>
/// </list>
/// Restore authenticates with Basic (user name + token). See <see cref="RegistryAuth"/>.
/// </summary>
public static partial class NuGetEndpoints
{
    private const string Challenge = "Basic realm=\"ByteBench NuGet\"";

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]{0,99}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^(\d+)(\.\d+){0,3}(-[0-9A-Za-z\-.]+)?$")]
    private static partial Regex VersionPattern();

    public static IEndpointRouteBuilder MapNuGetFeed(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/packages/{projectKey}/nuget").DisableAntiforgery();
        g.MapGet("/index.json", ServiceIndexAsync);
        g.MapPut("", PushAsync);
        g.MapDelete("/{id}/{version}", DeleteAsync);
        g.MapGet("/v3/flatcontainer/{id}/index.json", VersionsAsync);
        g.MapGet("/v3/flatcontainer/{id}/{version}/{file}", DownloadAsync);
        g.MapGet("/v3/registration/{id}/index.json", RegistrationIndexAsync);
        g.MapGet("/v3/registration/{id}/{version}.json", RegistrationLeafAsync);
        g.MapGet("/query", SearchAsync);
        return app;
    }

    private static Task Fail(HttpContext ctx, int status, string message) =>
        RegistryHttp.JsonAsync(ctx, status, new { message });

    private static Task<RegistryRequest?> GuardAsync(HttpContext ctx, string projectKey, RegistryOp op, bool allowCookie = false)
    {
        RegistryHttp.Prepare(ctx);
        return RegistryHttp.GuardAsync(ctx, projectKey, op, Challenge, (s, m) => Fail(ctx, s, m), allowCookie);
    }

    private static string Feed(HttpContext ctx, Project p) =>
        $"{ctx.RequestServices.GetRequiredService<Api.PublicBaseUrl>().For(ctx.Request)}/packages/{p.Key}/nuget";

    // ---------------------------------------------------------------- versions

    /// <summary>
    /// NuGet's normalized form of <paramref name="version"/> (no build metadata, no leading zeros, three or four parts),
    /// or null if it isn't a valid version.
    /// </summary>
    public static string? NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var v = version.Trim();
        var plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        if (!VersionPattern().IsMatch(v)) return null;
        var dash = v.IndexOf('-');
        var release = dash >= 0 ? v[dash..] : "";
        var parts = (dash >= 0 ? v[..dash] : v).Split('.').Select(p => long.TryParse(p, out var n) ? n : -1).ToList();
        if (parts.Any(p => p < 0)) return null;
        while (parts.Count < 3) parts.Add(0);
        if (parts.Count == 4 && parts[3] == 0) parts.RemoveAt(3);
        return string.Join('.', parts) + release;
    }

    /// <summary>Orders NuGet versions (numeric parts, then a release label sorts before the release itself).</summary>
    public static int CompareVersions(string a, string b)
    {
        static (long[] Nums, string Label) Split(string v)
        {
            var dash = v.IndexOf('-');
            var nums = (dash >= 0 ? v[..dash] : v).Split('.').Select(p => long.TryParse(p, out var n) ? n : 0).ToArray();
            return (nums, dash >= 0 ? v[(dash + 1)..] : "");
        }
        var (an, al) = Split(a);
        var (bn, bl) = Split(b);
        for (var i = 0; i < 4; i++)
        {
            var c = (i < an.Length ? an[i] : 0).CompareTo(i < bn.Length ? bn[i] : 0);
            if (c != 0) return c;
        }
        if (al.Length == 0 || bl.Length == 0) return bl.Length.CompareTo(al.Length);
        return string.Compare(al, bl, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- service index

    private static async Task ServiceIndexAsync(HttpContext ctx, string projectKey)
    {
        if (await GuardAsync(ctx, projectKey, RegistryOp.Read) is not { } req) return;
        var feed = Feed(ctx, req.Project);
        var resources = new JsonArray();
        void Add(string id, string type, string? comment = null) =>
            resources.Add(new JsonObject { ["@id"] = id, ["@type"] = type, ["comment"] = comment });
        Add(feed, "PackagePublish/2.0.0");
        Add(feed + "/v3/flatcontainer/", "PackageBaseAddress/3.0.0");
        foreach (var t in new[] { "SearchQueryService", "SearchQueryService/3.0.0-beta", "SearchQueryService/3.0.0-rc", "SearchQueryService/3.5.0" })
            Add(feed + "/query", t);
        foreach (var t in new[] { "RegistrationsBaseUrl", "RegistrationsBaseUrl/3.0.0-beta", "RegistrationsBaseUrl/3.0.0-rc", "RegistrationsBaseUrl/3.6.0" })
            Add(feed + "/v3/registration/", t);
        await RegistryHttp.JsonAsync(ctx, 200, new JsonObject { ["version"] = "3.0.0", ["resources"] = resources });
    }

    // ---------------------------------------------------------------- push / delete

    private sealed record NuSpec(string Id, string Version, JsonObject Metadata);

    private static async Task PushAsync(HttpContext ctx, string projectKey)
    {
        if (await GuardAsync(ctx, projectKey, RegistryOp.Write) is not { } req) return;
        var storage = ctx.RequestServices.GetRequiredService<PackageStorage>();
        RegistryHttp.AllowLargeBody(ctx, storage.MaxBlobBytes);

        StoredBlob blob;
        try
        {
            var body = ctx.Request.Body;
            if (MediaTypeHeaderValue.TryParse(ctx.Request.ContentType, out var type) && type.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            {
                var boundary = HeaderUtilities.RemoveQuotes(type.Boundary).Value;
                if (string.IsNullOrEmpty(boundary)) { await Fail(ctx, 400, "Missing multipart boundary."); return; }
                var reader = new MultipartReader(boundary, body) { BodyLengthLimit = storage.MaxBlobBytes };
                var section = await reader.ReadNextSectionAsync(ctx.RequestAborted);
                if (section == null) { await Fail(ctx, 400, "The upload has no package."); return; }
                blob = await storage.SaveAsync(section.Body, null, ctx.RequestAborted);
            }
            else blob = await storage.SaveAsync(body, null, ctx.RequestAborted);
        }
        catch (PackageUploadException ex) { await Fail(ctx, ex.Status, ex.Message); return; }
        catch (IOException ex) when (ex is not FileNotFoundException) { await Fail(ctx, 400, "The upload could not be read: " + ex.Message); return; }

        NuSpec? spec;
        try { spec = ReadNuSpec(storage.PathFor(blob.Sha256)); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Xml.XmlException) { spec = null; }
        if (spec == null)
        {
            await storage.DeleteUnreferencedAsync([blob.Sha256]);
            await Fail(ctx, 400, "Not a valid .nupkg: it needs a .nuspec with a valid id and version.");
            return;
        }

        var packages = ctx.RequestServices.GetRequiredService<PackageService>();
        var package = await packages.GetOrCreatePackageAsync(req.Project.Id, PackageType.NuGet, spec.Id);
        var normalized = spec.Version.ToLowerInvariant();
        var version = new PackageVersion
        {
            PackageId = package.Id, Version = spec.Version, NormalizedVersion = normalized, Size = blob.Size,
            MetadataJson = spec.Metadata.ToJsonString(), PublishedById = req.Identity.ActorId, PipelineId = req.Identity.Job?.PipelineId,
            Files = [new PackageFile { FileName = $"{spec.Id}.{spec.Version}.nupkg".ToLowerInvariant(), Sha256 = blob.Sha256, Sha1 = blob.Sha1, Sha512 = blob.Sha512Base64, Size = blob.Size }],
        };
        await using (var db = await ctx.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(CancellationToken.None))
        {
            if (await db.PackageVersions.AnyAsync(v => v.PackageId == package.Id && v.NormalizedVersion == normalized))
            {
                await storage.DeleteUnreferencedAsync([blob.Sha256]);
                await Fail(ctx, 409, $"{spec.Id} {spec.Version} already exists. Versions can't be overwritten; delete it first or publish a new version.");
                return;
            }
            db.PackageVersions.Add(version);
            await db.Packages.Where(p => p.Id == package.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.UpdatedAt, DateTime.UtcNow));
            try { await db.SaveChangesAsync(CancellationToken.None); }
            catch (DbUpdateException) { await Fail(ctx, 409, $"{spec.Id} {spec.Version} already exists."); return; }
        }
        await packages.RecordPublishedAsync(req.Project, package, version, req.Identity);
        ctx.Response.StatusCode = 201;
    }

    /// <summary>The id, normalized version and metadata of a .nupkg (null when it has no valid .nuspec).</summary>
    private static NuSpec? ReadNuSpec(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.Entries.FirstOrDefault(e => !e.FullName.Contains('/') && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        if (entry == null || entry.Length > 1024 * 1024) return null;
        using var stream = entry.Open();
        // No DOCTYPE at all: nothing to expand (XDocument.Load(Stream) would parse DTDs, capped only at 10M characters of entities).
        using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings
        {
            DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024,
        });
        var doc = XDocument.Load(reader);
        var meta = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
        string? Get(string name) => meta?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
        var id = Get("id");
        var version = NormalizeVersion(Get("version"));
        if (id == null || !IdPattern().IsMatch(id) || version == null) return null;

        var groups = new JsonArray();
        var deps = meta!.Elements().FirstOrDefault(e => e.Name.LocalName == "dependencies");
        if (deps != null)
        {
            JsonArray List(XElement parent) => [.. parent.Elements().Where(e => e.Name.LocalName == "dependency")
                .Select(d => (JsonNode)new JsonObject { ["id"] = (string?)d.Attribute("id"), ["range"] = (string?)d.Attribute("version") })];
            var groupElements = deps.Elements().Where(e => e.Name.LocalName == "group").ToList();
            foreach (var g in groupElements)
                groups.Add(new JsonObject { ["targetFramework"] = (string?)g.Attribute("targetFramework"), ["dependencies"] = List(g) });
            if (groupElements.Count == 0 && deps.Elements().Any())
                groups.Add(new JsonObject { ["dependencies"] = List(deps) });
        }
        var metadata = new JsonObject
        {
            ["description"] = Get("description"),
            ["authors"] = Get("authors"),
            ["title"] = Get("title"),
            ["tags"] = Get("tags"),
            ["projectUrl"] = Get("projectUrl"),
            ["license"] = Get("license"),
            ["dependencyGroups"] = groups,
        };
        return new NuSpec(id, version, metadata);
    }

    private static async Task DeleteAsync(HttpContext ctx, string projectKey, string id, string version)
    {
        if (await GuardAsync(ctx, projectKey, RegistryOp.Delete) is not { } req) return;
        var packages = ctx.RequestServices.GetRequiredService<PackageService>();
        var normalized = NormalizeVersion(version)?.ToLowerInvariant();
        var package = await packages.FindPackageAsync(req.Project.Id, PackageType.NuGet, id);
        int? versionId = null;
        if (package != null && normalized != null)
        {
            await using var db = await ctx.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(ctx.RequestAborted);
            versionId = await db.PackageVersions.Where(v => v.PackageId == package.Id && v.NormalizedVersion == normalized).Select(v => (int?)v.Id).FirstOrDefaultAsync();
        }
        if (versionId == null || !await packages.DeleteVersionCoreAsync(package!.Id, versionId.Value, req.Identity.ActorId, req.Identity))
        {
            await Fail(ctx, 404, "Package version not found.");
            return;
        }
        ctx.Response.StatusCode = 204;
    }

    // ---------------------------------------------------------------- restore

    private static async Task<(Package Package, List<PackageVersion> Versions)?> LoadAsync(HttpContext ctx, RegistryRequest req, string id)
    {
        var normalized = id.ToLowerInvariant();
        await using var db = await ctx.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(ctx.RequestAborted);
        var package = await db.Packages.AsNoTracking().Include(p => p.Versions).ThenInclude(v => v.Files)
            .FirstOrDefaultAsync(p => p.ProjectId == req.Project.Id && p.Type == PackageType.NuGet && p.NormalizedName == normalized, ctx.RequestAborted);
        if (package == null || package.Versions.Count == 0) return null;
        var versions = package.Versions.ToList();
        versions.Sort((a, b) => CompareVersions(a.Version, b.Version));
        return (package, versions);
    }

    private static async Task VersionsAsync(HttpContext ctx, string projectKey, string id)
    {
        if (await GuardAsync(ctx, projectKey, RegistryOp.Read) is not { } req) return;
        if (await LoadAsync(ctx, req, id) is not { } found) { await Fail(ctx, 404, "Package not found."); return; }
        await RegistryHttp.JsonAsync(ctx, 200, new { versions = found.Versions.Select(v => v.NormalizedVersion).ToList() });
    }

    private static async Task DownloadAsync(HttpContext ctx, string projectKey, string id, string version, string file)
    {
        if (await GuardAsync(ctx, projectKey, RegistryOp.Read, allowCookie: true) is not { } req) return;
        var found = await LoadAsync(ctx, req, id);
        var v = found?.Versions.FirstOrDefault(x => x.NormalizedVersion == version.ToLowerInvariant());
        var f = v?.Files.FirstOrDefault();
        var storage = ctx.RequestServices.GetRequiredService<PackageStorage>();
        if (v == null || f == null || !storage.Exists(f.Sha256)) { await Fail(ctx, 404, "Package not found."); return; }
        var stem = $"{found!.Value.Package.NormalizedName}.{v.NormalizedVersion}";
        ctx.Response.Headers.XContentTypeOptions = "nosniff";
        if (file.Equals(stem + ".nupkg", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{stem}.nupkg\"";
            ctx.Response.ContentLength = f.Size;
            await ctx.Response.SendFileAsync(storage.PathFor(f.Sha256), ctx.RequestAborted);
            return;
        }
        if (file.Equals(found.Value.Package.NormalizedName + ".nuspec", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(storage.PathFor(f.Sha256));
            var entry = zip.Entries.FirstOrDefault(e => !e.FullName.Contains('/') && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            if (entry == null) { await Fail(ctx, 404, "Not found."); return; }
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/xml";
            await using var s = entry.Open();
            await s.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
            return;
        }
        await Fail(ctx, 404, "Not found.");
    }

    private static JsonObject CatalogEntry(string feed, Package p, PackageVersion v)
    {
        var meta = v.MetadataJson != null ? JsonNode.Parse(v.MetadataJson) as JsonObject ?? [] : [];
        var leaf = $"{feed}/v3/registration/{p.NormalizedName}/{v.NormalizedVersion}.json";
        var groups = new JsonArray();
        if (meta["dependencyGroups"] is JsonArray gs)
            foreach (var g in gs.OfType<JsonObject>())
            {
                var deps = new JsonArray();
                if (g["dependencies"] is JsonArray ds)
                    foreach (var d in ds.OfType<JsonObject>())
                        deps.Add(new JsonObject
                        {
                            ["@id"] = leaf + "#dependency/" + d["id"],
                            ["id"] = d["id"]?.DeepClone(),
                            ["range"] = d["range"]?.GetValue<string>() is { Length: > 0 } r ? r : "(, )",
                            ["registration"] = $"{feed}/v3/registration/{d["id"]?.GetValue<string>()?.ToLowerInvariant()}/index.json",
                        });
                groups.Add(new JsonObject { ["@id"] = leaf + "#dependencygroup", ["targetFramework"] = g["targetFramework"]?.DeepClone(), ["dependencies"] = deps });
            }
        return new JsonObject
        {
            ["@id"] = leaf,
            ["@type"] = "PackageDetails",
            ["id"] = p.Name,
            ["version"] = v.Version,
            ["description"] = meta["description"]?.DeepClone() ?? "",
            ["authors"] = meta["authors"]?.DeepClone() ?? "",
            ["title"] = meta["title"]?.DeepClone(),
            ["tags"] = meta["tags"]?.DeepClone(),
            ["projectUrl"] = meta["projectUrl"]?.DeepClone(),
            ["listed"] = true,
            ["published"] = Api.Dto.Utc(v.CreatedAt).ToString("o"),
            ["packageContent"] = PackageContent(feed, p, v),
            ["dependencyGroups"] = groups,
        };
    }

    private static string PackageContent(string feed, Package p, PackageVersion v) =>
        $"{feed}/v3/flatcontainer/{p.NormalizedName}/{v.NormalizedVersion}/{p.NormalizedName}.{v.NormalizedVersion}.nupkg";

    private static async Task RegistrationIndexAsync(HttpContext ctx, string projectKey, string id)
    {
        if (await GuardAsync(ctx, projectKey, RegistryOp.Read) is not { } req) return;
        if (await LoadAsync(ctx, req, id) is not { } found) { await Fail(ctx, 404, "Package not found."); return; }
        var feed = Feed(ctx, req.Project);
        var (p, versions) = found;
        var index = $"{feed}/v3/registration/{p.NormalizedName}/index.json";
        var items = new JsonArray();
        foreach (var v in versions)
            items.Add(new JsonObject
            {
                ["@id"] = $"{feed}/v3/registration/{p.NormalizedName}/{v.NormalizedVersion}.json",
                ["@type"] = "Package",
                ["catalogEntry"] = CatalogEntry(feed, p, v),
                ["packageContent"] = PackageContent(feed, p, v),
                ["registration"] = index,
            });
        var page = new JsonObject
        {
            ["@id"] = $"{index}#page/{versions[0].NormalizedVersion}/{versions[^1].NormalizedVersion}",
            ["@type"] = "catalog:CatalogPage",
            ["count"] = versions.Count,
            ["lower"] = versions[0].NormalizedVersion,
            ["upper"] = versions[^1].NormalizedVersion,
            ["parent"] = index,
            ["items"] = items,
        };
        await RegistryHttp.JsonAsync(ctx, 200, new JsonObject
        {
            ["@id"] = index,
            ["@type"] = new JsonArray("catalog:CatalogRoot", "PackageRegistration", "catalog:Permalink"),
            ["count"] = 1,
            ["items"] = new JsonArray(page),
        });
    }

    private static async Task RegistrationLeafAsync(HttpContext ctx, string projectKey, string id, string version)
    {
        if (await GuardAsync(ctx, projectKey, RegistryOp.Read) is not { } req) return;
        var found = await LoadAsync(ctx, req, id);
        var v = found?.Versions.FirstOrDefault(x => x.NormalizedVersion == version.ToLowerInvariant());
        if (v == null) { await Fail(ctx, 404, "Package version not found."); return; }
        var feed = Feed(ctx, req.Project);
        var p = found!.Value.Package;
        await RegistryHttp.JsonAsync(ctx, 200, new JsonObject
        {
            ["@id"] = $"{feed}/v3/registration/{p.NormalizedName}/{v.NormalizedVersion}.json",
            ["@type"] = new JsonArray("Package", "http://schema.nuget.org/catalog#Permalink"),
            ["catalogEntry"] = CatalogEntry(feed, p, v),
            ["listed"] = true,
            ["packageContent"] = PackageContent(feed, p, v),
            ["published"] = Api.Dto.Utc(v.CreatedAt).ToString("o"),
            ["registration"] = $"{feed}/v3/registration/{p.NormalizedName}/index.json",
        });
    }

    private static async Task SearchAsync(HttpContext ctx, string projectKey)
    {
        if (await GuardAsync(ctx, projectKey, RegistryOp.Read) is not { } req) return;
        var q = ctx.Request.Query["q"].ToString().Trim();
        var skip = int.TryParse(ctx.Request.Query["skip"], out var s) ? Math.Max(0, s) : 0;
        var take = int.TryParse(ctx.Request.Query["take"], out var t) ? Math.Clamp(t, 0, 100) : 20;
        var prerelease = bool.TryParse(ctx.Request.Query["prerelease"], out var pr) && pr;
        List<Package> packages;
        await using (var db = await ctx.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(ctx.RequestAborted))
        {
            var query = db.Packages.AsNoTracking().Include(p => p.Versions)
                .Where(p => p.ProjectId == req.Project.Id && p.Type == PackageType.NuGet);
            if (q.Length > 0) query = query.Where(p => p.NormalizedName.Contains(q.ToLower()));
            packages = await query.OrderBy(p => p.NormalizedName).ToListAsync(ctx.RequestAborted);
        }
        var feed = Feed(ctx, req.Project);
        var hits = packages
            .Select(p => (Package: p, Versions: p.Versions.Where(v => prerelease || !v.Version.Contains('-')).ToList()))
            .Where(x => x.Versions.Count > 0).ToList();
        var data = new JsonArray();
        foreach (var (p, vs) in hits.Skip(skip).Take(take))
        {
            vs.Sort((a, b) => CompareVersions(a.Version, b.Version));
            var latest = vs[^1];
            var meta = latest.MetadataJson != null ? JsonNode.Parse(latest.MetadataJson) as JsonObject ?? [] : [];
            var index = $"{feed}/v3/registration/{p.NormalizedName}/index.json";
            data.Add(new JsonObject
            {
                ["@id"] = index,
                ["@type"] = "Package",
                ["registration"] = index,
                ["id"] = p.Name,
                ["version"] = latest.Version,
                ["description"] = meta["description"]?.DeepClone() ?? "",
                ["authors"] = new JsonArray((meta["authors"]?.GetValue<string>() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(a => (JsonNode)a).ToArray()),
                ["totalDownloads"] = 0,
                ["verified"] = false,
                ["versions"] = new JsonArray(vs.Select(v => (JsonNode)new JsonObject
                {
                    ["version"] = v.Version, ["downloads"] = 0,
                    ["@id"] = $"{feed}/v3/registration/{p.NormalizedName}/{v.NormalizedVersion}.json",
                }).ToArray()),
            });
        }
        await RegistryHttp.JsonAsync(ctx, 200, new JsonObject { ["totalHits"] = hits.Count, ["data"] = data });
    }
}
