using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GitBench.Api;
using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Webhooks;

/// <summary>
/// Background delivery: drains <see cref="WebhookDispatcher"/>, writes a <see cref="WebhookDelivery"/> row per hook and
/// sends it (up to <see cref="MaxConcurrent"/> at a time, so one slow receiver doesn't hold up the others). Each delivery
/// gets a 10 s timeout per attempt and up to 3 retries with backoff on network errors, timeouts and 5xx responses.
/// After <see cref="WebhookService.MaxConsecutiveFailures"/> failed deliveries in a row, the hook is deactivated.
/// </summary>
public sealed class WebhookWorker(WebhookDispatcher dispatcher, WebhookSender sender, IDbContextFactory<AppDbContext> dbFactory,
    PublicBaseUrl baseUrl, WebhookSecrets secrets, ILogger<WebhookWorker> log, IEnumerable<IWebhookTransport> transports) : BackgroundService
{
    public const int MaxConcurrent = 8;
    /// <summary>Waits before retries 1, 2 and 3.</summary>
    public static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];

    private readonly SemaphoreSlim slots = new(MaxConcurrent);
    private readonly IReadOnlyList<IWebhookTransport> transports = transports.ToList();

    /// <summary>The transport for a chat hook (Slack, Teams); null for generic webhooks.</summary>
    private IWebhookTransport? TransportFor(Webhook hook) =>
        hook.Kind == WebhookKind.Generic ? null : transports.FirstOrDefault(t => t.Handles(hook.Kind));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        await ResumePendingAsync(stoppingToken);
        try
        {
            await foreach (var job in dispatcher.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    foreach (var deliveryRowId in await PrepareAsync(job, stoppingToken))
                    {
                        await slots.WaitAsync(stoppingToken);
                        _ = Task.Run(async () =>
                        {
                            try { await DeliverAsync(deliveryRowId, stoppingToken); }
                            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                            catch (Exception ex) { log.LogError(ex, "Webhook delivery {Id} failed unexpectedly", deliveryRowId); }
                            finally { slots.Release(); }
                        }, CancellationToken.None);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { log.LogError(ex, "Could not prepare webhook job {Job}", job); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>Deliveries interrupted by a restart (recorded but never completed in the last day) are sent again.</summary>
    private async Task ResumePendingAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var since = DateTime.UtcNow.AddDays(-1);
            var pending = await db.WebhookDeliveries.Where(d => d.Success == null && d.CreatedAt > since).Select(d => d.Id).ToListAsync(ct);
            foreach (var id in pending) _ = DeliverAsync(id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Could not resume pending webhook deliveries"); }
    }

    /// <summary>Turns a job into stored delivery rows (one per target hook) and returns their ids.</summary>
    private async Task<List<int>> PrepareAsync(WebhookJob job, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = new List<WebhookDelivery>();
        switch (job)
        {
            case WebhookEventJob e:
            {
                var hooks = (await ChatIntegrations.ChatHookResolver.ActiveHooksAsync(db, e.ProjectId, ct))
                    .Where(h => WebhookEventNames.Includes(h.Events, e.Event)).ToList();
                if (hooks.Count == 0) return [];
                var envelope = await EnvelopeBaseAsync(db, e.ProjectId, e.SenderId, ct);
                foreach (var hook in hooks)
                {
                    var body = BuildObject(envelope, e.Event, e.Timestamp, e.Payload, out var id);
                    if (hook.Kind == WebhookKind.Generic) { rows.Add(NewDelivery(hook.Id, e.Event, body.ToJsonString(Dto.Json), id)); continue; }
                    if (TransportFor(hook) is not { } transport) continue;
                    try
                    {
                        foreach (var m in await transport.BuildAsync(db, hook, e.Event, body, ct))
                            rows.Add(NewDelivery(hook.Id, m.Event, m.Body, Guid.NewGuid().ToString()));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.LogError(ex, "Could not build the {Kind} message for hook {HookId} ({Event})", hook.Kind, hook.Id, e.Event);
                    }
                }
                break;
            }
            case WebhookPingJob p:
            {
                var hook = await db.Webhooks.FirstOrDefaultAsync(h => h.Id == p.WebhookId, ct);
                if (hook == null) return [];
                var envelope = await EnvelopeBaseAsync(db, hook.ProjectId, p.SenderId, ct);
                var payload = new JsonObject
                {
                    ["zen"] = "Keep it logically awesome.",
                    ["hook"] = new JsonObject
                    {
                        ["id"] = hook.Id, ["url"] = hook.Url, ["active"] = hook.IsActive,
                        ["events"] = new JsonArray([.. WebhookEventNames.Parse(hook.Events).Select(x => (JsonNode)JsonValue.Create(x)!)]),
                    },
                };
                var pingBody = BuildObject(envelope, WebhookEventNames.Ping, DateTimeOffset.UtcNow, payload, out var id);
                if (hook.Kind == WebhookKind.Generic) rows.Add(NewDelivery(hook.Id, WebhookEventNames.Ping, pingBody.ToJsonString(Dto.Json), id));
                else if (TransportFor(hook) is { } transport)
                {
                    var test = transport.BuildTest(hook, pingBody);
                    rows.Add(NewDelivery(hook.Id, test.Event, test.Body, id));
                }
                break;
            }
            case WebhookRedeliverJob r:
            {
                var original = await db.WebhookDeliveries.AsNoTracking().FirstOrDefaultAsync(d => d.Id == r.DeliveryRowId, ct);
                if (original == null) return [];
                var copy = NewDelivery(original.WebhookId, original.Event, original.Payload, original.DeliveryId);
                copy.IsRedelivery = true;
                rows.Add(copy);
                break;
            }
        }
        db.WebhookDeliveries.AddRange(rows);
        await db.SaveChangesAsync(ct);
        return rows.Select(r => r.Id).ToList();
    }

    private static WebhookDelivery NewDelivery(int hookId, string eventName, string body, string deliveryId) =>
        new() { WebhookId = hookId, Event = eventName, Payload = body, DeliveryId = deliveryId, CreatedAt = DateTime.UtcNow };

    private async Task<(JsonNode? Project, JsonNode? Sender)> EnvelopeBaseAsync(AppDbContext db, int? projectId, int? senderId, CancellationToken ct)
    {
        var project = projectId == null ? null : await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct);
        var sender = senderId is { } sid ? await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == sid, ct) : null;
        return (project == null ? null : JsonSerializer.SerializeToNode(Dto.ProjectRef(project, baseUrl.Value), Dto.Json),
                sender == null ? null : JsonSerializer.SerializeToNode(Dto.User(sender), Dto.Json));
    }

    /// <summary>The JSON body: event, deliveryId, timestamp, project, sender, then the event's own properties.</summary>
    private static JsonObject BuildObject((JsonNode? Project, JsonNode? Sender) common, string eventName, DateTimeOffset timestamp, JsonObject payload, out string deliveryId)
    {
        deliveryId = Guid.NewGuid().ToString();
        var body = new JsonObject
        {
            ["event"] = eventName,
            ["deliveryId"] = deliveryId,
            ["timestamp"] = timestamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["project"] = payload["project"]?.DeepClone() ?? common.Project?.DeepClone(),
            ["sender"] = payload["sender"]?.DeepClone() ?? common.Sender?.DeepClone(),
        };
        foreach (var (key, value) in payload)
            if (!body.ContainsKey(key)) body[key] = value?.DeepClone();
        return body;
    }

    private async Task DeliverAsync(int deliveryRowId, CancellationToken ct)
    {
        WebhookDelivery? delivery;
        Webhook? hook;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            delivery = await db.WebhookDeliveries.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deliveryRowId, ct);
            hook = delivery == null ? null : await db.Webhooks.AsNoTracking().FirstOrDefaultAsync(h => h.Id == delivery.WebhookId, ct);
        }
        if (delivery == null || hook == null) return;

        var body = Encoding.UTF8.GetBytes(delivery.Payload);
        WebhookAttempt attempt;
        var attempts = 0;
        string? secret = null;
        var transport = TransportFor(hook);
        if (hook.Kind == WebhookKind.Generic)
        {
            try { secret = secrets.Reveal(hook.Secret); }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                log.LogError(ex, "Webhook {HookId}: the secret can't be decrypted (Data Protection keys changed?)", hook.Id);
            }
        }
        while (true)
        {
            attempts++;
            if (hook.Kind != WebhookKind.Generic)
            {
                // Chat integrations: the transport formats nothing here (the body is stored), it only sends.
                attempt = transport == null
                    ? new WebhookAttempt(false, null, $"{hook.Kind} integrations aren't available on this server.", null, 0, Retryable: false)
                    : await transport.SendAsync(hook, delivery, ct);
                if (attempt.Success || !attempt.Retryable || attempts > Backoff.Length) break;
                log.LogInformation("{Kind} integration {HookId} delivery {Delivery} attempt {N} failed ({Error}); retrying",
                    hook.Kind, hook.Id, delivery.DeliveryId, attempts, attempt.Error);
                await Task.Delay(Backoff[attempts - 1], ct);
                continue;
            }
            if (secret == null)
            {
                attempt = new WebhookAttempt(false, null, "The webhook's secret can't be decrypted (the server's Data Protection keys changed). Rotate the secret with New secret.", null, 0, false);
                break;
            }
            attempt = await sender.SendAsync(hook.Url, delivery.Event, delivery.DeliveryId, secret, body, delivery.IsRedelivery, hook.Id, ct);
            if (attempt.Success || !attempt.Retryable || attempts > Backoff.Length) break;
            log.LogInformation("Webhook {HookId} delivery {Delivery} attempt {N} failed ({Error}); retrying", hook.Id, delivery.DeliveryId, attempts, attempt.Error);
            await Task.Delay(Backoff[attempts - 1], ct);
        }

        var now = DateTime.UtcNow;
        await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
        {
            await db.WebhookDeliveries.Where(d => d.Id == deliveryRowId).ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Success, attempt.Success)
                .SetProperty(d => d.StatusCode, attempt.StatusCode)
                .SetProperty(d => d.Error, attempt.Error)
                .SetProperty(d => d.ResponseBody, attempt.ResponseBody)
                .SetProperty(d => d.DurationMs, attempt.DurationMs)
                .SetProperty(d => d.Attempts, attempts)
                .SetProperty(d => d.CompletedAt, now), CancellationToken.None);

            var hooks = db.Webhooks.Where(h => h.Id == hook.Id);
            if (attempt.Success)
            {
                await hooks.ExecuteUpdateAsync(s => s.SetProperty(h => h.ConsecutiveFailures, 0).SetProperty(h => h.LastDeliveryAt, now), CancellationToken.None);
            }
            else
            {
                await hooks.ExecuteUpdateAsync(s => s.SetProperty(h => h.ConsecutiveFailures, h => h.ConsecutiveFailures + 1)
                    .SetProperty(h => h.LastDeliveryAt, now), CancellationToken.None);
                var disabled = await hooks.Where(h => h.IsActive && h.ConsecutiveFailures >= WebhookService.MaxConsecutiveFailures)
                    .ExecuteUpdateAsync(s => s.SetProperty(h => h.IsActive, false).SetProperty(h => h.DisabledAt, now), CancellationToken.None);
                if (disabled > 0)
                    log.LogWarning("Webhook {HookId} ({Url}) disabled after {N} consecutive failed deliveries", hook.Id, hook.Url, WebhookService.MaxConsecutiveFailures);
            }
        }
        log.LogInformation("Webhook {HookId} {Event} delivery {Delivery}: {Result} after {Attempts} attempt(s)",
            hook.Id, delivery.Event, delivery.DeliveryId, attempt.Success ? $"HTTP {attempt.StatusCode}" : attempt.Error, attempts);
    }
}
