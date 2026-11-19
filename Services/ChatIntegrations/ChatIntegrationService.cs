using System.Security.Cryptography;
using System.Text.RegularExpressions;
using GitBench.Data;
using GitBench.Services.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GitBench.Services.ChatIntegrations;

/// <summary>An integration as the settings page sees it: secrets only as hints ("••••3f9a").</summary>
public sealed record ChatIntegrationInfo(int Id, WebhookKind Kind, string Name, ChatSettings Settings, bool IsActive, DateTime? DisabledAt,
    int ConsecutiveFailures, DateTime? LastDeliveryAt, string? UrlHint, string? TokenHint, IReadOnlyDictionary<ChatEventGroup, string> GroupUrlHints);

/// <summary>
/// What the form submits. Secrets are write-only: on update a blank <see cref="Url"/>/<see cref="Token"/>/group URL
/// keeps the stored one; <see cref="ClearGroupUrls"/> removes group URLs.
/// </summary>
public sealed class ChatIntegrationInput
{
    public WebhookKind Kind { get; set; } = WebhookKind.Slack;
    public string? Name { get; set; }
    public ChatMode Mode { get; set; } = ChatMode.SlackWebhook;
    public string? Url { get; set; }
    public string? Token { get; set; }
    public Dictionary<ChatEventGroup, string?> GroupUrls { get; set; } = [];
    public HashSet<ChatEventGroup> ClearGroupUrls { get; set; } = [];
    public List<string> Events { get; set; } = [.. ChatEvents.Defaults];
    public string? Channel { get; set; }
    public Dictionary<ChatEventGroup, string?> GroupChannels { get; set; } = [];
    public List<string> Branches { get; set; } = [];
    public List<string> Labels { get; set; } = [];
    public IssuePriority? MinPriority { get; set; }
    public bool Active { get; set; } = true;
}

