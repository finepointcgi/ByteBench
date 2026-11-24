using System.Threading.Channels;
using ByteBench.Api;
using ByteBench.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.ChatIntegrations;

/// <summary>
/// Sends notifications flagged <see cref="Notification.ChatPending"/> (users who turned on Slack DMs in their notification
/// preferences) as direct messages from the Slack app's bot. Wakes on <see cref="NotificationsChanged"/> and polls every
/// 30 s. Without a bot token, or for users who haven't linked Slack, pending flags are cleared so no backlog builds up.
/// One attempt per notification: DMs are a convenience next to in-app notifications, not the record.
/// </summary>
public sealed class SlackDmWorker(IDbContextFactory<AppDbContext> dbFactory, LiveEvents live, ChatHttp http, PublicBaseUrl baseUrl,
    IOptionsMonitor<SlackOptions> options, ILogger<SlackDmWorker> log) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    public const int BatchSize = 50;

    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var sub = live.Subscribe<NotificationsChanged>(_ => { wake.Writer.TryWrite(true); return Task.CompletedTask; });
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try { WorkerPulse.Tick("slack-dm"); await ProcessAsync(stoppingToken); WorkerPulse.Ok("slack-dm"); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { WorkerPulse.Fail("slack-dm", ex); log.LogError(ex, "Slack DM pass failed"); }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(PollInterval);
            try { await wake.Reader.ReadAsync(timeout.Token); }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { /* poll */ }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One pass over pending notifications. Internal so tests can drive it.</summary>
    internal async Task ProcessAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var token = options.CurrentValue.BotToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            await db.Notifications.Where(n => n.ChatPending).ExecuteUpdateAsync(s => s.SetProperty(n => n.ChatPending, false), ct);
            return;
        }
        var pending = await db.Notifications.AsNoTracking().Include(n => n.Actor)
            .Where(n => n.ChatPending).OrderBy(n => n.Id).Take(BatchSize).ToListAsync(ct);
        if (pending.Count == 0) return;
        var userIds = pending.Select(n => n.UserId).Distinct().ToList();
        var slackIds = (await db.ChatUserLinks.AsNoTracking()
                .Where(l => l.Provider == SlackAccountLinks.Provider && l.RevokedAt == null && userIds.Contains(l.UserId))
                .OrderByDescending(l => l.Id).ToListAsync(ct))
            .GroupBy(l => l.UserId).ToDictionary(g => g.Key, g => g.First().ExternalUserId);

        foreach (var n in pending)
        {
            if (slackIds.TryGetValue(n.UserId, out var slackUser))
            {
                var message = SlackFormat.Message(new ChatMessage
                {
                    Event = "notification",
                    Summary = n.Title,
                    Title = n.Title,
                    Url = baseUrl.Value + n.Url,
                    Status = n.Kind == NotificationKind.PipelineFailed ? ChatStatus.Danger : ChatStatus.Info,
                    Actor = n.Actor?.DisplayName,
                    Body = ChatText.Excerpt(n.Excerpt, 600),
                }, slackUser);
                var result = await http.CallSlackAsync("chat.postMessage", token, message.ToJsonString(), [], ct);
                if (!result.Success) log.LogWarning("Slack DM for notification {Id} failed: {Error}", n.Id, result.Error);
            }
            await db.Notifications.Where(x => x.Id == n.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ChatPending, false), ct);
        }
    }
}
