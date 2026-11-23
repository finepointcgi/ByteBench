using ByteBench.Services;
using ByteBench.Services.Insights;
using Microsoft.Extensions.Options;

namespace ByteBench.Api;

/// <summary>GET /api/v1/projects/{key}/repos/{slug}/insights</summary>
public static class InsightsEndpoints
{
    public static void MapInsightsEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/projects/{key}/repos/{slug}/insights", GetInsights).WithTags("Repositories").WithName("GetRepositoryInsights")
            .WithSummary("Repository insights")
            .WithDescription("The numbers of the Insights page: contributors (commits and lines over ?range=7d|30d|90d|1y|all, default 1y; merged by e-mail or name, with the ByteBench user when the e-mail matches), " +
                             "commits per week and per day for the last 52 weeks, additions and deletions per week, the language breakdown of the default branch, busiest hours (UTC) and weekdays, " +
                             "and pull request stats per month. Needs the same access as viewing the repository (read scope). The numbers are computed from the default branch's history " +
                             "(at most Insights:MaxCommits commits) and cached per head commit: when the cache is stale the call waits up to Insights:ApiWaitSeconds for the computation, " +
                             "then answers 202 with status \"computing\"; ask again. status \"empty\": the repository has no commits yet.")
            .Produces<InsightsView>(StatusCodes.Status200OK).Produces<InsightsView>(StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> GetInsights(RepositoryService repos, InsightsService insights, IOptionsMonitor<InsightsOptions> options,
        string key, string slug, string? range, CancellationToken ct)
    {
        var repo = await repos.GetRepositoryAsync(key, slug) ?? throw ApiException.NotFound($"Repository '{key.ToUpperInvariant()}/{slug}' not found.");
        if (!string.IsNullOrWhiteSpace(range) && !InsightsService.Ranges.Contains(range.ToLowerInvariant()))
            throw ApiException.BadRequest("range must be one of " + string.Join(", ", InsightsService.Ranges) + ".");
        var view = await insights.GetAsync(repo, range, TimeSpan.FromSeconds(Math.Clamp(options.CurrentValue.ApiWaitSeconds, 0, 120)), ct);
        return view.Status switch
        {
            InsightsStatus.Computing => TypedResults.Json(view, statusCode: StatusCodes.Status202Accepted),
            InsightsStatus.Failed when view.CommitsScanned == 0 => ApiResults.Problem(StatusCodes.Status503ServiceUnavailable, "Insights unavailable", view.Message),
            _ => TypedResults.Ok(view),
        };
    }
}
