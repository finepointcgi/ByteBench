using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Workspaces;

namespace ByteBench.Api;

public record AiModeDto(AiEditMode Mode, AiEditMode? WorkspaceMode, AiEditMode? DefaultMode);

/// <summary><c>mode</c>: reviewFirst or applyDirectly; null clears the choice. <c>scope</c>: <c>workspace</c> (default) or <c>default</c> (your default for every workspace).</summary>
public record AiModeRequest(AiEditMode? Mode = null, string? Scope = null);

public record AiHunkDto(int Index, int OldStart, int NewStart, IReadOnlyList<string> Removed, IReadOnlyList<string> Added, IReadOnlyList<string> ContextBefore, IReadOnlyList<string> ContextAfter);

public record AiChangeFileDto(int Id, string Path, AiChangeKind Kind, AiChangeDecision Decision, string? Note, int AddedLines, int RemovedLines,
    IReadOnlyList<AiHunkDto> Hunks, IReadOnlyList<int> AcceptedHunks);

public record AiChangeSetDto(int Id, string Prompt, string? Summary, AiChangeSetState State, DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt, IReadOnlyList<AiChangeFileDto> Files);

/// <summary>Accept body: <c>hunks</c> omitted accepts the whole file; a list applies only those hunks (an empty list rejects the file).</summary>
public record AiAcceptRequest(IReadOnlyList<int>? Hunks = null);

public record AiDecisionDto(AiChangeSetDto ChangeSet, int Applied, int Rejected, int Conflicts);

/// <summary>Start body: <c>repository</c> is the repository slug (default: the project's oldest repository that isn't archived).</summary>
public record StartAiRunRequest(string? Repository = null);

public record AiRunDto(string Id, string Issue, string Project, string Repository, string Status, string? StopReason, string? Branch, string? BaseBranch, string Trigger,
    string RequestedBy, int? PullRequest, int Steps, int ToolCalls, long InputTokens, long OutputTokens, decimal CostUsd, string? Summary, string? Error,
    bool CancelRequested, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string Url);

public record AiRunEventDto(int Id, DateTimeOffset At, string Kind, string Message);

/// <summary>
/// Review mode for the workspace assistant (the setting, pending change sets, accept and reject per file, hunk or all) and task-driven AI runs on issues
/// (start, list, read, log, cancel). Workspaces are private to their owner: anyone else's gives 404. Runs follow the project's permissions: reading needs
/// ViewProject, starting needs EditIssues, cancelling needs EditIssues or being the requester.
/// </summary>
public static class AiWorkspaceEndpoints
{
    public static void MapAiWorkspaceEndpoints(this RouteGroupBuilder api)
    {
        var w = api.MapGroup("/workspaces/{id}/ai").WithTags("Workspaces");
        w.MapGet("/settings", GetMode).WithName("GetWorkspaceAiMode").WithSummary("How the AI's file edits reach this workspace")
            .WithDescription("reviewFirst (default): edits become a pending change set you accept or reject. applyDirectly: edits are written at once. Shows the effective mode, this workspace's choice and your default.");
        w.MapPut("/settings", SetMode).WithName("SetWorkspaceAiMode").WithSummary("Choose review first or apply directly")
            .WithDescription("For this workspace (scope workspace) or as your default (scope default). A null mode clears the choice. Audited.").WithJsonBody<AiModeRequest>();
        w.MapGet("/changes", ListChanges).WithName("ListWorkspaceAiChanges").WithSummary("The AI's change sets for this workspace, newest first")
            .WithDescription("Each file carries its hunks (removed and added lines with context). Query: pending=true for unresolved ones only.");
        w.MapGet("/changes/{changeSetId:int}", GetChange).WithName("GetWorkspaceAiChange").WithSummary("One change set");
        w.MapPost("/changes/{changeSetId:int}/files/{fileId:int}/accept", AcceptFile).WithName("AcceptWorkspaceAiFile").WithSummary("Accept a file (or some of its hunks)")
            .WithDescription("Writes the accepted text into the workspace, only if the file still holds what the proposal was made against (otherwise the file is marked as a conflict and nothing is written). 422 if the workspace isn't running.")
            .WithJsonBody<AiAcceptRequest>(optional: true);
        w.MapPost("/changes/{changeSetId:int}/files/{fileId:int}/reject", RejectFile).WithName("RejectWorkspaceAiFile").WithSummary("Reject a file");
        w.MapPost("/changes/{changeSetId:int}/accept-all", AcceptAll).WithName("AcceptAllWorkspaceAiChanges").WithSummary("Accept every pending file of the change set");
        w.MapPost("/changes/{changeSetId:int}/reject-all", RejectAll).WithName("RejectAllWorkspaceAiChanges").WithSummary("Reject every pending file of the change set");

        api.MapPost("/projects/{key}/issues/{number:int}/ai-runs", StartRun).WithTags("AI runs").WithName("StartAiRun").WithSummary("Let the AI work on an issue")
            .WithDescription("Needs EditIssues. The AI user starts its own workspace on a branch named from the issue, works the issue, commits, pushes, opens a pull request that fixes it, comments on the issue and stops the workspace. 202 with the queued run (or the run already in flight, 200). 422 when runs are off, the AI user isn't a Developer in the project, the issue is closed, or a limit is reached.")
            .WithJsonBody<StartAiRunRequest>(optional: true).Produces<AiRunDto>(StatusCodes.Status202Accepted);
        api.MapGet("/projects/{key}/issues/{number:int}/ai-runs", ListIssueRuns).WithTags("AI runs").WithName("ListIssueAiRuns").WithSummary("An issue's AI runs, newest first");
        api.MapGet("/ai-runs", ListRuns).WithTags("AI runs").WithName("ListAiRuns").WithSummary("AI runs in the projects you can see, newest first")
            .WithDescription("Query: project (key), take (max 200).");
        api.MapGet("/ai-runs/{id}", GetRun).WithTags("AI runs").WithName("GetAiRun").WithSummary("One AI run")
            .WithDescription("status: queued, starting, running, succeeded, failed or cancelled. stopReason says why a run that didn't simply succeed ended (step limit, token budget, cost budget, time limit, cancelled, error…).");
        api.MapGet("/ai-runs/{id}/events", GetRunEvents).WithTags("AI runs").WithName("GetAiRunEvents").WithSummary("A run's log")
            .WithDescription("Status changes, each tool the AI used and errors; never file contents or command output. Query: after (an event id) to read only newer ones.");
        api.MapPost("/ai-runs/{id}/cancel", CancelRun).WithTags("AI runs").WithName("CancelAiRun").WithSummary("Cancel a queued or running AI run")
            .WithDescription("A queued run is cancelled at once; a running one stops at its next step, nothing is pushed, and its workspace is stopped. 422 if it already finished.");
    }

