using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Dashboards;
using ByteBench.Services.Roadmap;

namespace ByteBench.Api;

public record RoadmapItemDto(int Id, string Key, string Title, IssueType Type, IssueStatus Status, DateOnly? StartDate, DateOnly? DueDate,
    UserDto? Assignee, IReadOnlyList<string> Labels, int ChildrenDone, int ChildrenTotal, int Progress, bool CanEdit, string WebUrl,
    IReadOnlyList<RoadmapItemDto> Children);
public record RoadmapDependencyDto(string Blocker, string Blocked);
public record RoadmapSprintDto(int Id, string Project, string Name, DateOnly Start, DateOnly End, SprintState State);
public record RoadmapMilestoneDto(int Id, string Title, DateOnly Due, bool Closed, string? Project);
public record RoadmapDto(IReadOnlyList<RoadmapItemDto> Epics, IReadOnlyList<RoadmapDependencyDto> Dependencies,
    IReadOnlyList<RoadmapSprintDto> Sprints, IReadOnlyList<RoadmapMilestoneDto> Milestones);
public record SetDatesRequest(DateOnly? StartDate, DateOnly? DueDate);
public record IssueDatesDto(string Key, DateOnly? StartDate, DateOnly? DueDate);

public record GadgetDto(int Id, GadgetKind Kind, string? Title, int Order, int Width, GadgetConfig Config);
public record DashboardDto(int Id, string Name, string? Description, UserDto Owner, string? SharedWith, bool CanEdit, bool IsHome,
    DateTimeOffset UpdatedAt, string WebUrl, IReadOnlyList<GadgetDto> Gadgets);
public record DashboardSummaryDto(int Id, string Name, string? Description, string Owner, bool IsMine, string? SharedWith, bool IsHome,
    int GadgetCount, DateTimeOffset UpdatedAt, string WebUrl);

