using ByteBench.Data;
using Markdig;
using Markdig.Syntax;
using Microsoft.AspNetCore.Components;

namespace ByteBench.Services;

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

    public static MarkupString Markdown(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new("");
        var doc = Markdig.Markdown.Parse(text, Pipeline);
        NeutralizeUnsafeUrls(doc);
        return new(Markdig.Markdown.ToHtml(doc, Pipeline));
    }

    /// <summary>
    /// Link and image destinations (including reference links and &lt;scheme:…&gt; autolinks) that name a scheme other than
    /// http, https or mailto become "#": DisableHtml stops raw HTML, but not <c>[x](javascript:…)</c>, which runs on click
    /// wherever there is no CSP (notification emails, or the CSP switched off).
    /// </summary>
    public static void NeutralizeUnsafeUrls(Markdig.Syntax.MarkdownDocument doc)
    {
        foreach (var link in doc.Descendants<Markdig.Syntax.Inlines.LinkInline>())
            if (!IsSafeUrl(link.Url)) link.Url = "#";
        foreach (var auto in doc.Descendants<Markdig.Syntax.Inlines.AutolinkInline>())
            if (!auto.IsEmail && !IsSafeUrl(auto.Url)) auto.Url = "#";
    }

    /// <summary>Relative, or http/https/mailto, judged as a browser reads it (ASCII whitespace and control characters removed).</summary>
    public static bool IsSafeUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return true;
        var s = new string(url.Where(c => c > ' ').ToArray());
        var colon = s.IndexOf(':');
        if (colon < 0) return true;
        var end = s.IndexOfAny(['/', '?', '#']);
        if (end >= 0 && end < colon) return true; // the ':' is in the path, query or fragment: a relative URL
        return s[..colon].ToLowerInvariant() is "http" or "https" or "mailto";
    }
}
