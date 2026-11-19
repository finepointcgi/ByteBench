using ByteBench.Data;

namespace ByteBench.Services;

public partial class NotificationService
{
    /// <summary>
    /// A notification sent by an automation rule's "send notification" action. Goes through the usual gates: the actor (the
    /// automation bot or rule author) is skipped, recipients must be able to view the project, and preferences apply.
    /// </summary>
    /// <returns>The users who got it.</returns>
    public async Task<IReadOnlyCollection<int>> NotifyFromAutomationAsync(int projectId, IEnumerable<int> userIds, int actorId, string title, string url, string? excerpt = null)
    {
        var allowed = await FilterRecipientsAsync(projectId, userIds.Where(u => u != actorId));
        if (allowed.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        return await DeliverAsync(db, allowed.Select(u => new Notification
        {
            UserId = u, ProjectId = projectId, ActorId = actorId, Kind = NotificationKind.Automation,
            Title = title, Url = url, Excerpt = excerpt, CreatedAt = now,
        }).ToList());
    }

    /// <summary>Watchers of an issue who haven't muted it (the "watchers" target of the automation notify action).</summary>
    public async Task<List<int>> GetIssueWatcherIdsAsync(int issueId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await IssueWatchersAsync(db, issueId);
    }
}
