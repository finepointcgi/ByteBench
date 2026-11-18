using System.Text.RegularExpressions;
using GitBench.Data;
using GitBench.Services.Git;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Forks;

/// <summary>What the repository header shows: the visible upstream (if any) and how many visible forks there are.</summary>
/// <param name="Parent">The repository this one was forked from, when the viewer can see it.</param>
/// <param name="ParentHidden">Forked from a repository the viewer can't see.</param>
public sealed record ForkInfo(Repository? Parent, bool ParentHidden, int ForkCount);

/// <summary>A cross-fork PR preview: the comparison and merge check, computed in the target repository.</summary>
public sealed record ForkCompare(CompareResult? Compare, MergeStatus? Merge);

/// <summary>
/// Forks (scoped). A fork is an ordinary <see cref="Repository"/> with <see cref="Repository.ForkedFromId"/> set, in any
/// project where the user can create repositories. GitBench has no personal namespaces: a user forks into a project of
/// their own (whoever creates a project is its Owner) or into any project or group project where they're Maintainer+.
/// <para>
/// <b>Storage.</b> The bare repository is copied with <c>git clone --bare --local</c>, which hard-links the object files:
/// nearly free on disk, yet fully independent. We don't use <c>--shared</c> (alternates): a fork would then read objects
/// from the upstream's directory, so deleting the upstream (or a gc there after a force push) would corrupt every fork
/// unless each fork were repacked first. With hard links, deleting either side only drops a link.
/// </para>
/// <para>
/// <b>Fork network.</b> Repositories linked through <see cref="Repository.ForkedFromId"/> to the same root; pull requests
/// can go between any two of them (<see cref="CreatePullRequestAsync"/>).
/// </para>
/// </summary>
public partial class ForkService(IDbContextFactory<AppDbContext> dbFactory, RepoStorage storage, ProjectAccess access,
    RepositoryService repos, PullRequestService prs, BranchProtectionService protection, Ci.CiRefTracker ciRefs,
    Ci.CiTriggerService ciTriggers, ForkPullRequestSync prSync, Webhooks.WebhookPushEvents pushEvents, Audit.AuditLog audit,
    IConfiguration config)
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$")]
    private static partial Regex SlugRx();

    // ---------- Queries ----------

    /// <summary>Projects the user can fork into: those where they have ManageRepositories (Maintainer+), by name.</summary>
    public async Task<List<Project>> TargetProjectsAsync(int? userId = null)
    {
        var uid = await access.ResolveUserIdAsync(userId);
        var visible = await access.VisibleProjectIdsAsync(uid);
        await using var db = await dbFactory.CreateDbContextAsync();
        var projects = await db.Projects.AsNoTracking().Where(p => visible.Contains(p.Id)).OrderBy(p => p.Name).ToListAsync();
        var result = new List<Project>();
        foreach (var p in projects)
            if (await access.CanAsync(p.Id, Permission.ManageRepositories, uid)) result.Add(p);
        return result;
    }

    /// <summary>The visible upstream and number of visible forks of <paramref name="repo"/>.</summary>
    public async Task<ForkInfo> GetInfoAsync(Repository repo)
    {
        var visible = await access.VisibleProjectIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var parent = repo.ForkedFromId is { } pid
            ? await db.Repositories.AsNoTracking().Include(r => r.Project).FirstOrDefaultAsync(r => r.Id == pid)
            : null;
        var count = await db.Repositories.CountAsync(r => r.ForkedFromId == repo.Id && visible.Contains(r.ProjectId));
        var canSee = parent != null && visible.Contains(parent.ProjectId);
        return new ForkInfo(canSee ? parent : null, parent != null && !canSee, count);
    }

    /// <summary>Forks of <paramref name="repo"/> in projects the acting user can see (with Project), newest first.</summary>
    public async Task<List<Repository>> ListForksAsync(Repository repo)
    {
        var visible = await access.VisibleProjectIdsAsync();
        if (!visible.Contains(repo.ProjectId)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Repositories.AsNoTracking().Include(r => r.Project)
            .Where(r => r.ForkedFromId == repo.Id && visible.Contains(r.ProjectId))
            .OrderByDescending(r => r.CreatedAt).ToListAsync();
    }

    /// <summary>The id of the original repository at the top of <paramref name="repositoryId"/>'s fork chain.</summary>
    public async Task<int> RootOfAsync(int repositoryId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var id = repositoryId;
        for (var i = 0; i < 100; i++) // forks of forks; the bound only guards against a corrupt cycle
        {
            var parent = await db.Repositories.Where(r => r.Id == id).Select(r => r.ForkedFromId).FirstOrDefaultAsync();
            if (parent is not { } p) break;
            id = p;
        }
        return id;
    }

    /// <summary>Other repositories of <paramref name="repo"/>'s fork network that the acting user can see (with Project): PR targets.</summary>
    public async Task<List<Repository>> NetworkAsync(Repository repo)
    {
        var root = await RootOfAsync(repo.Id);
        var visible = await access.VisibleProjectIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var all = await db.Repositories.AsNoTracking().Include(r => r.Project).ToListAsync();
        var byParent = all.Where(r => r.ForkedFromId != null).ToLookup(r => r.ForkedFromId!.Value);
        var result = new List<Repository>();
        var stack = new Stack<Repository>(all.Where(r => r.Id == root));
        while (stack.Count > 0)
        {
            var r = stack.Pop();
            if (r.Id != repo.Id && visible.Contains(r.ProjectId)) result.Add(r);
            foreach (var child in byParent[r.Id]) stack.Push(child);
        }
        // The upstream first, then the rest by name.
        return result.OrderByDescending(r => r.Id == repo.ForkedFromId).ThenBy(r => r.Project.Key).ThenBy(r => r.Slug).ToList();
    }

    // ---------- Forking ----------

    /// <summary>
    /// Forks <paramref name="upstream"/> (which the actor must be able to view) into <paramref name="targetProjectId"/>
    /// (where they need ManageRepositories). The slug defaults to the upstream's; the name too. Requires upstream.Project.
    /// </summary>
    public async Task<Repository> ForkAsync(Repository upstream, int targetProjectId, string? slug, string? name, User actor)
    {
        await access.RequireAsync(upstream.ProjectId, Permission.ViewProject, actor.Id);
        await access.RequireAsync(targetProjectId, Permission.ManageRepositories, actor.Id);
        name = string.IsNullOrWhiteSpace(name) ? upstream.Name : name.Trim();
        if (name.Length > 100) throw new InvalidOperationException("Name is too long (max 100 characters).");
        slug = string.IsNullOrWhiteSpace(slug) ? upstream.Slug : slug.Trim().ToLowerInvariant();
        if (slug.Length is 0 or > 100 || !SlugRx().IsMatch(slug) || slug.EndsWith(".git"))
            throw new InvalidOperationException("Slug must start with a letter or digit and use only lowercase letters, digits, '.', '_' or '-'.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var project = await db.Projects.FindAsync(targetProjectId) ?? throw new InvalidOperationException("Project not found.");
        if (await db.Repositories.AnyAsync(r => r.ProjectId == targetProjectId && r.Slug == slug))
            throw new InvalidOperationException($"A repository with slug '{slug}' already exists in {project.Key}. Choose another slug.");

        var from = repos.PathOf(upstream);
        var to = storage.PathFor(project.Key, slug);
        if (!GitOps.Exists(from)) throw new InvalidOperationException("The repository's files are missing on the server.");
        var git = GitHttpBackend.LocateGit(config);
        await Task.Run(() => GitOps.CloneBare(from, to, git));

        var fork = new Repository
        {
            ProjectId = project.Id, Project = project, Name = name, Slug = slug, Description = upstream.Description,
            DefaultBranch = upstream.DefaultBranch, ForkedFromId = upstream.Id,
        };
        try
        {
            db.Repositories.Add(fork);
            await db.SaveChangesAsync();
        }
        catch
        {
            try { Directory.Delete(to, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        // CI: the copied branches aren't "new" to the fork; only later pushes start pipelines.
        ciRefs.Prime(fork.Id, await Task.Run(() => GitOps.ListBranches(to, fork.DefaultBranch)));
        await audit.RecordAsync("repository.forked", "repository", fork.Id, project.Id,
            new { fork.Slug, fork.Name, upstream = $"{upstream.Project.Key}/{upstream.Slug}", upstreamId = upstream.Id }, actor.Id);
        return fork;
    }

    /// <summary>
    /// "Sync fork": fetches the upstream's branches into the fork's <c>refs/upstream/*</c> and fast-forwards the fork's
    /// default branch to the upstream's, unless the fork has commits upstream doesn't (then it reports Ahead or
    /// Diverged and changes nothing). Needs PushCode in the fork (subject to its branch protection) and ViewProject in
    /// the upstream. Requires fork.Project.
    /// </summary>
    public async Task<ForkSyncResult> SyncAsync(Repository fork, int actorId)
    {
        await access.RequireAsync(fork.ProjectId, Permission.PushCode, actorId);
        RepositorySettingsService.EnsureNotArchived(fork);
        Repository? upstream;
        await using (var db = await dbFactory.CreateDbContextAsync())
            upstream = fork.ForkedFromId is { } pid ? await db.Repositories.AsNoTracking().Include(r => r.Project).FirstOrDefaultAsync(r => r.Id == pid) : null;
        if (upstream == null) throw new InvalidOperationException("This repository isn't a fork (or its upstream was deleted).");
        await access.RequireAsync(upstream.ProjectId, Permission.ViewProject, actorId);
        await protection.EnsureAllowedAsync(fork, fork.DefaultBranch, RefChangeKind.Update, actorId);

        var (forkPath, upPath) = (repos.PathOf(fork), repos.PathOf(upstream));
        var result = await Task.Run(() => GitOps.SyncFork(forkPath, upPath, fork.DefaultBranch, upstream.DefaultBranch));
        if (result.State == ForkSyncState.FastForwarded)
        {
            // Like a push to the fork: webhooks, the fork's CI, and the heads of PRs from this branch.
            await pushEvents.SendAsync(fork.Id, [Webhooks.WebhookPushEvents.Branch(fork.DefaultBranch, result.Before, result.After)], actorId);
            await ciTriggers.SyncRepositoryAsync(fork.Id, actorId);
            await prSync.OnSourcePushedAsync(fork.Id, actorId);
        }
        return result;
    }

    // ---------- Cross-fork pull requests ----------

    /// <summary>
    /// Compares <paramref name="sourceBranch"/> of <paramref name="source"/> with <paramref name="targetBranch"/> of
    /// <paramref name="target"/>: the branch is fetched into a temporary ref of the target, compared and merge-checked
    /// there, and the ref is removed. Null Compare if a branch doesn't exist. Both need Project.
    /// </summary>
    public async Task<ForkCompare> PreviewAsync(Repository source, string sourceBranch, Repository target, string targetBranch)
    {
        await EnsurePullRequestAllowedAsync(source, target, await access.ResolveUserIdAsync());
        var (from, into) = (repos.PathOf(source), repos.PathOf(target));
        var temp = $"refs/gitbench-preview/{Guid.NewGuid():N}";
        return await Task.Run(() =>
        {
            if (!GitOps.BranchExists(into, targetBranch) || targetBranch.StartsWith("refs/", StringComparison.Ordinal)) return new ForkCompare(null, null);
            try
            {
                if (GitOps.FetchBranchInto(into, from, sourceBranch, temp) == null) return new ForkCompare(null, null);
                var cmp = GitOps.Compare(into, targetBranch, temp);
                var merge = cmp == null || cmp.Commits.Count == 0 ? null : GitOps.CheckMerge(into, temp, targetBranch);
                return new ForkCompare(cmp, merge);
            }
            finally { GitOps.DeleteRef(into, temp); }
        });
    }

    /// <summary>
    /// Opens a pull request from <paramref name="sourceBranch"/> of the fork <paramref name="source"/> into
    /// <paramref name="targetBranch"/> of <paramref name="target"/> (another repository of its fork network). The author
    /// needs CreatePullRequest in the fork's project and ViewProject in the target's. Both need Project.
    /// </summary>
    public async Task<PullRequest> CreatePullRequestAsync(Repository source, string sourceBranch, Repository target, string targetBranch,
        string title, string? description, int authorId, bool draft = false, IEnumerable<int>? reviewerIds = null)
    {
        await EnsurePullRequestAllowedAsync(source, target, authorId);
        await RepositorySettingsService.EnsureNotArchivedAsync(dbFactory, target.Id);
        if (string.IsNullOrWhiteSpace(sourceBranch) || string.IsNullOrWhiteSpace(targetBranch))
            throw new InvalidOperationException("Choose a source and a target branch.");
        var preview = await PreviewAsync(source, sourceBranch, target, targetBranch);
        if (preview.Compare == null)
            throw new InvalidOperationException($"Branch '{sourceBranch}' (in {source.Project.Key}/{source.Slug}) or '{targetBranch}' (in {target.Project.Key}/{target.Slug}) does not exist.");
        if (preview.Compare.Commits.Count == 0)
            throw new InvalidOperationException($"'{sourceBranch}' has no commits that aren't already in '{targetBranch}'.");
        return await prs.CreateCrossForkAsync(target, source, sourceBranch, targetBranch, title, description, authorId, preview.Compare, draft, reviewerIds);
    }

    private async Task EnsurePullRequestAllowedAsync(Repository source, Repository target, int? userId)
    {
        await access.RequireAsync(target.ProjectId, Permission.ViewProject, userId);
        await access.RequireAsync(source.ProjectId, Permission.CreatePullRequest, userId);
        if (source.Id == target.Id) throw new InvalidOperationException("Choose a different repository: use a normal pull request within one repository.");
        if (await RootOfAsync(source.Id) != await RootOfAsync(target.Id))
            throw new InvalidOperationException($"{source.Project.Key}/{source.Slug} and {target.Project.Key}/{target.Slug} aren't forks of each other.");
    }
}
