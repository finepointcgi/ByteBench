using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Boards;

namespace ByteBench.Api;

public record BoardSummaryDto(int Id, string Name, BoardType Type, bool IsDefault, BoardSwimlane Swimlane, string WebUrl);

public record BoardQuickFilterDto(int Id, string Name, string Query);

/// <summary>A card on a board (a compact issue). EpicKey: the epic it belongs to, if any.</summary>
public record BoardIssueDto(int Id, string Key, int Number, string Title, IssueType Type, IssuePriority Priority, int? StoryPoints,
    UserDto? Assignee, IReadOnlyList<LabelDto> Labels, WorkflowStatusRefDto? WorkflowStatus, int? SprintId, string? EpicKey, string WebUrl);

/// <summary>Count: issues in the column within the board's scope (what the WIP limit counts); Issues: those shown (after quick filters).</summary>
public record BoardColumnDto(string Key, string Name, IReadOnlyList<WorkflowStatusRefDto> Statuses, int? WipLimit, int Count, bool OverWipLimit,
    IReadOnlyList<BoardIssueDto> Issues);

public record BoardDto(int Id, string Name, BoardType Type, bool IsDefault, bool FollowsWorkflow, string? FilterQuery, BoardSwimlane Swimlane,
    BoardCardFields CardFields, bool BlockOverWip, int? DoneDays, SprintRefDto? Sprint, IReadOnlyList<BoardQuickFilterDto> QuickFilters,
    IReadOnlyList<BoardColumnDto> Columns, string WebUrl);

public record SprintRefDto(int Id, string Name);

/// <summary>/api/v1/projects/{key}/boards… (read-only). Moving cards: PATCH the issue's workflowStatusId.</summary>
public static class BoardEndpoints
{
    public static void MapBoardEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/projects/{key}").WithTags("Boards");
        g.MapGet("/boards", ListBoards).WithName("ListBoards").WithSummary("List boards")
            .WithDescription("The project's boards, the default board first.");
        g.MapGet("/boards/{id:int}", GetBoard).WithName("GetBoard").WithSummary("Get a board with its column issues")
            .WithDescription("Columns in order, each with its workflow statuses, WIP limit and issues. quickFilters: comma-separated quick filter ids to apply.");
    }

    private static string WebUrl(string baseUrl, string projectKey, Board b) =>
        b.IsDefault ? $"{baseUrl}/p/{projectKey}/board" : $"{baseUrl}/p/{projectKey}/boards/{b.Id}";

    private static async Task<List<BoardSummaryDto>> ListBoards(HttpContext http, IssueService issues, BoardService boards, PublicBaseUrl url, string key)
    {
        var paging = Paging.From(http.Request);
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        var baseUrl = url.For(http.Request);
        return paging.Apply(http, await boards.GetBoardsAsync(project.Id),
            b => new BoardSummaryDto(b.Id, b.Name, b.Type, b.IsDefault, b.Swimlane, WebUrl(baseUrl, project.Key, b)));
    }

    private static async Task<BoardDto> GetBoard(HttpContext http, IssueService issues, BoardService boards, PublicBaseUrl url, string key, int id, string? quickFilters)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        var board = await boards.GetBoardAsync(project.Id, id) ?? throw ApiException.NotFound($"Board {id} not found in {project.Key}.");
        var quick = new List<int>();
        foreach (var part in (quickFilters ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            quick.Add(int.TryParse(part, out var q) ? q : throw ApiException.BadRequest("quickFilters must be comma-separated quick filter ids."));

        var data = await boards.GetDataAsync(board, new BoardViewOptions { QuickFilterIds = quick });
        var byColumn = data.ByColumn();
        var baseUrl = url.For(http.Request);
        return new BoardDto(board.Id, board.Name, board.Type, board.IsDefault, board.FollowsWorkflow, board.FilterQuery, board.Swimlane, board.CardFields,
            board.BlockOverWip, board.Type == BoardType.Kanban ? board.DoneDays : null,
            data.Sprint is { } s ? new SprintRefDto(s.Id, s.Name) : null,
            board.QuickFilters.OrderBy(q => q.Order).Select(q => new BoardQuickFilterDto(q.Id, q.Name, q.Query)).ToList(),
            data.Columns.Select(c => new BoardColumnDto(c.Key, c.Name,
                c.Statuses.Select(st => new WorkflowStatusRefDto(st.Id, st.Name, st.Category)).ToList(),
                c.WipLimit, data.ScopeCounts.GetValueOrDefault(c.Key), data.IsOverWip(c),
                byColumn[c.Key].Select(i => Card(i, data, baseUrl)).ToList())).ToList(),
            WebUrl(baseUrl, project.Key, board));
    }

    private static BoardIssueDto Card(Issue i, BoardData data, string baseUrl) => new(
        i.Id, i.Key, i.Number, i.Title, i.Type, i.Priority, i.StoryPoints,
        i.Assignee is { } a ? Dto.User(a) : null,
        i.Labels.OrderBy(l => l.Name).Select(Dto.Label).ToList(),
        data.Workflow.Effective(i) is { } w ? new WorkflowStatusRefDto(w.Id, w.Name, w.Category) : null,
        i.SprintId, data.Epics.GetValueOrDefault(i.Id)?.Key,
        $"{baseUrl}/p/{i.Project.Key}/issues/{i.Number}");
}
