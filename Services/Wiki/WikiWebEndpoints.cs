using System.Net;
using System.Security.Cryptography;
using System.Text;
using ByteBench.Data;
using ByteBench.Services.Code;
using ByteBench.Services.Hosting;
using ByteBench.Services.Snippets;
using Microsoft.AspNetCore.Identity;
using Microsoft.Net.Http.Headers;

namespace ByteBench.Services.Wiki;

/// <summary>
/// Browser endpoints (cookie session) for things Blazor pages can't serve:
/// <list type="bullet">
/// <item><c>GET /wiki-files/{KEY}/{**path}</c>: a file of the wiki repository (uploaded images). PNG/JPEG/GIF/WebP whose bytes
/// match are inline; anything else is an <c>application/octet-stream</c> download. ViewProject + wiki enabled, else 404.</item>
/// <item><c>GET /snippets/{id}/raw/{fileName}?rev=</c>: a snippet file as <c>text/plain; charset=utf-8</c> with nosniff and a
/// sandboxing CSP (<c>?download=1</c> for an attachment).</item>
/// <item><c>GET /snippets/{id}/embed</c>: a chrome-less, read-only HTML view of a snippet that same-origin pages may frame.</item>
/// </list>
/// </summary>
public static class WikiWebEndpoints
{
    public static IEndpointRouteBuilder MapWikiAndSnippetDownloads(this IEndpointRouteBuilder app)
    {
        app.MapGet("/wiki-files/{key}/{**path}", async (HttpContext http, string key, string path, UserManager<User> users, ProjectAccess access, WikiService wikis) =>
        {
            if (await SignedInAsync(http, users) is not { } user) return Results.Unauthorized();
            access.ActAs(user.Id);
            var wiki = await wikis.GetWikiAsync(key);
            path = path.Trim('/');
            if (wiki is not { Enabled: true } || path.Length == 0 || path.Split('/').Any(p => p is "" or "." or "..")) return Results.NotFound();
            var bytes = await wikis.GetFileAsync(wiki, path);
            if (bytes == null) return Results.NotFound();
            var type = WikiService.ImageType(bytes);
            SafeHeaders(http, "sandbox; default-src 'none'; img-src 'self'; style-src 'unsafe-inline'");
            var disposition = new ContentDispositionHeaderValue(type != null ? "inline" : "attachment");
            disposition.SetHttpFileName(Path.GetFileName(path));
            http.Response.Headers.ContentDisposition = disposition.ToString();
            var etag = new EntityTagHeaderValue("\"" + Convert.ToHexStringLower(SHA256.HashData(bytes))[..32] + "\"");
            return Results.File(bytes, type ?? "application/octet-stream", entityTag: etag);
        }).ExcludeFromDescription();

        app.MapGet("/snippets/{id:int}/raw/{fileName}", async (HttpContext http, int id, string fileName, int? rev, bool? download,
            UserManager<User> users, ProjectAccess access, SnippetService snippets) =>
        {
            if (await SignedInAsync(http, users) is not { } user) return Results.Unauthorized();
            access.ActAs(user.Id);
            var file = await snippets.GetFileAsync(id, fileName, rev);
            return file == null ? Results.NotFound() : Raw(http, file, download == true);
        }).ExcludeFromDescription();

        app.MapGet("/snippets/{id:int}/embed", async (HttpContext http, int id, UserManager<User> users, ProjectAccess access, SnippetService snippets) =>
        {
            if (await SignedInAsync(http, users) is not { } user) return Results.Unauthorized();
            access.ActAs(user.Id);
            var view = await snippets.GetAsync(id);
            if (view == null) return Results.NotFound();
            http.Items[ProductionSetup.AllowSameOriginFramingKey] = true;
            SafeHeaders(http, "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
                              "base-uri 'none'; form-action 'none'; frame-ancestors 'self'");
            return Results.Content(EmbedHtml(http, view), "text/html; charset=utf-8");
        }).ExcludeFromDescription();
        return app;
    }

