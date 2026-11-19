using GitBench.Services.Webhooks;

namespace GitBench.Services.ChatIntegrations;

/// <summary>Event groups: each can post to its own channel (bot token) or incoming webhook.</summary>
public enum ChatEventGroup { Issues, PullRequests, Pipelines, Releases, Mentions }

/// <summary>
/// The chat events an integration can announce. Each is derived from a generic webhook event (see <see cref="SourcesOf"/>),
/// so chat integrations ride on the existing webhook queue: e.g. <c>issue.transitioned</c> is an <c>issue.updated</c> whose
/// status changed, and <c>pipeline.fixed</c> a passing <c>pipeline.completed</c> after a failure on the same ref.
/// </summary>
public static class ChatEvents
{
    public const string IssueCreated = "issue.created";
    public const string IssueTransitioned = "issue.transitioned";
    public const string IssueAssigned = "issue.assigned";
    public const string IssueCommented = "issue.commented";
    public const string PullRequestOpened = "pull_request.opened";
    public const string PullRequestMerged = "pull_request.merged";
    public const string PullRequestReviewRequested = "pull_request.review_requested";
    public const string PullRequestApproved = "pull_request.approved";
    public const string PipelineFailed = "pipeline.failed";
    public const string PipelineFixed = "pipeline.fixed";
    public const string ReleasePublished = "release.published";
    public const string Deployment = "deployment";
    public const string Mention = "mention";
    /// <summary>Only for "Send test message"; not subscribable.</summary>
    public const string Test = "test";

    public sealed record Info(string Name, string Title, ChatEventGroup Group, IReadOnlyList<string> Sources);

    public static readonly IReadOnlyList<Info> All =
    [
        new(IssueCreated, "Issue created", ChatEventGroup.Issues, [WebhookEventNames.IssueCreated]),
        new(IssueTransitioned, "Issue status changed", ChatEventGroup.Issues, [WebhookEventNames.IssueUpdated]),
        new(IssueAssigned, "Issue assigned", ChatEventGroup.Issues, [WebhookEventNames.IssueUpdated]),
        new(IssueCommented, "Issue commented", ChatEventGroup.Issues, [WebhookEventNames.IssueCommented]),
        new(PullRequestOpened, "Pull request opened", ChatEventGroup.PullRequests, [WebhookEventNames.PullRequestOpened]),
        new(PullRequestMerged, "Pull request merged", ChatEventGroup.PullRequests, [WebhookEventNames.PullRequestMerged]),
        new(PullRequestReviewRequested, "Review requested", ChatEventGroup.PullRequests, [WebhookEventNames.PullRequestUpdated]),
        new(PullRequestApproved, "Pull request approved", ChatEventGroup.PullRequests, [WebhookEventNames.PullRequestUpdated]),
        new(PipelineFailed, "Pipeline failed", ChatEventGroup.Pipelines, [WebhookEventNames.PipelineCompleted]),
        new(PipelineFixed, "Pipeline fixed", ChatEventGroup.Pipelines, [WebhookEventNames.PipelineCompleted]),
        new(Deployment, "Deployment finished", ChatEventGroup.Pipelines, [WebhookEventNames.DeploymentStatus]),
        new(ReleasePublished, "Release published", ChatEventGroup.Releases, [WebhookEventNames.ReleasePublished]),
        new(Mention, "@mentions in issues, pull requests and comments", ChatEventGroup.Mentions,
            [WebhookEventNames.IssueCreated, WebhookEventNames.IssueCommented, WebhookEventNames.PullRequestOpened, WebhookEventNames.PullRequestCommented]),
    ];

    public static readonly IReadOnlyList<string> Defaults =
        [IssueCreated, IssueTransitioned, PullRequestOpened, PullRequestMerged, PipelineFailed, PipelineFixed, ReleasePublished];

    public static Info? Find(string name) => All.FirstOrDefault(e => e.Name == name);

    public static bool IsKnown(string name) => Find(name) != null;

    public static ChatEventGroup GroupOf(string chatEvent) => Find(chatEvent)?.Group ?? ChatEventGroup.Issues;

    /// <summary>The webhook events (for <see cref="Data.Webhook.Events"/>) that the chosen chat events derive from.</summary>
    public static IReadOnlyList<string> SourcesOf(IEnumerable<string> chatEvents) =>
        chatEvents.Select(Find).Where(e => e != null).SelectMany(e => e!.Sources).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

    public static string GroupTitle(ChatEventGroup g) => g switch
    {
        ChatEventGroup.PullRequests => "Pull requests",
        ChatEventGroup.Pipelines => "Pipelines & deployments",
        _ => g.ToString(),
    };
}
