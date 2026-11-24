using System.Net;
using System.Text.RegularExpressions;
using ByteBench.Services;
using ByteBench.Services.Wiki;

namespace ByteBench.Tests;

/// <summary>
/// Stored XSS through Markdown link destinations: raw HTML is escaped, but a link or image URL with a script scheme
/// (<c>javascript:</c>, <c>vbscript:</c>, <c>data:</c>) would still run on click wherever the CSP is off or absent
/// (Security:ContentSecurityPolicy=false or report-only, and notification emails, which carry this HTML).
/// </summary>
public class MarkdownLinkSafetyTests
{
    public static readonly TheoryData<string> Payloads = new()
    {
        "[click](javascript:alert(1))",
        "[click](JavaScript:alert(1))",
        "[click](javascript&#58;alert(1))",
        "[click](<java\tscript:alert(1)>)",
        "[click](< javascript:alert(1)>)",
        "[click][ref]\n\n[ref]: javascript:alert(1)",
        "<javascript:alert(1)>",
        "![img](javascript:alert(1))",
        "[click](vbscript:msgbox(1))",
        "[click](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==)",
    };

    private static readonly Regex UrlAttribute = new("(?:href|src)=\"([^\"]*)\"", RegexOptions.IgnoreCase);

    /// <summary>The scheme a browser would see: entities decoded, then ASCII whitespace and controls removed (WHATWG URL parsing).</summary>
    private static void AssertNoScriptUrl(string html)
    {
        foreach (Match m in UrlAttribute.Matches(html))
        {
            var url = new string(WebUtility.HtmlDecode(m.Groups[1].Value).Where(c => c > ' ').ToArray());
            var scheme = Regex.Match(url, "^([A-Za-z][A-Za-z0-9+.-]*):");
            Assert.True(!scheme.Success || scheme.Groups[1].Value.ToLowerInvariant() is "http" or "https" or "mailto",
                $"unsafe URL {m.Groups[1].Value} in {html}");
        }
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public void Issue_and_comment_markdown_has_no_script_urls(string markdown) =>
        AssertNoScriptUrl(Display.Markdown(markdown).Value);

    [Theory]
    [MemberData(nameof(Payloads))]
    public void Wiki_markdown_has_no_script_urls(string markdown) =>
        AssertNoScriptUrl(WikiMarkdown.RenderHtml(markdown, new WikiRenderContext("WEB", "home", ["home"])));

    [Fact]
    public void Safe_links_are_kept()
    {
        var html = Display.Markdown("[a](https://example.com/x?y=1) [b](/p/WEB/issues/1) [c](mailto:a@example.com) [d](#top) <https://example.com/auto> www.example.org").Value;
        Assert.Contains("href=\"https://example.com/x?y=1\"", html);
        Assert.Contains("href=\"/p/WEB/issues/1\"", html);
        Assert.Contains("href=\"mailto:a@example.com\"", html);
        Assert.Contains("href=\"#top\"", html);
        Assert.Contains("href=\"https://example.com/auto\"", html);
        Assert.Contains("href=\"http://www.example.org\"", html);
    }
}
