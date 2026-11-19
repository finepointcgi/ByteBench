using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Explore;

public enum ExploreSort { Name, Active, Stars }
public enum ExploreTab { All, Starred, Mine }

/// <summary>A project card on /explore.</summary>
public sealed record ExploreProject(int Id, string Key, string Name, string? Description, ProjectVisibility Visibility,
    int Stars, bool Starred, bool Member, int OpenIssues, int Repositories, DateTime LastActivityAt);

/// <summary>/explore: every project the acting user can view, with star counts and last activity.</summary>
public class ExploreService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access)
{
    public async Task<List<ExploreProject>> ListAsync(string? query = null, ExploreSort sort = ExploreSort.Name, ExploreTab tab = ExploreTab.All)
    {
        var uid = await access.ResolveUserIdAsync();
        var visible = await access.VisibleProjectIdsAsync();
        if (visible.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync();

        var q = db.Projects.AsNoTracking().Where(p => visible.Contains(p.Id));
        if (!string.IsNullOrWhiteSpace(query))
        {
            var like = Like.Contains(query.Trim());
            q = q.Where(p => EF.Functions.Like(p.Key, like, Like.Escape) || EF.Functions.Like(p.Name, like, Like.Escape)
                             || (p.Description != null && EF.Functions.Like(p.Description, like, Like.Escape)));
        }
        if (tab == ExploreTab.Starred) q = q.Where(p => db.ProjectStars.Any(s => s.ProjectId == p.Id && s.UserId == uid));
        if (tab == ExploreTab.Mine) q = q.Where(p => p.Members.Any(m => m.UserId == uid));

        var rows = await q.Select(p => new
        {
            p.Id, p.Key, p.Name, p.Description, p.Visibility, p.CreatedAt,
            Stars = db.ProjectStars.Count(s => s.ProjectId == p.Id),
            Starred = db.ProjectStars.Any(s => s.ProjectId == p.Id && s.UserId == uid),
            Member = p.Members.Any(m => m.UserId == uid),
            Open = p.Issues.Count(i => i.Status != IssueStatus.Done),
            Repos = db.Repositories.Count(r => r.ProjectId == p.Id),
            IssueAt = p.Issues.Max(i => (DateTime?)i.UpdatedAt),
            PrAt = db.PullRequests.Where(x => x.Repository.ProjectId == p.Id).Max(x => (DateTime?)x.UpdatedAt),
            PushAt = db.PushEvents.Where(x => x.ProjectId == p.Id).Max(x => (DateTime?)x.CreatedAt),
        }).ToListAsync();

        var list = rows.Select(r => new ExploreProject(r.Id, r.Key, r.Name, r.Description, r.Visibility, r.Stars, r.Starred, r.Member,
            r.Open, r.Repos, Latest(r.CreatedAt, r.IssueAt, r.PrAt, r.PushAt))).ToList();

        return sort switch
        {
            ExploreSort.Active => list.OrderByDescending(p => p.LastActivityAt).ThenBy(p => p.Name).ToList(),
            ExploreSort.Stars => list.OrderByDescending(p => p.Stars).ThenBy(p => p.Name).ToList(),
            _ => list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    private static DateTime Latest(DateTime first, params DateTime?[] others) =>
        others.Aggregate(first, (max, d) => d is { } x && x > max ? x : max);
}

/// <summary>SQL LIKE patterns with user text escaped (<c>%</c>, <c>_</c> and the escape character match literally).</summary>
public static class Like
{
    public const string Escape = "\\";

    public static string Contains(string text) => "%" + Quote(text) + "%";

    public static string Quote(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
