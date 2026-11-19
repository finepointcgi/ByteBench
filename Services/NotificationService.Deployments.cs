using ByteBench.Data;

namespace ByteBench.Services;

public partial class NotificationService
{
    /// <summary>
    /// Deployment approvals: "waiting for your approval" to approvers, "approved"/"rejected" to whoever triggered the
    /// pipeline. The actor is skipped, recipients must be able to view the project, and preferences (Pipelines group) apply.
    /// </summary>
    public async Task<IReadOnlyCollection<int>> OnDeploymentAsync(int projectId, NotificationKind kind, IEnumerable<int> userIds, int? actorId,
        string title, string url, string? excerpt = null)
    {
        var allowed = await FilterRecipientsAsync(projectId, userIds.Where(u => u != actorId).Distinct());
        if (allowed.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        return await DeliverAsync(db, allowed.Select(u => new Notification
        {
            UserId = u, ProjectId = projectId, ActorId = actorId, Kind = kind, Title = title, Url = url, Excerpt = excerpt, CreatedAt = now,
        }).ToList());
    }
}
