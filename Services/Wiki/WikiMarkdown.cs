using System.Text.RegularExpressions;
using ByteBench.Services.Git;
using Markdig;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Components;

namespace ByteBench.Services.Wiki;

/// <summary>Where a wiki page is rendered: its project, its own path and the pages that exist (for links).</summary>
public sealed record WikiRenderContext(string ProjectKey, string PagePath, IReadOnlyCollection<string> Pages);

/// <summary>
/// Wiki Markdown: the app's safe pipeline (<see cref="Display.Pipeline"/> settings: raw HTML escaped, @mentions) plus
/// <list type="bullet">
/// <item><c>[[Page Name]]</c> and <c>[[path/to/page|label]]</c> wiki links (resolved next to the current page, then from
/// the root, then by title; missing pages get the <c>wiki-missing</c> class and open the editor);</item>
/// <item>relative links and images resolved against the page's folder (<c>uploads/…</c> from the wiki root): pages go to
/// <c>/p/{KEY}/wiki/…</c>, other files to <see cref="WikiPaths.FileUrl"/>;</item>
/// <item>this project's issue keys (<c>WEB-12</c>) linked to the issue, outside code and existing links;</item>
/// <item>links with a scheme other than http, https or mailto are neutralised.</item>
/// </list>
/// </summary>
public static partial class WikiMarkdown
{
    internal static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Use<MentionExtension>()
        .Use<WikiLinkExtension>()
        .DisableHtml()
        .Build();

    public static MarkupString Render(string? markdown, WikiRenderContext ctx) => new(RenderHtml(markdown, ctx));

    public static string RenderHtml(string? markdown, WikiRenderContext ctx)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var doc = Markdig.Markdown.Parse(markdown, Pipeline);
        var pages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in ctx.Pages) pages.TryAdd(p, p);
        var dir = WikiPaths.DirOf(ctx.PagePath);

        foreach (var link in doc.Descendants<LinkInline>().ToList())
        {
            if (link is WikiLinkInline wl) ResolveWikiLink(wl, ctx.ProjectKey, dir, pages);
            else if (!link.IsAutoLink) link.Url = RewriteUrl(link.Url, link.IsImage, ctx.ProjectKey, dir);
        }
        LinkIssueKeys(doc, ctx.ProjectKey);
        Display.NeutralizeUnsafeUrls(doc); // also <javascript:…> autolinks, which aren't LinkInlines

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(doc);
        writer.Flush();
        return writer.ToString();
    }

    /// <summary>The page a <c>[[target]]</c> names, or null when no such page exists.</summary>
    public static string? ResolveTarget(string target, string dir, IReadOnlyDictionary<string, string> pages)
    {
        var norm = WikiPaths.Normalize(target);
        if (norm == null) return null;
        if (dir.Length > 0 && pages.TryGetValue(dir + "/" + norm, out var near)) return near;
        if (pages.TryGetValue(norm, out var root)) return root;
        var byTitle = pages.Values.Where(p => string.Equals(p[(p.LastIndexOf('/') + 1)..], norm, StringComparison.OrdinalIgnoreCase)).ToList();
        return byTitle.Count == 1 ? byTitle[0] : null;
    }

    private static void ResolveWikiLink(WikiLinkInline link, string key, string dir, IReadOnlyDictionary<string, string> pages)
    {
        var (target, anchor) = SplitAnchor(link.Target);
        if (ResolveTarget(target, dir, pages) is { } page)
        {
            link.Url = WikiPaths.Url(key, page) + anchor;
            return;
        }
        var norm = WikiPaths.Normalize(target);
        link.Url = norm == null ? "#" : WikiPaths.Url(key, norm, "action=edit");
        link.GetAttributes().AddClass("wiki-missing");
        link.Title = "This page doesn't exist yet";
    }

    private static (string Path, string Anchor) SplitAnchor(string s)
    {
        var i = s.IndexOf('#');
        return i < 0 ? (s, "") : (s[..i], s[i..]);
    }

    private static readonly string[] SafeSchemes = ["http", "https", "mailto"];

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*:")]
    private static partial Regex SchemePattern();

    internal static string? RewriteUrl(string? url, bool isImage, string key, string dir)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        url = url.Trim();
        if (SchemePattern().Match(url) is { Success: true } m)
            return SafeSchemes.Contains(m.Value[..^1], StringComparer.OrdinalIgnoreCase) ? url : "#";
        if (url.StartsWith('#') || url.StartsWith('/') || url.StartsWith('?')) return url;

        var q = url.IndexOfAny(['#', '?']);
        var suffix = q >= 0 ? url[q..] : "";
        var rel = Uri.UnescapeDataString(q >= 0 ? url[..q] : url);
        var baseDir = rel.StartsWith(WikiPaths.UploadsFolder + "/", StringComparison.OrdinalIgnoreCase) ? "" : dir;
        if (WikiPaths.Combine(baseDir, rel) is not { Length: > 0 } target) return "#";

        if (isImage || target.StartsWith(WikiPaths.UploadsFolder + "/", StringComparison.OrdinalIgnoreCase))
            return WikiPaths.FileUrl(key, target);
        return WikiPaths.Normalize(target) is { } page ? WikiPaths.Url(key, page) + suffix : "#";
    }

    private static void LinkIssueKeys(MarkdownDocument doc, string key)
    {
        var rx = IssueKeys.Any(key);
        foreach (var lit in doc.Descendants<LiteralInline>().ToList())
        {
            if (InsideLink(lit)) continue;
            var text = lit.Content.ToString();
            var matches = rx.Matches(text);
            if (matches.Count == 0) continue;
            var pos = 0;
            foreach (Match m in matches)
            {
                if (m.Index > pos) lit.InsertBefore(new LiteralInline(text[pos..m.Index]));
                var issue = new LinkInline($"/p/{key}/issues/{m.Groups[1].Value}", "") { IsClosed = true };
                issue.AppendChild(new LiteralInline(m.Value));
                issue.GetAttributes().AddClass("issue-key-link");
                lit.InsertBefore(issue);
                pos = m.Index + m.Length;
            }
            if (pos < text.Length) lit.InsertBefore(new LiteralInline(text[pos..]));
            lit.Remove();
        }
    }

    private static bool InsideLink(Inline inline)
    {
        for (var p = inline.Parent; p != null; p = p.Parent)
            if (p is LinkInline) return true;
        return false;
    }
}

