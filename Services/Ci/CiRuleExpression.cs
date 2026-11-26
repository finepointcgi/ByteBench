using System.Text;
using System.Text.RegularExpressions;

namespace ByteBench.Services.Ci;

/// <summary>
/// The condition of a <c>rules: - if:</c> entry. A small subset of GitLab's rules syntax: <c>$NAME</c> (true when the variable
/// is set and not empty), string literals (<c>"x"</c> or <c>'x'</c>), <c>null</c>, comparisons <c>==</c> and <c>!=</c>,
/// regular-expression matches <c>=~</c> and <c>!~</c> against a <c>/pattern/</c> (flag <c>i</c> allowed), <c>&amp;&amp;</c>,
/// <c>||</c>, <c>!</c> and parentheses. An unset variable compares as <c>null</c>. Parsed once when the file is read, so a
/// typo is a pipeline file error; evaluated with the pipeline's predefined variables (<c>CI_COMMIT_TAG</c>,
/// <c>CI_COMMIT_BRANCH</c>, <c>CI_COMMIT_REF_NAME</c>, <c>CI_PIPELINE_SOURCE</c>) and its schedule or manual-run variables.
/// </summary>
public sealed class CiRuleExpression
{
    public const int MaxLength = 500;

    private readonly Func<IReadOnlyDictionary<string, string>, bool> eval;
    public string Source { get; }

    private CiRuleExpression(string source, Func<IReadOnlyDictionary<string, string>, bool> eval) { Source = source; this.eval = eval; }

    /// <summary>True if the expression refers to <paramref name="variable"/>.</summary>
    public bool Mentions(string variable) => Source.Contains("$" + variable, StringComparison.Ordinal);

    public bool Evaluate(IReadOnlyDictionary<string, string> variables) => eval(variables);

    /// <summary>Parses <paramref name="text"/>; <c>Error</c> says what is wrong with it.</summary>
    public static (CiRuleExpression? Expression, string? Error) Parse(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return (null, "is empty");
        if (text.Length > MaxLength) return (null, $"is longer than {MaxLength} characters");
        try
        {
            var p = new Parser(text);
            var node = p.ParseOr();
            if (!p.AtEnd) throw new FormatException($"unexpected '{p.Rest}'");
            return (new CiRuleExpression(text, node), null);
        }
        catch (FormatException ex) { return (null, ex.Message); }
    }

    private delegate string? Value(IReadOnlyDictionary<string, string> v);

    private sealed class Parser(string s)
    {
        private int pos;
        public bool AtEnd { get { Skip(); return pos >= s.Length; } }
        public string Rest => s[pos..];

        private void Skip() { while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++; }
        private bool Eat(string token)
        {
            Skip();
            if (string.CompareOrdinal(s, pos, token, 0, token.Length) != 0) return false;
            pos += token.Length;
            return true;
        }

        public Func<IReadOnlyDictionary<string, string>, bool> ParseOr()
        {
            var left = ParseAnd();
            while (Eat("||")) { var l = left; var r = ParseAnd(); left = v => l(v) || r(v); }
            return left;
        }

        private Func<IReadOnlyDictionary<string, string>, bool> ParseAnd()
        {
            var left = ParseUnary();
            while (Eat("&&")) { var l = left; var r = ParseUnary(); left = v => l(v) && r(v); }
            return left;
        }

        private Func<IReadOnlyDictionary<string, string>, bool> ParseUnary()
        {
            Skip();
            // "!" must not swallow the "!=" / "!~" operators, which only follow an operand.
            if (pos < s.Length && s[pos] == '!') { pos++; var inner = ParseUnary(); return v => !inner(v); }
            if (Eat("("))
            {
                var inner = ParseOr();
                if (!Eat(")")) throw new FormatException("missing ')'");
                return inner;
            }
            var left = ParseOperand();
            if (Eat("=="))
            {
                var right = ParseOperand();
                return v => Equals(left.Value(v), right.Value(v));
            }
            if (Eat("!="))
            {
                var right = ParseOperand();
                return v => !Equals(left.Value(v), right.Value(v));
            }
            var negate = false;
            if (Eat("=~") || (negate = Eat("!~")))
            {
                var right = ParseOperand();
                if (right.Regex == null) throw new FormatException("'=~' and '!~' need a /regular expression/ on the right");
                var rx = right.Regex;
                return v => (left.Value(v) is { } text && rx.IsMatch(text)) != negate;
            }
            // A bare operand: a variable is true when set and not empty, a literal when not empty.
            return v => !string.IsNullOrEmpty(left.Value(v));
        }

        private static bool Equals(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);

        private (Value Value, Regex? Regex) ParseOperand()
        {
            Skip();
            if (pos >= s.Length) throw new FormatException("unexpected end of the condition");
            var c = s[pos];
            if (c == '$')
            {
                var start = ++pos;
                while (pos < s.Length && (char.IsAsciiLetterOrDigit(s[pos]) || s[pos] == '_')) pos++;
                if (pos == start) throw new FormatException("'$' needs a variable name");
                var name = s[start..pos];
                return (v => v.TryGetValue(name, out var x) ? x : null, null);
            }
            if (c is '"' or '\'')
            {
                var sb = new StringBuilder();
                pos++;
                while (true)
                {
                    if (pos >= s.Length) throw new FormatException("a string is not closed");
                    if (s[pos] == c) { pos++; break; }
                    if (s[pos] == '\\' && pos + 1 < s.Length) pos++;
                    sb.Append(s[pos++]);
                }
                var text = sb.ToString();
                return (_ => text, null);
            }
            if (c == '/')
            {
                var sb = new StringBuilder();
                pos++;
                while (true)
                {
                    if (pos >= s.Length) throw new FormatException("a /regular expression/ is not closed");
                    if (s[pos] == '/') { pos++; break; }
                    if (s[pos] == '\\' && pos + 1 < s.Length) { sb.Append(s[pos++]); }
                    sb.Append(s[pos++]);
                }
                var options = RegexOptions.CultureInvariant;
                if (pos < s.Length && s[pos] == 'i') { options |= RegexOptions.IgnoreCase; pos++; }
                Regex rx;
                try { rx = new Regex(sb.ToString(), options, TimeSpan.FromMilliseconds(100)); }
                catch (ArgumentException ex) { throw new FormatException("the regular expression is invalid: " + ex.Message); }
                return (_ => null, rx);
            }
            if (s.AsSpan(pos).StartsWith("null", StringComparison.Ordinal) && (pos + 4 >= s.Length || !char.IsAsciiLetterOrDigit(s[pos + 4])))
            {
                pos += 4;
                return (_ => null, null);
            }
            throw new FormatException($"unexpected '{s[pos..Math.Min(s.Length, pos + 12)]}'");
        }
    }
}
