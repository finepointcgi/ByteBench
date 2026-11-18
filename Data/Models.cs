using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace GitBench.Data;

public enum IssueStatus { Backlog, Todo, InProgress, InReview, Done }
public enum IssuePriority { Lowest, Low, Medium, High, Highest }
public enum IssueType { Task, Bug, Feature, Story, Epic, Subtask }

/// <summary>An account. Id, UserName, Email and PasswordHash come from ASP.NET Core Identity.</summary>
public partial class User : IdentityUser<int>
{
    [Required, MaxLength(80)] public string DisplayName { get; set; } = "";
    [MaxLength(9)] public string Color { get; set; } = "#6366f1";

    public string Initials => string.Concat(DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));

    /// <summary>Site administrator: has every permission on every project, private ones included.</summary>
    public bool IsAdmin { get; set; }
}

public partial class Project
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

public partial class Issue
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

public partial class Label
{
    public int Id { get; set; }
    /// <summary>The owning project; null for a group label (<see cref="GroupId"/>), which every project under the group inherits.</summary>
    public int? ProjectId { get; set; }
    [Required, MaxLength(40)] public string Name { get; set; } = "";
    [MaxLength(9)] public string Color { get; set; } = "#6b7280";
    public List<Issue> Issues { get; set; } = [];
}

public partial class Milestone
{
    public int Id { get; set; }
    /// <summary>The owning project; null for a group milestone (<see cref="GroupId"/>), which every project under the group inherits.</summary>
    public int? ProjectId { get; set; }
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
public partial class PullRequest
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
public partial class PullRequestComment
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
    IssueLinked, IssueHierarchyChanged,
}

/// <summary>An in-app notification for one user. Created by <see cref="GitBench.Services.NotificationService"/>.</summary>
public partial class Notification
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
public partial class Watch
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
public enum TokenScopes { None = 0, ReadRepository = 1, WriteRepository = 2, ReadApi = 4, WriteApi = 8 }

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
public partial class BranchProtectionRule
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

// ----- Webhooks & API -----

/// <summary>
/// An outgoing webhook of a project: GitBench POSTs a signed JSON payload to <see cref="Url"/> for each subscribed event.
/// Managed by <see cref="GitBench.Services.Webhooks.WebhookService"/>, delivered by <see cref="GitBench.Services.Webhooks.WebhookWorker"/>.
/// </summary>
public class Webhook
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    [Required, MaxLength(2000)] public string Url { get; set; } = "";
    /// <summary>
    /// HMAC-SHA256 signing key (16 to 200 characters), encrypted at rest with ASP.NET Core Data Protection by
    /// <see cref="GitBench.Services.Webhooks.WebhookSecrets"/>: stored as <c>dp:</c> + ciphertext. A value without that
    /// prefix is a legacy plaintext secret, encrypted at startup. Shown to users only once (at creation and rotation).
    /// </summary>
    [Required, MaxLength(1000)] public string Secret { get; set; } = "";
    /// <summary>Comma-separated event names (see <see cref="GitBench.Services.Webhooks.WebhookEventNames"/>).</summary>
    [Required, MaxLength(1000)] public string Events { get; set; } = "";
    public bool IsActive { get; set; } = true;
    /// <summary>Failed deliveries in a row; at <see cref="GitBench.Services.Webhooks.WebhookService.MaxConsecutiveFailures"/> the hook is disabled.</summary>
    public int ConsecutiveFailures { get; set; }
    /// <summary>Set when GitBench turned the hook off automatically; cleared when someone re-activates it.</summary>
    public DateTime? DisabledAt { get; set; }
    public int CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastDeliveryAt { get; set; }
}

