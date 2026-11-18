using GitBench.Data;
using GitBench.Services;
using GitBench.Services.Git;
using Microsoft.AspNetCore.Identity;

namespace GitBench.Api;

/// <summary>/api/v1/projects/{key}/repos… and pull requests. Reads use <see cref="RepositoryService"/>/<see cref="GitOps"/>; writes go through <see cref="PullRequestService"/>.</summary>
public static class RepoEndpoints
{
    public static void MapRepoEndpoints(this RouteGroupBuilder api)
    {
        var repos = api.MapGroup("/projects/{key}/repos").WithTags("Repositories");
        repos.MapGet("", ListRepos).WithName("ListRepositories").WithSummary("List repositories");
        repos.MapGet("/{slug}", GetRepo).WithName("GetRepository").WithSummary("Get a repository");
        repos.MapGet("/{slug}/branches", ListBranches).WithName("ListBranches").WithSummary("List branches");
        repos.MapGet("/{slug}/commits", ListCommits).WithName("ListCommits")
            .WithSummary("List commits").WithDescription("Newest first. ?ref= (branch, tag or SHA; default branch if omitted), ?path= to limit to one file or folder.");
        repos.MapGet("/{slug}/raw/{**path}", GetRaw).WithName("GetRawFile")
            .WithSummary("Get a file's raw content").WithDescription("?ref= selects the branch, tag or SHA (default branch if omitted). Text is served as text/plain, everything else as application/octet-stream.")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream");

        var prs = api.MapGroup("/projects/{key}").WithTags("Pull requests");
        prs.MapGet("/pull-requests", ListProjectPullRequests).WithName("ListProjectPullRequests")
            .WithSummary("List pull requests across the project's repositories").WithDescription("?state=open (default), merged, declined or all; ?repo= slug.");
        prs.MapGet("/repos/{slug}/pull-requests", ListRepoPullRequests).WithName("ListPullRequests")
            .WithSummary("List a repository's pull requests").WithDescription("?state=open (default), merged, declined or all.");
        prs.MapGet("/repos/{slug}/pull-requests/{number:int}", GetPullRequest).WithName("GetPullRequest").WithSummary("Get a pull request");
        prs.MapPost("/repos/{slug}/pull-requests", CreatePullRequest).WithName("CreatePullRequest").WithSummary("Open a pull request")
            .WithJsonBody<CreatePullRequestRequest>().Produces<PullRequestDto>(StatusCodes.Status201Created);
        prs.MapPost("/repos/{slug}/pull-requests/{number:int}/merge", MergePullRequest).WithName("MergePullRequest")
            .WithSummary("Merge a pull request").WithDescription("Body is optional. 409 with conflictPaths if it can't be merged cleanly.")
            .WithJsonBody<MergePullRequestRequest>(optional: true);
        prs.MapGet("/repos/{slug}/pull-requests/{number:int}/comments", ListPullRequestComments).WithName("ListPullRequestComments").WithSummary("List a pull request's comments");
        prs.MapPost("/repos/{slug}/pull-requests/{number:int}/comments", AddPullRequestComment).WithName("AddPullRequestComment")
            .WithSummary("Comment on a pull request").WithDescription("Set path and line (new side) or oldLine (deleted line) for an inline comment.")
            .WithJsonBody<AddPullRequestCommentRequest>().Produces<PullRequestCommentDto>(StatusCodes.Status201Created);
    }

    private static async Task<Repository> RequireRepoAsync(RepositoryService repos, string key, string slug) =>
        await repos.GetRepositoryAsync(key, slug) ?? throw ApiException.NotFound($"Repository '{key.ToUpperInvariant()}/{slug}' not found.");

    private static async Task<PullRequest> RequirePullRequestAsync(PullRequestService prs, string key, string slug, int number) =>
        await prs.GetAsync(key, slug, number) ?? throw ApiException.NotFound($"Pull request {slug}#{number} not found.");

    // ----- Repositories -----

    private static async Task<List<RepositoryDto>> ListRepos(HttpContext http, IssueService issues, RepositoryService repos, PublicBaseUrl url, string key)
    {
        var paging = Paging.From(http.Request);
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        var list = await repos.GetRepositoriesAsync(project.Id);
        var baseUrl = url.For(http.Request);
        return paging.Apply(http, list, r => Dto.Repository(r.Repository, baseUrl));
    }

    private static async Task<RepositoryDto> GetRepo(HttpContext http, RepositoryService repos, PublicBaseUrl url, string key, string slug) =>
        Dto.Repository(await RequireRepoAsync(repos, key, slug), url.For(http.Request));

