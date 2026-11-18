using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Packages;

/// <summary>
/// The container registry: the subset of the OCI Distribution API (Docker Registry HTTP API v2) that <c>docker push</c>
/// and <c>docker pull</c> use, at <c>/v2/</c>. Images are named <c>{host}/{project key in lowercase}/{name}</c>
/// (the name may have more path components).
/// <list type="bullet">
/// <item><c>GET /v2/</c> checks credentials; <c>GET /v2/token</c> trades Basic credentials (any user name + personal
/// access token, or a CI job token) for a bearer token. Every endpoint also accepts Basic or Bearer directly.</item>
/// <item>Blobs: <c>HEAD/GET/DELETE …/blobs/{digest}</c>; uploads <c>POST …/blobs/uploads/</c> (monolithic with
/// <c>?digest=</c>, cross-repository <c>?mount=&amp;from=</c>, or a session), <c>PATCH</c> chunks (with or without
/// <c>Content-Range</c>), <c>PUT ?digest=</c> to finish, <c>GET</c> status, <c>DELETE</c> cancel.</item>
/// <item>Manifests: <c>HEAD/GET/PUT/DELETE …/manifests/{tag|digest}</c> (Docker v2 schema 2, OCI image manifest and
/// index, manifest list). A manifest is only accepted when every blob and child manifest it references was pushed.</item>
/// <item><c>GET …/tags/list</c> with <c>n</c>/<c>last</c> paging.</item>
/// </list>
/// Digests are verified on every upload. Pulling needs Viewer, pushing Developer, deleting Maintainer
/// (<see cref="RegistryAuth"/>).
/// </summary>
public static partial class OciEndpoints
{
    public const string ApiVersionHeader = "Docker-Distribution-API-Version";
    public const long MaxManifestBytes = 4 * 1024 * 1024;

    [GeneratedRegex(@"^(?<name>[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)*)/(?:(?<kind>blobs/uploads)/?(?<ref>[^/]*)|(?<kind>blobs)/(?<ref>[^/]+)|(?<kind>manifests)/(?<ref>[^/]+)|(?<kind>tags/list))$")]
    private static partial Regex RoutePattern();

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex TagPattern();

