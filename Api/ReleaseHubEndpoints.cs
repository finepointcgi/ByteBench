using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Releases;

namespace ByteBench.Api;

public record HubArtifactDto(int Id, int PipelineId, string Job, long SizeBytes, int FileCount, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, bool Expired);

public record HubPipelineDto(int Id, string Ref, string Status, DateTimeOffset CreatedAt, IReadOnlyList<HubArtifactDto> Artifacts);

public record HubPackageDto(int PackageId, string Name, string Type, string Version, string? ManifestDigest, long Size, bool PublishedByPipeline, string WebUrl);

public record HubSecurityDto(int PipelineId, int Critical, int High, int Medium, int Low, int Other, int Dismissed, int Open, DateTimeOffset ScannedAt);

public record HubIssueDto(int Number, string Key, string Title, string Status, string Via);

public record HubPullRequestDto(int Number, string Title, string? Author);

public record HubSignatureDto(string Status, string Label, string? Format, string? Reason);

public record HubTestRunDto(int Number, string Name, string Build, TestSummaryDto Summary, string WebUrl);

public record ReleaseHubDto(ReleaseDto Release, string? CommitSha, string? PreviousTag, string? CompareUrl, bool TagMissing, HubSignatureDto? Signature,
    IReadOnlyList<HubPipelineDto> Pipelines, IReadOnlyList<HubPackageDto> Packages, HubSecurityDto? Security, IReadOnlyList<ReleaseAssetDto> Sboms,
    IReadOnlyList<HubIssueDto> Issues, IReadOnlyList<HubPullRequestDto> PullRequests, IReadOnlyList<HubTestRunDto> TestRuns, long TotalDownloads,
    string ChecksumsUrl, bool AutoAttachEnabled, IReadOnlyList<string> AutoAttachPatterns);

public record ReleaseSettingsDto(bool AutoAttachEnabled, IReadOnlyList<string> Patterns);

public record SaveReleaseSettingsRequest(bool AutoAttachEnabled, IReadOnlyList<string>? Patterns = null);

public record AttachArtifactsResultDto(int Attached, IReadOnlyList<string> Names, IReadOnlyList<string> Notes);

public record ReleaseOverviewDto(string Repository, ReleaseDto Release, bool Latest, int Assets, long Downloads, TestSummaryDto? Tests);

