using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Data;

/// <summary>What a deployment does to its environment: <c>environment: { action: stop }</c> stops it.</summary>
public enum DeploymentAction { Start, Stop }

public partial class PipelineJob
{
    /// <summary><c>environment.action</c>: null (start, the default) or <c>stop</c>.</summary>
    [MaxLength(16)] public string? EnvironmentAction { get; set; }

    public bool IsStopJob => EnvironmentAction == "stop";
}

public partial class CiEnvironment
{
    /// <summary>For a protected environment: how many distinct approvals a deploy job needs before it runs (at least 1).</summary>
    public int RequiredApprovals { get; set; } = 1;
    /// <summary>Set when a stop job (<c>environment: { action: stop }</c>) passes; cleared by the next successful deployment.</summary>
    public DateTime? StoppedAt { get; set; }
    public List<CiEnvironmentApprover> Approvers { get; set; } = [];
}

/// <summary>
/// Who may approve deployments to a protected environment: a user, or every member of a group. With none listed,
/// Maintainers and Owners of the project approve.
/// </summary>
public class CiEnvironmentApprover
{
    public int Id { get; set; }
    public int EnvironmentId { get; set; }
    public int? UserId { get; set; }
    public User? User { get; set; }
    public int? GroupId { get; set; }
    public Group? Group { get; set; }
}

public partial class Deployment
{
    public DeploymentAction Action { get; set; } = DeploymentAction.Start;
    /// <summary>The earlier deployment of the same job this one repeats (a redeploy or rollback).</summary>
    public int? RedeployOfId { get; set; }
    /// <summary>A redeploy of an older deployment while a newer one was current.</summary>
    public bool IsRollback { get; set; }
}

/// <summary>
/// An approval (or rejection) of a job that deploys to a protected environment. Approvals are per job run: retrying or
/// rolling back the job marks the old ones <see cref="IsStale"/>.
/// </summary>
public class DeploymentApproval
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public PipelineJob Job { get; set; } = null!;
    public int PipelineId { get; set; }
    /// <summary>The waiting deployment, when the runner recorded one.</summary>
    public int? DeploymentId { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public bool Approved { get; set; }
    [MaxLength(1000)] public string? Comment { get; set; }
    public bool IsStale { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A deploy freeze: while it is active, nobody can approve or run deploy jobs to the project's protected environments.
/// Either a date range (<see cref="StartsAt"/>–<see cref="EndsAt"/>, UTC) or a recurring window between two cron
/// expressions (UTC), e.g. from <c>0 18 * * 5</c> to <c>0 8 * * 1</c> for weekends.
/// </summary>
public class DeployFreezeWindow
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    [MaxLength(200)] public string? Description { get; set; }
    [MaxLength(100)] public string? CronStart { get; set; }
    [MaxLength(100)] public string? CronEnd { get; set; }
    public DateTime? StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public int? CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public partial class AppDbContext
{
    public DbSet<CiEnvironmentApprover> CiEnvironmentApprovers => Set<CiEnvironmentApprover>();
    public DbSet<DeploymentApproval> DeploymentApprovals => Set<DeploymentApproval>();
    public DbSet<DeployFreezeWindow> DeployFreezeWindows => Set<DeployFreezeWindow>();

    private static void ConfigureDeployments(ModelBuilder b)
    {
        b.Entity<CiEnvironment>().Property(e => e.RequiredApprovals).HasDefaultValue(1);
        b.Entity<CiEnvironment>().HasMany(e => e.Approvers).WithOne().HasForeignKey(a => a.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<CiEnvironmentApprover>().HasIndex(a => a.EnvironmentId);
        b.Entity<CiEnvironmentApprover>().HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<CiEnvironmentApprover>().HasOne(a => a.Group).WithMany().HasForeignKey(a => a.GroupId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<Deployment>().HasIndex(d => d.CommitSha);
        b.Entity<Deployment>().HasOne<Deployment>().WithMany().HasForeignKey(d => d.RedeployOfId).OnDelete(DeleteBehavior.SetNull);

        b.Entity<DeploymentApproval>().HasIndex(a => new { a.JobId, a.IsStale });
        b.Entity<DeploymentApproval>().HasOne(a => a.Job).WithMany().HasForeignKey(a => a.JobId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<DeploymentApproval>().HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<DeploymentApproval>().HasOne<Deployment>().WithMany().HasForeignKey(a => a.DeploymentId).OnDelete(DeleteBehavior.SetNull);

        b.Entity<DeployFreezeWindow>().HasIndex(f => f.ProjectId);
        b.Entity<DeployFreezeWindow>().HasOne<Project>().WithMany().HasForeignKey(f => f.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<DeployFreezeWindow>().HasOne<User>().WithMany().HasForeignKey(f => f.CreatedById).OnDelete(DeleteBehavior.SetNull);
    }
}