    private static async Task<User?> SignedInAsync(HttpContext http, UserManager<User> users) =>
        http.User.Identity?.IsAuthenticated == true && await users.GetUserAsync(http.User) is { } user && !await users.IsLockedOutAsync(user)
        && user.DeactivatedAt == null ? user : null;

    private static void SafeHeaders(HttpContext http, string csp)
    {
        var h = http.Response.Headers;
        h.XContentTypeOptions = "nosniff";
        h.ContentSecurityPolicy = csp;
        h["Cross-Origin-Resource-Policy"] = "same-origin";
        h.CacheControl = "private, no-cache";
    }

    /// <summary>A snippet file (also used by the REST API): always text/plain, never sniffed or rendered.</summary>
    public static IResult Raw(HttpContext http, SnippetFile file, bool download)
    {
        SafeHeaders(http, "sandbox; default-src 'none'");
        var disposition = new ContentDispositionHeaderValue(download ? "attachment" : "inline");
        disposition.SetHttpFileName(file.FileName);
        http.Response.Headers.ContentDisposition = disposition.ToString();
        return Results.Text(file.Content, "text/plain; charset=utf-8", Encoding.UTF8);
    }

    private static string EmbedHtml(HttpContext http, SnippetView view)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var s = view.Snippet;
        var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>").Append(E(s.Title)).Append(" · ByteBench snippet</title>");
        sb.Append("<link rel=\"stylesheet\" href=\"/css/code-tools.css\">");
        sb.Append("<style>:root{color-scheme:light}body{margin:0;font:14px/1.45 system-ui,sans-serif;background:Canvas;color:CanvasText}")
          .Append(".snip-head{padding:8px 12px;border-bottom:1px solid #8884;display:flex;gap:8px;align-items:baseline}")
          .Append(".snip-head a{color:inherit;font-weight:600;text-decoration:none}.muted{opacity:.7;font-size:12px}")
          .Append(".file{border-bottom:1px solid #8884}.file-name{padding:6px 12px;font:600 12px ui-monospace,monospace;background:#8881;display:flex;justify-content:space-between}")
          .Append(".file-name a{color:inherit;opacity:.7}.code-table{border-collapse:collapse;width:100%;font:12px/1.5 ui-monospace,monospace}")
          .Append(".code-table td.ln{width:1%;padding:0 10px;text-align:right;opacity:.5;user-select:none;white-space:nowrap}")
          .Append(".code-table td.code{white-space:pre;padding:0 10px}.wrap{overflow-x:auto}</style></head><body>");
        sb.Append("<div class=\"snip-head\"><a target=\"_top\" href=\"").Append(E(baseUrl + SnippetService.Url(s.Id))).Append("\">")
          .Append(E(s.Title)).Append("</a><span class=\"muted\">by ").Append(E(s.Author.DisplayName)).Append(" · ByteBench</span></div>");
        foreach (var f in view.Revision.Files)
        {
            sb.Append("<div class=\"file\"><div class=\"file-name\"><span>").Append(E(f.FileName)).Append("</span><a target=\"_top\" href=\"")
              .Append(E(SnippetService.RawUrl(s.Id, f.FileName))).Append("\">raw</a></div><div class=\"wrap\">");
            sb.Append("<table class=\"code-table\" data-hl-lang=\"").Append(E(CodeLanguages.ForFile(f.FileName, f.Content.Length) ?? "")).Append("\">");
            var n = 0;
            foreach (var line in f.Content.TrimEnd('\n').Split('\n'))
                sb.Append("<tr><td class=\"ln\">").Append(++n).Append("</td><td class=\"code\">").Append(CodeLanguages.Line(line).Value).Append("</td></tr>");
            sb.Append("</table></div></div>");
        }
        sb.Append("<script src=\"/lib/highlightjs/highlight.min.js\"></script><script src=\"/js/code-tools.js\"></script></body></html>");
        return sb.ToString();
    }
}
