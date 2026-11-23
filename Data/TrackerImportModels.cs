using System.ComponentModel.DataAnnotations;

namespace ByteBench.Data;

/// <summary>Which tracker an import reads from.</summary>
public enum TrackerSourceKind { GitHub, GitLab, Jira }

public enum TrackerImportStatus { Queued, Running, Done, Failed, Cancelled }

/// <summary>
/// An import of issues from another tracker (GitHub, GitLab or Jira) into a project: a background job that reads the source's
/// API and creates issues, comments, labels and milestones. Never holds credentials (they live in memory for the one run).
/// Re-running an import into the same project adds only what is new (see <see cref="ImportedItem"/>).
/// See <see cref="ByteBench.Services.TrackerImport.TrackerImportService"/>.
/// </summary>
public class TrackerImportJob
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public int? RequestedById { get; set; }
    public User? RequestedBy { get; set; }

    public TrackerSourceKind Source { get; set; }
    /// <summary>The source's address without credentials (empty for the public GitHub and GitLab sites).</summary>
    [MaxLength(500)] public string SourceUrl { get; set; } = "";
    /// <summary><c>owner/repo</c> (GitHub), the project path or id (GitLab) or the project key (Jira).</summary>
    [Required, MaxLength(300)] public string SourceProject { get; set; } = "";
    /// <summary>The settings the import was started with (JSON of <see cref="ByteBench.Services.TrackerImport.TrackerImportSettings"/>).</summary>
    public string? SettingsJson { get; set; }

    public TrackerImportStatus Status { get; set; } = TrackerImportStatus.Queued;
    [MaxLength(2000)] public string? Error { get; set; }
    /// <summary>What the source said it has (0 when it doesn't say); drives the progress bar.</summary>
    public int IssuesExpected { get; set; }
    public int IssuesRead { get; set; }
    public int IssuesCreated { get; set; }
    /// <summary>Already imported earlier, so left as they are.</summary>
    public int IssuesSkipped { get; set; }
    public int CommentsCreated { get; set; }
    public int LabelsCreated { get; set; }
    public int MilestonesCreated { get; set; }
    public int LinksCreated { get; set; }
    public int WarningCount { get; set; }
    /// <summary>JSON array of the first warnings (things not imported or mapped loosely).</summary>
    public string? WarningsJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    public bool IsActive => Status is TrackerImportStatus.Queued or TrackerImportStatus.Running;
}

public enum ImportedKind { Issue, Comment, Label, Milestone }

/// <summary>
/// Remembers that something in the source became something here, so a second import of the same source adds only what is new
/// and never duplicates. <see cref="SourceKey"/> names the source project (e.g. <c>github:owner/repo</c>, in lower case) and
/// <see cref="ExternalId"/> is the source's stable id (an issue's id or key, never its display number alone).
/// </summary>
public class ImportedItem
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    [Required, MaxLength(400)] public string SourceKey { get; set; } = "";
    public ImportedKind Kind { get; set; }
    [Required, MaxLength(200)] public string ExternalId { get; set; } = "";
    /// <summary>The row id here (issue, comment, label or milestone).</summary>
    public int LocalId { get; set; }
    public int? ImportId { get; set; }
}
