using GitBench.Data;
using GitBench.Services;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Api;

/// <summary>A sprint with its current issue counts. Committed* is the snapshot taken when it started, Completed* when it ended.</summary>
public record SprintDto(int Id, string Name, string? Goal, SprintState State, DateOnly? StartDate, DateOnly? EndDate,
    DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
    int IssueCount, int DoneCount, int Points, int DonePoints,
    int? CommittedCount, int? CommittedPoints, int? CompletedCount, int? CompletedPoints, string WebUrl);

/// <summary>/api/v1/projects/{key}/sprints… (read-only). Sprint issues: <c>GET /issues?sprint={id}&amp;status=all</c>.</summary>
public static class SprintEndpoints
{
    public static void MapSprintEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/projects/{key}").WithTags("Sprints");
        g.MapGet("/sprints", ListSprints).WithName("ListSprints").WithSummary("List sprints")
            .WithDescription("Active first, then planned, then completed (newest first). Filter: state (planned, active, completed).");
        g.MapGet("/sprints/{id:int}", GetSprint).WithName("GetSprint").WithSummary("Get a sprint");
    }

    /// <summary>Maps the issue list's <c>sprint</c> filter (id, 'none' or 'active') to <see cref="IssueFilter.SprintId"/>.</summary>
    public static async Task<int> ResolveSprintFilterAsync(SprintService sprints, int projectId, string value)
    {
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase)) return 0;
        if (value.Equals("active", StringComparison.OrdinalIgnoreCase)) return (await sprints.GetActiveAsync(projectId))?.Id ?? -1;
        return int.TryParse(value, out var id) && id > 0 ? id : throw ApiException.BadRequest("sprint must be an id, 'none' or 'active'.");
    }

    private static async Task<List<SprintDto>> ListSprints(HttpContext http, IssueService issues, SprintService sprints,
        IDbContextFactory<AppDbContext> dbFactory, PublicBaseUrl url, string key, string? state)
    {
        var paging = Paging.From(http.Request);
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        var list = await sprints.GetSprintsAsync(project.Id);
        if (!string.IsNullOrEmpty(state))
        {
            var s = IssueEndpoints.ParseEnum<SprintState>(state, "state");
            list = list.Where(x => x.State == s).ToList();
        }
        var page = list.Skip(paging.Skip).Take(paging.PerPage).ToList();
        paging.WriteHeaders(http, list.Count);
        var stats = await StatsAsync(dbFactory, page.Select(s => s.Id).ToList());
        var baseUrl = url.For(http.Request);
        return page.Select(s => ToDto(s, stats, project.Key, baseUrl)).ToList();
    }

    private static async Task<SprintDto> GetSprint(HttpContext http, IssueService issues, SprintService sprints,
        IDbContextFactory<AppDbContext> dbFactory, PublicBaseUrl url, string key, int id)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        var sprint = await sprints.GetSprintAsync(project.Id, id) ?? throw ApiException.NotFound($"Sprint {id} not found in {project.Key}.");
        return ToDto(sprint, await StatsAsync(dbFactory, [sprint.Id]), project.Key, url.For(http.Request));
    }

    private record Stats(int Count, int Done, int Points, int DonePoints);

    private static async Task<Dictionary<int, Stats>> StatsAsync(IDbContextFactory<AppDbContext> dbFactory, List<int> sprintIds)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.Issues.Where(i => i.SprintId != null && sprintIds.Contains(i.SprintId.Value))
            .Select(i => new { Sprint = i.SprintId!.Value, Done = i.Status == IssueStatus.Done, Points = i.StoryPoints ?? 0 }).ToListAsync();
        return rows.GroupBy(r => r.Sprint).ToDictionary(g => g.Key, g => new Stats(g.Count(), g.Count(r => r.Done), g.Sum(r => r.Points), g.Where(r => r.Done).Sum(r => r.Points)));
    }

    private static SprintDto ToDto(Sprint s, Dictionary<int, Stats> stats, string projectKey, string baseUrl)
    {
        var st = stats.GetValueOrDefault(s.Id) ?? new Stats(0, 0, 0, 0);
        return new SprintDto(s.Id, s.Name, s.Goal, s.State, Dto.Date(s.StartDate), Dto.Date(s.EndDate), Dto.Utc(s.StartedAt), Dto.Utc(s.CompletedAt),
            st.Count, st.Done, st.Points, st.DonePoints, s.CommittedCount, s.CommittedPoints, s.CompletedCount, s.CompletedPoints,
            $"{baseUrl}/p/{projectKey}/backlog#sprint-{s.Id}");
    }
}
