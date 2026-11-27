namespace ByteBench.Data;

/// <summary>
/// Ties a workspace to the work it was opened for: an issue (<c>Start workspace</c> on an issue page), a pull request (on a PR page, or
/// the pull request opened from the workspace bar), or both. Rows go away with the workspace, the issue or the pull request.
/// </summary>
public class WorkspaceLink
{
    public int Id { get; set; }
    public int WorkspaceId { get; set; }
    public Workspace Workspace { get; set; } = null!;
    public int? IssueId { get; set; }
    public Issue? Issue { get; set; }
    public int? PullRequestId { get; set; }
    public PullRequest? PullRequest { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
