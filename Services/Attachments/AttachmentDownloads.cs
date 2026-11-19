using ByteBench.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Net.Http.Headers;

namespace ByteBench.Services.Attachments;

/// <summary>
/// <c>GET /attachments/{id}/{fileName}</c> for signed-in browser sessions (the REST API has its own, token-friendly route).
/// Requires ViewProject on the attachment's project; anything else is a 404 so ids don't reveal private projects.
/// </summary>
public static class AttachmentDownloads
{
    public static IEndpointRouteBuilder MapAttachmentDownloads(this IEndpointRouteBuilder app)
    {
        app.MapGet("/attachments/{id:int}/{fileName}", async (HttpContext http, int id, UserManager<User> users, ProjectAccess access, AttachmentService attachments) =>
        {
            if (http.User.Identity?.IsAuthenticated != true || await users.GetUserAsync(http.User) is not { } user || await users.IsLockedOutAsync(user))
                return Results.Unauthorized();
            access.ActAs(user.Id);
            var a = await attachments.FindAsync(id);
            return a == null ? Results.NotFound() : Serve(http, a, attachments.PathFor(a));
        }).ExcludeFromDescription();
        return app;
    }

    /// <summary>
    /// Sends an attachment safely: <c>nosniff</c>, a sandboxing CSP, and inline only for png/jpeg/gif/webp whose bytes
    /// matched at upload. Everything else (SVG, HTML, PDF, …) is <c>application/octet-stream</c> with
    /// <c>Content-Disposition: attachment</c>.
    /// </summary>
    public static IResult Serve(HttpContext http, Attachment a, string path)
    {
        if (!File.Exists(path)) return Results.NotFound();
        var h = http.Response.Headers;
        h.XContentTypeOptions = "nosniff";
        h.ContentSecurityPolicy = "sandbox; default-src 'none'; img-src 'self'; style-src 'unsafe-inline'";
        h["Cross-Origin-Resource-Policy"] = "same-origin";
        h.CacheControl = "private, no-cache";

        var inline = AttachmentStorage.IsInlineImage(a.ContentType);
        var disposition = new ContentDispositionHeaderValue(inline ? "inline" : "attachment");
        disposition.SetHttpFileName(a.FileName);
        h.ContentDisposition = disposition.ToString();

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Results.File(stream, inline ? a.ContentType : "application/octet-stream",
            lastModified: new DateTimeOffset(DateTime.SpecifyKind(a.CreatedAt, DateTimeKind.Utc)),
            entityTag: new EntityTagHeaderValue($"\"{a.Sha256}\""), enableRangeProcessing: true);
    }
}
