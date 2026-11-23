using ByteBench.Data;

namespace ByteBench.Services;

public partial class NotificationService
{
    /// <summary>
    /// Tells the person who armed "merge when ready" that it was cancelled (new commits by someone else, lost permission, the
    /// pull request was closed or a check failed). <paramref name="actorId"/> is who cancelled it by hand, or null when
    /// ByteBench did; the person who cancelled it themselves is not notified.
    /// </summary>
    public async Task OnAutoMergeCancelledAsync(int prId, int armedById, int? actorId, string reason)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var pr = await LoadPrAsync(db, prId);
        if (pr == null) return;
        if (actorId != armedById && (await FilterRecipientsAsync(pr.Repository.ProjectId, [armedById])).Count > 0)
        {
            await DeliverAsync(db, [new Notification
            {
                UserId = armedById, ProjectId = pr.Repository.ProjectId, ActorId = actorId, Kind = NotificationKind.AutoMergeCancelled,
                Title = PrTitle(pr), Url = PrUrl(pr), Excerpt = reason, CreatedAt = DateTime.UtcNow,
            }]);
        }
        live.Publish(new PullRequestChanged(prId, pr.Repository.ProjectId));
    }
}
