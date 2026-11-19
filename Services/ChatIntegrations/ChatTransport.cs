using System.Security.Cryptography;
using System.Text.Json.Nodes;
using GitBench.Data;
using GitBench.Services.Webhooks;
using Microsoft.Extensions.Options;

namespace GitBench.Services.ChatIntegrations;

/// <summary>
/// The Slack / Teams <see cref="IWebhookTransport"/>: formats chat messages from webhook events (Block Kit or Adaptive
/// Card, chosen by the integration's <see cref="ChatMode"/>) and posts them. Everything else (queue, delivery log,
/// retries, auto-disable, redelivery) is the webhook pipeline's.
/// </summary>
public sealed class ChatTransport(WebhookSecrets secrets, ChatHttp http, IOptionsMonitor<ChatIntegrationOptions> options,
    ILogger<ChatTransport> log) : IWebhookTransport
{
    public bool Handles(WebhookKind kind) => kind is WebhookKind.Slack or WebhookKind.Teams;

    public async Task<IReadOnlyList<WebhookOutgoing>> BuildAsync(AppDbContext db, Webhook hook, string eventName, JsonObject envelope, CancellationToken ct)
    {
        var settings = ChatSettings.Parse(hook.ChatSettings);
        if (settings.Events.Count == 0) return [];
        var messages = await ChatEventClassifier.ClassifyAsync(db, eventName, envelope, ct);
        return messages.Where(m => settings.Events.Contains(m.Event) && settings.Filters.Matches(m))
            .Select(m => new WebhookOutgoing(m.Event, Format(settings, m).ToJsonString()))
            .ToList();
    }

    public WebhookOutgoing BuildTest(Webhook hook, JsonObject envelope)
    {
        var settings = ChatSettings.Parse(hook.ChatSettings);
        var key = envelope["project"]?["key"]?.GetValue<string>();
        var name = envelope["project"]?["name"]?.GetValue<string>();
        var actor = envelope["sender"]?["displayName"]?.GetValue<string>();
        var m = new ChatMessage
        {
            Event = ChatEvents.Test,
            Summary = $"GitBench is connected: this channel will get updates from {key}.",
            Title = $"{key} {name}".Trim(),
            Url = envelope["project"]?["webUrl"]?.GetValue<string>(),
            Status = ChatStatus.Good,
            Actor = actor,
            Context = "Test message",
            Fields = [new("Events", string.Join(", ", settings.Events.Select(e => ChatEvents.Find(e)?.Title ?? e)))],
        };
        return new WebhookOutgoing(ChatEvents.Test, Format(settings, m).ToJsonString());
    }

    public static JsonObject Format(ChatSettings settings, ChatMessage m) => settings.Mode switch
    {
        ChatMode.TeamsWebhook => TeamsFormat.Message(m),
        ChatMode.SlackBot => SlackFormat.Message(m, m.Event == ChatEvents.Test ? settings.Channel : settings.ChannelFor(ChatEvents.GroupOf(m.Event))),
        _ => SlackFormat.Message(m),
    };

    public async Task<WebhookAttempt> SendAsync(Webhook hook, WebhookDelivery delivery, CancellationToken ct)
    {
        ChatSecrets s;
        try { s = ChatSecrets.Parse(secrets.Reveal(hook.Secret)); }
        catch (Exception ex) when (ex is CryptographicException or System.Text.Json.JsonException)
        {
            log.LogError(ex, "{Kind} integration {HookId}: its secrets can't be decrypted", hook.Kind, hook.Id);
            return new(false, null, "The integration's URL or token can't be decrypted (the server's Data Protection keys changed). Enter it again.", null, 0, Retryable: false);
        }
        var settings = ChatSettings.Parse(hook.ChatSettings);
        var group = delivery.Event == ChatEvents.Test ? (ChatEventGroup?)null : ChatEvents.GroupOf(delivery.Event);
        if (settings.Mode == ChatMode.SlackBot)
        {
            if (string.IsNullOrWhiteSpace(s.Token)) return new(false, null, "No bot token is configured.", null, 0, Retryable: false);
            return await http.CallSlackAsync("chat.postMessage", s.Token, delivery.Payload, s.All(), ct);
        }
        var url = group is { } g ? s.UrlFor(g) : s.Url;
        if (ChatUrlRules.Validate(hook.Kind, url, options.CurrentValue.AllowedHosts) is { } problem)
            return new(false, null, problem, null, 0, Retryable: false);
        return await http.PostWebhookAsync(url!, delivery.Payload, s.All(), ct);
    }
}