    private static async Task<List<BranchDto>> ListBranches(HttpContext http, RepositoryService repos, string key, string slug)
    {
        var paging = Paging.From(http.Request);
        var repo = await RequireRepoAsync(repos, key, slug);
        var branches = await repos.Git(repo, path => GitOps.Exists(path) ? GitOps.ListBranches(path, repo.DefaultBranch) : []);
        return paging.Apply(http, branches, Dto.Branch);
    }

    private static async Task<List<CommitDto>> ListCommits(HttpContext http, RepositoryService repos, string key, string slug, string? @ref, string? path)
    {
        var paging = Paging.From(http.Request);
        var repo = await RequireRepoAsync(repos, key, slug);
        var rev = string.IsNullOrEmpty(@ref) ? repo.DefaultBranch : @ref;
        var (total, items) = await repos.Git(repo, p =>
        {
            if (!GitOps.Exists(p) || ApiGit.CountCommits(p, rev, path) is not { } count) return ((int?)null, new List<CommitInfo>());
            return (count, GitOps.Log(p, rev, path, paging.Skip, paging.PerPage).Items);
        });
        if (total == null) throw ApiException.NotFound($"Ref '{rev}' not found.");
        paging.WriteHeaders(http, total.Value);
        return items.Select(Dto.Commit).ToList();
    }

