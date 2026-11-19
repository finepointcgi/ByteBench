using System.Text.Json.Nodes;
using ByteBench.Data;

namespace ByteBench.Services.Webhooks;

/// <summary>One delivery a transport wants recorded: the event name shown in the delivery log and the exact body to send.</summary>
public sealed record WebhookOutgoing(string Event, string Body);

/// <summary>
/// Delivers hooks of a non-generic <see cref="WebhookKind"/> (Slack, Teams) through the webhook pipeline: the
/// <see cref="WebhookWorker"/> still drains the queue, records a <see cref="WebhookDelivery"/> per message, retries and
/// auto-disables; the transport only decides what to send (<see cref="BuildAsync"/>) and how (<see cref="SendAsync"/>).
/// </summary>
public interface IWebhookTransport
{
    bool Handles(WebhookKind kind);

    /// <summary>
    /// The messages <paramref name="hook"/> gets for one event (none if it isn't subscribed or a filter rejects it).
    /// <paramref name="envelope"/> is the generic webhook body (event, project, sender and the event's payload).
    /// </summary>
    Task<IReadOnlyList<WebhookOutgoing>> BuildAsync(AppDbContext db, Webhook hook, string eventName, JsonObject envelope, CancellationToken ct);

    /// <summary>The "Send test message" body.</summary>
    WebhookOutgoing BuildTest(Webhook hook, JsonObject envelope);

    /// <summary>One attempt. Must never put secrets (URLs, tokens) into the returned error or response text.</summary>
    Task<WebhookAttempt> SendAsync(Webhook hook, WebhookDelivery delivery, CancellationToken ct);
}
