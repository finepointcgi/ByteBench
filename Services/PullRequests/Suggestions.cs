using System.Text.RegularExpressions;

namespace ByteBench.Services;

/// <summary>A suggested change parsed from a comment: the replacement lines, and the comment text around the block.</summary>
/// <param name="Lines">Replacement for the commented line(s); empty deletes them.</param>
/// <param name="Before">Markdown before the ```suggestion block (may be empty).</param>
/// <param name="After">Markdown after the block (may be empty).</param>
public sealed record Suggestion(IReadOnlyList<string> Lines, string Before, string After);

/// <summary>
/// GitHub-style suggested changes: an inline comment whose body contains a fenced block opened with
/// <c>```suggestion</c> proposes that block's content as the replacement for the commented line(s).
/// Only the first suggestion block of a comment counts.
/// </summary>
public static partial class Suggestions
{
    // The opening fence (3+ backticks or tildes) followed by "suggestion", then the content up to a matching fence.
    [GeneratedRegex(@"^(?<fence>`{3,}|~{3,})[ \t]*suggestion[ \t]*\r?\n(?<body>(?:.*\r?\n)*?)(?:\k<fence>)[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex Block();

    /// <summary>The comment's suggestion, or null if it has none.</summary>
    public static Suggestion? Parse(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var text = body.Replace("\r\n", "\n");
        // Make "```suggestion\n```" (no content lines, i.e. "delete these lines") parse too.
        var m = Block().Match(text.EndsWith('\n') ? text : text + "\n");
        if (!m.Success) return null;
        var content = m.Groups["body"].Value;
        // Every content line ends with \n: drop the last one and split (so "\n" is one blank line, "" is none).
        IReadOnlyList<string> lines = content.Length == 0 ? [] : content[..^1].Split('\n');
        var after = m.Index + m.Length <= text.Length ? text[Math.Min(text.Length, m.Index + m.Length)..] : "";
        return new Suggestion(lines, text[..m.Index].Trim(), after.Trim());
    }

    public static bool Has(string? body) => Parse(body) != null;
}
