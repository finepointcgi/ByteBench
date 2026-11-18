using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Packages;

/// <summary>
/// An npm registry per project at <c>/packages/{KEY}/npm/</c> (set it as the registry, or as a scope's registry, with
/// <c>//host/packages/{KEY}/npm/:_authToken=&lt;token&gt;</c>):
/// <list type="bullet">
/// <item><c>GET /{name}</c>: the packument (all versions, dist-tags, tarball URLs with <c>shasum</c> and
/// <c>integrity</c>); <c>GET /{name}/{version}</c>; <c>GET /{name}/-/{file}.tgz</c>: the tarball.</item>
/// <item><c>PUT /{name}</c>: <c>npm publish</c> (Developer). The tarball's SHA-1/SHA-512 must match the <c>dist</c> the
/// client sent; an existing version is 403 like the public registry.</item>
/// <item><c>PUT /{name}/-rev/{rev}</c>: <c>npm unpublish pkg@version</c> (versions missing from the body are deleted:
/// Maintainer) and <c>npm deprecate</c>; <c>DELETE /{name}/-rev/{rev}</c>: unpublish everything (Maintainer).</item>
/// <item><c>/-/package/{name}/dist-tags[/{tag}]</c>: <c>npm dist-tag ls/add/rm</c>; <c>/-/whoami</c>, <c>/-/ping</c>,
/// <c>/-/v1/search</c>.</item>
/// </list>
/// Scoped names arrive as <c>@scope%2fname</c> or <c>@scope/name</c>; both work. See <see cref="RegistryAuth"/>.
/// </summary>
public static partial class NpmEndpoints
{
    private const string Challenge = "Basic realm=\"GitBench npm\"";
    public const int MaxNameLength = 214;

