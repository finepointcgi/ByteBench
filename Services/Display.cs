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
        : $"background:color-mix(in srgb, {color} 18%, white);color:color-mix(in srgb, {color} 75%, black)";

    /// <summary>Picks black or white text for readable contrast on a hex background.</summary>
    public static string TextOn(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length < 6) return "#fff";
        int r = Convert.ToInt32(hex[..2], 16), g = Convert.ToInt32(hex[2..4], 16), b = Convert.ToInt32(hex[4..6], 16);
        return (r * 299 + g * 587 + b * 114) / 1000 > 150 ? "#111" : "#fff";
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