    // ---------- review mode ----------

    private static async Task RequireMineAsync(WorkspaceService workspaces, ApiCaller caller, string id) =>
        _ = await workspaces.GetMineAsync(id, caller.Id) ?? throw ApiException.NotFound("Workspace not found.");

    private static AiChangeSetDto ToDto(AiChangeSetView s) => new(s.Id, s.Prompt, s.Summary, s.State, Dto.Utc(s.CreatedAt), Dto.Utc(s.ResolvedAt),
        s.Files.Select(f => new AiChangeFileDto(f.Id, f.Path, f.Kind, f.Decision, f.Note, f.AddedLines, f.RemovedLines,
            f.Hunks.Select(h => new AiHunkDto(h.Index, h.OldStart, h.NewStart, h.Removed, h.Added, h.ContextBefore, h.ContextAfter)).ToList(), f.AcceptedHunks)).ToList());

    private static AiDecisionDto ToDto(AiDecisionResult r) => new(ToDto(r.ChangeSet), r.Applied, r.Rejected, r.Conflicts);

    private static AiModeDto ToDto(AiModeInfo m) => new(m.Effective, m.WorkspaceMode, m.DefaultMode);

    private static async Task<AiModeDto> GetMode(ApiCaller caller, WorkspaceService workspaces, AiChangeService changes, string id)
    {
        await RequireMineAsync(workspaces, caller, id);
        return ToDto(await changes.GetModeInfoAsync(id, caller.Id));
    }

    private static async Task<AiModeDto> SetMode(HttpContext http, ApiCaller caller, WorkspaceService workspaces, AiChangeService changes, string id)
    {
        await RequireMineAsync(workspaces, caller, id);
        var body = await ApiResults.ReadBodyAsync<AiModeRequest>(http.Request);
        var scope = (body.Scope ?? "workspace").ToLowerInvariant();
        if (scope is not ("workspace" or "default")) throw ApiException.Unprocessable("scope must be 'workspace' or 'default'.");
        return ToDto(await changes.SetModeAsync(id, caller.Id, body.Mode, scope == "default"));
    }

    private static async Task<List<AiChangeSetDto>> ListChanges(ApiCaller caller, WorkspaceService workspaces, AiChangeService changes, string id, bool pending = false)
    {
        await RequireMineAsync(workspaces, caller, id);
        return (await changes.ListAsync(id, caller.Id, pending)).Select(ToDto).ToList();
    }

    private static async Task<AiChangeSetDto> GetChange(ApiCaller caller, WorkspaceService workspaces, AiChangeService changes, string id, int changeSetId)
    {
        await RequireMineAsync(workspaces, caller, id);
        try { return ToDto(await changes.GetAsync(id, caller.Id, changeSetId)); }
        catch (InvalidOperationException) { throw ApiException.NotFound("Change set not found."); }
    }

