using GitBench.Data;
using GitBench.Services.Git;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Ci;

/// <summary>
/// Where some commits are deployed: an environment whose current deployment contains <see cref="Included"/> of the
/// <see cref="Total"/> commits, and the first successful deployment that shipped any of them.
/// </summary>
public sealed record EnvironmentTrace(Repository Repository, CiEnvironment Environment, Deployment Current, Deployment? FirstDeployment,
    int Included, int Total)
{
    public bool All => Included == Total;
}

/// <summary>
/// Traceability (scoped, ViewProject): which environments the commits of an issue, a pull request or a release tag are
/// deployed to. A commit counts as deployed when the environment's current successful deployment is that commit or a
/// descendant of it.
/// </summary>
public class DeploymentTraceService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, RepoStorage storage)
{
    /// <summary>How many of the newest successful deployments per environment are searched for the first one.</summary>
    private const int HistoryDepth = 100;

    /// <summary>Environments of <paramref name="repo"/> that contain any of <paramref name="revs"/> (SHAs, branches or tags).</summary>
    public async Task<List<EnvironmentTrace>> ForRevisionsAsync(Repository repo, IReadOnlyCollection<string> revs)
    {
        if (revs.Count == 0 || !await access.CanAsync(repo.ProjectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        return await TraceAsync(db, repo, revs.Distinct().ToList());
    }

    /// <summary>Where a pull request's merge commit is deployed (empty unless it was merged).</summary>
    public async Task<List<EnvironmentTrace>> ForPullRequestAsync(Repository repo, PullRequest pr) =>
        pr.MergeCommitSha is { } sha ? await ForRevisionsAsync(repo, [sha]) : [];

    /// <summary>Where a tag (e.g. a release's) is deployed.</summary>
    public async Task<List<EnvironmentTrace>> ForTagAsync(Repository repo, string tagName)
    {
        if (!await access.CanAsync(repo.ProjectId, Permission.ViewProject)) return [];
        var path = storage.PathFor(repo.Project.Key, repo.Slug);
        var sha = await Task.Run(() => GitOps.TagCommitSha(path, tagName));
        return sha == null ? [] : await ForRevisionsAsync(repo, [sha]);
    }

    /// <summary>
    /// Where an issue's work is deployed, across the project's repositories: commits that mention its key, and the merge
    /// commits of merged pull requests whose title or source branch mentions it.
    /// </summary>
    public async Task<List<EnvironmentTrace>> ForIssueAsync(int projectId, string projectKey, int issueNumber)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        var rx = IssueKeys.Exact(projectKey, issueNumber);
        await using var db = await dbFactory.CreateDbContextAsync();
        var repos = await db.Repositories.AsNoTracking().Include(r => r.Project).Where(r => r.ProjectId == projectId).ToListAsync();
        var envRepoIds = await db.CiEnvironments.Select(e => e.RepositoryId).Distinct().ToListAsync();
        repos = repos.Where(r => envRepoIds.Contains(r.Id)).ToList();
        if (repos.Count == 0) return [];
        var repoIds = repos.Select(r => r.Id).ToList();
        var merged = (await db.PullRequests.AsNoTracking()
                .Where(p => repoIds.Contains(p.RepositoryId) && p.State == PullRequestState.Merged && p.MergeCommitSha != null)
                .Select(p => new { p.RepositoryId, p.Title, p.SourceBranch, p.MergeCommitSha }).ToListAsync())
            .Where(p => rx.IsMatch(p.Title) || rx.IsMatch(p.SourceBranch)).ToList();

        var result = new List<EnvironmentTrace>();
        foreach (var repo in repos)
        {
            var path = storage.PathFor(repo.Project.Key, repo.Slug);
            var shas = await Task.Run(() => GitOps.Exists(path) ? GitOps.FindCommits(path, rx, scanLimit: 2000, take: 100).Select(c => c.Sha).ToList() : []);
            shas.AddRange(merged.Where(p => p.RepositoryId == repo.Id).Select(p => p.MergeCommitSha!));
            if (shas.Count > 0) result.AddRange(await TraceAsync(db, repo, shas.Distinct().ToList()));
        }
        return result;
    }

    private async Task<List<EnvironmentTrace>> TraceAsync(AppDbContext db, Repository repo, List<string> revs)
    {
        var envs = await db.CiEnvironments.AsNoTracking().Where(e => e.RepositoryId == repo.Id).OrderBy(e => e.Name).ToListAsync();
        if (envs.Count == 0) return [];
        var envIds = envs.Select(e => e.Id).ToList();
        var history = await db.Deployments.AsNoTracking().Include(d => d.User)
            .Where(d => envIds.Contains(d.EnvironmentId) && d.Status == PipelineStatus.Passed && d.Action == DeploymentAction.Start)
            .OrderByDescending(d => d.Id).Take(HistoryDepth * envs.Count).ToListAsync();
        if (history.Count == 0) return [];
        var path = storage.PathFor(repo.Project.Key, repo.Slug);
        var tips = history.Select(d => d.CommitSha).Distinct().ToList();
        var contained = await Task.Run(() => GitOps.Contained(path, revs, tips));
        var result = new List<EnvironmentTrace>();
        foreach (var env in envs)
        {
            var mine = history.Where(d => d.EnvironmentId == env.Id).ToList(); // newest first
            if (mine.Count == 0) continue;
            var current = mine[0];
            var included = contained.GetValueOrDefault(current.CommitSha)?.Count ?? 0;
            if (included == 0) continue;
            var first = mine.Take(HistoryDepth).LastOrDefault(d => contained.GetValueOrDefault(d.CommitSha)?.Count > 0);
            result.Add(new EnvironmentTrace(repo, env, current, first, included, revs.Count));
        }
        return result;
    }
}
