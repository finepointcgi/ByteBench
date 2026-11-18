using GitBench.Data;
using GitBench.Services;
using GitBench.Services.Git;
using GitBench.Services.Releases;

namespace GitBench.Api;

public record TagDto(string Name, string Sha, string ObjectSha, bool Annotated, string? Message, string? TaggerName, string? TaggerEmail,
    DateTimeOffset? TaggedAt, CommitDto? Commit, string ZipUrl, string TarGzUrl);

public record ReleaseAssetDto(int Id, string Name, string ContentType, long Size, string Sha256, int DownloadCount, DateTimeOffset CreatedAt, string DownloadUrl);

public record ReleaseDto(int Id, string TagName, string Title, string? Notes, bool Prerelease, bool Draft, UserDto? Author,
    DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt, IReadOnlyList<ReleaseAssetDto> Assets, string WebUrl, string ZipUrl, string TarGzUrl);

/// <summary>POST body for creating a release. <c>target</c> (branch or SHA) is needed only when the tag doesn't exist yet.</summary>
public record CreateReleaseRequest(string TagName, string? Title = null, string? Notes = null, string? Target = null,
    string? TagMessage = null, bool Prerelease = false, bool Draft = false, bool GenerateNotes = false);

public static class ReleaseDtos
{
    /// <summary>Requires r.Repository.Project; Author and Assets when loaded.</summary>
    public static ReleaseDto Release(Release r, string baseUrl) => new(
        r.Id, r.TagName, r.Title, r.Notes, r.IsPrerelease, r.IsDraft, r.Author == null ? null : Dto.User(r.Author),
        Dto.Utc(r.CreatedAt), Dto.Utc(r.PublishedAt),
        r.Assets.OrderBy(a => a.FileName, StringComparer.OrdinalIgnoreCase).Select(a => Asset(a, baseUrl)).ToList(),
        baseUrl + ReleaseUrls.Detail(r.Repository, r.Id),
        baseUrl + ReleaseUrls.Archive(r.Repository, r.TagName, ArchiveFormat.Zip),
        baseUrl + ReleaseUrls.Archive(r.Repository, r.TagName, ArchiveFormat.TarGz));

    public static ReleaseAssetDto Asset(ReleaseAsset a, string baseUrl) =>
        new(a.Id, a.FileName, a.ContentType, a.Size, a.Sha256, a.DownloadCount, Dto.Utc(a.CreatedAt), baseUrl + ReleaseUrls.Asset(a));

    public static TagDto Tag(Repository repo, TagInfo t, string baseUrl) => new(
        t.Name, t.Commit?.Sha ?? t.ObjectSha, t.ObjectSha, t.IsAnnotated, t.Message, t.TaggerName, t.TaggerEmail,
        t.TaggedAt?.ToUniversalTime(), t.Commit is { } c ? Dto.Commit(c) : null,
        baseUrl + ReleaseUrls.Archive(repo, t.Name, ArchiveFormat.Zip),
        baseUrl + ReleaseUrls.Archive(repo, t.Name, ArchiveFormat.TarGz));
}

