using System.Text.Json;
using System.Text.Json.Serialization;
using GitBench.Data;
using GitBench.Services.Git;

namespace GitBench.Api;

// Wire types for the REST API (/api/v1) and webhook payloads. EF entities are never serialized directly.
// JSON is camelCase, enums are camelCase strings, timestamps are ISO 8601 UTC and dates are yyyy-MM-dd.

public record UserDto(int Id, string Username, string DisplayName, string AvatarColor);

public record CurrentUserDto(int Id, string Username, string DisplayName, string? Email, string AvatarColor, bool IsAdmin, IReadOnlyList<string>? Scopes);

public record ProjectDto(int Id, string Key, string Name, string? Description, ProjectVisibility Visibility,
    DateTimeOffset CreatedAt, ProjectRole? Role, string WebUrl);

/// <summary>The small project object embedded in webhook payloads.</summary>
public record ProjectRefDto(int Id, string Key, string Name, string WebUrl);

public record LabelDto(int Id, string Name, string Color);

public record MilestoneRefDto(int Id, string Title);

public record MilestoneDto(int Id, string Title, string? Description, DateOnly? StartDate, DateOnly? DueDate, bool IsClosed,
    int OpenIssues, int ClosedIssues);

public record IssueDto(int Id, string Key, int Number, string Title, string? Description,
    IssueType Type, IssueStatus Status, IssuePriority Priority, int? StoryPoints, DateOnly? DueDate,
    UserDto? Assignee, UserDto Reporter, MilestoneRefDto? Milestone, IReadOnlyList<LabelDto> Labels,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ClosedAt, string WebUrl);

public record CommentDto(int Id, string Body, UserDto Author, DateTimeOffset CreatedAt, DateTimeOffset? EditedAt);

public record RepositoryDto(int Id, string Slug, string Name, string? Description, string DefaultBranch,
    DateTimeOffset CreatedAt, string CloneUrl, string WebUrl);

public record CommitDto(string Sha, string Message, string AuthorName, string AuthorEmail, DateTimeOffset Timestamp,
    IReadOnlyList<string> Parents);

public record BranchDto(string Name, bool IsDefault, CommitDto? Commit);

public record ReviewDto(UserDto User, ReviewState State, DateTimeOffset CreatedAt);

public record PullRequestDto(int Id, int Number, string Repository, string Title, string? Description,
    string SourceBranch, string TargetBranch, PullRequestState State, UserDto Author, UserDto? MergedBy,
    string? MergeCommitSha, IReadOnlyList<ReviewDto> Reviews,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ClosedAt, string WebUrl);

public record PullRequestCommentDto(int Id, string Body, UserDto Author, string? Path, int? Line, int? OldLine,
    DateTimeOffset CreatedAt, DateTimeOffset? EditedAt);

public record MergeResultDto(bool Merged, string? MergeCommitSha, bool FastForwarded, IReadOnlyList<string> ConflictPaths, PullRequestDto PullRequest);

// ----- Request bodies -----

public record CreateIssueRequest(string Title, string? Description = null, IssueType? Type = null, IssuePriority? Priority = null,
    IssueStatus? Status = null, string? Assignee = null, IReadOnlyList<string>? Labels = null, int? MilestoneId = null,
    int? StoryPoints = null, DateOnly? DueDate = null);

/// <summary>
/// PATCH semantics: only fields present in the JSON change. <c>null</c> clears assignee, milestoneId, storyPoints,
/// dueDate and description. <c>labels</c> replaces the whole label set.
/// </summary>
public record UpdateIssueRequest(string? Title = null, string? Description = null, IssueType? Type = null, IssuePriority? Priority = null,
    IssueStatus? Status = null, string? Assignee = null, IReadOnlyList<string>? Labels = null, int? MilestoneId = null,
    int? StoryPoints = null, DateOnly? DueDate = null);

public record AddCommentRequest(string Body);

public record CreatePullRequestRequest(string Title, string SourceBranch, string TargetBranch, string? Description = null);

public record MergePullRequestRequest(bool DeleteSourceBranch = false, bool FastForward = false);

public record AddPullRequestCommentRequest(string Body, string? Path = null, int? Line = null, int? OldLine = null);

