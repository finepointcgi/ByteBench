using ByteBench.Data;

namespace ByteBench.Services;

public partial class NotificationService
{
    /// <summary>
    /// Tells the person who started a test run that it finished (closed by hand or ingested from CI). A system notification
    /// without an actor, so the person who closed it themselves still hears about a failed CI ingest. The recipient must be
    /// able to view the project, and the Pipelines notification group's preference applies.
    /// </summary>
    public async Task OnTestRunCompletedAsync(int projectId, int userId, string title, string url, string summary)
    {
        if ((await FilterRecipientsAsync(projectId, [userId])).Count == 0) return;
        await using var db = await dbFactory.CreateDbContextAsync();
        await DeliverAsync(db, [new Notification
        {
            UserId = userId, ProjectId = projectId, ActorId = null, Kind = NotificationKind.TestRunCompleted,
            Title = title, Url = url, Excerpt = summary, CreatedAt = DateTime.UtcNow,
        }]);
    }
}
