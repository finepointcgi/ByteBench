using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace GitBench.Data;

public enum IssueStatus { Backlog, Todo, InProgress, InReview, Done }
public enum IssuePriority { Lowest, Low, Medium, High, Highest }
public enum IssueType { Task, Bug, Feature, Story, Epic }

/// <summary>An account. Id, UserName, Email and PasswordHash come from ASP.NET Core Identity.</summary>
public class User : IdentityUser<int>
{
    [Required, MaxLength(80)] public string DisplayName { get; set; } = "";
    [MaxLength(9)] public string Color { get; set; } = "#6366f1";

    public string Initials => string.Concat(DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));

    /// <summary>Site administrator: has every permission on every project, private ones included.</summary>
    public bool IsAdmin { get; set; }
}

public class Project
{
    public int Id { get; set; }
    [Required, MaxLength(10), RegularExpression("^[A-Z][A-Z0-9]{1,9}$", ErrorMessage = "Key must be 2-10 uppercase letters/digits, starting with a letter.")]
    public string Key { get; set; } = "";
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int NextIssueNumber { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<Issue> Issues { get; set; } = [];
    public List<Label> Labels { get; set; } = [];
    public List<Milestone> Milestones { get; set; } = [];
    /// <summary>When true (default), GitBench AI reviews new pull requests and new pushes automatically (if AI is configured).</summary>
    public bool AiReviewEnabled { get; set; } = true;

    /// <summary>Private: members only. Internal: every signed-in user can view it (as Viewer unless a member).</summary>
    public ProjectVisibility Visibility { get; set; } = ProjectVisibility.Private;
    public List<ProjectMember> Members { get; set; } = [];
}

public class Issue
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public int Number { get; set; }

    [Required, MaxLength(200)] public string Title { get; set; } = "";
    public string? Description { get; set; }
    public IssueType Type { get; set; } = IssueType.Task;
    public IssueStatus Status { get; set; } = IssueStatus.Todo;
    public IssuePriority Priority { get; set; } = IssuePriority.Medium;
    [Range(0, 100)] public int? StoryPoints { get; set; }
    public DateTime? DueDate { get; set; }
    public double SortOrder { get; set; }

    public int? AssigneeId { get; set; }
    public User? Assignee { get; set; }
    public int ReporterId { get; set; }
    public User Reporter { get; set; } = null!;
    public int? MilestoneId { get; set; }
    public Milestone? Milestone { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }

    public List<Label> Labels { get; set; } = [];
    public List<Comment> Comments { get; set; } = [];
    public List<Activity> Activities { get; set; } = [];

    public string Key => $"{Project?.Key}-{Number}";
}

public class Label
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    [Required, MaxLength(40)] public string Name { get; set; } = "";
    [MaxLength(9)] public string Color { get; set; } = "#6b7280";
    public List<Issue> Issues { get; set; } = [];
}

public class Milestone
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    [Required, MaxLength(100)] public string Title { get; set; } = "";
    public string? Description { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? DueDate { get; set; }
    public bool IsClosed { get; set; }
    public List<Issue> Issues { get; set; } = [];
}

public class Comment
{
    public int Id { get; set; }
    public int IssueId { get; set; }
    public int AuthorId { get; set; }
    public User Author { get; set; } = null!;
    [Required] public string Body { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EditedAt { get; set; }
}

public class Activity
{
    public int Id { get; set; }
    public int IssueId { get; set; }
    public Issue Issue { get; set; } = null!;
    public int ActorId { get; set; }
    public User Actor { get; set; } = null!;
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A Git repository belonging to a project (Bitbucket-style). The bare repo lives on disk at
/// <see cref="GitBench.Services.RepoStorage.PathFor"/>; this row is its metadata.
/// </summary>
public partial class Repository
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    [Required, MaxLength(100), RegularExpression("^[a-z0-9][a-z0-9._-]*$", ErrorMessage = "Use lowercase letters, digits, '.', '_' or '-'.")]
    public string Slug { get; set; } = "";
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    public string? Description { get; set; }
    [MaxLength(100)] public string DefaultBranch { get; set; } = "main";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ----- Pull requests (Bitbucket-style code review) -----

public enum PullRequestState { Open, Merged, Declined }
public enum ReviewState { Approved, ChangesRequested }

/// <summary>A request to merge <see cref="SourceBranch"/> into <see cref="TargetBranch"/> within one repository.</summary>
public class PullRequest
{
    public int Id { get; set; }
    public int RepositoryId { get; set; }
    public Repository Repository { get; set; } = null!;
    /// <summary>Sequential per repository (web-app#1, web-app#2, …).</summary>
    public int Number { get; set; }

