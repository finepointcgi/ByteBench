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