/// <summary>Mapping from entities and git snapshots to DTOs, plus the JSON settings shared by the API and webhooks.</summary>
public static class Dto
{
    /// <summary>camelCase properties, camelCase enum strings, nulls kept (stable shape for clients).</summary>
    public static readonly JsonSerializerOptions Json = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions o)
    {
        o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.NumberHandling = JsonNumberHandling.Strict; // numbers are numbers, not "5"
        if (!o.Converters.OfType<JsonStringEnumConverter>().Any())
            o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }

    /// <summary>SQLite returns DateTime with Kind=Unspecified; every stored timestamp is UTC.</summary>
    public static DateTimeOffset Utc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));
    public static DateTimeOffset? Utc(DateTime? d) => d is { } x ? Utc(x) : null;
    public static DateOnly? Date(DateTime? d) => d is { } x ? DateOnly.FromDateTime(x) : null;

    public static UserDto User(User u) => new(u.Id, u.UserName ?? "", u.DisplayName, u.Color);
    public static UserDto? UserOrNull(User? u) => u == null ? null : User(u);

    public static ProjectDto Project(Project p, ProjectRole? role, string baseUrl) =>
        new(p.Id, p.Key, p.Name, p.Description, p.Visibility, Utc(p.CreatedAt), role, $"{baseUrl}/p/{p.Key}");

    public static ProjectRefDto ProjectRef(Project p, string baseUrl) => new(p.Id, p.Key, p.Name, $"{baseUrl}/p/{p.Key}");

    public static LabelDto Label(Label l) => new(l.Id, l.Name, l.Color);

    /// <summary>Requires Project, Assignee, Reporter, Milestone and Labels to be loaded.</summary>
    public static IssueDto Issue(Issue i, string baseUrl) => new(
        i.Id, i.Key, i.Number, i.Title, i.Description, i.Type, i.Status, i.Priority, i.StoryPoints, Date(i.DueDate),
        UserOrNull(i.Assignee), User(i.Reporter), i.Milestone is { } m ? new MilestoneRefDto(m.Id, m.Title) : null,
        i.Labels.OrderBy(l => l.Name).Select(Label).ToList(),
        Utc(i.CreatedAt), Utc(i.UpdatedAt), Utc(i.ClosedAt), $"{baseUrl}/p/{i.Project.Key}/issues/{i.Number}");

    public static CommentDto Comment(Comment c) => new(c.Id, c.Body, User(c.Author), Utc(c.CreatedAt), Utc(c.EditedAt));

    /// <summary>Requires Project to be loaded.</summary>
    public static RepositoryDto Repository(Repository r, string baseUrl) => new(
        r.Id, r.Slug, r.Name, r.Description, r.DefaultBranch, Utc(r.CreatedAt),
        RepoUrls.CloneUrl(baseUrl, r), baseUrl + RepoUrls.Base(r));

    public static CommitDto Commit(CommitInfo c) => new(c.Sha, c.Message, c.AuthorName, c.AuthorEmail, c.When.ToUniversalTime(), c.ParentShas);

    public static BranchDto Branch(BranchInfo b) => new(b.Name, b.IsDefault, b.Tip is { } t ? Commit(t) : null);

    /// <summary>Requires Repository.Project, Author, MergedBy and Reviews.User to be loaded.</summary>
    public static PullRequestDto PullRequest(PullRequest p, string baseUrl) => new(
        p.Id, p.Number, p.Repository.Slug, p.Title, p.Description, p.SourceBranch, p.TargetBranch, p.State,
        User(p.Author), UserOrNull(p.MergedBy), p.MergeCommitSha,
        p.Reviews.OrderBy(r => r.CreatedAt).Select(r => new ReviewDto(User(r.User), r.State, Utc(r.CreatedAt))).ToList(),
        Utc(p.CreatedAt), Utc(p.UpdatedAt), Utc(p.ClosedAt), baseUrl + RepoUrls.PullRequest(p));

    public static PullRequestCommentDto PullRequestComment(PullRequestComment c) =>
        new(c.Id, c.Body, User(c.Author), c.FilePath, c.Line, c.OldLine, Utc(c.CreatedAt), Utc(c.EditedAt));
}