    public static IEndpointRouteBuilder MapContainerRegistry(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/v2/{**rest}", ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE"], DispatchAsync).DisableAntiforgery();
        return app;
    }

    private sealed record Target(string ProjectKey, string Image, string FullName);

    private static string BaseUrl(HttpContext ctx) => ctx.RequestServices.GetRequiredService<Api.PublicBaseUrl>().For(ctx.Request);

    private static string ChallengeFor(HttpContext ctx) => $"Bearer realm=\"{BaseUrl(ctx)}/v2/token\",service=\"gitbench\"";

    private static async Task Error(HttpContext ctx, int status, string code, string message)
    {
        ctx.Response.StatusCode = status;
        if (HttpMethods.IsHead(ctx.Request.Method)) return;
        await RegistryHttp.JsonAsync(ctx, status, new { errors = new[] { new { code, message } } });
    }

    private static Task<RegistryRequest?> GuardAsync(HttpContext ctx, Target t, RegistryOp op) =>
        RegistryHttp.GuardAsync(ctx, t.ProjectKey, op, ChallengeFor(ctx), (status, message) => Error(ctx, status, status switch
        {
            401 => "UNAUTHORIZED",
            403 => "DENIED",
            _ => "NAME_UNKNOWN",
        }, message));

    private static async Task DispatchAsync(HttpContext ctx, string? rest)
    {
        RegistryHttp.Prepare(ctx);
        ctx.Response.Headers[ApiVersionHeader] = "registry/2.0";
        rest = (rest ?? "").TrimStart('/');
        var method = ctx.Request.Method;
        if (rest.Length == 0) { await BaseAsync(ctx); return; }
        if (rest == "token" && HttpMethods.IsGet(method)) { await TokenAsync(ctx); return; }

        var m = RoutePattern().Match(rest);
        if (!m.Success) { await Error(ctx, 404, "NAME_INVALID", "Not a registry path. Images are named {project key}/{image}."); return; }
        var full = m.Groups["name"].Value;
        var slash = full.IndexOf('/');
        if (slash < 0) { await Error(ctx, 404, "NAME_INVALID", "Image names start with the project key: {key}/{image}."); return; }
        if (full.Length > 255) { await Error(ctx, 400, "NAME_INVALID", "The image name is too long."); return; }
        var target = new Target(full[..slash], full[(slash + 1)..], full);
        var reference = m.Groups["ref"].Value;

        switch (m.Groups["kind"].Value, method)
        {
            case ("tags/list", "GET"): await TagsAsync(ctx, target); return;
            case ("manifests", "GET" or "HEAD"): await GetManifestAsync(ctx, target, reference); return;
            case ("manifests", "PUT"): await PutManifestAsync(ctx, target, reference); return;
            case ("manifests", "DELETE"): await DeleteManifestAsync(ctx, target, reference); return;
            case ("blobs", "GET" or "HEAD"): await GetBlobAsync(ctx, target, reference); return;
            case ("blobs", "DELETE"): await DeleteBlobAsync(ctx, target, reference); return;
            case ("blobs/uploads", "POST") when reference.Length == 0: await StartUploadAsync(ctx, target); return;
            case ("blobs/uploads", "PATCH") when reference.Length > 0: await PatchUploadAsync(ctx, target, reference); return;
            case ("blobs/uploads", "PUT") when reference.Length > 0: await FinishUploadAsync(ctx, target, reference); return;
            case ("blobs/uploads", "GET") when reference.Length > 0: await UploadStatusAsync(ctx, target, reference); return;
            case ("blobs/uploads", "DELETE") when reference.Length > 0: await CancelUploadAsync(ctx, target, reference); return;
        }
        await Error(ctx, 405, "UNSUPPORTED", "This operation isn't supported.");
    }

    // ---------------------------------------------------------------- auth

    private static async Task BaseAsync(HttpContext ctx)
    {
        var (identity, error) = await ctx.RequestServices.GetRequiredService<RegistryAuth>().AuthenticateAsync(ctx);
        if (identity == null)
        {
            ctx.Response.Headers.WWWAuthenticate = ChallengeFor(ctx);
            await Error(ctx, 401, "UNAUTHORIZED", error ?? "Authentication required.");
            return;
        }
        await RegistryHttp.JsonAsync(ctx, 200, new { });
    }

    /// <summary>The token endpoint of the bearer challenge: Basic credentials in, a one-hour registry token out.</summary>
    private static async Task TokenAsync(HttpContext ctx)
    {
        var auth = ctx.RequestServices.GetRequiredService<RegistryAuth>();
        var (identity, error) = await auth.AuthenticateAsync(ctx);
        if (identity == null)
        {
            ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"GitBench registry\"";
            await Error(ctx, 401, "UNAUTHORIZED", error ?? "Sign in with your user name and a personal access token.");
            return;
        }
        string? jobToken = null;
        if (identity.Job != null)
        {
            var header = ctx.Request.Headers.Authorization.ToString();
            jobToken = header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)
                ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim())).Split(':', 2)[1]
                : header[7..].Trim();
        }
        var token = auth.IssueBearer(identity, jobToken);
        await RegistryHttp.JsonAsync(ctx, 200, new
        {
            token, access_token = token, expires_in = (int)RegistryAuth.BearerLifetime.TotalSeconds, issued_at = DateTimeOffset.UtcNow,
        });
    }

    // ---------------------------------------------------------------- helpers

    private static Task<Package?> FindAsync(HttpContext ctx, RegistryRequest req, Target t) =>
        ctx.RequestServices.GetRequiredService<PackageService>().FindPackageAsync(req.Project.Id, PackageType.Container, t.Image);

    private static async Task<ContainerBlob?> FindBlobAsync(HttpContext ctx, int packageId, string sha, bool? manifest = null)
    {
        await using var db = await ctx.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(ctx.RequestAborted);
        return await db.ContainerBlobs.AsNoTracking()
            .FirstOrDefaultAsync(b => b.PackageId == packageId && b.Sha256 == sha && (manifest == null || b.IsManifest == manifest), ctx.RequestAborted);
    }

    private static string BlobLocation(Target t, string sha) => $"/v2/{t.FullName}/blobs/sha256:{sha}";
    private static string UploadLocation(Target t, string id) => $"/v2/{t.FullName}/blobs/uploads/{id}";

    private static void UploadHeaders(HttpContext ctx, Target t, string id, long size)
    {
        ctx.Response.Headers.Location = UploadLocation(t, id);
        ctx.Response.Headers["Range"] = $"0-{Math.Max(0, size - 1)}";
        ctx.Response.Headers["Docker-Upload-UUID"] = id;
        ctx.Response.ContentLength = 0;
    }

    private static async Task BlobCreatedAsync(HttpContext ctx, Target t, int packageId, StoredBlob blob)
    {
        await ctx.RequestServices.GetRequiredService<PackageService>().LinkBlobAsync(packageId, blob, false, null);
        ctx.Response.StatusCode = 201;
        ctx.Response.Headers.Location = BlobLocation(t, blob.Sha256);
        ctx.Response.Headers["Docker-Content-Digest"] = "sha256:" + blob.Sha256;
        ctx.Response.ContentLength = 0;
    }

    // ---------------------------------------------------------------- blobs

    private static async Task GetBlobAsync(HttpContext ctx, Target t, string digest)
    {
        if (await GuardAsync(ctx, t, RegistryOp.Read) is not { } req) return;
        var sha = PackageStorage.HexOfDigest(digest);
        var storage = ctx.RequestServices.GetRequiredService<PackageStorage>();
        var package = sha == null ? null : await FindAsync(ctx, req, t);
        var blob = package == null ? null : await FindBlobAsync(ctx, package.Id, sha!);
        if (blob == null || !storage.Exists(blob.Sha256)) { await Error(ctx, 404, "BLOB_UNKNOWN", "Blob unknown to this repository."); return; }
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.Headers["Docker-Content-Digest"] = "sha256:" + blob.Sha256;
        ctx.Response.Headers.ETag = $"\"sha256:{blob.Sha256}\"";
        ctx.Response.ContentLength = blob.Size;
        if (HttpMethods.IsHead(ctx.Request.Method)) return;
        await ctx.Response.SendFileAsync(storage.PathFor(blob.Sha256), ctx.RequestAborted);
    }

    private static async Task DeleteBlobAsync(HttpContext ctx, Target t, string digest)
    {
        if (await GuardAsync(ctx, t, RegistryOp.Delete) is not { } req) return;
        var sha = PackageStorage.HexOfDigest(digest);
        var package = sha == null ? null : await FindAsync(ctx, req, t);
        if (package == null || sha == null) { await Error(ctx, 404, "BLOB_UNKNOWN", "Blob unknown to this repository."); return; }
        await using (var db = await ctx.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(CancellationToken.None))
            if (await db.ContainerBlobs.Where(b => b.PackageId == package.Id && b.Sha256 == sha && !b.IsManifest).ExecuteDeleteAsync() == 0)
            {
                await Error(ctx, 404, "BLOB_UNKNOWN", "Blob unknown to this repository.");
                return;
            }
        await ctx.RequestServices.GetRequiredService<PackageStorage>().DeleteUnreferencedAsync([sha]);
        ctx.Response.StatusCode = 202;
        ctx.Response.ContentLength = 0;
    }

    private static async Task StartUploadAsync(HttpContext ctx, Target t)
    {
        if (await GuardAsync(ctx, t, RegistryOp.Write) is not { } req) return;
        var storage = ctx.RequestServices.GetRequiredService<PackageStorage>();
        var packages = ctx.RequestServices.GetRequiredService<PackageService>();
        var query = ctx.Request.Query;

        // Cross-repository mount: link a blob the user can already read elsewhere.
        if (PackageStorage.HexOfDigest(query["mount"]) is { } mountSha && query["from"].ToString() is { Length: > 0 } from
            && from.IndexOf('/') is > 0 and var cut && storage.Exists(mountSha))
        {
            var auth = ctx.RequestServices.GetRequiredService<RegistryAuth>();
            var source = await auth.AuthorizeAsync(req.Identity, from[..cut], RegistryOp.Read);
            var sourcePackage = source.Allowed ? await packages.FindPackageAsync(source.Project!.Id, PackageType.Container, from[(cut + 1)..]) : null;
            if (sourcePackage != null && await FindBlobAsync(ctx, sourcePackage.Id, mountSha, manifest: false) is { } mounted)
            {
                var package = await packages.GetOrCreatePackageAsync(req.Project.Id, PackageType.Container, t.Image);
                await BlobCreatedAsync(ctx, t, package.Id, new StoredBlob(mountSha, mounted.Size, "", ""));
                return;
            }
        }

        // Monolithic upload: the whole blob in this request.
        if (query["digest"].ToString() is { Length: > 0 } digest)
        {
            if (PackageStorage.HexOfDigest(digest) is not { } expected) { await Error(ctx, 400, "DIGEST_INVALID", "Only sha256 digests are supported."); return; }
            RegistryHttp.AllowLargeBody(ctx, storage.MaxBlobBytes);
            StoredBlob blob;
            try { blob = await storage.SaveAsync(ctx.Request.Body, expected, ctx.RequestAborted); }
            catch (PackageUploadException ex) { await Error(ctx, ex.Status, ex.Code, ex.Message); return; }
            var package = await packages.GetOrCreatePackageAsync(req.Project.Id, PackageType.Container, t.Image);
            await BlobCreatedAsync(ctx, t, package.Id, blob);
            return;
        }

        var session = storage.StartUpload(req.Project.Id, t.Image, req.Identity.Name);
        ctx.Response.StatusCode = 202;
        UploadHeaders(ctx, t, session.Id, 0);
    }

    private static async Task<UploadSession?> SessionAsync(HttpContext ctx, RegistryRequest req, Target t, string id)
    {
        var session = ctx.RequestServices.GetRequiredService<PackageStorage>().GetUpload(id);
        if (session == null || session.ProjectId != req.Project.Id || session.Repository != t.Image)
        {
            await Error(ctx, 404, "BLOB_UPLOAD_UNKNOWN", "Upload unknown.");
            return null;
        }
        return session;
    }

    /// <summary>The start of a <c>Content-Range: start-end</c> (or <c>bytes start-end/total</c>) header, if any.</summary>
    private static long? RangeStart(HttpContext ctx)
    {
        var range = ctx.Request.Headers.ContentRange.ToString();
        if (range.Length == 0) return null;
        if (range.StartsWith("bytes ", StringComparison.OrdinalIgnoreCase)) range = range[6..];
        var dash = range.IndexOf('-');
        return dash > 0 && long.TryParse(range[..dash], out var start) ? start : -1;
    }

    private static async Task PatchUploadAsync(HttpContext ctx, Target t, string id)
    {
        if (await GuardAsync(ctx, t, RegistryOp.Write) is not { } req) return;
        if (await SessionAsync(ctx, req, t, id) is null) return;
        var storage = ctx.RequestServices.GetRequiredService<PackageStorage>();
        RegistryHttp.AllowLargeBody(ctx, storage.MaxBlobBytes);
        long size;
        try { size = await storage.AppendUploadAsync(id, ctx.Request.Body, RangeStart(ctx), ctx.RequestAborted); }
        catch (PackageUploadException ex)
        {
            if (ex.Status == 416) UploadHeaders(ctx, t, id, storage.UploadedBytes(id));
            await Error(ctx, ex.Status, ex.Code, ex.Message);
            return;
        }
        ctx.Response.StatusCode = 202;
        UploadHeaders(ctx, t, id, size);
    }

    private static async Task FinishUploadAsync(HttpContext ctx, Target t, string id)
    {
        if (await GuardAsync(ctx, t, RegistryOp.Write) is not { } req) return;
        if (await SessionAsync(ctx, req, t, id) is null) return;
        if (PackageStorage.HexOfDigest(ctx.Request.Query["digest"]) is not { } expected) { await Error(ctx, 400, "DIGEST_INVALID", "A sha256 digest is required."); return; }
        var storage = ctx.RequestServices.GetRequiredService<PackageStorage>();
        RegistryHttp.AllowLargeBody(ctx, storage.MaxBlobBytes);
        StoredBlob blob;
        try
        {
            await storage.AppendUploadAsync(id, ctx.Request.Body, RangeStart(ctx) is >= 0 and var s ? s : null, ctx.RequestAborted);
            blob = await storage.CompleteUploadAsync(id, expected, ctx.RequestAborted);
        }
        catch (PackageUploadException ex) { await Error(ctx, ex.Status, ex.Code, ex.Message); return; }
        var package = await ctx.RequestServices.GetRequiredService<PackageService>().GetOrCreatePackageAsync(req.Project.Id, PackageType.Container, t.Image);
        await BlobCreatedAsync(ctx, t, package.Id, blob);
    }

    private static async Task UploadStatusAsync(HttpContext ctx, Target t, string id)
    {
        if (await GuardAsync(ctx, t, RegistryOp.Write) is not { } req) return;
        if (await SessionAsync(ctx, req, t, id) is null) return;
        ctx.Response.StatusCode = 204;
        UploadHeaders(ctx, t, id, ctx.RequestServices.GetRequiredService<PackageStorage>().UploadedBytes(id));
    }

    private static async Task CancelUploadAsync(HttpContext ctx, Target t, string id)
    {
        if (await GuardAsync(ctx, t, RegistryOp.Write) is not { } req) return;
        if (await SessionAsync(ctx, req, t, id) is null) return;
        ctx.RequestServices.GetRequiredService<PackageStorage>().CancelUpload(id);
        ctx.Response.StatusCode = 204;
    }

    // ---------------------------------------------------------------- manifests

    /// <summary>The manifest a tag or digest refers to in the repository: (sha, media type, size), or null.</summary>
    private static async Task<ContainerBlob?> ResolveAsync(HttpContext ctx, int packageId, string reference)
    {
        var sha = PackageStorage.HexOfDigest(reference);
        if (sha == null)
        {
            await using var db = await ctx.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(ctx.RequestAborted);
            var digest = await db.PackageVersions.Where(v => v.PackageId == packageId && v.NormalizedVersion == reference)
                .Select(v => v.ManifestDigest).FirstOrDefaultAsync(ctx.RequestAborted);
            sha = PackageStorage.HexOfDigest(digest);
        }
        return sha == null ? null : await FindBlobAsync(ctx, packageId, sha, manifest: true);
    }

    private static async Task GetManifestAsync(HttpContext ctx, Target t, string reference)
    {
        if (await GuardAsync(ctx, t, RegistryOp.Read) is not { } req) return;
        var storage = ctx.RequestServices.GetRequiredService<PackageStorage>();
        var package = await FindAsync(ctx, req, t);
        var manifest = package == null ? null : await ResolveAsync(ctx, package.Id, reference);
        if (manifest == null || !storage.Exists(manifest.Sha256)) { await Error(ctx, 404, "MANIFEST_UNKNOWN", "Manifest unknown."); return; }
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = manifest.MediaType ?? OciManifest.OciManifestV1;
        ctx.Response.Headers["Docker-Content-Digest"] = "sha256:" + manifest.Sha256;
        ctx.Response.Headers.ETag = $"\"sha256:{manifest.Sha256}\"";
        ctx.Response.ContentLength = manifest.Size;
        if (HttpMethods.IsHead(ctx.Request.Method)) return;
        await ctx.Response.SendFileAsync(storage.PathFor(manifest.Sha256), ctx.RequestAborted);
    }

    private static async Task PutManifestAsync(HttpContext ctx, Target t, string reference)
    {
        if (await GuardAsync(ctx, t, RegistryOp.Write) is not { } req) return;
        var isDigest = reference.StartsWith("sha256:", StringComparison.Ordinal);
        if (isDigest ? PackageStorage.HexOfDigest(reference) == null : !TagPattern().IsMatch(reference))
        {
            await Error(ctx, 400, isDigest ? "DIGEST_INVALID" : "TAG_INVALID", "Invalid tag or digest.");
            return;
        }
        if (ctx.Request.ContentLength > MaxManifestBytes) { await Error(ctx, 413, "SIZE_INVALID", "The manifest is too large."); return; }
        byte[] body;
        using (var buffer = new MemoryStream())
        {
            var chunk = new byte[81920];
            int n;
            while ((n = await ctx.Request.Body.ReadAsync(chunk, ctx.RequestAborted)) > 0)
            {
                buffer.Write(chunk, 0, n);
                if (buffer.Length > MaxManifestBytes) { await Error(ctx, 413, "SIZE_INVALID", "The manifest is too large."); return; }
            }
            body = buffer.ToArray();
        }

        string mediaType;
        List<(string Sha256, long Size, bool IsManifest)> descriptors;
        try
        {
            var json = JsonNode.Parse(body) as JsonObject ?? throw new JsonException("Not a JSON object.");
            mediaType = json["mediaType"]?.GetValue<string>() ?? (ctx.Request.ContentType ?? "").Split(';')[0].Trim();
            if (mediaType.Length == 0) mediaType = json["manifests"] != null ? OciManifest.OciIndex : OciManifest.OciManifestV1;
            if (!OciManifest.Supported.Contains(mediaType)) { await Error(ctx, 415, "MANIFEST_INVALID", $"Unsupported manifest type {mediaType}."); return; }
            descriptors = OciManifest.Descriptors(body);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            await Error(ctx, 400, "MANIFEST_INVALID", "The manifest isn't valid: " + ex.Message);
            return;
        }
        var sha = Convert.ToHexStringLower(SHA256.HashData(body));
        if (isDigest && reference != "sha256:" + sha) { await Error(ctx, 400, "DIGEST_INVALID", $"The manifest's digest is sha256:{sha}."); return; }

        var packages = ctx.RequestServices.GetRequiredService<PackageService>();
        var package = await packages.GetOrCreatePackageAsync(req.Project.Id, PackageType.Container, t.Image);
        foreach (var d in descriptors)
            if (await FindBlobAsync(ctx, package.Id, d.Sha256, d.IsManifest ? true : null) is null)
            {
                await Error(ctx, 400, d.IsManifest ? "MANIFEST_UNKNOWN" : "MANIFEST_BLOB_UNKNOWN", $"sha256:{d.Sha256} hasn't been pushed to this repository.");
                return;
            }

        var storage = ctx.RequestServices.GetRequiredService<PackageStorage>();
        var stored = await storage.SaveAsync(body, CancellationToken.None);
        await packages.LinkBlobAsync(package.Id, stored, true, mediaType);

        if (!isDigest)
        {
            var size = body.Length + descriptors.Sum(d => d.Size);
            var meta = new JsonObject { ["mediaType"] = mediaType, ["layers"] = descriptors.Count(d => !d.IsManifest) - 1, ["manifests"] = descriptors.Count(d => d.IsManifest) };
            if (OciManifest.IsIndex(mediaType)) meta["layers"] = 0;
            string? previous;
            PackageVersion version;
            await using (var db = await ctx.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(CancellationToken.None))
            {
                var row = await db.PackageVersions.FirstOrDefaultAsync(v => v.PackageId == package.Id && v.NormalizedVersion == reference);
                previous = row?.ManifestDigest;
                if (row == null) db.PackageVersions.Add(row = new PackageVersion { PackageId = package.Id, Version = reference, NormalizedVersion = reference });
                row.ManifestDigest = "sha256:" + sha;
                row.Size = size;
                row.MetadataJson = meta.ToJsonString();
                row.PublishedById = req.Identity.ActorId;
                row.PipelineId = req.Identity.Job?.PipelineId;
                row.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                await db.Packages.Where(p => p.Id == package.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.UpdatedAt, DateTime.UtcNow));
                version = row;
            }
            if (previous != "sha256:" + sha)
            {
                await packages.RecordPublishedAsync(req.Project, package, version, req.Identity);
                if (PackageStorage.HexOfDigest(previous) is { } old) await packages.ReleaseManifestIfUnusedAsync(package.Id, old);
            }
        }

        ctx.Response.StatusCode = 201;
        ctx.Response.Headers.Location = $"/v2/{t.FullName}/manifests/sha256:{sha}";
        ctx.Response.Headers["Docker-Content-Digest"] = "sha256:" + sha;
        ctx.Response.ContentLength = 0;
    }

    private static async Task DeleteManifestAsync(HttpContext ctx, Target t, string reference)
    {
        if (await GuardAsync(ctx, t, RegistryOp.Delete) is not { } req) return;
        var packages = ctx.RequestServices.GetRequiredService<PackageService>();
        var package = await FindAsync(ctx, req, t);
        if (package == null) { await Error(ctx, 404, "MANIFEST_UNKNOWN", "Manifest unknown."); return; }
        await using var db = await ctx.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(CancellationToken.None);
        if (PackageStorage.HexOfDigest(reference) is { } sha)
        {
            if (await FindBlobAsync(ctx, package.Id, sha, manifest: true) is null) { await Error(ctx, 404, "MANIFEST_UNKNOWN", "Manifest unknown."); return; }
            // Deleting by digest removes every tag that points at it, then the manifest itself.
            var tags = await db.PackageVersions.Where(v => v.PackageId == package.Id && v.ManifestDigest == reference).Select(v => v.Id).ToListAsync();
            foreach (var id in tags) await packages.DeleteVersionCoreAsync(package.Id, id, req.Identity.ActorId, req.Identity);
            if (await FindBlobAsync(ctx, package.Id, sha, manifest: true) is not null)
                await ctx.RequestServices.GetRequiredService<PackageStorage>().DeleteUnreferencedAsync(await packages.DeleteManifestCoreAsync(package.Id, sha));
        }
        else
        {
            var id = await db.PackageVersions.Where(v => v.PackageId == package.Id && v.NormalizedVersion == reference).Select(v => (int?)v.Id).FirstOrDefaultAsync();
            if (id == null || !await packages.DeleteVersionCoreAsync(package.Id, id.Value, req.Identity.ActorId, req.Identity))
            {
                await Error(ctx, 404, "MANIFEST_UNKNOWN", "Tag unknown.");
                return;
            }
        }
        ctx.Response.StatusCode = 202;
        ctx.Response.ContentLength = 0;
    }

    // ---------------------------------------------------------------- tags

    private static async Task TagsAsync(HttpContext ctx, Target t)
    {
        if (await GuardAsync(ctx, t, RegistryOp.Read) is not { } req) return;
        var package = await FindAsync(ctx, req, t);
        if (package == null) { await Error(ctx, 404, "NAME_UNKNOWN", "Repository unknown."); return; }
        List<string> tags;
        await using (var db = await ctx.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(ctx.RequestAborted))
            tags = await db.PackageVersions.Where(v => v.PackageId == package.Id).Select(v => v.Version).ToListAsync(ctx.RequestAborted);
        tags.Sort(StringComparer.Ordinal);
        var last = ctx.Request.Query["last"].ToString();
        if (last.Length > 0) tags = tags.Where(x => string.CompareOrdinal(x, last) > 0).ToList();
        if (int.TryParse(ctx.Request.Query["n"], out var n) && n >= 0 && tags.Count > n)
        {
            tags = tags.Take(n).ToList();
            if (tags.Count > 0) ctx.Response.Headers.Link = $"</v2/{t.FullName}/tags/list?n={n}&last={Uri.EscapeDataString(tags[^1])}>; rel=\"next\"";
        }
        await RegistryHttp.JsonAsync(ctx, 200, new { name = t.FullName, tags });
    }
}
