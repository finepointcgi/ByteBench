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
        g.MapPost("/{id}/resume", Resume).WithName("ResumeWorkspace").WithSummary("Resume a stopped workspace")
            .WithDescription("Starts a stopped workspace again on its saved volumes (checkout, installed dependencies and caches are kept; setup re-runs only if it changed). 202; poll it. 422 if its files are gone, or at a limit.");
        g.MapDelete("/{id}", Delete).WithName("DeleteWorkspace").WithSummary("Delete a workspace and its volumes")
            .WithDescription("Stops it if it runs and removes its container, its project and cache volumes and the workspace itself. Push first. Audited (workspace.deleted).");
        g.MapGet("/{id}/environment", Environment).WithName("GetWorkspaceEnvironment").WithSummary("The workspace's environment")
            .WithDescription("What the repository's .bytebench/workspace.yml (or devcontainer.json) asked for and what happened: problems with the file, notes (ignored devcontainer features…), warnings (submodules that couldn't be fetched…), setup state, tasks, ports, services and the start-up log.");
        g.MapGet("/{id}/tasks", ListTasks).WithName("ListWorkspaceTasks").WithSummary("The workspace's tasks and their state")
            .WithDescription("Tasks from the environment file with their latest run state (Idle, Running, Succeeded, Failed, Stopped).");
        g.MapPost("/{id}/tasks/{name}/run", RunTask).WithName("RunWorkspaceTask").WithSummary("Run a task")
            .WithDescription("Starts the named task in the workspace's container as the developer's user. A task that is already running is left alone. 422 while setup is running, or when the workspace isn't running. Audited (workspace.task_started).");
        g.MapPost("/{id}/tasks/{name}/stop", StopTask).WithName("StopWorkspaceTask").WithSummary("Stop a task")
            .WithDescription("Ends the task's whole process tree in the container. Audited (workspace.task_stopped).");
        g.MapGet("/{id}/tasks/{name}/logs", TaskLogs).WithName("GetWorkspaceTaskLogs").WithSummary("A task's output")
            .WithDescription("Lines after ?since= (0: everything kept, at most the newest 500) and the position to pass next. Output is capped; values of the workspace's secrets are masked as ***.");
        g.MapGet("/{id}/setup", SetupLog).WithName("GetWorkspaceSetup").WithSummary("The setup commands' state and output")
            .WithDescription("State (None, Pending, Running, Succeeded, Failed, UpToDate), a message and the output after ?since=.");
        g.MapPost("/{id}/setup/rerun", RerunSetup).WithName("RerunWorkspaceSetup").WithSummary("Run the setup commands again")
            .WithDescription("Forgets that setup succeeded and runs it now (for example after a failure). 202.");
        api.MapPost("/projects/{key}/repos/{slug}/workspaces", Start).WithTags("Workspaces").WithName("StartWorkspace")
            .WithSummary("Start a workspace on a branch, tag or commit")
            .WithDescription("Needs ViewProject. 202: the container comes up in the background; poll the workspace until its state is Running (or Failed). 422 when workspaces are off, Docker or the image isn't ready, or a limit is reached. Audited.")
            .WithJsonBody<StartWorkspaceRequest>(optional: true);
        api.MapWorkspacePolicyEndpoints(); // limits, idle auto-stop, prebuilds (WorkspacePolicyEndpoints.cs)
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

    private static async Task RequireMine(WorkspaceService workspaces, ApiCaller caller, string id)
    {
        if (await workspaces.GetMineAsync(id, caller.Id) == null) throw ApiException.NotFound("Workspace not found.");
    }

    private static async Task<IResult> Resume(ApiCaller caller, WorkspaceService workspaces, string id)
    {
        await RequireMine(workspaces, caller, id);
        await workspaces.ResumeAsync(id, caller.Id);
        return TypedResults.Json(ToDto(await workspaces.GetMineAsync(id, caller.Id) ?? throw ApiException.NotFound("Workspace not found.")), statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> Delete(ApiCaller caller, WorkspaceService workspaces, string id)
    {
        await RequireMine(workspaces, caller, id);
        await workspaces.DeleteAsync(id, caller.Id);
        return TypedResults.NoContent();
    }

    private static async Task<WorkspaceService.WorkspaceEnvironmentView> Environment(ApiCaller caller, WorkspaceService workspaces, string id)
    {
        await RequireMine(workspaces, caller, id);
        return await workspaces.GetEnvironmentAsync(id, caller.Id);
    }

    private static async Task<List<TaskInfo>> ListTasks(ApiCaller caller, WorkspaceService workspaces, string id)
    {
        await RequireMine(workspaces, caller, id);
        return await workspaces.ListTasksAsync(id, caller.Id);
    }

    private static async Task<IResult> RunTask(ApiCaller caller, WorkspaceService workspaces, string id, string name)
    {
        await RequireMine(workspaces, caller, id);
        return TypedResults.Json(await workspaces.RunTaskAsync(id, caller.Id, name), statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<TaskInfo> StopTask(ApiCaller caller, WorkspaceService workspaces, string id, string name)
    {
        await RequireMine(workspaces, caller, id);
        return await workspaces.StopTaskAsync(id, caller.Id, name);
    }

    public record TaskLogDto(TaskInfo Task, IReadOnlyList<string> Lines, long Next);

    private static async Task<TaskLogDto> TaskLogs(ApiCaller caller, WorkspaceService workspaces, string id, string name, long? since)
    {
        await RequireMine(workspaces, caller, id);
        var (lines, next, task) = await workspaces.TaskLogAsync(id, caller.Id, name, since ?? 0);
        return new TaskLogDto(task, lines, next);
    }

    public record SetupLogDto(string State, string? Message, IReadOnlyList<string> Lines, long Next);

    private static async Task<SetupLogDto> SetupLog(ApiCaller caller, WorkspaceService workspaces, string id, long? since)
    {
        await RequireMine(workspaces, caller, id);
        var (lines, next, state, message) = await workspaces.SetupLogAsync(id, caller.Id, since ?? 0);
        return new SetupLogDto(state, message, lines, next);
    }

    private static async Task<IResult> RerunSetup(ApiCaller caller, WorkspaceService workspaces, string id)
    {
        await RequireMine(workspaces, caller, id);
        await workspaces.RerunSetupAsync(id, caller.Id);
        return TypedResults.Json(new { state = "Running" }, statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<WorkspaceDto> Restart(ApiCaller caller, WorkspaceService workspaces, string id)
    {
        if (await workspaces.GetMineAsync(id, caller.Id) == null) throw ApiException.NotFound("Workspace not found.");
        await workspaces.RestartAsync(id, caller.Id);
        return ToDto(await workspaces.GetMineAsync(id, caller.Id) ?? throw ApiException.NotFound("Workspace not found."));
    }
}

