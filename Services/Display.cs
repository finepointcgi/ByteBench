using GitBench.Data;
using Markdig;
using Microsoft.AspNetCore.Components;

namespace GitBench.Services;

/// <summary>Presentation helpers shared across components.</summary>
public static class Display
{
    public static string Name(this IssueStatus s) => s switch
    {
        IssueStatus.InProgress => "In Progress",
        IssueStatus.InReview => "In Review",
        IssueStatus.Todo => "To Do",
        _ => s.ToString()
    };

    public static string Css(this IssueStatus s) => s.ToString().ToLowerInvariant();

    public static string Icon(this IssueType t) => t switch
    {
        IssueType.Bug => "🐞",
        IssueType.Feature => "✨",
        IssueType.Story => "📗",
        IssueType.Epic => "⚡",
        IssueType.Subtask => "↳",
        _ => "☑️"
    };

    public static string Icon(this IssuePriority p) => p switch
    {
        IssuePriority.Highest => "⇈",
        IssuePriority.High => "↑",
        IssuePriority.Medium => "=",
        IssuePriority.Low => "↓",
        _ => "⇊"
    };

    public static string Css(this IssuePriority p) => "prio-" + p.ToString().ToLowerInvariant();

    public static bool IsClosed(this IssueStatus s) => s == IssueStatus.Done;

    /// <summary>The issue's workflow status if it's loaded and consistent with its category; otherwise null.</summary>
    public static WorkflowStatus? WorkflowStatusOf(Issue i) => i.WorkflowStatus is { } w && w.Category == i.Status ? w : null;

    /// <summary>Status to show for an issue: its workflow status name, falling back to the category name.</summary>
    public static string StatusName(Issue i) => WorkflowStatusOf(i)?.Name ?? i.Status.Name();

    /// <summary>Inline style for a status chip in the workflow status's colour (empty: use the category's CSS class).</summary>
    public static string StatusStyle(string? color) => string.IsNullOrEmpty(color) ? ""
        : $"background:color-mix(in srgb, {color} 18%, var(--status-mix-bg, white));color:color-mix(in srgb, {color} 75%, var(--status-mix-fg, black))";

    /// <summary>
    /// Picks near-black or white text for a hex background: whichever has the higher WCAG contrast ratio. Chips and
    /// avatars carry their own background, so this holds in light and dark mode alike.
    /// </summary>
    public static string TextOn(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length < 6 || !int.TryParse(hex[..6], System.Globalization.NumberStyles.HexNumber, null, out var rgb)) return "#fff";
        static double Channel(int v) { var c = v / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        var lum = 0.2126 * Channel(rgb >> 16 & 0xff) + 0.7152 * Channel(rgb >> 8 & 0xff) + 0.0722 * Channel(rgb & 0xff);
        const double darkLum = 0.0056; // #111
        var onWhite = 1.05 / (lum + 0.05);
        var onDark = (lum + 0.05) / (darkLum + 0.05);
        return onDark > onWhite ? "#111" : "#fff";
    }

    public static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays}d ago";
        return utc.ToLocalTime().ToString("MMM d, yyyy");
    }

    internal static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Use<MentionExtension>() // @username → highlighted mention (Services/Mentions.cs)
        .DisableHtml() // raw HTML in user content is escaped, preventing XSS
        .Build();

    public static MarkupString Markdown(string? text) =>
        new(string.IsNullOrWhiteSpace(text) ? "" : Markdig.Markdown.ToHtml(text, Pipeline));
}
