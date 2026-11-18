using System.Text.RegularExpressions;
using GitBench.Data;
using GitBench.Services.Git;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services;

public record RepoSummary(Repository Repository, int OpenPullRequests);
public record DevBranch(Repository Repo, BranchInfo Branch);
public record DevCommit(Repository Repo, CommitInfo Commit);
public record DevelopmentInfo(List<DevBranch> Branches, List<PullRequest> PullRequests, List<DevCommit> Commits)
{
    public bool IsEmpty => Branches.Count == 0 && PullRequests.Count == 0 && Commits.Count == 0;
}

/// <summary>Repository metadata (database) plus access to the bare repos on disk via <see cref="GitOps"/>.</summary>
public partial class RepositoryService(IDbContextFactory<AppDbContext> dbFactory, RepoStorage storage, ProjectAccess access)
{
    public string PathOf(Repository repo) => storage.PathFor(repo.Project.Key, repo.Slug);

    /// <summary>Runs a Git operation against <paramref name="repo"/> on the thread pool. Requires repo.Project to be loaded.</summary>
    public Task<T> Git<T>(Repository repo, Func<string, T> op)
    {
        var path = PathOf(repo);
        return Task.Run(() => op(path));
    }

    public Task Git(Repository repo, Action<string> op)
    {
        var path = PathOf(repo);
        return Task.Run(() => op(path));
    }

    public static GitIdentity IdentityOf(User u) => new(u.DisplayName, u.Email ?? $"{u.UserName}@gitbench.local");

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$")]
    private static partial Regex SlugRx();

