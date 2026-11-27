using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Boards;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Api;

// ----- Management wire types: sprints and boards -----

/// <summary>Creates a planned sprint. Without a <c>name</c> it is called "KEY Sprint N". Dates are yyyy-MM-dd.</summary>
public record CreateSprintRequest(string? Name = null, string? Goal = null, DateOnly? StartDate = null, DateOnly? EndDate = null);

/// <summary>Only the properties you send change. An empty <c>goal</c> clears it.</summary>
public record UpdateSprintRequest(string? Name = null, string? Goal = null, DateOnly? StartDate = null, DateOnly? EndDate = null);

public record StartSprintRequest(DateOnly? StartDate = null, DateOnly? EndDate = null, string? Goal = null);

/// <summary><c>carryOver</c>: backlog (default), existingSprint (needs targetSprintId, a planned sprint) or newSprint (optional newSprintName).</summary>
public record CompleteSprintRequest(CarryOver? CarryOver = null, int? TargetSprintId = null, string? NewSprintName = null);

public record CompleteSprintResult(SprintDto Sprint, SprintDto? CarriedTo);

public record CreateBoardRequest(string? Name = null, BoardType? Type = null);

/// <summary>Only the properties you send change. <c>cardFields</c> is a list of names (key, type, labels, priority, storyPoints, assignee, …); an empty <c>filterQuery</c> clears the filter.</summary>
public record UpdateBoardRequest(string? Name = null, BoardType? Type = null, string? FilterQuery = null, List<int>? FilterLabelIds = null, int? FilterAssigneeId = null,
    IssueType? FilterType = null, int? FilterEpicId = null, BoardSwimlane? Swimlane = null, List<string>? CardFields = null, bool? BlockOverWip = null,
    int? DoneDays = null, bool? StartOnActiveSprint = null);

public record BoardColumnRequest(string? Name = null, int? WipLimit = null, List<int>? StatusIds = null);

public record SetColumnsRequest(List<BoardColumnRequest>? Columns = null);

public record BoardQuickFilterRequest(string? Name = null, string? Query = null);

public record SetQuickFiltersRequest(List<BoardQuickFilterRequest>? QuickFilters = null);

/// <summary>
/// Sprint and board management: create/edit/start/complete/delete sprints (Maintainer+) and create/configure/delete boards, their columns (workflow
/// statuses per column, WIP limits) and quick filters (Maintainer+). The workflow statuses themselves are under <c>/workflow/statuses</c>.
/// The same services as the backlog and board settings pages; bodies reject unknown properties.
/// </summary>
public static class PlanningManagementEndpoints
{
    public static void MapPlanningManagementEndpoints(this RouteGroupBuilder api)
    {
        var s = api.MapGroup("/projects/{key}/sprints").WithTags("Sprints");
        s.MapPost("", CreateSprint).WithName("CreateSprint").WithSummary("Create a planned sprint (Maintainer+)")
            .WithJsonBody<CreateSprintRequest>().Produces<SprintDto>(StatusCodes.Status201Created);
        s.MapPatch("/{id:int}", UpdateSprint).WithName("UpdateSprint").WithSummary("Change a sprint's name, goal or dates (Maintainer+)")
            .WithJsonBody<UpdateSprintRequest>();
        s.MapPost("/{id:int}/start", StartSprint).WithName("StartSprint").WithSummary("Start a planned sprint (Maintainer+)")
            .WithDescription("Needs startDate and endDate. Only one sprint can be active: 409 when another one is.").WithJsonBody<StartSprintRequest>().ProducesProblem(StatusCodes.Status409Conflict);
        s.MapPost("/{id:int}/complete", CompleteSprint).WithName("CompleteSprint").WithSummary("Complete the active sprint (Maintainer+)")
            .WithDescription("Open issues go to the backlog, to a planned sprint (carryOver existingSprint) or to a new one (carryOver newSprint).")
            .WithJsonBody<CompleteSprintRequest>(optional: true).Produces<CompleteSprintResult>();
        s.MapDelete("/{id:int}", DeleteSprint).WithName("DeleteSprint").WithSummary("Delete a planned or active sprint; its issues go back to the backlog (Maintainer+)")
            .Produces(StatusCodes.Status204NoContent);

        var b = api.MapGroup("/projects/{key}/boards").WithTags("Boards");
        b.MapPost("", CreateBoard).WithName("CreateBoard").WithSummary("Create a board that follows the workflow (Maintainer+)")
            .WithJsonBody<CreateBoardRequest>().Produces<BoardSummaryDto>(StatusCodes.Status201Created);
        b.MapPatch("/{id:int}", UpdateBoard).WithName("UpdateBoard").WithSummary("Change a board's settings (Maintainer+)")
            .WithJsonBody<UpdateBoardRequest>();
        b.MapPut("/{id:int}/columns", SetColumns).WithName("SetBoardColumns").WithSummary("Replace a board's columns (Maintainer+)")
            .WithDescription("Each column has a name, an optional WIP limit and the workflow status ids it shows (a status in at most one column). The board stops following the workflow.")
            .WithJsonBody<SetColumnsRequest>();
        b.MapPost("/{id:int}/reset-columns", ResetColumns).WithName("ResetBoardColumns").WithSummary("Make a board follow the workflow again: one column per status (Maintainer+)");
        b.MapPut("/{id:int}/quick-filters", SetQuickFilters).WithName("SetBoardQuickFilters").WithSummary("Replace a board's quick filters (Maintainer+)")
            .WithJsonBody<SetQuickFiltersRequest>();
        b.MapPost("/{id:int}/make-default", MakeDefault).WithName("MakeBoardDefault").WithSummary("Make this board the project's default board (Maintainer+)");
        b.MapDelete("/{id:int}", DeleteBoard).WithName("DeleteBoard").WithSummary("Delete a board (Maintainer+); the default board can't be deleted")
            .Produces(StatusCodes.Status204NoContent);
    }

