using ByteBench.Data;
using ByteBench.Services.Workspaces;

namespace ByteBench.Api;

/// <param name="Id">The public id (unguessable); use it in every other call and in /workspaces/{id}.</param>
public record WorkspaceDto(string Id, string Project, string Repository, string Ref, string? CommitSha, string? Source, string State, bool CanPush,
    string EgressMode, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset LastActivityAt, DateTimeOffset ExpiresAt, DateTimeOffset? StoppedAt,
    string? StopReason, string? Error, string Url);

/// <summary>Start body: <c>ref</c> defaults to the default branch; <c>source</c> is free text such as <c>pr:12</c>.</summary>
public record StartWorkspaceRequest(string? Ref = null, string? Source = null);

/// <summary>
/// Workspaces over the API: list, start, read, stop, restart. A workspace is private to its owner; asking for anyone else's gives 404. There is no
/// endpoint that returns an IDE link or credentials: the IDE is opened from the workspace page (a single-use link is minted for the signed-in browser).
/// </summary>
public static class WorkspaceEndpoints
{
    public static void MapWorkspaceEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/workspaces").WithTags("Workspaces");
        g.MapGet("", List).WithName("ListWorkspaces").WithSummary("Your workspaces")
            .WithDescription("Running ones first, then the most recent finished ones. Only your own.");
        g.MapGet("/{id}", Get).WithName("GetWorkspace").WithSummary("One of your workspaces").WithDescription("404 for anyone else's.");
        g.MapPost("/{id}/stop", Stop).WithName("StopWorkspace").WithSummary("Stop and delete a workspace")
            .WithDescription("Removes the container and everything in it (push first) and revokes its git token. Audited (workspace.stopped).");
        g.MapPost("/{id}/restart", Restart).WithName("RestartWorkspace").WithSummary("Restart a running workspace")
            .WithDescription("Restarts the container (files stay, running processes end). 422 if it isn't running.");
        api.MapPost("/projects/{key}/repos/{slug}/workspaces", Start).WithTags("Workspaces").WithName("StartWorkspace")
            .WithSummary("Start a workspace on a branch, tag or commit")
            .WithDescription("Needs ViewProject. 202: the container comes up in the background; poll the workspace until its state is Running (or Failed). 422 when workspaces are off, Docker or the image isn't ready, or a limit is reached. Audited.")
            .WithJsonBody<StartWorkspaceRequest>(optional: true);
    }

    private static WorkspaceDto ToDto(WorkspaceInfo v) => new(v.Workspace.PublicId, v.ProjectKey, v.RepoSlug, v.Workspace.Ref, v.Workspace.CommitSha, v.Workspace.Source,
        v.Workspace.State.ToString(), v.Workspace.CanPush, v.Workspace.EgressMode, Dto.Utc(v.Workspace.CreatedAt), Dto.Utc(v.Workspace.StartedAt), Dto.Utc(v.Workspace.LastActivityAt),
        Dto.Utc(v.Workspace.ExpiresAt), Dto.Utc(v.Workspace.StoppedAt), v.Workspace.StopReason, v.Workspace.Error, $"/workspaces/{v.Workspace.PublicId}");

    private static async Task<List<WorkspaceDto>> List(ApiCaller caller, WorkspaceService workspaces) =>
        (await workspaces.ListMineAsync(caller.Id)).Select(ToDto).ToList();

    private static async Task<WorkspaceDto> Get(ApiCaller caller, WorkspaceService workspaces, string id) =>
        ToDto(await workspaces.GetMineAsync(id, caller.Id) ?? throw ApiException.NotFound("Workspace not found."));

    private static async Task<IResult> Start(HttpContext http, ApiCaller caller, WorkspaceService workspaces, ByteBench.Services.RepositoryService repos, string key, string slug)
    {
        // A repository the caller can't see is a 404 whatever state Docker is in.
        if (await repos.GetRepositoryAsync(key, slug) == null) throw ApiException.NotFound($"Repository '{key.ToUpperInvariant()}/{slug}' not found.");
        var body = http.Request.ContentLength is > 0 ? await ApiResults.ReadBodyAsync<StartWorkspaceRequest>(http.Request) : new StartWorkspaceRequest();
        var ws = await workspaces.StartAsync(key, slug, body.Ref, body.Source, caller.Id);
        var view = await workspaces.GetMineAsync(ws.PublicId, caller.Id) ?? throw ApiException.NotFound("Workspace not found.");
        return TypedResults.Json(ToDto(view), statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<WorkspaceDto> Stop(ApiCaller caller, WorkspaceService workspaces, string id)
    {
        if (await workspaces.GetMineAsync(id, caller.Id) == null) throw ApiException.NotFound("Workspace not found.");
        await workspaces.StopAsync(id, caller.Id);
        return ToDto(await workspaces.GetMineAsync(id, caller.Id) ?? throw ApiException.NotFound("Workspace not found."));
    }

    private static async Task<WorkspaceDto> Restart(ApiCaller caller, WorkspaceService workspaces, string id)
    {
        if (await workspaces.GetMineAsync(id, caller.Id) == null) throw ApiException.NotFound("Workspace not found.");
        await workspaces.RestartAsync(id, caller.Id);
        return ToDto(await workspaces.GetMineAsync(id, caller.Id) ?? throw ApiException.NotFound("Workspace not found."));
    }
}

