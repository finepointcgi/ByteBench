namespace ByteBench.Services.ChatIntegrations;

/// <summary>
/// The site's Slack app (section <c>Slack</c>), used by the slash command, interactivity, link unfurling and DMs. Keep
/// the secrets out of appsettings: use user-secrets in development (<c>dotnet user-secrets set Slack:SigningSecret …</c>)
/// or environment variables (<c>Slack__SigningSecret</c>, <c>Slack__BotToken</c>) in production. Per-project channel
/// integrations don't need any of this: their webhook URLs / bot tokens are stored encrypted per integration.
/// </summary>
public sealed class SlackOptions
{
    /// <summary>The app's signing secret: requests to <c>/integrations/slack/*</c> are rejected unless signed with it.</summary>
    public string? SigningSecret { get; set; }
    /// <summary>Bot token (<c>xoxb-…</c>) for unfurling links and DMs. Optional.</summary>
    public string? BotToken { get; set; }
    /// <summary>When set, only requests from this workspace (team id, e.g. <c>T0123</c>) are accepted.</summary>
    public string? TeamId { get; set; }
    /// <summary>How old a request's <c>X-Slack-Request-Timestamp</c> may be (replay protection). Default 5 minutes.</summary>
    public int TimestampToleranceSeconds { get; set; } = 300;
    /// <summary>Slack Web API base URL.</summary>
    public string ApiBaseUrl { get; set; } = "https://slack.com/api/";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SigningSecret);
    public bool HasBotToken => !string.IsNullOrWhiteSpace(BotToken);
}

/// <summary>Section <c>ChatIntegrations</c>.</summary>
public sealed class ChatIntegrationOptions
{
    /// <summary>
    /// Extra hosts integrations may post to, besides Slack's and Teams' own (e.g. a proxy). An entry starting with a dot
    /// matches subdomains (<c>.example.com</c>); otherwise the host must match exactly.
    /// </summary>
    public List<string> AllowedHosts { get; set; } = [];
}

/// <summary>Which URLs an integration may post to (on top of the webhook SSRF checks).</summary>
public static class ChatUrlRules
{
    public static readonly IReadOnlyList<string> SlackHosts = ["hooks.slack.com"];
    /// <summary>Teams incoming webhooks (<c>*.webhook.office.com</c>) and Workflows / Power Automate triggers.</summary>
    public static readonly IReadOnlyList<string> TeamsHosts = [".webhook.office.com", ".logic.azure.com", ".api.powerplatform.com", ".powerautomate.com"];

    /// <summary>Null if <paramref name="url"/> is acceptable for <paramref name="kind"/>, else why not (never echoes the URL: it is a secret).</summary>
    public static string? Validate(Data.WebhookKind kind, string? url, IEnumerable<string> extraHosts)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" || string.IsNullOrEmpty(uri.Host))
            return "Enter the full https:// URL.";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "Credentials in the URL aren't allowed.";
        var allowed = (kind == Data.WebhookKind.Teams ? TeamsHosts : SlackHosts).Concat(extraHosts);
        if (!allowed.Any(h => HostMatches(h, uri.Host)))
            return kind == Data.WebhookKind.Teams
                ? "Use a Teams incoming webhook (…webhook.office.com) or a Workflows URL (…logic.azure.com / …api.powerplatform.com)."
                : "Use a Slack incoming webhook URL (https://hooks.slack.com/services/…).";
        return null;
    }

    public static bool HostMatches(string pattern, string host)
    {
        pattern = pattern.Trim().ToLowerInvariant();
        host = host.ToLowerInvariant();
        return pattern.StartsWith('.') ? host.EndsWith(pattern, StringComparison.Ordinal) || host == pattern[1..] : host == pattern;
    }
}
