using ByteBench.Data;

namespace ByteBench.Services.TrackerImport;

/// <summary>Where to read from. <see cref="Secret"/> is held in memory for one run and never stored.</summary>
public sealed record SourceConnection(TrackerSourceKind Kind, string? BaseUrl, string Project, string? Username, string? Secret);

/// <summary>A person in the source. <see cref="Login"/> is the source's username; email is often not shown by the source.</summary>
public sealed record SourceUser(string? Login, string? Name, string? Email);

public sealed record SourceLabel(string Name, string? Color);

/// <summary>A status of the source's workflow and which of ByteBench's five categories it counts as (only Jira has real statuses).</summary>
public sealed record SourceStatus(string Name, IssueStatus Category);

public sealed record SourceMilestone(string ExternalId, string Title, string? Description, DateTime? StartDate, DateTime? DueDate, bool Closed);

public sealed record SourceComment(string ExternalId, SourceUser? Author, string Body, DateTime CreatedAt);

/// <summary>A link to another issue of the same source project, by its <see cref="SourceIssue.ExternalId"/>.</summary>
public sealed record SourceLink(string TargetExternalId, IssueLinkType Type);

/// <summary>A file attached in the source. Files aren't copied; they are listed in the issue so they can be fetched from the source.</summary>
public sealed record SourceAttachment(string FileName, string? Url);

/// <summary>
/// One issue in neutral form. Readers fill what the source has and leave the rest null/empty; the writer maps it into a project.
/// Text is markdown (readers convert Jira's formats to markdown).
/// </summary>
public sealed class SourceIssue
{
    /// <summary>The source's stable id (never reused): GitHub's issue id, GitLab's global id, Jira's issue key. Used by re-imports.</summary>
    public required string ExternalId { get; init; }
    /// <summary>How people refer to it in the source: <c>#12</c>, <c>#12</c>, <c>PROJ-12</c>.</summary>
    public required string DisplayKey { get; init; }
    public string? Url { get; init; }
    public required string Title { get; init; }
    public string? Body { get; init; }
    public IssueType Type { get; init; } = IssueType.Task;
    /// <summary>The source's status name (Jira: "In Review"); null for open/closed-only sources.</summary>
    public string? StatusName { get; init; }
    public IssueStatus Category { get; init; } = IssueStatus.Todo;
    public IssuePriority? Priority { get; init; }
    public int? StoryPoints { get; init; }
    public DateTime? DueDate { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; init; }
    public DateTime? ClosedAt { get; init; }
    public SourceUser? Author { get; init; }
    public SourceUser? Assignee { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = [];
    /// <summary>A <see cref="SourceMilestone.ExternalId"/> (Jira: the fix version or sprint chosen by the reader).</summary>
    public string? MilestoneId { get; init; }
    /// <summary>The parent issue's <see cref="ExternalId"/> (an epic; for sub-tasks the issue they belong to).</summary>
    public string? ParentExternalId { get; init; }
    public IReadOnlyList<SourceLink> Links { get; init; } = [];
    public IReadOnlyList<SourceComment> Comments { get; init; } = [];
    public IReadOnlyList<SourceAttachment> Attachments { get; init; } = [];
    /// <summary>Things the reader couldn't carry over (an unsupported field, a truncated comment list); shown as warnings.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>What step 1 (check connection) shows.</summary>
public sealed record SourceProbe(string ProjectName, string? Description, int IssueCount, IReadOnlyList<string> SampleTitles, string? Warning = null);

/// <summary>What to read. Readers must honour <see cref="MaxIssues"/> and stop there.</summary>
public sealed record SourceReadOptions(bool IncludeClosed, bool IncludeComments, bool IncludePullRequests, int MaxIssues);

/// <summary>A source problem with a message that is safe to show (never contains a credential).</summary>
public sealed class SourceException(string message, bool authFailure = false) : Exception(message)
{
    public bool AuthFailure { get; } = authFailure;
}

/// <summary>
/// Reads one source project through its API. Created per run by <see cref="SourceReaderFactory"/>; callers have already
/// checked permissions and the address. Throw <see cref="SourceException"/> for problems the user can act on.
/// </summary>
public interface ISourceReader : IDisposable
{
    /// <summary>Checks the address, credentials and project. Throws <see cref="SourceException"/> (bad token, project not found, ...).</summary>
    Task<SourceProbe> ProbeAsync(CancellationToken ct);
    /// <summary>Distinct statuses of the source (open/closed-only sources return Open and Closed).</summary>
    Task<IReadOnlyList<SourceStatus>> ReadStatusesAsync(CancellationToken ct);
    Task<IReadOnlyList<SourceLabel>> ReadLabelsAsync(CancellationToken ct);
    Task<IReadOnlyList<SourceMilestone>> ReadMilestonesAsync(CancellationToken ct);
    /// <summary>Issues oldest first, comments and links included when asked. Streams: don't buffer the whole project.</summary>
    IAsyncEnumerable<SourceIssue> ReadIssuesAsync(SourceReadOptions options, CancellationToken ct);
}
