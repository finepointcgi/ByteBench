using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GitBench.Data;

namespace GitBench.Services.ChatIntegrations;

/// <summary>How an integration posts: a Slack incoming webhook, the Slack Web API with a bot token, or a Teams webhook / Workflows URL.</summary>
public enum ChatMode { SlackWebhook, SlackBot, TeamsWebhook }

/// <summary>
/// An integration's non-secret settings, stored as JSON in <see cref="Webhook.ChatSettings"/>. Secrets (URLs and
/// tokens) live in <see cref="ChatSecrets"/>, encrypted in <see cref="Webhook.Secret"/>.
/// </summary>
public sealed class ChatSettings
{
    public ChatMode Mode { get; set; }
    /// <summary>Chat event names (<see cref="ChatEvents"/>).</summary>
    public List<string> Events { get; set; } = [];
    /// <summary>Bot token mode: the default channel (e.g. <c>#dev</c> or a channel id).</summary>
    public string? Channel { get; set; }
    /// <summary>Bot token mode: channels for event groups that shouldn't go to <see cref="Channel"/>.</summary>
    public Dictionary<ChatEventGroup, string> GroupChannels { get; set; } = [];
    public ChatFilters Filters { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static ChatSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<ChatSettings>(json, Json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Where messages of <paramref name="group"/> go in bot mode.</summary>
    public string? ChannelFor(ChatEventGroup group) =>
        GroupChannels.TryGetValue(group, out var c) && !string.IsNullOrWhiteSpace(c) ? c : Channel;
}

/// <summary>
/// Optional filters. Each applies only to events that carry that information: branches to pipelines, deployments and
/// pull requests (target branch); labels and minimum priority ("severity") to issue events and mentions in issues.
/// </summary>
public sealed class ChatFilters
{
    /// <summary>Branch names or globs (<c>main</c>, <c>release/*</c>); empty: any branch.</summary>
    public List<string> Branches { get; set; } = [];
    /// <summary>Only issues with at least one of these labels (case-insensitive); empty: any.</summary>
    public List<string> Labels { get; set; } = [];
    /// <summary>Only issues at or above this priority; null: any.</summary>
    public IssuePriority? MinPriority { get; set; }

    public bool IsEmpty => Branches.Count == 0 && Labels.Count == 0 && MinPriority == null;

    public bool Matches(ChatMessage m)
    {
        if (Branches.Count > 0 && m.Branch != null && !Branches.Any(b => BranchMatches(b, m.Branch))) return false;
        if (m.IsIssue)
        {
            if (Labels.Count > 0 && !(m.Labels ?? []).Any(l => Labels.Contains(l, StringComparer.OrdinalIgnoreCase))) return false;
            if (MinPriority is { } min && m.Priority is { } p && p < min) return false;
        }
        return true;
    }

    /// <summary><c>*</c> matches any run of characters (including <c>/</c>); everything else literally.</summary>
    public static bool BranchMatches(string pattern, string branch)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0) return false;
        if (!pattern.Contains('*')) return string.Equals(pattern, branch, StringComparison.Ordinal);
        var regex = "^" + string.Join(".*", pattern.Split('*').Select(Regex.Escape)) + "$";
        return Regex.IsMatch(branch, regex, RegexOptions.None, TimeSpan.FromMilliseconds(100));
    }
}

/// <summary>An integration's secrets: encrypted together in <see cref="Webhook.Secret"/>, never shown back to anyone.</summary>
public sealed class ChatSecrets
{
    /// <summary>The Slack incoming webhook URL or the Teams webhook / Workflows URL.</summary>
    public string? Url { get; set; }
    /// <summary>Slack bot token (<c>xoxb-…</c>) for bot mode.</summary>
    public string? Token { get; set; }
    /// <summary>Webhook modes: other URLs for event groups that should post somewhere else.</summary>
    public Dictionary<ChatEventGroup, string> GroupUrls { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static ChatSecrets Parse(string json) => JsonSerializer.Deserialize<ChatSecrets>(json, Json) ?? new();

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public string? UrlFor(ChatEventGroup group) => GroupUrls.TryGetValue(group, out var u) && !string.IsNullOrWhiteSpace(u) ? u : Url;

    /// <summary>Every secret value, for scrubbing error and response text.</summary>
    public IEnumerable<string> All() =>
        new[] { Url, Token }.Concat(GroupUrls.Values).Where(s => !string.IsNullOrEmpty(s))!;
}
