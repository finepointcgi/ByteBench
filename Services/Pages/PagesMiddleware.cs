using System.Security.Claims;
using GitBench.Api;
using GitBench.Data;
using GitBench.Services.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace GitBench.Services.Pages;

/// <summary>
/// Serves deployed sites, and nothing else. It answers (a) any request whose host is under <c>Pages:BaseDomain</c> and (b)
/// <c>/pages/{KEY}/{slug}/...</c> on the GitBench host, and passes every other request on. It runs before anything of the
/// Blazor app (routing, antiforgery, static assets), so a site can never reach GitBench's own endpoints through it.
/// <para>
/// <b>Isolation.</b> User-uploaded HTML/JS is the one thing in this app that must never run with GitBench's privileges:
/// </para>
/// <list type="bullet">
/// <item><b>Dedicated host</b> (<c>{slug}.{key}.{BaseDomain}</c>): the site has its own origin, so the browser itself keeps it away
/// from GitBench's cookies, storage and API. The GitBench sign-in cookie is ignored there (members-only sites use their own
/// host-only cookie, see below).</item>
/// <item><b>Path mode</b> (<c>/pages/...</c> on the GitBench host) shares GitBench's origin, so every response carries
/// <c>Content-Security-Policy: sandbox allow-scripts allow-forms allow-popups ...</c> <i>without</i> <c>allow-same-origin</c>: the page
/// gets an opaque origin, so its scripts can't read GitBench's cookies or storage, call GitBench's API as the user, or touch the
/// app's windows. (Consequence: a site's own <c>localStorage</c>/cookies don't work in path mode; use a dedicated host.)</item>
/// <item>Always: <c>X-Content-Type-Options: nosniff</c>, a restrictive <c>Permissions-Policy</c>, no cookies set (except the host
/// login below), no GitBench CSP/headers (this middleware marks the response so the app's headers don't apply). The app's own
/// CSP is untouched.</item>
/// </list>
/// <para>
/// <b>Members-only sites</b> (the default; everyone who can view the project): public sites need no sign-in. For members-only
/// ones every request is checked with ProjectAccess for a user that is proven by (1) on a dedicated host, a host-only HttpOnly
/// cookie obtained by <c>/pages/-/auth</c> on the GitBench host (which needs the GitBench sign-in) and a one-minute signed code;
/// (2) in path mode, either the GitBench sign-in cookie on top-level page loads, which are redirected to a URL carrying a
/// signed, expiring token (<c>/-/t/{token}/</c>), because a sandboxed page's sub-requests don't carry the cookie; relative
/// links keep working inside that URL. Tokens are bound to the repository, expire (default 1 hour) and are re-checked against
/// project membership on every request; pages get <c>Referrer-Policy: no-referrer</c> so the token is not leaked.
/// </para>
/// </summary>
public sealed class PagesMiddleware(RequestDelegate next, IOptionsMonitor<PagesOptions> options, PagesLinks links, PagesStorage storage,
    PagesAccessTokens tokens, IDbContextFactory<AppDbContext> dbFactory, PublicBaseUrl baseUrl)
{
    public const string CookieName = "gb_pages";
    /// <summary><c>sandbox</c> without <c>allow-same-origin</c>: scripts run, but in an opaque origin.</summary>
    public const string PathModeCsp = "sandbox allow-scripts allow-forms allow-popups allow-popups-to-escape-sandbox allow-modals allow-downloads";
    private const string PermissionsPolicy = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";

    private static readonly FileExtensionContentTypeProvider ContentTypes = Create();

    private static FileExtensionContentTypeProvider Create()
    {
        var p = new FileExtensionContentTypeProvider();
        p.Mappings[".mjs"] = "text/javascript";
        p.Mappings[".js"] = "text/javascript";
        p.Mappings[".webmanifest"] = "application/manifest+json";
        p.Mappings[".avif"] = "image/avif";
        p.Mappings[".md"] = "text/markdown";
        p.Mappings[".wasm"] = "application/wasm";
        p.Mappings[".map"] = "application/json";
        return p;
    }

    private sealed record Request(string Key, string Slug, string Slot, string? Token, string[] SitePath, bool TrailingSlash, bool HostMode);

    private sealed record SiteRow(int Id, int RepositoryId, int ProjectId, bool Enabled, PagesVisibility Visibility, PagesNotFoundMode NotFound);

    public async Task InvokeAsync(HttpContext ctx)
    {
        var o = options.CurrentValue;
        var host = ctx.Request.Host.Host;
        if (o.Domain != null && links.IsPagesHost(host))
        {
            ctx.Items[ProductionSetup.OwnSecurityHeadersKey] = true;
            if (!o.Enabled || links.ParseHost(host) is not { } target) { await NotFound(ctx, hostMode: true); return; }
            var segs = ctx.Request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var trailing = ctx.Request.Path.Value.EndsWith('/') || segs.Length == 0;
            if (segs.Length >= 1 && segs[0] == "_gb")
            {
                if (segs.Length == 2 && segs[1] == "pages-login") await HostLogin(ctx, target);
                else await NotFound(ctx, hostMode: true);
                return;
            }
            await Serve(ctx, new Request(target.Key, target.Slug, target.Slot, null, segs, trailing, true));
            return;
        }

        if (!o.Enabled || !ctx.Request.Path.StartsWithSegments(PagesLinks.PathPrefix, out var rest))
        {
            await next(ctx);
            return;
        }
        // /pages/{key}/{slug}[/-/t/{token}][/-/r/{slot}]/path...
        var parts = rest.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && parts[0] == "-" && parts[1] == "auth") { ctx.Items[ProductionSetup.OwnSecurityHeadersKey] = true; await HostAuth(ctx); return; }
        if (parts.Length < 2) { await next(ctx); return; }
        ctx.Items[ProductionSetup.OwnSecurityHeadersKey] = true;
        if (!o.PathModeEnabled) { await NotFound(ctx, hostMode: false); return; }
        if (parts.Length == 2 && !rest.Value.EndsWith('/'))
        {
            Redirect(ctx, StatusCodes.Status301MovedPermanently, ctx.Request.PathBase + ctx.Request.Path.ToUriComponent() + "/" + ctx.Request.QueryString);
            return;
        }
        string? token = null, slot = "";
        var i = 2;
        while (i + 2 < parts.Length && parts[i] == "-" && parts[i + 1] is "t" or "r")
        {
            if (parts[i + 1] == "t") token = parts[i + 2]; else slot = parts[i + 2];
            i += 3;
        }
        await Serve(ctx, new Request(parts[0], parts[1], slot, token, parts[i..], rest.Value.EndsWith('/'), false));
    }

    // ------------------------------------------------------------------ serving

    private async Task Serve(HttpContext ctx, Request r)
    {
        var o = options.CurrentValue;
        var method = ctx.Request.Method;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
        {
            ctx.Response.Headers.Allow = "GET, HEAD";
            await Plain(ctx, StatusCodes.Status405MethodNotAllowed, "Method not allowed.", r.HostMode, false);
            return;
        }
        if (r.Slot.Length > 0 && !PagesSlots.IsValid(r.Slot)) { await NotFound(ctx, r.HostMode); return; }

        await using var db = await dbFactory.CreateDbContextAsync();
        var key = r.Key.ToUpperInvariant();
        var slug = r.Slug.ToLowerInvariant();
        var site = await db.PagesSites.AsNoTracking().Where(s => s.Repository.Slug == slug && s.Repository.Project.Key == key)
            .Select(s => new SiteRow(s.Id, s.RepositoryId, s.Repository.ProjectId, s.Enabled, s.Visibility, s.NotFound)).FirstOrDefaultAsync();
        if (site is not { Enabled: true }) { await NotFound(ctx, r.HostMode); return; }

        var isPublic = site.Visibility == PagesVisibility.Public && o.AllowPublic;
        var (verdict, viaCookie) = isPublic ? (Verdict.Allowed, false) : await Authorize(ctx, site, r);
        switch (verdict)
        {
            case Verdict.Denied: await NotFound(ctx, r.HostMode); return;
            case Verdict.Challenge: await Challenge(ctx, r); return;
        }
        var members = !isPublic;
        // Path mode, signed in with the GitBench cookie: page loads move to a tokenized URL so sub-resources work in the sandbox.
        if (members && !r.HostMode && viaCookie && IsDocumentRequest(ctx.Request))
        {
            var userId = UserIdOf(ctx.User)!.Value;
            var t = tokens.Create(PagesTokenKind.PathLink, userId, site.RepositoryId, TimeSpan.FromMinutes(Math.Max(1, o.PathTokenMinutes)));
            var path = $"{PagesLinks.PathPrefix}/{Uri.EscapeDataString(r.Key)}/{Uri.EscapeDataString(r.Slug)}/-/t/{t}/"
                + (r.Slot.Length > 0 ? $"-/r/{r.Slot}/" : "")
                + string.Join('/', r.SitePath.Select(Uri.EscapeDataString)) + (r.TrailingSlash && r.SitePath.Length > 0 ? "/" : "");
            ctx.Response.Headers.CacheControl = "no-store";
            Secure(ctx, false, true, true);
            Redirect(ctx, StatusCodes.Status302Found, ctx.Request.PathBase + path + ctx.Request.QueryString, secured: true);
            return;
        }
        if (members && viaCookie && !r.HostMode && !IsDocumentRequest(ctx.Request)
            && string.Equals(ctx.Request.Headers["Sec-Fetch-Site"], "cross-site", StringComparison.OrdinalIgnoreCase))
        {
            await NotFound(ctx, false);
            return;
        }

        var dep = await db.PagesDeployments.AsNoTracking().Where(d => d.SiteId == site.Id && d.Slot == r.Slot && d.IsCurrent)
            .Select(d => new { d.Id, d.Directory, d.ExpiresAt }).FirstOrDefaultAsync();
        if (dep == null || dep.ExpiresAt is { } exp && exp < DateTime.UtcNow || !PagesStorage.IsValidDirectory(dep.Directory)) { await NotFound(ctx, r.HostMode, members); return; }

        var root = Path.TrimEndingDirectorySeparator(storage.DeploymentPath(site.RepositoryId, dep.Directory));
        var found = Resolve(root, r.SitePath, r.TrailingSlash, out var redirectToDir);
        Secure(ctx, r.HostMode, members, r.Token != null);
        if (redirectToDir)
        {
            Redirect(ctx, StatusCodes.Status301MovedPermanently, ctx.Request.PathBase + ctx.Request.Path.ToUriComponent() + "/" + ctx.Request.QueryString, secured: true);
            return;
        }
        if (found != null)
        {
            await SendFile(ctx, found, dep.Id, StatusCodes.Status200OK);
            return;
        }

        // No such file: the site's 404.html, an SPA fallback, or the plain page.
        string? errorFile = null;
        var status = StatusCodes.Status404NotFound;
        if (site.NotFound == PagesNotFoundMode.SpaFallback && r.SitePath.Length > 0 && !Path.HasExtension(r.SitePath[^1]))
        {
            errorFile = RegularFile(Path.Combine(root, "index.html"));
            if (errorFile != null) status = StatusCodes.Status200OK;
        }
        if (errorFile == null && site.NotFound != PagesNotFoundMode.Plain) errorFile = RegularFile(Path.Combine(root, "404.html"));
        if (errorFile != null) await SendFile(ctx, errorFile, dep.Id, status, fresh: true);
        else await Plain(ctx, StatusCodes.Status404NotFound, "404 Not Found", r.HostMode, members, secured: true);
    }

    /// <summary>The regular file for a request path: itself, <c>index.html</c> of a folder, or <c>name.html</c>; null if none.</summary>
    public static string? Resolve(string root, string[] sitePath, bool trailingSlash, out bool redirectToDir)
    {
        redirectToDir = false;
        foreach (var s in sitePath)
            if (s is "." or ".." || s.Contains('\\') || s.Contains('\0')) return null;
        var full = Path.GetFullPath(Path.Combine(root, string.Join(Path.DirectorySeparatorChar, sitePath)));
        if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        if (sitePath.Length == 0 || trailingSlash) return RegularFile(Path.Combine(full, "index.html"));
        if (RegularFile(full) is { } f) return f;
        if (Directory.Exists(full)) { redirectToDir = RegularFile(Path.Combine(full, "index.html")) != null; return null; }
        return RegularFile(full + ".html");
    }

    private static string? RegularFile(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.LinkTarget == null ? path : null;
    }

    private async Task SendFile(HttpContext ctx, string path, int deploymentId, int status, bool fresh = false)
    {
        var info = new FileInfo(path);
        if (!ContentTypes.TryGetContentType(path, out var type)) type = "application/octet-stream";
        if (type.StartsWith("text/") || type is "application/json" or "application/xml" or "image/svg+xml" or "application/manifest+json" or "application/javascript")
            type += "; charset=utf-8";
        if (status != StatusCodes.Status200OK || fresh)
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = type;
            ctx.Response.ContentLength = info.Length;
            ctx.Response.Headers.CacheControl = "no-cache";
            if (!HttpMethods.IsHead(ctx.Request.Method)) await ctx.Response.SendFileAsync(path);
            return;
        }
        var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        var etag = new EntityTagHeaderValue($"\"{deploymentId}-{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"");
        await Results.File(path, type, lastModified: modified, entityTag: etag, enableRangeProcessing: true).ExecuteAsync(ctx);
    }

    // ------------------------------------------------------------------ access

    private enum Verdict { Allowed, Denied, Challenge }

    private async Task<(Verdict, bool ViaCookie)> Authorize(HttpContext ctx, SiteRow site, Request r)
    {
        int? userId = null;
        var viaCookie = false;
        if (r.HostMode) userId = tokens.Validate(PagesTokenKind.HostCookie, ctx.Request.Cookies[CookieName], site.RepositoryId);
        else
        {
            if (r.Token != null) userId = tokens.Validate(PagesTokenKind.PathLink, r.Token, site.RepositoryId);
            if (userId == null && UserIdOf(ctx.User) is { } uid) { userId = uid; viaCookie = true; }
        }
        if (userId == null) return (Verdict.Challenge, false);
        var access = ctx.RequestServices.GetRequiredService<ProjectAccess>();
        access.ActAs(userId.Value);
        return await access.CanAsync(site.ProjectId, Permission.ViewProject) ? (Verdict.Allowed, viaCookie) : (Verdict.Denied, false);
    }

    private static int? UserIdOf(ClaimsPrincipal? user) =>
        int.TryParse(user?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static bool IsDocumentRequest(HttpRequest req)
    {
        var dest = req.Headers["Sec-Fetch-Dest"].ToString();
        if (dest.Length > 0) return dest is "document" or "iframe" or "frame";
        return req.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Not signed in (or the token expired): send a page load to sign in, answer anything else 401.</summary>
    private async Task Challenge(HttpContext ctx, Request r)
    {
        if (!IsDocumentRequest(ctx.Request)) { await Plain(ctx, StatusCodes.Status401Unauthorized, "Sign in to GitBench to see this site.", r.HostMode, true); return; }
        Secure(ctx, r.HostMode, true, false);
        ctx.Response.Headers.CacheControl = "no-store";
        if (r.HostMode)
        {
            var main = baseUrl.Value;
            if (main.Length == 0) { await Plain(ctx, StatusCodes.Status500InternalServerError, "PublicBaseUrl must be configured to sign in to members-only sites.", true, true, secured: true); return; }
            var back = $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.Path.ToUriComponent()}{ctx.Request.QueryString}";
            Redirect(ctx, StatusCodes.Status302Found, $"{main}{PagesLinks.PathPrefix}/-/auth?return={Uri.EscapeDataString(back)}", secured: true);
            return;
        }
        // The same page without the (expired or invalid) token.
        var clean = $"{PagesLinks.PathPrefix}/{Uri.EscapeDataString(r.Key)}/{Uri.EscapeDataString(r.Slug)}/"
            + (r.Slot.Length > 0 ? $"-/r/{r.Slot}/" : "") + string.Join('/', r.SitePath.Select(Uri.EscapeDataString))
            + (r.TrailingSlash && r.SitePath.Length > 0 ? "/" : "") + ctx.Request.QueryString;
        Redirect(ctx, StatusCodes.Status302Found, $"{ctx.Request.PathBase}/account/login?returnUrl={Uri.EscapeDataString(ctx.Request.PathBase + clean)}", secured: true);
    }

    /// <summary>GET /pages/-/auth?return={URL on a dedicated host}: after GitBench sign-in, hands a one-minute code to that host.</summary>
    private async Task HostAuth(HttpContext ctx)
    {
        if (!HttpMethods.IsGet(ctx.Request.Method)) { await Plain(ctx, 405, "Method not allowed.", false, true); return; }
        ctx.Response.Headers.CacheControl = "no-store";
        if (!Uri.TryCreate(ctx.Request.Query["return"], UriKind.Absolute, out var back) || back.Scheme is not ("http" or "https")
            || links.ParseHost(back.Host) is not { } target || !back.AbsolutePath.StartsWith('/'))
        {
            await Plain(ctx, StatusCodes.Status400BadRequest, "Invalid return address.", false, true);
            return;
        }
        if (UserIdOf(ctx.User) is not { } userId)
        {
            Secure(ctx, false, true, false);
            Redirect(ctx, StatusCodes.Status302Found, $"{ctx.Request.PathBase}/account/login?returnUrl={Uri.EscapeDataString(ctx.Request.PathBase + ctx.Request.Path.ToUriComponent() + ctx.Request.QueryString)}", secured: true);
            return;
        }
        await using var db = await dbFactory.CreateDbContextAsync();
        var key = target.Key.ToUpperInvariant();
        var site = await db.PagesSites.AsNoTracking().Where(s => s.Repository.Slug == target.Slug && s.Repository.Project.Key == key)
            .Select(s => new SiteRow(s.Id, s.RepositoryId, s.Repository.ProjectId, s.Enabled, s.Visibility, s.NotFound)).FirstOrDefaultAsync();
        var access = ctx.RequestServices.GetRequiredService<ProjectAccess>();
        access.ActAs(userId);
        if (site is not { Enabled: true } || !await access.CanAsync(site.ProjectId, Permission.ViewProject)) { await NotFound(ctx, false); return; }
        var code = tokens.Create(PagesTokenKind.HostExchange, userId, site.RepositoryId, TimeSpan.FromMinutes(1));
        Secure(ctx, false, true, true);
        Redirect(ctx, StatusCodes.Status302Found, $"{back.Scheme}://{back.Authority}/_gb/pages-login?code={Uri.EscapeDataString(code)}&next={Uri.EscapeDataString(back.PathAndQuery)}", secured: true);
    }

    /// <summary>GET /_gb/pages-login?code=...&amp;next=/path on a dedicated host: trades the code for the host-only cookie.</summary>
    private async Task HostLogin(HttpContext ctx, PagesTarget target)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        await using var db = await dbFactory.CreateDbContextAsync();
        var key = target.Key.ToUpperInvariant();
        var repoId = await db.PagesSites.AsNoTracking().Where(s => s.Repository.Slug == target.Slug && s.Repository.Project.Key == key)
            .Select(s => (int?)s.RepositoryId).FirstOrDefaultAsync();
        var userId = repoId == null ? null : tokens.Validate(PagesTokenKind.HostExchange, ctx.Request.Query["code"], repoId.Value);
        if (userId == null) { await Plain(ctx, StatusCodes.Status403Forbidden, "This sign-in link is invalid or has expired.", true, true); return; }
        var next = ctx.Request.Query["next"].ToString();
        if (!next.StartsWith('/') || next.StartsWith("//") || next.Contains('\\') || next.Any(char.IsControl)) next = "/";
        var o = options.CurrentValue;
        var lifetime = TimeSpan.FromHours(Math.Max(1, o.HostCookieHours));
        ctx.Response.Cookies.Append(CookieName, tokens.Create(PagesTokenKind.HostCookie, userId.Value, repoId!.Value, lifetime), new CookieOptions
        {
            HttpOnly = true, Secure = ctx.Request.IsHttps, SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax, Path = "/", MaxAge = lifetime, IsEssential = true,
        });
        Secure(ctx, true, true, true);
        Redirect(ctx, StatusCodes.Status302Found, next, secured: true);
    }

    // ------------------------------------------------------------------ responses

    /// <summary>Headers every Pages response has; see the class remarks for why.</summary>
    private void Secure(HttpContext ctx, bool hostMode, bool members, bool noReferrer)
    {
        var h = ctx.Response.Headers;
        ctx.Items[ProductionSetup.OwnSecurityHeadersKey] = true;
        h.XContentTypeOptions = "nosniff";
        h["Permissions-Policy"] = PermissionsPolicy;
        h["Referrer-Policy"] = members || noReferrer ? "no-referrer" : "strict-origin-when-cross-origin";
        if (!hostMode) h.ContentSecurityPolicy = PathModeCsp;
        if (!h.ContainsKey(HeaderNames.CacheControl))
            h.CacheControl = members ? "private, no-cache" : "public, max-age=60, must-revalidate";
    }

    private void Redirect(HttpContext ctx, int status, string location, bool secured = false)
    {
        if (!secured) Secure(ctx, links.IsPagesHost(ctx.Request.Host.Host), false, false);
        ctx.Response.StatusCode = status;
        ctx.Response.Headers.Location = location;
        ctx.Response.ContentLength = 0;
    }

    private Task NotFound(HttpContext ctx, bool hostMode, bool members = false) => Plain(ctx, StatusCodes.Status404NotFound, "404 Not Found", hostMode, members);

    private async Task Plain(HttpContext ctx, int status, string text, bool hostMode, bool members, bool secured = false)
    {
        if (!secured) Secure(ctx, hostMode, members, false);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsHead(ctx.Request.Method)) await ctx.Response.WriteAsync(text);
    }
}