/// <summary>/api/v1/projects/{key}/repos/{slug}/tags and …/releases.</summary>
public static class ReleaseEndpoints
{
    public static void MapReleaseEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/projects/{key}/repos/{slug}").WithTags("Releases");
        g.MapGet("/tags", ListTags).WithName("ListTags").WithSummary("List tags").WithDescription("Newest first (tagger date, else commit date).");
        g.MapGet("/releases", ListReleases).WithName("ListReleases").WithSummary("List releases")
            .WithDescription("Newest first. Drafts are included only for users who can push (Developer+).");
        g.MapGet("/releases/latest", GetLatest).WithName("GetLatestRelease").WithSummary("The newest published release that isn't a pre-release");
        g.MapGet("/releases/{id:int}", GetRelease).WithName("GetRelease").WithSummary("Get a release");
        g.MapGet("/releases/tags/{**tag}", GetReleaseByTag).WithName("GetReleaseByTag").WithSummary("Get a release by its tag name");
        g.MapPost("/releases", CreateRelease).WithName("CreateRelease").WithSummary("Create a release")
            .WithDescription("Needs Developer+. If tagName doesn't exist it is created from target (a branch or SHA); tagMessage makes it annotated. " +
                             "generateNotes fills empty notes from the commits and merged pull requests since the previous release.")
            .WithJsonBody<CreateReleaseRequest>().Produces<ReleaseDto>(StatusCodes.Status201Created);
    }

    private static async Task<Repository> RequireRepoAsync(RepositoryService repos, string key, string slug) =>
        await repos.GetRepositoryAsync(key, slug) ?? throw ApiException.NotFound($"Repository '{key.ToUpperInvariant()}/{slug}' not found.");

    private static async Task<List<TagDto>> ListTags(HttpContext http, RepositoryService repos, ReleaseService releases, PublicBaseUrl url, string key, string slug)
    {
        var paging = Paging.From(http.Request);
        var repo = await RequireRepoAsync(repos, key, slug);
        var tags = await releases.ListTagsAsync(repo);
        var baseUrl = url.For(http.Request);
        return paging.Apply(http, tags, t => ReleaseDtos.Tag(repo, t, baseUrl));
    }

    private static async Task<List<ReleaseDto>> ListReleases(HttpContext http, RepositoryService repos, ReleaseService releases, PublicBaseUrl url, string key, string slug)
    {
        var paging = Paging.From(http.Request);
        var repo = await RequireRepoAsync(repos, key, slug);
        var list = await releases.ListAsync(repo);
        var baseUrl = url.For(http.Request);
        return paging.Apply(http, list, r => ReleaseDtos.Release(r, baseUrl));
    }

    private static async Task<ReleaseDto> GetRelease(HttpContext http, RepositoryService repos, ReleaseService releases, PublicBaseUrl url, string key, string slug, int id)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        var r = await releases.GetAsync(repo, id) ?? throw ApiException.NotFound($"Release {id} not found.");
        return ReleaseDtos.Release(r, url.For(http.Request));
    }

    private static async Task<ReleaseDto> GetReleaseByTag(HttpContext http, RepositoryService repos, ReleaseService releases, PublicBaseUrl url, string key, string slug, string tag)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        var r = await releases.GetByTagAsync(repo, tag) ?? throw ApiException.NotFound($"No release for tag '{tag}'.");
        return ReleaseDtos.Release(r, url.For(http.Request));
    }

    private static async Task<ReleaseDto> GetLatest(HttpContext http, RepositoryService repos, ReleaseService releases, PublicBaseUrl url, string key, string slug)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        var latest = await releases.GetLatestAsync(repo) ?? throw ApiException.NotFound("This repository has no published release.");
        var r = await releases.GetAsync(repo, latest.Id) ?? throw ApiException.NotFound("This repository has no published release.");
        return ReleaseDtos.Release(r, url.For(http.Request));
    }

    private static async Task<IResult> CreateRelease(HttpContext http, RepositoryService repos, ReleaseService releases, ProjectAccess access,
        ApiCaller caller, PublicBaseUrl url, string key, string slug)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        var body = await ApiResults.ReadBodyAsync<CreateReleaseRequest>(http.Request);
        await access.RequireAsync(repo.ProjectId, Permission.PushCode);
        if (string.IsNullOrWhiteSpace(body.TagName)) throw ApiException.Unprocessable("tagName is required.");
        var notes = body.Notes;
        if (body.GenerateNotes && string.IsNullOrWhiteSpace(notes))
        {
            var exists = await repos.Git(repo, p => GitOps.TagExists(p, body.TagName.Trim()));
            var from = exists ? body.TagName.Trim() : body.Target;
            if (!string.IsNullOrWhiteSpace(from)) notes = (await releases.GenerateNotesAsync(repo, from, body.TagName.Trim())).Markdown;
        }
        var created = await releases.CreateAsync(repo, new ReleaseInput
        {
            TagName = body.TagName, Target = body.Target, TagMessage = body.TagMessage, Title = body.Title ?? "",
            Notes = notes, IsPrerelease = body.Prerelease, IsDraft = body.Draft,
        }, caller.Id);
        var r = await releases.GetAsync(repo, created.Id) ?? throw ApiException.NotFound("Release not found.");
        var baseUrl = url.For(http.Request);
        return TypedResults.Created($"{baseUrl}/api/v1/projects/{repo.Project.Key}/repos/{repo.Slug}/releases/{r.Id}", ReleaseDtos.Release(r, baseUrl));
    }
}
