using ByteBench.Data;
using ByteBench.Services;

namespace ByteBench.Api;

// ----- Linked work wire types -----

public partial record IssueDto
{
    /// <summary>Id of the parent issue (an epic, or a sub-task's story/task), or null.</summary>
    public int? ParentId { get; init; }
}

public partial record CreateIssueRequest
{
    public int? ParentId { get; init; }
}

public partial record UpdateIssueRequest
{
    /// <summary>Present and null removes the parent.</summary>
    public int? ParentId { get; init; }
}

/// <summary>A small issue reference used in link lists.</summary>
public record IssueRefDto(int Id, string Key, int Number, string Project, string Title, IssueType Type, IssueStatus Status, string WebUrl);

/// <summary>A link seen from the issue in the URL: <c>type</c> is blocks, isBlockedBy, relatesTo, duplicates, isDuplicatedBy, clones or isClonedBy.</summary>
public record IssueLinkDto(int Id, IssueLinkRelation Type, IssueRefDto Issue, DateTimeOffset CreatedAt);

/// <summary><c>issue</c> is the other issue's key, e.g. "OPS-2" (any project you can view).</summary>
public record AddIssueLinkRequest(IssueLinkRelation? Type, string? Issue);

/// <summary>/api/v1/projects/{key}/issues/{number}/links and /children. Writes go through <see cref="LinkedWorkService"/>.</summary>
public static class LinkEndpoints
{
    public static void MapLinkEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/projects/{key}/issues/{number:int}").WithTags("Issues");
        g.MapGet("/links", ListLinks).WithName("ListIssueLinks").WithSummary("List an issue's links")
            .WithDescription("Links to issues you can't view are left out.");
        g.MapPost("/links", AddLink).WithName("AddIssueLink").WithSummary("Link an issue to another issue")
            .WithDescription("Body: {type, issue}. type is blocks, isBlockedBy, relatesTo, duplicates, isDuplicatedBy, clones or isClonedBy, seen from this issue; issue is the other issue's key (may be in another project you can view).")
            .WithJsonBody<AddIssueLinkRequest>().Produces<IssueLinkDto>(StatusCodes.Status201Created);
        g.MapDelete("/links/{linkId:int}", RemoveLink).WithName("RemoveIssueLink").WithSummary("Remove a link").Produces(StatusCodes.Status204NoContent);
        g.MapGet("/children", ListChildren).WithName("ListIssueChildren").WithSummary("List an issue's child issues (an epic's issues, or sub-tasks)");
    }

    private static async Task<Issue> RequireIssueAsync(IssueService issues, string key, int number) =>
        await issues.GetIssueAsync(key, number) ?? throw ApiException.NotFound($"Issue {key.ToUpperInvariant()}-{number} not found.");

    private static IssueLinkDto ToDto(IssueLinkView l, string baseUrl) => new(l.LinkId, l.Relation,
        new IssueRefDto(l.Other.Id, l.Other.Key, l.Other.Number, l.Other.ProjectKey, l.Other.Title, l.Other.Type, l.Other.Status, baseUrl + l.Other.Url),
        Dto.Utc(l.CreatedAt));

    private static async Task<List<IssueLinkDto>> ListLinks(HttpContext http, IssueService issues, LinkedWorkService linked, PublicBaseUrl url, string key, int number)
    {
        var paging = Paging.From(http.Request);
        var issue = await RequireIssueAsync(issues, key, number);
        var baseUrl = url.For(http.Request);
        return paging.Apply(http, await linked.GetLinksAsync(issue.Id), l => ToDto(l, baseUrl));
    }

    private static async Task<IResult> AddLink(HttpContext http, IssueService issues, LinkedWorkService linked, ApiCaller caller, ProjectAccess access,
        PublicBaseUrl url, string key, int number)
    {
        var issue = await RequireIssueAsync(issues, key, number);
        var body = await ApiResults.ReadBodyAsync<AddIssueLinkRequest>(http.Request);
        await access.RequireAsync(issue.ProjectId, Permission.EditIssues);
        if (body.Type is not { } type) throw ApiException.Unprocessable("type is required.");
        if (string.IsNullOrWhiteSpace(body.Issue)) throw ApiException.Unprocessable("issue (the other issue's key) is required.");
        var other = await linked.FindByKeyAsync(body.Issue) ?? throw ApiException.Unprocessable($"Issue '{body.Issue}' not found.");
        var link = await linked.AddLinkAsync(issue.Id, other.Id, type, caller.Id);
        var baseUrl = url.For(http.Request);
        return TypedResults.Created($"{baseUrl}/api/v1/projects/{issue.Project.Key}/issues/{number}/links", ToDto(link, baseUrl));
    }

    private static async Task<IResult> RemoveLink(IssueService issues, LinkedWorkService linked, ApiCaller caller, ProjectAccess access, string key, int number, int linkId)
    {
        var issue = await RequireIssueAsync(issues, key, number);
        await access.RequireAsync(issue.ProjectId, Permission.EditIssues);
        if ((await linked.GetLinksAsync(issue.Id)).All(l => l.LinkId != linkId)) throw ApiException.NotFound($"Link {linkId} not found on {issue.Key}.");
        await linked.RemoveLinkAsync(linkId, issue.Id, caller.Id);
        return TypedResults.NoContent();
    }

    private static async Task<List<IssueDto>> ListChildren(HttpContext http, IssueService issues, LinkedWorkService linked, PublicBaseUrl url, string key, int number)
    {
        var paging = Paging.From(http.Request);
        var issue = await RequireIssueAsync(issues, key, number);
        var children = new List<Issue>();
        foreach (var c in (await linked.GetHierarchyAsync(issue.Id)).Children)
            if (await issues.GetIssueAsync(c.ProjectKey, c.Number) is { } full) children.Add(full);
        var baseUrl = url.For(http.Request);
        return paging.Apply(http, children, i => Dto.Issue(i, baseUrl));
    }
}
