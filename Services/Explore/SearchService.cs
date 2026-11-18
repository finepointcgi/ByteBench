using System.Text.RegularExpressions;
using GitBench.Data;
using GitBench.Services.Code;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Explore;

public enum SearchType { Issues, PullRequests, Projects, Users, Code }

public sealed record IssueHit(int Id, string Key, string ProjectKey, int Number, string Title, IssueType Type, IssueStatus Status, DateTime UpdatedAt)
{
    public string Url => $"/p/{ProjectKey}/issues/{Number}";
}

public sealed record PullRequestHit(int Id, string ProjectKey, string Repository, int Number, string Title, PullRequestState State, User Author, DateTime UpdatedAt)
{
    public string Url => $"/p/{ProjectKey}/repos/{Repository}/pull-requests/{Number}";
}

public sealed record ProjectHit(int Id, string Key, string Name, string? Description)
{
    public string Url => $"/p/{Key}/issues";
}

public sealed record UserHit(User User)
{
    public string Url => $"/u/{Uri.EscapeDataString(User.UserName ?? "")}";
}

/// <summary>
/// Results of one search. The lists hold at most the requested number of hits per type; the counts are the totals
/// (code has no total: it's searched only when asked for, and capped). <see cref="JumpUrl"/> is set when the query is
/// an issue key such as <c>WEB-12</c> that names a visible issue.
/// </summary>
public sealed record GlobalSearchResults(string Query, string? JumpUrl,
    IReadOnlyList<IssueHit> Issues, int IssueCount,
    IReadOnlyList<PullRequestHit> PullRequests, int PullRequestCount,
    IReadOnlyList<ProjectHit> Projects, int ProjectCount,
    IReadOnlyList<UserHit> Users, int UserCount,
    IReadOnlyList<RepoSearchResult> Code)
{
    public static GlobalSearchResults Empty(string q) => new(q, null, [], 0, [], 0, [], 0, [], 0, []);
    public bool IsEmpty => IssueCount + PullRequestCount + ProjectCount + UserCount == 0 && Code.All(c => c.IsEmpty);
}

/// <summary>
/// Global search over issues (key, title, description), pull requests, projects, users and, on request, code. Uses SQL
/// LIKE (case-insensitive for ASCII in SQLite). Everything is limited to projects the acting user can view.
/// </summary>
public partial class SearchService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, CodeSearchService codeSearch)
{
    public const int MaxQueryLength = 200;
    public const int MaxCodeRepositories = 25;
    public const int MaxCodeMatchesPerRepo = 20;
    public const int MaxCodeMatchesTotal = 100;

