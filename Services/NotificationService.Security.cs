using ByteBench.Data;

namespace ByteBench.Services;

public partial class NotificationService
{
    /// <summary>
    /// A system notification (no actor) that a scan found new security issues (Services/Security). Recipients must be able
    /// to view the project, and their "Pipelines" preference applies.
    /// </summary>
    /// <returns>The users who got it.</returns>
    public async Task<IReadOnlyCollection<int>> OnSecurityFindingsAsync(int projectId, IEnumerable<int> userIds, string title, string url, string? excerpt = null)
    {
        var allowed = await FilterRecipientsAsync(projectId, userIds.Distinct());
        if (allowed.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        return await DeliverAsync(db, allowed.Select(u => new Notification
        {
            UserId = u, ProjectId = projectId, ActorId = null, Kind = NotificationKind.SecurityFindings,
            Title = title.Length > 200 ? title[..199] + "…" : title, Url = url,
            Excerpt = excerpt is { Length: > 1200 } e ? e[..1199] + "…" : excerpt, CreatedAt = now,
        }).ToList());
    }
}
