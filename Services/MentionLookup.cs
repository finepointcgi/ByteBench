using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Services.Ai;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services;

/// <summary>One autocomplete row. <see cref="Insert"/> is the text that replaces the typed token (e.g. <c>@bob</c>, <c>WEB-12</c>).</summary>
public sealed record AutocompleteItem(string Insert, string Label, string? Detail, string? Color, string? Initials);

/// <summary>
/// Suggestions for the Markdown editor's autocomplete (<c>wwwroot/js/mention-autocomplete.js</c>): people for
/// <c>@name</c> and issues for <c>#12</c>, <c>#title</c> or <c>WEB-1</c>. Runs as the signed-in user, and only returns
/// what they may see: nothing unless they can view the project, and only users who can view it too
/// (site admins, members, and everyone when the project is Internal: <see cref="ProjectAccess"/>'s rules).
/// </summary>
public partial class MentionLookup(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access)
{
    public const int MaxResults = 8;
    public const int MaxQueryLength = 50;

    public async Task<IReadOnlyList<AutocompleteItem>> UsersAsync(int projectId, string? query)
    {
        query = (query ?? "").Trim();
        if (query.Length > MaxQueryLength || !await access.CanAsync(projectId, Permission.ViewProject)) return [];

        await using var db = await dbFactory.CreateDbContextAsync();
        var project = await db.Projects.Where(p => p.Id == projectId).Select(p => new { p.Visibility }).FirstAsync();
        var bot = AiBot.UserName.ToUpperInvariant();
        var q = query.ToUpperInvariant();

        var automationBot = Automation.AutomationBot.UserName.ToUpperInvariant();
        var deskBot = ServiceDesk.ServiceDeskBot.UserName.ToUpperInvariant();
        var depBot = DependencyBot.DependencyBotAccount.UserName.ToUpperInvariant();
        var users = db.Users.Where(u => u.NormalizedUserName != bot && u.NormalizedUserName != automationBot && u.NormalizedUserName != deskBot && u.NormalizedUserName != depBot);
        if (project.Visibility != ProjectVisibility.Internal)
            users = users.Where(u => u.IsAdmin || db.ProjectMembers.Any(m => m.ProjectId == projectId && m.UserId == u.Id));
        if (q.Length > 0)
            users = users.Where(u => u.NormalizedUserName!.StartsWith(q) || u.DisplayName.ToUpper().Contains(q));

        var found = await users
            .Select(u => new
            {
                u.UserName, u.DisplayName, u.Color,
                Member = db.ProjectMembers.Any(m => m.ProjectId == projectId && m.UserId == u.Id),
                Prefix = q.Length > 0 && u.NormalizedUserName!.StartsWith(q),
            })
            .OrderByDescending(u => u.Prefix).ThenByDescending(u => u.Member).ThenBy(u => u.UserName)
            .Take(MaxResults).ToListAsync();

        return found.Select(u => new AutocompleteItem("@" + u.UserName, u.DisplayName, "@" + u.UserName,
            HexColor().IsMatch(u.Color) ? u.Color : null, Initials(u.DisplayName))).ToList();
    }

    /// <summary>
    /// Issues of <paramref name="projectId"/>, or of the project with key <paramref name="projectKey"/> when given (if the
    /// user can view it). A numeric <paramref name="query"/> matches issue numbers by prefix; text matches titles.
    /// </summary>
    public async Task<IReadOnlyList<AutocompleteItem>> IssuesAsync(int projectId, string? projectKey, string? query)
    {
        query = (query ?? "").Trim();
        if (query.Length > MaxQueryLength) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!string.IsNullOrEmpty(projectKey))
        {
            var key = projectKey.ToUpperInvariant();
            var id = await db.Projects.Where(p => p.Key == key).Select(p => (int?)p.Id).FirstOrDefaultAsync();
            if (id == null) return [];
            projectId = id.Value;
        }
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];

        var issues = db.Issues.Where(i => i.ProjectId == projectId);
        if (query.Length > 0)
        {
            if (query.All(char.IsAsciiDigit)) issues = issues.Where(i => i.Number.ToString().StartsWith(query));
            else
            {
                var q = query.ToUpperInvariant();
                issues = issues.Where(i => i.Title.ToUpper().Contains(q));
            }
        }
        var found = await issues.OrderByDescending(i => i.Number)
            .Select(i => new { i.Project.Key, i.Number, i.Title, i.Status }).Take(MaxResults).ToListAsync();
        return found.Select(i => new AutocompleteItem($"{i.Key}-{i.Number}", i.Title, $"{i.Key}-{i.Number} · {i.Status.Name()}", null, null)).ToList();
    }

    private static string Initials(string name) =>
        string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));

    [GeneratedRegex("^#[0-9a-fA-F]{3,8}$")]
    private static partial Regex HexColor();
}
