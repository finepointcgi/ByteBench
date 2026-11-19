using ByteBench.Data;
using ByteBench.Services.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Pages;

/// <summary>What the settings page edits.</summary>
public sealed record PagesSettingsInput(bool Enabled, PagesVisibility Visibility, PagesNotFoundMode NotFound, bool ReviewSites, int ReviewExpireDays);

/// <summary>A repository's site as the settings page shows it. <see cref="Site"/> is a default (Id 0) until the first deployment or save.</summary>
public sealed record PagesSiteInfo(PagesSite Site, List<PagesDeployment> Deployments, string? MainUrl, bool PagesEnabledOnServer, bool PublicAllowed, bool HostMode)
{
    public bool Exists => Site.Id != 0;
    public PagesDeployment? Current => Deployments.FirstOrDefault(d => d.Slot == "" && d.IsCurrent);
    public IEnumerable<PagesDeployment> ReviewSites => Deployments.Where(d => d.Slot != "" && d.IsCurrent);
}

/// <summary>
/// Pages settings, history, rollback and deletion for the UI. Viewing needs ViewProject; changing settings, rolling back and
/// deleting need ManageRepositories (Maintainer); making a site <b>public</b> needs ManageProjectSettings (Owner), because it
/// publishes the repository's build output to people outside the project.
/// </summary>
public class PagesService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, PagesDeployer deployer, PagesLinks links,
    IOptionsMonitor<PagesOptions> options, AuditLog audit)
{
    public const int MaxHistory = 100;

    public async Task<PagesSiteInfo> GetAsync(Repository repo)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ViewProject);
        await using var db = await dbFactory.CreateDbContextAsync();
        var site = await db.PagesSites.AsNoTracking().FirstOrDefaultAsync(s => s.RepositoryId == repo.Id) ?? new PagesSite { RepositoryId = repo.Id };
        var deployments = site.Id == 0 ? [] : await db.PagesDeployments.AsNoTracking().Include(d => d.DeployedBy)
            .Where(d => d.SiteId == site.Id).OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id).Take(MaxHistory).ToListAsync();
        var o = options.CurrentValue;
        var live = deployments.Any(d => d.Slot == "" && d.IsCurrent);
        return new PagesSiteInfo(site, deployments, live && site.Enabled ? links.SiteUrl(repo) : null, o.Enabled, o.AllowPublic, o.Domain != null);
    }

    /// <summary>The URL of the live main site for the repository page's link, or null when there is none (or the user can't see it).</summary>
    public async Task<string?> LiveUrlAsync(Repository repo)
    {
        if (!options.CurrentValue.Enabled || !await access.CanAsync(repo.ProjectId, Permission.ViewProject)) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        var live = await db.PagesSites.AsNoTracking().AnyAsync(s => s.RepositoryId == repo.Id && s.Enabled
            && s.Deployments.Any(d => d.Slot == "" && d.IsCurrent));
        return live ? links.SiteUrl(repo) : null;
    }

    public async Task<PagesSite> UpdateSettingsAsync(Repository repo, PagesSettingsInput input)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        var o = options.CurrentValue;
        if (input.ReviewExpireDays is < 1 or > 365) throw new InvalidOperationException("Review sites expire after 1 to 365 days.");
        if (!Enum.IsDefined(input.Visibility) || !Enum.IsDefined(input.NotFound)) throw new InvalidOperationException("Unknown setting.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var site = await db.PagesSites.FirstOrDefaultAsync(s => s.RepositoryId == repo.Id);
        var created = site == null;
        site ??= db.PagesSites.Add(new PagesSite { RepositoryId = repo.Id }).Entity;
        if (input.Visibility == PagesVisibility.Public && site.Visibility != PagesVisibility.Public)
        {
            if (!o.AllowPublic) throw new InvalidOperationException("This server doesn't allow public sites.");
            await access.RequireAsync(repo.ProjectId, Permission.ManageProjectSettings);
        }
        var before = new { site.Enabled, visibility = site.Visibility.ToString(), notFound = site.NotFound.ToString(), site.ReviewSites, site.ReviewExpireDays };
        site.Enabled = input.Enabled;
        site.Visibility = input.Visibility;
        site.NotFound = input.NotFound;
        site.ReviewSites = input.ReviewSites;
        site.ReviewExpireDays = input.ReviewExpireDays;
        site.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync("pages.settings_updated", "repository", repo.Id, repo.ProjectId, new
        {
            repository = repo.Slug, created, before = created ? null : before,
            after = new { site.Enabled, visibility = site.Visibility.ToString(), notFound = site.NotFound.ToString(), site.ReviewSites, site.ReviewExpireDays },
        });
        return site;
    }

    public async Task RollbackAsync(Repository repo, int deploymentId)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        int siteId;
        await using (var db = await dbFactory.CreateDbContextAsync())
            siteId = await db.PagesSites.Where(s => s.RepositoryId == repo.Id).Select(s => s.Id).FirstOrDefaultAsync();
        if (siteId == 0) throw new InvalidOperationException("The repository has no site.");
        var userId = await access.ResolveUserIdAsync();
        await deployer.RollbackAsync(siteId, deploymentId, userId); // audit and webhook come from the deployer
    }

    /// <summary>Deletes the site's content: everything, or one review site. Settings and the site's URL stay.</summary>
    public async Task DeleteContentAsync(Repository repo, string? slot = null)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        int siteId;
        await using (var db = await dbFactory.CreateDbContextAsync())
            siteId = await db.PagesSites.Where(s => s.RepositoryId == repo.Id).Select(s => s.Id).FirstOrDefaultAsync();
        if (siteId == 0) return;
        var n = await deployer.DeleteContentAsync(siteId, slot);
        await audit.RecordAsync("pages.deleted", "repository", repo.Id, repo.ProjectId, new { repository = repo.Slug, slot = slot ?? "(all)", versions = n });
    }
}