    private static async Task<IResult> GetRaw(HttpContext http, RepositoryService repos, string key, string slug, string path, string? @ref)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        var rev = string.IsNullOrEmpty(@ref) ? repo.DefaultBranch : @ref;
        if (string.IsNullOrWhiteSpace(path) || path.Split('/').Any(s => s is ".." or ".")) throw ApiException.NotFound("File not found.");
        var file = await repos.Git(repo, p => GitOps.Exists(p) ? ApiGit.GetRaw(p, rev, path) : null)
                   ?? throw ApiException.NotFound($"File '{path}' not found at '{rev}'.");
        if (file.Content == null)
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, "Payload Too Large", $"The file is {file.Size} bytes; the limit is {ApiGit.MaxRawBytes}. Clone the repository instead.");
        http.Response.Headers.ETag = $"\"{file.Sha}\"";
        http.Response.Headers.ContentDisposition = file.IsBinary ? $"attachment; filename=\"{Path.GetFileName(path).Replace("\"", "")}\"" : "inline";
        // Never text/html or similar: content is untrusted, so it must not render as a page on this origin.
        return TypedResults.Bytes(file.Content, file.IsBinary ? "application/octet-stream" : "text/plain; charset=utf-8");
    }

    // ----- Pull requests -----

    private static PullRequestState? ParseState(string? state) => state?.ToLowerInvariant() switch
    {
        null or "" or "open" => PullRequestState.Open,
        "all" => null,
        _ => IssueEndpoints.ParseEnum<PullRequestState>(state, "state"),
    };

    private static async Task<List<PullRequestDto>> ListProjectPullRequests(HttpContext http, IssueService issues, RepositoryService repos,
        PullRequestService prs, PublicBaseUrl url, string key, string? state, string? repo)
    {
        var paging = Paging.From(http.Request);
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        int? repoId = string.IsNullOrEmpty(repo) ? null : (await RequireRepoAsync(repos, key, repo)).Id;
        var list = await prs.ListAsync(project.Id, ParseState(state), repoId);
        var baseUrl = url.For(http.Request);
        return paging.Apply(http, list, p => Dto.PullRequest(p, baseUrl));
    }

    private static async Task<List<PullRequestDto>> ListRepoPullRequests(HttpContext http, RepositoryService repos, PullRequestService prs,
        PublicBaseUrl url, string key, string slug, string? state)
    {
        var paging = Paging.From(http.Request);
        var repo = await RequireRepoAsync(repos, key, slug);
        var list = await prs.ListAsync(repo.ProjectId, ParseState(state), repo.Id);
        var baseUrl = url.For(http.Request);
        return paging.Apply(http, list, p => Dto.PullRequest(p, baseUrl));
    }

    private static async Task<PullRequestDto> GetPullRequest(HttpContext http, PullRequestService prs, PublicBaseUrl url, string key, string slug, int number) =>
        Dto.PullRequest(await RequirePullRequestAsync(prs, key, slug, number), url.For(http.Request));

    private static async Task<IResult> CreatePullRequest(HttpContext http, RepositoryService repos, PullRequestService prs, ApiCaller caller,
        ProjectAccess access, PublicBaseUrl url, string key, string slug)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        var body = await ApiResults.ReadBodyAsync<CreatePullRequestRequest>(http.Request);
        if (string.IsNullOrWhiteSpace(body.SourceBranch) || string.IsNullOrWhiteSpace(body.TargetBranch))
            throw ApiException.Unprocessable("sourceBranch and targetBranch are required.");
        PullRequest pr;
        if (!string.IsNullOrWhiteSpace(body.SourceRepository))
        {
            // Cross-fork: sourceRepository is "KEY/slug" of a fork in this repository's network (see ForkEndpoints).
            var source = await ForkEndpoints.RequireRepoPathAsync(repos, body.SourceRepository);
            pr = await http.RequestServices.GetRequiredService<GitBench.Services.Forks.ForkService>().CreatePullRequestAsync(
                source, body.SourceBranch, repo, body.TargetBranch, body.Title ?? "", body.Description, caller.Id, body.Draft);
        }
        else
        {
            await access.RequireAsync(repo.ProjectId, Permission.CreatePullRequest);
            pr = await prs.CreateAsync(repo, body.Title ?? "", body.Description, body.SourceBranch, body.TargetBranch, caller.Id, draft: body.Draft);
        }
        var baseUrl = url.For(http.Request);
        var dto = Dto.PullRequest(await RequirePullRequestAsync(prs, key, slug, pr.Number), baseUrl);
        return TypedResults.Created($"{baseUrl}/api/v1/projects/{repo.Project.Key}/repos/{repo.Slug}/pull-requests/{pr.Number}", dto);
    }

    private static async Task<IResult> MergePullRequest(HttpContext http, PullRequestService prs, ApiCaller caller, ProjectAccess access,
        PublicBaseUrl url, string key, string slug, int number)
    {
        var pr = await RequirePullRequestAsync(prs, key, slug, number);
        var body = http.Request.ContentLength is > 0 || ApiResults.IsJson(http.Request) && http.Request.ContentLength != 0
            ? await ApiResults.ReadBodyAsync<MergePullRequestRequest>(http.Request)
            : new MergePullRequestRequest();
        await access.RequireAsync(pr.Repository.ProjectId, Permission.MergePullRequest);
        if (pr.State != PullRequestState.Open)
            throw new ApiException(StatusCodes.Status409Conflict, "Conflict", $"The pull request is already {pr.State.ToString().ToLowerInvariant()}.");

        MergeStrategy strategy;
        if (string.IsNullOrWhiteSpace(body.Strategy)) strategy = MergeStrategySettings.Default(pr.Repository);
        else strategy = MergeStrategySettings.Parse(body.Strategy) ?? throw ApiException.Unprocessable("strategy must be merge, squash or rebase.");
        var outcome = await prs.MergeAsync(pr.Id, caller.User, new MergeRequest(strategy, body.DeleteSourceBranch, body.FastForward, body.CommitMessage));
        if (!outcome.Success)
            throw new ApiException(StatusCodes.Status409Conflict, "Conflict", outcome.Error ?? "The pull request can't be merged.",
                new Dictionary<string, object?> { ["conflictPaths"] = outcome.ConflictPaths });
        var merged = await RequirePullRequestAsync(prs, key, slug, number);
        return TypedResults.Ok(new MergeResultDto(true, outcome.MergeCommitSha, outcome.FastForwarded, outcome.ConflictPaths,
            Dto.PullRequest(merged, url.For(http.Request))));
    }

    private static async Task<List<PullRequestCommentDto>> ListPullRequestComments(HttpContext http, PullRequestService prs, string key, string slug, int number)
    {
        var paging = Paging.From(http.Request);
        var pr = await RequirePullRequestAsync(prs, key, slug, number);
        return paging.Apply(http, pr.Comments.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToList(), Dto.PullRequestComment);
    }

    private static async Task<IResult> AddPullRequestComment(HttpContext http, PullRequestService prs, ApiCaller caller, ProjectAccess access,
        PublicBaseUrl url, string key, string slug, int number)
    {
        var pr = await RequirePullRequestAsync(prs, key, slug, number);
        var body = await ApiResults.ReadBodyAsync<AddPullRequestCommentRequest>(http.Request);
        await access.RequireAsync(pr.Repository.ProjectId, Permission.Comment);
        if (string.IsNullOrWhiteSpace(body.Body)) throw ApiException.Unprocessable("body is required.");
        if (body.ParentId == null && body.Path != null && body.Line == null && body.OldLine == null) throw ApiException.Unprocessable("An inline comment needs line or oldLine.");
        var c = await prs.AddCommentAsync(pr.Id, caller.Id, body.Body, string.IsNullOrWhiteSpace(body.Path) ? null : body.Path.Trim('/'), body.Line, body.OldLine,
            body.ParentId, body.StartLine);
        c.Author = caller.User;
        return TypedResults.Created($"{url.For(http.Request)}/api/v1/projects/{pr.Repository.Project.Key}/repos/{pr.Repository.Slug}/pull-requests/{number}/comments", Dto.PullRequestComment(c));
    }
}