    /// <summary>
    /// Searches <paramref name="types"/> (default: everything except code). <paramref name="limit"/> caps each list;
    /// <paramref name="withCounts"/> false skips the total counts (suggestions).
    /// </summary>
    public async Task<GlobalSearchResults> SearchAsync(string? query, IReadOnlyCollection<SearchType>? types = null, int limit = 20,
        bool withCounts = true, CancellationToken ct = default)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0 || await access.ResolveUserIdAsync() == null) return GlobalSearchResults.Empty(q);
        if (q.Length > MaxQueryLength) q = q[..MaxQueryLength];
        types ??= [SearchType.Issues, SearchType.PullRequests, SearchType.Projects, SearchType.Users];
        var visible = await access.VisibleProjectIdsAsync();
        var like = Like.Contains(q);
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // "WEB-12": the issue, if it exists and is visible.
        string? jump = null;
        IssueHit? keyHit = null;
        if (IssueKey().Match(q) is { Success: true } km)
        {
            var key = km.Groups[1].Value.ToUpperInvariant();
            var number = int.Parse(km.Groups[2].Value);
            keyHit = await db.Issues.AsNoTracking()
                .Where(i => i.Project.Key == key && i.Number == number && visible.Contains(i.ProjectId))
                .Select(i => new IssueHit(i.Id, key + "-" + i.Number, key, i.Number, i.Title, i.Type, i.Status, i.UpdatedAt))
                .FirstOrDefaultAsync(ct);
            jump = keyHit?.Url;
        }

        List<IssueHit> issues = [];
        var issueCount = 0;
        if (types.Contains(SearchType.Issues))
        {
            var iq = db.Issues.AsNoTracking().Where(i => visible.Contains(i.ProjectId)
                && (EF.Functions.Like(i.Title, like, Like.Escape) || (i.Description != null && EF.Functions.Like(i.Description, like, Like.Escape))));
            issues = await iq.OrderByDescending(i => i.UpdatedAt).Take(limit)
                .Select(i => new IssueHit(i.Id, i.Project.Key + "-" + i.Number, i.Project.Key, i.Number, i.Title, i.Type, i.Status, i.UpdatedAt))
                .ToListAsync(ct);
            issueCount = withCounts ? await iq.CountAsync(ct) : issues.Count;
            if (keyHit != null && issues.All(i => i.Id != keyHit.Id))
            {
                issues.Insert(0, keyHit);
                if (issues.Count > limit) issues.RemoveAt(issues.Count - 1);
                issueCount++;
            }
        }

        List<PullRequestHit> prs = [];
        var prCount = 0;
        if (types.Contains(SearchType.PullRequests))
        {
            var pq = db.PullRequests.AsNoTracking().Where(p => visible.Contains(p.Repository.ProjectId)
                && (EF.Functions.Like(p.Title, like, Like.Escape) || (p.Description != null && EF.Functions.Like(p.Description, like, Like.Escape))));
            prs = await pq.OrderByDescending(p => p.UpdatedAt).Take(limit)
                .Select(p => new PullRequestHit(p.Id, p.Repository.Project.Key, p.Repository.Slug, p.Number, p.Title, p.State, p.Author, p.UpdatedAt))
                .ToListAsync(ct);
            prCount = withCounts ? await pq.CountAsync(ct) : prs.Count;
        }

        List<ProjectHit> projects = [];
        var projectCount = 0;
        if (types.Contains(SearchType.Projects))
        {
            var jq = db.Projects.AsNoTracking().Where(p => visible.Contains(p.Id)
                && (EF.Functions.Like(p.Key, like, Like.Escape) || EF.Functions.Like(p.Name, like, Like.Escape)
                    || (p.Description != null && EF.Functions.Like(p.Description, like, Like.Escape))));
            projects = await jq.OrderBy(p => p.Name).Take(limit).Select(p => new ProjectHit(p.Id, p.Key, p.Name, p.Description)).ToListAsync(ct);
            projectCount = withCounts ? await jq.CountAsync(ct) : projects.Count;
        }

        List<UserHit> users = [];
        var userCount = 0;
        if (types.Contains(SearchType.Users))
        {
            var uq = db.Users.AsNoTracking().Where(u => u.DeactivatedAt == null
                && (EF.Functions.Like(u.UserName!, like, Like.Escape) || EF.Functions.Like(u.DisplayName, like, Like.Escape)));
            users = (await uq.OrderBy(u => u.DisplayName).Take(limit).ToListAsync(ct)).Select(u => new UserHit(u)).ToList();
            userCount = withCounts ? await uq.CountAsync(ct) : users.Count;
        }

        var code = types.Contains(SearchType.Code) ? await SearchCodeAsync(db, visible, q, ct) : [];
        return new GlobalSearchResults(q, jump, issues, issueCount, prs, prCount, projects, projectCount, users, userCount, code);
    }

    /// <summary>Default branch of each visible repository (capped), plain text, case-insensitive. Repositories without matches are left out.</summary>
    private async Task<List<RepoSearchResult>> SearchCodeAsync(AppDbContext db, IReadOnlyList<int> visible, string q, CancellationToken ct)
    {
        var repos = await db.Repositories.AsNoTracking().Include(r => r.Project)
            .Where(r => visible.Contains(r.ProjectId) && !r.IsArchived)
            .OrderBy(r => r.Project.Key).ThenBy(r => r.Name).Take(MaxCodeRepositories).ToListAsync(ct);
        var results = new List<RepoSearchResult>();
        var budget = MaxCodeMatchesTotal;
        foreach (var repo in repos)
        {
            if (budget <= 0) break;
            var r = await codeSearch.SearchRepositoryAsync(repo, null, new CodeSearchOptions(q), Math.Min(budget, MaxCodeMatchesPerRepo), ct);
            if (r.Error != null || r.IsEmpty) continue; // empty repos, missing default branches
            budget -= r.MatchCount;
            results.Add(r);
        }
        return results;
    }

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9]{1,9})-(\d{1,9})$")]
    private static partial Regex IssueKey();
}
