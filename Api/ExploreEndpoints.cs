using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Explore;

namespace ByteBench.Api;

public record StarDto(string Project, bool Starred, int Stars);

public record ProfileProjectDto(int Id, string Key, string Name, ProjectRole Role, string WebUrl);

public record UserProfileDto(int Id, string Username, string DisplayName, string AvatarColor, bool IsAdmin, DateTimeOffset? MemberSince,
    IReadOnlyList<ProfileProjectDto> Projects, IReadOnlyList<FeedItemDto> Activity, string WebUrl);

public record FeedItemDto(string Kind, DateTimeOffset At, UserDto Actor, string Project, string Title, string WebUrl, string? Detail);

public record SearchIssueDto(int Id, string Key, string Title, IssueType Type, IssueStatus Status, DateTimeOffset UpdatedAt, string WebUrl);

public record SearchPullRequestDto(int Id, string Project, string Repository, int Number, string Title, PullRequestState State, UserDto Author,
    DateTimeOffset UpdatedAt, string WebUrl);

public record SearchProjectDto(int Id, string Key, string Name, string? Description, string WebUrl);

public record SearchCodeMatchDto(string Project, string Repository, string Ref, string Path, int Line, string Text, string WebUrl);

/// <summary>Result groups; a type that wasn't searched is null. <c>JumpUrl</c> is set when the query is a visible issue key.</summary>
public record SearchResultDto(string Query, string? JumpUrl,
    IReadOnlyList<SearchIssueDto>? Issues, int? IssueCount,
    IReadOnlyList<SearchPullRequestDto>? PullRequests, int? PullRequestCount,
    IReadOnlyList<SearchProjectDto>? Projects, int? ProjectCount,
    IReadOnlyList<UserDto>? Users, int? UserCount,
    IReadOnlyList<SearchCodeMatchDto>? Code);

