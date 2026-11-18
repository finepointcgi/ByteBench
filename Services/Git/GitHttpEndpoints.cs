using System.Text;
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
/// require HTTP Basic credentials checked against Identity (username or email + password or personal access
/// token), honoring lockout. Then project permissions apply (<see cref="GitBench.Services.ProjectAccess"/>):
/// clone/fetch need ViewProject, push needs PushCode (and a token needs the WriteRepository scope). A user who
/// can't view the project gets 404, so the repository's existence isn't revealed.
/// </summary>
public static class GitHttpEndpoints
{
    private const string Realm = "GitBench";
    private const string UploadPack = "git-upload-pack";
    private const string ReceivePack = "git-receive-pack";

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
        if (!GitRepoLookup.IsValid(projectKey, slug))
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
        var (user, tokenScopes, authError) = await AuthenticateAsync(ctx, logger, allowCookie: service == UploadPack);
        if (user is null)
        {
            ctx.Response.Headers.WWWAuthenticate = $"Basic realm=\"{Realm}\", charset=\"UTF-8\"";
            await WritePlain(ctx, StatusCodes.Status401Unauthorized, authError ?? "Authentication required.");
            return;
        }

        var storage = ctx.RequestServices.GetRequiredService<RepoStorage>();
        var dbFactory = ctx.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var found = await GitRepoLookup.FindAsync(dbFactory, projectKey, slug, ctx.RequestAborted);
        if (found is null) { await NotFound(ctx); return; }

        // Project permissions. Not viewable → 404 so the repo's existence isn't revealed.
        var access = ctx.RequestServices.GetRequiredService<GitBench.Services.ProjectAccess>();
        access.ActAs(user.Id); // push listeners run in this scope as the pushing user
        var role = await access.GetRoleAsync(found.ProjectId, user.Id);
        if (role is null || (found.IsWiki && !found.WikiEnabled)) { await NotFound(ctx); return; }
        var writing = service == ReceivePack;
        if (writing && !GitBench.Services.ProjectAccess.RoleHas(role.Value, GitRepoLookup.WritePermission(found)))
        {
            logger.LogInformation("Git push to {Key}/{Slug} denied for {User} (role {Role})", found.Key, found.Slug, user.UserName, role);
            await WritePlain(ctx, StatusCodes.Status403Forbidden, $"You don't have permission to push to this repository (requires Developer; you are {role}).");
            return;
        }
        if (writing && found.IsArchived)
        {
            logger.LogInformation("Git push to archived {Key}/{Slug} rejected for {User}", found.Key, found.Slug, user.UserName);
            await RejectArchivedPushAsync(ctx, rest);
            return;
        }
        if (tokenScopes is { } scopes)
        {
            var needed = writing ? TokenScopes.WriteRepository : TokenScopes.ReadRepository | TokenScopes.WriteRepository;
            if ((scopes & needed) == 0)
            {
                await WritePlain(ctx, StatusCodes.Status403Forbidden, writing
                    ? "This access token doesn't have the write_repository scope."
                    : "This access token doesn't have the read_repository scope.");
                return;
            }
        }

        if (GitRepoLookup.DiskPath(storage, found, logger) is not { } diskPath)
        {
            await NotFound(ctx);
            return;
        }

        // Packs can be arbitrarily large; lift Kestrel's 30 MB default for these endpoints.
        var sizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = null;

        // PATH_INFO relative to GIT_PROJECT_ROOT, built from the canonical DB values.
        var relative = Path.GetRelativePath(storage.Root, diskPath).Replace(Path.DirectorySeparatorChar, '/');
        var pathInfo = $"/{relative}/{rest}";

        // Branch protection: refuse protected ref updates before git sees them (see ProtectedPushGuard).
        Stream? body = null;
        IReadOnlyDictionary<string, string>? extraEnv = null;
        if (isPush && !found.IsWiki) // wikis have no branch protection
        {
            var guard = await ProtectedPushGuard.InspectAsync(ctx, logger, found.Id, found.ProjectId, user.Id, storage);
            if (guard.Rejected) return;
            (body, extraEnv) = (guard.Body, guard.Environment);
        }

        var (exitCode, requestBytes) = await backend.RunAsync(ctx, pathInfo, user.UserName!, body, extraEnv);

