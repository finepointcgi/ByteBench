using GitBench.Data;
using GitBench.Services;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Api;

/// <summary>A group (namespace). <c>Role</c> is the caller's role in it, inherited from parent groups too (null if none).</summary>
public record GroupDto(int Id, string Name, string Path, string FullPath, string? Description, ProjectVisibility Visibility, string Color,
    int? ParentId, DateTimeOffset CreatedAt, ProjectRole? Role, string WebUrl);

/// <summary>
/// /api/v1/groups… (read-only). A group is addressed by its id or its URL-encoded full path (<c>acme%2Fplatform</c>);
/// old paths of renamed or moved groups still resolve. Groups the caller can't view are 404.
/// </summary>
public static class GroupEndpoints
{
    public static void MapGroupEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/groups").WithTags("Groups");
        g.MapGet("", ListGroups).WithName("ListGroups").WithSummary("List groups you can see")
            .WithDescription("Ordered by full path. Filter: q (name or path contains).");
        g.MapGet("/{group}", GetGroup).WithName("GetGroup").WithSummary("Get a group")
            .WithDescription("{group} is the id or the URL-encoded full path, e.g. acme%2Fplatform.");
        g.MapGet("/{group}/subgroups", ListSubgroups).WithName("ListSubgroups").WithSummary("List a group's direct subgroups");
        g.MapGet("/{group}/projects", ListGroupProjects).WithName("ListGroupProjects").WithSummary("List a group's projects")
            .WithDescription("Projects directly in the group; includeSubgroups=true adds those in all its subgroups.");
        g.MapGet("/{group}/issues", ListGroupIssues).WithName("ListGroupIssues").WithSummary("List issues across a group")
            .WithDescription("Issues in every project of the group and its subgroups that you can view. Filters: status (open [default], closed, all), q (text or issue key), sort (updated [default], created, priority, due).");
    }

    private static GroupDto ToDto(Group g, ProjectRole? role, string baseUrl) =>
        new(g.Id, g.Name, g.Path, g.FullPath, g.Description, g.Visibility, g.Color, g.ParentId,
            new DateTimeOffset(DateTime.SpecifyKind(g.CreatedAt, DateTimeKind.Utc)), role, $"{baseUrl}/g/{g.FullPath}");

    /// <summary>The group named by id or (encoded) path, if the caller can view it.</summary>
    internal static async Task<Group> RequireGroupAsync(GroupService groups, string idOrPath)
    {
        var raw = Uri.UnescapeDataString(idOrPath);
        Group? group;
        if (int.TryParse(raw, out var id)) group = await groups.GetAsync(id);
        else
        {
            var found = await groups.FindAsync(raw);
            group = found.Group ?? (found.RedirectTo is { } to ? (await groups.FindAsync(to)).Group : null);
        }
        return group ?? throw ApiException.NotFound($"Group '{raw}' not found.");
    }

    private static async Task<List<GroupDto>> ListGroups(HttpContext http, GroupService groups, ProjectAccess access, IDbContextFactory<AppDbContext> dbFactory,
        PublicBaseUrl url, string? q)
    {
        var paging = Paging.From(http.Request);
        var visible = await access.VisibleGroupIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var query = db.Groups.AsNoTracking().Where(g => visible.Contains(g.Id));
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(g => g.Name.Contains(q) || g.FullPath.Contains(q.ToLower()));
        var all = await query.OrderBy(g => g.FullPath).ToListAsync();
        paging.WriteHeaders(http, all.Count);
        var page = all.Skip(paging.Skip).Take(paging.PerPage).ToList();
        var roles = await RolesAsync(db, access, page);
        var baseUrl = url.For(http.Request);
        return page.Select(g => ToDto(g, roles.GetValueOrDefault(g.Id), baseUrl)).ToList();
    }

    /// <summary>The caller's role in each group (inherited included), from one membership query.</summary>
    private static async Task<Dictionary<int, ProjectRole?>> RolesAsync(AppDbContext db, ProjectAccess access, List<Group> page)
    {
        if (await access.IsAdminAsync()) return page.ToDictionary(g => g.Id, _ => (ProjectRole?)ProjectRole.Owner);
        var uid = await access.ResolveUserIdAsync();
        var memberships = await db.GroupMembers.Where(m => m.UserId == uid).Select(m => new { m.Group.FullPath, m.Role }).ToListAsync();
        return page.ToDictionary(g => g.Id, g => memberships
            .Where(m => g.FullPath == m.FullPath || g.FullPath.StartsWith(m.FullPath + "/"))
            .Select(m => (ProjectRole?)m.Role).Max());
    }

    private static async Task<GroupDto> GetGroup(HttpContext http, GroupService groups, ProjectAccess access, PublicBaseUrl url, string group)
    {
        var g = await RequireGroupAsync(groups, group);
        return ToDto(g, await access.GetGroupRoleAsync(g.Id), url.For(http.Request));
    }

    private static async Task<List<GroupDto>> ListSubgroups(HttpContext http, GroupService groups, ProjectAccess access, IDbContextFactory<AppDbContext> dbFactory,
        PublicBaseUrl url, string group)
    {
        var g = await RequireGroupAsync(groups, group);
        var paging = Paging.From(http.Request);
        var visible = await access.VisibleGroupIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var all = await db.Groups.AsNoTracking().Where(x => x.ParentId == g.Id && visible.Contains(x.Id)).OrderBy(x => x.Name).ToListAsync();
        paging.WriteHeaders(http, all.Count);
        var page = all.Skip(paging.Skip).Take(paging.PerPage).ToList();
        var roles = await RolesAsync(db, access, page);
        var baseUrl = url.For(http.Request);
        return page.Select(x => ToDto(x, roles.GetValueOrDefault(x.Id), baseUrl)).ToList();
    }

    private static async Task<List<ProjectDto>> ListGroupProjects(HttpContext http, GroupService groups, ProjectAccess access, IDbContextFactory<AppDbContext> dbFactory,
        PublicBaseUrl url, string group, bool? includeSubgroups)
    {
        var g = await RequireGroupAsync(groups, group);
        var paging = Paging.From(http.Request);
        var ids = await groups.ProjectIdsAsync(g.Id);
        await using var db = await dbFactory.CreateDbContextAsync();
        var query = db.Projects.AsNoTracking().Where(p => ids.Contains(p.Id));
        if (includeSubgroups != true) query = query.Where(p => p.GroupId == g.Id);
        var projects = await query.OrderBy(p => p.Key).ToListAsync();
        paging.WriteHeaders(http, projects.Count);
        var baseUrl = url.For(http.Request);
        var result = new List<ProjectDto>();
        foreach (var p in projects.Skip(paging.Skip).Take(paging.PerPage)) result.Add(Dto.Project(p, await access.GetRoleAsync(p.Id), baseUrl));
        return result;
    }

    private static async Task<List<IssueDto>> ListGroupIssues(HttpContext http, GroupService groups, IDbContextFactory<AppDbContext> dbFactory, PublicBaseUrl url,
        string group, string? status, string? q, string? sort)
    {
        var g = await RequireGroupAsync(groups, group);
        var paging = Paging.From(http.Request);
        var filter = new IssueFilter { Search = q, Sort = sort ?? "updated" };
        filter.Open = status?.ToLowerInvariant() switch
        {
            null or "" or "open" => true,
            "closed" => false,
            "all" => null,
            _ => throw ApiException.BadRequest("status must be open, closed or all."),
        };
        var list = await groups.GetIssuesAsync(g.Id, filter);
        paging.WriteHeaders(http, list.Count);
        var page = list.Skip(paging.Skip).Take(paging.PerPage).ToList();
        var reporterIds = page.Select(i => i.ReporterId).Distinct().ToList();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var reporters = await db.Users.Where(u => reporterIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id);
            foreach (var i in page) i.Reporter = reporters[i.ReporterId];
        }
        var baseUrl = url.For(http.Request);
        return page.Select(i => Dto.Issue(i, baseUrl)).ToList();
    }
}
