using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ByteBench.Services.TrackerImport;

/// <summary>
/// Converts Jira text to markdown: Atlassian Document Format (Jira Cloud's REST v3 returns JSON objects) and wiki markup (Jira
/// Server/Data Center and REST v2 return strings). Never throws: input it can't make sense of comes back as its plain text.
/// </summary>
public static class JiraMarkup
{
    private const int MaxDepth = 40;

    /// <summary>A Jira text field of either kind: an ADF object, a wiki string, or null/missing (empty).</summary>
    public static string FromField(JsonElement value)
    {
        try
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => FromWiki(value.GetString()),
                JsonValueKind.Object => FromAdf(value),
                _ => "",
            };
        }
        catch (Exception) { return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : ""; }
    }

    // ---------------------------------------------------------------- ADF

    public static string FromAdf(JsonElement doc)
    {
        try { return Blocks(doc, 0).Trim(); }
        catch (Exception) { return PlainText(doc).Trim(); }
    }

    /// <summary>Parses ADF given as JSON text; text that isn't JSON is returned as it is.</summary>
    public static string FromAdfJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try { using var d = JsonDocument.Parse(json); return FromField(d.RootElement); }
        catch (Exception) { return json; }
    }

    private static IEnumerable<JsonElement> Children(JsonElement n) =>
        n.ValueKind == JsonValueKind.Object && n.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array ? c.EnumerateArray() : [];

    private static string? Str(JsonElement n, string name) =>
        n.ValueKind == JsonValueKind.Object && n.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Attr(JsonElement n, string name) =>
        n.ValueKind == JsonValueKind.Object && n.TryGetProperty("attrs", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;

    private static string Blocks(JsonElement node, int depth)
    {
        if (depth > MaxDepth) return PlainText(node);
        var parts = new List<string>();
        foreach (var child in Children(node))
        {
            var s = Block(child, depth + 1);
            if (s.Length > 0) parts.Add(s);
        }
        return string.Join("\n\n", parts);
    }

    private static string Block(JsonElement n, int depth)
    {
        if (n.ValueKind == JsonValueKind.String) return n.GetString() ?? "";
        if (n.ValueKind != JsonValueKind.Object) return "";
        if (depth > MaxDepth) return PlainText(n);
        var type = Str(n, "type");
        switch (type)
        {
            case "doc": return Blocks(n, depth);
            case "paragraph": return Inline(n, depth).Trim();
            case "heading":
            {
                var level = int.TryParse(Attr(n, "level"), out var l) ? Math.Clamp(l, 1, 6) : 1;
                return new string('#', level) + " " + Inline(n, depth).Trim();
            }
            case "bulletList": return List(n, depth, ordered: false);
            case "orderedList": return List(n, depth, ordered: true);
            case "taskList": return List(n, depth, ordered: false);
            case "listItem": case "taskItem": return ItemBody(n, depth);
            case "codeBlock":
                return "```" + (Attr(n, "language") ?? "") + "\n" + PlainText(n).TrimEnd('\n') + "\n```";
            case "blockquote": return Quote(Blocks(n, depth));
            case "panel":
            {
                var kind = Attr(n, "panelType") ?? "info";
                return Quote("**" + char.ToUpperInvariant(kind[0]) + kind[1..] + "**\n\n" + Blocks(n, depth));
            }
            case "rule": return "---";
            case "table": return Table(n, depth);
            case "mediaSingle": case "mediaGroup":
                return string.Join("\n", Children(n).Select(c => Block(c, depth + 1)).Where(s => s.Length > 0));
            case "media": return Media(n);
            case "blockCard": case "embedCard": return Card(n);
            case "expand": case "nestedExpand":
            {
                var title = Attr(n, "title");
                var body = Blocks(n, depth);
                return string.IsNullOrWhiteSpace(title) ? body : "**" + title + "**\n\n" + body;
            }
            case "decisionList": return List(n, depth, ordered: false);
            case "decisionItem": return Inline(n, depth).Trim();
            default:
                // Unknown block: its blocks if it has block children, else its inline text.
                if (Children(n).Any(c => Str(c, "type") is "paragraph" or "heading" or "bulletList" or "orderedList" or "codeBlock" or "table" or "blockquote"))
                    return Blocks(n, depth);
                return Inline(n, depth).Trim();
        }
    }

    private static string List(JsonElement n, int depth, bool ordered)
    {
        var sb = new StringBuilder();
        var i = int.TryParse(Attr(n, "order"), out var start) ? start : 1;
        foreach (var item in Children(n))
        {
            var marker = ordered ? $"{i++}. " : "- ";
            var body = ItemBody(item, depth + 1).Split('\n');
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(marker).Append(body[0]);
            for (var k = 1; k < body.Length; k++) sb.Append('\n').Append(body[k].Length == 0 ? "" : new string(' ', marker.Length) + body[k]);
        }
        return sb.ToString();
    }

    private static string ItemBody(JsonElement item, int depth)
    {
        var parts = new List<string>();
        foreach (var c in Children(item))
        {
            var s = Block(c, depth + 1);
            if (s.Length > 0) parts.Add(s);
        }
        var text = string.Join("\n", parts);
        if (Str(item, "type") == "taskItem") text = (Attr(item, "state") == "DONE" ? "[x] " : "[ ] ") + text;
        return text;
    }

    private static string Quote(string text) => string.Join("\n", text.Split('\n').Select(l => l.Length == 0 ? ">" : "> " + l));

    private static string Table(JsonElement n, int depth)
    {
        var rows = new List<List<string>>();
        var headerRow = false;
        foreach (var row in Children(n))
        {
            var cells = new List<string>();
            foreach (var cell in Children(row))
            {
                if (rows.Count == 0 && Str(cell, "type") == "tableHeader") headerRow = true;
                var text = Blocks(cell, depth + 1).Replace("\r", "").Replace("\n", " ").Replace("|", "\\|").Trim();
                cells.Add(text);
            }
            if (cells.Count > 0) rows.Add(cells);
        }
        if (rows.Count == 0) return "";
        var width = rows.Max(r => r.Count);
        foreach (var r in rows) while (r.Count < width) r.Add("");
        var sb = new StringBuilder();
        List<string> head = headerRow ? rows[0] : Enumerable.Repeat("", width).ToList();
        sb.Append("| ").Append(string.Join(" | ", head)).Append(" |\n");
        sb.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", width))).Append(" |");
        foreach (var r in rows.Skip(headerRow ? 1 : 0)) sb.Append("\n| ").Append(string.Join(" | ", r)).Append(" |");
        return sb.ToString();
    }

    private static string Media(JsonElement n)
    {
        var name = Attr(n, "alt") ?? Attr(n, "name") ?? Attr(n, "id") ?? "file";
        if (Attr(n, "type") == "external" && Attr(n, "url") is { Length: > 0 } url) return $"[attachment: {name}]({url})";
        return $"[attachment: {name}]";
    }

    private static string Card(JsonElement n)
    {
        var url = Attr(n, "url");
        if (string.IsNullOrEmpty(url) && n.ValueKind == JsonValueKind.Object && n.TryGetProperty("attrs", out var a) && a.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
            url = Str(d, "url");
        return string.IsNullOrEmpty(url) ? "" : $"[{url}]({url})";
    }

    private static string Inline(JsonElement parent, int depth)
    {
        var sb = new StringBuilder();
        foreach (var n in Children(parent)) sb.Append(InlineNode(n, depth + 1));
        return sb.ToString();
    }

    private static string InlineNode(JsonElement n, int depth)
    {
        if (n.ValueKind != JsonValueKind.Object) return "";
        if (depth > MaxDepth) return PlainText(n);
        switch (Str(n, "type"))
        {
            case "text": return Marked(Str(n, "text") ?? "", n);
            case "hardBreak": return "  \n";
            case "mention":
            {
                var t = (Attr(n, "text") ?? Attr(n, "id") ?? "someone").TrimStart('@');
                return "@" + t;
            }
            case "emoji": return Attr(n, "text") ?? Attr(n, "shortName") ?? "";
            case "inlineCard": case "blockCard": return Card(n);
            case "media": return Media(n);
            case "mediaInline": return Media(n);
            case "status": return Attr(n, "text") is { Length: > 0 } st ? $"`{st}`" : "";
            case "date":
                return long.TryParse(Attr(n, "timestamp"), out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd") : "";
            case "placeholder": return Attr(n, "text") ?? "";
            default:
                return Children(n).Any() ? Inline(n, depth) : Str(n, "text") ?? "";
        }
    }

    private static string Marked(string text, JsonElement node)
    {
        if (text.Length == 0) return "";
        if (!node.TryGetProperty("marks", out var marks) || marks.ValueKind != JsonValueKind.Array) return text;
        // Emphasis markers must hug the text: keep surrounding spaces outside them.
        var lead = text[..(text.Length - text.TrimStart().Length)];
        var trail = text[text.TrimEnd().Length..];
        var core = text.Trim();
        if (core.Length == 0) return text;
        string? link = null;
        foreach (var m in marks.EnumerateArray())
        {
            switch (Str(m, "type"))
            {
                case "code": core = "`" + core.Replace("`", "'") + "`"; break;
                case "strong": core = "**" + core + "**"; break;
                case "em": core = "*" + core + "*"; break;
                case "strike": core = "~~" + core + "~~"; break;
                case "link": link = Attr(m, "href"); break;
            }
        }
        if (!string.IsNullOrEmpty(link)) core = $"[{core}]({link})";
        return lead + core + trail;
    }

    private static string PlainText(JsonElement n)
    {
        var sb = new StringBuilder();
        AppendText(n, sb, 0);
        return sb.ToString();
    }

    private static void AppendText(JsonElement n, StringBuilder sb, int depth)
    {
        if (depth > 200) return;
        if (n.ValueKind == JsonValueKind.String) { sb.Append(n.GetString()); return; }
        if (n.ValueKind != JsonValueKind.Object) return;
        if (Str(n, "text") is { } t) { sb.Append(t); return; }
        var type = Str(n, "type");
        if (type == "hardBreak") { sb.Append('\n'); return; }
        if (type == "mention") { sb.Append(Attr(n, "text") ?? ""); return; }
        var any = false;
        foreach (var c in Children(n))
        {
            if (any && Str(c, "type") is "paragraph" or "heading" or "listItem" or "tableRow") sb.Append('\n');
            AppendText(c, sb, depth + 1);
            any = true;
        }
    }

    // ---------------------------------------------------------------- wiki markup

    private static readonly Regex CodeBlock = new(@"\{code(?::([^}\r\n]*))?\}(.*?)\{code\}", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex NoFormat = new(@"\{noformat(?::[^}\r\n]*)?\}(.*?)\{noformat\}", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex QuoteBlock = new(@"\{quote\}(.*?)\{quote\}", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex PanelBlock = new(@"\{panel(?::([^}\r\n]*))?\}(.*?)\{panel\}", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^h([1-6])\.\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex ListLine = new(@"^\s*([*#\-]+)\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Mono = new(@"\{\{(.+?)\}\}", RegexOptions.Compiled);
    private static readonly Regex Image = new(@"!([^!\s|\r\n][^!|\r\n]*?\.[A-Za-z0-9]{2,5})(?:\|[^!\r\n]*)?!", RegexOptions.Compiled);
    private static readonly Regex UserMention = new(@"\[~(?:accountid:)?([^\]\r\n]+)\]", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\[([^\]|\r\n]*)\|([^\]|\r\n]+)(?:\|[^\]\r\n]*)?\]", RegexOptions.Compiled);
    private static readonly Regex BareLink = new(@"\[((?:https?|ftp|mailto):[^\]\s]+)\]", RegexOptions.Compiled);
    private static readonly Regex Bold = new(@"(?<![\w*])\*(?!\s)([^*\r\n]+?)(?<!\s)\*(?![\w*])", RegexOptions.Compiled);
    private static readonly Regex Italic = new(@"(?<!\w)_(?!\s)([^_\r\n]+?)(?<!\s)_(?!\w)", RegexOptions.Compiled);
    private static readonly Regex Strike = new(@"(?<![\w\-])-(?!\s)([^\-\r\n]+?)(?<!\s)-(?![\w\-])", RegexOptions.Compiled);
    private static readonly Regex Color = new(@"\{color(?::[^}\r\n]*)?\}", RegexOptions.Compiled);

    public static string FromWiki(string? wiki)
    {
        if (string.IsNullOrWhiteSpace(wiki)) return "";
        try { return WikiBlocks(wiki.Replace("\r\n", "\n").Replace('\r', '\n'), 0).Trim(); }
        catch (Exception) { return wiki; }
    }

    private static string WikiBlocks(string text, int depth)
    {
        var stash = new List<string>();
        string Hold(string s) { stash.Add(s); return $"\u0001{stash.Count - 1}\u0002"; }

        // Multi-line constructs first, so their content isn't touched by the line and inline rules.
        text = CodeBlock.Replace(text, m =>
        {
            var lang = m.Groups[1].Success ? CodeLanguage(m.Groups[1].Value) : "";
            return "\n" + Hold("```" + lang + "\n" + m.Groups[2].Value.Trim('\n') + "\n```") + "\n";
        });
        text = NoFormat.Replace(text, m => "\n" + Hold("```\n" + m.Groups[1].Value.Trim('\n') + "\n```") + "\n");
        if (depth < 4)
        {
            text = PanelBlock.Replace(text, m =>
            {
                var title = m.Groups[1].Success ? Regex.Match(m.Groups[1].Value, @"title=([^|]*)") : null;
                var head = title is { Success: true } ? "**" + title.Groups[1].Value.Trim() + "**\n\n" : "";
                return "\n" + Hold(Quote(head + WikiBlocks(m.Groups[2].Value.Trim('\n'), depth + 1))) + "\n";
            });
            text = QuoteBlock.Replace(text, m => "\n" + Hold(Quote(WikiBlocks(m.Groups[1].Value.Trim('\n'), depth + 1))) + "\n");
        }

        var lines = text.Split('\n');
        var output = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith('|'))
            {
                var table = new List<string>();
                while (i < lines.Length && lines[i].StartsWith('|')) table.Add(lines[i++]);
                i--;
                output.Add(WikiTable(table));
                continue;
            }
            output.Add(WikiLine(line));
        }
        var result = Regex.Replace(string.Join("\n", output), "\n{3,}", "\n\n");
        for (var n = stash.Count - 1; n >= 0; n--) result = result.Replace($"\u0001{n}\u0002", stash[n]);
        return result;
    }

    private static string CodeLanguage(string spec)
    {
        // {code:java} or {code:title=Foo|language=java|borderStyle=solid}
        var m = Regex.Match(spec, @"language=([\w#+.\-]+)");
        if (m.Success) return m.Groups[1].Value;
        return spec.Contains('=') ? "" : Regex.Match(spec, @"^[\w#+.\-]+").Value;
    }

    private static string WikiLine(string line)
    {
        if (line.Trim().Length == 0) return "";
        if (Heading.Match(line) is { Success: true } h) return new string('#', int.Parse(h.Groups[1].Value)) + " " + WikiInline(h.Groups[2].Value);
        if (line.StartsWith("bq. ")) return "> " + WikiInline(line[4..]);
        var trimmed = line.Trim();
        if (trimmed.Length >= 4 && trimmed.All(c => c == '-')) return "---";
        if (ListLine.Match(line) is { Success: true } l)
        {
            var markers = l.Groups[1].Value;
            // A line like "-- not a list" or "**bold**" isn't a list; markers must be one kind of list, optionally nested.
            if (markers.All(c => c is '*' or '#' or '-') && !(markers.Contains('-') && markers.Length > 1))
            {
                var indent = new string(' ', 3 * (markers.Length - 1));
                var bullet = markers[^1] == '#' ? "1. " : "- ";
                return indent + bullet + WikiInline(l.Groups[2].Value);
            }
        }
        return WikiInline(line);
    }

    private static string WikiTable(List<string> rows)
    {
        var cellsByRow = new List<(bool Header, List<string> Cells)>();
        foreach (var raw in rows)
        {
            var header = raw.StartsWith("||");
            var body = raw.TrimEnd();
            var cells = new List<string>();
            if (header)
                cells = body.Trim('|').Split("||").Select(c => WikiInline(c.Replace("|", "\\|").Trim())).ToList();
            else
                cells = body.Trim('|').Split('|').Select(c => WikiInline(c.Trim())).ToList();
            cellsByRow.Add((header, cells));
        }
        var width = cellsByRow.Max(r => r.Cells.Count);
        foreach (var r in cellsByRow) while (r.Cells.Count < width) r.Cells.Add("");
        var sb = new StringBuilder();
        var first = cellsByRow[0].Header;
        sb.Append("| ").Append(string.Join(" | ", first ? cellsByRow[0].Cells : Enumerable.Repeat("", width))).Append(" |\n");
        sb.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", width))).Append(" |");
        foreach (var r in cellsByRow.Skip(first ? 1 : 0)) sb.Append("\n| ").Append(string.Join(" | ", r.Cells)).Append(" |");
        return sb.ToString();
    }

    private static string WikiInline(string s)
    {
        if (s.Length == 0) return s;
        var stash = new List<string>();
        string Hold(string v) { stash.Add(v); return $"\u0003{stash.Count - 1}\u0004"; }

        s = Mono.Replace(s, m => Hold("`" + m.Groups[1].Value.Replace("`", "'") + "`"));
        s = Image.Replace(s, m => Hold($"[image: {m.Groups[1].Value}]"));
        s = UserMention.Replace(s, m => Hold("@" + m.Groups[1].Value));
        s = Link.Replace(s, m =>
        {
            var text = m.Groups[1].Value.Trim();
            var url = m.Groups[2].Value.Trim();
            return Hold(text.Length == 0 ? $"<{url}>" : $"[{text}]({url})");
        });
        s = BareLink.Replace(s, m => Hold($"<{m.Groups[1].Value}>"));
        s = Color.Replace(s, "");
        s = s.Replace("\\\\", "  \n");
        s = Bold.Replace(s, "**$1**");
        s = Italic.Replace(s, "*$1*");
        s = Strike.Replace(s, "~~$1~~");
        for (var n = stash.Count - 1; n >= 0; n--) s = s.Replace($"\u0003{n}\u0004", stash[n]);
        return s;
    }
}
