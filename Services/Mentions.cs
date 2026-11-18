using System.Collections.Concurrent;
using Markdig;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace GitBench.Services;

/// <summary>
/// <c>@username</c> mentions. Parsing is a Markdig inline parser, so mentions inside code spans and code blocks
/// are never recognised (Markdig doesn't run inline parsers there), both for rendering and for notifications.
/// </summary>
public static class Mentions
{
    // Usernames we know exist, so rendering only highlights real users. Case-insensitive like Identity's lookup.
    private static readonly ConcurrentDictionary<string, byte> Known = new(StringComparer.OrdinalIgnoreCase);

    public static void AddKnownUsers(IEnumerable<string?> userNames)
    {
        foreach (var n in userNames)
            if (!string.IsNullOrEmpty(n)) Known.TryAdd(n, 0);
    }

    public static bool IsKnown(string userName) => Known.ContainsKey(userName);

    /// <summary>Distinct usernames mentioned in Markdown text, outside code (case-insensitive, as written).</summary>
    public static IReadOnlySet<string> Extract(string? markdown)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(markdown)) return names;
        var doc = Markdig.Markdown.Parse(markdown, Display.Pipeline);
        foreach (var m in doc.Descendants<MentionInline>()) names.Add(m.UserName);
        return names;
    }

    internal static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '+';
}

/// <summary>A parsed <c>@username</c>.</summary>
public sealed class MentionInline : LeafInline
{
    public string UserName { get; init; } = "";
}

/// <summary>Registers the mention parser and its HTML renderer on a Markdig pipeline.</summary>
public sealed class MentionExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        if (!pipeline.InlineParsers.Contains<MentionParser>())
            pipeline.InlineParsers.Insert(0, new MentionParser());
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is HtmlRenderer html && !html.ObjectRenderers.Contains<MentionRenderer>())
            html.ObjectRenderers.Insert(0, new MentionRenderer());
    }
}

internal sealed class MentionParser : InlineParser
{
    public MentionParser() => OpeningCharacters = ['@'];

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        // Not part of a word or an email address: "bob@example.com" is not a mention.
        var prev = slice.PeekCharExtra(-1);
        if (Mentions.IsNameChar(prev) || prev is '@' or '/' or '\\') return false;

        var start = slice.Start + 1;
        var end = start;
        var text = slice.Text;
        if (end > slice.End || !char.IsAsciiLetterOrDigit(text[end])) return false;
        while (end + 1 <= slice.End && Mentions.IsNameChar(text[end + 1])) end++;
        // Trailing punctuation belongs to the sentence ("thanks @carol.").
        while (!char.IsAsciiLetterOrDigit(text[end])) end--;

        var name = text.Substring(start, end - start + 1);
        processor.Inline = new MentionInline
        {
            UserName = name,
            Span = new SourceSpan(processor.GetSourcePosition(slice.Start, out var line, out var column), processor.GetSourcePosition(end)),
            Line = line,
            Column = column,
        };
        slice.Start = end + 1;
        return true;
    }
}

internal sealed class MentionRenderer : HtmlObjectRenderer<MentionInline>
{
    protected override void Write(HtmlRenderer renderer, MentionInline m)
    {
        // The name only ever contains [A-Za-z0-9._+-], and is escaped anyway.
        if (renderer.EnableHtmlForInline && Mentions.IsKnown(m.UserName))
        {
            renderer.Write("<span class=\"mention\" title=\"@");
            renderer.WriteEscape(m.UserName);
            renderer.Write("\">@");
            renderer.WriteEscape(m.UserName);
            renderer.Write("</span>");
        }
        else
        {
            renderer.Write('@');
            renderer.WriteEscape(m.UserName);
        }
    }
}
