using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.AutoMerge;

namespace ByteBench.Api;

/// <summary>A pull request's "merge when ready" request. <c>state</c>: armed, merging, merged or cancelled.</summary>
public record AutoMergeDto(string State, string Strategy, bool DeleteSourceBranch, UserDto ArmedBy, DateTimeOffset ArmedAt,
    string? WaitingFor, string? CancelReason, DateTimeOffset? FinishedAt);

/// <summary>PUT body of auto-merge: <c>strategy</c> is "merge", "squash" or "rebase" (default: the repository's default).</summary>
public record SetAutoMergeRequest(string? Strategy = null, bool DeleteSourceBranch = false);

/// <summary>"Merge when ready": arm, inspect and cancel.</summary>
public static class AutoMergeEndpoints
{
    public static void MapAutoMergeEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/projects/{key}/repos/{slug}/pull-requests/{number:int}/auto-merge").WithTags("Pull requests");
        g.MapGet("", Get).WithName("GetPullRequestAutoMerge").WithSummary("Get a pull request's auto-merge")
            .WithDescription("404 if auto-merge was never set. `state` is armed (waiting; see `waitingFor`), merging, merged or cancelled (see `cancelReason`).");
        g.MapPut("", Arm).WithName("SetPullRequestAutoMerge").WithSummary("Merge when ready")
            .WithDescription("Requires Maintainer (merge permission). The pull request is merged, with the same checks as a manual merge and as you, the moment every requirement is satisfied; if the target uses a merge queue it is queued instead. Replaces an armed request.")
            .WithJsonBody<SetAutoMergeRequest>(optional: true);
        g.MapDelete("", Cancel).WithName("CancelPullRequestAutoMerge").WithSummary("Cancel auto-merge")
            .WithDescription("The person who set it, or a Maintainer. 404 if it isn't armed.");
    }

    private static async Task<PullRequest> RequireAsync(PullRequestService prs, string key, string slug, int number) =>
        await prs.GetAsync(key, slug, number) ?? throw ApiException.NotFound($"Pull request {slug}#{number} not found.");

    private static string Name(Enum value) => value.ToString().ToLowerInvariant();

    private static AutoMergeDto ToDto(PullRequestAutoMerge a) => new(Name(a.State), Name(a.Strategy), a.DeleteSourceBranch, Dto.User(a.ArmedBy),
        Dto.Utc(a.ArmedAt), a.WaitingFor, a.CancelReason, Dto.Utc(a.FinishedAt));

    private static async Task<AutoMergeDto> Get(PullRequestService prs, AutoMergeService autoMerge, string key, string slug, int number)
    {
        var pr = await RequireAsync(prs, key, slug, number);
        return ToDto(await autoMerge.GetAsync(pr.Id) ?? throw ApiException.NotFound("Auto-merge was never set for this pull request."));
    }

    private static async Task<AutoMergeDto> Arm(HttpContext http, PullRequestService prs, AutoMergeService autoMerge, ApiCaller caller, string key, string slug, int number)
    {
        var pr = await RequireAsync(prs, key, slug, number);
        var body = http.Request.ContentLength is > 0 || ApiResults.IsJson(http.Request) && http.Request.ContentLength != 0
            ? await ApiResults.ReadBodyAsync<SetAutoMergeRequest>(http.Request)
            : new SetAutoMergeRequest();
        MergeStrategy strategy;
        if (string.IsNullOrWhiteSpace(body.Strategy)) strategy = MergeStrategySettings.Default(pr.Repository);
        else strategy = MergeStrategySettings.Parse(body.Strategy) ?? throw ApiException.Unprocessable("strategy must be merge, squash or rebase.");
        try { await autoMerge.ArmAsync(pr.Id, caller.Id, new MergeRequest(strategy, body.DeleteSourceBranch)); }
        catch (ForbiddenException) { throw; }
        catch (InvalidOperationException ex) { throw new ApiException(StatusCodes.Status409Conflict, "Conflict", ex.Message); }
        return ToDto(await autoMerge.GetAsync(pr.Id) ?? throw ApiException.NotFound("Auto-merge not found."));
    }

    private static async Task<IResult> Cancel(PullRequestService prs, AutoMergeService autoMerge, ApiCaller caller, string key, string slug, int number)
    {
        var pr = await RequireAsync(prs, key, slug, number);
        if (!await autoMerge.CancelAsync(pr.Id, caller.Id)) throw ApiException.NotFound("Auto-merge isn't armed for this pull request.");
        return TypedResults.NoContent();
    }
}
