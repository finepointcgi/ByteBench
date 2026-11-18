namespace GitBench.Services.Webhooks;

/// <summary>The event names a webhook can subscribe to (the <c>X-GitBench-Event</c> header and the payload's <c>event</c>).</summary>
public static class WebhookEventNames
{
    public const string Ping = "ping";
    public const string Push = "push";
    public const string IssueCreated = "issue.created";
    public const string IssueUpdated = "issue.updated";
    public const string IssueCommented = "issue.commented";
    public const string PullRequestOpened = "pull_request.opened";
    public const string PullRequestUpdated = "pull_request.updated";
    public const string PullRequestMerged = "pull_request.merged";
    public const string PullRequestDeclined = "pull_request.declined";
    public const string PullRequestCommented = "pull_request.commented";
    /// <summary>Raised by CI pipelines (not by this module) through <see cref="WebhookDispatcher.Enqueue"/>.</summary>
    public const string PipelineCompleted = "pipeline.completed";
    // ----- Releases -----
    public const string ReleasePublished = "release.published";
    public const string TagCreated = "tag.created";
    public const string TagDeleted = "tag.deleted";

    /// <summary>Events a hook can subscribe to, with a short description for the settings page. <c>ping</c> is always sent on request.</summary>
    public static readonly IReadOnlyList<(string Name, string Description)> Subscribable =
    [
        (Push, "Commits pushed to a repository"),
        (IssueCreated, "An issue is created"),
        (IssueUpdated, "An issue's fields or status change"),
        (IssueCommented, "Someone comments on an issue"),
        (PullRequestOpened, "A pull request is opened"),
        (PullRequestUpdated, "A pull request is edited, reviewed or reopened"),
        (PullRequestMerged, "A pull request is merged"),
        (PullRequestDeclined, "A pull request is declined"),
        (PullRequestCommented, "Someone comments on a pull request"),
        (PipelineCompleted, "A CI pipeline finishes"),
        (ReleasePublished, "A release is published"),
        (TagCreated, "A tag is created (in GitBench or by a push)"),
        (TagDeleted, "A tag is deleted (in GitBench or by a push)"),
    ];

    public static bool IsSubscribable(string name) => Subscribable.Any(e => e.Name == name);

    public static IReadOnlyList<string> Parse(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static bool Includes(string csv, string eventName) => eventName == Ping || Parse(csv).Contains(eventName);
}
