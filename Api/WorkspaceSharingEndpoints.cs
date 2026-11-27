using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Audit;
using ByteBench.Services.Workspaces;

namespace ByteBench.Api;

/// <param name="User">Username to invite (they must be able to view the repository).</param>
/// <param name="Role"><c>collaborator</c> (full IDE access) or <c>viewer</c> (read-only).</param>
/// <param name="AllowPush">Collaborators only: give this person a git credential of their own (their user name, this repository only) so they can push while invited (default false: git runs without credentials for them).</param>
public record InviteToWorkspaceRequest(string User, string Role, bool AllowPush = false);

public record WorkspaceShareDto(string User, string? DisplayName, string Role, DateTimeOffset InvitedAt, bool Connected, DateTimeOffset? LastSeen, bool AllowPush);

public record SharedWorkspaceDto(string Id, string Project, string Repository, string Ref, string Owner, string Role, string State, string Url);

public record AuditEventDto(long Id, DateTimeOffset Time, string? Actor, int? ActorId, string? Ip, string Action, string? Workspace, string? Project, JsonElement? Details);

public record AuditPageDto(int Total, int Page, int PageSize, List<AuditEventDto> Items);

public record EgressRuleDto(string Rule);

public record EgressBlockDto(DateTimeOffset LastSeen, string Host, int Port, string Method, string Reason, int Count);

/// <summary>A workspace's network policy: what it started with, what the repository's <c>egress:</c> was not allowed to widen, and what was blocked.</summary>
public record WorkspaceEgressDto(string Mode, string AdminMode, bool RepositoryNarrowed, List<string> Allow, List<string> Warnings, List<EgressBlockDto> Blocked, long Allowed, long BlockedTotal);

