using System.Text.Json;

namespace ByteBench.Services.Scim;

/// <summary>One comparison of a SCIM filter: <c>attribute op "value"</c>, or <c>attribute pr</c>.</summary>
public sealed record ScimComparison(string Attribute, string Op, string? Value);

/// <summary>
/// The subset of the RFC 7644 §3.4.2.2 filter grammar identity providers send: comparisons (<c>eq ne co sw ew pr</c>)
/// joined by <c>and</c>, on the attributes the caller supports. Anything else is an <c>invalidFilter</c> error.
/// </summary>
public static class ScimFilter
{
    private static readonly HashSet<string> Ops = ["eq", "ne", "co", "sw", "ew", "pr"];

    /// <summary>Parses <paramref name="filter"/>. Throws <see cref="ScimError"/> (400 invalidFilter).</summary>
    public static List<ScimComparison> Parse(string filter, IReadOnlySet<string> attributes)
    {
        if (filter.Length > 1000) throw Invalid("The filter is too long.");
        var tokens = Tokenize(filter);
        var result = new List<ScimComparison>();
        var i = 0;
        while (true)
        {
            if (i >= tokens.Count) throw Invalid("The filter is incomplete.");
            var attr = tokens[i++];
            if (attr.Quoted || !attributes.Contains(attr.Text)) throw Invalid($"Filtering on '{attr.Text}' isn't supported.");
            if (i >= tokens.Count) throw Invalid("The filter is missing an operator.");
            var op = tokens[i++].Text.ToLowerInvariant();
            if (!Ops.Contains(op)) throw Invalid($"Unknown filter operator '{op}'.");
            string? value = null;
            if (op != "pr")
            {
                if (i >= tokens.Count) throw Invalid("The filter is missing a value.");
                var v = tokens[i++];
                value = v.Quoted ? v.Text : v.Text.ToLowerInvariant() switch
                {
                    "true" or "false" => v.Text.ToLowerInvariant(),
                    "null" => null,
                    _ => double.TryParse(v.Text, System.Globalization.CultureInfo.InvariantCulture, out _) ? v.Text : throw Invalid($"'{v.Text}' isn't a valid filter value (quote strings)."),
                };
            }
            result.Add(new ScimComparison(Canonical(attributes, attr.Text), op, value));
            if (i == tokens.Count) return result;
            var join = tokens[i++];
            if (join.Quoted || !join.Text.Equals("and", StringComparison.OrdinalIgnoreCase))
                throw Invalid($"Only 'and' can join filter expressions (found '{join.Text}').");
        }
    }

    /// <summary>True when <paramref name="actual"/> (one of the attribute's values) satisfies the comparison. Strings compare case-insensitively.</summary>
    public static bool Matches(ScimComparison c, IEnumerable<string?> actual)
    {
        var values = actual.Where(v => v != null).Select(v => v!).ToList();
        if (c.Op == "pr") return values.Any(v => v.Length > 0);
        var expected = c.Value ?? "";
        const StringComparison ic = StringComparison.OrdinalIgnoreCase;
        return c.Op switch
        {
            "eq" => c.Value == null ? values.Count == 0 : values.Any(v => v.Equals(expected, ic)),
            "ne" => c.Value == null ? values.Count > 0 : !values.Any(v => v.Equals(expected, ic)),
            "co" => values.Any(v => v.Contains(expected, ic)),
            "sw" => values.Any(v => v.StartsWith(expected, ic)),
            "ew" => values.Any(v => v.EndsWith(expected, ic)),
            _ => false,
        };
    }

    private static string Canonical(IReadOnlySet<string> attributes, string name) => attributes.First(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    private sealed record Token(string Text, bool Quoted);

    private static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i])) { i++; continue; }
            if (s[i] == '(' || s[i] == ')' || s[i] == '[' || s[i] == ']') throw Invalid("Grouping and value filters aren't supported in filters.");
            if (s[i] == '"')
            {
                // A JSON string literal, escapes included.
                var start = i++;
                while (i < s.Length && s[i] != '"') i += s[i] == '\\' ? 2 : 1;
                if (i >= s.Length) throw Invalid("Unterminated string in the filter.");
                i++;
                try { tokens.Add(new Token(JsonSerializer.Deserialize<string>(s[start..i])!, true)); }
                catch (JsonException) { throw Invalid("Invalid string in the filter."); }
                continue;
            }
            var begin = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '"' && s[i] != '(' && s[i] != ')') i++;
            tokens.Add(new Token(s[begin..i], false));
        }
        return tokens;
    }

    private static ScimError Invalid(string detail) => new(400, detail, "invalidFilter");
}

/// <summary>A SCIM error response (RFC 7644 §3.12).</summary>
public sealed class ScimError(int status, string detail, string? scimType = null) : Exception(detail)
{
    public int Status { get; } = status;
    public string? ScimType { get; } = scimType;
}
