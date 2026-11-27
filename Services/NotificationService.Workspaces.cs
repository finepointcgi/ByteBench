using ByteBench.Data;

namespace ByteBench.Services;

public partial class NotificationService
{
    /// <summary>Tells a user the owner invited them to a workspace (in-app; the recipient must be able to view the project).</summary>
    public async Task OnWorkspaceSharedAsync(int projectId, int inviteeId, int ownerId, string title, string url, string excerpt)
    {
        if ((await FilterRecipientsAsync(projectId, [inviteeId])).Count == 0) return;
        await using var db = await dbFactory.CreateDbContextAsync();
        await DeliverAsync(db, [new Notification
        {
            UserId = inviteeId, ProjectId = projectId, ActorId = ownerId, Kind = NotificationKind.WorkspaceShared,
            Title = title, Url = url, Excerpt = excerpt, CreatedAt = DateTime.UtcNow,
        }]);
    }
}