/// <summary>Sharing, audit history and network policy of workspaces. Everything here is the workspace owner's (and, for the audit, site admins').</summary>
public static class WorkspaceSharingEndpoints
{
    public static void MapWorkspaceSharingEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/workspaces").WithTags("Workspaces");
        g.MapGet("/shared", SharedWithMe).WithName("ListSharedWorkspaces").WithSummary("Workspaces others invited you to")
            .WithDescription("Running workspaces where you are a collaborator (full IDE) or viewer (read-only). Open them at the `url`.");
        g.MapGet("/{id}/shares", ListShares).WithName("ListWorkspaceShares").WithSummary("Who is invited to your workspace")
            .WithDescription("Owner only. `connected` is true for someone seen in the last minute and a half or with an IDE connection open.");
        g.MapPost("/{id}/shares", Invite).WithName("InviteToWorkspace").WithSummary("Invite someone to your running workspace")
            .WithDescription("Owner only; the workspace must be running. The person must be able to view the repository (and, unless an admin allowed more, be a project member). `collaborator` gets the full IDE through the proxy with their own ticket and acts as the container's user, including the git push token; `viewer` can only read files and terminal output through ByteBench and never gets an IDE session. Inviting again changes the role. Audited (workspace.shared); the invitee is notified.")
            .WithJsonBody<InviteToWorkspaceRequest>();
        g.MapDelete("/{id}/shares/{user}", Revoke).WithName("RevokeWorkspaceShare").WithSummary("Remove one person")
            .WithDescription("Owner only. Immediate: their sessions end and open IDE connections are cut. Audited (workspace.share_ended).");
        g.MapDelete("/{id}/shares", EndSharing).WithName("EndWorkspaceSharing").WithSummary("End all sharing of a workspace")
            .WithDescription("Owner only. Sharing also ends by itself when the workspace stops.");
        g.MapPost("/{id}/leave", Leave).WithName("LeaveWorkspace").WithSummary("Leave a workspace you were invited to");
        g.MapGet("/{id}/audit", History).WithName("GetWorkspaceAudit").WithSummary("A workspace's audit history")
            .WithDescription("Owner and site admins. Newest first. Query: `action` (prefix such as `workspace.secret`), `q` (text), `actor`, `from`/`to` (yyyy-MM-dd UTC), `page`, `size`. CSV: GET /workspaces/{id}/audit/export.csv. Secrets appear by name only, never by value.");
        g.MapGet("/{id}/egress", Egress).WithName("GetWorkspaceEgress").WithSummary("A workspace's network policy and blocked requests")
            .WithDescription("Owner only. `allow` is the final allow-list when `mode` is `allowlist`; `warnings` lists what the repository's `egress:` asked for beyond the server's policy (a repository can only narrow it). `blocked` is the in-memory log of refused requests (distinct host and port with counts) for a running workspace; each first refusal is also audited (workspace.egress_blocked).");
        api.MapGet("/admin/workspaces/audit", AdminAudit).WithTags("Workspaces").WithName("SearchWorkspaceAudit").WithSummary("Search every workspace's audit events")
            .WithDescription("Site admins only. Filters: `workspace` (public id), `actor`, `action` (prefix), `q`, `from`/`to`, `page`, `size`. CSV: GET /admin/workspaces/audit/export.csv.");
    }

    private static WorkspaceShareRole ParseRole(string? role) => role?.Trim().ToLowerInvariant() switch
    {
        "collaborator" => WorkspaceShareRole.Collaborator,
        "viewer" => WorkspaceShareRole.Viewer,
        _ => throw ApiException.BadRequest("role must be 'collaborator' or 'viewer'."),
    };

    private static WorkspaceShareDto ToDto(ShareInfo s) => new(s.UserName, s.DisplayName, s.Role.ToString().ToLowerInvariant(), Dto.Utc(s.CreatedAt), s.Connected, Dto.Utc(s.LastSeen), s.AllowPush);

    private static async Task<List<SharedWorkspaceDto>> SharedWithMe(ApiCaller caller, WorkspaceSharingService sharing) =>
        (await sharing.SharedWithMeAsync(caller.Id)).Select(w => new SharedWorkspaceDto(w.Workspace.PublicId, w.ProjectKey, $"{w.ProjectKey}/{w.RepoSlug}", w.Workspace.Ref, w.OwnerName,
            w.Role.ToString().ToLowerInvariant(), w.Workspace.State.ToString(), $"/workspaces/shared/{w.Workspace.PublicId}")).ToList();

    private static async Task<List<WorkspaceShareDto>> ListShares(ApiCaller caller, WorkspaceSharingService sharing, string id)
    {
        try { return (await sharing.ListAsync(id, caller.Id)).Select(ToDto).ToList(); }
        catch (InvalidOperationException) { throw ApiException.NotFound("Workspace not found."); }
    }

    private static async Task<IResult> Invite(HttpContext http, ApiCaller caller, WorkspaceSharingService sharing, WorkspaceService workspaces, string id)
    {
        if (await workspaces.GetMineAsync(id, caller.Id) == null) throw ApiException.NotFound("Workspace not found.");
        var body = await ApiResults.ReadBodyAsync<InviteToWorkspaceRequest>(http.Request);
        var share = await sharing.InviteAsync(id, caller.Id, body.User, ParseRole(body.Role), body.AllowPush);
        return TypedResults.Json(ToDto(share), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> Revoke(ApiCaller caller, WorkspaceSharingService sharing, WorkspaceService workspaces, string id, string user)
    {
        if (await workspaces.GetMineAsync(id, caller.Id) == null) throw ApiException.NotFound("Workspace not found.");
        var shares = await sharing.ListAsync(id, caller.Id);
        var target = shares.FirstOrDefault(s => s.UserName.Equals(user, StringComparison.OrdinalIgnoreCase)) ?? throw ApiException.NotFound($"{user} isn't invited to this workspace.");
        await sharing.RevokeAsync(id, caller.Id, target.UserId);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> EndSharing(ApiCaller caller, WorkspaceSharingService sharing, WorkspaceService workspaces, string id)
    {
        if (await workspaces.GetMineAsync(id, caller.Id) == null) throw ApiException.NotFound("Workspace not found.");
        await sharing.EndSharingAsync(id, caller.Id);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> Leave(ApiCaller caller, WorkspaceSharingService sharing, string id)
    {
        try { await sharing.LeaveAsync(id, caller.Id); }
        catch (InvalidOperationException) { throw ApiException.NotFound("Workspace not found."); }
        return TypedResults.NoContent();
    }

    private static AuditPageDto ToDto(AuditPage page, int pageNo, int size) => new(page.Total, pageNo, size, page.Events.Select(e => new AuditEventDto(e.Id, Dto.Utc(e.CreatedAt), e.ActorName, e.ActorId,
        e.IpAddress, e.Action, e.TargetType == "workspace" ? e.TargetId : null, e.ProjectId is { } p ? page.ProjectKeys.GetValueOrDefault(p) : null,
        e.Details == null ? null : JsonDocument.Parse(e.Details).RootElement.Clone())).ToList());

    private static async Task<AuditPageDto> History(ApiCaller caller, WorkspaceAuditService audit, string id, string? action, string? q, string? actor, DateOnly? from, DateOnly? to, int? page, int? size)
    {
        var query = WorkspaceAuditEndpoints.Query(null, actor, action, q, from, to);
        query.Page = Math.Max(page ?? 1, 1);
        query.PageSize = Math.Clamp(size ?? 50, 1, 200);
        try { return ToDto(await audit.HistoryAsync(id, caller.Id, query), query.Page, query.PageSize); }
        catch (ForbiddenException) { throw; }
        catch (InvalidOperationException) { throw ApiException.NotFound("Workspace not found."); }
    }

    private static async Task<AuditPageDto> AdminAudit(ApiCaller caller, WorkspaceAuditService audit, string? workspace, string? actor, string? action, string? q, DateOnly? from, DateOnly? to, int? page, int? size)
    {
        var query = WorkspaceAuditEndpoints.Query(workspace, actor, action, q, from, to);
        query.Page = Math.Max(page ?? 1, 1);
        query.PageSize = Math.Clamp(size ?? 50, 1, 200);
        return ToDto(await audit.SearchAsync(caller.Id, query), query.Page, query.PageSize);
    }

    private static async Task<WorkspaceEgressDto> Egress(ApiCaller caller, WorkspaceService workspaces, WorkspaceEgressReader egress, string id)
    {
        var w = await workspaces.GetMineAsync(id, caller.Id) ?? throw ApiException.NotFound("Workspace not found.");
        var view = await egress.GetAsync(w.Workspace);
        return new WorkspaceEgressDto(view.Mode, view.AdminMode, view.RepoNarrowed, view.Rules, view.Warnings,
            view.Blocked.Select(b => new EgressBlockDto(Dto.Utc(b.At), b.Host, b.Port, b.Method, b.Reason, b.Count)).ToList(), view.Allowed, view.BlockedTotal);
    }
}
