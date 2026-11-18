using System.Text.RegularExpressions;
using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Explore;

public enum FeedKind { IssueOpened, IssueClosed, IssueComment, PullRequestOpened, PullRequestMerged, PullRequestComment, Push }

/// <summary>One entry in an activity feed (profile or dashboard). <see cref="Title"/> is plain text; <see cref="Url"/> is app-relative.</summary>
public sealed record FeedItem(FeedKind Kind, DateTime At, User Actor, string ProjectKey, string ProjectName, string Title, string Url, string? Detail = null);

/// <summary>A public profile as the acting user sees it.</summary>
public sealed record UserProfile(User User, DateTime? MemberSince, IReadOnlyList<ProfileProject> Projects);

public sealed record ProfileProject(int Id, string Key, string Name, string? Description, ProjectRole Role);

/// <summary>
/// Profiles, activity feeds and contribution calendars, built from issues, issue activity and comments, pull requests and
/// their comments, and <see cref="PushEvent"/>s. Everything is filtered to projects the acting user can view, so a
/// profile never reveals work in a project the viewer can't see.
/// </summary>
public partial class UserActivityService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access)
{
    public const int CalendarDays = 364; // 52 weeks

    /// <summary>The user with this username (case-insensitive) and the projects they're a member of that the viewer can see.</summary>
    public async Task<UserProfile?> GetProfileAsync(string userName)
    {
        if (await access.ResolveUserIdAsync() == null) return null;
        var visible = await access.VisibleProjectIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var normalized = userName.ToUpperInvariant();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedUserName == normalized);
        if (user == null) return null;
        var memberships = await db.ProjectMembers.AsNoTracking().Where(m => m.UserId == user.Id)
            .Select(m => new { m.ProjectId, m.Project.Key, m.Project.Name, m.Project.Description, m.Role, m.CreatedAt })
            .ToListAsync();
        var projects = memberships.Where(m => visible.Contains(m.ProjectId)).OrderBy(m => m.Name)
            .Select(m => new ProfileProject(m.ProjectId, m.Key, m.Name, m.Description, m.Role)).ToList();
        // Accounts created before CreatedAt was recorded: fall back to their earliest membership (any project; only a date leaks).
        var since = user.CreatedAt ?? memberships.Select(m => (DateTime?)m.CreatedAt).Min();
        return new UserProfile(user, since, projects);
    }

    /// <summary>
    /// The newest feed items: by <paramref name="actorId"/> when given (a profile), else by anyone. Only projects the
    /// acting user can view, narrowed to <paramref name="projectIds"/> when given (e.g. the dashboard's own projects).
    /// </summary>
    public async Task<List<FeedItem>> GetFeedAsync(int? actorId = null, IReadOnlyCollection<int>? projectIds = null, int take = 30)
    {
        var scope = await ScopeAsync(projectIds);
        if (scope.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var items = new List<FeedItem>();

        // Issues opened.
        var issues = db.Issues.AsNoTracking().Where(i => scope.Contains(i.ProjectId));
        if (actorId != null) issues = issues.Where(i => i.ReporterId == actorId);
        items.AddRange((await issues.OrderByDescending(i => i.CreatedAt).Take(take)
                .Select(i => new { i.CreatedAt, i.Reporter, i.Project.Key, i.Project.Name, i.Number, i.Title }).ToListAsync())
            .Select(x => new FeedItem(FeedKind.IssueOpened, x.CreatedAt, x.Reporter, x.Key, x.Name,
                $"{x.Key}-{x.Number} {x.Title}", $"/p/{x.Key}/issues/{x.Number}")));

        // Issues closed: status changes into a Done-category status.
        var closes = await ClosingActivities(db, scope, actorId).OrderByDescending(a => a.CreatedAt).Take(take * 4)
            .Select(a => new { a.CreatedAt, a.Actor, a.Text, a.Issue.ProjectId, a.Issue.Project.Key, a.Issue.Project.Name, a.Issue.Number, a.Issue.Title })
            .ToListAsync();
        var done = await DoneStatusNamesAsync(db, closes.Select(c => c.ProjectId).Distinct().ToList());
        items.AddRange(closes.Where(c => IsClose(c.Text, c.ProjectId, done)).Take(take)
            .Select(x => new FeedItem(FeedKind.IssueClosed, x.CreatedAt, x.Actor, x.Key, x.Name,
                $"{x.Key}-{x.Number} {x.Title}", $"/p/{x.Key}/issues/{x.Number}")));

        // Issue comments.
        var comments = from c in db.Comments.AsNoTracking()
                       join i in db.Issues on c.IssueId equals i.Id
                       where scope.Contains(i.ProjectId)
                       select new { c, i };
        if (actorId != null) comments = comments.Where(x => x.c.AuthorId == actorId);
        items.AddRange((await comments.OrderByDescending(x => x.c.CreatedAt).Take(take)
                .Select(x => new { x.c.CreatedAt, x.c.Author, x.c.Body, x.i.Project.Key, x.i.Project.Name, x.i.Number, x.i.Title }).ToListAsync())
            .Select(x => new FeedItem(FeedKind.IssueComment, x.CreatedAt, x.Author, x.Key, x.Name,
                $"{x.Key}-{x.Number} {x.Title}", $"/p/{x.Key}/issues/{x.Number}", Excerpt(x.Body))));

        // Pull requests opened and merged.
        var prs = db.PullRequests.AsNoTracking().Where(p => scope.Contains(p.Repository.ProjectId));
        items.AddRange((await (actorId != null ? prs.Where(p => p.AuthorId == actorId) : prs).OrderByDescending(p => p.CreatedAt).Take(take)
                .Select(p => new { p.CreatedAt, p.Author, p.Repository.Project.Key, p.Repository.Project.Name, p.Repository.Slug, p.Number, p.Title }).ToListAsync())
            .Select(x => new FeedItem(FeedKind.PullRequestOpened, x.CreatedAt, x.Author, x.Key, x.Name,
                $"{x.Slug}#{x.Number} {x.Title}", $"/p/{x.Key}/repos/{x.Slug}/pull-requests/{x.Number}")));
        var merged = prs.Where(p => p.State == PullRequestState.Merged && p.MergedById != null && p.ClosedAt != null);
        if (actorId != null) merged = merged.Where(p => p.MergedById == actorId);
        items.AddRange((await merged.OrderByDescending(p => p.ClosedAt).Take(take)
                .Select(p => new { ClosedAt = p.ClosedAt!.Value, MergedBy = p.MergedBy!, p.Repository.Project.Key, p.Repository.Project.Name, p.Repository.Slug, p.Number, p.Title, p.TargetBranch }).ToListAsync())
            .Select(x => new FeedItem(FeedKind.PullRequestMerged, x.ClosedAt, x.MergedBy, x.Key, x.Name,
                $"{x.Slug}#{x.Number} {x.Title}", $"/p/{x.Key}/repos/{x.Slug}/pull-requests/{x.Number}", $"into {x.TargetBranch}")));

        // Pull request comments.
        var prComments = from c in db.PullRequestComments.AsNoTracking()
                         join p in db.PullRequests on c.PullRequestId equals p.Id
                         where scope.Contains(p.Repository.ProjectId)
                         select new { c, p };
        if (actorId != null) prComments = prComments.Where(x => x.c.AuthorId == actorId);
        items.AddRange((await prComments.OrderByDescending(x => x.c.CreatedAt).Take(take)
                .Select(x => new { x.c.CreatedAt, x.c.Author, x.c.Body, x.p.Repository.Project.Key, x.p.Repository.Project.Name, x.p.Repository.Slug, x.p.Number, x.p.Title }).ToListAsync())
            .Select(x => new FeedItem(FeedKind.PullRequestComment, x.CreatedAt, x.Author, x.Key, x.Name,
                $"{x.Slug}#{x.Number} {x.Title}", $"/p/{x.Key}/repos/{x.Slug}/pull-requests/{x.Number}", Excerpt(x.Body))));

        // Pushes.
        var pushes = db.PushEvents.AsNoTracking().Where(p => scope.Contains(p.ProjectId));
        if (actorId != null) pushes = pushes.Where(p => p.UserId == actorId);
        items.AddRange((await pushes.OrderByDescending(p => p.CreatedAt).Take(take)
                .Select(p => new { p.CreatedAt, p.User, p.Ref, p.After, p.CommitCount, p.Repository.Project.Key, p.Repository.Project.Name, p.Repository.Slug }).ToListAsync())
            .Select(x => PushItem(x.CreatedAt, x.User, x.Ref, x.After, x.CommitCount, x.Key, x.Name, x.Slug)));

        return items.OrderByDescending(i => i.At).Take(take).ToList();
    }

    /// <summary>
    /// Contributions per UTC day over the last <see cref="CalendarDays"/> days (through <paramref name="today"/>, default
    /// today): issues opened and closed, comments, pull requests opened and merged, and pushed commits. Visible projects only.
    /// </summary>
    public async Task<Dictionary<DateOnly, int>> GetContributionsAsync(int userId, DateOnly? today = null)
    {
        var end = today ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var since = end.AddDays(-CalendarDays).ToDateTime(TimeOnly.MinValue);
        var scope = await ScopeAsync(null);
        var days = new Dictionary<DateOnly, int>();
        if (scope.Count == 0) return days;
        await using var db = await dbFactory.CreateDbContextAsync();

        void Add(IEnumerable<DateTime> times)
        {
            foreach (var t in times)
            {
                var d = DateOnly.FromDateTime(t);
                if (d > end) continue;
                days[d] = days.GetValueOrDefault(d) + 1;
            }
        }

        Add(await db.Issues.Where(i => i.ReporterId == userId && i.CreatedAt >= since && scope.Contains(i.ProjectId)).Select(i => i.CreatedAt).ToListAsync());
        var closes = await ClosingActivities(db, scope, userId).Where(a => a.CreatedAt >= since)
            .Select(a => new { a.CreatedAt, a.Text, a.Issue.ProjectId }).ToListAsync();
        var done = await DoneStatusNamesAsync(db, closes.Select(c => c.ProjectId).Distinct().ToList());
        Add(closes.Where(c => IsClose(c.Text, c.ProjectId, done)).Select(c => c.CreatedAt));
        Add(await (from c in db.Comments join i in db.Issues on c.IssueId equals i.Id
                   where c.AuthorId == userId && c.CreatedAt >= since && scope.Contains(i.ProjectId) select c.CreatedAt).ToListAsync());
        Add(await db.PullRequests.Where(p => p.AuthorId == userId && p.CreatedAt >= since && scope.Contains(p.Repository.ProjectId)).Select(p => p.CreatedAt).ToListAsync());
        Add(await db.PullRequests.Where(p => p.MergedById == userId && p.State == PullRequestState.Merged && p.ClosedAt >= since && scope.Contains(p.Repository.ProjectId))
            .Select(p => p.ClosedAt!.Value).ToListAsync());
        Add(await (from c in db.PullRequestComments join p in db.PullRequests on c.PullRequestId equals p.Id
                   where c.AuthorId == userId && c.CreatedAt >= since && scope.Contains(p.Repository.ProjectId) select c.CreatedAt).ToListAsync());
        // A push counts once per commit it brought (at least once).
        foreach (var p in await db.PushEvents.Where(p => p.UserId == userId && p.CreatedAt >= since && scope.Contains(p.ProjectId))
                     .Select(p => new { p.CreatedAt, p.CommitCount }).ToListAsync())
        {
            var d = DateOnly.FromDateTime(p.CreatedAt);
            if (d <= end) days[d] = days.GetValueOrDefault(d) + Math.Max(1, p.CommitCount);
        }
        return days;
    }

    private async Task<IReadOnlyCollection<int>> ScopeAsync(IReadOnlyCollection<int>? projectIds)
    {
        var visible = await access.VisibleProjectIdsAsync();
        return projectIds == null ? visible : visible.Where(projectIds.Contains).ToList();
    }

    private static IQueryable<Activity> ClosingActivities(AppDbContext db, IReadOnlyCollection<int> scope, int? actorId)
    {
        var q = db.Activities.AsNoTracking().Where(a => scope.Contains(a.Issue.ProjectId) && a.Text.StartsWith("changed status from"));
        return actorId != null ? q.Where(a => a.ActorId == actorId) : q;
    }

    /// <summary>Names of each project's Done-category workflow statuses (plus the built-in "Done").</summary>
    private static async Task<HashSet<(int, string)>> DoneStatusNamesAsync(AppDbContext db, List<int> projectIds)
    {
        var names = await db.WorkflowStatuses.Where(s => projectIds.Contains(s.ProjectId) && s.Category == IssueStatus.Done)
            .Select(s => new { s.ProjectId, s.Name }).ToListAsync();
        var set = names.Select(n => (n.ProjectId, n.Name)).ToHashSet();
        foreach (var id in projectIds) set.Add((id, IssueStatus.Done.Name()));
        return set;
    }

    /// <summary>True for "changed status from **A** to **B**" where B is a Done status of the project.</summary>
    internal static bool IsClose(string text, int projectId, HashSet<(int, string)> done) =>
        StatusChange().Match(text) is { Success: true } m && done.Contains((projectId, m.Groups[1].Value));

    [GeneratedRegex(@"^changed status from \*\*.+?\*\* to \*\*(.+?)\*\*")]
    private static partial Regex StatusChange();

    private static FeedItem PushItem(DateTime at, User user, string @ref, string? after, int commits, string key, string name, string slug)
    {
        var isTag = @ref.StartsWith("refs/tags/", StringComparison.Ordinal);
        var shortRef = @ref.StartsWith("refs/heads/", StringComparison.Ordinal) ? @ref["refs/heads/".Length..]
            : isTag ? @ref["refs/tags/".Length..] : @ref;
        var deleted = after == null;
        var title = $"{slug}:{shortRef}";
        var detail = deleted ? (isTag ? "deleted tag" : "deleted branch")
            : isTag ? "pushed tag"
            : commits == 1 ? "1 commit" : $"{commits}{(commits >= PushActivityListener.MaxCommits ? "+" : "")} commits";
        var url = deleted ? $"/p/{key}/repos/{slug}" : isTag ? $"/p/{key}/repos/{slug}/tags"
            : commits == 1 ? $"/p/{key}/repos/{slug}/commits/{after}"
            : $"/p/{key}/repos/{slug}/commits?ref={Uri.EscapeDataString(shortRef).Replace("%2F", "/")}";
        return new FeedItem(FeedKind.Push, at, user, key, name, title, url, detail);
    }

    private static string Excerpt(string body)
    {
        var flat = Regex.Replace(body, @"\s+", " ").Trim();
        return flat.Length <= 140 ? flat : flat[..140].TrimEnd() + "…";
    }
}
