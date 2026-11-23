using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Data;

/// <summary>
/// The site's connection to Workbench (one row, <see cref="SingletonId"/>), managed on <c>/admin/workbench</c>. The API key is
/// stored encrypted (<see cref="ApiKeyProtected"/>, Data Protection) and never shown back: only <see cref="KeyHint"/> is.
/// </summary>
public class WorkbenchConnection
{
    public const int SingletonId = 1;
    public int Id { get; set; } = SingletonId;
    /// <summary>Overrides <c>Workbench:BaseUrl</c> when set.</summary>
    [MaxLength(500)] public string? BaseUrl { get; set; }
    [MaxLength(1000)] public string? ApiKeyProtected { get; set; }
    /// <summary>The last four characters of the key, for recognising it.</summary>
    [MaxLength(16)] public string? KeyHint { get; set; }
    public DateTime? KeySetAt { get; set; }
    public int? KeySetById { get; set; }
    public DateTime? LastTestAt { get; set; }
    public bool? LastTestOk { get; set; }
    /// <summary>JSON: the per-area probe result of the last test.</summary>
    public string? LastTestJson { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum WorkbenchOutboxState { Pending, Done, Failed, Skipped }

/// <summary>
/// One durable "update this Workbench task's work state" job. Delivered by the Workbench push worker with retry and backoff;
/// <see cref="IdempotencyKey"/> (project, WBS code, percent, status) is unique among open rows, so the same desired state is
/// never queued twice.
/// </summary>
public class WorkbenchOutboxItem
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    [MaxLength(50)] public string WbsCode { get; set; } = "";
    [MaxLength(50)] public string? WorkbenchProjectCode { get; set; }
    public decimal Percent { get; set; }
    /// <summary>The Workbench Status option to set ("Done", "In Progress"), or null to leave the status alone.</summary>
    [MaxLength(100)] public string? Status { get; set; }
    [MaxLength(80)] public string IdempotencyKey { get; set; } = "";
    public WorkbenchOutboxState State { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    [MaxLength(1000)] public string? LastError { get; set; }
    /// <summary>What happened, e.g. a conflict note when Workbench had a different value.</summary>
    [MaxLength(1000)] public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

/// <summary>What ByteBench last pushed for a WBS task of a project: the dedupe base and the conflict reference.</summary>
public class WorkbenchTaskSync
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    [MaxLength(50)] public string WbsCode { get; set; } = "";
    public decimal? LastPercent { get; set; }
    [MaxLength(100)] public string? LastStatus { get; set; }
    public DateTime? LastPushedAt { get; set; }
}

public enum WorkbenchPlanSyncState { Idle, Queued, Running, Failed }

/// <summary>Per-project plan-to-issues sync settings and progress (Workbench plan tasks to ByteBench epics and issues).</summary>
public class WorkbenchPlanSync
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    /// <summary>Only this WBS element's descendants are synced; null: the whole plan.</summary>
    [MaxLength(50)] public string? RootWbsCode { get; set; }
    /// <summary>How many levels of summary tasks become epics (the rest become tasks and sub-tasks).</summary>
    public int EpicLevels { get; set; } = 1;
    /// <summary>The periodic reconcile includes this project.</summary>
    public bool ReconcileEnabled { get; set; }
    public WorkbenchPlanSyncState State { get; set; }
    public int Total { get; set; }
    public int Processed { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? LastRunAt { get; set; }
    [MaxLength(1000)] public string? LastError { get; set; }
    /// <summary>JSON report of the last run: counts, notes, people without a ByteBench account.</summary>
    public string? ReportJson { get; set; }
}

/// <summary>Ties an issue to its Workbench plan task (the idempotency key) and remembers what the last sync wrote, to tell who changed what.</summary>
public class WorkbenchIssueLink
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int IssueId { get; set; }
    public Guid TaskId { get; set; }
    [MaxLength(50)] public string? WbsCode { get; set; }
    [MaxLength(200)] public string? LastTitle { get; set; }
    /// <summary>The task is no longer in the synced part of the Workbench plan (removed or moved): flagged, never deleted.</summary>
    public bool Missing { get; set; }
    public DateTime? MissingSince { get; set; }
    public DateTime LastSyncedAt { get; set; } = DateTime.UtcNow;
}

public partial class Project
{
    /// <summary>Opt-in (Maintainer+): push work state (% complete, status) of issues mapped to a WBS task to Workbench.</summary>
    public bool WorkbenchPushEnabled { get; set; }
}

public partial class AppDbContext
{
    public DbSet<WorkbenchConnection> WorkbenchConnections => Set<WorkbenchConnection>();
    public DbSet<WorkbenchOutboxItem> WorkbenchOutbox => Set<WorkbenchOutboxItem>();
    public DbSet<WorkbenchTaskSync> WorkbenchTaskSyncs => Set<WorkbenchTaskSync>();
    public DbSet<WorkbenchPlanSync> WorkbenchPlanSyncs => Set<WorkbenchPlanSync>();
    public DbSet<WorkbenchIssueLink> WorkbenchIssueLinks => Set<WorkbenchIssueLink>();

    private static void ConfigureWorkbench(ModelBuilder b)
    {
        b.Entity<WorkbenchConnection>().Property(c => c.Id).ValueGeneratedNever();
        b.Entity<WorkbenchOutboxItem>().HasIndex(o => new { o.State, o.NextAttemptAt });
        b.Entity<WorkbenchOutboxItem>().HasIndex(o => new { o.ProjectId, o.CreatedAt });
        b.Entity<WorkbenchOutboxItem>().HasOne<Project>().WithMany().HasForeignKey(o => o.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<WorkbenchPlanSync>().HasIndex(s => s.ProjectId).IsUnique();
        b.Entity<WorkbenchPlanSync>().HasOne<Project>().WithMany().HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<WorkbenchIssueLink>().HasIndex(l => new { l.ProjectId, l.TaskId }).IsUnique();
        b.Entity<WorkbenchIssueLink>().HasIndex(l => l.IssueId).IsUnique();
        b.Entity<WorkbenchIssueLink>().HasOne<Project>().WithMany().HasForeignKey(l => l.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<WorkbenchIssueLink>().HasOne<Issue>().WithMany().HasForeignKey(l => l.IssueId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<WorkbenchTaskSync>().HasIndex(s => new { s.ProjectId, s.WbsCode }).IsUnique();
        b.Entity<WorkbenchTaskSync>().HasOne<Project>().WithMany().HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.Cascade);
    }
}
