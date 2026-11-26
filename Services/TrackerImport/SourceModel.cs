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

/// <summary>
/// A file attached in the source. <see cref="Url"/> is a link a person can open; <see cref="DownloadUrl"/> is what the importer fetches
/// (with the source's credentials only when it is on the source's own address) when "include attachments" is on. <see cref="ExternalId"/>
/// is stable and unique within the issue (Jira's attachment id, GitHub's upload path, GitLab's upload hash and name) so re-runs don't
/// copy a file twice. <see cref="BodyRef"/> is the exact link text as it appears in the issue's or a comment's markdown: when the file
/// was copied, that text is replaced by the local attachment's address. Attachments without a <see cref="BodyRef"/> (Jira's) are only
/// attached to the issue.
/// </summary>
public sealed record SourceAttachment(string FileName, string? Url, string? DownloadUrl = null, string? ExternalId = null, string? BodyRef = null, long? Size = null);

/// <summary>A sprint of the source (Jira). <see cref="ExternalId"/> is the source's sprint id; <see cref="SourceIssue.SprintId"/> refers to it.</summary>
public sealed record SourceSprint(string ExternalId, string Name, string? Goal, DateTime? StartDate, DateTime? EndDate, SprintState State);

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
    /// <summary>A <see cref="SourceSprint.ExternalId"/>: the sprint the issue sits in (an issue that was in several sprints names the latest).</summary>
    public string? SprintId { get; init; }
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
/// <param name="Notice">Where a reader reports something the user should know about what it left out (shown as an import warning).</param>
/// <param name="IncludeAttachments">Also report files linked from issue and comment text (GitHub, GitLab); Jira always reports its attachment list.</param>
public sealed record SourceReadOptions(bool IncludeClosed, bool IncludeComments, bool IncludePullRequests, int MaxIssues, bool IncludeAttachments = false, Action<string>? Notice = null);

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

    /// <summary>The source's sprints (called before the issues). Sources without sprints keep this default.</summary>
    Task<IReadOnlyList<SourceSprint>> ReadSprintsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SourceSprint>>([]);

    /// <summary>
    /// Opens the file of an attachment (its <see cref="SourceAttachment.DownloadUrl"/>) for reading, with the source's credentials applied by
    /// <see cref="TrackerHttp.GetStreamAsync"/>. The stream stops (throws <see cref="Attachments.AttachmentTooLargeException"/>) past
    /// <paramref name="maxBytes"/>; the caller disposes it. Throw <see cref="SourceException"/> for a file that can't be fetched.
    /// </summary>
    Task<Stream> OpenAttachmentAsync(SourceAttachment attachment, CancellationToken ct, long maxBytes = TrackerHttp.DefaultAttachmentMaxBytes) =>
        throw new SourceException("This source can't hand out attachment files.");
}