/// <summary>One delivery (with its retries) of an event to a webhook.</summary>
public class WebhookDelivery
{
    public int Id { get; set; }
    public int WebhookId { get; set; }
    /// <summary>The X-GitBench-Delivery GUID. A redelivery reuses the original's.</summary>
    [Required, MaxLength(36)] public string DeliveryId { get; set; } = "";
    [Required, MaxLength(60)] public string Event { get; set; } = "";
    /// <summary>The exact JSON body that was (or will be) sent and signed.</summary>
    [Required] public string Payload { get; set; } = "";
    public bool IsRedelivery { get; set; }
    public bool? Success { get; set; }
    /// <summary>HTTP status of the last attempt; null for network errors, timeouts and blocked addresses.</summary>
    public int? StatusCode { get; set; }
    [MaxLength(1000)] public string? Error { get; set; }
    public int Attempts { get; set; }
    /// <summary>Duration of the last attempt.</summary>
    public int DurationMs { get; set; }
    /// <summary>The first 4 KB of the last response body.</summary>
    [MaxLength(4096)] public string? ResponseBody { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

// ----- Linked work -----

/// <summary>Hierarchy: an issue's parent (an epic, or for a sub-task its story/task). Rules: <see cref="GitBench.Services.HierarchyRules"/>.</summary>
public partial class Issue
{
    public int? ParentId { get; set; }
    public Issue? Parent { get; set; }
    public List<Issue> Children { get; set; } = [];

    /// <summary>Not stored: parent key, sub-task progress and blocked flag, filled in for lists and the board.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped] public IssueWorkInfo? Work { get; set; }
}

/// <summary>Display info for list rows and board cards (see <see cref="Issue.Work"/>).</summary>
public sealed record IssueWorkInfo(string? ParentKey, int? ParentNumber, string? ParentTitle, int ChildDone, int ChildTotal, IReadOnlyList<string> BlockedBy)
{
    public bool Blocked => BlockedBy.Count > 0;
}

/// <summary>Kinds of issue link. Stored from the source's point of view: Source blocks / relates to / duplicates / clones Target.</summary>
public enum IssueLinkType { Blocks, RelatesTo, Duplicates, Clones }

/// <summary>A directed link between two issues, possibly in different projects.</summary>
public class IssueLink
{
    public int Id { get; set; }
    public int SourceIssueId { get; set; }
    public Issue SourceIssue { get; set; } = null!;
    public int TargetIssueId { get; set; }
    public Issue TargetIssue { get; set; } = null!;
    public IssueLinkType Type { get; set; }
    public int? CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ----- Attachments & filters -----

/// <summary>
/// A file attached to an issue (from the issue page, a comment or the API). The bytes live on disk at
/// <see cref="GitBench.Services.Attachments.AttachmentStorage.PathFor"/>(<see cref="StoredName"/>), never under the user's filename.
/// </summary>
public class Attachment
{
    public int Id { get; set; }
    public int IssueId { get; set; }
    public Issue Issue { get; set; } = null!;
    /// <summary>Random 32-hex-character id; the file's name on disk.</summary>
    [Required, MaxLength(32)] public string StoredName { get; set; } = "";
    /// <summary>Sanitized original file name (display and Content-Disposition only).</summary>
    [Required, MaxLength(255)] public string FileName { get; set; } = "";
    /// <summary>Raster image types are only recorded when the file's bytes match them; otherwise from the extension, or octet-stream.</summary>
    [Required, MaxLength(100)] public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    /// <summary>Lowercase hex SHA-256 of the content.</summary>
    [Required, MaxLength(64)] public string Sha256 { get; set; } = "";
    public int UploaderId { get; set; }
    public User Uploader { get; set; } = null!;
    /// <summary>True when uploaded from a comment box (needs Comment rather than EditIssues).</summary>
    public bool FromComment { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool IsImage => GitBench.Services.Attachments.AttachmentStorage.IsInlineImage(ContentType);
}

/// <summary>
/// A named issue-list filter: the list's URL query string (see <see cref="GitBench.Services.IssueFilterQuery"/>) plus a name.
/// Private to its owner unless <see cref="IsShared"/>, which makes it visible to everyone who can view the project.
/// </summary>
public class SavedFilter
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int OwnerId { get; set; }
    public User Owner { get; set; } = null!;
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    /// <summary>Query string without the leading '?', e.g. <c>status=InProgress&amp;assigneeId=3</c>.</summary>
    [MaxLength(2000)] public string Query { get; set; } = "";
    public bool IsShared { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A user's star on a saved filter (own or shared); starred filters show in the sidebar under the project.</summary>
public class SavedFilterStar
{
    public int Id { get; set; }
    public int SavedFilterId { get; set; }
    public int UserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ----- Sprints -----

public enum SprintState { Planned, Active, Completed }

/// <summary>
/// A time-boxed iteration of a project (Scrum-style). At most one sprint per project is <see cref="SprintState.Active"/>.
/// Managed by <see cref="GitBench.Services.SprintService"/>; report history is rebuilt from issue activity by
/// <see cref="GitBench.Services.SprintHistory"/>.
/// </summary>
public class Sprint
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    [MaxLength(500)] public string? Goal { get; set; }
    /// <summary>Planned dates (UTC calendar dates, time 00:00).</summary>
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public SprintState State { get; set; } = SprintState.Planned;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>When someone pressed "Start sprint" / "Complete sprint".</summary>
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    /// <summary>Snapshot taken when the sprint started: issues and story points in it (the "committed" scope for velocity).</summary>
    public int? CommittedCount { get; set; }
    public int? CommittedPoints { get; set; }
    /// <summary>Snapshot taken when the sprint was completed: issues and story points done in it.</summary>
    public int? CompletedCount { get; set; }
    public int? CompletedPoints { get; set; }
}

public partial class Issue
{
    /// <summary>The sprint this issue is planned in; null = the backlog.</summary>
    public int? SprintId { get; set; }
    public Sprint? Sprint { get; set; }
}

// ----- Workflows & audit -----

public partial class Project
{
    /// <summary>The project's workflow: its issue statuses, in board order. See <see cref="GitBench.Services.WorkflowService"/>.</summary>
    public List<WorkflowStatus> WorkflowStatuses { get; set; } = [];
}

public partial class Issue
{
    /// <summary>
    /// The issue's status in its project's workflow. <see cref="Status"/> always equals this status's
    /// <see cref="WorkflowStatus.Category"/>: <see cref="GitBench.Services.IssueService"/> sets both together.
    /// </summary>
    public int? WorkflowStatusId { get; set; }
    public WorkflowStatus? WorkflowStatus { get; set; }
}

/// <summary>
/// A per-project issue status (e.g. "Blocked", "QA"). Its <see cref="Category"/> is one of the five built-in
/// <see cref="IssueStatus"/> values, which code that only knows categories (sprints, the API, filters) keeps using.
/// </summary>
public class WorkflowStatus
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    [Required, MaxLength(40)] public string Name { get; set; } = "";
    [MaxLength(9)] public string Color { get; set; } = "#64748b";
    /// <summary>Board column order (ascending).</summary>
    public int Order { get; set; }
    public IssueStatus Category { get; set; }
}

/// <summary>
/// An allowed move from one workflow status to another. A status with no outgoing transitions may move anywhere;
/// once it has at least one, only the listed targets are allowed.
/// </summary>
public class WorkflowTransition
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int FromStatusId { get; set; }
    public int ToStatusId { get; set; }
}

/// <summary>
/// One security-relevant event (sign-in, membership change, settings change, ...). Append-only: the app never updates
/// or deletes these rows except for retention cleanup (<see cref="GitBench.Services.Audit.AuditRetentionService"/>).
/// No foreign keys, so events outlive the users and projects they mention. <see cref="Details"/> never holds secrets.
/// </summary>
public class AuditEvent
{
    public long Id { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>The acting user; null for the system or an anonymous caller (e.g. a failed sign-in).</summary>
    public int? ActorId { get; set; }
    /// <summary>The actor's username when the event was recorded (kept if the account is later renamed or deleted).</summary>
    [MaxLength(256)] public string? ActorName { get; set; }
    [MaxLength(64)] public string? IpAddress { get; set; }
    [MaxLength(512)] public string? UserAgent { get; set; }
    /// <summary>Dotted name, e.g. <c>member.role_changed</c>.</summary>
    [Required, MaxLength(100)] public string Action { get; set; } = "";
    [MaxLength(50)] public string? TargetType { get; set; }
    [MaxLength(100)] public string? TargetId { get; set; }
    public int? ProjectId { get; set; }
    /// <summary>JSON object with event-specific details.</summary>
    public string? Details { get; set; }
}

// ----- Email & preferences -----

/// <summary>How a user hears about one <see cref="NotificationGroup"/>. A missing row means the group's default.</summary>
public enum NotificationChannel { Off = 0, InApp = 1, InAppAndEmail = 2 }

/// <summary>The groups on the preferences page. Each <see cref="NotificationKind"/> belongs to exactly one (see <c>NotificationPreferences.GroupOf</c>).</summary>
public enum NotificationGroup
{
    Assigned, Mentioned, WatchedComments, WatchedChanges, ReviewRequested, Reviews, Pipelines, IssueLinks,
}

/// <summary>A user's choice for one <see cref="NotificationGroup"/>.</summary>
public class NotificationPreference
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public NotificationGroup Group { get; set; }
    public NotificationChannel Channel { get; set; }
}

/// <summary>Per-user email settings: one row per user, created on first save or first email.</summary>
public class UserNotificationSettings
{
    [Key, System.ComponentModel.DataAnnotations.Schema.DatabaseGenerated(System.ComponentModel.DataAnnotations.Schema.DatabaseGeneratedOption.None)]
    public int UserId { get; set; }
    /// <summary>Master switch: false means no notification emails at all, whatever the groups say.</summary>
    public bool EmailEnabled { get; set; } = true;
    /// <summary>When the last notification email (single or digest) went to this user; drives <c>Email:DigestMinutes</c>.</summary>
    public DateTime? LastEmailAt { get; set; }
}

public partial class Notification
{
    /// <summary>True while this notification still has to be emailed (immediately, or in the user's next digest).</summary>
    public bool EmailPending { get; set; }
    /// <summary>Markdown source of the comment or description behind it (shortened), shown in the email. Never rendered as raw HTML.</summary>
    [MaxLength(1200)] public string? Excerpt { get; set; }
}

public partial class Watch
{
    /// <summary>
    /// Muted: the user stays a watcher, so auto-watching on comment, assignment or review doesn't bring the notifications
    /// back, but gets no watcher notifications. Direct ones (assigned, mentioned, review requested) still arrive.
    /// </summary>
    public bool Muted { get; set; }
}

// ----- Admin, 2FA & CI secrets -----

public partial class User
{
    /// <summary>
    /// Set when a site admin deactivates the account (it is then locked indefinitely and its access tokens are revoked).
    /// Cleared when an admin unlocks it.
    /// </summary>
    public DateTime? DeactivatedAt { get; set; }
}

/// <summary>
/// A CI secret variable, injected into pipeline step containers as an environment variable. Belongs to a project
/// (<see cref="RepositoryId"/> null: every repository in it) or to one repository, which overrides a project variable
/// with the same name. The value is encrypted with ASP.NET Core Data Protection and never shown again after saving.
/// </summary>
public class CiVariable
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int? RepositoryId { get; set; }
    /// <summary>Environment variable name: letters, digits and underscores, not starting with a digit.</summary>
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    /// <summary>The value, protected by <see cref="GitBench.Services.Ci.CiVariableService"/> (Data Protection).</summary>
    [Required] public string EncryptedValue { get; set; } = "";
    /// <summary>Only exposed to pipelines whose branch is protected (see <see cref="GitBench.Services.BranchProtectionService"/>).</summary>
    public bool IsProtected { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int? UpdatedById { get; set; }
    public User? UpdatedBy { get; set; }
}

// ----- Releases -----

/// <summary>
/// A release of a repository: a git tag plus a title, Markdown notes and downloadable assets. Drafts are visible only to
/// people who can push (Developer+); publishing sets <see cref="PublishedAt"/> once. The tag must exist in the repository
/// (it can be created from a branch when the release is created) and can't be deleted while a release uses it.
/// </summary>
public class Release
{
    public int Id { get; set; }
    public int RepositoryId { get; set; }
    public Repository Repository { get; set; } = null!;
    /// <summary>Short tag name (without <c>refs/tags/</c>). Unique per repository.</summary>
    [Required, MaxLength(250)] public string TagName { get; set; } = "";
    [Required, MaxLength(200)] public string Title { get; set; } = "";
    /// <summary>Markdown.</summary>
    public string? Notes { get; set; }
    public bool IsPrerelease { get; set; }
    public bool IsDraft { get; set; }
    public int AuthorId { get; set; }
    public User Author { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>When the release was first published (stays set if it is turned back into a draft).</summary>
    public DateTime? PublishedAt { get; set; }
    public List<ReleaseAsset> Assets { get; set; } = [];
}

/// <summary>
/// A file attached to a release. Stored like issue attachments (<see cref="GitBench.Services.Attachments.AttachmentStorage"/>,
/// random 32-hex name, never the user's filename) but in its own table.
/// </summary>
public class ReleaseAsset
{
    public int Id { get; set; }
    public int ReleaseId { get; set; }
    public Release Release { get; set; } = null!;
    [Required, MaxLength(32)] public string StoredName { get; set; } = "";
    [Required, MaxLength(255)] public string FileName { get; set; } = "";
    [Required, MaxLength(100)] public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    [Required, MaxLength(64)] public string Sha256 { get; set; } = "";
    public int UploaderId { get; set; }
    public User Uploader { get; set; } = null!;
    public int DownloadCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ----- Explore & profiles -----

/// <summary>A user's star on a project (Explore, the sidebar's starred projects). One per user and project.</summary>
public class ProjectStar
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public int UserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One branch or tag that a <c>git push</c> moved, recorded for the profile activity feed and contribution calendar
/// (<see cref="GitBench.Services.Explore.PushActivityListener"/>).
/// </summary>
public class PushEvent
{
    public long Id { get; set; }
    public int ProjectId { get; set; }
    public int RepositoryId { get; set; }
    public Repository Repository { get; set; } = null!;
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    /// <summary>Full ref name, e.g. <c>refs/heads/main</c>.</summary>
    [Required, MaxLength(250)] public string Ref { get; set; } = "";
    [MaxLength(40)] public string? Before { get; set; }
    [MaxLength(40)] public string? After { get; set; }
    /// <summary>Commits the push added to the ref (capped at <see cref="GitBench.Services.Explore.PushActivityListener.MaxCommits"/>); 0 for a deletion.</summary>
    public int CommitCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public partial class User
{
    /// <summary>When the account was created ("member since"). Null for accounts created before this was recorded.</summary>
    public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;
}

// ----- Groups -----

/// <summary>
/// A GitLab-style namespace that owns projects and subgroups (nested up to <see cref="MaxDepth"/> levels). Its URL is
/// <c>/g/{FullPath}</c>. Members' roles are inherited by every subgroup and project below it
/// (see <see cref="GitBench.Services.ProjectAccess"/>).
/// </summary>
public class Group
{
    public const int MaxDepth = 4;
    public const string PathPattern = "^[a-z0-9][a-z0-9_.-]{0,49}$";

    public int Id { get; set; }
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    /// <summary>This group's own URL segment, e.g. <c>platform</c>.</summary>
    [Required, MaxLength(50), RegularExpression(PathPattern, ErrorMessage = "Use lowercase letters, digits, '.', '_' or '-'.")]
    public string Path { get; set; } = "";
    /// <summary>Parent paths plus <see cref="Path"/>, e.g. <c>acme/platform</c>. Unique; kept in sync by GroupService.</summary>
    [Required, MaxLength(260)] public string FullPath { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Private: only members (and members of projects/subgroups in it) see the group. Internal: every signed-in user.</summary>
    public ProjectVisibility Visibility { get; set; } = ProjectVisibility.Private;
    [MaxLength(9)] public string Color { get; set; } = "#6366f1";
    public int? ParentId { get; set; }
    public Group? Parent { get; set; }
    public List<Group> Children { get; set; } = [];
    public List<GroupMember> Members { get; set; } = [];
    public List<Project> Projects { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>1 for a top-level group.</summary>
    public int Depth => FullPath.Count(c => c == '/') + 1;
}

/// <summary>A user's role in a group (at most one row per group and user). Inherited by subgroups and projects.</summary>
public class GroupMember
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public Group Group { get; set; } = null!;
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public ProjectRole Role { get; set; } = ProjectRole.Developer;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>An old <see cref="Group.FullPath"/> that still leads to the group after a rename or move.</summary>
public class GroupRedirect
{
    public int Id { get; set; }
    [Required, MaxLength(260)] public string OldPath { get; set; } = "";
    public int GroupId { get; set; }
    public Group Group { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public partial class Project
{
    /// <summary>The group that owns this project; null for a top-level project.</summary>
    public int? GroupId { get; set; }
    public Group? Group { get; set; }
}

public partial class Label
{
    /// <summary>Set for a group label (then <see cref="ProjectId"/> is null).</summary>
    public int? GroupId { get; set; }
}

public partial class Milestone
{
    /// <summary>Set for a group milestone (then <see cref="ProjectId"/> is null).</summary>
    public int? GroupId { get; set; }
}

// ----- PR features -----

/// <summary>How a pull request's commits reach the target branch.</summary>
public enum MergeStrategy
{
    /// <summary>A two-parent merge commit (or a fast-forward, when asked for and possible).</summary>
    Merge,
    /// <summary>One new commit on the target with the PR's combined changes.</summary>
    Squash,
    /// <summary>The PR's commits replayed one by one onto the target, which is then fast-forwarded.</summary>
    Rebase,
}

/// <summary>A set of <see cref="MergeStrategy"/> values (bit = 1 &lt;&lt; strategy).</summary>
[Flags]
public enum MergeStrategies { None = 0, Merge = 1, Squash = 2, Rebase = 4, All = Merge | Squash | Rebase }

public partial class PullRequest
{
    /// <summary>Work in progress: can't be merged, and assigned reviewers aren't notified until it's marked ready.</summary>
    public bool IsDraft { get; set; }
    /// <summary>How the PR was merged (null while unmerged, and for PRs merged before strategies existed).</summary>
    public MergeStrategy? MergeStrategy { get; set; }
}

/// <summary>
/// Threads: an inline comment without <see cref="ParentId"/> starts a thread; replies point at it and copy its
/// position. Resolution state lives on the thread's first comment.
/// </summary>
public partial class PullRequestComment
{
    public int? ParentId { get; set; }
    public bool IsResolved { get; set; }
    public int? ResolvedById { get; set; }
    public User? ResolvedBy { get; set; }
    public DateTime? ResolvedAt { get; set; }
    /// <summary>Source-branch head the inline comment was written on (null for older comments and replies).</summary>
    [MaxLength(40)] public string? CommitSha { get; set; }
    /// <summary>First new-side line of a multi-line comment (null: the comment covers <see cref="PullRequestComment.Line"/> only).</summary>
    public int? StartLine { get; set; }
    /// <summary>The commented line(s) as they read at <see cref="CommitSha"/>, joined with \n. Used to spot outdated threads and stale suggestions.</summary>
    public string? LineText { get; set; }
    /// <summary>The commit that applied this comment's suggestion, once applied.</summary>
    [MaxLength(40)] public string? AppliedCommitSha { get; set; }

    public bool IsThreadRoot => FilePath != null && ParentId == null;
}

public partial class BranchProtectionRule
{
    /// <summary>Every inline comment thread on the PR must be resolved before merging.</summary>
    public bool RequireThreadResolution { get; set; }
    /// <summary>Each changed file with CODEOWNERS owners needs an approval from one of them.</summary>
    public bool RequireCodeOwnerApproval { get; set; }
}

public partial class Repository
{
    /// <summary>Merge strategies pull requests may use (null: all of them).</summary>
    public MergeStrategies? AllowedMergeStrategies { get; set; }
    /// <summary>The strategy preselected in the merge box (null: merge commit).</summary>
    public MergeStrategy? DefaultMergeStrategy { get; set; }
}

// ----- SSH -----

/// <summary>
/// An OpenSSH public key that authenticates git over SSH (<see cref="GitBench.Services.Ssh.SshGitServer"/>). Either a
/// user's key (<see cref="UserId"/> set: acts as that user, with their project permissions) or a repository's deploy key
/// (<see cref="RepositoryId"/> set: only that repository, read-only unless <see cref="CanWrite"/>). Both kinds share this
/// table so a <see cref="Fingerprint"/> is unique across all users and deploy keys.
/// </summary>
public class SshKey
{
    public int Id { get; set; }
    /// <summary>The owner of a user key; null for a deploy key.</summary>
    public int? UserId { get; set; }
    public User? User { get; set; }
    /// <summary>The repository of a deploy key; null for a user key.</summary>
    public int? RepositoryId { get; set; }
    public Repository? Repository { get; set; }
    /// <summary>Deploy keys only: may push (subject to branch protection, like a Developer).</summary>
    public bool CanWrite { get; set; }
    [Required, MaxLength(100)] public string Title { get; set; } = "";
    /// <summary>The key type, e.g. <c>ssh-ed25519</c>, <c>ecdsa-sha2-nistp256</c> or <c>ssh-rsa</c>.</summary>
    [Required, MaxLength(40)] public string KeyType { get; set; } = "";
    /// <summary>The public key blob in base64, as in an <c>authorized_keys</c> line.</summary>
    [Required, MaxLength(4096)] public string PublicKey { get; set; } = "";
    /// <summary><c>SHA256:</c> + unpadded base64 of the SHA-256 of the key blob (as <c>ssh-keygen -l</c> prints it).</summary>
    [Required, MaxLength(64)] public string Fingerprint { get; set; } = "";
    /// <summary>Who added the key. For a deploy key, pushes run their push listeners (webhooks, CI, smart commits) as this user.</summary>
    public int? CreatedById { get; set; }
    public User? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }

    public bool IsDeployKey => RepositoryId != null;
    public bool IsExpired(DateTime utcNow) => ExpiresAt != null && ExpiresAt <= utcNow;
}