    [Required, MaxLength(200)] public string Title { get; set; } = "";
    public string? Description { get; set; }
    [Required, MaxLength(250)] public string SourceBranch { get; set; } = "";
    [Required, MaxLength(250)] public string TargetBranch { get; set; } = "";

    public int AuthorId { get; set; }
    public User Author { get; set; } = null!;
    public PullRequestState State { get; set; } = PullRequestState.Open;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }

    public int? MergedById { get; set; }
    public User? MergedBy { get; set; }
    [MaxLength(40)] public string? MergeCommitSha { get; set; }
    /// <summary>Source/target tips captured when the PR was merged or declined, so its diff survives branch moves and deletion.</summary>
    [MaxLength(40)] public string? SourceCommitSha { get; set; }
    [MaxLength(40)] public string? TargetCommitSha { get; set; }

    public List<PullRequestComment> Comments { get; set; } = [];
    public List<PullRequestReview> Reviews { get; set; } = [];
    public List<PullRequestActivity> Activities { get; set; } = [];
}

/// <summary>A comment on a PR. With <see cref="FilePath"/> set it is an inline comment on a diff line.</summary>
public class PullRequestComment
{
    public int Id { get; set; }
    public int PullRequestId { get; set; }
    public int AuthorId { get; set; }
    public User Author { get; set; } = null!;
    [Required] public string Body { get; set; } = "";
    [MaxLength(500)] public string? FilePath { get; set; }
    /// <summary>Line number in the new file (null for comments on deleted lines).</summary>
    public int? Line { get; set; }
    /// <summary>Line number in the old file, for comments on deleted lines.</summary>
    public int? OldLine { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EditedAt { get; set; }

    public bool IsInline => FilePath != null;
}

/// <summary>One reviewer's current verdict on a PR (at most one row per user).</summary>
public class PullRequestReview
{
    public int Id { get; set; }
    public int PullRequestId { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public ReviewState State { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Source-branch tip the verdict was given on (null for reviews recorded before this was tracked).</summary>
    [MaxLength(40)] public string? CommitSha { get; set; }
}

public class PullRequestActivity
{
    public int Id { get; set; }
    public int PullRequestId { get; set; }
    public int ActorId { get; set; }
    public User Actor { get; set; } = null!;
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One Claude API call, for cost reporting. Written by <see cref="GitBench.Services.Ai.AiClient"/>.</summary>
public class AiUsage
{
    public int Id { get; set; }
    [MaxLength(60)] public string Feature { get; set; } = "";
    [MaxLength(60)] public string Model { get; set; } = "";
    public int? UserId { get; set; }
    public int? ProjectId { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ----- Notifications & watching -----

public enum NotificationKind
{
    Assigned, Mentioned,
    IssueCommented, IssueStatusChanged, IssueClosed, IssueReopened,
    PullRequestCommented, PullRequestReviewed, PullRequestMerged, PullRequestDeclined, PullRequestReopened,
    ReviewRequested,
    PipelineFailed,
}

/// <summary>An in-app notification for one user. Created by <see cref="GitBench.Services.NotificationService"/>.</summary>
public class Notification
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public int ProjectId { get; set; }
    /// <summary>Who caused it; null for system-generated notifications.</summary>
    public int? ActorId { get; set; }
    public User? Actor { get; set; }
    public NotificationKind Kind { get; set; }
    /// <summary>Short subject line, e.g. "WEB-12 Login button misaligned".</summary>
    [Required, MaxLength(200)] public string Title { get; set; } = "";
    [Required, MaxLength(500)] public string Url { get; set; } = "";
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A user watching exactly one issue or one pull request.</summary>
public class Watch
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int? IssueId { get; set; }
    public int? PullRequestId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ----- AI code review -----

public enum AiReviewStatus { Queued, Running, Completed, Failed, Skipped }

/// <summary>One GitBench AI review run of a pull request at a given head commit. Written by <see cref="GitBench.Services.Ai.AiReviewService"/>.</summary>
public class AiReview
{
    public int Id { get; set; }
    public int PullRequestId { get; set; }
    /// <summary>Source-branch tip that was (or will be) reviewed. The same head is reviewed once unless someone re-runs it.</summary>
    [MaxLength(40)] public string HeadSha { get; set; } = "";
    public AiReviewStatus Status { get; set; } = AiReviewStatus.Queued;
    /// <summary>"created", "push" or "manual".</summary>
    [MaxLength(20)] public string Trigger { get; set; } = "";
    public int? RequestedById { get; set; }
    public string? Error { get; set; }
    /// <summary>Findings returned by the model, how many were posted inline, and how many could not be placed on the diff.</summary>
    public int FindingsCount { get; set; }
    public int InlineCount { get; set; }
    public int UnplacedCount { get; set; }
    /// <summary>Files left out (binary, lockfiles, generated, over budget) and the number of model calls the review took.</summary>
    public int SkippedFilesCount { get; set; }
    public int BatchCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

// ----- Membership, roles & access tokens -----

public enum ProjectVisibility { Private, Internal }

/// <summary>Project roles, ordered: each role has every permission of the roles before it. See <see cref="GitBench.Services.ProjectAccess"/>.</summary>
public enum ProjectRole { Viewer, Developer, Maintainer, Owner }

/// <summary>A user's role in a project (at most one row per project and user).</summary>
public class ProjectMember
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public ProjectRole Role { get; set; } = ProjectRole.Developer;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Flags]
public enum TokenScopes { None = 0, ReadRepository = 1, WriteRepository = 2 }

/// <summary>
/// A personal access token for Git over HTTP. Only the SHA-256 hash of the token is stored;
/// the plaintext (<c>gbp_…</c>) is shown to the user once, when it is created.
/// </summary>
public class PersonalAccessToken
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    /// <summary>SHA-256 of the UTF-8 token string.</summary>
    [Required, MaxLength(32)] public byte[] TokenHash { get; set; } = [];
    /// <summary>The first few characters of the token (e.g. <c>gbp_Ab12Cd</c>), for display only.</summary>
    [Required, MaxLength(16)] public string Prefix { get; set; } = "";
    public TokenScopes Scopes { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; }

    public bool IsActive(DateTime utcNow) => RevokedAt == null && (ExpiresAt == null || ExpiresAt > utcNow);
}

// ----- Branch protection & reviewers -----

/// <summary>
/// Protection for the branches of one repository whose name matches <see cref="Pattern"/> (exact, or a glob:
/// <c>*</c> and <c>?</c> stay within one path segment, <c>**</c> matches across <c>/</c>). Enforced by
/// <see cref="GitBench.Services.BranchProtectionService"/> on git push, branch create/delete and PR merge.
/// </summary>
public class BranchProtectionRule
{
    public int Id { get; set; }
    public int RepositoryId { get; set; }
    public Repository Repository { get; set; } = null!;
    [Required, MaxLength(250)] public string Pattern { get; set; } = "";
    /// <summary>Approvals on the PR's current head (from people other than the author) needed to merge.</summary>
    [Range(0, 10)] public int RequiredApprovals { get; set; }
    /// <summary>Changes reach matching branches only by merging pull requests (pushes and branch creation are refused).</summary>
    public bool BlockDirectPushes { get; set; }
    /// <summary>Refuses pushes that aren't fast-forwards.</summary>
    public bool BlockForcePushes { get; set; } = true;
    public bool BlockDeletion { get; set; } = true;
    /// <summary>Every external merge check (pipelines, ...) must be Passed or Neutral before merging.</summary>
    public bool RequireChecksToPass { get; set; }
    /// <summary>New commits on the PR's source branch dismiss existing approvals.</summary>
    public bool DismissStaleApprovals { get; set; }
    /// <summary>Site admins and project Owners are exempt from this rule.</summary>
    public bool AllowAdminBypass { get; set; }
    /// <summary>Users added as reviewers to new pull requests that target a matching branch.</summary>
    public List<int> DefaultReviewerIds { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A user asked to review a pull request. Their verdict, if any, is their <see cref="PullRequestReview"/>.</summary>
public class PullRequestReviewer
{
    public int Id { get; set; }
    public int PullRequestId { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public int? AddedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ----- Pipelines -----

/// <summary>Status of a pipeline, job or step. <see cref="Skipped"/> is used for jobs and steps only.</summary>
public enum PipelineStatus { Queued, Running, Passed, Failed, Canceled, Error, Skipped }

/// <summary>What started a pipeline.</summary>
public enum PipelineTrigger { Push, PullRequest, Manual, Retry }

/// <summary>One CI run of <c>.gitbench-ci.yml</c> at a commit on a branch. Run by <see cref="GitBench.Services.Ci.CiRunner"/>.</summary>
public class Pipeline
{
    public int Id { get; set; }
    public int RepositoryId { get; set; }
    public Repository Repository { get; set; } = null!;
    /// <summary>Branch name (e.g. <c>main</c>, <c>feature/x</c>).</summary>
    [Required, MaxLength(250)] public string Ref { get; set; } = "";
    [Required, MaxLength(40)] public string CommitSha { get; set; } = "";
    [MaxLength(200)] public string? CommitMessage { get; set; }
    public int? PullRequestId { get; set; }
    public PipelineStatus Status { get; set; } = PipelineStatus.Queued;
    public PipelineTrigger Trigger { get; set; }
    public int? TriggeredById { get; set; }
    public User? TriggeredBy { get; set; }
    /// <summary>Why the pipeline is in the Error state (invalid YAML, Docker unavailable, ...).</summary>
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public List<PipelineJob> Jobs { get; set; } = [];

    public bool IsFinished => Status is not (PipelineStatus.Queued or PipelineStatus.Running);
}

public class PipelineJob
{
    public int Id { get; set; }
    public int PipelineId { get; set; }
    public Pipeline Pipeline { get; set; } = null!;
    [Required, MaxLength(64)] public string Name { get; set; } = "";
    [Required, MaxLength(300)] public string Image { get; set; } = "";
    /// <summary>Comma-separated names of jobs that must pass first.</summary>
    [MaxLength(1000)] public string Needs { get; set; } = "";
    public int Order { get; set; }
    public PipelineStatus Status { get; set; } = PipelineStatus.Queued;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public List<PipelineStep> Steps { get; set; } = [];

    public IEnumerable<string> NeedsList => Needs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public class PipelineStep
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public PipelineJob Job { get; set; } = null!;
    public int Order { get; set; }
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    [Required] public string Run { get; set; } = "";
    public PipelineStatus Status { get; set; } = PipelineStatus.Queued;
    public int? ExitCode { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public PipelineStepLog? Log { get; set; }
}

/// <summary>A step's combined stdout/stderr, kept apart from the step row so listing steps never loads logs.</summary>
public class PipelineStepLog
{
    public int StepId { get; set; }
    public string Text { get; set; } = "";
    public bool Truncated { get; set; }
}
// ----- Code tools -----

public partial class Repository
{
    /// <summary>Archived repositories are read-only: pushes are rejected and pull requests can't be created or merged.</summary>
    public bool IsArchived { get; set; }
    public DateTime? ArchivedAt { get; set; }
}

/// <summary>
/// A slug a repository used to have. Web URLs and git clone URLs that use it keep working: pages redirect to the
/// current slug and Git Smart HTTP serves the renamed repository. Removed with the repository.
/// </summary>
public class RepositoryRedirect
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    [Required, MaxLength(100)] public string OldSlug { get; set; } = "";
    public int RepositoryId { get; set; }
    public Repository Repository { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
