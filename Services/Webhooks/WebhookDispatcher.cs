using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ByteBench.Api;

namespace ByteBench.Services.Webhooks;

/// <summary>Work for <see cref="WebhookWorker"/>.</summary>
public abstract record WebhookJob;

/// <summary>An event in a project, fanned out to every active hook subscribed to it.</summary>
public sealed record WebhookEventJob(int ProjectId, string Event, JsonObject Payload, int? SenderId, DateTimeOffset Timestamp) : WebhookJob;

/// <summary>A <c>ping</c> to one hook ("Send test ping"). Sent even if the hook is inactive.</summary>
public sealed record WebhookPingJob(int WebhookId, int? SenderId) : WebhookJob;

/// <summary>Sends a stored delivery's exact body again, under the same delivery id.</summary>
public sealed record WebhookRedeliverJob(int DeliveryRowId) : WebhookJob;

/// <summary>
/// The public entry point for raising webhook events (singleton). <see cref="Enqueue"/> only writes to an in-memory
/// queue and returns immediately, so callers are never blocked by slow receivers; <see cref="WebhookWorker"/> builds
/// the envelope, records a <see cref="Data.WebhookDelivery"/> per hook and delivers it in the background.
/// </summary>
public sealed class WebhookDispatcher(ILogger<WebhookDispatcher> log)
{
    private readonly Channel<WebhookJob> channel = Channel.CreateBounded<WebhookJob>(
        new BoundedChannelOptions(10_000) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });

    public ChannelReader<WebhookJob> Reader => channel.Reader;

    /// <summary>
    /// Queues <paramref name="eventName"/> (e.g. <c>"pipeline.completed"</c>) for every active webhook of the project that
    /// subscribes to it. <paramref name="payload"/> is serialized with the API's JSON settings (camelCase) and its
    /// properties are merged into the envelope next to <c>event</c>, <c>deliveryId</c>, <c>timestamp</c>,
    /// <c>project</c> and <c>sender</c> (a payload may supply its own <c>project</c>/<c>sender</c>). Never throws for
    /// delivery problems and never blocks. Returns false if the queue is full (the event is dropped and logged).
    /// </summary>
    /// <param name="senderUserId">The user who caused the event, for <c>sender</c>; null for system events.</param>
    public bool Enqueue(int projectId, string eventName, object payload, int? senderUserId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        var node = payload as JsonObject ?? JsonSerializer.SerializeToNode(payload, payload.GetType(), Dto.Json) as JsonObject
            ?? new JsonObject { ["data"] = JsonSerializer.SerializeToNode(payload, payload.GetType(), Dto.Json) };
        return Write(new WebhookEventJob(projectId, eventName, node, senderUserId, DateTimeOffset.UtcNow));
    }

    public bool EnqueuePing(int webhookId, int? senderUserId) => Write(new WebhookPingJob(webhookId, senderUserId));

    public bool EnqueueRedelivery(int deliveryRowId) => Write(new WebhookRedeliverJob(deliveryRowId));

    private bool Write(WebhookJob job)
    {
        if (channel.Writer.TryWrite(job)) return true;
        log.LogWarning("Webhook queue is full; dropped {Job}", job.GetType().Name);
        return false;
    }
}
