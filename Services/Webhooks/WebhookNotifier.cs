using GitBench.Api;
using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Webhooks;

/// <summary>
/// Hook points for <see cref="IssueService"/> and <see cref="PullRequestService"/>: each method builds the event's payload
/// and hands it to <see cref="WebhookDispatcher"/>. They return quickly (one cheap query when no hook listens), never
/// throw, and are called after the change is saved.
/// </summary>
public class WebhookNotifier(IDbContextFactory<AppDbContext> dbFactory, WebhookDispatcher dispatcher, PublicBaseUrl baseUrl, ILogger<WebhookNotifier> log)
{
    // ----- Issues -----

    public Task IssueCreatedAsync(int issueId, int actorId) =>
        IssueEventAsync(WebhookEventNames.IssueCreated, issueId, actorId);

    /// <param name="change">The activity text recorded for the change (Markdown), e.g. "changed status from **To do** to **Done**".</param>
    public Task IssueUpdatedAsync(int issueId, int actorId, string? change) =>
        IssueEventAsync(WebhookEventNames.IssueUpdated, issueId, actorId, change: change);

    public Task IssueCommentedAsync(int issueId, int commentId, int actorId) =>
        IssueEventAsync(WebhookEventNames.IssueCommented, issueId, actorId, commentId: commentId);

    private Task IssueEventAsync(string eventName, int issueId, int actorId, string? change = null, int? commentId = null) => SafeAsync(eventName, async db =>
    {
        var projectId = await db.Issues.Where(i => i.Id == issueId).Select(i => (int?)i.ProjectId).FirstOrDefaultAsync();
        if (projectId is not { } pid || !await AnyListenerAsync(db, pid, eventName)) return;
        var issue = await db.Issues.AsNoTracking().Include(i => i.Project).Include(i => i.Assignee).Include(i => i.Reporter)
            .Include(i => i.Milestone).Include(i => i.Labels).AsSplitQuery().FirstAsync(i => i.Id == issueId);
        var dto = Dto.Issue(issue, baseUrl.Value);
        object payload = commentId is { } cid
            ? new { issue = dto, comment = Dto.Comment(await db.Comments.AsNoTracking().Include(c => c.Author).FirstAsync(c => c.Id == cid)) }
            : change != null ? new { issue = dto, change } : new { issue = dto };
        dispatcher.Enqueue(pid, eventName, payload, actorId);
    });

    // ----- Pull requests -----

    public Task PullRequestOpenedAsync(int prId, int actorId) => PullRequestEventAsync(WebhookEventNames.PullRequestOpened, prId, actorId);

    /// <param name="action">What happened: "edited", "reopened", "approved", "changes_requested" or "review_dismissed".</param>
    public Task PullRequestUpdatedAsync(int prId, int actorId, string action) =>
        PullRequestEventAsync(WebhookEventNames.PullRequestUpdated, prId, actorId, action: action);

    public Task PullRequestMergedAsync(int prId, int actorId) => PullRequestEventAsync(WebhookEventNames.PullRequestMerged, prId, actorId);

    public Task PullRequestDeclinedAsync(int prId, int actorId) => PullRequestEventAsync(WebhookEventNames.PullRequestDeclined, prId, actorId);

    public Task PullRequestCommentedAsync(int prId, int commentId, int actorId) =>
        PullRequestEventAsync(WebhookEventNames.PullRequestCommented, prId, actorId, commentId: commentId);

    private Task PullRequestEventAsync(string eventName, int prId, int actorId, string? action = null, int? commentId = null) => SafeAsync(eventName, async db =>
    {
        var projectId = await db.PullRequests.Where(p => p.Id == prId).Select(p => (int?)p.Repository.ProjectId).FirstOrDefaultAsync();
        if (projectId is not { } pid || !await AnyListenerAsync(db, pid, eventName)) return;
        var pr = await db.PullRequests.AsNoTracking().Include(p => p.Repository).ThenInclude(r => r.Project)
            .Include(p => p.Author).Include(p => p.MergedBy).Include(p => p.Reviews).ThenInclude(r => r.User)
            .AsSplitQuery().FirstAsync(p => p.Id == prId);
        var dto = Dto.PullRequest(pr, baseUrl.Value);
        var repo = Dto.Repository(pr.Repository, baseUrl.Value);
        object payload = commentId is { } cid
            ? new
            {
                pullRequest = dto, repository = repo,
                comment = Dto.PullRequestComment(await db.PullRequestComments.AsNoTracking().Include(c => c.Author).FirstAsync(c => c.Id == cid)),
            }
            : action != null ? new { action, pullRequest = dto, repository = repo } : new { pullRequest = dto, repository = repo };
        dispatcher.Enqueue(pid, eventName, payload, actorId);
    });

    // ----- Helpers -----

    /// <summary>True if an active hook of the project subscribes to <paramref name="eventName"/> (so payloads aren't built for nobody).</summary>
    public static async Task<bool> AnyListenerAsync(AppDbContext db, int projectId, string eventName)
    {
        var events = await db.Webhooks.Where(h => h.ProjectId == projectId && h.IsActive).Select(h => h.Events).ToListAsync();
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
