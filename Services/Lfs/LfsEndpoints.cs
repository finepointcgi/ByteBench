using System.Text.Json;
using System.Text.Json.Serialization;
using ByteBench.Data;
using ByteBench.Services.Git;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Lfs;

/// <summary>
/// The Git LFS API at <c>/git/{KEY}/{slug}.git/info/lfs/…</c> (the URL git-lfs derives from the clone URL), with the
/// <c>basic</c> transfer adapter:
/// <list type="bullet">
/// <item><c>POST objects/batch</c>: for each object, a download action (if this repository has it) or an upload action
/// (unless it already has it) plus a verify action.</item>
/// <item><c>GET objects/{oid}</c> downloads, <c>PUT objects/{oid}</c> uploads (the SHA-256 and the size limit are checked
/// while streaming), <c>POST objects/verify</c> confirms an upload.</item>
/// </list>
/// Authentication is the same as git over HTTP: Basic with the password or a personal access token, or
/// <c>Authorization: Bearer gbp_…</c>. The batch actions carry a short-lived <c>Bearer gblfs.…</c> token for this user,
/// repository and operation (Data Protection, one hour), so git-lfs doesn't need the credentials again. Downloads need
/// ViewProject (and read_repository or write_repository on a token); uploads need PushCode (and write_repository), and are
/// refused for archived repositories and pull mirrors. A browser session (cookie) can download, for the file viewer's link.
/// Users who can't see the project get 404. The locks API isn't implemented (git-lfs then warns once and carries on).
/// </summary>
public static class LfsEndpoints
{
    public const string MediaType = "application/vnd.git-lfs+json";
    private const string ActionTokenPrefix = "gblfs.";
    // Stored identifier: keep "GitBench" (the product was renamed to ByteBench). Changing it would make existing encrypted data/tokens unreadable.
    private const string TokenPurpose = "GitBench.Lfs.Action.v1";
    private static readonly TimeSpan ActionLifetime = TimeSpan.FromHours(1);
    public const int MaxObjectsPerBatch = 1000;

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private enum Op { Download, Upload }

    public static IEndpointRouteBuilder MapGitLfs(this IEndpointRouteBuilder app)
    {
        var logger = app.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("ByteBench.Lfs");
        var group = app.MapGroup("/git/{projectKey}/{repo}/info/lfs").DisableAntiforgery();
        group.MapPost("/objects/batch", (HttpContext ctx, string projectKey, string repo) => BatchAsync(ctx, logger, projectKey, repo));
        group.MapPost("/objects/verify", (HttpContext ctx, string projectKey, string repo) => VerifyAsync(ctx, logger, projectKey, repo));
        group.MapGet("/objects/{oid}", (HttpContext ctx, string projectKey, string repo, string oid) => DownloadAsync(ctx, logger, projectKey, repo, oid));
        group.MapPut("/objects/{oid}", (HttpContext ctx, string projectKey, string repo, string oid) => UploadAsync(ctx, logger, projectKey, repo, oid));
        return app;
    }

    /// <summary>The LFS URL of a repository's object (what the batch API hands out and the file viewer links to).</summary>
    public static string ObjectPath(string projectKey, string slug, string oid) => $"/git/{projectKey}/{slug}.git/info/lfs/objects/{oid}";

    // ---------------------------------------------------------------- batch

    private sealed record BatchRequest(string? Operation, List<string>? Transfers, List<BatchObject>? Objects,
        [property: JsonPropertyName("hash_algo")] string? HashAlgo);

    private sealed record BatchObject(string? Oid, long Size);

