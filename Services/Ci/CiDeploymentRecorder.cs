using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Ci;

/// <summary>
/// Records deployments for the runner (singleton, no permission checks): a job with <c>environment:</c> creates the
/// environment on first use and a <see cref="Deployment"/> each time it runs. A job gated by a protected environment gets
/// its deployment early, in <see cref="PipelineStatus.Manual"/> ("waiting for approval"), which turns Running when the
/// job starts. Every status change raises the <c>deployment.status</c> webhook.
/// </summary>
public class CiDeploymentRecorder(IDbContextFactory<AppDbContext> dbFactory, IServiceScopeFactory scopes,
    Webhooks.WebhookDispatcher webhooks, Api.PublicBaseUrl baseUrl, ILogger<CiDeploymentRecorder> log)
{
    /// <summary>Names of the repository's protected environments.</summary>
    public async Task<HashSet<string>> ProtectedNamesAsync(int repositoryId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return (await db.CiEnvironments.Where(e => e.RepositoryId == repositoryId && e.IsProtected).Select(e => e.Name).ToListAsync())
            .ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<CiEnvironment> EnsureEnvironmentAsync(AppDbContext db, int repositoryId, PipelineJob job)
    {
        var name = job.EnvironmentName!;
        var env = await db.CiEnvironments.FirstOrDefaultAsync(e => e.RepositoryId == repositoryId && e.Name == name);
        if (env == null)
        {
            env = new CiEnvironment { RepositoryId = repositoryId, Name = name, ExternalUrl = job.EnvironmentUrl };
            db.CiEnvironments.Add(env);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear(); // created by a parallel job
                env = await db.CiEnvironments.FirstAsync(e => e.RepositoryId == repositoryId && e.Name == name);
            }
        }
        else if (job.EnvironmentUrl != null && env.ExternalUrl != job.EnvironmentUrl && !job.IsStopJob) env.ExternalUrl = job.EnvironmentUrl;
        return env;
    }

    /// <summary>The earlier deployment of this job (a redeploy), and whether repeating it now rolls the environment back.</summary>
    private static async Task<(int? RedeployOf, bool Rollback)> RedeployInfoAsync(AppDbContext db, int environmentId, PipelineJob job, string sha)
    {
        var previous = await db.Deployments.Where(d => d.JobId == job.Id && d.Status != PipelineStatus.Manual)
            .OrderByDescending(d => d.Id).Select(d => (int?)d.Id).FirstOrDefaultAsync();
        if (previous == null || job.IsStopJob) return (previous, false);
        var current = await db.Deployments.Where(d => d.EnvironmentId == environmentId && d.Status == PipelineStatus.Passed && d.Action == DeploymentAction.Start)
            .OrderByDescending(d => d.Id).Select(d => new { d.Id, d.CommitSha }).FirstOrDefaultAsync();
        return (previous, current != null && current.Id > previous && current.CommitSha != sha);
    }

    /// <summary>A job waits for approval: records its deployment as Manual (once) and tells the approvers.</summary>
    public async Task<int?> BlockAsync(Pipeline p, PipelineJob job)
    {
        if (job.EnvironmentName == null) return null;
        int id;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var existing = await db.Deployments.Where(d => d.JobId == job.Id && d.Status == PipelineStatus.Manual).Select(d => (int?)d.Id).FirstOrDefaultAsync();
            if (existing != null) return existing;
            var env = await EnsureEnvironmentAsync(db, p.RepositoryId, job);
            var (redeployOf, rollback) = await RedeployInfoAsync(db, env.Id, job, p.CommitSha);
            var d = new Deployment
            {
                EnvironmentId = env.Id, PipelineId = p.Id, JobId = job.Id, CommitSha = p.CommitSha, Ref = p.Ref,
                UserId = job.PlayedById ?? p.TriggeredById, Status = PipelineStatus.Manual, CreatedAt = DateTime.UtcNow,
                Action = job.IsStopJob ? DeploymentAction.Stop : DeploymentAction.Start, RedeployOfId = redeployOf, IsRollback = rollback,
            };
            db.Deployments.Add(d);
            await db.SaveChangesAsync();
            id = d.Id;
            await db.DeploymentApprovals.Where(a => a.JobId == job.Id && !a.IsStale && a.DeploymentId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.DeploymentId, id));
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            log.LogWarning(ex, "Could not record the waiting deployment of job {Id}", job.Id);
            return null;
        }
        await RaiseAsync(id);
        await NotifyApproversAsync(id);
        return id;
    }

    /// <summary>Creates the environment if needed (updating its URL when the job gives one) and a Running deployment.</summary>
    public async Task<int?> StartAsync(Pipeline p, PipelineJob job, int? userId)
    {
        if (job.EnvironmentName is not { } name) return null;
        int id;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var env = await EnsureEnvironmentAsync(db, p.RepositoryId, job);
            var d = await db.Deployments.Where(x => x.JobId == job.Id && x.Status == PipelineStatus.Manual).OrderByDescending(x => x.Id).FirstOrDefaultAsync();
            if (d == null)
            {
                var (redeployOf, rollback) = await RedeployInfoAsync(db, env.Id, job, p.CommitSha);
                d = new Deployment
                {
                    EnvironmentId = env.Id, PipelineId = p.Id, JobId = job.Id, CommitSha = p.CommitSha, Ref = p.Ref,
                    Action = job.IsStopJob ? DeploymentAction.Stop : DeploymentAction.Start, RedeployOfId = redeployOf, IsRollback = rollback,
                };
                db.Deployments.Add(d);
            }
            d.UserId = userId;
            d.Status = PipelineStatus.Running;
            d.CreatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            id = d.Id;
            await db.DeploymentApprovals.Where(a => a.JobId == job.Id && !a.IsStale && a.DeploymentId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.DeploymentId, id));
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            log.LogWarning(ex, "Could not record a deployment of job {Id} to {Env}", job.Id, name);
            return null;
        }
        await RaiseAsync(id);
        return id;
    }

    /// <summary>Records the result. A passed stop job marks the environment stopped; a passed deployment starts it again.</summary>
    public async Task FinishAsync(int deploymentId, PipelineStatus status)
    {
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var d = await db.Deployments.Include(x => x.Environment).FirstOrDefaultAsync(x => x.Id == deploymentId);
            if (d == null) return;
            d.Status = status;
            d.FinishedAt = DateTime.UtcNow;
            if (status == PipelineStatus.Passed) d.Environment.StoppedAt = d.Action == DeploymentAction.Stop ? d.FinishedAt : null;
            await db.SaveChangesAsync();
        }
        await RaiseAsync(deploymentId);
    }

    /// <summary>A waiting deployment ends without running: its job was rejected or its pipeline canceled.</summary>
    public async Task CancelWaitingAsync(int jobId)
    {
        List<int> ids;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            ids = await db.Deployments.Where(d => d.JobId == jobId && d.Status == PipelineStatus.Manual).Select(d => d.Id).ToListAsync();
            if (ids.Count == 0) return;
            var now = DateTime.UtcNow;
            await db.Deployments.Where(d => ids.Contains(d.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, PipelineStatus.Canceled).SetProperty(d => d.FinishedAt, now));
        }
        foreach (var id in ids) await RaiseAsync(id);
    }

    /// <summary>Webhook status name of a deployment.</summary>
    public static string StatusName(PipelineStatus s) => s switch
    {
        PipelineStatus.Manual => "waiting_for_approval",
        PipelineStatus.Running => "running",
        PipelineStatus.Passed => "success",
        PipelineStatus.Failed or PipelineStatus.Error => "failed",
        PipelineStatus.Canceled => "canceled",
        _ => s.ToString().ToLowerInvariant(),
    };

    /// <summary>Sends <c>deployment.status</c> for the deployment's current state.</summary>
    public async Task RaiseAsync(int deploymentId)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var d = await db.Deployments.AsNoTracking().Include(x => x.Environment).Include(x => x.User).Include(x => x.Job)
                .FirstOrDefaultAsync(x => x.Id == deploymentId);
            if (d == null) return;
            var repo = await db.Repositories.AsNoTracking().Include(r => r.Project).FirstAsync(r => r.Id == d.Environment.RepositoryId);
            var b = baseUrl.Value;
            webhooks.Enqueue(repo.ProjectId, Webhooks.WebhookEventNames.DeploymentStatus, new
            {
                deployment = new
                {
                    id = d.Id,
                    status = StatusName(d.Status),
                    action = d.Action == DeploymentAction.Stop ? "stop" : "start",
                    @ref = d.Ref,
                    commitSha = d.CommitSha,
                    isRollback = d.IsRollback,
                    redeployOfId = d.RedeployOfId,
                    pipelineId = d.PipelineId,
                    jobId = d.JobId,
                    jobName = d.Job.Name,
                    createdAt = d.CreatedAt,
                    finishedAt = d.FinishedAt,
                    user = d.User == null ? null : new { id = d.User.Id, userName = d.User.UserName, displayName = d.User.DisplayName },
                    url = b + PipelineUrls.Detail(repo, d.PipelineId),
                },
                environment = new { id = d.Environment.Id, name = d.Environment.Name, externalUrl = d.Environment.ExternalUrl, isProtected = d.Environment.IsProtected },
                repository = new { id = repo.Id, slug = repo.Slug, name = repo.Name },
            }, d.UserId);
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not raise deployment.status for deployment {Id}", deploymentId); }
    }

    private async Task NotifyApproversAsync(int deploymentId)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var access = scope.ServiceProvider.GetRequiredService<ProjectAccess>();
            access.ActAsSystem();
            await using var db = await dbFactory.CreateDbContextAsync();
            var d = await db.Deployments.AsNoTracking().Include(x => x.Job).FirstAsync(x => x.Id == deploymentId);
            var env = await db.CiEnvironments.AsNoTracking().Include(e => e.Approvers).FirstAsync(e => e.Id == d.EnvironmentId);
            var repo = await db.Repositories.AsNoTracking().Include(r => r.Project).FirstAsync(r => r.Id == env.RepositoryId);
            var approvers = await DeploymentApprovalRules.ApproverIdsAsync(db, access, repo.ProjectId, env);
            await scope.ServiceProvider.GetRequiredService<NotificationService>().OnDeploymentAsync(repo.ProjectId,
                NotificationKind.DeploymentApprovalRequested, approvers, null,
                $"{repo.Slug}: {d.Job.Name} to {env.Name} ({d.CommitSha[..7]} on {d.Ref})", PipelineUrls.Detail(repo, d.PipelineId));
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not notify approvers of deployment {Id}", deploymentId); }
    }
}
