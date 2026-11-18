using System.Text;
using GitBench.Data;

namespace GitBench.Services.ChatIntegrations;

/// <summary>Colour of a message: green for success, red for failures, amber for warnings, blue for news.</summary>
public enum ChatStatus { Neutral, Info, Good, Warning, Danger }

public sealed record ChatField(string Title, string Value);

/// <summary>
/// A chat message independent of Slack or Teams: <see cref="SlackFormat"/> and <see cref="TeamsFormat"/> render it. All
/// strings are plain text (user content, unescaped); the formatters escape and truncate them for their format.
/// </summary>
public sealed class ChatMessage
{
    /// <summary>The chat event (<see cref="ChatEvents"/>), or <see cref="ChatEvents.Test"/>.</summary>
    public required string Event { get; init; }
    /// <summary>One line saying what happened, e.g. "alice moved WEB-12 to Done".</summary>
    public required string Summary { get; init; }
    /// <summary>The item, e.g. "WEB-12 Login button misaligned"; linked to <see cref="Url"/>.</summary>
    public required string Title { get; init; }
    public string? Url { get; init; }
    public ChatStatus Status { get; init; } = ChatStatus.Info;
    public string? Actor { get; init; }
    /// <summary>Small print under the message: project and repository.</summary>
    public string? Context { get; init; }
    /// <summary>Longer text: a comment, description, release notes or error (shortened by the formatters).</summary>
    public string? Body { get; init; }
    public List<ChatField> Fields { get; init; } = [];

    // ----- For filters -----
    public bool IsIssue { get; init; }
    public string? Branch { get; init; }
    public IReadOnlyList<string>? Labels { get; init; }
    public IssuePriority? Priority { get; init; }
}

/// <summary>Plain-text helpers shared by the formatters.</summary>
public static class ChatText
{
    /// <summary>Shortens to at most <paramref name="max"/> characters (ending in "…"), never splitting a surrogate pair.</summary>
    public static string Truncate(string? s, int max)
    {
        s ??= "";
        if (max <= 0) return "";
        if (s.Length <= max) return s;
        var cut = max - 1;
        if (cut > 0 && char.IsHighSurrogate(s[cut - 1])) cut--;
        return s[..cut].TrimEnd() + "…";
    }

    /// <summary>Collapses whitespace (newlines included) to single spaces.</summary>
    public static string OneLine(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Normalised line breaks, at most <paramref name="maxLines"/> lines, control characters removed.</summary>
    public static string Excerpt(string? s, int maxChars, int maxLines = 12)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var lines = s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var text = string.Join("\n", lines.Take(maxLines)).Trim();
        text = new string(text.Where(c => c == '\n' || c == '\t' || !char.IsControl(c)).ToArray());
        if (lines.Length > maxLines) text += "\n…";
        return Truncate(text, maxChars);
    }

    /// <summary>The URL if it is an absolute http(s) URL, else null (so formats never link to anything else).</summary>
    public static string? SafeUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u.AbsoluteUri : null;

    /// <summary>"refs/heads/main" → "main".</summary>
    public static string ShortRef(string? r) =>
        r == null ? "" : r.StartsWith("refs/heads/", StringComparison.Ordinal) ? r["refs/heads/".Length..]
            : r.StartsWith("refs/tags/", StringComparison.Ordinal) ? r["refs/tags/".Length..] : r;

    public static string ShortSha(string? sha) => sha is { Length: > 8 } ? sha[..8] : sha ?? "";

    /// <summary>Removes the Markdown bold markers GitBench puts in activity text ("changed status from **A** to **B**").</summary>
    public static string StripBold(string? s) => (s ?? "").Replace("**", "");
}
