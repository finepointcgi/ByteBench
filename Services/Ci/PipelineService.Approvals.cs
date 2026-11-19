using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Ci;

/// <summary>A job that deploys to a protected environment: its rule, the approvals so far, and whether the user may approve.</summary>
public sealed record DeploymentGateInfo(CiEnvironment? Environment, int Required, List<DeploymentApproval> Approvals, bool CanApprove,
    DeployFreezeWindow? Freeze)
{
    public int Approved => Approvals.Count(a => a.Approved);
}

/// <summary>
/// Deployment approvals. A job that deploys to a protected environment waits in <see cref="PipelineStatus.Manual"/>
/// ("waiting for approval") until <see cref="CiEnvironment.RequiredApprovals"/> distinct approvers approve it; one
/// rejection cancels it. Approving, running, redeploying and rolling back are refused during a deploy freeze.
/// Everything is audited (<c>deployment.approved</c>, <c>deployment.rejected</c>) and the pipeline's author is notified.
/// </summary>
public partial class PipelineService
{
    private async Task<bool> IsGatedAsync(Repository repo, PipelineJob job)
    {
        if (job.RequiresApproval) return true;
        return job.EnvironmentName != null && (await deployments.ProtectedNamesAsync(repo.Id)).Contains(job.EnvironmentName);
    }

    /// <summary>The approval state of a job, or null if it doesn't need approval (or can't be viewed).</summary>
    public async Task<DeploymentGateInfo?> GetGateAsync(Repository repo, PipelineJob job)
    {
        if (!await access.CanAsync(repo.ProjectId, Permission.ViewProject)) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        var env = await DeploymentApprovalRules.EnvironmentAsync(db, repo.Id, job.EnvironmentName);
        if (!DeploymentApprovalRules.IsGated(job, env)) return null;
        var approvals = await DeploymentApprovalRules.CurrentAsync(db, job.Id);
        var me = await access.ResolveUserIdAsync();
        var can = me is { } uid && job.Status == PipelineStatus.Manual && approvals.All(a => a.UserId != uid)
            && await DeploymentApprovalRules.CanApproveAsync(db, access, repo.ProjectId, env, uid);
        return new DeploymentGateInfo(env, DeploymentApprovalRules.Required(env), approvals, can,
            await DeployFreezeService.ActiveAsync(db, repo.ProjectId, DateTime.UtcNow));
    }

    private static string ApproverMessage(CiEnvironment? env, string? name) =>
        env == null || env.Approvers.Count == 0
            ? $"Only Maintainers can approve deployments to the protected environment '{name}'."
            : $"Only the listed approvers can approve deployments to the protected environment '{name}'.";

    private static async Task RequireNoFreezeAsync(AppDbContext db, Repository repo)
    {
        if (await DeployFreezeService.ActiveAsync(db, repo.ProjectId, DateTime.UtcNow) is { } f)
            throw new InvalidOperationException($"Deployments to protected environments are frozen ({DeployFreezeService.Describe(f)}).");
    }

