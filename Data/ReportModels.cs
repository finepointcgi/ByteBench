using System.ComponentModel.DataAnnotations;

namespace ByteBench.Data;

/// <summary>Delivery-report settings (see <see cref="ByteBench.Services.Reports.DoraReportService"/>). Reports themselves have no tables: they read issues, activity, deployments and pull requests.</summary>
public partial class Project
{
    /// <summary>
    /// Name of the environment (in any of the project's repositories) that counts as production for the DORA metrics.
    /// Null: an environment named "production" or "prod" is used when there is one.
    /// </summary>
    [MaxLength(64)] public string? ProductionEnvironment { get; set; }

    /// <summary>A production deployment counts as failed when a rollback, a failed deployment or a hotfix issue follows within this many hours (1–720).</summary>
    public int DoraFailureWindowHours { get; set; } = 24;
}