    private static async Task BatchAsync(HttpContext ctx, ILogger logger, string projectKey, string repo)
    {
        Prepare(ctx);
        BatchRequest? request;
        try { request = await JsonSerializer.DeserializeAsync<BatchRequest>(ctx.Request.Body, Json, ctx.RequestAborted); }
        catch (JsonException) { await ErrorAsync(ctx, 422, "The request isn't valid JSON."); return; }
        var op = request?.Operation switch { "download" => Op.Download, "upload" => Op.Upload, _ => (Op?)null };
        if (request == null || op == null) { await ErrorAsync(ctx, 422, "operation must be \"download\" or \"upload\"."); return; }

        var access = await AuthorizeAsync(ctx, logger, projectKey, repo, op.Value, allowCookie: false);
        if (access == null) return;

        if (request.Transfers is { Count: > 0 } transfers && !transfers.Contains("basic"))
        {
            await ErrorAsync(ctx, 422, "Only the basic transfer adapter is supported.");
            return;
        }
        if (request.HashAlgo != null && request.HashAlgo != "sha256") { await ErrorAsync(ctx, 409, "Only sha256 is supported."); return; }
        var objects = request.Objects ?? [];
        if (objects.Count > MaxObjectsPerBatch) { await ErrorAsync(ctx, 413, $"At most {MaxObjectsPerBatch} objects per batch."); return; }

        var storage = ctx.RequestServices.GetRequiredService<LfsStorage>();
        var oids = objects.Where(o => LfsStorage.IsValidOid(o.Oid)).Select(o => o.Oid!).Distinct().ToList();
        Dictionary<string, long> have;
        await using (var db = await ctx.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(ctx.RequestAborted))
            have = await db.LfsObjects.Where(o => o.RepositoryId == access.Repo.Id && oids.Contains(o.Oid))
                .ToDictionaryAsync(o => o.Oid, o => o.Size, ctx.RequestAborted);

        var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.PathBase}";
        var header = new Dictionary<string, string> { ["Authorization"] = "Bearer " + IssueToken(ctx, access.User.Id, access.Repo.Id, op.Value) };
        var expiresIn = (int)ActionLifetime.TotalSeconds - 60;
        var results = new List<object>();
        foreach (var o in objects)
        {
            if (!LfsStorage.IsValidOid(o.Oid) || o.Size < 0)
            {
                results.Add(new { oid = o.Oid, size = o.Size, error = new { code = 422, message = "Invalid oid or size." } });
                continue;
            }
            var href = baseUrl + ObjectPath(access.Repo.Key, access.Repo.Slug, o.Oid!);
            var present = have.TryGetValue(o.Oid!, out var size) && size == o.Size && storage.Exists(o.Oid!, o.Size);
            if (op == Op.Download)
            {
                results.Add(present
                    ? (object)new { oid = o.Oid, size = o.Size, authenticated = true, actions = new { download = new { href, header, expires_in = expiresIn } } }
                    : new { oid = o.Oid, size = o.Size, error = new { code = 404, message = "Object does not exist." } });
            }
            else if (o.Size > storage.MaxObjectBytes)
            {
                results.Add(new { oid = o.Oid, size = o.Size, error = new { code = 422, message = $"The object is larger than the limit of {storage.MaxObjectBytes} bytes." } });
            }
            else if (present)
            {
                results.Add(new { oid = o.Oid, size = o.Size }); // already uploaded: nothing to do
            }
            else
            {
                var verify = baseUrl + $"/git/{access.Repo.Key}/{access.Repo.Slug}.git/info/lfs/objects/verify";
                results.Add(new
                {
                    oid = o.Oid, size = o.Size, authenticated = true,
                    actions = new { upload = new { href, header, expires_in = expiresIn }, verify = new { href = verify, header, expires_in = expiresIn } },
                });
            }
        }
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = MediaType;
        await JsonSerializer.SerializeAsync(ctx.Response.Body, new { transfer = "basic", objects = results, hash_algo = "sha256" }, Json, ctx.RequestAborted);
    }

    // ---------------------------------------------------------------- objects

    private static async Task DownloadAsync(HttpContext ctx, ILogger logger, string projectKey, string repo, string oid)
    {
        Prepare(ctx);
        var access = await AuthorizeAsync(ctx, logger, projectKey, repo, Op.Download, allowCookie: true);
        if (access == null) return;
        var storage = ctx.RequestServices.GetRequiredService<LfsStorage>();
        if (!LfsStorage.IsValidOid(oid) || !await HasObjectAsync(ctx, access.Repo.Id, oid) || !storage.Exists(oid))
        {
            await ErrorAsync(ctx, 404, "Object does not exist.");
            return;
        }
        var path = storage.PathFor(oid);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.Headers.XContentTypeOptions = "nosniff";
        var name = ctx.Request.Query["download"].ToString();
        if (!string.IsNullOrWhiteSpace(name))
        {
            var safe = Attachments.AttachmentStorage.SanitizeFileName(Path.GetFileName(name));
            ctx.Response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition { FileName = safe, DispositionType = "attachment" }.ToString();
        }
        ctx.Response.ContentLength = new FileInfo(path).Length;
        await ctx.Response.SendFileAsync(path, ctx.RequestAborted);
    }

    private static async Task UploadAsync(HttpContext ctx, ILogger logger, string projectKey, string repo, string oid)
    {
        Prepare(ctx);
        var access = await AuthorizeAsync(ctx, logger, projectKey, repo, Op.Upload, allowCookie: false);
        if (access == null) return;
        if (!LfsStorage.IsValidOid(oid)) { await ErrorAsync(ctx, 422, "Invalid oid."); return; }
        var storage = ctx.RequestServices.GetRequiredService<LfsStorage>();
        if (ctx.Request.ContentLength > storage.MaxObjectBytes)
        {
            await ErrorAsync(ctx, 413, $"The object is larger than the limit of {storage.MaxObjectBytes} bytes.");
            return;
        }
        var sizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = storage.MaxObjectBytes + 1;

        long size;
        try { size = await storage.SaveAsync(oid, ctx.Request.Body, ctx.RequestAborted); }
        catch (LfsUploadException ex)
        {
            logger.LogInformation("LFS upload of {Oid} to {Key}/{Slug} by {User} refused: {Reason}", oid, access.Repo.Key, access.Repo.Slug, access.User.UserName, ex.Message);
            await ErrorAsync(ctx, ex.Status, ex.Message);
            return;
        }
        if (ctx.Request.ContentLength is { } declared && declared != size) { await ErrorAsync(ctx, 422, "The upload is incomplete."); return; }

        await using (var db = await ctx.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(CancellationToken.None))
        {
            if (!await db.LfsObjects.AnyAsync(o => o.RepositoryId == access.Repo.Id && o.Oid == oid))
            {
                db.LfsObjects.Add(new LfsObject { RepositoryId = access.Repo.Id, Oid = oid, Size = size, UploadedById = access.User.Id });
                try { await db.SaveChangesAsync(CancellationToken.None); }
                catch (DbUpdateException) { /* a concurrent upload of the same object won: fine */ }
            }
        }
        logger.LogInformation("LFS object {Oid} ({Size} bytes) uploaded to {Key}/{Slug} by {User}", oid, size, access.Repo.Key, access.Repo.Slug, access.User.UserName);
        ctx.Response.StatusCode = 200;
    }

    private static async Task VerifyAsync(HttpContext ctx, ILogger logger, string projectKey, string repo)
    {
        Prepare(ctx);
        var access = await AuthorizeAsync(ctx, logger, projectKey, repo, Op.Upload, allowCookie: false);
        if (access == null) return;
        BatchObject? o;
        try { o = await JsonSerializer.DeserializeAsync<BatchObject>(ctx.Request.Body, Json, ctx.RequestAborted); }
        catch (JsonException) { await ErrorAsync(ctx, 422, "The request isn't valid JSON."); return; }
        if (o == null || !LfsStorage.IsValidOid(o.Oid)) { await ErrorAsync(ctx, 422, "Invalid oid."); return; }
        await using var db = await ctx.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(ctx.RequestAborted);
        var ok = await db.LfsObjects.AnyAsync(x => x.RepositoryId == access.Repo.Id && x.Oid == o.Oid && x.Size == o.Size, ctx.RequestAborted)
                 && ctx.RequestServices.GetRequiredService<LfsStorage>().Exists(o.Oid!, o.Size);
        if (!ok) { await ErrorAsync(ctx, 404, "Object does not exist or has a different size."); return; }
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = MediaType;
        await ctx.Response.WriteAsync("{}");
    }

    private static async Task<bool> HasObjectAsync(HttpContext ctx, int repositoryId, string oid)
    {
        await using var db = await ctx.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(ctx.RequestAborted);
        return await db.LfsObjects.AnyAsync(o => o.RepositoryId == repositoryId && o.Oid == oid, ctx.RequestAborted);
    }

    // ---------------------------------------------------------------- auth

    private sealed record Access(User User, GitRepoLookup.RepoInfo Repo);

    /// <summary>
    /// Authenticates the request and checks the user may <paramref name="op"/> in the repository. On failure the response
    /// (401 with <c>LFS-Authenticate</c>, 403 or 404) has been written and null is returned.
    /// </summary>
    private static async Task<Access?> AuthorizeAsync(HttpContext ctx, ILogger logger, string projectKey, string repo, Op op, bool allowCookie)
    {
        if (!repo.EndsWith(".git", StringComparison.Ordinal) || !GitRepoLookup.IsValid(projectKey, repo[..^4]))
        {
            await ErrorAsync(ctx, 404, "Repository not found.");
            return null;
        }
        var slug = repo[..^4];

        User? user;
        TokenScopes? scopes = null;
        string? error = null;
        (int RepositoryId, Op Op)? actionToken = null;
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer " + ActionTokenPrefix, StringComparison.Ordinal))
        {
            var t = ReadToken(ctx, header["Bearer ".Length..].Trim());
            user = t == null ? null : await ActiveUserAsync(ctx, t.Value.UserId);
            if (t != null) actionToken = (t.Value.RepositoryId, t.Value.Op);
        }
        else if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = await ctx.RequestServices.GetRequiredService<AccessTokenService>().FindActiveAsync(header[7..].Trim());
            user = token == null ? null : await ActiveUserAsync(ctx, token.UserId);
            scopes = token?.Scopes;
        }
        else
        {
            (user, scopes, error) = await GitHttpEndpoints.AuthenticateAsync(ctx, logger, allowCookie);
        }
        if (user == null)
        {
            ctx.Response.Headers["LFS-Authenticate"] = "Basic realm=\"ByteBench\"";
            ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"ByteBench\", charset=\"UTF-8\"";
            await ErrorAsync(ctx, 401, error ?? "Authentication required.");
            return null;
        }

        var dbFactory = ctx.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var found = await GitRepoLookup.FindAsync(dbFactory, projectKey, slug, ctx.RequestAborted);
        var role = found == null ? null : await ctx.RequestServices.GetRequiredService<ProjectAccess>().GetRoleAsync(found.ProjectId, user.Id);
        if (found == null || role == null || ByteBench.Services.Workspaces.WorkspaceGitAuth.WrongRepository(ctx, found.Id)) { await ErrorAsync(ctx, 404, "Repository not found."); return null; }
        if (actionToken is { } at && (at.RepositoryId != found.Id || (at.Op != op && !(at.Op == Op.Upload && op == Op.Download))))
        {
            await ErrorAsync(ctx, 403, "This LFS token is for another repository or operation.");
            return null;
        }
        if (scopes is { } s)
        {
            var needed = op == Op.Upload ? TokenScopes.WriteRepository : TokenScopes.ReadRepository | TokenScopes.WriteRepository;
            if ((s & needed) == 0)
            {
                await ErrorAsync(ctx, 403, op == Op.Upload ? "This access token doesn't have the write_repository scope." : "This access token doesn't have the read_repository scope.");
                return null;
            }
        }
        if (op == Op.Upload)
        {
            if (!ProjectAccess.RoleHas(role.Value, Permission.PushCode))
            {
                await ErrorAsync(ctx, 403, $"You don't have permission to push to this repository (requires Developer; you are {role}).");
                return null;
            }
            if (found.IsArchived) { await ErrorAsync(ctx, 403, GitRepoLookup.ArchivedMessage); return null; }
            await using var db = await dbFactory.CreateDbContextAsync(ctx.RequestAborted);
            if (await Mirroring.MirrorService.IsPullMirrorAsync(db, found.Id))
            {
                await ErrorAsync(ctx, 403, "This repository is a pull mirror and is read-only.");
                return null;
            }
        }
        return new Access(user, found);
    }

    private static async Task<User?> ActiveUserAsync(HttpContext ctx, int userId)
    {
        var users = ctx.RequestServices.GetRequiredService<UserManager<User>>();
        var user = await users.FindByIdAsync(userId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (user == null || user.DeactivatedAt != null || await users.IsLockedOutAsync(user)) return null;
        return user;
    }

    private static string IssueToken(HttpContext ctx, int userId, int repositoryId, Op op)
    {
        var protector = ctx.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(TokenPurpose).ToTimeLimitedDataProtector();
        return ActionTokenPrefix + protector.Protect($"{userId}:{repositoryId}:{(op == Op.Upload ? "u" : "d")}", ActionLifetime);
    }

    private static (int UserId, int RepositoryId, Op Op)? ReadToken(HttpContext ctx, string token)
    {
        if (!token.StartsWith(ActionTokenPrefix, StringComparison.Ordinal)) return null;
        try
        {
            var protector = ctx.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(TokenPurpose).ToTimeLimitedDataProtector();
            var parts = protector.Unprotect(token[ActionTokenPrefix.Length..]).Split(':');
            if (parts.Length == 3 && int.TryParse(parts[0], out var u) && int.TryParse(parts[1], out var r) && parts[2] is "u" or "d")
                return (u, r, parts[2] == "u" ? Op.Upload : Op.Download);
        }
        catch (System.Security.Cryptography.CryptographicException) { /* expired or forged */ }
        return null;
    }

    // ---------------------------------------------------------------- responses

    private static void Prepare(HttpContext ctx)
    {
        if (ctx.Features.Get<IStatusCodePagesFeature>() is { } pages) pages.Enabled = false;
        ctx.Response.Headers.CacheControl = "no-store";
    }

    private static async Task ErrorAsync(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = MediaType;
        await JsonSerializer.SerializeAsync(ctx.Response.Body, new { message }, Json);
    }
}