    /// <summary>
    /// Approves a job waiting for approval, with an optional comment. Once enough distinct approvers approved, the job
    /// runs (as the last approver). Returns true if it starts.
    /// </summary>
    public async Task<bool> ApproveJobAsync(Repository repo, int pipelineId, int jobId, string? comment)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ViewProject);
        EnsureEnabled();
        var job = await LoadJobAsync(repo, pipelineId, jobId);
        if (job.Status != PipelineStatus.Manual) throw new InvalidOperationException($"Job '{job.Name}' isn't waiting for approval.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var env = await DeploymentApprovalRules.EnvironmentAsync(db, repo.Id, job.EnvironmentName);
        if (!DeploymentApprovalRules.IsGated(job, env)) throw new InvalidOperationException($"Job '{job.Name}' doesn't need approval.");
        var me = await access.ResolveUserIdAsync() ?? throw new ForbiddenException("Sign in to approve deployments.");
        if (!await DeploymentApprovalRules.CanApproveAsync(db, access, repo.ProjectId, env, me))
            throw new ForbiddenException(ApproverMessage(env, job.EnvironmentName));
        await RequireNoFreezeAsync(db, repo);
        var current = await DeploymentApprovalRules.CurrentAsync(db, job.Id);
        if (current.Any(a => a.UserId == me)) throw new InvalidOperationException("You already approved this deployment.");

        var deploymentId = await WaitingDeploymentIdAsync(db, job.Id);
        db.DeploymentApprovals.Add(new DeploymentApproval
        {
            JobId = job.Id, PipelineId = pipelineId, DeploymentId = deploymentId, UserId = me, Approved = true, Comment = Clean(comment),
        });
        await db.SaveChangesAsync();
        var required = DeploymentApprovalRules.Required(env);
        var approved = current.Count(a => a.Approved) + 1;
        await audit.RecordAsync("deployment.approved", "deployment", deploymentId ?? job.Id, repo.ProjectId, new
        {
            repositoryId = repo.Id, environment = job.EnvironmentName, pipelineId, jobId, approvals = approved, required, comment = Clean(comment),
        });

        if (approved < required)
        {
            live.Publish(new PipelineChanged(pipelineId, repo.Id));
            return false;
        }
        var n = await db.PipelineJobs.Where(j => j.Id == jobId && j.Status == PipelineStatus.Manual)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, PipelineStatus.Queued)
                .SetProperty(j => j.PlayedById, me).SetProperty(j => j.PlayedAt, DateTime.UtcNow));
        if (n == 0) throw new InvalidOperationException($"Job '{job.Name}' isn't waiting for approval.");
        await ResumeAsync(repo, pipelineId);
        await NotifyAuthorAsync(db, repo, job, NotificationKind.DeploymentApproved, me, comment);
        return true;
    }

    /// <summary>Rejects a job waiting for approval: it is canceled (failing the pipeline) and the author is told why.</summary>
    public async Task RejectJobAsync(Repository repo, int pipelineId, int jobId, string? comment)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ViewProject);
        var job = await LoadJobAsync(repo, pipelineId, jobId);
        if (job.Status != PipelineStatus.Manual) throw new InvalidOperationException($"Job '{job.Name}' isn't waiting for approval.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var env = await DeploymentApprovalRules.EnvironmentAsync(db, repo.Id, job.EnvironmentName);
        if (!DeploymentApprovalRules.IsGated(job, env)) throw new InvalidOperationException($"Job '{job.Name}' doesn't need approval.");
        var me = await access.ResolveUserIdAsync() ?? throw new ForbiddenException("Sign in to reject deployments.");
        if (!await DeploymentApprovalRules.CanApproveAsync(db, access, repo.ProjectId, env, me))
            throw new ForbiddenException(ApproverMessage(env, job.EnvironmentName).Replace("approve", "reject"));

        var now = DateTime.UtcNow;
        var n = await db.PipelineJobs.Where(j => j.Id == jobId && j.Status == PipelineStatus.Manual)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, PipelineStatus.Canceled).SetProperty(j => j.FinishedAt, now));
        if (n == 0) throw new InvalidOperationException($"Job '{job.Name}' isn't waiting for approval.");
        await db.PipelineSteps.Where(s => s.JobId == jobId && s.Status == PipelineStatus.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, PipelineStatus.Canceled).SetProperty(x => x.FinishedAt, now));
        var deploymentId = await WaitingDeploymentIdAsync(db, job.Id);
        db.DeploymentApprovals.Add(new DeploymentApproval
        {
            JobId = job.Id, PipelineId = pipelineId, DeploymentId = deploymentId, UserId = me, Approved = false, Comment = Clean(comment),
        });
        await db.SaveChangesAsync();
        await deployments.CancelWaitingAsync(job.Id);
        await audit.RecordAsync("deployment.rejected", "deployment", deploymentId ?? job.Id, repo.ProjectId, new
        {
            repositoryId = repo.Id, environment = job.EnvironmentName, pipelineId, jobId, comment = Clean(comment),
        });
        await ResumeAsync(repo, pipelineId);
        await NotifyAuthorAsync(db, repo, job, NotificationKind.DeploymentRejected, me, comment);
    }

    /// <summary>
    /// Before retrying (redeploying, rolling back) a job: null if it isn't gated; otherwise the requester must be an
    /// approver and no freeze may be active.
    /// </summary>
    private async Task<(CiEnvironment? Environment, int Required)?> RetryGateAsync(Repository repo, PipelineJob job)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var env = await DeploymentApprovalRules.EnvironmentAsync(db, repo.Id, job.EnvironmentName);
        if (!DeploymentApprovalRules.IsGated(job, env)) return null;
        var me = await access.ResolveUserIdAsync() ?? throw new ForbiddenException("Sign in to deploy.");
        if (!await DeploymentApprovalRules.CanApproveAsync(db, access, repo.ProjectId, env, me))
            throw new ForbiddenException(ApproverMessage(env, job.EnvironmentName).Replace("approve deployments to", "redeploy to"));
        await RequireNoFreezeAsync(db, repo);
        return (env, DeploymentApprovalRules.Required(env));
    }

    /// <summary>A retried gated job starts a new approval round, with the requester's approval as the first one.</summary>
    private async Task RecordRetryApprovalAsync(Repository repo, PipelineJob job, int me, CiEnvironment? env)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.DeploymentApprovals.Where(a => a.JobId == job.Id && !a.IsStale).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsStale, true));
        db.DeploymentApprovals.Add(new DeploymentApproval { JobId = job.Id, PipelineId = job.PipelineId, UserId = me, Approved = true, Comment = "Requested a redeploy" });
        await db.SaveChangesAsync();
        await audit.RecordAsync("deployment.approved", "job", job.Id, repo.ProjectId, new
        {
            repositoryId = repo.Id, environment = job.EnvironmentName, pipelineId = job.PipelineId, jobId = job.Id, redeploy = true,
            approvals = 1, required = DeploymentApprovalRules.Required(env),
        });
    }

    private static Task<int?> WaitingDeploymentIdAsync(AppDbContext db, int jobId) =>
        db.Deployments.Where(d => d.JobId == jobId && d.Status == PipelineStatus.Manual).OrderByDescending(d => d.Id).Select(d => (int?)d.Id).FirstOrDefaultAsync();

    private static string? Clean(string? comment) =>
        string.IsNullOrWhiteSpace(comment) ? null : comment.Trim() is { Length: > 1000 } c ? c[..1000] : comment.Trim();

    private async Task NotifyAuthorAsync(AppDbContext db, Repository repo, PipelineJob job, NotificationKind kind, int actorId, string? comment)
    {
        var p = await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == job.PipelineId);
        if (p?.TriggeredById is not { } author) return;
        await notifications.OnDeploymentAsync(repo.ProjectId, kind, [author], actorId,
            $"{repo.Slug}: {job.Name} to {job.EnvironmentName} ({p.CommitSha[..Math.Min(7, p.CommitSha.Length)]} on {p.Ref})",
            PipelineUrls.Detail(repo, job.PipelineId), Clean(comment));
    }
}
