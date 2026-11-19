using System.Net;
using Microsoft.AspNetCore.Components;

namespace ByteBench.Services.Code;

/// <summary>
/// Picks a highlight.js language for a file and renders code lines so <c>wwwroot/js/code-tools.js</c> can
/// highlight them in the browser. See "Syntax highlighting" in the README.
/// </summary>
public static class CodeLanguages
{
    /// <summary>Files larger than this are shown without highlighting.</summary>
    public const long MaxHighlightBytes = 200 * 1024;

    // highlight.js language ids. Everything here is in the vendored "common" bundle or wwwroot/lib/highlightjs/languages.
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp", [".csx"] = "csharp",
        [".fs"] = "fsharp", [".fsx"] = "fsharp", [".fsi"] = "fsharp",
        [".vb"] = "vbnet",
        [".js"] = "javascript", [".mjs"] = "javascript", [".cjs"] = "javascript", [".jsx"] = "javascript",
        [".ts"] = "typescript", [".tsx"] = "typescript", [".mts"] = "typescript", [".cts"] = "typescript",
        [".json"] = "json", [".jsonc"] = "json", [".csproj"] = "xml", [".fsproj"] = "xml", [".props"] = "xml", [".targets"] = "xml",
        [".sln"] = "plaintext", [".resx"] = "xml", [".config"] = "xml",
        [".html"] = "xml", [".htm"] = "xml", [".xml"] = "xml", [".xaml"] = "xml", [".svg"] = "xml",
        [".razor"] = "xml", [".cshtml"] = "xml", [".vue"] = "xml",
        [".css"] = "css", [".scss"] = "scss", [".less"] = "less",
        [".py"] = "python", [".pyw"] = "python", [".rb"] = "ruby", [".php"] = "php", [".pl"] = "perl", [".pm"] = "perl",
        [".java"] = "java", [".kt"] = "kotlin", [".kts"] = "kotlin", [".swift"] = "swift", [".go"] = "go", [".rs"] = "rust",
        [".c"] = "c", [".h"] = "c", [".cpp"] = "cpp", [".cc"] = "cpp", [".cxx"] = "cpp", [".hpp"] = "cpp", [".hh"] = "cpp",
        [".m"] = "objectivec", [".mm"] = "objectivec", [".lua"] = "lua", [".r"] = "r",
        [".sh"] = "bash", [".bash"] = "bash", [".zsh"] = "bash", [".ps1"] = "powershell", [".psm1"] = "powershell", [".psd1"] = "powershell",
        [".sql"] = "sql", [".graphql"] = "graphql", [".gql"] = "graphql",
        [".yml"] = "yaml", [".yaml"] = "yaml", [".toml"] = "ini", [".ini"] = "ini", [".cfg"] = "ini", [".editorconfig"] = "ini",
        [".md"] = "markdown", [".markdown"] = "markdown", [".diff"] = "diff", [".patch"] = "diff",
        [".mk"] = "makefile", [".wat"] = "wasm",
    };

    private static readonly Dictionary<string, string> ByFileName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dockerfile"] = "dockerfile", ["Containerfile"] = "dockerfile", ["Makefile"] = "makefile", ["GNUmakefile"] = "makefile",
        [".gitignore"] = "bash", [".gitattributes"] = "bash", [".dockerignore"] = "bash", [".env"] = "bash",
        [".editorconfig"] = "ini", ["Gemfile"] = "ruby", ["Rakefile"] = "ruby",
    };

    /// <summary>The highlight.js language for <paramref name="path"/>, or null to leave it plain.</summary>
    public static string? ForPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (ByFileName.TryGetValue(name, out var lang)) return lang;
        if (name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase)) return "dockerfile";
        var dot = name.LastIndexOf('.');
        return dot >= 0 && ByExtension.TryGetValue(name[dot..], out lang) ? lang : null;
    }

    /// <summary>The language to highlight a file of <paramref name="size"/> bytes with, or null if it's too big or unknown.</summary>
    public static string? ForFile(string? path, long size) => size > MaxHighlightBytes ? null : ForPath(path);

    /// <summary>
    /// One line of code as <c>&lt;span class="hl-line"&gt;…&lt;/span&gt;</c> markup. Rendering it as markup (rather than a
    /// text node) lets the highlighter rewrite the span's children safely: Blazor only tracks the span itself and
    /// replaces it wholesale when the line changes, after which the highlighter runs again.
    /// </summary>
    public static MarkupString Line(string text) =>
        new("<span class=\"hl-line\">" + WebUtility.HtmlEncode(text) + "</span>");
}
