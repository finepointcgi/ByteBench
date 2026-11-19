using GitBench.Data;
using GitBench.Services;
using GitBench.Services.ServiceDesk;

namespace GitBench.Api;

/// <summary>A service desk request in the queue. <c>waitingOnUs</c>: nobody answered yet, or the requester wrote last.</summary>
public record ServiceDeskRequestDto(int IssueId, string Key, string Title, string Status, bool IsClosed, string RequesterName, string RequesterEmail,
    string Source, DateTimeOffset CreatedAt, DateTimeOffset LastRequesterActivityAt, DateTimeOffset? LastAgentReplyAt, bool WaitingOnUs, bool Overdue, string WebUrl);

public record ReplyToRequesterRequest(string? Body);

public record ReplyToRequesterResponse(int CommentId, bool Emailed);

/// <summary>/api/v1/projects/{key}/service-desk/requests and /issues/{number}/requester-replies (Developer+).</summary>
public static class ServiceDeskEndpoints
{
    public static void MapServiceDeskEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/projects/{key}").WithTags("Service desk");
        g.MapGet("/service-desk/requests", ListRequests).WithName("ListServiceDeskRequests").WithSummary("List service desk requests")
            .WithDescription("filter: waitingOnUs (default), unanswered, open, closed or all. Longest-waiting first. Needs Developer or above.");
        g.MapPost("/issues/{number:int}/requester-replies", Reply).WithName("ReplyToRequester").WithSummary("Reply to a service desk requester")
            .WithDescription("Body: {body} (Markdown). Adds a public comment and emails it to the requester; ordinary comments are never emailed to requesters.")
            .WithJsonBody<ReplyToRequesterRequest>().Produces<ReplyToRequesterResponse>(StatusCodes.Status201Created);
    }

    private static async Task<Project> RequireProjectAsync(IssueService issues, string key) =>
        await issues.GetProjectAsync(key) ?? throw ApiException.NotFound($"Project {key.ToUpperInvariant()} not found.");

    private static async Task<List<ServiceDeskRequestDto>> ListRequests(HttpContext http, IssueService issues, ServiceDeskService desk, PublicBaseUrl url,
        string key, string? filter)
    {
        var project = await RequireProjectAsync(issues, key);
        var f = string.IsNullOrEmpty(filter) ? ServiceDeskQueueFilter.WaitingOnUs
            : Enum.TryParse<ServiceDeskQueueFilter>(filter, ignoreCase: true, out var parsed) ? parsed
            : throw ApiException.Unprocessable("filter must be waitingOnUs, unanswered, open, closed or all.");
        var baseUrl = url.For(http.Request);
        var paging = Paging.From(http.Request);
        return paging.Apply(http, await desk.GetQueueAsync(project.Id, f), r => new ServiceDeskRequestDto(r.IssueId, r.Key, r.Title, r.Status, r.IsClosed,
            r.RequesterName, r.RequesterEmail, r.Source.ToString().ToLowerInvariant(), Dto.Utc(r.CreatedAt), Dto.Utc(r.LastRequesterActivityAt),
            r.LastAgentReplyAt is { } at ? Dto.Utc(at) : null, r.WaitingOnUs, r.Overdue, $"{baseUrl}/p/{project.Key}/issues/{r.Number}"));
    }

    private static async Task<IResult> Reply(HttpContext http, IssueService issues, ServiceDeskService desk, ApiCaller caller, string key, int number)
    {
        var issue = await issues.GetIssueAsync(key, number) ?? throw ApiException.NotFound($"Issue {key.ToUpperInvariant()}-{number} not found.");
        var body = await ApiResults.ReadBodyAsync<ReplyToRequesterRequest>(http.Request);
        if (string.IsNullOrWhiteSpace(body.Body)) throw ApiException.Unprocessable("body is required.");
        var (comment, emailed) = await desk.ReplyToRequesterAsync(issue.Id, body.Body, caller.Id);
        return TypedResults.Created((string?)null, new ReplyToRequesterResponse(comment.Id, emailed));
    }
}
