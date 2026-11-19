using GitBench.Data;
using GitBench.Services;
using GitBench.Services.Ai.Triage;

namespace GitBench.Api;

public record TriageItemDto(int Id, string Kind, string Value, int? TargetId, string? Reason, string State);

public record TriageRunDto(int Id, string Status, string Trigger, string? Summary, string? Error, DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt, IReadOnlyList<TriageItemDto> Items);

/// <summary>An issue's AI suggestions. <c>run</c> is null until suggestions were requested.</summary>
public record TriageSuggestionsDto(bool Enabled, bool Available, bool InProgress, TriageRunDto? Run);

public record TriageSettingsDto(bool Enabled, bool AutoApplyLabels, bool Available);

/// <summary>PUT body for the project's AI triage settings.</summary>
public record TriageSettingsRequest(bool Enabled = false, bool AutoApplyLabels = false);

/// <summary>/api/v1/projects/{key}/issues/{number}/triage…: AI suggestions for an issue. The model call runs on a background queue.</summary>
public static class TriageEndpoints
{
    public static void MapTriageEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/projects/{key}").WithTags("AI triage");
        g.MapGet("/triage-settings", GetSettings).WithName("GetTriageSettings").WithSummary("The project's AI triage settings");
        g.MapPut("/triage-settings", PutSettings).WithName("SetTriageSettings").WithSummary("Turn AI triage on or off for the project")
            .WithDescription("Needs Maintainer+. autoApplyLabels applies suggested labels to new issues straight away; every other suggestion waits for a person.")
            .WithJsonBody<TriageSettingsRequest>();
        g.MapGet("/issues/{number:int}/triage", GetTriage).WithName("GetIssueTriage").WithSummary("The issue's latest AI suggestions")
            .WithDescription("Suggested labels (existing project labels only), component, priority, type, assignee (Developer+ members only), likely duplicates (issue keys with reasons) and a one-line summary. Each suggestion is pending, accepted, dismissed or autoApplied.");
        g.MapPost("/issues/{number:int}/triage", RequestTriage).WithName("RequestIssueTriage").WithSummary("Ask for AI suggestions")
            .WithDescription("Queues a run (Developer+) and answers 202 with the run, normally still queued: poll GET until its status is completed, failed or skipped. Answers 429 when a rate limit is hit (per issue, per user and per project, per hour) and 422 when triage is off for the project or no AI is configured.")
            .Produces<TriageSuggestionsDto>(StatusCodes.Status202Accepted);
        g.MapPost("/issues/{number:int}/triage/items/{id:int}/accept", Accept).WithName("AcceptTriageSuggestion")
            .WithSummary("Apply one suggestion")
            .WithDescription("Applies it through the normal issue services as the caller (Developer+), so permissions, activity, notifications, workflow rules and automation all run.");
        g.MapPost("/issues/{number:int}/triage/items/{id:int}/dismiss", Dismiss).WithName("DismissTriageSuggestion").WithSummary("Dismiss one suggestion");
    }

    public static TriageSuggestionsDto ToDto(TriageView v) => new(v.Enabled, v.Available, v.InProgress, v.Run == null ? null : ToDto(v.Run));

    public static TriageRunDto ToDto(IssueTriageRun r) => new(r.Id, Name(r.Status), r.Trigger, r.Summary, r.Error, Dto.Utc(r.CreatedAt), Dto.Utc(r.CompletedAt),
        r.Items.OrderBy(i => i.Id).Select(i => new TriageItemDto(i.Id, Name(i.Kind), i.Value, i.TargetId, i.Reason, Name(i.State))).ToList());

    private static string Name<T>(T e) where T : struct, Enum => char.ToLowerInvariant(e.ToString()[0]) + e.ToString()[1..];

    private static async Task<Issue> RequireIssueAsync(IssueService issues, string key, int number) =>
        await issues.GetIssueAsync(key, number) ?? throw ApiException.NotFound($"Issue {key.ToUpperInvariant()}-{number} not found.");

    private static async Task<TriageSettingsDto> GetSettings(IssueService issues, TriageService triage, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        return new(project.AiTriageEnabled, project.AiTriageAutoApplyLabels, triage.IsAvailable);
    }

    private static async Task<TriageSettingsDto> PutSettings(HttpContext http, IssueService issues, TriageService triage, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        var body = await ApiResults.ReadBodyAsync<TriageSettingsRequest>(http.Request);
        await triage.SetSettingsAsync(project.Id, body.Enabled, body.Enabled && body.AutoApplyLabels);
        return new(body.Enabled, body.Enabled && body.AutoApplyLabels, triage.IsAvailable);
    }

    private static async Task<TriageSuggestionsDto> GetTriage(IssueService issues, TriageService triage, string key, int number) =>
        ToDto(await triage.GetViewAsync((await RequireIssueAsync(issues, key, number)).Id));

    private static async Task<IResult> RequestTriage(IssueService issues, TriageService triage, string key, int number)
    {
        var issue = await RequireIssueAsync(issues, key, number);
        try { await triage.RequestAsync(issue.Id); }
        catch (TriageLimitException ex) { throw new ApiException(StatusCodes.Status429TooManyRequests, "Too Many Requests", ex.Message); }
        return Results.Json(ToDto(await triage.GetViewAsync(issue.Id)), statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<TriageSuggestionsDto> Accept(IssueService issues, TriageService triage, string key, int number, int id)
    {
        var issue = await RequireIssueAsync(issues, key, number);
        await RequireItemOfAsync(triage, issue.Id, id);
        await triage.AcceptAsync(id);
        return ToDto(await triage.GetViewAsync(issue.Id));
    }

    private static async Task<TriageSuggestionsDto> Dismiss(IssueService issues, TriageService triage, string key, int number, int id)
    {
        var issue = await RequireIssueAsync(issues, key, number);
        await RequireItemOfAsync(triage, issue.Id, id);
        await triage.DismissAsync(id);
        return ToDto(await triage.GetViewAsync(issue.Id));
    }

    /// <summary>The suggestion must belong to this issue's latest run (the URL names the issue; don't let an id from elsewhere through).</summary>
    private static async Task RequireItemOfAsync(TriageService triage, int issueId, int itemId)
    {
        var view = await triage.GetViewAsync(issueId);
        if (view.Run?.Items.All(i => i.Id != itemId) != false)
            throw ApiException.NotFound($"Suggestion {itemId} not found on this issue.");
    }
}
