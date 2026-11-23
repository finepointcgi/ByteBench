using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Pages;

namespace ByteBench.Api;

/// <param name="Slot">Empty for the main site; the review site's name otherwise.</param>
public record PagesDeploymentDto(int Id, string Slot, int Version, string Ref, string CommitSha, int? PipelineId, UserDto? DeployedBy, int FileCount,
    long SizeBytes, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, bool Current, bool IsRollback, DateTimeOffset? ActivatedAt, string Url);

public record PagesSettingsDto(bool Enabled, PagesVisibility Visibility, PagesNotFoundMode NotFound, bool ReviewSites, int ReviewExpireDays);

/// <summary>A repository's site. <c>url</c> is the live main site (null while it has no deployment or is disabled).</summary>
public record PagesSiteDto(bool Exists, PagesSettingsDto Settings, bool ServerEnabled, bool PublicAllowed, string? Url,
    PagesDeploymentDto? Current, IReadOnlyList<PagesDeploymentDto> ReviewSites);

/// <summary>PUT body: omitted fields keep their current value. Making a site <c>public</c> needs an Owner.</summary>
public record PagesSettingsRequest(bool? Enabled = null, PagesVisibility? Visibility = null, PagesNotFoundMode? NotFound = null,
    bool? ReviewSites = null, int? ReviewExpireDays = null);

/// <summary>Pages sites: status, deployment history, rollback, deleting the content, and settings.</summary>
public static class PagesEndpoints
{
    public static void MapPagesEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/projects/{key}/repos/{slug}/pages").WithTags("Pages");
        g.MapGet("", GetSite).WithName("GetPagesSite").WithSummary("The repository's site: settings, live deployment, review sites, URLs")
            .WithDescription("Sites are published by CI jobs with a pages section; there is no upload endpoint.");
        g.MapDelete("", DeleteSite).WithName("DeletePagesSite").WithSummary("Delete the site's content")
            .WithDescription("Needs Maintainer+. Deletes every deployment and its files (or only one review site with ?slot=). The settings and the site's URL stay. Audited.");
        g.MapGet("/deployments", ListDeployments).WithName("ListPagesDeployments").WithSummary("Deployment history, newest first")
            .WithDescription("The last 100 versions. ?slot= limits it to one review site (an empty value is the main site).");
        g.MapPost("/deployments/{id:int}/rollback", Rollback).WithName("RollbackPagesDeployment").WithSummary("Make an earlier deployment live again")
            .WithDescription("Needs Maintainer+. Instant (the files are still on disk). 422 if it is already live, expired or its files are gone. Sends the pages.deployed webhook.");
        g.MapGet("/settings", GetSettings).WithName("GetPagesSettings").WithSummary("The site's settings").WithDescription("Needs Maintainer+.");
        g.MapPut("/settings", PutSettings).WithName("SetPagesSettings").WithSummary("Change the site's settings")
            .WithDescription("Needs Maintainer+; making the site public (visibility: public) needs an Owner and a server that allows public sites. Audited.")
            .WithJsonBody<PagesSettingsRequest>();
    }

    private static async Task<Repository> RequireRepoAsync(RepositoryService repos, string key, string slug) =>
        await repos.GetRepositoryAsync(key, slug) ?? throw ApiException.NotFound($"Repository '{key.ToUpperInvariant()}/{slug}' not found.");

    private static PagesSettingsDto Settings(PagesSite s) => new(s.Enabled, s.Visibility, s.NotFound, s.ReviewSites, s.ReviewExpireDays);

    private static PagesDeploymentDto Deployment(Repository repo, PagesDeployment d, PagesLinks links) => new(
        d.Id, d.Slot, d.Version, d.Ref, d.CommitSha, d.PipelineId, Dto.UserOrNull(d.DeployedBy), d.FileCount, d.SizeBytes, Dto.Utc(d.CreatedAt),
        Dto.Utc(d.ExpiresAt), d.IsCurrent, d.IsRollback, Dto.Utc(d.ActivatedAt), links.SiteUrl(repo, d.Slot));

    private static async Task<PagesSiteDto> SiteAsync(PagesService pages, PagesLinks links, Repository repo)
    {
        var info = await pages.GetAsync(repo);
        return new PagesSiteDto(info.Exists, Settings(info.Site), info.PagesEnabledOnServer, info.PublicAllowed, info.MainUrl,
            info.Current is { } c ? Deployment(repo, c, links) : null, info.ReviewSites.Select(d => Deployment(repo, d, links)).ToList());
    }

    private static async Task<PagesSiteDto> GetSite(RepositoryService repos, PagesService pages, PagesLinks links, string key, string slug) =>
        await SiteAsync(pages, links, await RequireRepoAsync(repos, key, slug));

    private static async Task<List<PagesDeploymentDto>> ListDeployments(HttpContext http, RepositoryService repos, PagesService pages, PagesLinks links, string key, string slug)
    {
        var paging = Paging.From(http.Request);
        var repo = await RequireRepoAsync(repos, key, slug);
        var all = (await pages.GetAsync(repo)).Deployments;
        if (http.Request.Query.TryGetValue("slot", out var slot)) all = all.Where(d => d.Slot == slot.ToString()).ToList();
        return paging.Apply(http, all, d => Deployment(repo, d, links));
    }

    private static async Task<PagesSiteDto> Rollback(RepositoryService repos, PagesService pages, PagesLinks links, string key, string slug, int id)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        if ((await pages.GetAsync(repo)).Deployments.All(d => d.Id != id)) throw ApiException.NotFound($"Deployment {id} not found.");
        await pages.RollbackAsync(repo, id);
        return await SiteAsync(pages, links, repo);
    }

    private static async Task<IResult> DeleteSite(HttpContext http, RepositoryService repos, PagesService pages, string key, string slug)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        var slot = http.Request.Query.TryGetValue("slot", out var s) ? s.ToString() : null;
        if (slot is { Length: > 0 } && (await pages.GetAsync(repo)).Deployments.All(d => d.Slot != slot))
            throw ApiException.NotFound($"Review site '{slot}' not found.");
        await pages.DeleteContentAsync(repo, string.IsNullOrEmpty(slot) ? null : slot);
        return TypedResults.NoContent();
    }

    private static async Task<PagesSettingsDto> GetSettings(RepositoryService repos, PagesService pages, ProjectAccess access, string key, string slug)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        return Settings((await pages.GetAsync(repo)).Site);
    }

    private static async Task<PagesSettingsDto> PutSettings(HttpContext http, RepositoryService repos, PagesService pages, string key, string slug)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        var body = await ApiResults.ReadBodyAsync<PagesSettingsRequest>(http.Request);
        var cur = (await pages.GetAsync(repo)).Site;
        var site = await pages.UpdateSettingsAsync(repo, new PagesSettingsInput(body.Enabled ?? cur.Enabled, body.Visibility ?? cur.Visibility,
            body.NotFound ?? cur.NotFound, body.ReviewSites ?? cur.ReviewSites, body.ReviewExpireDays ?? cur.ReviewExpireDays));
        return Settings(site);
    }
}
