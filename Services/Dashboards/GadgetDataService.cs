using ByteBench.Data;
using ByteBench.Services.Explore;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Dashboards;

/// <summary>Issues behind an issue gadget, or why there are none (<see cref="Problem"/>).</summary>
public sealed record GadgetIssues(string? ProjectKey, string? ProjectName, string? FilterName, string Query, List<Issue> Issues, string? Problem)
{
    /// <summary>The same issues on the issue list.</summary>
    public string? ListUrl => ProjectKey == null ? null : $"/p/{ProjectKey}/issues" + (Query.Length > 0 ? "?" + Query : "");
}

public sealed record GadgetPipelines(Repository Repository, List<Pipeline> Pipelines);

/// <summary>
/// Data for dashboard gadgets, always loaded as the acting user (the viewer, not the dashboard's owner) through the
/// services' <see cref="ProjectAccess"/> checks, so shared dashboards only ever show what the viewer could see anyway.
/// </summary>
public class GadgetDataService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, IssueService issues,
    SavedFilterService filters, SprintService sprints, UserActivityService feed)
{
    public const int MaxIssues = 500;

    /// <summary>The issues for a filter-results or chart gadget.</summary>
    public async Task<GadgetIssues> IssuesAsync(GadgetConfig c)
    {
        if (c.ProjectId is not { } pid) return new(null, null, null, "", [], "Choose a project in the gadget's settings.");
        var project = await ProjectAsync(pid);
        if (project == null) return new(null, null, null, "", [], "This project isn't available to you.");
        string? filterName = null;
        var query = c.Query ?? "";
        if (c.SavedFilterId is { } fid)
        {
            var f = (await filters.ListAsync(pid)).FirstOrDefault(x => x.Id == fid);
            if (f == null) return new(project.Key, project.Name, null, "", [], "This saved filter isn't available to you.");
            (filterName, query) = (f.Name, f.Query);
        }
        var filter = IssueFilterQuery.Parse(query);
        var list = await issues.GetIssuesAsync(pid, filter);
        if (list.Count > MaxIssues) list = list.Take(MaxIssues).ToList();
        return new(project.Key, project.Name, filterName, IssueFilterQuery.ToQueryString(filter), list, null);
    }

    public async Task<(List<ChartSlice> Slices, GadgetIssues Source)> ChartAsync(GadgetConfig c)
    {
        var source = await IssuesAsync(c);
        return (GadgetMath.CountBy(source.Issues, c.FieldOrDefault), source);
    }

    /// <summary>Created vs resolved per day in one project, or in every project the viewer can see.</summary>
    public async Task<List<CreatedResolvedDay>> CreatedVsResolvedAsync(GadgetConfig c, DateTime? today = null)
    {
        var visible = await access.VisibleProjectIdsAsync();
        var ids = c.ProjectId is { } p ? visible.Where(v => v == p).ToList() : visible.ToList();
        var now = (today ?? DateTime.UtcNow).Date;
        var days = c.DaysOrDefault;
        var from = now.AddDays(-(days - 1));
        if (ids.Count == 0) return GadgetMath.CreatedVsResolved([], now, days);
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.Issues.Where(i => ids.Contains(i.ProjectId) && (i.CreatedAt >= from || (i.ClosedAt != null && i.ClosedAt >= from)))
            .Select(i => new { i.CreatedAt, i.ClosedAt }).ToListAsync();
        return GadgetMath.CreatedVsResolved(rows.Select(r => (r.CreatedAt, r.ClosedAt)), now, days);
    }

    /// <summary>The configured sprint's burndown, or the project's active sprint's.</summary>
    public async Task<(Project? Project, Burndown? Burndown)> BurndownAsync(GadgetConfig c)
    {
        if (c.ProjectId is not { } pid || await ProjectAsync(pid) is not { } project) return (null, null);
        var sprintId = c.SprintId ?? (await sprints.GetActiveAsync(pid))?.Id;
        return (project, sprintId is { } s ? await sprints.GetBurndownAsync(pid, s) : null);
    }

    public Task<List<FeedItem>> ActivityAsync(GadgetConfig c) =>
        feed.GetFeedAsync(projectIds: c.ProjectId is { } p ? [p] : null, take: c.LimitOrDefault);

    public async Task<List<Issue>> AssignedToMeAsync(GadgetConfig c)
    {
        var uid = await access.ResolveUserIdAsync();
        return uid == null ? [] : (await issues.GetAssignedToAsync(uid.Value)).Take(c.LimitOrDefault).ToList();
    }

    /// <summary>Open pull requests in visible projects where the viewer is a reviewer and hasn't reviewed yet.</summary>
    public async Task<List<PullRequest>> ReviewRequestsAsync(GadgetConfig c)
    {
        var uid = await access.ResolveUserIdAsync();
        if (uid == null) return [];
        var visible = await access.VisibleProjectIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.PullRequestReviewers.Where(r => r.UserId == uid)
            .Join(db.PullRequests, r => r.PullRequestId, p => p.Id, (r, p) => p)
            .Where(p => visible.Contains(p.Repository.ProjectId) && p.State == PullRequestState.Open && !p.IsDraft
                        && !p.Reviews.Any(v => v.UserId == uid))
            .Include(p => p.Repository).ThenInclude(r => r.Project).Include(p => p.Author)
            .OrderByDescending(p => p.UpdatedAt).Take(c.LimitOrDefault).AsNoTracking().ToListAsync();
    }

    /// <summary>The repository's latest pipelines, or null when it doesn't exist or the viewer can't see it.</summary>
    public async Task<GadgetPipelines?> PipelinesAsync(GadgetConfig c)
    {
        if (c.RepositoryId is not { } rid) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        var repo = await db.Repositories.AsNoTracking().Include(r => r.Project).FirstOrDefaultAsync(r => r.Id == rid);
        if (repo == null || !await access.CanAsync(repo.ProjectId, Permission.ViewProject)) return null;
        var list = await db.Pipelines.AsNoTracking().Where(p => p.RepositoryId == rid).OrderByDescending(p => p.Id)
            .Take(c.LimitOrDefault).Include(p => p.TriggeredBy).ToListAsync();
        return new(repo, list);
    }

    /// <summary>Repositories in projects the viewer can see (for the pipeline gadget's picker).</summary>
    public async Task<List<Repository>> RepositoriesAsync()
    {
        var visible = await access.VisibleProjectIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Repositories.AsNoTracking().Include(r => r.Project).Where(r => visible.Contains(r.ProjectId))
            .OrderBy(r => r.Project.Key).ThenBy(r => r.Name).ToListAsync();
    }

    private async Task<Project?> ProjectAsync(int projectId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId);
    }
}
