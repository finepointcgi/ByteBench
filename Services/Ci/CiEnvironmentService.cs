using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Ci;

/// <summary>An environment with its current (last successful) and latest deployments.</summary>
public sealed record CiEnvironmentInfo(CiEnvironment Environment, Deployment? Current, Deployment? Latest, int Count);

/// <summary>
/// Records deployments for the runner (singleton, no permission checks): a job with <c>environment:</c> creates the
/// environment on first use and a <see cref="Deployment"/> each time it runs.
/// </summary>
public class CiDeploymentRecorder(IDbContextFactory<AppDbContext> dbFactory, ILogger<CiDeploymentRecorder> log)
{
    /// <summary>Names of the repository's protected environments.</summary>
    public async Task<HashSet<string>> ProtectedNamesAsync(int repositoryId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return (await db.CiEnvironments.Where(e => e.RepositoryId == repositoryId && e.IsProtected).Select(e => e.Name).ToListAsync())
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Creates the environment if needed (updating its URL when the job gives one) and a Running deployment.</summary>
    public async Task<int?> StartAsync(Pipeline p, PipelineJob job, int? userId)
    {
        if (job.EnvironmentName is not { } name) return null;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var env = await db.CiEnvironments.FirstOrDefaultAsync(e => e.RepositoryId == p.RepositoryId && e.Name == name);
            if (env == null)
            {
                env = new CiEnvironment { RepositoryId = p.RepositoryId, Name = name, ExternalUrl = job.EnvironmentUrl };
                db.CiEnvironments.Add(env);
                try { await db.SaveChangesAsync(); }
                catch (DbUpdateException)
                {
                    db.ChangeTracker.Clear(); // created by a parallel job
                    env = await db.CiEnvironments.FirstAsync(e => e.RepositoryId == p.RepositoryId && e.Name == name);
                }
            }
            else if (job.EnvironmentUrl != null && env.ExternalUrl != job.EnvironmentUrl) env.ExternalUrl = job.EnvironmentUrl;

            var d = new Deployment
            {
                EnvironmentId = env.Id, PipelineId = p.Id, JobId = job.Id, CommitSha = p.CommitSha, Ref = p.Ref, UserId = userId,
                Status = PipelineStatus.Running, CreatedAt = DateTime.UtcNow,
            };
            db.Deployments.Add(d);
            await db.SaveChangesAsync();
            return d.Id;
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            log.LogWarning(ex, "Could not record a deployment of job {Id} to {Env}", job.Id, name);
            return null;
        }
    }

    public async Task FinishAsync(int deploymentId, PipelineStatus status)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Deployments.Where(d => d.Id == deploymentId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, status).SetProperty(d => d.FinishedAt, DateTime.UtcNow));
    }
}

/// <summary>
/// Environments and deployments for the UI (scoped). Reads need ViewProject; protecting or deleting an environment
/// needs ManageRepositories (Maintainer+). Redeploying re-runs the deployment's job (<see cref="PipelineService.RetryJobAsync"/>).
/// </summary>
public class CiEnvironmentService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, Audit.AuditLog audit)
{
    public async Task<List<CiEnvironmentInfo>> ListAsync(Repository repo)
    {
        if (!await access.CanAsync(repo.ProjectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var envs = await db.CiEnvironments.AsNoTracking().Where(e => e.RepositoryId == repo.Id).OrderBy(e => e.Name).ToListAsync();
        var ids = envs.Select(e => e.Id).ToList();
        var counts = await db.Deployments.Where(d => ids.Contains(d.EnvironmentId)).GroupBy(d => d.EnvironmentId)
            .Select(g => new { g.Key, Count = g.Count(), Latest = g.Max(d => d.Id), Current = g.Where(d => d.Status == PipelineStatus.Passed).Max(d => (int?)d.Id) })
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
    public async Task<List<Deployment>> HistoryAsync(Repository repo, int environmentId, int take = 20)
    {
        if (!await access.CanAsync(repo.ProjectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Deployments.AsNoTracking().Include(d => d.User).Include(d => d.Job)
            .Where(d => d.EnvironmentId == environmentId && d.Environment.RepositoryId == repo.Id)
            .OrderByDescending(d => d.Id).Take(take).ToListAsync();
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

    /// <summary>The deployment (for "Redeploy"), if it belongs to an environment of this repository.</summary>
    public async Task<Deployment?> GetDeploymentAsync(Repository repo, int deploymentId)
    {
        if (!await access.CanAsync(repo.ProjectId, Permission.ViewProject)) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Deployments.AsNoTracking().Include(d => d.Environment)
            .FirstOrDefaultAsync(d => d.Id == deploymentId && d.Environment.RepositoryId == repo.Id);
    }
}