    [GeneratedRegex(@"^(@[a-z0-9\-~][a-z0-9\-._~]*/)?[a-z0-9\-~][a-z0-9\-._~]*$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z\-.]+)?(\+[0-9A-Za-z\-.]+)?$")]
    private static partial Regex SemVer();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9\-._]{0,127}$")]
    private static partial Regex TagPattern();

    public static bool IsValidName(string name) => name.Length <= MaxNameLength && NamePattern().IsMatch(name);

    public static IEndpointRouteBuilder MapNpmRegistry(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/packages/{projectKey}/npm/{**rest}", ["GET", "PUT", "DELETE"], DispatchAsync).DisableAntiforgery();
        return app;
    }

    private static Task Fail(HttpContext ctx, int status, string message) =>
        RegistryHttp.JsonAsync(ctx, status, new { error = message, message });

    private static Task<RegistryRequest?> GuardAsync(HttpContext ctx, string projectKey, RegistryOp op, bool allowCookie = false) =>
        RegistryHttp.GuardAsync(ctx, projectKey, op, Challenge, (s, m) => Fail(ctx, s, m), allowCookie);

    private static string Registry(HttpContext ctx, Project p) =>
        PackageUrls.NpmRegistry(ctx.RequestServices.GetRequiredService<Api.PublicBaseUrl>().For(ctx.Request), p.Key);

    private static async Task DispatchAsync(HttpContext ctx, string projectKey)
    {
        RegistryHttp.Prepare(ctx);
        // The raw path: Kestrel keeps %2F encoded, so scoped names arrive as one segment.
        var prefix = $"/packages/{projectKey}/npm/";
        var raw = ctx.Request.Path.Value ?? "";
        var rest = raw.Length > prefix.Length ? raw[prefix.Length..] : "";
        var segments = rest.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString)
            .SelectMany(s => s.Split('/')).Where(s => s.Length > 0).ToList();
        var method = ctx.Request.Method;

        if (segments is ["-", "ping"]) { await RegistryHttp.JsonAsync(ctx, 200, new { }); return; }
        if (segments is ["-", "whoami"])
        {
            if (await GuardAsync(ctx, projectKey, RegistryOp.Read) is { } who) await RegistryHttp.JsonAsync(ctx, 200, new { username = who.Identity.Name });
            return;
        }
        if (segments is ["-", "v1", "search"]) { await SearchAsync(ctx, projectKey); return; }
        if (segments.Count >= 4 && segments[0] == "-" && segments[1] == "package")
        {
            var (tagName, after) = TakeName(segments, 2);
            if (tagName == null || after.Count == 0 || after[0] != "dist-tags") { await Fail(ctx, 404, "Not found."); return; }
            await DistTagsAsync(ctx, projectKey, tagName, after.Count > 1 ? after[1] : null, method);
            return;
        }

        var (name, tail) = TakeName(segments, 0);
        if (name == null || !IsValidName(name)) { await Fail(ctx, 404, "Not found."); return; }
        switch (tail, method)
        {
            case ([], "GET"): await PackumentAsync(ctx, projectKey, name, null); return;
            case ([], "PUT"): await PublishAsync(ctx, projectKey, name); return;
            case (["-rev", _], "PUT"): await UpdateAsync(ctx, projectKey, name); return;
            case (["-rev", _], "DELETE"): await UnpublishAllAsync(ctx, projectKey, name); return;
            case (["-", var file], "GET"): await TarballAsync(ctx, projectKey, name, file); return;
            case (["-", _, "-rev", _], "DELETE"):
                // npm unpublish deletes the tarball after updating the packument, which already removed the version.
                if (await GuardAsync(ctx, projectKey, RegistryOp.Delete) is not null) await RegistryHttp.JsonAsync(ctx, 200, new { ok = true });
                return;
            case ([var version], "GET"): await PackumentAsync(ctx, projectKey, name, version); return;
        }
        await Fail(ctx, 405, "Not supported.");
    }

    /// <summary>The package name starting at <paramref name="start"/> (two segments when scoped) and what follows it.</summary>
    private static (string? Name, List<string> Tail) TakeName(List<string> segments, int start)
    {
        if (segments.Count <= start) return (null, []);
        if (segments[start].StartsWith('@'))
            return segments.Count > start + 1 ? ($"{segments[start]}/{segments[start + 1]}", segments[(start + 2)..]) : (null, []);
        return (segments[start], segments[(start + 1)..]);
    }

    private static string Unscoped(string name) => name.Contains('/') ? name[(name.IndexOf('/') + 1)..] : name;

    // ---------------------------------------------------------------- reads

    private static async Task<(Package Package, List<PackageVersion> Versions, Dictionary<string, string> Tags)?> LoadAsync(HttpContext ctx, int projectId, string name)
    {
        await using var db = await ctx.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(ctx.RequestAborted);
        var p = await db.Packages.AsNoTracking().Include(x => x.Versions).ThenInclude(v => v.Files)
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Type == PackageType.Npm && x.NormalizedName == name, ctx.RequestAborted);
        if (p == null) return null;
        var tags = await db.PackageDistTags.AsNoTracking().Where(t => t.PackageId == p.Id).ToDictionaryAsync(t => t.Tag, t => t.Version, ctx.RequestAborted);
        return (p, p.Versions.OrderBy(v => v.CreatedAt).ToList(), tags);
    }

    private static JsonObject VersionManifest(string registry, Package p, PackageVersion v)
    {
        var manifest = v.MetadataJson != null ? JsonNode.Parse(v.MetadataJson) as JsonObject ?? [] : [];
        manifest["name"] = p.Name;
        manifest["version"] = v.Version;
        manifest["_id"] = $"{p.Name}@{v.Version}";
        if (v.Files.FirstOrDefault() is { } f)
            manifest["dist"] = new JsonObject
            {
                ["tarball"] = $"{registry}{p.Name}/-/{f.FileName}",
                ["shasum"] = f.Sha1,
                ["integrity"] = "sha512-" + f.Sha512,
                ["unpackedSize"] = manifest["dist"]?["unpackedSize"]?.DeepClone(),
            };
        return manifest;
    }

    private static async Task PackumentAsync(HttpContext ctx, string projectKey, string name, string? version)
    {
        if (await GuardAsync(ctx, projectKey, RegistryOp.Read) is not { } req) return;
        if (await LoadAsync(ctx, req.Project.Id, name) is not { Versions.Count: > 0 } found) { await Fail(ctx, 404, "Not found."); return; }
        var (p, versions, tags) = found;
        var registry = Registry(ctx, req.Project);
        if (version != null)
        {
            var target = tags.TryGetValue(version, out var tagged) ? tagged : version;
            if (versions.FirstOrDefault(v => v.Version == target) is not { } one) { await Fail(ctx, 404, "Version not found."); return; }
            await RegistryHttp.JsonAsync(ctx, 200, VersionManifest(registry, p, one));
            return;
        }
        var all = new JsonObject();
        var time = new JsonObject { ["created"] = Api.Dto.Utc(p.CreatedAt).ToString("o"), ["modified"] = Api.Dto.Utc(p.UpdatedAt).ToString("o") };
        foreach (var v in versions)
        {
            all[v.Version] = VersionManifest(registry, p, v);
            time[v.Version] = Api.Dto.Utc(v.CreatedAt).ToString("o");
        }
        var latest = tags.TryGetValue("latest", out var l) ? versions.FirstOrDefault(v => v.Version == l) : null;
        var description = latest?.MetadataJson != null ? (JsonNode.Parse(latest.MetadataJson) as JsonObject)?["description"]?.DeepClone() : null;
        await RegistryHttp.JsonAsync(ctx, 200, new JsonObject
        {
            ["_id"] = p.Name,
            ["_rev"] = $"{versions.Count}-{p.UpdatedAt.Ticks:x}",
            ["name"] = p.Name,
            ["description"] = description,
            ["dist-tags"] = new JsonObject(tags.Select(t => KeyValuePair.Create(t.Key, (JsonNode?)t.Value))),
            ["versions"] = all,
            ["time"] = time,
        });
    }

    private static async Task TarballAsync(HttpContext ctx, string projectKey, string name, string file)
    {
        if (await GuardAsync(ctx, projectKey, RegistryOp.Read, allowCookie: true) is not { } req) return;
        var found = await LoadAsync(ctx, req.Project.Id, name);
        var f = found?.Versions.SelectMany(v => v.Files).FirstOrDefault(x => x.FileName == file);
        var storage = ctx.RequestServices.GetRequiredService<PackageStorage>();
        if (f == null || !storage.Exists(f.Sha256)) { await Fail(ctx, 404, "Tarball not found."); return; }
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.Headers.XContentTypeOptions = "nosniff";
        ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{f.FileName}\"";
        ctx.Response.ContentLength = f.Size;
        await ctx.Response.SendFileAsync(storage.PathFor(f.Sha256), ctx.RequestAborted);
    }

    private static async Task SearchAsync(HttpContext ctx, string projectKey)
    {
        if (await GuardAsync(ctx, projectKey, RegistryOp.Read) is not { } req) return;
        var text = ctx.Request.Query["text"].ToString().Trim().ToLowerInvariant();
        var size = int.TryParse(ctx.Request.Query["size"], out var s) ? Math.Clamp(s, 1, 250) : 20;
        await using var db = await ctx.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(ctx.RequestAborted);
        var rows = await db.Packages.AsNoTracking()
            .Where(p => p.ProjectId == req.Project.Id && p.Type == PackageType.Npm && (text == "" || p.NormalizedName.Contains(text)))
            .OrderBy(p => p.NormalizedName).Take(size)
            .Select(p => new { p.Name, p.UpdatedAt, Latest = p.Versions.OrderByDescending(v => v.CreatedAt).Select(v => v.Version).FirstOrDefault() })
            .ToListAsync(ctx.RequestAborted);
        await RegistryHttp.JsonAsync(ctx, 200, new
        {
            objects = rows.Where(r => r.Latest != null).Select(r => new { package = new { name = r.Name, version = r.Latest, date = Api.Dto.Utc(r.UpdatedAt) } }),
            total = rows.Count,
            time = DateTimeOffset.UtcNow,
        });
    }

    // ---------------------------------------------------------------- publish

    private static async Task<JsonObject?> ReadBodyAsync(HttpContext ctx, long max)
    {
        RegistryHttp.AllowLargeBody(ctx, max);
        try { return await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static async Task PublishAsync(HttpContext ctx, string projectKey, string name)
    {
        if (await GuardAsync(ctx, projectKey, RegistryOp.Write) is not { } req) return;
        var storage = ctx.RequestServices.GetRequiredService<PackageStorage>();
        // Base64 makes the body about 4/3 of the tarball.
        if (await ReadBodyAsync(ctx, storage.MaxBlobBytes / 3 * 4 + 1024 * 1024) is not { } body) { await Fail(ctx, 400, "The body isn't a JSON object."); return; }
        if (body["name"]?.GetValue<string>() is { } bodyName && bodyName != name) { await Fail(ctx, 400, "The package name doesn't match the URL."); return; }
        if (body["versions"] is not JsonObject { Count: 1 } versions || body["_attachments"] is not JsonObject { Count: 1 } attachments)
        {
            await Fail(ctx, 400, "Publish one version with its tarball at a time.");
            return;
        }
        var (version, manifestNode) = versions.First();
        if (!SemVer().IsMatch(version) || manifestNode is not JsonObject manifest) { await Fail(ctx, 400, $"\"{version}\" isn't a valid semver version."); return; }
        var data = attachments.First().Value?["data"]?.GetValue<string>();
        byte[] tarball;
        try { tarball = Convert.FromBase64String(data ?? ""); }
        catch (FormatException) { await Fail(ctx, 400, "The tarball isn't valid base64."); return; }
        if (tarball.Length == 0) { await Fail(ctx, 400, "The tarball is empty."); return; }

        var packages = ctx.RequestServices.GetRequiredService<PackageService>();
        var existing = await packages.FindPackageAsync(req.Project.Id, PackageType.Npm, name);
        var dbFactory = ctx.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>();
        if (existing != null)
        {
            await using var check = await dbFactory.CreateDbContextAsync(ctx.RequestAborted);
            if (await check.PackageVersions.AnyAsync(v => v.PackageId == existing.Id && v.NormalizedVersion == version, ctx.RequestAborted))
            {
                await Fail(ctx, 403, $"You cannot publish over the previously published version {version}.");
                return;
            }
        }

        StoredBlob blob;
        try { blob = await storage.SaveAsync(new MemoryStream(tarball, writable: false), null, ctx.RequestAborted); }
        catch (PackageUploadException ex) { await Fail(ctx, ex.Status, ex.Message); return; }
        var dist = manifest["dist"] as JsonObject;
        var shasum = dist?["shasum"]?.GetValue<string>();
        var integrity = dist?["integrity"]?.GetValue<string>();
        if ((shasum != null && !shasum.Equals(blob.Sha1, StringComparison.OrdinalIgnoreCase))
            || (integrity != null && integrity.StartsWith("sha512-", StringComparison.Ordinal) && integrity != "sha512-" + blob.Sha512Base64))
        {
            await storage.DeleteUnreferencedAsync([blob.Sha256]);
            await Fail(ctx, 400, "The tarball doesn't match its shasum/integrity.");
            return;
        }

        manifest.Remove("_attachments");
        var package = existing ?? await packages.GetOrCreatePackageAsync(req.Project.Id, PackageType.Npm, name);
        var row = new PackageVersion
        {
            PackageId = package.Id, Version = version, NormalizedVersion = version, Size = blob.Size, MetadataJson = manifest.ToJsonString(),
            PublishedById = req.Identity.ActorId, PipelineId = req.Identity.Job?.PipelineId,
            Files = [new PackageFile { FileName = $"{Unscoped(name)}-{version}.tgz", Sha256 = blob.Sha256, Sha1 = blob.Sha1, Sha512 = blob.Sha512Base64, Size = blob.Size }],
        };
        await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
        {
            db.PackageVersions.Add(row);
            try { await db.SaveChangesAsync(CancellationToken.None); }
            catch (DbUpdateException) { await Fail(ctx, 403, $"You cannot publish over the previously published version {version}."); return; }
            var tags = (body["dist-tags"] as JsonObject)?.Where(t => t.Value?.GetValue<string>() == version).Select(t => t.Key).ToList() ?? [];
            if (tags.Count == 0 && !await db.PackageDistTags.AnyAsync(t => t.PackageId == package.Id && t.Tag == "latest")) tags.Add("latest");
            foreach (var tag in tags.Where(t => TagPattern().IsMatch(t))) await SetTagAsync(db, package.Id, tag, version);
            await db.Packages.Where(p => p.Id == package.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.UpdatedAt, DateTime.UtcNow));
        }
        await packages.RecordPublishedAsync(req.Project, package, row, req.Identity);
        await RegistryHttp.JsonAsync(ctx, 201, new { ok = true, id = name });
    }

    private static async Task SetTagAsync(AppDbContext db, int packageId, string tag, string version)
    {
        var row = await db.PackageDistTags.FirstOrDefaultAsync(t => t.PackageId == packageId && t.Tag == tag);
        if (row == null) db.PackageDistTags.Add(new PackageDistTag { PackageId = packageId, Tag = tag, Version = version });
        else row.Version = version;
        await db.SaveChangesAsync();
    }

    /// <summary><c>npm unpublish pkg@x</c> (versions missing from the body are deleted) and <c>npm deprecate</c>.</summary>
    private static async Task UpdateAsync(HttpContext ctx, string projectKey, string name)
    {
        if (await GuardAsync(ctx, projectKey, RegistryOp.Write) is not { } req) return;
        if (await ReadBodyAsync(ctx, 16 * 1024 * 1024) is not { } body || body["versions"] is not JsonObject sent) { await Fail(ctx, 400, "Expected a packument."); return; }
        if (await LoadAsync(ctx, req.Project.Id, name) is not { } found) { await Fail(ctx, 404, "Not found."); return; }
        var removed = found.Versions.Where(v => !sent.ContainsKey(v.Version)).ToList();
        var packages = ctx.RequestServices.GetRequiredService<PackageService>();
        if (removed.Count > 0)
        {
            var auth = ctx.RequestServices.GetRequiredService<RegistryAuth>();
            var check = await auth.AuthorizeAsync(req.Identity, projectKey, RegistryOp.Delete);
            if (!check.Allowed) { await Fail(ctx, 403, check.Message); return; }
            foreach (var v in removed) await packages.DeleteVersionCoreAsync(found.Package.Id, v.Id, req.Identity.ActorId, req.Identity);
        }
        await using (var db = await ctx.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(CancellationToken.None))
        {
            foreach (var v in found.Versions.Except(removed))
            {
                var deprecated = sent[v.Version]?["deprecated"]?.GetValue<string>();
                var meta = v.MetadataJson != null ? JsonNode.Parse(v.MetadataJson) as JsonObject ?? [] : [];
                if (meta["deprecated"]?.GetValue<string>() == deprecated) continue;
                if (string.IsNullOrEmpty(deprecated)) meta.Remove("deprecated"); else meta["deprecated"] = deprecated;
                var json = meta.ToJsonString();
                await db.PackageVersions.Where(x => x.Id == v.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.MetadataJson, json));
            }
            // Dist-tags that pointed at removed versions go; "latest" moves to the newest remaining version.
            var remaining = found.Versions.Except(removed).ToList();
            if (removed.Count > 0 && remaining.Count > 0 && !await db.PackageDistTags.AnyAsync(t => t.PackageId == found.Package.Id && t.Tag == "latest"))
                await SetTagAsync(db, found.Package.Id, "latest", remaining[^1].Version);
            if (remaining.Count == 0) await packages.DeletePackageCoreAsync(req.Project.Id, found.Package.Id, req.Identity.ActorId, req.Identity);
        }
        await RegistryHttp.JsonAsync(ctx, 200, new { ok = true });
    }

    private static async Task UnpublishAllAsync(HttpContext ctx, string projectKey, string name)
    {
        if (await GuardAsync(ctx, projectKey, RegistryOp.Delete) is not { } req) return;
        var packages = ctx.RequestServices.GetRequiredService<PackageService>();
        var p = await packages.FindPackageAsync(req.Project.Id, PackageType.Npm, name);
        if (p == null || !await packages.DeletePackageCoreAsync(req.Project.Id, p.Id, req.Identity.ActorId, req.Identity)) { await Fail(ctx, 404, "Not found."); return; }
        await RegistryHttp.JsonAsync(ctx, 200, new { ok = true });
    }

    // ---------------------------------------------------------------- dist-tags

    private static async Task DistTagsAsync(HttpContext ctx, string projectKey, string name, string? tag, string method)
    {
        var op = method == "GET" ? RegistryOp.Read : RegistryOp.Write;
        if (await GuardAsync(ctx, projectKey, op) is not { } req) return;
        if (await LoadAsync(ctx, req.Project.Id, name) is not { } found) { await Fail(ctx, 404, "Not found."); return; }
        await using var db = await ctx.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(CancellationToken.None);
        switch (method, tag)
        {
            case ("GET", null):
                await RegistryHttp.JsonAsync(ctx, 200, found.Tags);
                return;
            case ("PUT", not null):
                if (!TagPattern().IsMatch(tag) || SemVer().IsMatch(tag)) { await Fail(ctx, 400, "Invalid tag name."); return; }
                string? version;
                try { version = (await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted))?.GetValue<string>(); }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException) { version = null; }
                if (version == null || found.Versions.All(v => v.Version != version)) { await Fail(ctx, 404, "That version doesn't exist."); return; }
                await SetTagAsync(db, found.Package.Id, tag, version);
                await RegistryHttp.JsonAsync(ctx, 201, new { ok = true });
                return;
            case ("DELETE", not null):
                if (tag == "latest") { await Fail(ctx, 400, "The latest tag can't be removed."); return; }
                await db.PackageDistTags.Where(t => t.PackageId == found.Package.Id && t.Tag == tag).ExecuteDeleteAsync();
                await RegistryHttp.JsonAsync(ctx, 200, new { ok = true });
                return;
        }
        await Fail(ctx, 405, "Not supported.");
    }
}
