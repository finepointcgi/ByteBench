using GitBench.Data;

namespace GitBench.Services;

public partial class NotificationService
{
    /// <summary>
    /// Tells a PR's author that the merge queue dropped their pull request (failed pipeline, conflict, timeout, ...).
    /// <paramref name="actorId"/> is who removed it by hand, or null when the queue itself did (a system notification).
    /// </summary>
    public async Task OnMergeQueueRemovedAsync(int prId, int? actorId, string reason)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var pr = await LoadPrAsync(db, prId);
        if (pr == null) return;
        if (actorId == pr.AuthorId) { live.Publish(new PullRequestChanged(prId, pr.Repository.ProjectId)); return; }
        if ((await FilterRecipientsAsync(pr.Repository.ProjectId, [pr.AuthorId])).Count > 0)
        {
            await DeliverAsync(db, [new Notification
            {
                UserId = pr.AuthorId, ProjectId = pr.Repository.ProjectId, ActorId = actorId, Kind = NotificationKind.MergeQueueRemoved,
                Title = PrTitle(pr), Url = PrUrl(pr), Excerpt = reason, CreatedAt = DateTime.UtcNow,
            }]);
        }
        live.Publish(new PullRequestChanged(prId, pr.Repository.ProjectId));
    }
}
