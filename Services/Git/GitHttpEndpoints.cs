using System.Text;
using System.Text.RegularExpressions;
using GitBench.Data;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Git;

/// <summary>
/// Git Smart HTTP at /git/{PROJECTKEY}/{slug}.git/..., served by <c>git http-backend</c>.
/// Only the smart protocol is exposed:
///   GET  info/refs?service=git-upload-pack|git-receive-pack
///   POST git-upload-pack
///   POST git-receive-pack
/// Everything else under /git is 404 (or 403 for dumb-protocol info/refs). Both reads and writes
/// require HTTP Basic credentials checked against Identity (username or email), honoring lockout.
/// </summary>
public static partial class GitHttpEndpoints
{
    private const string Realm = "GitBench";
    private const string UploadPack = "git-upload-pack";
    private const string ReceivePack = "git-receive-pack";

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{1,9}$")]
    private static partial Regex ProjectKeyPattern();

    // Mirrors Repository.Slug validation; ".." is excluded separately.
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,99}$")]
    private static partial Regex SlugPattern();

    public static IEndpointRouteBuilder MapGitHttp(this IEndpointRouteBuilder app)
    {
        var services = app.ServiceProvider;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("GitBench.GitHttp");
        var gitPath = GitHttpBackend.LocateGit(services.GetRequiredService<IConfiguration>());
        if (gitPath is null)
            logger.LogError("git executable not found (searched PATH and the Git:Executable setting). " +
                            "Git clone/fetch/push over HTTP will answer 503 until git is installed.");
        else
            logger.LogInformation("Git Smart HTTP enabled at /git/{{KEY}}/{{slug}}.git using {Git}", gitPath);
        var backend = new GitHttpBackend(gitPath, services.GetRequiredService<RepoStorage>(), logger);

        var group = app.MapGroup("/git");
        group.DisableAntiforgery();

        group.MapGet("/{projectKey}/{repo}/info/refs", (HttpContext ctx, string projectKey, string repo) =>
        {
            var service = ctx.Request.Query["service"].ToString();
            if (service is not (UploadPack or ReceivePack))
            {
                // Dumb protocol (no ?service=) or an unknown service.
                PrepareResponse(ctx);
                return WritePlain(ctx, StatusCodes.Status403Forbidden, "Only the Git Smart HTTP protocol is supported.");
            }
            return HandleAsync(ctx, backend, logger, projectKey, repo, "info/refs", service, isPush: false);
        });

        group.MapPost("/{projectKey}/{repo}/" + UploadPack, (HttpContext ctx, string projectKey, string repo) =>
            HandleAsync(ctx, backend, logger, projectKey, repo, UploadPack, UploadPack, isPush: false));

        group.MapPost("/{projectKey}/{repo}/" + ReceivePack, (HttpContext ctx, string projectKey, string repo) =>
            HandleAsync(ctx, backend, logger, projectKey, repo, ReceivePack, ReceivePack, isPush: true));

        // Anything else under /git (dumb-protocol object/HEAD fetches, wrong methods, junk): plain 404.
        group.Map("/{**rest}", (HttpContext ctx) =>
        {
            PrepareResponse(ctx);
            return WritePlain(ctx, StatusCodes.Status404NotFound, "Not found.");
        });

        return app;
    }

    private static async Task HandleAsync(HttpContext ctx, GitHttpBackend backend, ILogger logger,
        string projectKey, string repo, string rest, string service, bool isPush)
    {
        PrepareResponse(ctx);

        // Strict path validation: "{KEY}/{slug}.git", nothing that could climb out of the repo root.
        if (!repo.EndsWith(".git", StringComparison.Ordinal)) { await NotFound(ctx); return; }
        var slug = repo[..^4];
        if (!ProjectKeyPattern().IsMatch(projectKey) || !SlugPattern().IsMatch(slug) || slug.Contains(".."))
        {
            await NotFound(ctx);
            return;
        }

        if (backend.GitPath is null)
        {
            logger.LogError("Rejecting git request: git executable not found on PATH");
            await WritePlain(ctx, StatusCodes.Status503ServiceUnavailable, "Git is not available on this server.");
            return;
        }

        // Authenticate before looking the repo up so unauthenticated callers can't probe for repos.
        var user = await AuthenticateAsync(ctx, logger, allowCookie: service == UploadPack);
        if (user is null)
        {
            ctx.Response.Headers.WWWAuthenticate = $"Basic realm=\"{Realm}\", charset=\"UTF-8\"";
            await WritePlain(ctx, StatusCodes.Status401Unauthorized, "Authentication required.");
            return;
        }

        var storage = ctx.RequestServices.GetRequiredService<RepoStorage>();
        var dbFactory = ctx.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var upperKey = projectKey.ToUpperInvariant();
        RepoInfo? found;
        await using (var db = await dbFactory.CreateDbContextAsync(ctx.RequestAborted))
        {
            found = await db.Repositories.AsNoTracking()
                .Where(r => r.Project.Key.ToUpper() == upperKey && r.Slug == slug)
                .Select(r => new RepoInfo(r.Id, r.Project.Key, r.Slug))
                .FirstOrDefaultAsync(ctx.RequestAborted);
        }
        if (found is null) { await NotFound(ctx); return; }

        var root = Path.TrimEndingDirectorySeparator(storage.Root) + Path.DirectorySeparatorChar;
        var diskPath = Path.GetFullPath(storage.PathFor(found.Key, found.Slug));
        if (!diskPath.StartsWith(root, StringComparison.Ordinal) || !Directory.Exists(diskPath))
        {
            if (!Directory.Exists(diskPath))
                logger.LogWarning("Repository {Key}/{Slug} (id {Id}) is in the database but missing on disk at {Path}",
                    found.Key, found.Slug, found.Id, diskPath);
            await NotFound(ctx);
            return;
        }

        // Packs can be arbitrarily large; lift Kestrel's 30 MB default for these endpoints.
        var sizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = null;

        // PATH_INFO relative to GIT_PROJECT_ROOT, built from the canonical DB values.
        var relative = Path.GetRelativePath(storage.Root, diskPath).Replace(Path.DirectorySeparatorChar, '/');
        var pathInfo = $"/{relative}/{rest}";

        var (exitCode, requestBytes) = await backend.RunAsync(ctx, pathInfo, user.UserName!);

        // For large pushes git first sends a probe POST whose body is a lone flush packet ("0000")
        // to check auth before streaming chunked; it updates nothing, so don't notify for it.
        var isProbe = requestBytes <= 4;
        if (isPush && !isProbe && exitCode == 0 && ctx.Response.StatusCode == StatusCodes.Status200OK)
        {
            logger.LogInformation("Push to {Key}/{Slug} by {User}", found.Key, found.Slug, user.UserName);
            await NotifyPushListenersAsync(ctx, logger, found.Id, user.Id);
        }
        else if (exitCode != 0)
        {
            logger.LogWarning("git http-backend exited with {ExitCode} for {Method} {PathInfo}",
                exitCode, ctx.Request.Method, pathInfo);
        }
    }

    private sealed record RepoInfo(int Id, string Key, string Slug);

    /// <summary>
    /// HTTP Basic (username or email + password) via Identity, counting failures toward lockout.
    /// For read-only upload-pack, an existing cookie session is also accepted (Basic for push only,
    /// so a cross-site form POST riding the cookie can never write).
    /// </summary>
    private static async Task<User?> AuthenticateAsync(HttpContext ctx, ILogger logger, bool allowCookie)
    {
        var users = ctx.RequestServices.GetRequiredService<UserManager<User>>();
        var header = ctx.Request.Headers.Authorization.ToString();

        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            string decoded;
            try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim())); }
            catch (FormatException) { return null; }
            var sep = decoded.IndexOf(':');
            if (sep <= 0) return null;
            var login = decoded[..sep];
            var password = decoded[(sep + 1)..];

            var user = await users.FindByNameAsync(login);
            if (user is null && login.Contains('@')) user = await users.FindByEmailAsync(login);
            if (user is null)
            {
                logger.LogInformation("Git auth failed: unknown user {Login} from {Ip}", login, ctx.Connection.RemoteIpAddress);
                return null;
            }
            if (await users.IsLockedOutAsync(user))
            {
                logger.LogWarning("Git auth rejected: user {User} is locked out", user.UserName);
                return null;
            }
            if (!await users.CheckPasswordAsync(user, password))
            {
                await users.AccessFailedAsync(user);
                logger.LogInformation("Git auth failed: bad password for {User} from {Ip}", user.UserName, ctx.Connection.RemoteIpAddress);
                return null;
            }
            if (await users.GetAccessFailedCountAsync(user) > 0) await users.ResetAccessFailedCountAsync(user);
            return user;
        }

        if (allowCookie && ctx.User.Identity?.IsAuthenticated == true)
        {
            var user = await users.GetUserAsync(ctx.User);
            if (user is not null && !await users.IsLockedOutAsync(user)) return user;
        }
        return null;
    }

    private static async Task NotifyPushListenersAsync(HttpContext ctx, ILogger logger, int repositoryId, int userId)
    {
        var listeners = ctx.RequestServices.GetServices<IGitPushListener>();
        // The push is already committed; don't let a client disconnect cancel the notifications.
        var ct = ctx.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        foreach (var listener in listeners)
        {
            try
            {
                await listener.OnPushAsync(repositoryId, userId, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "IGitPushListener {Listener} failed for repository {RepositoryId}",
                    listener.GetType().FullName, repositoryId);
            }
        }
    }

    /// <summary>Keep git responses raw: no HTML status-code pages, no caching.</summary>
    private static void PrepareResponse(HttpContext ctx)
    {
        var statusPages = ctx.Features.Get<IStatusCodePagesFeature>();
        if (statusPages is not null) statusPages.Enabled = false;
        ctx.Response.Headers.CacheControl = "no-cache, max-age=0, must-revalidate";
    }

    private static Task NotFound(HttpContext ctx) =>
        WritePlain(ctx, StatusCodes.Status404NotFound, "Repository not found.");

    private static Task WritePlain(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        return ctx.Response.WriteAsync(message + "\n");
    }
}
