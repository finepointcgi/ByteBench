using GitBench.Data;
using GitBench.Services.Git;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Ci;

/// <summary>An environment with its current (last successful) and latest deployments.</summary>
public sealed record CiEnvironmentInfo(CiEnvironment Environment, Deployment? Current, Deployment? Latest, int Count)
{
    public bool IsStopped => Environment.StoppedAt != null;
}

/// <summary>A repository's environments (for the project-wide Environments page).</summary>
public sealed record RepoEnvironments(Repository Repository, List<CiEnvironmentInfo> Environments);

/// <summary>What a deployment changed: the commits between the previous successful deployment and it.</summary>
/// <param name="Backwards">The deployment went back in history (a rollback): <see cref="Commits"/> are the ones it removed.</param>
public sealed record DeploymentChanges(Deployment? Previous, List<CommitInfo> Commits, bool Truncated, bool Backwards);

/// <summary>A deployment waiting for approval, with the approvals so far.</summary>
public sealed record WaitingDeployment(Repository Repository, Deployment Deployment, CiEnvironment Environment, int Required,
    List<DeploymentApproval> Approvals, bool CanApprove);

/// <summary>
/// Environments and deployments for the UI and API (scoped). Reads need ViewProject; protecting, deleting, approval rules
/// and stopping without a stop job need ManageRepositories (Maintainer+). Redeploy and rollback re-run the deployment's
/// job (<see cref="PipelineService.RetryJobAsync"/>), which asks for approval again on protected environments.
/// </summary>
public class CiEnvironmentService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, Audit.AuditLog audit,
    RepoStorage storage, PipelineService pipelines)
{
    public async Task<List<CiEnvironmentInfo>> ListAsync(Repository repo)
    {
        if (!await access.CanAsync(repo.ProjectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        return await ListAsync(db, [repo.Id]);
    }

    /// <summary>Every repository of the project that has environments, by repository name.</summary>
    public async Task<List<RepoEnvironments>> ListProjectAsync(int projectId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var repos = await db.Repositories.AsNoTracking().Include(r => r.Project).Where(r => r.ProjectId == projectId).OrderBy(r => r.Name).ToListAsync();
        var all = await ListAsync(db, repos.Select(r => r.Id).ToList());
        return repos.Select(r => new RepoEnvironments(r, all.Where(i => i.Environment.RepositoryId == r.Id).ToList()))
            .Where(x => x.Environments.Count > 0).ToList();
    }

    private static async Task<List<CiEnvironmentInfo>> ListAsync(AppDbContext db, List<int> repoIds)
    {
        var envs = await db.CiEnvironments.AsNoTracking().Include(e => e.Approvers).ThenInclude(a => a.User)
            .Include(e => e.Approvers).ThenInclude(a => a.Group)
            .Where(e => repoIds.Contains(e.RepositoryId)).OrderBy(e => e.Name).AsSplitQuery().ToListAsync();
        var ids = envs.Select(e => e.Id).ToList();
        var counts = await db.Deployments.Where(d => ids.Contains(d.EnvironmentId)).GroupBy(d => d.EnvironmentId)
            .Select(g => new
            {
                g.Key, Count = g.Count(), Latest = g.Max(d => d.Id),
                Current = g.Where(d => d.Status == PipelineStatus.Passed && d.Action == DeploymentAction.Start).Max(d => (int?)d.Id),
            })
            .ToListAsync();
        var wanted = counts.SelectMany(c => c.Current is { } cur ? new[] { c.Latest, cur } : [c.Latest]).Distinct().ToList();
        var rows = await db.Deployments.AsNoTracking().Include(d => d.User).Include(d => d.Job).Where(d => wanted.Contains(d.Id)).ToDictionaryAsync(d => d.Id);
        return envs.Select(e =>
        {
            var c = counts.FirstOrDefault(x => x.Key == e.Id);
            return new CiEnvironmentInfo(e, c?.Current is { } cur ? rows.GetValueOrDefault(cur) : null,
                c == null ? null : rows.GetValueOrDefault(c.Latest), c?.Count ?? 0);
        }).ToList();
    }

    /// <summary>The newest deployments of an environment (empty if it isn't in this repository).</summary>
    public async Task<List<Deployment>> HistoryAsync(Repository repo, int environmentId, int take = 20, int skip = 0)
    {
        if (!await access.CanAsync(repo.ProjectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Deployments.AsNoTracking().Include(d => d.User).Include(d => d.Job)
            .Where(d => d.EnvironmentId == environmentId && d.Environment.RepositoryId == repo.Id)
            .OrderByDescending(d => d.Id).Skip(skip).Take(take).ToListAsync();
    }

    /// <summary>One environment of the repository, with its approvers.</summary>
    public async Task<CiEnvironment?> GetEnvironmentAsync(Repository repo, int environmentId)
    {
        if (!await access.CanAsync(repo.ProjectId, Permission.ViewProject)) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.CiEnvironments.AsNoTracking().Include(e => e.Approvers).ThenInclude(a => a.User).Include(e => e.Approvers).ThenInclude(a => a.Group)
            .AsSplitQuery().FirstOrDefaultAsync(e => e.Id == environmentId && e.RepositoryId == repo.Id);
    }

    public async Task SetProtectedAsync(Repository repo, int environmentId, bool isProtected)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        await using var db = await dbFactory.CreateDbContextAsync();
        var env = await db.CiEnvironments.FirstOrDefaultAsync(e => e.Id == environmentId && e.RepositoryId == repo.Id)
            ?? throw new InvalidOperationException("That environment no longer exists.");
        if (env.IsProtected == isProtected) return;
        env.IsProtected = isProtected;
        await db.SaveChangesAsync();
        await audit.RecordAsync(isProtected ? "environment.protected" : "environment.unprotected", "environment", env.Id, repo.ProjectId,
            new { repositoryId = repo.Id, name = env.Name });
    }

    /// <summary>
    /// Sets how many approvals a protected environment needs (1–10) and who gives them: users and groups (empty: Maintainers).
    /// Listed users and groups must be able to push to the project (Developer+) to approve.
    /// </summary>
    public async Task SetApprovalRulesAsync(Repository repo, int environmentId, int requiredApprovals, IReadOnlyCollection<int> userIds, IReadOnlyCollection<int> groupIds)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        if (requiredApprovals is < 1 or > 10) throw new InvalidOperationException("Require between 1 and 10 approvals.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var env = await db.CiEnvironments.Include(e => e.Approvers).FirstOrDefaultAsync(e => e.Id == environmentId && e.RepositoryId == repo.Id)
            ?? throw new InvalidOperationException("That environment no longer exists.");
        var users = userIds.Distinct().ToList();
        var groups = groupIds.Distinct().ToList();
        if (await db.Users.CountAsync(u => users.Contains(u.Id)) != users.Count) throw new InvalidOperationException("Unknown user.");
        if (await db.Groups.CountAsync(g => groups.Contains(g.Id)) != groups.Count) throw new InvalidOperationException("Unknown group.");
        foreach (var u in users)
            if (!await access.CanAsync(repo.ProjectId, Permission.PushCode, u))
                throw new InvalidOperationException("Approvers must be Developers or above in this project.");
        env.RequiredApprovals = requiredApprovals;
        db.CiEnvironmentApprovers.RemoveRange(env.Approvers);
        env.Approvers = [.. users.Select(u => new CiEnvironmentApprover { UserId = u }), .. groups.Select(g => new CiEnvironmentApprover { GroupId = g })];
        await db.SaveChangesAsync();
        await audit.RecordAsync("environment.approval_rules_changed", "environment", env.Id, repo.ProjectId,
            new { repositoryId = repo.Id, name = env.Name, requiredApprovals, userIds = users, groupIds = groups });
    }

    /// <summary>
    /// Adds an environment before any job deploys to it, so it can be protected from the first deployment on.
    /// Needs ManageRepositories.
    /// </summary>
    public async Task<CiEnvironment> CreateAsync(Repository repo, string name, bool isProtected)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        name = name.Trim();
        if (!CiFile.IsValidEnvironmentName(name))
            throw new InvalidOperationException("Use letters, digits, spaces, '_', '.', '/' or '-' (up to 64 characters), starting with a letter or digit.");
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.CiEnvironments.AnyAsync(e => e.RepositoryId == repo.Id && e.Name == name))
            throw new InvalidOperationException($"The environment '{name}' already exists.");
        var env = new CiEnvironment { RepositoryId = repo.Id, Name = name, IsProtected = isProtected };
        db.CiEnvironments.Add(env);
        await db.SaveChangesAsync();
        await audit.RecordAsync("environment.created", "environment", env.Id, repo.ProjectId, new { repositoryId = repo.Id, name, isProtected });
        return env;
    }

    /// <summary>Deletes an environment and its deployment history (the pipelines stay).</summary>
    public async Task DeleteAsync(Repository repo, int environmentId)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        await using var db = await dbFactory.CreateDbContextAsync();
        var env = await db.CiEnvironments.FirstOrDefaultAsync(e => e.Id == environmentId && e.RepositoryId == repo.Id);
        if (env == null) return;
        db.CiEnvironments.Remove(env);
        await db.SaveChangesAsync();
        await audit.RecordAsync("environment.deleted", "environment", env.Id, repo.ProjectId, new { repositoryId = repo.Id, name = env.Name });
    }

    /// <summary>The deployment, if it belongs to an environment of this repository.</summary>
    public async Task<Deployment?> GetDeploymentAsync(Repository repo, int deploymentId)
    {
        if (!await access.CanAsync(repo.ProjectId, Permission.ViewProject)) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Deployments.AsNoTracking().Include(d => d.Environment).Include(d => d.User).Include(d => d.Job)
            .FirstOrDefaultAsync(d => d.Id == deploymentId && d.Environment.RepositoryId == repo.Id);
    }

    /// <summary>Approvals (and rejections) recorded for a deployment's job run.</summary>
    public async Task<List<DeploymentApproval>> ApprovalsAsync(Repository repo, Deployment d)
    {
        if (!await access.CanAsync(repo.ProjectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.DeploymentApprovals.AsNoTracking().Include(a => a.User)
            .Where(a => a.DeploymentId == d.Id || a.JobId == d.JobId && a.DeploymentId == null && !a.IsStale && d.Status == PipelineStatus.Manual)
            .OrderBy(a => a.Id).ToListAsync();
    }

    /// <summary>Deployments of the project (or one repository) that wait for approval, oldest first.</summary>
    public async Task<List<WaitingDeployment>> WaitingAsync(int projectId, int? repositoryId = null)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        var me = await access.ResolveUserIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.Deployments.AsNoTracking().Include(d => d.User).Include(d => d.Job).Include(d => d.Environment).ThenInclude(e => e.Approvers)
            .Where(d => d.Status == PipelineStatus.Manual && d.Job.Status == PipelineStatus.Manual)
            .Join(db.Repositories.Include(r => r.Project).Where(r => r.ProjectId == projectId && (repositoryId == null || r.Id == repositoryId)),
                d => d.Environment.RepositoryId, r => r.Id, (d, r) => new { d, r })
            .OrderBy(x => x.d.Id).Take(100).ToListAsync();
        var result = new List<WaitingDeployment>();
        foreach (var x in rows)
        {
            var approvals = await DeploymentApprovalRules.CurrentAsync(db, x.d.JobId);
            var can = me is { } uid && await DeploymentApprovalRules.CanApproveAsync(db, access, projectId, x.d.Environment, uid)
                && approvals.All(a => a.UserId != uid);
            result.Add(new WaitingDeployment(x.r, x.d, x.d.Environment, DeploymentApprovalRules.Required(x.d.Environment), approvals, can));
        }
        return result;
    }

    /// <summary>The commits between the previous successful deployment of the environment and this one.</summary>
    public async Task<DeploymentChanges?> ChangesAsync(Repository repo, int deploymentId, int max = 50)
    {
        var d = await GetDeploymentAsync(repo, deploymentId);
        if (d == null) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        var prev = await db.Deployments.AsNoTracking().Include(x => x.User)
            .Where(x => x.EnvironmentId == d.EnvironmentId && x.Id < d.Id && x.Status == PipelineStatus.Passed && x.Action == DeploymentAction.Start)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync();
        var path = storage.PathFor(repo.Project.Key, repo.Slug);
        return await Task.Run(() =>
        {
            var (fwd, truncated) = GitOps.CommitsBetween(path, prev?.CommitSha, d.CommitSha, max);
            if (fwd.Count > 0 || prev == null) return new DeploymentChanges(prev, fwd, truncated, false);
            var (back, t2) = GitOps.CommitsBetween(path, d.CommitSha, prev.CommitSha, max);
            return new DeploymentChanges(prev, back, t2, back.Count > 0);
        });
    }

    /// <summary>The tag at (or nearest below) a commit, e.g. <c>v1.4.0</c> or <c>v1.4.0+3</c>.</summary>
    public async Task<CommitVersion?> VersionAsync(Repository repo, string sha)
    {
        if (!await access.CanAsync(repo.ProjectId, Permission.ViewProject)) return null;
        var path = storage.PathFor(repo.Project.Key, repo.Slug);
        return await Task.Run(() => GitOps.DescribeVersion(path, sha));
    }

    /// <summary>
    /// Rolls the environment back to an earlier successful deployment by running its job again (same pipeline, same
    /// commit). Needs PushCode; protected environments ask for approval again.
    /// </summary>
    public async Task RollbackAsync(Repository repo, int deploymentId)
    {
        var d = await GetDeploymentAsync(repo, deploymentId) ?? throw new InvalidOperationException("Deployment not found.");
        if (d.Status != PipelineStatus.Passed || d.Action != DeploymentAction.Start)
            throw new InvalidOperationException("Only a successful deployment can be redeployed.");
        await pipelines.RetryJobAsync(repo, d.PipelineId, d.JobId);
        await audit.RecordAsync("deployment.redeployed", "deployment", d.Id, repo.ProjectId,
            new { repositoryId = repo.Id, environment = d.Environment.Name, commitSha = d.CommitSha, pipelineId = d.PipelineId, jobId = d.JobId });
    }

    /// <summary>
    /// Stops an environment: runs the stop job (<c>environment: { action: stop }</c>) of the pipeline that deployed it.
    /// Without one, a Maintainer can mark it stopped. Returns true if a stop job was started (or is waiting for approval).
    /// </summary>
    public async Task<bool> StopAsync(Repository repo, int environmentId)
    {
        if (!await access.CanAsync(repo.ProjectId, Permission.PushCode)) throw new ForbiddenException("You can't stop environments in this project.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var env = await db.CiEnvironments.FirstOrDefaultAsync(e => e.Id == environmentId && e.RepositoryId == repo.Id)
            ?? throw new InvalidOperationException("That environment no longer exists.");
        var current = await db.Deployments.AsNoTracking()
            .Where(x => x.EnvironmentId == env.Id && x.Status == PipelineStatus.Passed && x.Action == DeploymentAction.Start)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync();
        var stopJob = current == null ? null : await db.PipelineJobs.AsNoTracking()
            .Where(j => j.PipelineId == current.PipelineId && j.EnvironmentName == env.Name && j.EnvironmentAction == "stop").OrderBy(j => j.Order).FirstOrDefaultAsync();
        if (stopJob != null)
        {
            if (stopJob.Status == PipelineStatus.Manual) await pipelines.PlayJobAsync(repo, stopJob.PipelineId, stopJob.Id);
            else await pipelines.RetryJobAsync(repo, stopJob.PipelineId, stopJob.Id);
            await audit.RecordAsync("environment.stop_requested", "environment", env.Id, repo.ProjectId, new { repositoryId = repo.Id, name = env.Name, jobId = stopJob.Id });
            return true;
        }
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        if (env.StoppedAt != null) return false;
        env.StoppedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync("environment.stopped", "environment", env.Id, repo.ProjectId, new { repositoryId = repo.Id, name = env.Name });
        return false;
    }

    /// <summary>True if the environment's current pipeline has a stop job (for the Stop button's wording).</summary>
    public async Task<bool> HasStopJobAsync(CiEnvironmentInfo info)
    {
        if (info.Current == null) return false;
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.PipelineJobs.AnyAsync(j => j.PipelineId == info.Current.PipelineId && j.EnvironmentName == info.Environment.Name && j.EnvironmentAction == "stop");
    }
}