/// <summary>The release hub: <c>/projects/{key}/releases</c> (every repository) and <c>…/repos/{slug}/releases/{id}/hub</c> (one release with everything around it).</summary>
public static class ReleaseHubEndpoints
{
    public static void MapReleaseHubEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/projects/{key}/releases", Overview).WithTags("Releases").WithName("ListProjectReleases").WithSummary("Releases of all repositories of the project")
            .WithDescription("Newest first, with asset and download counts and the newest test run for each release. Drafts only for Developer+.");
        var g = api.MapGroup("/projects/{key}/repos/{slug}").WithTags("Releases");
        g.MapGet("/releases/{id:int}/hub", GetHub).WithName("GetReleaseHub").WithSummary("A release with everything built from its tag")
            .WithDescription("Assets and download counts, CI pipelines and artifacts of the tagged commit, packages and images (published by those pipelines or versioned like the tag), " +
                             "the commit's security scan, SBOM files, linked issues (fix version or milestone named like the tag), merged pull requests since the previous release, test runs and the commit's signature.");
        g.MapGet("/releases/{id:int}/checksums", Checksums).WithName("GetReleaseChecksums").WithSummary("SHA-256 checksums of the release's assets (sha256sum format)");
        g.MapPost("/releases/{id:int}/attach-artifacts", Attach).WithName("AttachReleaseArtifacts").WithSummary("Copy matching CI artifacts onto the release (Developer+)")
            .WithDescription("Uses the repository's release settings patterns on the passed pipelines of the tagged commit.");
        g.MapGet("/release-settings", GetSettings).WithName("GetReleaseSettings").WithSummary("Auto-attach settings of the repository");
        g.MapPut("/release-settings", SaveSettings).WithName("SaveReleaseSettings").WithSummary("Set which CI artifact files are attached to releases (Maintainer+)")
            .WithDescription("patterns are globs matched against paths inside the artifact zip (dist/*.zip, **/*.nupkg).").WithJsonBody<SaveReleaseSettingsRequest>();
    }

    private static async Task<Repository> RepoAsync(RepositoryService repos, string key, string slug) =>
        await repos.GetRepositoryAsync(key, slug) ?? throw ApiException.NotFound($"Repository '{key.ToUpperInvariant()}/{slug}' not found.");

    private static async Task<List<ReleaseOverviewDto>> Overview(HttpContext http, IssueService issues, ReleaseHubService hub, PublicBaseUrl url, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        var b = url.For(http.Request);
        var rows = await hub.OverviewAsync(project.Id);
        return Paging.From(http.Request).Apply(http, rows, r =>
        {
            r.Release.Repository = r.Repository;
            return new ReleaseOverviewDto(r.Repository.Slug, ReleaseDtos.Release(r.Release, b), r.IsLatest, r.AssetCount, r.Downloads, r.Tests is { } t ? TestDtos.Summary(t) : null);
        });
    }

    private static async Task<ReleaseHubDto> GetHub(HttpContext http, RepositoryService repos, ReleaseHubService hubs, PublicBaseUrl url, string key, string slug, int id)
    {
        var repo = await RepoAsync(repos, key, slug);
        var h = await hubs.GetAsync(repo, id) ?? throw ApiException.NotFound($"Release {id} not found.");
        var b = url.For(http.Request);
        var rule = h.AutoAttach;
        return new ReleaseHubDto(ReleaseDtos.Release(h.Release, b), h.CommitSha, h.PreviousTag, h.CompareUrl == null ? null : b + h.CompareUrl, h.TagMissing,
            h.Signature == null ? null : new HubSignatureDto(h.Signature.Status.ToString().ToLowerInvariant(), h.Signature.Label, h.Signature.Format, h.Signature.Reason),
            h.Pipelines.Select(p => new HubPipelineDto(p.Id, p.Ref, p.Status.ToString().ToLowerInvariant(), Dto.Utc(p.CreatedAt),
                p.Artifacts.Select(a => new HubArtifactDto(a.Id, a.PipelineId, a.JobName, a.SizeBytes, a.FileCount, Dto.Utc(a.CreatedAt), Dto.Utc(a.ExpiresAt), a.Expired)).ToList())).ToList(),
            h.Packages.Select(p => new HubPackageDto(p.PackageId, p.Name, p.Type.ToString().ToLowerInvariant(), p.Version, p.ManifestDigest, p.Size, p.ByPipeline,
                b + Services.Packages.PackageUrls.Detail(repo.Project.Key, p.PackageId))).ToList(),
            h.Security == null ? null : new HubSecurityDto(h.Security.PipelineId, h.Security.Critical, h.Security.High, h.Security.Medium, h.Security.Low, h.Security.Other,
                h.Security.Dismissed, h.Security.Open, Dto.Utc(h.Security.ScannedAt)),
            h.Sboms.Select(a => ReleaseDtos.Asset(a, b)).ToList(),
            h.Issues.Select(i => new HubIssueDto(i.Number, $"{repo.Project.Key}-{i.Number}", i.Title, i.Status.ToString(), i.Via)).ToList(),
            h.PullRequests.Select(p => new HubPullRequestDto(p.Number, p.Title, p.Author)).ToList(),
            h.TestRuns.Select(r => new HubTestRunDto(r.Run.Number, r.Run.Name, r.Run.Label, TestDtos.Summary(r.Summary), $"{b}/p/{repo.Project.Key}/tests/runs/{r.Run.Number}")).ToList(),
            h.TotalDownloads, $"{b}/api/v1/projects/{repo.Project.Key}/repos/{repo.Slug}/releases/{id}/checksums",
            rule?.Enabled ?? false, ReleaseAutoAttachService.ParsePatterns(rule?.Patterns));
    }

    private static async Task<IResult> Checksums(HttpContext http, RepositoryService repos, ReleaseHubService hubs, string key, string slug, int id)
    {
        var repo = await RepoAsync(repos, key, slug);
        var text = await hubs.ChecksumsAsync(repo, id) ?? throw ApiException.NotFound($"Release {id} not found.");
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers.ContentDisposition = "attachment; filename=\"SHA256SUMS\"";
        return TypedResults.Text(text, "text/plain; charset=utf-8");
    }

    private static async Task<AttachArtifactsResultDto> Attach(RepositoryService repos, ReleaseHubService hubs, string key, string slug, int id)
    {
        var repo = await RepoAsync(repos, key, slug);
        var r = await hubs.AttachNowAsync(repo, id);
        return new AttachArtifactsResultDto(r.Attached, r.Names, r.Notes);
    }

    private static async Task<ReleaseSettingsDto> GetSettings(RepositoryService repos, ReleaseAutoAttachService attach, ProjectAccess access, string key, string slug)
    {
        var repo = await RepoAsync(repos, key, slug);
        await access.RequireAsync(repo.ProjectId, Permission.ViewProject);
        var rule = await attach.GetRuleAsync(repo.Id);
        return new ReleaseSettingsDto(rule?.Enabled ?? false, ReleaseAutoAttachService.ParsePatterns(rule?.Patterns));
    }

    private static async Task<ReleaseSettingsDto> SaveSettings(HttpContext http, RepositoryService repos, ReleaseHubService hubs, ReleaseAutoAttachService attach, string key, string slug)
    {
        var repo = await RepoAsync(repos, key, slug);
        var body = await ApiResults.ReadBodyAsync<SaveReleaseSettingsRequest>(http.Request);
        await hubs.SaveAutoAttachAsync(repo, body.AutoAttachEnabled, string.Join("\n", body.Patterns ?? []));
        var rule = await attach.GetRuleAsync(repo.Id);
        return new ReleaseSettingsDto(rule?.Enabled ?? false, ReleaseAutoAttachService.ParsePatterns(rule?.Patterns));
    }
}
