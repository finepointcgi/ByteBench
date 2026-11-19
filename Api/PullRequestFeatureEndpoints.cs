using System.Text.Json;
using System.Text.Json.Nodes;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Git;

namespace ByteBench.Api;

/// <summary>Pull request extras: update (title, description, draft), thread resolution, suggested changes and branch compare.</summary>
public static class PullRequestFeatureEndpoints
{
    public static void MapPullRequestFeatureEndpoints(this RouteGroupBuilder api)
    {
        var prs = api.MapGroup("/projects/{key}/repos/{slug}").WithTags("Pull requests");
        prs.MapPatch("/pull-requests/{number:int}", UpdatePullRequest).WithName("UpdatePullRequest")
            .WithSummary("Update a pull request").WithDescription("Only fields present in the body change: title, description, draft (false marks it ready for review).")
            .WithJsonBody<UpdatePullRequestRequest>();
        prs.MapPost("/pull-requests/{number:int}/comments/{commentId:int}/resolve", (PullRequestService s, ApiCaller c, string key, string slug, int number, int commentId) =>
                SetResolved(s, c, key, slug, number, commentId, true))
            .WithName("ResolvePullRequestThread").WithSummary("Resolve a comment thread").WithDescription("Any comment of the thread may be named.");
        prs.MapPost("/pull-requests/{number:int}/comments/{commentId:int}/unresolve", (PullRequestService s, ApiCaller c, string key, string slug, int number, int commentId) =>
                SetResolved(s, c, key, slug, number, commentId, false))
            .WithName("UnresolvePullRequestThread").WithSummary("Reopen a resolved comment thread");
        prs.MapPost("/pull-requests/{number:int}/comments/{commentId:int}/apply-suggestion", (PullRequestService s, ApiCaller c, string key, string slug, int number, int commentId) =>
                Apply(s, c, key, slug, number, [commentId]))
            .WithName("ApplyPullRequestSuggestion").WithSummary("Apply a comment's suggested change")
            .WithDescription("Commits the suggestion to the source branch. 409 if the suggested lines changed since.");
        prs.MapPost("/pull-requests/{number:int}/apply-suggestions", ApplyMany).WithName("ApplyPullRequestSuggestions")
            .WithSummary("Apply several suggested changes in one commit").WithJsonBody<ApplySuggestionsRequest>();
        prs.MapGet("/compare", Compare).WithName("CompareBranches")
            .WithSummary("Compare two refs").WithDescription("?base= and ?head= (branches, tags or SHAs). Commits on head but not base (newest first), and the files changed since their merge base.");
    }

    private static async Task<PullRequest> RequirePullRequestAsync(PullRequestService prs, string key, string slug, int number) =>
        await prs.GetAsync(key, slug, number) ?? throw ApiException.NotFound($"Pull request {slug}#{number} not found.");

    private static async Task<PullRequestDto> UpdatePullRequest(HttpContext http, PullRequestService prs, ApiCaller caller, PublicBaseUrl url,
        string key, string slug, int number)
    {
        var pr = await RequirePullRequestAsync(prs, key, slug, number);
        var node = await ApiResults.ReadBodyAsync<JsonObject>(http.Request);
        UpdatePullRequestRequest body;
        try { body = node.Deserialize<UpdatePullRequestRequest>(Dto.Json) ?? new(); }
        catch (JsonException ex) { throw ApiException.BadRequest(ApiResults.InvalidJson(ex)); }
        if (node.ContainsKey("title") || node.ContainsKey("description"))
        {
            var title = node.ContainsKey("title") ? body.Title ?? "" : pr.Title;
            var description = node.ContainsKey("description") ? body.Description : pr.Description;
            if (title.Trim().Length is 0 or > 200) throw ApiException.Unprocessable("title is required (max 200 characters).");
            if (title.Trim() != pr.Title || description != pr.Description) await prs.UpdateAsync(pr.Id, caller.Id, title, description);
        }
        if (node.ContainsKey("draft"))
        {
            if (body.Draft is not { } draft) throw ApiException.Unprocessable("draft must be true or false.");
            await prs.SetDraftAsync(pr.Id, caller.Id, draft);
        }
        return Dto.PullRequest(await RequirePullRequestAsync(prs, key, slug, number), url.For(http.Request));
    }

    private static async Task<IResult> SetResolved(PullRequestService prs, ApiCaller caller, string key, string slug, int number, int commentId, bool resolved)
    {
        var pr = await RequirePullRequestAsync(prs, key, slug, number);
        var comment = pr.Comments.FirstOrDefault(c => c.Id == commentId) ?? throw ApiException.NotFound($"Comment {commentId} not found on {slug}#{number}.");
        await prs.SetThreadResolvedAsync(comment.Id, caller.Id, resolved);
        var root = (await RequirePullRequestAsync(prs, key, slug, number)).Comments.First(c => c.Id == (comment.ParentId ?? comment.Id));
        return TypedResults.Ok(Dto.PullRequestComment(root));
    }

    private static async Task<IResult> ApplyMany(HttpContext http, PullRequestService prs, ApiCaller caller, string key, string slug, int number)
    {
        var body = await ApiResults.ReadBodyAsync<ApplySuggestionsRequest>(http.Request);
        if (body.CommentIds is not { Count: > 0 }) throw ApiException.Unprocessable("commentIds must list at least one comment.");
        return await Apply(prs, caller, key, slug, number, body.CommentIds);
    }

    private static async Task<IResult> Apply(PullRequestService prs, ApiCaller caller, string key, string slug, int number, IReadOnlyList<int> ids)
    {
        var pr = await RequirePullRequestAsync(prs, key, slug, number);
        if (ids.FirstOrDefault(id => pr.Comments.All(c => c.Id != id)) is var missing and not 0)
            throw ApiException.NotFound($"Comment {missing} not found on {slug}#{number}.");
        try
        {
            var sha = await prs.ApplySuggestionsAsync(pr.Id, caller.User, ids.ToList());
            return TypedResults.Ok(new AppliedSuggestionsDto(sha, ids));
        }
        catch (GitConflictException ex)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "Conflict", ex.Message);
        }
    }

    private static async Task<CompareDto> Compare(RepositoryService repos, string key, string slug, string? @base, string? head)
    {
        var repo = await repos.GetRepositoryAsync(key, slug) ?? throw ApiException.NotFound($"Repository '{key.ToUpperInvariant()}/{slug}' not found.");
        var b = string.IsNullOrWhiteSpace(@base) ? repo.DefaultBranch : @base;
        if (string.IsNullOrWhiteSpace(head)) throw ApiException.Unprocessable("head is required.");
        var cmp = await repos.Git(repo, path => GitOps.Exists(path) ? GitOps.Compare(path, b, head) : null)
                  ?? throw ApiException.NotFound($"'{b}' or '{head}' doesn't exist.");
        return new CompareDto(b, head, cmp.BaseSha, cmp.HeadSha, cmp.MergeBaseSha, cmp.Ahead, cmp.Behind,
            cmp.Commits.Select(Dto.Commit).ToList(),
            cmp.Files.Select(f => new CompareFileDto(f.Path, f.OldPath, f.Status, f.Additions, f.Deletions)).ToList());
    }
}
