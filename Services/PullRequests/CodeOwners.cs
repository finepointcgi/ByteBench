using System.Text;
using System.Text.RegularExpressions;

namespace GitBench.Services;

/// <summary>One CODEOWNERS line: a path pattern and its owners (as written, e.g. "@alice", "@team/backend", "dev@example.com").</summary>
public sealed record CodeOwnerRule(int LineNumber, string Pattern, IReadOnlyList<string> Owners, Regex Matcher);

/// <summary>
/// A parsed CODEOWNERS file, GitHub syntax: one rule per line, <c>pattern owner owner…</c>; <c>#</c> starts a comment;
/// the <b>last</b> matching rule wins (a matching rule with no owners clears ownership). Patterns follow .gitignore
/// rules: <c>*</c> and <c>?</c> stay within a path segment, <c>**</c> crosses <c>/</c>; a leading <c>/</c> or a
/// <c>/</c> in the middle anchors the pattern to the repository root, otherwise it matches at any depth; a trailing
/// <c>/</c> matches everything under a directory; a pattern also matches everything inside a matching directory,
/// except when its last segment is a single <c>*</c> (so <c>docs/*</c> owns <c>docs/a.md</c> but not <c>docs/x/b.md</c>).
/// </summary>
public sealed class CodeOwnersFile
{
    /// <summary>Where GitBench looks for the file on the target branch, in order.</summary>
    public static readonly string[] Locations = [".gitbench/CODEOWNERS", "CODEOWNERS", "docs/CODEOWNERS"];

    public IReadOnlyList<CodeOwnerRule> Rules { get; }
    /// <summary>Lines that couldn't be parsed ("line 4: …").</summary>
    public IReadOnlyList<string> Errors { get; }

    private CodeOwnersFile(List<CodeOwnerRule> rules, List<string> errors) { Rules = rules; Errors = errors; }

    public static CodeOwnersFile Parse(string? text)
    {
        var rules = new List<CodeOwnerRule>();
        var errors = new List<string>();
        var n = 0;
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            n++;
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            var parts = SplitFields(line);
            var pattern = parts[0];
            if (pattern.StartsWith('!') || pattern.Contains('['))
            {
                errors.Add($"line {n}: '{pattern}' uses syntax CODEOWNERS doesn't support (! or [ ]).");
                continue;
            }
            try
            {
                rules.Add(new CodeOwnerRule(n, pattern, parts.Skip(1).ToList(), ToRegex(pattern)));
            }
            catch (ArgumentException)
            {
                errors.Add($"line {n}: '{pattern}' isn't a valid pattern.");
            }
        }
        return new CodeOwnersFile(rules, errors);
    }

    /// <summary>The rule that decides who owns <paramref name="path"/> (the last match), or null.</summary>
    public CodeOwnerRule? RuleFor(string path)
    {
        path = path.Trim('/');
        for (var i = Rules.Count - 1; i >= 0; i--)
            if (Rules[i].Matcher.IsMatch(path)) return Rules[i];
        return null;
    }

    /// <summary>The owners of <paramref name="path"/> as written in the file (empty when nobody owns it).</summary>
    public IReadOnlyList<string> OwnersOf(string path) => RuleFor(path)?.Owners ?? [];

    private static string StripComment(string line)
    {
        // "#" starts a comment unless escaped ("\#" is a literal hash in a pattern).
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\') { i++; continue; }
            if (line[i] == '#') return line[..i];
        }
        return line;
    }

    private static List<string> SplitFields(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && i + 1 < line.Length) { sb.Append(line[++i]); continue; } // "\ " keeps a space in a pattern
            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) { fields.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) fields.Add(sb.ToString());
        return fields;
    }

    internal static Regex ToRegex(string pattern)
    {
        var p = pattern;
        var dirOnly = p.EndsWith('/');
        p = p.Trim('/');
        if (p.Length == 0 || p == "**") return new Regex("^.*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        var anchored = pattern.StartsWith('/') || p.Contains('/');
        var lastSegment = p[(p.LastIndexOf('/') + 1)..];

        var rx = new StringBuilder(anchored ? "^" : "^(?:.*/)?");
        for (var i = 0; i < p.Length; i++)
        {
            var c = p[i];
            if (c == '*' && i + 1 < p.Length && p[i + 1] == '*')
            {
                var slashAfter = i + 2 < p.Length && p[i + 2] == '/';
                rx.Append(slashAfter ? "(?:.*/)?" : ".*");
                i += slashAfter ? 2 : 1;
            }
            else if (c == '*') rx.Append("[^/]*");
            else if (c == '?') rx.Append("[^/]");
            else rx.Append(Regex.Escape(c.ToString()));
        }
        if (dirOnly) rx.Append("/.*");
        else if (lastSegment != "*") rx.Append("(?:/.*)?");
        rx.Append('$');
        return new Regex(rx.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }
}
