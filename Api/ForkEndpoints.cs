using GitBench.Data;
using GitBench.Services;
using GitBench.Services.Forks;
using GitBench.Services.Git;

namespace GitBench.Api;

/// <summary>A fork (or any repository) with the upstream it was forked from.</summary>
/// <param name="ForkedFrom">"KEY/slug" of the upstream; null for an original, or when you can't see it.</param>
public record ForkDto(RepositoryDto Repository, string Project, string? ForkedFrom);

/// <param name="Project">Key of the project to fork into (you need Maintainer+ there).</param>
/// <param name="Slug">Defaults to the upstream's slug.</param>
public record CreateForkRequest(string Project, string? Slug = null, string? Name = null);

public record SyncForkResultDto(string State, string? Before, string? After, int Ahead, int Behind);

/// <summary>/api/v1/projects/{key}/repos/{slug}/forks and sync-fork. Cross-fork PRs: <c>sourceRepository</c> on the create PR body.</summary>
public static class ForkEndpoints
{
    public static void MapForkEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/projects/{key}/repos/{slug}").WithTags("Forks");
        g.MapGet("/forks", ListForks).WithName("ListForks").WithSummary("List a repository's forks (those in projects you can see)");
        g.MapPost("/forks", CreateFork).WithName("CreateFork").WithSummary("Fork a repository into another project")
            .WithDescription("Needs ViewProject on this repository and Maintainer+ in the target project.")
            .WithJsonBody<CreateForkRequest>().Produces<ForkDto>(StatusCodes.Status201Created);
        g.MapPost("/sync-fork", SyncFork).WithName("SyncFork").WithSummary("Fast-forward a fork's default branch to its upstream")
            .WithDescription("state: upToDate, fastForwarded, ahead (nothing to do), diverged (nothing changed) or noUpstreamBranch.");
    }

    /// <summary>"KEY/slug" → the repository, or 404 (also when it can't be seen).</summary>
    public static async Task<Repository> RequireRepoPathAsync(RepositoryService repos, string path)
    {
        var parts = path.Trim().Split('/', 2);
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            throw ApiException.Unprocessable("sourceRepository must be \"KEY/slug\".");
        return await repos.GetRepositoryAsync(parts[0], parts[1]) ?? throw ApiException.NotFound($"Repository '{path}' not found.");
    }

    private static async Task<Repository> RequireRepoAsync(RepositoryService repos, string key, string slug) =>
        await repos.GetRepositoryAsync(key, slug) ?? throw ApiException.NotFound($"Repository '{key.ToUpperInvariant()}/{slug}' not found.");

    private static async Task<List<ForkDto>> ListForks(HttpContext http, RepositoryService repos, ForkService forks, PublicBaseUrl url, string key, string slug)
    {
        var paging = Paging.From(http.Request);
        var repo = await RequireRepoAsync(repos, key, slug);
        var baseUrl = url.For(http.Request);
        var from = $"{repo.Project.Key}/{repo.Slug}";
        return paging.Apply(http, await forks.ListForksAsync(repo), f => new ForkDto(Dto.Repository(f, baseUrl), f.Project.Key, from));
    }

    private static async Task<IResult> CreateFork(HttpContext http, RepositoryService repos, IssueService issues, ForkService forks, ApiCaller caller,
        PublicBaseUrl url, string key, string slug)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        var body = await ApiResults.ReadBodyAsync<CreateForkRequest>(http.Request);
        if (string.IsNullOrWhiteSpace(body.Project)) throw ApiException.Unprocessable("project is required.");
        var target = await IssueEndpoints.RequireProjectAsync(issues, body.Project);
        var fork = await forks.ForkAsync(repo, target.Id, body.Slug, body.Name, caller.User);
        var baseUrl = url.For(http.Request);
        return TypedResults.Created($"{baseUrl}/api/v1/projects/{target.Key}/repos/{fork.Slug}",
            new ForkDto(Dto.Repository(fork, baseUrl), target.Key, $"{repo.Project.Key}/{repo.Slug}"));
    }

    private static async Task<SyncForkResultDto> SyncFork(RepositoryService repos, ForkService forks, ApiCaller caller, string key, string slug)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        var r = await forks.SyncAsync(repo, caller.Id);
        return new SyncForkResultDto(char.ToLowerInvariant(r.State.ToString()[0]) + r.State.ToString()[1..], r.Before, r.After, r.Ahead, r.Behind);
    }
}