/// <summary>A parsed <c>[[target|label]]</c>; its <see cref="LinkInline.Url"/> is filled in by <see cref="WikiMarkdown"/>.</summary>
public sealed class WikiLinkInline : LinkInline
{
    public string Target { get; init; } = "";
}

public sealed class WikiLinkExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        if (!pipeline.InlineParsers.Contains<WikiLinkParser>())
            pipeline.InlineParsers.Insert(0, new WikiLinkParser());
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer) { }
}

internal sealed class WikiLinkParser : InlineParser
{
    private const int MaxLength = 300;

    public WikiLinkParser() => OpeningCharacters = ['['];

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        if (slice.PeekChar(1) != '[') return false;
        var text = slice.Text;
        var start = slice.Start + 2;
        var end = -1;
        for (var i = start; i < slice.End && i - start <= MaxLength; i++)
        {
            var c = text[i];
            if (c is '\n' or '\r' or '[') return false;
            if (c == ']' && text[i + 1] == ']') { end = i; break; }
        }
        if (end <= start) return false;
        var inner = text[start..end];
        var bar = inner.IndexOf('|');
        var target = (bar >= 0 ? inner[..bar] : inner).Trim();
        var label = (bar >= 0 ? inner[(bar + 1)..] : inner).Trim();
        if (target.Length == 0) return false;
        if (label.Length == 0) label = target;

        var link = new WikiLinkInline
        {
            Target = target,
            Url = "#",
            IsClosed = true,
            Span = new SourceSpan(processor.GetSourcePosition(slice.Start, out var line, out var column), processor.GetSourcePosition(end + 1)),
            Line = line,
            Column = column,
        };
        link.AppendChild(new LiteralInline(label));
        processor.Inline = link;
        slice.Start = end + 2;
        return true;
    }
}
