using System.Threading.Channels;
using GitBench.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GitBench.Services.Email;

/// <summary>
/// Turns notifications flagged <see cref="Notification.EmailPending"/> (by <c>NotificationService.DeliverAsync</c>) into
/// emails on <see cref="EmailQueue"/>. Wakes on every <see cref="NotificationsChanged"/> live event and every 30 seconds
/// (for digests coming due).
/// <list type="bullet">
/// <item><c>Email:DigestMinutes</c> = 0: one email per notification, right away.</item>
/// <item>N &gt; 0: at most one email per user every N minutes. The first goes out at once; the rest wait and are sent
/// together as a digest.</item>
/// </list>
/// Notifications the user has already read in the app, or whose group no longer emails, are dropped from the email.
/// </summary>
public sealed class NotificationEmailWorker(IDbContextFactory<AppDbContext> dbFactory, LiveEvents live, EmailQueue queue,
    NotificationEmailComposer composer, IOptions<EmailOptions> options, ILogger<NotificationEmailWorker> log) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var sub = live.Subscribe<NotificationsChanged>(_ => { wake.Writer.TryWrite(true); return Task.CompletedTask; });
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Notification email pass failed"); }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(PollInterval);
            try { await wake.Reader.ReadAsync(timeout.Token); }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { /* poll */ }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One pass over the pending notifications. Internal so it can be driven directly.</summary>
    internal async Task ProcessAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!queue.IsEnabled)
        {
            // Email is off: never let a backlog build up that would flood people once it's turned on.
            await db.Notifications.Where(n => n.EmailPending).ExecuteUpdateAsync(s => s.SetProperty(n => n.EmailPending, false), ct);
            return;
        }

        var pending = await db.Notifications.Include(n => n.Actor).Include(n => n.User)
            .Where(n => n.EmailPending).OrderBy(n => n.CreatedAt).ThenBy(n => n.Id).ToListAsync(ct);
        if (pending.Count == 0) return;

        var digest = TimeSpan.FromMinutes(Math.Max(0, options.Value.DigestMinutes));
        var now = DateTime.UtcNow;
        var userIds = pending.Select(n => n.UserId).Distinct().ToList();
        var prefs = await NotificationPreferences.LoadAsync(db, userIds);
        var settings = await db.UserNotificationSettings.Where(s => userIds.Contains(s.UserId)).ToDictionaryAsync(s => s.UserId, ct);

        foreach (var group in pending.GroupBy(n => n.UserId))
        {
            var user = group.First().User;
            settings.TryGetValue(user.Id, out var s);
            if (digest > TimeSpan.Zero && s?.LastEmailAt is { } last && now - last < digest) continue; // wait for the digest window

            var p = prefs[user.Id];
            var send = group.Where(n => !n.IsRead && p.EmailEnabled && p.ChannelFor(n.Kind) == NotificationChannel.InAppAndEmail).ToList();
            foreach (var n in group) n.EmailPending = false;
            if (send.Count > 0 && !string.IsNullOrWhiteSpace(user.Email))
            {
                var batches = digest > TimeSpan.Zero ? [send] : send.Select(n => new List<Notification> { n });
                foreach (var batch in batches) queue.Enqueue(composer.Compose(user, batch));
                if (s == null) db.UserNotificationSettings.Add(s = new UserNotificationSettings { UserId = user.Id });
                s.LastEmailAt = now;
                settings[user.Id] = s;
                log.LogDebug("Queued {Count} notification(s) for user {UserId} by email", send.Count, user.Id);
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
