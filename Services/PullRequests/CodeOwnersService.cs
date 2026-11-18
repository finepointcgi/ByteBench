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
                : GitOps.ChangedPaths(path, pr.TargetBranch, pr.SourceRef));
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
            var users = list.SelectMany(n => resolved.GetValueOrDefault(n.ToLowerInvariant()) ?? []).DistinctBy(u => u.Id).ToList();
            if (users.Count > 0) owners[path] = users;
        }
        return new CodeOwnersReport(file.Path, owners, warnings);
    }

    /// <summary>
    /// Maps owner names (lower-cased) to the users they stand for, keeping only users who can see the project:
    /// <c>@user</c> and emails are one user; <c>@group/path</c> is that group's direct members with Developer or higher.
    /// Adds a warning for each name that resolves to nobody.
    /// </summary>
    private async Task<Dictionary<string, List<User>>> ResolveAsync(int projectId, List<string> names, List<string> warnings)
    {
        var result = new Dictionary<string, List<User>>();
        if (names.Count == 0) return result;
        await using var db = await dbFactory.CreateDbContextAsync();
        var userNames = names.Where(n => n.StartsWith('@') && !n.Contains('/')).Select(n => n[1..].ToUpperInvariant()).ToList();
        var emails = names.Where(n => !n.StartsWith('@') && n.Contains('@')).Select(n => n.ToUpperInvariant()).ToList();
        // "@a/b" is always a group; a bare "@acme" is a user first, then a top-level group of that path.
        var groupPaths = names.Where(n => n.StartsWith('@')).Select(n => n[1..].ToLowerInvariant()).ToList();
        var users = await db.Users.AsNoTracking()
            .Where(u => userNames.Contains(u.NormalizedUserName!) || emails.Contains(u.NormalizedEmail!)).ToListAsync();
        var groupMembers = groupPaths.Count == 0 ? [] : await db.GroupMembers.AsNoTracking().Include(m => m.User).Include(m => m.Group)
            .Where(m => groupPaths.Contains(m.Group.FullPath.ToLower()) && m.Role >= ProjectRole.Developer).ToListAsync();
        foreach (var name in names)
        {
            List<User> candidates;
            if (name.StartsWith('@') && name.Contains('/'))
            {
                var path = name[1..];
                candidates = groupMembers.Where(m => string.Equals(m.Group.FullPath, path, StringComparison.OrdinalIgnoreCase)).Select(m => m.User).ToList();
                if (candidates.Count == 0)
                {
                    warnings.Add($"Owner {name} was ignored: there is no such group, or it has no Developer+ members.");
                    continue;
                }
            }
            else
            {
                var user = name.StartsWith('@')
                    ? users.FirstOrDefault(u => string.Equals("@" + u.UserName, name, StringComparison.OrdinalIgnoreCase))
                    : users.FirstOrDefault(u => string.Equals(u.Email, name, StringComparison.OrdinalIgnoreCase));
                if (user != null) candidates = [user];
                else
                {
                    candidates = name.StartsWith('@')
                        ? groupMembers.Where(m => string.Equals(m.Group.FullPath, name[1..], StringComparison.OrdinalIgnoreCase)).Select(m => m.User).ToList()
                        : [];
                    if (candidates.Count == 0)
                    {
                        warnings.Add($"Owner {name} was ignored: there is no such user or group.");
                        continue;
                    }
                }
            }
            var allowed = new List<User>();
            foreach (var u in candidates)
                if (await access.CanAsync(projectId, Permission.ViewProject, u.Id)) allowed.Add(u);
            if (allowed.Count == 0)
            {
                warnings.Add($"Owner {name} was ignored: they don't have access to this project.");
                continue;
            }
            result[name.ToLowerInvariant()] = allowed;
        }
        return result;
    }
}