/// <summary>Roadmap JSON (<c>/projects/{key}/roadmap</c>, <c>/groups/{group}/roadmap</c>), epic dates and dashboards (read-only).</summary>
public static class RoadmapDashboardEndpoints
{
    public static void MapRoadmapDashboardEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/projects/{key}/roadmap", ProjectRoadmap).WithName("GetProjectRoadmap").WithSummary("Get a project's roadmap")
            .WithDescription("Epics with their start/due dates, child progress and children; 'blocks' dependencies between them; sprints and milestones with dates. Filters: label (id), assignee (username or 'none').")
            .WithTags("Roadmap");
        api.MapGet("/groups/{group}/roadmap", GroupRoadmap).WithName("GetGroupRoadmap").WithSummary("Get a group's roadmap")
            .WithDescription("The roadmap across every project of the group and its subgroups that you can view.").WithTags("Roadmap");
        api.MapPut("/projects/{key}/issues/{number:int}/dates", SetDates).WithName("SetIssueDates").WithSummary("Set an issue's start and due dates")
            .WithDescription("Both fields are written (null clears). The start must not be after the due date (422). Needs EditIssues.")
            .WithTags("Roadmap").WithJsonBody<SetDatesRequest>();
        api.MapGet("/dashboards", ListDashboards).WithName("ListDashboards").WithSummary("List dashboards")
            .WithDescription("Your dashboards and those shared with projects or groups you can view.").WithTags("Dashboards");
        api.MapGet("/dashboards/{id:int}", GetDashboard).WithName("GetDashboard").WithSummary("Get a dashboard with its gadgets").WithTags("Dashboards");
    }

    private static async Task<RoadmapDto> ProjectRoadmap(HttpContext http, IssueService issues, RoadmapService roadmap, PublicBaseUrl url,
        string key, int? label, string? assignee)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        var data = await roadmap.GetAsync([project.Id], await FilterAsync(issues, label, assignee));
        return ToDto(data, url.For(http.Request));
    }

    private static async Task<RoadmapDto> GroupRoadmap(HttpContext http, IssueService issues, GroupService groups, RoadmapService roadmap,
        PublicBaseUrl url, string group, int? label, string? assignee)
    {
        var g = await GroupEndpoints.RequireGroupAsync(groups, group);
        var data = await roadmap.GetAsync(await groups.ProjectIdsAsync(g.Id), await FilterAsync(issues, label, assignee));
        return ToDto(data, url.For(http.Request));
    }

    private static async Task<RoadmapFilter> FilterAsync(IssueService issues, int? label, string? assignee)
    {
        int? assigneeId = null;
        if (!string.IsNullOrEmpty(assignee))
            assigneeId = assignee.Equals("none", StringComparison.OrdinalIgnoreCase) ? 0
                : (await issues.GetUsersAsync()).FirstOrDefault(u => string.Equals(u.UserName, assignee, StringComparison.OrdinalIgnoreCase))?.Id
                  ?? throw ApiException.BadRequest($"Unknown user '{assignee}'.");
        return new RoadmapFilter(label, assigneeId);
    }

    private static async Task<IssueDatesDto> SetDates(HttpContext http, IssueService issues, RoadmapService roadmap, ApiCaller caller, string key, int number)
    {
        var body = await ApiResults.ReadBodyAsync<SetDatesRequest>(http.Request);
        var issue = await issues.GetIssueAsync(key, number) ?? throw ApiException.NotFound($"Issue {key.ToUpperInvariant()}-{number} not found.");
        try
        {
            await roadmap.SetDatesAsync(issue.Id, body.StartDate?.ToDateTime(TimeOnly.MinValue), body.DueDate?.ToDateTime(TimeOnly.MinValue), caller.Id);
        }
        catch (InvalidOperationException ex) when (ex is not ForbiddenException)
        {
            throw ApiException.Unprocessable(ex.Message);
        }
        return new IssueDatesDto(issue.Key, body.StartDate, body.DueDate);
    }

    private static async Task<List<DashboardSummaryDto>> ListDashboards(HttpContext http, DashboardService dashboards, PublicBaseUrl url)
    {
        var baseUrl = url.For(http.Request);
        return (await dashboards.ListAsync()).Select(d => new DashboardSummaryDto(d.Id, d.Name, d.Description, d.OwnerName, d.IsMine, d.SharedWith,
            d.IsHome, d.GadgetCount, Dto.Utc(d.UpdatedAt), $"{baseUrl}/dashboards/{d.Id}")).ToList();
    }

    private static async Task<DashboardDto> GetDashboard(HttpContext http, DashboardService dashboards, PublicBaseUrl url, int id)
    {
        var v = await dashboards.GetAsync(id) ?? throw ApiException.NotFound($"Dashboard {id} not found.");
        var d = v.Dashboard;
        return new DashboardDto(d.Id, d.Name, d.Description, Dto.User(d.Owner), v.SharedWith, v.CanEdit, v.IsHome, Dto.Utc(d.UpdatedAt),
            $"{url.For(http.Request)}/dashboards/{d.Id}",
            d.Gadgets.Select(g => new GadgetDto(g.Id, g.Kind, g.Title, g.Order, g.Width, GadgetConfig.Parse(g.Config))).ToList());
    }

    private static RoadmapDto ToDto(RoadmapData data, string baseUrl)
    {
        var keys = data.AllItems.ToDictionary(i => i.Id, i => i.Key);
        RoadmapItemDto Item(RoadmapItem i) => new(i.Id, i.Key, i.Title, i.Type, i.Status, Dto.Date(i.StartDate), Dto.Date(i.DueDate),
            Dto.UserOrNull(i.Assignee), i.Labels.Select(l => l.Name).ToList(), i.Done, i.Total, i.Percent, i.CanEdit, baseUrl + i.Url,
            i.Children.Select(Item).ToList());
        return new RoadmapDto(data.Epics.Select(Item).ToList(),
            data.Dependencies.Select(d => new RoadmapDependencyDto(keys[d.FromIssueId], keys[d.ToIssueId])).ToList(),
            data.Sprints.Select(s => new RoadmapSprintDto(s.Id, s.ProjectKey, s.Name, DateOnly.FromDateTime(s.Start), DateOnly.FromDateTime(s.End), s.State)).ToList(),
            data.Milestones.Select(m => new RoadmapMilestoneDto(m.Id, m.Title, DateOnly.FromDateTime(m.Due), m.IsClosed, m.ProjectKey)).ToList());
    }
}
