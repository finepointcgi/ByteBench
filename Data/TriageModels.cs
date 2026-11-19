using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Data;

public partial class Project
{
    /// <summary>When true, ByteBench AI suggests labels, component, priority, type, assignee and duplicates for new issues (and on demand). Off by default; Maintainer+ can change it.</summary>
    public bool AiTriageEnabled { get; set; }

    /// <summary>When true (and triage is on), suggested labels are applied to new issues straight away. Everything else always waits for a person.</summary>
    public bool AiTriageAutoApplyLabels { get; set; }
}

public enum TriageRunStatus { Queued, Running, Completed, Failed, Skipped }

public enum TriageItemKind { Label, Component, Priority, Type, Assignee, Duplicate }

public enum TriageItemState { Pending, Accepted, Dismissed, AutoApplied }

/// <summary>One AI triage pass over an issue: its suggestions are <see cref="Items"/>. Written by <see cref="ByteBench.Services.Ai.Triage.TriageService"/>.</summary>
public class IssueTriageRun
{
    public int Id { get; set; }
    public int IssueId { get; set; }
    public Issue Issue { get; set; } = null!;
    /// <summary>"created" (automatic, on issue creation) or "manual" (the Suggest button or the API).</summary>
    [MaxLength(20)] public string Trigger { get; set; } = "manual";
    public int? RequestedById { get; set; }
    public TriageRunStatus Status { get; set; } = TriageRunStatus.Queued;
    /// <summary>The model's one-line summary (validated: single line, no markup).</summary>
    [MaxLength(300)] public string? Summary { get; set; }
    [MaxLength(300)] public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public List<IssueTriageItem> Items { get; set; } = [];
}

/// <summary>A single validated suggestion. <see cref="TargetId"/> is the id of the label, component, user or issue it refers to.</summary>
public class IssueTriageItem
{
    public int Id { get; set; }
    public int RunId { get; set; }
    public IssueTriageRun Run { get; set; } = null!;
    public TriageItemKind Kind { get; set; }
    /// <summary>Label, component, user or issue id (null for priority and type).</summary>
    public int? TargetId { get; set; }
    /// <summary>What is suggested, for display: label name, component name, "High", "Bug", user name, issue key.</summary>
    [MaxLength(120)] public string Value { get; set; } = "";
    [MaxLength(200)] public string? Reason { get; set; }
    public TriageItemState State { get; set; } = TriageItemState.Pending;
    public int? ResolvedById { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

public partial class AppDbContext
{
    public DbSet<IssueTriageRun> IssueTriageRuns => Set<IssueTriageRun>();
    public DbSet<IssueTriageItem> IssueTriageItems => Set<IssueTriageItem>();

    private static void ConfigureAiTriage(ModelBuilder b)
    {
        b.Entity<IssueTriageRun>().HasIndex(r => new { r.IssueId, r.CreatedAt });
        b.Entity<IssueTriageRun>().HasOne(r => r.Issue).WithMany().HasForeignKey(r => r.IssueId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<IssueTriageRun>().HasMany(r => r.Items).WithOne(i => i.Run).HasForeignKey(i => i.RunId).OnDelete(DeleteBehavior.Cascade);
    }
}
