using System.Text.Json;
using ByteBench.Services.TrackerImport;

namespace ByteBench.Tests;

public class JiraMarkupTests
{
    private static string Adf(string json)
    {
        using var d = JsonDocument.Parse(json);
        return JiraMarkup.FromField(d.RootElement);
    }

    private static string Doc(string content) => "{\"version\":1,\"type\":\"doc\",\"content\":[" + content + "]}";
    private static string P(string inline) => "{\"type\":\"paragraph\",\"content\":[" + inline + "]}";
    private static string T(string text, string? marks = null) =>
        "{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(text) + (marks == null ? "" : ",\"marks\":[" + marks + "]") + "}";
    private static string M(string type, string? attrs = null) => "{\"type\":\"" + type + "\"" + (attrs == null ? "" : ",\"attrs\":" + attrs) + "}";
    private static string Li(string inner) => "{\"type\":\"listItem\",\"content\":[" + inner + "]}";

    public static TheoryData<string, string, string> AdfCases => new()
    {
        { "paragraph", Doc(P(T("Hello world"))), "Hello world" },
        { "two paragraphs", Doc(P(T("One")) + "," + P(T("Two"))), "One\n\nTwo" },
        { "heading", Doc("{\"type\":\"heading\",\"attrs\":{\"level\":3},\"content\":[" + T("Title") + "]}"), "### Title" },
        { "strong em code strike", Doc(P(T("b", M("strong")) + "," + T(" ") + "," + T("i", M("em")) + "," + T(" ") + "," + T("c", M("code")) + "," + T(" ") + "," + T("s", M("strike")))), "**b** *i* `c` ~~s~~" },
        { "mark keeps spaces outside", Doc(P(T("bold ", M("strong")) + "," + T("after"))), "**bold** after" },
        { "link", Doc(P(T("site", M("link", "{\"href\":\"https://example.com\"}")))), "[site](https://example.com)" },
        { "bold link", Doc(P(T("site", M("strong") + "," + M("link", "{\"href\":\"https://e.com\"}")))), "[**site**](https://e.com)" },
        { "bullet list", Doc("{\"type\":\"bulletList\",\"content\":[" + Li(P(T("a"))) + "," + Li(P(T("b"))) + "]}"), "- a\n- b" },
        { "ordered list", Doc("{\"type\":\"orderedList\",\"content\":[" + Li(P(T("a"))) + "," + Li(P(T("b"))) + "]}"), "1. a\n2. b" },
        {
            "nested list",
            Doc("{\"type\":\"bulletList\",\"content\":[" + Li(P(T("a")) + ",{\"type\":\"bulletList\",\"content\":[" + Li(P(T("a1"))) + "]}") + "," + Li(P(T("b"))) + "]}"),
            "- a\n  - a1\n- b"
        },
        { "code block with language", Doc("{\"type\":\"codeBlock\",\"attrs\":{\"language\":\"csharp\"},\"content\":[" + T("var x = 1;") + "]}"), "```csharp\nvar x = 1;\n```" },
        { "code block without language", Doc("{\"type\":\"codeBlock\",\"content\":[" + T("plain") + "]}"), "```\nplain\n```" },
        { "blockquote", Doc("{\"type\":\"blockquote\",\"content\":[" + P(T("quoted")) + "]}"), "> quoted" },
        { "panel", Doc("{\"type\":\"panel\",\"attrs\":{\"panelType\":\"warning\"},\"content\":[" + P(T("careful")) + "]}"), "> **Warning**\n>\n> careful" },
        { "rule", Doc(P(T("a")) + ",{\"type\":\"rule\"}," + P(T("b"))), "a\n\n---\n\nb" },
        {
            "table",
            Doc("{\"type\":\"table\",\"content\":[" +
                "{\"type\":\"tableRow\",\"content\":[{\"type\":\"tableHeader\",\"content\":[" + P(T("H1")) + "]},{\"type\":\"tableHeader\",\"content\":[" + P(T("H2")) + "]}]}," +
                "{\"type\":\"tableRow\",\"content\":[{\"type\":\"tableCell\",\"content\":[" + P(T("a")) + "]},{\"type\":\"tableCell\",\"content\":[" + P(T("b|c")) + "]}]}]}"),
            "| H1 | H2 |\n| --- | --- |\n| a | b\\|c |"
        },
        {
            "table without header row",
            Doc("{\"type\":\"table\",\"content\":[{\"type\":\"tableRow\",\"content\":[{\"type\":\"tableCell\",\"content\":[" + P(T("x")) + "]}]}]}"),
            "|  |\n| --- |\n| x |"
        },
        { "mention", Doc(P(T("hi ") + "," + M("mention", "{\"id\":\"1\",\"text\":\"@Ann Lee\"}"))), "hi @Ann Lee" },
        { "emoji", Doc(P(M("emoji", "{\"shortName\":\":smile:\",\"text\":\"😄\"}"))), "😄" },
        { "emoji short name only", Doc(P(M("emoji", "{\"shortName\":\":smile:\"}"))), ":smile:" },
        { "inline card", Doc(P(M("inlineCard", "{\"url\":\"https://x.example/a\"}"))), "[https://x.example/a](https://x.example/a)" },
        { "block card", Doc(M("blockCard", "{\"url\":\"https://x.example/b\"}")), "[https://x.example/b](https://x.example/b)" },
        { "media", Doc("{\"type\":\"mediaSingle\",\"content\":[" + M("media", "{\"id\":\"abc\",\"type\":\"file\",\"alt\":\"shot.png\"}") + "]}"), "[attachment: shot.png]" },
        { "media group", Doc("{\"type\":\"mediaGroup\",\"content\":[" + M("media", "{\"id\":\"a1\",\"type\":\"file\"}") + "]}"), "[attachment: a1]" },
        { "hard break", Doc(P(T("a") + ",{\"type\":\"hardBreak\"}," + T("b"))), "a  \nb" },
        { "unknown inline node uses its text", Doc(P("{\"type\":\"fancy\",\"content\":[" + T("inner") + "]}")), "inner" },
        { "unknown block node uses its text", Doc("{\"type\":\"weird\",\"content\":[" + T("text") + "]}"), "text" },
        { "empty document", Doc(""), "" },
    };

    [Theory]
    [MemberData(nameof(AdfCases))]
    public void Atlassian_document_format_becomes_markdown(string name, string json, string expected)
    {
        Assert.True(name.Length > 0);
        Assert.Equal(expected, Adf(json));
    }

    public static TheoryData<string, string> WikiCases => new()
    {
        { "h1. Title", "# Title" },
        { "h4. Deep", "#### Deep" },
        { "a *bold* word", "a **bold** word" },
        { "an _italic_ word", "an *italic* word" },
        { "a -struck- word", "a ~~struck~~ word" },
        { "use {{mono}} here", "use `mono` here" },
        { "{{a_b *c*}} stays", "`a_b *c*` stays" },
        { "snake_case_name is plain", "snake_case_name is plain" },
        { "2 * 3 * 4", "2 * 3 * 4" },
        { "well - this - works", "well - this - works" },
        { "{code}\nx = 1\n{code}", "```\nx = 1\n```" },
        { "{code:java}\nint x;\n{code}", "```java\nint x;\n```" },
        { "{code:title=Foo.cs|language=csharp|borderStyle=solid}\nvar a;\n{code}", "```csharp\nvar a;\n```" },
        { "{noformat}\n*not bold*\n{noformat}", "```\n*not bold*\n```" },
        { "{quote}\nsaid so\n{quote}", "> said so" },
        { "bq. quoted line", "> quoted line" },
        { "{panel:title=Note}\nbody\n{panel}", "> **Note**\n>\n> body" },
        { "[docs|https://example.com/docs]", "[docs](https://example.com/docs)" },
        { "[https://example.com]", "<https://example.com>" },
        { "[~bob] please look", "@bob please look" },
        { "* one\n* two", "- one\n- two" },
        { "# one\n# two", "1. one\n1. two" },
        { "* one\n** nested\n* two", "- one\n   - nested\n- two" },
        { "# one\n## sub\n# two", "1. one\n   1. sub\n1. two" },
        { "- dash item", "- dash item" },
        { "||A||B||\n|1|2|", "| A | B |\n| --- | --- |\n| 1 | 2 |" },
        { "|1|2|\n|3|4|", "|  |  |\n| --- | --- |\n| 1 | 2 |\n| 3 | 4 |" },
        { "{color:red}warm{color} words", "warm words" },
        { "see !shot.png! here", "see [image: shot.png] here" },
        { "!thumb.png|thumbnail!", "[image: thumb.png]" },
        { "Hello! World! Wow!", "Hello! World! Wow!" },
        { "----", "---" },
        { "line one\nline two", "line one\nline two" },
        { "", "" },
    };

    [Theory]
    [MemberData(nameof(WikiCases))]
    public void Wiki_markup_becomes_markdown(string wiki, string expected) => Assert.Equal(expected, JiraMarkup.FromWiki(wiki));

    [Fact]
    public void A_field_is_either_wiki_text_or_a_document_or_empty()
    {
        using var s = JsonDocument.Parse("\"h2. Hi\"");
        using var o = JsonDocument.Parse(Doc(P(T("Doc text"))));
        using var n = JsonDocument.Parse("null");
        using var num = JsonDocument.Parse("42");
        Assert.Equal("## Hi", JiraMarkup.FromField(s.RootElement));
        Assert.Equal("Doc text", JiraMarkup.FromField(o.RootElement));
        Assert.Equal("", JiraMarkup.FromField(n.RootElement));
        Assert.Equal("", JiraMarkup.FromField(num.RootElement));
        Assert.Equal("", JiraMarkup.FromField(default));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    [InlineData("{\"type\":\"doc\"}")]
    [InlineData("{\"type\":\"doc\",\"content\":\"nope\"}")]
    [InlineData("{\"type\":\"doc\",\"content\":[1,null,\"x\",[],{}]}")]
    [InlineData("{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\"}]}]}")]
    [InlineData("{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":5,\"marks\":\"x\"}]}]}")]
    [InlineData("{\"type\":\"doc\",\"content\":[{\"type\":\"heading\",\"attrs\":{\"level\":\"abc\"},\"content\":[]}]}")]
    [InlineData("{\"type\":\"doc\",\"content\":[{\"type\":\"table\",\"content\":[{\"type\":\"tableRow\",\"content\":[null]}]}]}")]
    [InlineData("{\"type\":\"doc\",\"content\":[{\"type\":\"orderedList\",\"attrs\":{\"order\":\"x\"},\"content\":[7,{\"type\":\"listItem\"}]}]}")]
    [InlineData("{\"type\":\"doc\",\"content\":[{\"type\":\"mention\",\"attrs\":null},{\"type\":\"emoji\"},{\"type\":\"media\",\"attrs\":[]},{\"type\":\"date\",\"attrs\":{\"timestamp\":\"zzz\"}}]}")]
    public void Malformed_adf_never_throws(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { Assert.NotNull(JiraMarkup.FromAdfJson(json)); return; }
        using (doc) Assert.NotNull(JiraMarkup.FromField(doc.RootElement));
    }

    [Fact]
    public void Absurdly_deep_adf_never_throws()
    {
        var json = string.Concat(Enumerable.Repeat("{\"type\":\"blockquote\",\"content\":[", 300)) + P(T("deep")) + string.Concat(Enumerable.Repeat("]}", 300));
        var opts = new JsonDocumentOptions { MaxDepth = 2000 };
        using var doc = JsonDocument.Parse(Doc(json), opts);
        Assert.NotNull(JiraMarkup.FromField(doc.RootElement));
    }

    [Theory]
    [InlineData("{code} never closed")]
    [InlineData("{code:java}")]
    [InlineData("{quote} open")]
    [InlineData("{{unclosed mono")]
    [InlineData("[broken|")]
    [InlineData("[|]")]
    [InlineData("|||||")]
    [InlineData("||")]
    [InlineData("|")]
    [InlineData("* ")]
    [InlineData("h1.")]
    [InlineData("h7. not a heading")]
    [InlineData("!!")]
    [InlineData("*")]
    [InlineData("_ _ _")]
    [InlineData("\u0001 \u0002 \u0003 0 \u0004 \u00010\u0002")]
    [InlineData("{panel:title=}{panel}")]
    [InlineData("[~]")]
    public void Malformed_wiki_never_throws(string wiki) => Assert.NotNull(JiraMarkup.FromWiki(wiki));

    [Fact]
    public void Text_with_the_internal_placeholder_characters_comes_back_intact()
    {
        var text = "plain \u0001 and \u0003 text";
        Assert.Equal(text, JiraMarkup.FromWiki(text));
    }

    [Fact]
    public void Wiki_blocks_mix_with_text_around_them()
    {
        var wiki = "h2. Steps\n# open it\n# click {{Save}}\n\n{code:sql}\nselect 1;\n{code}\n\nThen *check* the [log|https://example.com/log].";
        Assert.Equal("## Steps\n1. open it\n1. click `Save`\n\n```sql\nselect 1;\n```\n\nThen **check** the [log](https://example.com/log).", JiraMarkup.FromWiki(wiki));
    }
}