    private static async Task<AiDecisionDto> AcceptFile(HttpContext http, ApiCaller caller, WorkspaceService workspaces, AiChangeService changes, string id, int changeSetId, int fileId)
    {
        await RequireMineAsync(workspaces, caller, id);
        var body = http.Request.ContentLength is > 0 ? await ApiResults.ReadBodyAsync<AiAcceptRequest>(http.Request) : new AiAcceptRequest();
        return ToDto(await changes.DecideFileAsync(id, caller.Id, changeSetId, fileId, body.Hunks, http.RequestAborted));
    }

    private static async Task<AiDecisionDto> RejectFile(HttpContext http, ApiCaller caller, WorkspaceService workspaces, AiChangeService changes, string id, int changeSetId, int fileId)
    {
        await RequireMineAsync(workspaces, caller, id);
        return ToDto(await changes.DecideFileAsync(id, caller.Id, changeSetId, fileId, [], http.RequestAborted));
    }

    private static async Task<AiDecisionDto> AcceptAll(HttpContext http, ApiCaller caller, WorkspaceService workspaces, AiChangeService changes, string id, int changeSetId)
    {
        await RequireMineAsync(workspaces, caller, id);
        return ToDto(await changes.AcceptAllAsync(id, caller.Id, changeSetId, http.RequestAborted));
    }

    private static async Task<AiDecisionDto> RejectAll(HttpContext http, ApiCaller caller, WorkspaceService workspaces, AiChangeService changes, string id, int changeSetId)
    {
        await RequireMineAsync(workspaces, caller, id);
        return ToDto(await changes.RejectAllAsync(id, caller.Id, changeSetId, http.RequestAborted));
    }

    // ---------- runs ----------

    private static AiRunDto ToDto(AiRunView v) => new(v.Run.PublicId, v.IssueKey, v.ProjectKey, v.RepoSlug, v.Run.Status.ToString().ToLowerInvariant(), v.Run.StopReason,
        v.Run.Branch, v.Run.BaseBranch, v.Run.Trigger, v.RequestedBy, v.PullRequestNumber, v.Run.Steps, v.Run.ToolCalls, v.Run.InputTokens, v.Run.OutputTokens, v.Run.CostUsd,
        v.Run.Summary, v.Run.Error, v.Run.CancelRequested, Dto.Utc(v.Run.CreatedAt), Dto.Utc(v.Run.StartedAt), Dto.Utc(v.Run.FinishedAt), $"/ai-runs/{v.Run.PublicId}");

    private static async Task<Issue> RequireIssueAsync(IssueService issues, string key, int number) =>
        await issues.GetIssueAsync(key, number) ?? throw ApiException.NotFound($"Issue {key.ToUpperInvariant()}-{number} not found.");

    private static async Task<IResult> StartRun(HttpContext http, ApiCaller caller, IssueService issues, AiRunService runs, string key, int number)
    {
        var issue = await RequireIssueAsync(issues, key, number);
        var body = http.Request.ContentLength is > 0 ? await ApiResults.ReadBodyAsync<StartAiRunRequest>(http.Request) : new StartAiRunRequest();
        var (run, created) = await runs.RequestAsync(issue.Id, caller.Id, "api", body.Repository);
        var view = await runs.GetAsync(run.PublicId, caller.Id) ?? throw ApiException.NotFound("Run not found.");
        return TypedResults.Json(ToDto(view), Dto.Json, statusCode: created ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
    }

    private static async Task<List<AiRunDto>> ListIssueRuns(ApiCaller caller, IssueService issues, AiRunService runs, string key, int number)
    {
        var issue = await RequireIssueAsync(issues, key, number);
        return (await runs.ListForIssueAsync(issue.Id, caller.Id)).Select(ToDto).ToList();
    }

    private static async Task<List<AiRunDto>> ListRuns(ApiCaller caller, AiRunService runs, string? project = null, int take = 50) =>
        (await runs.ListAsync(caller.Id, project, take)).Select(ToDto).ToList();

    private static async Task<AiRunDto> GetRun(ApiCaller caller, AiRunService runs, string id) =>
        ToDto(await runs.GetAsync(id, caller.Id) ?? throw ApiException.NotFound("Run not found."));

    private static async Task<List<AiRunEventDto>> GetRunEvents(ApiCaller caller, AiRunService runs, string id, int after = 0)
    {
        if (await runs.GetAsync(id, caller.Id) == null) throw ApiException.NotFound("Run not found.");
        return (await runs.GetEventsAsync(id, caller.Id, after)).Select(e => new AiRunEventDto(e.Id, Dto.Utc(e.At), e.Kind, e.Message)).ToList();
    }

    private static async Task<AiRunDto> CancelRun(ApiCaller caller, AiRunService runs, string id)
    {
        if (await runs.GetAsync(id, caller.Id) == null) throw ApiException.NotFound("Run not found.");
        await runs.CancelAsync(id, caller.Id);
        return ToDto(await runs.GetAsync(id, caller.Id) ?? throw ApiException.NotFound("Run not found."));
    }
}
