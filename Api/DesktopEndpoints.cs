using ByteBench.Services;
using ByteBench.Services.Hosting.Backup;
using ByteBench.Services.Workspaces;
using Microsoft.Extensions.Options;

namespace ByteBench.Api;

/// <summary>Public, non-sensitive facts about this server, for the desktop app's compatibility check.</summary>
public record ServerInfoDto(string Name, string Version, int ApiVersion, List<string> Capabilities, string? WorkspaceBaseDomain);

/// <param name="Url">A server-relative path (starts with "/") to open in the web UI.</param>
public record NotificationDto(int Id, string Kind, string Title, string Url, bool IsRead, DateTimeOffset CreatedAt, string? Actor);

public record NotificationListDto(int UnreadCount, List<NotificationDto> Items);

/// <summary>
/// Endpoints the ByteBench desktop app uses: <c>GET /api/v1/server-info</c> (anonymous) and the caller's own notifications
/// (<c>GET /api/v1/notifications</c>, <c>POST /api/v1/notifications/{id}/read</c>).
/// </summary>
public static class DesktopEndpoints
{
    /// <summary>Bump when the desktop app's required endpoints change incompatibly. Capabilities name optional features.</summary>
    public const int ApiVersion = 1;
    public static readonly string[] Capabilities = ["notifications", "workspace-ports"];

    public static IEndpointRouteBuilder MapServerInfo(this IEndpointRouteBuilder app)
    {
        // Unauthenticated on purpose: the desktop app asks before anyone has signed in. Nothing here is secret
        // (the workspace base domain is public DNS).
        app.MapGet("/api/v1/server-info", (IOptions<WorkspaceOptions> ws, HttpContext http) =>
        {
            ApiResults.PrepareResponse(http);
            var baseDomain = string.IsNullOrWhiteSpace(ws.Value.BaseDomain) ? null : ws.Value.BaseDomain.Trim().ToLowerInvariant();
            return TypedResults.Json(new ServerInfoDto("ByteBench", BackupManifest.CurrentAppVersion(), ApiVersion, [.. Capabilities], baseDomain), Dto.Json);
        }).AllowAnonymous().WithName("GetServerInfo").WithSummary("Name, version and capabilities (anonymous)").WithTags("Server");
        return app;
    }

    public static void MapNotificationApiEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/notifications").WithTags("Notifications");
        g.MapGet("", List).WithName("ListNotifications").WithSummary("Your notifications")
            .WithDescription("Newest first. Filters: unread=true, afterId=N (only notifications with a higher id; for polling), limit (1-100, default 30). Always includes unreadCount. Only your own.");
        g.MapPost("/{id:int}/read", MarkRead).WithName("MarkNotificationRead").WithSummary("Mark one of your notifications read");
    }

    private static async Task<NotificationListDto> List(ApiCaller caller, NotificationService notes, bool? unread, int? afterId, int? limit)
    {
        var take = Math.Clamp(limit ?? 30, 1, 100);
        var page = await notes.GetPageAsync(caller.Id, unread == true, 1, take);
        var items = page.Items.Where(n => afterId is not { } a || n.Id > a)
            .Select(n => new NotificationDto(n.Id, n.Kind.ToString(), n.Title, SafePath(n.Url), n.IsRead, Dto.Utc(n.CreatedAt), n.Actor?.DisplayName)).ToList();
        return new NotificationListDto(await notes.GetUnreadCountAsync(caller.Id), items);
    }

    private static async Task<IResult> MarkRead(ApiCaller caller, NotificationService notes, int id)
    {
        await notes.MarkReadAsync(caller.Id, id);
        return TypedResults.NoContent();
    }

    /// <summary>Notification URLs are written by the server as paths; anything else is not handed to the desktop app.</summary>
    private static string SafePath(string url) => url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : "/notifications";
}