        // For large pushes git first sends a probe POST whose body is a lone flush packet ("0000")
        // to check auth before streaming chunked; it updates nothing, so don't notify for it.
        var isProbe = requestBytes <= 4;
        if (isPush && !isProbe && exitCode == 0 && ctx.Response.StatusCode == StatusCodes.Status200OK)
        {
            logger.LogInformation("Push to {Key}/{Slug} by {User}", found.Key, found.Slug, user.UserName);
            if (found.IsWiki) await GitRepoLookup.AfterWikiPushAsync(ctx.RequestServices, logger, found);
            else await GitRepoLookup.NotifyPushListenersAsync(ctx.RequestServices, logger, found.Id, user.Id);
        }
        else if (exitCode != 0)
        {
            logger.LogWarning("git http-backend exited with {ExitCode} for {Method} {PathInfo}",
                exitCode, ctx.Request.Method, pathInfo);
        }
    }

    private const string ArchivedMessage = GitRepoLookup.ArchivedMessage;

    /// <summary>
    /// Refuses a push to an archived repository. On the ref advertisement git prints an <c>ERR</c> packet as
    /// "remote error: …", which is far clearer than a bare HTTP 403; the pack POST itself gets a plain 403.
    /// </summary>
    private static async Task RejectArchivedPushAsync(HttpContext ctx, string rest)
    {
        if (rest != "info/refs")
        {
            await WritePlain(ctx, StatusCodes.Status403Forbidden, ArchivedMessage);
            return;
        }
        static string Pkt(string line) => (Encoding.UTF8.GetByteCount(line) + 4).ToString("x4") + line;
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/x-" + ReceivePack + "-advertisement";
        await ctx.Response.WriteAsync(Pkt("# service=" + ReceivePack + "\n") + "0000" + Pkt("ERR " + ArchivedMessage + "\n"));
    }

    /// <summary>
    /// HTTP Basic via Identity: the login is a username or email, the password is either the account password or
    /// one of that user's personal access tokens (<c>gbp_…</c>, unrevoked and unexpired). Failures count toward
    /// lockout. For read-only upload-pack, an existing cookie session is also accepted (Basic for push only, so a
    /// cross-site form POST riding the cookie can never write). Scopes are returned when a token was used, else null.
    /// Accounts with two-factor sign-in must use a token: their correct password is refused with a message saying so.
    /// </summary>
    private static async Task<(User? User, TokenScopes? Scopes, string? Error)> AuthenticateAsync(HttpContext ctx, ILogger logger, bool allowCookie)
    {
        var users = ctx.RequestServices.GetRequiredService<UserManager<User>>();
        var header = ctx.Request.Headers.Authorization.ToString();

        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            string decoded;
            try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim())); }
            catch (FormatException) { return (null, null, null); }
            var sep = decoded.IndexOf(':');
            if (sep <= 0) return (null, null, null);
            var login = decoded[..sep];
            var password = decoded[(sep + 1)..];

            var user = await users.FindByNameAsync(login);
            if (user is null && login.Contains('@')) user = await users.FindByEmailAsync(login);
            if (user is null)
            {
                logger.LogInformation("Git auth failed: unknown user {Login} from {Ip}", login, ctx.Connection.RemoteIpAddress);
                return (null, null, null);
            }
            if (await users.IsLockedOutAsync(user) || user.DeactivatedAt != null)
            {
                logger.LogWarning("Git auth rejected: user {User} is locked out", user.UserName);
                return (null, null, null);
            }

            TokenScopes? scopes = null;
            if (password.StartsWith(GitBench.Services.AccessTokenService.TokenPrefix, StringComparison.Ordinal)
                && await ctx.RequestServices.GetRequiredService<GitBench.Services.AccessTokenService>().ValidateAsync(user.Id, password) is { } token)
            {
                scopes = token.Scopes;
                logger.LogInformation("Git auth: {User} used access token {Prefix} ({Scopes})", user.UserName, token.Prefix, token.Scopes);
            }
            else if (!await users.CheckPasswordAsync(user, password))
            {
                await users.AccessFailedAsync(user);
                logger.LogInformation("Git auth failed: bad password or token for {User} from {Ip}", user.UserName, ctx.Connection.RemoteIpAddress);
                return (null, null, null);
            }
            else if (await users.GetTwoFactorEnabledAsync(user))
            {
                // Like GitHub: with two-factor sign-in on, the password alone isn't enough; git needs a token.
                logger.LogInformation("Git auth refused: {User} has two-factor sign-in and used a password", user.UserName);
                return (null, null, "Password authentication is not supported for accounts with two-factor sign-in. " +
                    "Use a personal access token as the password (Profile → Access tokens).");
            }
            if (await users.GetAccessFailedCountAsync(user) > 0) await users.ResetAccessFailedCountAsync(user);
            return (user, scopes, null);
        }

        if (allowCookie && ctx.User.Identity?.IsAuthenticated == true)
        {
            var user = await users.GetUserAsync(ctx.User);
            if (user is not null && !await users.IsLockedOutAsync(user)) return (user, null, null);
        }
        return (null, null, null);
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
