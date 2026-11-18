using GitBench.Data;
using GitBench.Services.Git;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services;

/// <summary>Who owns each changed file of a pull request, per the CODEOWNERS file on its target branch.</summary>
/// <param name="FilePath">Where the CODEOWNERS file was found (null: none, so nothing is owned).</param>
/// <param name="Owners">Changed path → its owners that are users who can see the project (paths without owners are left out).</param>
/// <param name="Warnings">Owners that were ignored and lines that couldn't be parsed.</param>
public sealed record CodeOwnersReport(string? FilePath, IReadOnlyDictionary<string, IReadOnlyList<User>> Owners, IReadOnlyList<string> Warnings)
{
    public static readonly CodeOwnersReport None = new(null, new Dictionary<string, IReadOnlyList<User>>(), []);
    public IEnumerable<int> OwnerIds => Owners.Values.SelectMany(u => u).Select(u => u.Id).Distinct();
}

/// <summary>
/// Resolves CODEOWNERS (see <see cref="CodeOwnersFile"/>) against GitBench users. <c>@username</c> and e-mail
/// addresses name users; anything else (such as <c>@group/path</c>) is not supported yet and is reported as a
/// warning, as are users who can't see the project. Used to show owners per file, auto-request their reviews on
/// new pull requests, and by <see cref="MergeGate"/> for "require code owner approval".
/// </summary>
public class CodeOwnersService(IDbContextFactory<AppDbContext> dbFactory, RepositoryService repos, ProjectAccess access)
{
    /// <summary>The first CODEOWNERS file found at <paramref name="rev"/> (see <see cref="CodeOwnersFile.Locations"/>).</summary>
    public static (string Path, string Text)? Find(string repoPath, string rev)
    {
        foreach (var location in CodeOwnersFile.Locations)
            if (GitOps.GetBlob(repoPath, rev, location) is { Text: { } text }) return (location, text);
        return null;
    }

    /// <summary>The CODEOWNERS ruling for a pull request's changed files (read from the target branch, or its tip at close).</summary>
    public async Task<CodeOwnersReport> ForPullRequestAsync(PullRequest pr, IEnumerable<string>? changedPaths = null)
    {
        var rev = pr.State == PullRequestState.Open || pr.TargetCommitSha == null ? pr.TargetBranch : pr.TargetCommitSha;
        var paths = changedPaths?.ToList() ?? await repos.Git(pr.Repository, path =>
            pr.State != PullRequestState.Open && pr.SourceCommitSha != null && pr.TargetCommitSha != null
                ? GitOps.ChangedPaths(path, pr.TargetCommitSha, pr.SourceCommitSha)
                : GitOps.ChangedPaths(path, pr.TargetBranch, pr.SourceBranch));
        return await ForPathsAsync(pr.Repository, rev, paths);
    }

    /// <summary>Owners of <paramref name="paths"/> per the CODEOWNERS file at <paramref name="rev"/>. Requires repo.Project.</summary>
    public async Task<CodeOwnersReport> ForPathsAsync(Repository repo, string rev, IEnumerable<string> paths)
    {
        var found = await repos.Git(repo, path => GitOps.Exists(path) ? Find(path, rev) : null);
        if (found is not { } file) return CodeOwnersReport.None;
        var parsed = CodeOwnersFile.Parse(file.Text);
        var warnings = parsed.Errors.Select(e => $"{file.Path} {e}").ToList();

        var byPath = paths.Distinct().ToDictionary(p => p, p => parsed.OwnersOf(p));
        var names = byPath.Values.SelectMany(o => o).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var resolved = await ResolveAsync(repo.ProjectId, names, warnings);

        var owners = new Dictionary<string, IReadOnlyList<User>>();
        foreach (var (path, list) in byPath)
        {
            var users = list.Select(n => resolved.GetValueOrDefault(n.ToLowerInvariant())).OfType<User>().DistinctBy(u => u.Id).ToList();
            if (users.Count > 0) owners[path] = users;
        }
        return new CodeOwnersReport(file.Path, owners, warnings);
    }

    /// <summary>Maps owner names (lower-cased) to users who can see the project; adds a warning for each one that doesn't.</summary>
    private async Task<Dictionary<string, User>> ResolveAsync(int projectId, List<string> names, List<string> warnings)
    {
        var result = new Dictionary<string, User>();
        if (names.Count == 0) return result;
        await using var db = await dbFactory.CreateDbContextAsync();
        var userNames = names.Where(n => n.StartsWith('@') && !n.Contains('/')).Select(n => n[1..].ToUpperInvariant()).ToList();
        var emails = names.Where(n => !n.StartsWith('@') && n.Contains('@')).Select(n => n.ToUpperInvariant()).ToList();
        var users = await db.Users.AsNoTracking()
            .Where(u => userNames.Contains(u.NormalizedUserName!) || emails.Contains(u.NormalizedEmail!)).ToListAsync();
        foreach (var name in names)
        {
            var user = name.StartsWith('@')
                ? users.FirstOrDefault(u => string.Equals("@" + u.UserName, name, StringComparison.OrdinalIgnoreCase))
                : users.FirstOrDefault(u => string.Equals(u.Email, name, StringComparison.OrdinalIgnoreCase));
            if (user == null)
            {
                warnings.Add(name.StartsWith('@') && name.Contains('/')
                    ? $"Owner {name} was ignored: group owners aren't supported yet."
                    : $"Owner {name} was ignored: there is no such user.");
                continue;
            }
            if (!await access.CanAsync(projectId, Permission.ViewProject, user.Id))
            {
                warnings.Add($"Owner {name} was ignored: they don't have access to this project.");
                continue;
            }
            result[name.ToLowerInvariant()] = user;
        }
        return result;
    }
}