    // ----------------------------------------------------------------------------------------------- sprints

    private static async Task<SprintDto> DtoAsync(SprintService sprints, IDbContextFactory<AppDbContext> dbFactory, Project project, int id, string baseUrl)
    {
        var sprint = await sprints.GetSprintAsync(project.Id, id) ?? throw ApiException.NotFound($"Sprint {id} not found in {project.Key}.");
        return SprintEndpoints.ToDto(sprint, await SprintEndpoints.StatsAsync(dbFactory, [id]), project.Key, baseUrl);
    }

    private static async Task<IResult> CreateSprint(HttpContext http, IssueService issues, SprintService sprints, ProjectAccess access, IDbContextFactory<AppDbContext> dbFactory,
        PublicBaseUrl url, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageLabelsAndMilestones);
        var body = await Management.ReadAsync<CreateSprintRequest>(http.Request);
        var sprint = await Management.RunAsync(() => sprints.CreateSprintAsync(project.Id, body.Name, body.Goal, Management.Date(body.StartDate), Management.Date(body.EndDate)));
        var baseUrl = url.For(http.Request);
        return TypedResults.Created($"{baseUrl}/api/v1/projects/{project.Key}/sprints/{sprint.Id}", await DtoAsync(sprints, dbFactory, project, sprint.Id, baseUrl));
    }

    private static async Task<SprintDto> UpdateSprint(HttpContext http, IssueService issues, SprintService sprints, ProjectAccess access, IDbContextFactory<AppDbContext> dbFactory,
        PublicBaseUrl url, string key, int id)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageLabelsAndMilestones);
        var existing = await sprints.GetSprintAsync(project.Id, id) ?? throw ApiException.NotFound($"Sprint {id} not found in {project.Key}.");
        var body = await Management.ReadAsync<UpdateSprintRequest>(http.Request);
        await Management.RunAsync(() => sprints.UpdateSprintAsync(id, body.Name == null ? existing.Name : Management.Required(body.Name, "name"),
            body.Goal ?? existing.Goal, body.StartDate != null ? Management.Date(body.StartDate) : existing.StartDate,
            body.EndDate != null ? Management.Date(body.EndDate) : existing.EndDate));
        return await DtoAsync(sprints, dbFactory, project, id, url.For(http.Request));
    }

    private static async Task<SprintDto> StartSprint(HttpContext http, IssueService issues, SprintService sprints, ProjectAccess access, IDbContextFactory<AppDbContext> dbFactory,
        PublicBaseUrl url, string key, int id)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageLabelsAndMilestones);
        var existing = await sprints.GetSprintAsync(project.Id, id) ?? throw ApiException.NotFound($"Sprint {id} not found in {project.Key}.");
        var body = await Management.ReadAsync<StartSprintRequest>(http.Request);
        var start = body.StartDate ?? throw ApiException.Unprocessable("startDate is required (yyyy-MM-dd).");
        var end = body.EndDate ?? throw ApiException.Unprocessable("endDate is required (yyyy-MM-dd).");
        await Management.RunAsync(() => sprints.StartSprintAsync(id, Management.Date(start)!.Value, Management.Date(end)!.Value, body.Goal ?? existing.Goal));
        return await DtoAsync(sprints, dbFactory, project, id, url.For(http.Request));
    }

    private static async Task<CompleteSprintResult> CompleteSprint(HttpContext http, IssueService issues, SprintService sprints, ProjectAccess access,
        IDbContextFactory<AppDbContext> dbFactory, PublicBaseUrl url, string key, int id)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageLabelsAndMilestones);
        _ = await sprints.GetSprintAsync(project.Id, id) ?? throw ApiException.NotFound($"Sprint {id} not found in {project.Key}.");
        var body = string.IsNullOrEmpty(http.Request.ContentType) && (http.Request.ContentLength ?? 0) == 0 ? new CompleteSprintRequest() : await Management.ReadAsync<CompleteSprintRequest>(http.Request);
        var carry = body.CarryOver ?? CarryOver.Backlog;
        var target = await Management.RunAsync(() => sprints.CompleteSprintAsync(id, carry, body.TargetSprintId, body.NewSprintName));
        var baseUrl = url.For(http.Request);
        return new CompleteSprintResult(await DtoAsync(sprints, dbFactory, project, id, baseUrl),
            target == null ? null : await DtoAsync(sprints, dbFactory, project, target.Id, baseUrl));
    }

    private static async Task<IResult> DeleteSprint(IssueService issues, SprintService sprints, ProjectAccess access, string key, int id)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageLabelsAndMilestones);
        _ = await sprints.GetSprintAsync(project.Id, id) ?? throw ApiException.NotFound($"Sprint {id} not found in {project.Key}.");
        await Management.RunAsync(() => sprints.DeleteSprintAsync(id));
        return TypedResults.NoContent();
    }

    // ----------------------------------------------------------------------------------------------- boards

    private static async Task<Project> ManageProjectAsync(IssueService issues, ProjectAccess access, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireRoleAsync(project.Id, ProjectRole.Maintainer, "manage boards"); // the same check BoardService makes
        return project;
    }

    private static async Task<Board> BoardAsync(BoardService boards, Project project, int id) =>
        await boards.GetBoardAsync(project.Id, id) ?? throw ApiException.NotFound($"Board {id} not found in {project.Key}.");

    private static BoardSummaryDto SummaryOf(Board b, string projectKey, string baseUrl) => new(b.Id, b.Name, b.Type, b.IsDefault, b.Swimlane,
        b.IsDefault ? $"{baseUrl}/p/{projectKey}/board" : $"{baseUrl}/p/{projectKey}/boards/{b.Id}");

    private static async Task<IResult> CreateBoard(HttpContext http, IssueService issues, BoardService boards, ProjectAccess access, PublicBaseUrl url, string key)
    {
        var project = await ManageProjectAsync(issues, access, key);
        var body = await Management.ReadAsync<CreateBoardRequest>(http.Request);
        var board = await Management.RunAsync(() => boards.CreateBoardAsync(project.Id, Management.Required(body.Name, "name"), body.Type ?? BoardType.Kanban));
        var baseUrl = url.For(http.Request);
        return TypedResults.Created($"{baseUrl}/api/v1/projects/{project.Key}/boards/{board.Id}", SummaryOf(board, project.Key, baseUrl));
    }

    private static BoardCardFields ParseCardFields(List<string> names)
    {
        var fields = BoardCardFields.None;
        foreach (var n in names) fields |= Management.Enum<BoardCardFields>(n, "cardFields");
        return fields;
    }

    private static async Task<BoardSummaryDto> UpdateBoard(HttpContext http, IssueService issues, BoardService boards, ProjectAccess access, PublicBaseUrl url, string key, int id)
    {
        var project = await ManageProjectAsync(issues, access, key);
        var board = await BoardAsync(boards, project, id);
        var body = await Management.ReadAsync<UpdateBoardRequest>(http.Request);
        var cur = BoardSettings.Of(board);
        var settings = cur with
        {
            Name = body.Name == null ? cur.Name : Management.Required(body.Name, "name"),
            Type = body.Type ?? cur.Type,
            FilterQuery = body.FilterQuery ?? cur.FilterQuery,
            FilterLabelIds = body.FilterLabelIds ?? cur.FilterLabelIds,
            FilterAssigneeId = body.FilterAssigneeId ?? cur.FilterAssigneeId,
            FilterType = body.FilterType ?? cur.FilterType,
            FilterEpicId = body.FilterEpicId ?? cur.FilterEpicId,
            Swimlane = body.Swimlane ?? cur.Swimlane,
            CardFields = body.CardFields == null ? cur.CardFields : ParseCardFields(body.CardFields),
            BlockOverWip = body.BlockOverWip ?? cur.BlockOverWip,
            DoneDays = body.DoneDays ?? cur.DoneDays,
            StartOnActiveSprint = body.StartOnActiveSprint ?? cur.StartOnActiveSprint,
        };
        await Management.RunAsync(() => boards.UpdateBoardAsync(id, settings));
        return SummaryOf(await BoardAsync(boards, project, id), project.Key, url.For(http.Request));
    }

    private static async Task<BoardSummaryDto> SetColumns(HttpContext http, IssueService issues, BoardService boards, ProjectAccess access, PublicBaseUrl url, string key, int id)
    {
        var project = await ManageProjectAsync(issues, access, key);
        await BoardAsync(boards, project, id);
        var body = await Management.ReadAsync<SetColumnsRequest>(http.Request);
        var drafts = (body.Columns ?? throw ApiException.Unprocessable("columns is required (1 to 20 columns)."))
            .Select(c => new BoardColumnDraft(Management.Required(c.Name, "columns[].name"), c.WipLimit, c.StatusIds ?? [])).ToList();
        await Management.RunAsync(() => boards.SaveColumnsAsync(id, drafts));
        return SummaryOf(await BoardAsync(boards, project, id), project.Key, url.For(http.Request));
    }

    private static async Task<BoardSummaryDto> ResetColumns(HttpContext http, IssueService issues, BoardService boards, ProjectAccess access, PublicBaseUrl url, string key, int id)
    {
        var project = await ManageProjectAsync(issues, access, key);
        await BoardAsync(boards, project, id);
        await Management.RunAsync(() => boards.ResetColumnsAsync(id));
        return SummaryOf(await BoardAsync(boards, project, id), project.Key, url.For(http.Request));
    }

    private static async Task<BoardSummaryDto> SetQuickFilters(HttpContext http, IssueService issues, BoardService boards, ProjectAccess access, PublicBaseUrl url, string key, int id)
    {
        var project = await ManageProjectAsync(issues, access, key);
        await BoardAsync(boards, project, id);
        var body = await Management.ReadAsync<SetQuickFiltersRequest>(http.Request);
        var drafts = (body.QuickFilters ?? throw ApiException.Unprocessable("quickFilters is required (a list, possibly empty)."))
            .Select(q => new BoardQuickFilterDraft(Management.Required(q.Name, "quickFilters[].name"), q.Query ?? "")).ToList();
        await Management.RunAsync(() => boards.SaveQuickFiltersAsync(id, drafts));
        return SummaryOf(await BoardAsync(boards, project, id), project.Key, url.For(http.Request));
    }

    private static async Task<BoardSummaryDto> MakeDefault(HttpContext http, IssueService issues, BoardService boards, ProjectAccess access, PublicBaseUrl url, string key, int id)
    {
        var project = await ManageProjectAsync(issues, access, key);
        await BoardAsync(boards, project, id);
        await Management.RunAsync(() => boards.MakeDefaultAsync(id));
        return SummaryOf(await BoardAsync(boards, project, id), project.Key, url.For(http.Request));
    }

    private static async Task<IResult> DeleteBoard(IssueService issues, BoardService boards, ProjectAccess access, string key, int id)
    {
        var project = await ManageProjectAsync(issues, access, key);
        await BoardAsync(boards, project, id);
        await Management.RunAsync(() => boards.DeleteBoardAsync(id));
        return TypedResults.NoContent();
    }
}
