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
public class Repository
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
