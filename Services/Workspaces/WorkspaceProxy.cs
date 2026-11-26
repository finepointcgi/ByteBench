using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using ByteBench.Data;
using ByteBench.Services.Hosting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Workspaces;

/// <summary>
/// The only way to reach a workspace's IDE and the servers running in it. Containers publish nothing but a loopback port; this middleware
/// authenticates and then relays HTTP and WebSocket (raw upgrade, so extensions like permessage-deflate pass through untouched).
/// <para>
/// <b>Subdomain mode</b> (<c>Workspaces:BaseDomain</c>): <c>ide-{id}.{base}</c> and <c>p3000-{id}.{base}</c> are separate origins with
/// host-only cookies. A signed-in ByteBench user gets a single-use ticket (60 s) from ByteBench's own origin and hands it to
/// <c>/_bb/enter</c> on the workspace origin, which turns it into that origin's own session cookie. ByteBench's session cookie is never sent
/// there, and scripts in the workspace can't read or act on ByteBench's origin.
/// </para>
/// <para>
/// <b>Shared-origin mode</b> (<c>AllowSharedOrigin</c>, no base domain): <c>/_ws/{id}/ide/…</c> and <c>/_ws/{id}/p3000/…</c> on ByteBench's origin,
/// authenticated by the normal session. Previews carry CSP <c>sandbox</c> (opaque origin: no cookies, no access to ByteBench); the IDE can't be sandboxed.
/// </para>
/// In both modes ByteBench's cookies are never forwarded upstream, the upstream's CSP/frame headers are replaced, and the OpenVSCode connection
/// token is injected by the proxy so the browser never learns it.
/// </summary>
public sealed class WorkspaceProxyMiddleware(RequestDelegate next, WorkspaceUrls urls, WorkspaceRuntime runtime, IWorkspaceBackend backend,
    IServiceScopeFactory scopes, ILogger<WorkspaceProxyMiddleware> log)
{
    public const string SessionCookie = "bb_ws";
    private const string TokenCookie = "vscode-tkn";

    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Proxy-Connection",
    };

    private readonly ConcurrentDictionary<string, HttpMessageInvoker> clients = new();
    private readonly ConcurrentDictionary<int, (DateTime At, bool Ok)> ownerChecks = new();

    public async Task InvokeAsync(HttpContext ctx)
    {
        var mode = urls.Mode;
        if (mode == WorkspaceOriginMode.Subdomain && urls.TryGetLabel(ctx.Request.Host.Host, out var label)
            && WorkspaceUrls.TryParseLabel(label, out var audience, out var publicId, out var port))
        {
            await HandleSubdomainAsync(ctx, audience, publicId, port);
            return;
        }
        if (mode == WorkspaceOriginMode.SharedPath && ctx.Request.Path.StartsWithSegments("/_ws", out var rest))
        {
            await HandleSharedAsync(ctx, rest);
            return;
        }
        await next(ctx);
    }

    // ---------- subdomain mode ----------

    private async Task HandleSubdomainAsync(HttpContext ctx, string audience, string publicId, int? previewPort)
    {
        Prepare(ctx);
        var live = runtime.Find(publicId);
        if (live == null) { await Plain(ctx, 404, "This workspace isn't running."); return; }

        if (ctx.Request.Path == WorkspaceUrls.EnterPath)
        {
            await EnterAsync(ctx, live, audience);
            return;
        }
        var cookie = ctx.Request.Cookies[SessionCookie];
        var grant = cookie == null ? null : runtime.FindSession(cookie, live.Id, audience);
        if (grant == null || grant.UserId != live.OwnerId || !await OwnerStillActiveAsync(live.OwnerId))
        {
            await Plain(ctx, 401, "Open this workspace from ByteBench (Workspaces → Open).");
            return;
        }
        live.Touch();
        var framing = $"frame-ancestors {grant.MainOrigin}";
        await ForwardAsync(ctx, live, previewPort ?? live.IdePort, ctx.Request.PathBase + ctx.Request.Path + ctx.Request.QueryString, framing,
            stripAllCookies: false, injectToken: previewPort == null);
    }

    private async Task EnterAsync(HttpContext ctx, LiveWorkspace live, string audience)
    {
        var ticket = ctx.Request.Query["ticket"].ToString();
        var nextPath = ctx.Request.Query["next"].ToString();
        if (!HttpMethods.IsGet(ctx.Request.Method) || ticket.Length == 0) { await Plain(ctx, 400, "Missing ticket."); return; }
        var session = runtime.RedeemTicket(ticket, live.Id, audience, out var userId);
        if (session == null || userId != live.OwnerId) { await Plain(ctx, 401, "This link has expired. Open the workspace from ByteBench again."); return; }
        var secure = ctx.Request.IsHttps;
        // The IDE is framed by ByteBench's page, which may be another site: a cookie set there needs SameSite=None, which needs Secure
        // (https, or *.localhost for development, which browsers treat as secure), and is stored Partitioned (CHIPS) so it keeps working
        // where third-party cookies are blocked. Plain HTTP elsewhere can only use Lax (fine for "open in a new tab").
        var crossSite = secure || ctx.Request.Host.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
        var cookieOptions = new CookieOptions
        {
            HttpOnly = true, Path = "/", Secure = crossSite, SameSite = crossSite ? SameSiteMode.None : SameSiteMode.Lax,
            MaxAge = WorkspaceRuntime.SessionLifetime,
        };
        if (crossSite) cookieOptions.Extensions.Add("Partitioned");
        ctx.Response.Cookies.Append(SessionCookie, session, cookieOptions);
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer"; // the ticket is in the URL
        ctx.Response.Redirect(LocalPath(nextPath));
    }

    /// <summary>Only a path on this origin: starts with a single '/', no scheme-relative or backslash tricks.</summary>
    public static string LocalPath(string? p) =>
        p is { Length: > 0 } && p[0] == '/' && !p.StartsWith("//", StringComparison.Ordinal) && !p.Contains('\\') && !p.Any(char.IsControl) ? p : "/";

    // ---------- shared-origin mode ----------

    private async Task HandleSharedAsync(HttpContext ctx, PathString rest)
    {
        Prepare(ctx);
        var segments = rest.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (segments.Length < 2 || !WorkspaceTokens.PublicIdPattern.IsMatch(segments[0])) { await Plain(ctx, 404, "Not found."); return; }
        var live = runtime.Find(segments[0]);
        var userId = int.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : 0;
        if (live == null || userId == 0 || live.OwnerId != userId) { await Plain(ctx, 404, "Not found."); return; } // not revealed to others
        var isIde = segments[1] == "ide";
        int? port = null;
        if (!isIde)
        {
            if (!segments[1].StartsWith('p') || !int.TryParse(segments[1].AsSpan(1), out var p) || p is < 1 or > 65535) { await Plain(ctx, 404, "Not found."); return; }
            port = p;
        }
        live.Touch();
        // The IDE was started with --server-base-path, so it keeps the whole path; a preview app doesn't know about the prefix.
        var prefixLength = "/_ws/".Length + segments[0].Length + 1 + segments[1].Length;
        var path = isIde ? ctx.Request.Path.Value! : ctx.Request.Path.Value![prefixLength..];
        if (path.Length == 0) path = "/";
        if (!isIde && path.Length > 0 && path[0] != '/') path = "/" + path;
        var csp = isIde ? "frame-ancestors 'self'" : "sandbox allow-scripts allow-forms allow-popups allow-modals allow-downloads; frame-ancestors 'self'";
        await ForwardAsync(ctx, live, port ?? live.IdePort, path + ctx.Request.QueryString, csp, stripAllCookies: true, injectToken: isIde);
    }

    // ---------- relay ----------

    private static void Prepare(HttpContext ctx)
    {
        ctx.Items[ProductionSetup.OwnSecurityHeadersKey] = true; // the app's own headers and CSP don't apply to workspace content
        if (ctx.Features.Get<IStatusCodePagesFeature>() is { } pages) pages.Enabled = false;
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size) size.MaxRequestBodySize = null; // uploads into the IDE
    }

    private static Task Plain(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.XContentTypeOptions = "nosniff";
        return ctx.Response.WriteAsync(message + "\n");
    }

    private async Task<bool> OwnerStillActiveAsync(int ownerId)
    {
        if (ownerChecks.TryGetValue(ownerId, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(60)) return c.Ok;
        await using var scope = scopes.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        var owner = await db.Users.AsNoTracking().Where(u => u.Id == ownerId).Select(u => new { u.DeactivatedAt, u.LockoutEnd }).FirstOrDefaultAsync();
        var ok = owner != null && owner.DeactivatedAt == null && !(owner.LockoutEnd > now);
        ownerChecks[ownerId] = (DateTime.UtcNow, ok);
        return ok;
    }

    private async Task ForwardAsync(HttpContext ctx, LiveWorkspace live, int port, string pathAndQuery, string cspToAdd, bool stripAllCookies, bool injectToken)
    {
        var upgrade = ctx.Request.Headers.Connection.ToString().Contains("upgrade", StringComparison.OrdinalIgnoreCase)
                      && string.Equals(ctx.Request.Headers.Upgrade.ToString(), "websocket", StringComparison.OrdinalIgnoreCase);
        var cookie = BuildCookie(ctx.Request.Headers.Cookie, stripAllCookies, injectToken ? live.ConnectionToken : null);
        try
        {
            if (upgrade) await TunnelAsync(ctx, live, port, pathAndQuery, cookie);
            else await RelayAsync(ctx, live, port, pathAndQuery, cookie, cspToAdd);
        }
        catch (Exception ex) when (ex is IOException or SocketException or HttpRequestException or InvalidOperationException && !ctx.Response.HasStarted)
        {
            log.LogDebug(ex, "Workspace {Id}: upstream port {Port} not reachable", live.PublicId, port);
            if (!ctx.Response.HasStarted) await Plain(ctx, 502, $"Nothing answered on port {port} in the workspace.");
        }
    }

    /// <summary>The Cookie header for upstream: ByteBench's session cookie never goes (nor, in shared mode, any other), and the IDE gets its token.</summary>
    public static string? BuildCookie(string? header, bool stripAll, string? connectionToken)
    {
        var parts = new List<string>();
        if (!stripAll && !string.IsNullOrEmpty(header))
            foreach (var pair in header.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var name = pair.Split('=', 2)[0].Trim();
                if (name == SessionCookie || name == TokenCookie) continue;
                parts.Add(pair);
            }
        if (connectionToken != null) parts.Add($"{TokenCookie}={connectionToken}");
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private HttpMessageInvoker ClientFor(LiveWorkspace live, int port) => clients.GetOrAdd($"{live.ContainerId}:{live.IdeHostPort}:{port}", _ =>
        new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15), PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (_, ct) => await backend.ConnectAsync(live.Endpoint, port, ct),
        }));

    private async Task RelayAsync(HttpContext ctx, LiveWorkspace live, int port, string pathAndQuery, string? cookie, string csp)
    {
        var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), "http://workspace.invalid" + pathAndQuery) { Version = HttpVersion.Version11 };
        req.Headers.Host = ctx.Request.Host.Value;
        var hasBody = ctx.Request.ContentLength > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding");
        if (hasBody) req.Content = new StreamContent(ctx.Request.Body);
        foreach (var (name, values) in ctx.Request.Headers)
        {
            if (HopByHop.Contains(name) || name.Equals("Host", StringComparison.OrdinalIgnoreCase) || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)) continue;
            if (!req.Headers.TryAddWithoutValidation(name, (IEnumerable<string>)values))
                req.Content?.Headers.TryAddWithoutValidation(name, (IEnumerable<string>)values);
        }
        if (cookie != null) req.Headers.TryAddWithoutValidation("Cookie", cookie);
        req.Headers.TryAddWithoutValidation("X-Forwarded-Proto", ctx.Request.Scheme);
        req.Headers.TryAddWithoutValidation("X-Forwarded-Host", ctx.Request.Host.Value);

        using var resp = await ClientFor(live, port).SendAsync(req, ctx.RequestAborted);
        ctx.Response.StatusCode = (int)resp.StatusCode;
        foreach (var h in resp.Headers.Concat(resp.Content.Headers))
        {
            if (HopByHop.Contains(h.Key) || IsOwnedHeader(h.Key)) continue;
            if (h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)) { AddSetCookies(ctx, h.Value); continue; }
            ctx.Response.Headers[h.Key] = h.Value.ToArray();
        }
        ctx.Response.Headers.Append("Content-Security-Policy", csp);
        ctx.Response.Headers["Referrer-Policy"] = "same-origin";
        await resp.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }

    /// <summary>Headers the proxy decides itself: framing (replaced by the proxy's CSP) and anything that could name ByteBench.</summary>
    private static bool IsOwnedHeader(string name) =>
        name.Equals("X-Frame-Options", StringComparison.OrdinalIgnoreCase) || name.Equals("Strict-Transport-Security", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Referrer-Policy", StringComparison.OrdinalIgnoreCase) || name.Equals("Content-Security-Policy-Report-Only", StringComparison.OrdinalIgnoreCase);

    private static void AddSetCookies(HttpContext ctx, IEnumerable<string> values)
    {
        foreach (var v in values)
        {
            // The workspace's own app may set cookies on its own origin, but never ours.
            if (v.StartsWith(SessionCookie + "=", StringComparison.OrdinalIgnoreCase)) continue;
            ctx.Response.Headers.Append("Set-Cookie", FrameableCookie(v, CanSetCrossSiteCookies(ctx)));
        }
    }

    /// <summary>Whether a SameSite=None; Secure cookie can be set here (https, or *.localhost in development).</summary>
    private static bool CanSetCrossSiteCookies(HttpContext ctx) =>
        ctx.Request.IsHttps || ctx.Request.Host.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The workspace is shown in an iframe of ByteBench's page (another site), where a SameSite=Lax cookie (OpenVSCode's own token cookie, which its
    /// page script reads) would be dropped. Where the browser accepts it, upstream cookies are made SameSite=None; Secure; Partitioned.
    /// </summary>
    public static string FrameableCookie(string setCookie, bool crossSiteCapable)
    {
        if (!crossSiteCapable) return setCookie;
        var parts = setCookie.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith("SameSite", StringComparison.OrdinalIgnoreCase) && !p.Equals("Secure", StringComparison.OrdinalIgnoreCase)
                        && !p.Equals("Partitioned", StringComparison.OrdinalIgnoreCase)).ToList();
        parts.AddRange(["SameSite=None", "Secure", "Partitioned"]);
        return string.Join("; ", parts);
    }

    private async Task TunnelAsync(HttpContext ctx, LiveWorkspace live, int port, string pathAndQuery, string? cookie)
    {
        var upgradeFeature = ctx.Features.Get<IHttpUpgradeFeature>();
        if (upgradeFeature is not { IsUpgradableRequest: true }) { await Plain(ctx, 400, "WebSocket upgrade is not available."); return; }
        await using var upstream = await backend.ConnectAsync(live.Endpoint, port, ctx.RequestAborted);

        var head = new StringBuilder();
        head.Append(ctx.Request.Method).Append(' ').Append(pathAndQuery).Append(" HTTP/1.1\r\n");
        head.Append("Host: ").Append(ctx.Request.Host.Value).Append("\r\n");
        foreach (var (name, values) in ctx.Request.Headers)
        {
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var v in values) head.Append(name).Append(": ").Append(Sanitize(v)).Append("\r\n");
        }
        if (cookie != null) head.Append("Cookie: ").Append(Sanitize(cookie)).Append("\r\n");
        head.Append("\r\n");
        await upstream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ctx.RequestAborted);
        await upstream.FlushAsync(ctx.RequestAborted);

        // Response head: everything up to the blank line; any bytes after it already belong to the tunnel.
        var buffer = new byte[16 * 1024];
        var filled = 0;
        var end = -1;
        while (end < 0)
        {
            if (filled == buffer.Length) { await Plain(ctx, 502, "The workspace sent an oversized response."); return; }
            var n = await upstream.ReadAsync(buffer.AsMemory(filled), ctx.RequestAborted);
            if (n == 0) { await Plain(ctx, 502, "The workspace closed the connection."); return; }
            filled += n;
            end = Find(buffer, filled);
        }
        var lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
        var status = lines[0].Split(' ', 3).Length > 1 && int.TryParse(lines[0].Split(' ', 3)[1], out var s) ? s : 502;
        if (status != 101)
        {
            // The upstream refused the upgrade (bad token, no such path): pass its status and a short note, not its body.
            await Plain(ctx, status is >= 400 and < 600 ? status : 502, "The workspace refused the connection.");
            return;
        }
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            ctx.Response.Headers.Append(line[..colon].Trim(), line[(colon + 1)..].Trim());
        }
        using var client = await upgradeFeature.UpgradeAsync();
        if (filled > end + 4) await client.WriteAsync(buffer.AsMemory(end + 4, filled - end - 4), ctx.RequestAborted);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        var up = Pump(client, upstream, live, cts.Token);
        var down = Pump(upstream, client, null, cts.Token);
        await Task.WhenAny(up, down);
        cts.Cancel();
        try { await Task.WhenAll(up, down); } catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private static async Task Pump(Stream from, Stream to, LiveWorkspace? touch, CancellationToken ct)
    {
        var buf = new byte[32 * 1024];
        while (true)
        {
            var n = await from.ReadAsync(buf, ct);
            if (n == 0) return;
            touch?.Touch(); // traffic from the browser counts as activity
            await to.WriteAsync(buf.AsMemory(0, n), ct);
            await to.FlushAsync(ct);
        }
    }

    private static int Find(byte[] buffer, int count)
    {
        for (var i = 0; i + 3 < count; i++)
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n') return i;
        return -1;
    }

    private static string Sanitize(string v) => v.Replace("\r", "").Replace("\n", "");
}

public static class WorkspaceProxyExtensions
{
    /// <summary>The workspace proxy (see <see cref="WorkspaceProxyMiddleware"/>): before authorization, rate limiting and antiforgery, which don't apply to relayed content.</summary>
    public static IApplicationBuilder UseWorkspaceProxy(this IApplicationBuilder app)
    {
        return app.UseMiddleware<WorkspaceProxyMiddleware>();
    }
}