/// <summary>/api/v1/search, /api/v1/users/{username}, and project stars.</summary>
public static class ExploreEndpoints
{
    public static void MapExploreEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/search", SearchAll).WithName("Search").WithTags("Search").WithSummary("Search issues, pull requests, projects, users and code")
            .WithDescription("q is required. type limits the search to one of: issues, pull_requests, projects, users, code (code is only searched " +
                             "when asked for: the default branch of each visible repository, plain text, capped). perPage caps each group (max 100).");
        api.MapGet("/users/{username}", GetUser).WithName("GetUser").WithTags("Users")
            .WithSummary("A user's profile: projects and recent activity you can see");
        api.MapGet("/user/starred", ListStarred).WithName("ListStarredProjects").WithTags("Projects").WithSummary("Projects you have starred");
        api.MapPut("/projects/{key}/star", Star).WithName("StarProject").WithTags("Projects").WithSummary("Star a project");
        api.MapDelete("/projects/{key}/star", Unstar).WithName("UnstarProject").WithTags("Projects").WithSummary("Unstar a project");
    }

    private static async Task<SearchResultDto> SearchAll(HttpContext http, SearchService search, PublicBaseUrl url, string? q, string? type)
    {
        if (string.IsNullOrWhiteSpace(q)) throw ApiException.BadRequest("q is required.");
        SearchType[]? types = null;
        if (!string.IsNullOrEmpty(type))
        {
            types = [type.ToLowerInvariant() switch
            {
                "issues" => SearchType.Issues,
                "pull_requests" or "pull-requests" => SearchType.PullRequests,
                "projects" => SearchType.Projects,
                "users" => SearchType.Users,
                "code" => SearchType.Code,
                _ => throw ApiException.BadRequest("type must be one of issues, pull_requests, projects, users, code."),
            }];
        }
        var paging = Paging.From(http.Request);
        var r = await search.SearchAsync(q, types, paging.PerPage, ct: http.RequestAborted);
        var b = url.For(http.Request);
        bool Has(SearchType t) => types == null ? t != SearchType.Code : types.Contains(t);
        return new SearchResultDto(r.Query, r.JumpUrl is { } j ? b + j : null,
            Has(SearchType.Issues) ? r.Issues.Select(i => new SearchIssueDto(i.Id, i.Key, i.Title, i.Type, i.Status, Dto.Utc(i.UpdatedAt), b + i.Url)).ToList() : null,
            Has(SearchType.Issues) ? r.IssueCount : null,
            Has(SearchType.PullRequests) ? r.PullRequests.Select(p => new SearchPullRequestDto(p.Id, p.ProjectKey, p.Repository, p.Number, p.Title, p.State,
                Dto.User(p.Author), Dto.Utc(p.UpdatedAt), b + p.Url)).ToList() : null,
            Has(SearchType.PullRequests) ? r.PullRequestCount : null,
            Has(SearchType.Projects) ? r.Projects.Select(p => new SearchProjectDto(p.Id, p.Key, p.Name, p.Description, b + p.Url)).ToList() : null,
            Has(SearchType.Projects) ? r.ProjectCount : null,
            Has(SearchType.Users) ? r.Users.Select(u => Dto.User(u.User)).ToList() : null,
            Has(SearchType.Users) ? r.UserCount : null,
            Has(SearchType.Code)
                ? r.Code.SelectMany(c => c.Files.SelectMany(f => f.Snippets.SelectMany(s => s).Where(l => l.IsMatch)
                    .Select(l => new SearchCodeMatchDto(c.Repository.Project.Key, c.Repository.Slug, c.Ref, f.Path, l.Number, l.Text,
                        b + ByteBench.Services.Git.RepoUrls.Source(c.Repository, c.Ref, f.Path))))).ToList()
                : null);
    }

    private static async Task<UserProfileDto> GetUser(HttpContext http, UserActivityService activity, PublicBaseUrl url, string username)
    {
        var profile = await activity.GetProfileAsync(username) ?? throw ApiException.NotFound($"User '{username}' not found.");
        var feed = await activity.GetFeedAsync(profile.User.Id, take: 30);
        var b = url.For(http.Request);
        var u = profile.User;
        return new UserProfileDto(u.Id, u.UserName ?? "", u.DisplayName, u.Color, u.IsAdmin, Dto.Utc(profile.MemberSince),
            profile.Projects.Select(p => new ProfileProjectDto(p.Id, p.Key, p.Name, p.Role, $"{b}/p/{p.Key}")).ToList(),
            feed.Select(f => new FeedItemDto(Kind(f.Kind), Dto.Utc(f.At), Dto.User(f.Actor), f.ProjectKey, f.Title, b + f.Url, f.Detail)).ToList(),
            $"{b}/u/{Uri.EscapeDataString(u.UserName ?? "")}");
    }

    private static async Task<List<ProjectRefDto>> ListStarred(HttpContext http, StarService stars, IssueService issues, PublicBaseUrl url)
    {
        var ids = await stars.StarredProjectIdsAsync();
        var projects = (await issues.GetProjectsAsync()).ToDictionary(p => p.Id);
        var b = url.For(http.Request);
        return ids.Where(projects.ContainsKey).Select(id => projects[id])
            .Select(p => new ProjectRefDto(p.Id, p.Key, p.Name, $"{b}/p/{p.Key}")).ToList();
    }

    private static async Task<StarDto> Star(IssueService issues, StarService stars, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        return new StarDto(project.Key, true, await stars.StarAsync(project.Id));
    }

    private static async Task<StarDto> Unstar(IssueService issues, StarService stars, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        return new StarDto(project.Key, false, await stars.UnstarAsync(project.Id));
    }

    private static string Kind(FeedKind k) => k switch
    {
        FeedKind.IssueOpened => "issue_opened",
        FeedKind.IssueClosed => "issue_closed",
        FeedKind.IssueComment => "issue_comment",
        FeedKind.PullRequestOpened => "pull_request_opened",
        FeedKind.PullRequestMerged => "pull_request_merged",
        FeedKind.PullRequestComment => "pull_request_comment",
        _ => "push",
    };
}