    /// <summary>"My Web App!" → "my-web-app".</summary>
    public static string Slugify(string name)
    {
        var s = Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9._-]+", "-").Trim('-', '.', '_');
        if (s.EndsWith(".git")) s = s[..^4];
        return s.Length > 100 ? s[..100] : s;
    }

    // ---------- Queries ----------

    public async Task<List<RepoSummary>> GetRepositoriesAsync(int projectId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var repos = await db.Repositories.Include(r => r.Project).Where(r => r.ProjectId == projectId)
            .OrderBy(r => r.Name).ToListAsync();
        var ids = repos.Select(r => r.Id).ToList();
        var open = await db.PullRequests.Where(p => ids.Contains(p.RepositoryId) && p.State == PullRequestState.Open)
            .GroupBy(p => p.RepositoryId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
        return repos.Select(r => new RepoSummary(r, open.GetValueOrDefault(r.Id))).ToList();
    }

    /// <summary>The repository, or null if it doesn't exist or the acting user can't view its project.</summary>
    public async Task<Repository?> GetRepositoryAsync(string projectKey, string slug)
    {
        var visible = await access.VisibleProjectIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var key = projectKey.ToUpperInvariant();
        var s = slug.ToLowerInvariant();
        return await db.Repositories.Include(r => r.Project).Where(r => visible.Contains(r.ProjectId))
            .FirstOrDefaultAsync(r => r.Project.Key == key && r.Slug == s);
    }

    /// <summary>All users keyed by e-mail (case-insensitive), to show avatars for commit authors.</summary>
    public async Task<Dictionary<string, User>> GetUsersByEmailAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var users = await db.Users.Where(u => u.Email != null).ToListAsync();
        return users.GroupBy(u => u.Email!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    // ---------- Commands ----------

    /// <summary>Creates the bare repo on disk (with an initial README commit) and its metadata row.</summary>
    public async Task<Repository> CreateAsync(int projectId, string name, string? slug, string? description, User author)
    {
        await access.RequireAsync(projectId, Permission.ManageRepositories, author.Id);
        name = name.Trim();
        if (name.Length is 0 or > 100) throw new InvalidOperationException("Name is required (max 100 characters).");
        slug = string.IsNullOrWhiteSpace(slug) ? Slugify(name) : slug.Trim().ToLowerInvariant();
        if (slug.Length is 0 or > 100 || !SlugRx().IsMatch(slug) || slug.EndsWith(".git"))
            throw new InvalidOperationException("Slug must start with a letter or digit and use only lowercase letters, digits, '.', '_' or '-'.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var project = await db.Projects.FindAsync(projectId) ?? throw new InvalidOperationException("Project not found.");
        if (await db.Repositories.AnyAsync(r => r.ProjectId == projectId && r.Slug == slug))
            throw new InvalidOperationException($"A repository with slug '{slug}' already exists in {project.Key}.");

        var repo = new Repository
        {
            ProjectId = projectId, Project = project, Name = name, Slug = slug,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
        };
        var path = storage.PathFor(project.Key, slug);
        var readme = $"# {name}\n\n{(repo.Description ?? $"Repository for the {project.Name} project.")}\n";
        await Task.Run(() => GitOps.CreateBare(path, repo.DefaultBranch, readme, IdentityOf(author)));

        try
        {
            db.Repositories.Add(repo);
            await db.SaveChangesAsync();
        }
        catch
        {
            // Don't leave an orphaned repo on disk if the row couldn't be saved.
            try { Directory.Delete(path, recursive: true); } catch (IOException) { }
            throw;
        }
        return repo;
    }

    public async Task UpdateAsync(int repoId, string name, string? description)
    {
        await access.RequireForRepositoryAsync(repoId, Permission.ManageRepositories);
        await using var db = await dbFactory.CreateDbContextAsync();
        var r = await db.Repositories.FindAsync(repoId) ?? throw new InvalidOperationException("Repository not found.");
        r.Name = name.Trim();
        r.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        await db.SaveChangesAsync();
    }

    /// <summary>Creates a branch (requires PushCode). Requires repo.Project to be loaded.</summary>
    public async Task CreateBranchAsync(Repository repo, string name, string fromRev, int? actorId = null)
    {
        await access.RequireAsync(repo.ProjectId, Permission.PushCode, actorId);
        await RepositorySettingsService.EnsureNotArchivedAsync(dbFactory, repo.Id);
        await Git(repo, path => GitOps.CreateBranch(path, name, fromRev));
    }

    /// <summary>Deletes a non-default branch (requires DeleteBranch). Requires repo.Project to be loaded.</summary>
    public async Task DeleteBranchAsync(Repository repo, string name, int? actorId = null)
    {
        await access.RequireAsync(repo.ProjectId, Permission.DeleteBranch, actorId);
        await RepositorySettingsService.EnsureNotArchivedAsync(dbFactory, repo.Id);
        await Git(repo, path => GitOps.DeleteBranch(path, name, repo.DefaultBranch));
    }

    // ---------- Jira-style development info ----------

    /// <summary>Branches, pull requests and recent commits (across all repos of the project) that mention the issue key.</summary>
    public async Task<DevelopmentInfo> GetDevelopmentAsync(int projectId, string projectKey, int issueNumber)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return new DevelopmentInfo([], [], []);
        var rx = IssueKeys.Exact(projectKey, issueNumber);
        await using var db = await dbFactory.CreateDbContextAsync();
        var repos = await db.Repositories.Include(r => r.Project).Where(r => r.ProjectId == projectId).ToListAsync();
        var prs = (await db.PullRequests.Include(p => p.Repository).ThenInclude(r => r.Project).Include(p => p.Author)
                .Where(p => p.Repository.ProjectId == projectId).ToListAsync())
            .Where(p => rx.IsMatch(p.Title) || rx.IsMatch(p.SourceBranch))
            .OrderBy(p => p.State).ThenByDescending(p => p.UpdatedAt).ToList();

        var branches = new List<DevBranch>();
        var commits = new List<DevCommit>();
        await Task.Run(() =>
        {
            foreach (var repo in repos)
            {
                var path = PathOf(repo);
                if (!GitOps.Exists(path)) continue;
                branches.AddRange(GitOps.ListBranches(path, repo.DefaultBranch).Where(b => rx.IsMatch(b.Name)).Select(b => new DevBranch(repo, b)));
                commits.AddRange(GitOps.FindCommits(path, rx, take: 10).Select(c => new DevCommit(repo, c)));
            }
        });
        commits = commits.OrderByDescending(c => c.Commit.When).Take(10).ToList();
        return new DevelopmentInfo(branches, prs, commits);
    }
}
