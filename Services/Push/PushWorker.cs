using System.Text.Json;
using System.Threading.Channels;
using ByteBench.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Push;

/// <summary>
/// Delivers notifications flagged <see cref="Notification.PushPending"/> (set by <c>NotificationService.DeliverAsync</c> for
/// users who turned browser push on for that group) to every browser they subscribed. Runs in the background, woken by
/// <see cref="NotificationsChanged"/> and every 30 seconds, so a slow push service never delays the request that created the
/// notification. Each notification is claimed atomically (at most one push per browser, even with several processes).
/// A 404/410 answer removes the subscription, so do 5 consecutive failures; a 429 pauses that subscription for <c>Retry-After</c>.
/// </summary>
public sealed class PushWorker(IDbContextFactory<AppDbContext> dbFactory, LiveEvents live, PushSender sender, PushSubscriptions subscriptions,
    IOptionsMonitor<PushOptions> options, TimeProvider clock, ILogger<PushWorker> log) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    public const int BatchSize = 50;
    public const int MaxFailures = 5;
    public const int MaxBody = 120;

    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.CurrentValue.DeliveryWorker) return;
        using var sub = live.Subscribe<NotificationsChanged>(_ => { wake.Writer.TryWrite(true); return Task.CompletedTask; });
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Browser push pass failed"); }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(PollInterval);
            try { await wake.Reader.ReadAsync(timeout.Token); }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { /* poll */ }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One pass over pending notifications; returns how many pushes were delivered. Public so tests can drive it.</summary>
    public async Task<int> ProcessAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!options.CurrentValue.Enabled)
        {
            await db.Notifications.Where(n => n.PushPending).ExecuteUpdateAsync(s => s.SetProperty(n => n.PushPending, false), ct);
            return 0;
        }
        var pending = await db.Notifications.AsNoTracking().Include(n => n.Actor)
            .Where(n => n.PushPending).OrderBy(n => n.Id).Take(BatchSize).ToListAsync(ct);
        if (pending.Count == 0) return 0;
        var userIds = pending.Select(n => n.UserId).Distinct().ToList();
        var subs = (await db.WebPushSubscriptions.AsNoTracking().Where(s => userIds.Contains(s.UserId)).ToListAsync(ct)).ToLookup(s => s.UserId);

        var delivered = 0;
        foreach (var n in pending)
        {
            // Claim: whoever flips the flag sends it.
            if (await db.Notifications.Where(x => x.Id == n.Id && x.PushPending).ExecuteUpdateAsync(s => s.SetProperty(x => x.PushPending, false), ct) == 0)
                continue;
            var now = clock.GetUtcNow().UtcDateTime;
            var targets = subs[n.UserId].Where(s => s.RetryAfterUntil == null || s.RetryAfterUntil <= now).ToList();
            if (targets.Count == 0) continue;
            var payload = Payload(n);
            var results = await Task.WhenAll(targets.Select(async s =>
            {
                if (subscriptions.Keys(s) is not { } keys) return (s, new PushResult(PushOutcome.Gone, null, "Unreadable keys."));
                return (s, await sender.SendAsync(s.Endpoint, keys.P256dh, keys.Auth, payload, ct));
            }));
            foreach (var (s, r) in results)
            {
                await RecordAsync(db, s, r, now, ct);
                if (r.Outcome == PushOutcome.Delivered) delivered++;
            }
        }
        return delivered;
    }

    private async Task RecordAsync(AppDbContext db, WebPushSubscription s, PushResult r, DateTime now, CancellationToken ct)
    {
        var q = db.WebPushSubscriptions.Where(x => x.Id == s.Id);
        switch (r.Outcome)
        {
            case PushOutcome.Delivered:
                await q.ExecuteUpdateAsync(x => x.SetProperty(p => p.LastUsedAt, now).SetProperty(p => p.FailureCount, 0).SetProperty(p => p.RetryAfterUntil, (DateTime?)null), ct);
                break;
            case PushOutcome.Gone:
                await q.ExecuteDeleteAsync(ct);
                log.LogInformation("Removed push subscription {Id} of user {UserId}: {Error}", s.Id, s.UserId, r.Error);
                break;
            case PushOutcome.RateLimited:
                await q.ExecuteUpdateAsync(x => x.SetProperty(p => p.RetryAfterUntil, now + (r.RetryAfter ?? TimeSpan.FromMinutes(1))), ct);
                log.LogWarning("Push service rate limited subscription {Id}; pausing for {Wait}", s.Id, r.RetryAfter);
                break;
            default:
                var failures = await q.Select(p => p.FailureCount).FirstOrDefaultAsync(ct) + 1;
                if (failures >= MaxFailures)
                {
                    await q.ExecuteDeleteAsync(ct);
                    log.LogInformation("Removed push subscription {Id} of user {UserId} after {Failures} failures: {Error}", s.Id, s.UserId, failures, r.Error);
                }
                else
                {
                    await q.ExecuteUpdateAsync(x => x.SetProperty(p => p.FailureCount, failures), ct);
                    log.LogWarning("Push to subscription {Id} failed ({Failures}/{Max}): {Error}", s.Id, failures, MaxFailures, r.Error);
                }
                break;
        }
    }

    /// <summary>
    /// The push message: the notification's title, a body of at most 120 characters ("Alice Chen commented on") and its
    /// same-origin path. Never the excerpt (comment or description text): push services and lock screens see less.
    /// </summary>
    public static byte[] Payload(Notification n)
    {
        var body = $"{n.Actor?.DisplayName ?? "ByteBench"} {NotificationService.Verb(n.Kind)}".TrimEnd(':', ' ');
        if (body.Length > MaxBody) body = body[..(MaxBody - 1)] + "…";
        var title = n.Title.Length > 100 ? n.Title[..99] + "…" : n.Title;
        var url = n.Url.StartsWith('/') && !n.Url.StartsWith("//") && !n.Url.StartsWith("/\\") ? n.Url : "/notifications";
        return JsonSerializer.SerializeToUtf8Bytes(new { title, body, url, tag = "bytebench-" + n.Id });
    }
}
