using System.Text.Json.Nodes;

namespace GitBench.Services.ChatIntegrations;

/// <summary>
/// Renders a <see cref="ChatMessage"/> as a Microsoft Teams message carrying an Adaptive Card (v1.4), the format both
/// Teams incoming webhooks and the Workflows "post card" flow accept.
/// <para>
/// <b>Escaping.</b> <c>TextBlock</c> and <c>FactSet</c> parse Markdown, so user content never goes there: every piece of
/// text is a <c>TextRun</c> inside a <c>RichTextBlock</c>, which Teams shows verbatim (no Markdown, no HTML). The JSON
/// serializer handles quoting. Links are only absolute http(s) URLs. Texts are truncated so the card stays well under
/// Teams' ~28 KB message limit.
/// </para>
/// </summary>
public static class TeamsFormat
{
    public const int MaxTitle = 250;
    public const int MaxBody = 1500;
    public const int MaxFieldValue = 300;
    public const int MaxFields = 10;

    public static string ContainerStyle(ChatStatus status) => status switch
    {
        ChatStatus.Good => "good",
        ChatStatus.Danger => "attention",
        ChatStatus.Warning => "warning",
        ChatStatus.Info => "accent",
        _ => "emphasis",
    };

    public static JsonObject Card(ChatMessage m)
    {
        var url = ChatText.SafeUrl(m.Url);
        var body = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "Container",
                ["style"] = ContainerStyle(m.Status),
                ["bleed"] = true,
                ["items"] = new JsonArray { Rich(Run(ChatText.Truncate(ChatText.OneLine(m.Summary), 300), weight: "Bolder")) },
            },
            Rich(Run(ChatText.Truncate(ChatText.OneLine(m.Title), MaxTitle), weight: "Bolder", size: "Medium", color: url != null ? "Accent" : null, url: url)),
        };
        foreach (var f in m.Fields.Where(f => !string.IsNullOrWhiteSpace(f.Value)).Take(MaxFields))
        {
            body.Add(new JsonObject
            {
                ["type"] = "ColumnSet",
                ["spacing"] = "Small",
                ["columns"] = new JsonArray
                {
                    Column("110px", Rich(Run(ChatText.Truncate(ChatText.OneLine(f.Title), 40), weight: "Bolder"))),
                    Column("stretch", Rich(Run(ChatText.Truncate(f.Value, MaxFieldValue)))),
                },
            });
        }
        if (!string.IsNullOrWhiteSpace(m.Body)) body.Add(Rich(Run(ChatText.Truncate(m.Body, MaxBody))));
        var context = string.Join(" · ", new[] { m.Actor, m.Context }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => ChatText.OneLine(s)));
        if (context.Length > 0) body.Add(Rich(Run(ChatText.Truncate(context, 300), subtle: true, size: "Small")));

        var card = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.4",
            ["msteams"] = new JsonObject { ["width"] = "Full" },
            ["fallbackText"] = ChatText.Truncate(ChatText.OneLine(m.Summary), 300),
            ["body"] = body,
        };
        if (url != null)
            card["actions"] = new JsonArray { new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = "Open in GitBench", ["url"] = url } };
        return card;
    }

    /// <summary>The POST body for a Teams incoming webhook or Workflows URL.</summary>
    public static JsonObject Message(ChatMessage m) => new()
    {
        ["type"] = "message",
        ["summary"] = ChatText.Truncate(ChatText.OneLine(m.Summary), 150),
        ["attachments"] = new JsonArray
        {
            new JsonObject
            {
                ["contentType"] = "application/vnd.microsoft.card.adaptive",
                ["contentUrl"] = null,
                ["content"] = Card(m),
            },
        },
    };

    private static JsonObject Rich(JsonObject run) => new() { ["type"] = "RichTextBlock", ["inlines"] = new JsonArray { run } };

    private static JsonObject Column(string width, JsonObject item) => new() { ["type"] = "Column", ["width"] = width, ["items"] = new JsonArray { item } };

    private static JsonObject Run(string text, string? weight = null, string? size = null, string? color = null, bool subtle = false, string? url = null)
    {
        var run = new JsonObject { ["type"] = "TextRun", ["text"] = text };
        if (weight != null) run["weight"] = weight;
        if (size != null) run["size"] = size;
        if (color != null) run["color"] = color;
        if (subtle) run["isSubtle"] = true;
        if (url != null) run["selectAction"] = new JsonObject { ["type"] = "Action.OpenUrl", ["url"] = url };
        return run;
    }
}
