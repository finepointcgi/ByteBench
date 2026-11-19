using System.Text.Json.Nodes;

namespace GitBench.Services.ChatIntegrations;

/// <summary>
/// Renders a <see cref="ChatMessage"/> as a Slack message with Block Kit blocks inside a coloured attachment.
/// <para>
/// <b>Escaping.</b> All user content goes into <c>mrkdwn</c> text with <c>&amp;</c>, <c>&lt;</c> and <c>&gt;</c> escaped,
/// which is what Slack requires: it disarms <c>&lt;!channel&gt;</c>, <c>&lt;@U123&gt;</c> and <c>&lt;url|label&gt;</c> in
/// titles and comments. Link targets are only absolute http(s) URLs with <c>|</c>, <c>&lt;</c> and <c>&gt;</c>
/// percent-encoded. Text is truncated <i>before</i> escaping and re-checked after, so an entity is never cut in half,
/// and every block stays within Slack's limits (section text 3000, field 2000, 10 fields).
/// </para>
/// </summary>
public static class SlackFormat
{
    public const int MaxSectionText = 3000;
    public const int MaxFieldText = 2000;
    public const int MaxFields = 10;
    public const int MaxTitle = 250;
    public const int MaxBody = 1500;
    public const int MaxFallback = 300;

    public static string Color(ChatStatus status) => status switch
    {
        ChatStatus.Good => "#2EB67D",
        ChatStatus.Danger => "#E01E5A",
        ChatStatus.Warning => "#ECB22E",
        ChatStatus.Info => "#36C5F0",
        _ => "#9E9E9E",
    };

    /// <summary>Escapes text for Slack <c>mrkdwn</c> / <c>plain_text</c>.</summary>
    public static string Escape(string? s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Escaped text of at most <paramref name="max"/> characters (truncated before escaping, so no entity is split).</summary>
    public static string Text(string? raw, int max)
    {
        raw ??= "";
        var n = Math.Min(raw.Length, max);
        while (true)
        {
            var escaped = Escape(ChatText.Truncate(raw, n));
            if (escaped.Length <= max) return escaped;
            if (n <= 1) return "…";
            // Shrink in proportion to the overshoot (entities expand up to 5×), at least by one character.
            n = (int)Math.Max(1, Math.Min(n - 1, (long)n * max / escaped.Length));
        }
    }

    /// <summary><c>&lt;url|label&gt;</c>, or just the escaped label when the URL isn't a safe http(s) URL.</summary>
    public static string Link(string? url, string label, int maxLabel = MaxTitle)
    {
        var text = Text(label, maxLabel);
        if (ChatText.SafeUrl(url) is not { } safe) return text;
        safe = safe.Replace("|", "%7C").Replace("<", "%3C").Replace(">", "%3E");
        return $"<{safe}|{text}>";
    }

    /// <summary>The coloured attachment (also the shape of a link unfurl).</summary>
    public static JsonObject Attachment(ChatMessage m)
    {
        var blocks = new JsonArray
        {
            Section($"*{Link(m.Url, m.Title)}*"),
        };
        var fields = m.Fields.Where(f => !string.IsNullOrWhiteSpace(f.Value)).Take(MaxFields).ToList();
        if (fields.Count > 0)
        {
            var array = new JsonArray();
            foreach (var f in fields)
                array.Add(Mrkdwn($"*{Text(ChatText.OneLine(f.Title), 100)}*\n{Text(f.Value, MaxFieldText - 110)}"));
            blocks.Add(new JsonObject { ["type"] = "section", ["fields"] = array });
        }
        if (!string.IsNullOrWhiteSpace(m.Body)) blocks.Add(Section(Text(m.Body, MaxBody)));
        var context = string.Join(" · ", new[] { m.Actor, m.Context }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => ChatText.OneLine(s)));
        if (context.Length > 0)
            blocks.Add(new JsonObject { ["type"] = "context", ["elements"] = new JsonArray { Mrkdwn(Text(context, 300)) } });
        return new JsonObject
        {
            ["color"] = Color(m.Status),
            ["fallback"] = Text(ChatText.OneLine(m.Summary), MaxFallback),
            ["blocks"] = blocks,
        };
    }

    /// <summary>
    /// A complete message for an incoming webhook or <c>chat.postMessage</c> (<paramref name="channel"/> set in bot mode).
    /// Link previews are off: the message already summarises the item.
    /// </summary>
    public static JsonObject Message(ChatMessage m, string? channel = null)
    {
        var msg = new JsonObject();
        if (!string.IsNullOrWhiteSpace(channel)) msg["channel"] = channel.Trim();
        msg["text"] = Text(ChatText.OneLine(m.Summary), MaxFallback);
        msg["blocks"] = new JsonArray { Section(Text(ChatText.OneLine(m.Summary), MaxSectionText)) };
        msg["attachments"] = new JsonArray { Attachment(m) };
        msg["unfurl_links"] = false;
        msg["unfurl_media"] = false;
        return msg;
    }

    /// <summary>A slash command reply: only the caller sees it unless <paramref name="inChannel"/>.</summary>
    public static JsonObject Reply(string text, ChatMessage? card = null, bool inChannel = false, JsonArray? extraBlocks = null)
    {
        var blocks = new JsonArray { Section(Text(text, MaxSectionText)) };
        if (extraBlocks != null) foreach (var b in extraBlocks) blocks.Add(b?.DeepClone());
        var reply = new JsonObject
        {
            ["response_type"] = inChannel ? "in_channel" : "ephemeral",
            ["text"] = Text(ChatText.OneLine(text), MaxFallback),
            ["blocks"] = blocks,
        };
        if (card != null) reply["attachments"] = new JsonArray { Attachment(card) };
        return reply;
    }

    /// <summary>A reply whose text is already Slack mrkdwn (built with <see cref="Text"/>/<see cref="Link"/>).</summary>
    public static JsonObject ReplyMrkdwn(string mrkdwn, string fallback, JsonArray? extraBlocks = null)
    {
        var blocks = new JsonArray { Section(mrkdwn.Length <= MaxSectionText ? mrkdwn : Text(fallback, MaxSectionText)) };
        if (extraBlocks != null) foreach (var b in extraBlocks) blocks.Add(b?.DeepClone());
        return new JsonObject
        {
            ["response_type"] = "ephemeral",
            ["text"] = Text(ChatText.OneLine(fallback), MaxFallback),
            ["blocks"] = blocks,
        };
    }

    /// <summary>A button that opens <paramref name="url"/> (no interactivity round trip).</summary>
    public static JsonObject LinkButton(string label, string url, string actionId) => new()
    {
        ["type"] = "actions",
        ["elements"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "button", ["action_id"] = actionId, ["url"] = url, ["style"] = "primary",
                ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = Text(label, 75) },
            },
        },
    };

    public static JsonObject Section(string mrkdwn) => new() { ["type"] = "section", ["text"] = Mrkdwn(mrkdwn) };

    private static JsonObject Mrkdwn(string text) => new() { ["type"] = "mrkdwn", ["text"] = text };
}
