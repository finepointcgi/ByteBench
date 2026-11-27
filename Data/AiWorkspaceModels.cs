using System.ComponentModel.DataAnnotations;

namespace ByteBench.Data;

/// <summary>How the workspace assistant's file edits reach the working tree.</summary>
public enum AiEditMode
{
    /// <summary>Edits are proposed as a pending change set (a diff per file); nothing changes until the user accepts it. The default.</summary>
    ReviewFirst,
    /// <summary>write_file / edit_file / delete_file change the working tree right away (the user reviews with git diff afterwards).</summary>
    ApplyDirectly,
}

/// <summary>
/// The user's choice of <see cref="AiEditMode"/>: for one workspace (<see cref="WorkspaceId"/> set, wins) or as their default for every
/// workspace (<see cref="WorkspaceId"/> null). With no row the mode is <see cref="AiEditMode.ReviewFirst"/>.
/// </summary>
public class AiWorkspacePreference
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int? WorkspaceId { get; set; }
    public AiEditMode Mode { get; set; } = AiEditMode.ReviewFirst;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum AiChangeSetState { Pending, Resolved }

/// <summary>What was decided about one proposed file: nothing yet, all of it, none of it, or some hunks.</summary>
public enum AiChangeDecision { Pending, Accepted, Rejected, Partial, Conflict }

public enum AiChangeKind { Create, Modify, Delete }

/// <summary>
/// The file changes the assistant proposed for one request in review mode: pending until the owner accepts or rejects each file (or hunk).
/// Nothing is written to the workspace before that. Rows are kept after resolving as the history of what the AI proposed and what was applied.
/// </summary>
public class AiChangeSet
{
    public int Id { get; set; }
    public int WorkspaceId { get; set; }
    public Workspace Workspace { get; set; } = null!;
    public int UserId { get; set; }
    /// <summary>The first 300 characters of the request that led to the proposal.</summary>
    [MaxLength(300)] public string Prompt { get; set; } = "";
    [MaxLength(2000)] public string? Summary { get; set; }
    public AiChangeSetState State { get; set; } = AiChangeSetState.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
    public List<AiChangeFile> Files { get; set; } = [];
}

/// <summary>One proposed file change. <see cref="OriginalText"/> is what the workspace held when the assistant first touched the file (null: it didn't exist).</summary>
public class AiChangeFile
{
    public int Id { get; set; }
    public int ChangeSetId { get; set; }
    public AiChangeSet ChangeSet { get; set; } = null!;
    [Required, MaxLength(500)] public string Path { get; set; } = "";
    public AiChangeKind Kind { get; set; }
    public string? OriginalText { get; set; }
    public string? NewText { get; set; }
    public AiChangeDecision Decision { get; set; } = AiChangeDecision.Pending;
    /// <summary>For a Partial decision: the accepted hunks as a JSON array of indexes.</summary>
    public string? AcceptedHunksJson { get; set; }
    [MaxLength(300)] public string? Note { get; set; }
    public DateTime? DecidedAt { get; set; }
}

public enum AiRunStatus { Queued, Starting, Running, Succeeded, Failed, Cancelled }

/// <summary>
/// One task-driven AI run: the AI user works an issue in its own workspace on its own branch, commits, pushes, opens a pull request linked to the
/// issue, and stops the workspace. Limits (concurrency, duration, steps, tokens, cost) are enforced by the executor; the log is <see cref="AiRunEvent"/>.
/// </summary>
public class AiRun
{
    public int Id { get; set; }
    [Required, MaxLength(32)] public string PublicId { get; set; } = "";
    public int ProjectId { get; set; }
    public int RepositoryId { get; set; }
    public Repository Repository { get; set; } = null!;
    public int IssueId { get; set; }
    public Issue Issue { get; set; } = null!;
    public int RequestedById { get; set; }
    public User RequestedBy { get; set; } = null!;
    /// <summary><c>assignment</c> (issue assigned to the AI user), <c>button</c> (Let AI work on this) or <c>api</c>.</summary>
    [MaxLength(20)] public string Trigger { get; set; } = "button";
    public AiRunStatus Status { get; set; } = AiRunStatus.Queued;
    /// <summary>Why a run that did not simply succeed ended: step limit, time limit, budget, cancelled, error.</summary>
    [MaxLength(60)] public string? StopReason { get; set; }
    [MaxLength(200)] public string? Branch { get; set; }
    [MaxLength(200)] public string? BaseBranch { get; set; }
    public int? WorkspaceId { get; set; }
    public int? PullRequestId { get; set; }
    public int Steps { get; set; }
    public int ToolCalls { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal CostUsd { get; set; }
    [MaxLength(4000)] public string? Summary { get; set; }
    [MaxLength(1000)] public string? Error { get; set; }
    public bool CancelRequested { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public List<AiRunEvent> Events { get; set; } = [];

    public bool IsActive => Status is AiRunStatus.Queued or AiRunStatus.Starting or AiRunStatus.Running;
}

/// <summary>A line of a run's log (status changes, each tool the AI used, errors). Never holds file contents or command output.</summary>
public class AiRunEvent
{
    public int Id { get; set; }
    public int RunId { get; set; }
    public AiRun Run { get; set; } = null!;
    public DateTime At { get; set; } = DateTime.UtcNow;
    /// <summary><c>status</c>, <c>step</c>, <c>tool</c>, <c>info</c> or <c>error</c>.</summary>
    [Required, MaxLength(12)] public string Kind { get; set; } = "info";
    [MaxLength(1000)] public string Message { get; set; } = "";
}