/// <summary>
/// A project's Slack and Teams integrations (page: <c>/p/{KEY}/settings/integrations</c>). Same permission as webhooks
/// (<see cref="WebhookService.RequiredPermission"/>, Maintainer+), same storage (<see cref="Webhook"/> rows of another
/// <see cref="WebhookKind"/>), same queue, retries and delivery log. URLs and tokens are encrypted with Data Protection
/// (like webhook and CI secrets) and never returned: the page only gets hints.
/// </summary>
public partial class ChatIntegrationService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, WebhookDispatcher dispatcher,
    WebhookSecrets secrets, WebhookSender sender, Audit.AuditLog audit, Hosting.AppRateLimits limits, IOptionsMonitor<ChatIntegrationOptions> options)
{
    public const Permission RequiredPermission = WebhookService.RequiredPermission;
    public const int MaxPerProject = 20;

    [GeneratedRegex(@"^[#@]?[A-Za-z0-9][A-Za-z0-9._-]{0,79}$")]
    private static partial Regex ChannelPattern();

    [GeneratedRegex(@"^xox[bp]-[A-Za-z0-9-]{10,250}$")]
    private static partial Regex TokenPattern();

    public static string Hint(string? secret) => string.IsNullOrEmpty(secret) ? "" : "••••••••" + (secret.Length > 8 ? secret[^4..] : "");

    public async Task<List<ChatIntegrationInfo>> ListAsync(int projectId)
    {
        await access.RequireAsync(projectId, RequiredPermission);
        await using var db = await dbFactory.CreateDbContextAsync();
        var hooks = await db.Webhooks.AsNoTracking().Where(h => h.ProjectId == projectId && h.Kind != WebhookKind.Generic).OrderBy(h => h.Id).ToListAsync();
        return hooks.Select(ToInfo).ToList();
    }

    public async Task<ChatIntegrationInfo> CreateAsync(int projectId, ChatIntegrationInput input)
    {
        await access.RequireAsync(projectId, RequiredPermission);
        var actor = await access.ResolveUserIdAsync() ?? throw new ForbiddenException("Sign in to manage integrations.");
        var hook = new Webhook { ProjectId = projectId, CreatedById = actor, Kind = input.Kind, IsActive = input.Active };
        Apply(hook, input, new ChatSecrets());
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.Webhooks.CountAsync(h => h.ProjectId == projectId && h.Kind != WebhookKind.Generic) >= MaxPerProject)
            throw new InvalidOperationException($"A project can have at most {MaxPerProject} chat integrations.");
        db.Webhooks.Add(hook);
        await db.SaveChangesAsync();
        await audit.RecordAsync("chat_integration.created", "webhook", hook.Id, projectId, AuditDetails(hook));
        return ToInfo(hook);
    }

    public async Task<ChatIntegrationInfo> UpdateAsync(int projectId, int id, ChatIntegrationInput input)
    {
        await access.RequireAsync(projectId, RequiredPermission);
        await using var db = await dbFactory.CreateDbContextAsync();
        var hook = await db.Webhooks.FirstOrDefaultAsync(h => h.Id == id && h.ProjectId == projectId && h.Kind != WebhookKind.Generic)
                   ?? throw new InvalidOperationException("Integration not found.");
        if (input.Kind != hook.Kind) throw new InvalidOperationException("An integration can't change between Slack and Teams; add a new one.");
        var current = RevealOrEmpty(hook.Secret);
        var secretBefore = hook.Secret;
        Apply(hook, input, current);
        if (input.Active && !hook.IsActive) { hook.ConsecutiveFailures = 0; hook.DisabledAt = null; }
        hook.IsActive = input.Active;
        await db.SaveChangesAsync();
        await audit.RecordAsync("chat_integration.updated", "webhook", hook.Id, projectId,
            new { kind = hook.Kind.ToString(), host = Audit.AuditLog.SafeUrl(hook.Url), hook.Name, hook.Events, hook.IsActive, secretReplaced = secretBefore != hook.Secret });
        return ToInfo(hook);
    }

    public async Task DeleteAsync(int projectId, int id)
    {
        await access.RequireAsync(projectId, RequiredPermission);
        await using var db = await dbFactory.CreateDbContextAsync();
        var hook = await db.Webhooks.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id && h.ProjectId == projectId && h.Kind != WebhookKind.Generic);
        if (hook == null) return;
        await db.Webhooks.Where(h => h.Id == id).ExecuteDeleteAsync();
        await audit.RecordAsync("chat_integration.deleted", "webhook", id, projectId, new { kind = hook.Kind.ToString(), hook.Name });
    }

    /// <summary>"Send test message": queued like a webhook ping (rate limited the same way).</summary>
    public async Task SendTestAsync(int projectId, int id)
    {
        await RequireAsync(projectId, id);
        var userId = await access.ResolveUserIdAsync();
        limits.CheckWebhookPing(userId);
        dispatcher.EnqueuePing(id, userId);
    }

    public async Task<List<WebhookDelivery>> RecentDeliveriesAsync(int projectId, int id, int take = 25)
    {
        await RequireAsync(projectId, id);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.WebhookDeliveries.AsNoTracking().Where(d => d.WebhookId == id).OrderByDescending(d => d.Id).Take(take).ToListAsync();
    }

    public async Task RedeliverAsync(int projectId, int deliveryRowId)
    {
        await access.RequireAsync(projectId, RequiredPermission);
        await using var db = await dbFactory.CreateDbContextAsync();
        var ok = await db.WebhookDeliveries.AnyAsync(d => d.Id == deliveryRowId
            && db.Webhooks.Any(h => h.Id == d.WebhookId && h.ProjectId == projectId && h.Kind != WebhookKind.Generic));
        if (!ok) throw new InvalidOperationException("Delivery not found.");
        dispatcher.EnqueueRedelivery(deliveryRowId);
    }

    private async Task RequireAsync(int projectId, int id)
    {
        await access.RequireAsync(projectId, RequiredPermission);
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await db.Webhooks.AnyAsync(h => h.Id == id && h.ProjectId == projectId && h.Kind != WebhookKind.Generic))
            throw new InvalidOperationException("Integration not found.");
    }

    // ----- Validation & mapping -----

    private void Apply(Webhook hook, ChatIntegrationInput input, ChatSecrets s)
    {
        if (input.Kind is not (WebhookKind.Slack or WebhookKind.Teams)) throw new InvalidOperationException("Choose Slack or Teams.");
        var mode = input.Kind == WebhookKind.Teams ? ChatMode.TeamsWebhook
            : input.Mode == ChatMode.SlackBot ? ChatMode.SlackBot : ChatMode.SlackWebhook;
        var extra = options.CurrentValue.AllowedHosts;

        var events = input.Events.Distinct().ToList();
        if (events.Count == 0) throw new InvalidOperationException("Choose at least one event.");
        if (events.FirstOrDefault(e => !ChatEvents.IsKnown(e)) is { } unknown) throw new InvalidOperationException($"Unknown event '{unknown}'.");

        var settings = new ChatSettings
        {
            Mode = mode,
            Events = events,
            Filters = new ChatFilters
            {
                Branches = Clean(input.Branches, 100, "branch pattern"),
                Labels = Clean(input.Labels, 100, "label"),
                MinPriority = input.MinPriority is { } p && Enum.IsDefined(p) ? p : null,
            },
        };

        var next = new ChatSecrets { Url = s.Url, Token = s.Token, GroupUrls = new(s.GroupUrls) };
        if (mode == ChatMode.SlackBot)
        {
            if (!string.IsNullOrWhiteSpace(input.Token))
            {
                var token = input.Token.Trim();
                if (!TokenPattern().IsMatch(token)) throw new InvalidOperationException("Enter a Slack bot token (xoxb-…).");
                next.Token = token;
            }
            if (string.IsNullOrEmpty(next.Token)) throw new InvalidOperationException("Enter the bot token.");
            next.Url = null;
            next.GroupUrls.Clear();
            settings.Channel = Channel(input.Channel, required: true);
            foreach (var (group, channel) in input.GroupChannels)
                if (Channel(channel, required: false) is { } c) settings.GroupChannels[group] = c;
            hook.Url = "https://slack.com/api";
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(input.Url))
            {
                if (ChatUrlRules.Validate(input.Kind, input.Url, extra) is { } bad) throw new InvalidOperationException(bad);
                next.Url = input.Url.Trim();
            }
            if (string.IsNullOrEmpty(next.Url)) throw new InvalidOperationException("Enter the webhook URL.");
            foreach (var g in input.ClearGroupUrls) next.GroupUrls.Remove(g);
            foreach (var (group, url) in input.GroupUrls)
            {
                if (string.IsNullOrWhiteSpace(url)) continue;
                if (ChatUrlRules.Validate(input.Kind, url, extra) is { } bad) throw new InvalidOperationException($"{ChatEvents.GroupTitle(group)}: {bad}");
                next.GroupUrls[group] = url.Trim();
            }
            next.Token = null;
            // Only scheme and host: the path of an incoming webhook URL is the secret.
            hook.Url = new Uri(next.Url).GetLeftPart(UriPartial.Authority);
        }
        foreach (var url in next.GroupUrls.Values.Append(next.Url).Where(u => u != null))
            if (sender.ValidateUrl(url!) is { } ssrf) throw new InvalidOperationException(ssrf);

        var name = input.Name?.Trim();
        hook.Name = string.IsNullOrEmpty(name) ? null : name.Length > 100 ? name[..100] : name;
        hook.Kind = input.Kind;
        hook.ChatSettings = settings.ToJson();
        hook.Events = string.Join(",", ChatEvents.SourcesOf(events));
        var json = next.ToJson();
        if (RevealOrNull(hook.Secret) != json) hook.Secret = secrets.Protect(json);
    }

    private static string? Channel(string? channel, bool required)
    {
        channel = channel?.Trim();
        if (string.IsNullOrEmpty(channel))
            return required ? throw new InvalidOperationException("Enter the channel to post to (e.g. #dev or a channel id).") : null;
        if (!ChannelPattern().IsMatch(channel)) throw new InvalidOperationException($"'{channel}' isn't a valid channel name or id.");
        return channel;
    }

    private static List<string> Clean(IEnumerable<string> values, int max, string what)
    {
        var list = values.Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count > 50) throw new InvalidOperationException($"At most 50 {what}s.");
        if (list.FirstOrDefault(v => v.Length > max) is { } tooLong) throw new InvalidOperationException($"The {what} '{tooLong[..20]}…' is too long.");
        return list;
    }

    private ChatSecrets RevealOrEmpty(string stored) =>
        RevealOrNull(stored) is { } json ? ChatSecrets.Parse(json) : new ChatSecrets();

    private string? RevealOrNull(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try { return secrets.Reveal(stored); }
        catch (CryptographicException) { return null; }
    }

    private ChatIntegrationInfo ToInfo(Webhook h)
    {
        var s = RevealOrEmpty(h.Secret);
        return new ChatIntegrationInfo(h.Id, h.Kind, h.Name ?? (h.Kind == WebhookKind.Teams ? "Microsoft Teams" : "Slack"), ChatSettings.Parse(h.ChatSettings),
            h.IsActive, h.DisabledAt, h.ConsecutiveFailures, h.LastDeliveryAt,
            s.Url == null ? null : Hint(s.Url), s.Token == null ? null : Hint(s.Token),
            s.GroupUrls.ToDictionary(kv => kv.Key, kv => Hint(kv.Value)));
    }

    private static object AuditDetails(Webhook h) =>
        new { kind = h.Kind.ToString(), host = Audit.AuditLog.SafeUrl(h.Url), h.Name, h.Events, h.IsActive };
}
