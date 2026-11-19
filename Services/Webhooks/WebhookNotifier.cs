using ByteBench.Api;
using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Webhooks;

/// <summary>
/// Hook points for <see cref="IssueService"/> and <see cref="PullRequestService"/>: each method builds the event's payload
/// and hands it to <see cref="WebhookDispatcher"/>. They return quickly (one cheap query when no hook listens), never
/// throw, and are called after the change is saved. They also queue the same events for automation rules
/// (<see cref="Automation.AutomationQueue"/>), so every call site feeds both.
/// </summary>
public class WebhookNotifier(IDbContextFactory<AppDbContext> dbFactory, WebhookDispatcher dispatcher, PublicBaseUrl baseUrl, ILogger<WebhookNotifier> log,
    Automation.AutomationQueue automation, ServiceDesk.ServiceDeskStatusQueue serviceDeskStatus)
{
    // ----- Issues -----

    public Task IssueCreatedAsync(int issueId, int actorId)
    {
        automation.Enqueue(new(Automation.AutomationEventKind.IssueCreated, actorId) { IssueId = issueId });
        return IssueEventAsync(WebhookEventNames.IssueCreated, issueId, actorId);
    }

    /// <param name="change">The activity text recorded for the change (Markdown), e.g. "changed status from **To do** to **Done**".</param>
    /// <param name="fields">For automation: which watched fields changed (status, assignee, priority, labels, sprint).</param>
    public Task IssueUpdatedAsync(int issueId, int actorId, string? change, Automation.IssueFieldChanges? fields = null)
    {
        if (fields is { Fields: not Automation.IssueField.None })
            automation.Enqueue(new(Automation.AutomationEventKind.IssueUpdated, actorId) { IssueId = issueId, Changes = fields });
        if (fields != null && fields.Fields.HasFlag(Automation.IssueField.Status))
            serviceDeskStatus.Enqueue(issueId, fields.ToStatusId); // may email a service desk requester
        return IssueEventAsync(WebhookEventNames.IssueUpdated, issueId, actorId, change: change, fields: fields?.Fields);
    }

    public Task IssueCommentedAsync(int issueId, int commentId, int actorId)
    {
        automation.Enqueue(new(Automation.AutomationEventKind.IssueCommented, actorId) { IssueId = issueId, CommentId = commentId });
        return IssueEventAsync(WebhookEventNames.IssueCommented, issueId, actorId, commentId: commentId);
    }

    private Task IssueEventAsync(string eventName, int issueId, int actorId, string? change = null, int? commentId = null,
        Automation.IssueField? fields = null) => SafeAsync(eventName, async db =>
    {
        var projectId = await db.Issues.Where(i => i.Id == issueId).Select(i => (int?)i.ProjectId).FirstOrDefaultAsync();
        if (projectId is not { } pid || !await AnyListenerAsync(db, pid, eventName)) return;
        var issue = await db.Issues.AsNoTracking().Include(i => i.Project).Include(i => i.Assignee).Include(i => i.Reporter)
            .Include(i => i.Milestone).Include(i => i.Labels).Include(i => i.WorkflowStatus).AsSplitQuery().FirstAsync(i => i.Id == issueId);
        var dto = await FieldEndpoints.EnrichAsync(db, Dto.Issue(issue, baseUrl.Value)); // + custom fields, components, versions, time
        object payload = commentId is { } cid
            ? new { issue = dto, comment = Dto.Comment(await db.Comments.AsNoTracking().Include(c => c.Author).FirstAsync(c => c.Id == cid)) }
            : change != null ? new { issue = dto, change, changedFields = ChangedFieldNames(fields) } : new { issue = dto };
        dispatcher.Enqueue(pid, eventName, payload, actorId);
    });

    // ----- Pull requests -----

    public Task PullRequestOpenedAsync(int prId, int actorId)
    {
        automation.Enqueue(new(Automation.AutomationEventKind.PullRequestOpened, actorId) { PullRequestId = prId });
        return PullRequestEventAsync(WebhookEventNames.PullRequestOpened, prId, actorId);
    }

    /// <param name="action">What happened: "edited", "reopened", "approved", "changes_requested" or "review_dismissed".</param>
    public Task PullRequestUpdatedAsync(int prId, int actorId, string action) =>
        PullRequestEventAsync(WebhookEventNames.PullRequestUpdated, prId, actorId, action: action);

    public Task PullRequestMergedAsync(int prId, int actorId)
    {
        automation.Enqueue(new(Automation.AutomationEventKind.PullRequestMerged, actorId) { PullRequestId = prId });
        return PullRequestEventAsync(WebhookEventNames.PullRequestMerged, prId, actorId);
    }

    public Task PullRequestDeclinedAsync(int prId, int actorId)
    {
        automation.Enqueue(new(Automation.AutomationEventKind.PullRequestDeclined, actorId) { PullRequestId = prId });
        return PullRequestEventAsync(WebhookEventNames.PullRequestDeclined, prId, actorId);
    }

    /// <summary><c>pull_request.updated</c> with action <c>review_requested</c> and the newly requested reviewers.</summary>
    public Task PullRequestReviewRequestedAsync(int prId, int actorId, IReadOnlyCollection<int> reviewerIds) =>
        reviewerIds.Count == 0 ? Task.CompletedTask
            : PullRequestEventAsync(WebhookEventNames.PullRequestUpdated, prId, actorId, action: "review_requested", reviewerIds: reviewerIds);

    public Task PullRequestCommentedAsync(int prId, int commentId, int actorId) =>
        PullRequestEventAsync(WebhookEventNames.PullRequestCommented, prId, actorId, commentId: commentId);

    private Task PullRequestEventAsync(string eventName, int prId, int actorId, string? action = null, int? commentId = null,
        IReadOnlyCollection<int>? reviewerIds = null) => SafeAsync(eventName, async db =>
    {
        var projectId = await db.PullRequests.Where(p => p.Id == prId).Select(p => (int?)p.Repository.ProjectId).FirstOrDefaultAsync();
        if (projectId is not { } pid || !await AnyListenerAsync(db, pid, eventName)) return;
        var pr = await db.PullRequests.AsNoTracking().Include(p => p.Repository).ThenInclude(r => r.Project)
            .Include(p => p.Author).Include(p => p.MergedBy).Include(p => p.Reviews).ThenInclude(r => r.User)
            .Include(p => p.SourceRepository).ThenInclude(r => r!.Project)
            .AsSplitQuery().FirstAsync(p => p.Id == prId);
        var dto = Dto.PullRequest(pr, baseUrl.Value);
        var repo = Dto.Repository(pr.Repository, baseUrl.Value);
        object payload = commentId is { } cid
            ? new
            {
                pullRequest = dto, repository = repo,
                comment = Dto.PullRequestComment(await db.PullRequestComments.AsNoTracking().Include(c => c.Author).FirstAsync(c => c.Id == cid)),
            }
            : reviewerIds != null
                ? new
                {
                    action, pullRequest = dto, repository = repo,
                    requestedReviewers = (await db.Users.AsNoTracking().Where(u => reviewerIds.Contains(u.Id)).OrderBy(u => u.UserName).ToListAsync()).Select(Dto.User).ToList(),
                }
            : action != null ? new { action, pullRequest = dto, repository = repo } : new { pullRequest = dto, repository = repo };
        dispatcher.Enqueue(pid, eventName, payload, actorId);
    });

    // ----- Helpers -----

    /// <summary>"status", "assignee", "priority", "labels", "sprint" for the flags set; null when unknown.</summary>
    private static List<string>? ChangedFieldNames(Automation.IssueField? fields) =>
        fields is not { } f ? null
            : Enum.GetValues<Automation.IssueField>().Where(x => x != Automation.IssueField.None && f.HasFlag(x))
                .Select(x => x.ToString().ToLowerInvariant()).ToList();

    /// <summary>True if an active hook of the project subscribes to <paramref name="eventName"/> (so payloads aren't built for nobody).</summary>
    public static async Task<bool> AnyListenerAsync(AppDbContext db, int projectId, string eventName)
    {
        var events = (await ChatIntegrations.ChatHookResolver.ActiveHooksAsync(db, projectId)).Select(h => h.Events).ToList();
        return events.Any(e => WebhookEventNames.Includes(e, eventName));
    }

    private async Task SafeAsync(string eventName, Func<AppDbContext, Task> work)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            await work(db);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not queue webhook event {Event}", eventName);
        }
    }
}
