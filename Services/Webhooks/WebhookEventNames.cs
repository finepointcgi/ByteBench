namespace ByteBench.Services.Webhooks;

/// <summary>The event names a webhook can subscribe to (the <c>X-ByteBench-Event</c> header and the payload's <c>event</c>).</summary>
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
    /// <summary>A pipeline was created (queued, or in Error because its file is invalid). Raised by CI.</summary>
    public const string PipelineCreated = "pipeline.created";
    /// <summary>A pipeline began running (once per pipeline, not again after a manual job or retry). Raised by CI.</summary>
    public const string PipelineStarted = "pipeline.started";
    /// <summary>A CI job started running (again for each retry). Raised by CI.</summary>
    public const string JobStarted = "job.started";
    /// <summary>A CI job finished: passed, failed or canceled. Raised by CI.</summary>
    public const string JobCompleted = "job.completed";
    /// <summary>Raised by security scanning (Services/Security) when the default branch gets new findings at or above the repository's notify severity.</summary>
    public const string SecurityNewFindings = "security.new_findings";
    /// <summary>A deployment waits for approval, starts, finishes, or is rejected or canceled (raised by CI).</summary>
    public const string DeploymentStatus = "deployment.status";
    /// <summary>A static site (Pages) was deployed, or an earlier version was rolled back to (Services/Pages).</summary>
    public const string PagesDeployed = "pages.deployed";
    // ----- Releases -----
    public const string ReleasePublished = "release.published";
    public const string TagCreated = "tag.created";
    public const string TagDeleted = "tag.deleted";
    // ----- Package & container registry -----
    public const string PackagePublished = "package.published";
    public const string PackageDeleted = "package.deleted";
    // ----- Test management -----
    /// <summary>A test run was closed, or an automated report (CI or API) was recorded. Raised by Services/Tests.</summary>
    public const string TestRunCompleted = "test_run.completed";
    // ----- Feature flags -----
    /// <summary>A feature flag is created, toggled, edited (value, rules, details), archived or deleted. Raised by Services/Flags.</summary>
    public const string FlagChanged = "flag.changed";

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
        (PipelineCreated, "A CI pipeline is created"),
        (PipelineStarted, "A CI pipeline starts running"),
        (PipelineCompleted, "A CI pipeline finishes"),
        (JobStarted, "A CI job starts running"),
        (JobCompleted, "A CI job finishes"),
        (SecurityNewFindings, "A security scan finds new high-severity issues on the default branch"),
        (DeploymentStatus, "A deployment changes status (waiting for approval, running, passed, failed, canceled)"),
        (PagesDeployed, "A Pages site is deployed or rolled back"),
        (ReleasePublished, "A release is published"),
        (TagCreated, "A tag is created (in ByteBench or by a push)"),
        (TagDeleted, "A tag is deleted (in ByteBench or by a push)"),
        (PackagePublished, "A package version or container image tag is published"),
        (PackageDeleted, "A package, version or image tag is deleted"),
        (TestRunCompleted, "A test run is closed or an automated test report is recorded"),
        (FlagChanged, "A feature flag is created, toggled, edited, archived or deleted"),
    ];

    public static bool IsSubscribable(string name) => Subscribable.Any(e => e.Name == name);

    public static IReadOnlyList<string> Parse(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static bool Includes(string csv, string eventName) => eventName == Ping || Parse(csv).Contains(eventName);
}
