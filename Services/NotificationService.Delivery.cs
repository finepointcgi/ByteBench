using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services;

public partial class NotificationService
{
    public const int ExcerptLength = 1000;

    /// <summary>
    /// The single fan-out point: every notification the service creates passes through here. Applies each recipient's
    /// <see cref="NotificationPreferences"/> (Off drops it; In-app + email also flags it for the email worker), saves,
    /// and publishes <see cref="NotificationsChanged"/> (which also wakes <c>NotificationEmailWorker</c>).
    /// Callers have already removed the actor and run <see cref="FilterRecipientsAsync"/>.
    /// </summary>
    /// <returns>The users who got a notification.</returns>
    private async Task<IReadOnlyCollection<int>> DeliverAsync(AppDbContext db, IReadOnlyCollection<Notification> candidates)
    {
        if (candidates.Count == 0) return [];
        var prefs = await NotificationPreferences.LoadAsync(db, candidates.Select(n => n.UserId).Distinct().ToList());
        var delivered = new HashSet<int>();
        foreach (var n in candidates)
        {
            var p = prefs[n.UserId];
            var channel = p.ChannelFor(n.Kind);
            if (channel == NotificationChannel.Off) continue;
            n.EmailPending = channel == NotificationChannel.InAppAndEmail && p.EmailEnabled;
            n.ChatPending = p.SlackDmEnabled; // ChatIntegrations.SlackDmWorker sends it (if a bot token is configured)
            if (n.Title.Length > 200) n.Title = n.Title[..199] + "…";
            if (n.Excerpt is { Length: > ExcerptLength } e) n.Excerpt = e[..ExcerptLength].TrimEnd() + "…";
            db.Notifications.Add(n);
            delivered.Add(n.UserId);
        }
        if (delivered.Count == 0) return [];
        await db.SaveChangesAsync();
        live.Publish(delivered.Select(u => (LiveEvent)new NotificationsChanged(u)).ToArray());
        return delivered;
    }

    // =====================================================================
    // Muting
    // =====================================================================

    public async Task<bool> IsIssueMutedAsync(int userId, int issueId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Watches.AnyAsync(w => w.UserId == userId && w.IssueId == issueId && w.Muted);
    }

    public async Task<bool> IsPullRequestMutedAsync(int userId, int prId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Watches.AnyAsync(w => w.UserId == userId && w.PullRequestId == prId && w.Muted);
    }

    /// <summary>Mutes or unmutes an issue for the user. Muting keeps (or creates) the watch, so auto-watching can't undo it.</summary>
    public Task SetIssueMutedAsync(int userId, int issueId, bool muted) => SetMutedAsync(userId, muted, issueId: issueId);

    public Task SetPullRequestMutedAsync(int userId, int prId, bool muted) => SetMutedAsync(userId, muted, pullRequestId: prId);

    private async Task SetMutedAsync(int userId, bool muted, int? issueId = null, int? pullRequestId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (muted) await EnsureWatchAsync(db, userId, issueId, pullRequestId);
        var q = issueId != null ? db.Watches.Where(w => w.UserId == userId && w.IssueId == issueId)
            : db.Watches.Where(w => w.UserId == userId && w.PullRequestId == pullRequestId);
        await q.ExecuteUpdateAsync(s => s.SetProperty(w => w.Muted, muted));
    }
}
