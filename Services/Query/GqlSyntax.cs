using System.Text;

namespace ByteBench.Services.Query;

// ByteBench Query Language (GQL): the tokenizer, the AST and the recursive-descent parser. Translation to EF lives in
// GqlService/GqlFields; nothing here touches the database. Grammar (keywords are case-insensitive):
//
//   query    := [or] [ORDER BY sort ("," sort)*]
//   or       := and (OR and)*
//   and      := not (AND not)*
//   not      := NOT not | "(" or ")" | clause
//   clause   := field op
//   op       := ("=" | "!=" | "~" | "!~" | "<" | ">" | "<=" | ">=") value
//             | [NOT] IN "(" value ("," value)* ")"
//             | IS [NOT] (EMPTY | NULL)
//   value    := string | word | word "(" [value ("," value)*] ")"      (a word followed by "(" is a function call)
//   sort     := field [ASC | DESC]

/// <summary>A query error with the character position (0-based) and length of the offending text.</summary>
public sealed class GqlException(string reason, int position, int length = 1)
    : Exception($"{reason} (at column {position + 1})")
{
    /// <summary>The message without the position.</summary>
    public string Reason { get; } = reason;
    public int Position { get; } = Math.Max(0, position);
    public int Length { get; } = Math.Max(1, length);
}

public enum GqlTokenKind { Word, String, LParen, RParen, Comma, Op, End }

public readonly record struct GqlToken(GqlTokenKind Kind, string Text, int Start, int Length, bool Unterminated = false)
{
    public int End => Start + Length;
    public bool Is(string keyword) => Kind == GqlTokenKind.Word && Text.Equals(keyword, StringComparison.OrdinalIgnoreCase);
    public bool IsValueToken => Kind is GqlTokenKind.Word or GqlTokenKind.String;
    public string Describe() => Kind switch
    {
        GqlTokenKind.End => "the end of the query",
        GqlTokenKind.String => $"\"{Text}\"",
        _ => $"'{Text}'",
    };
}

public static class GqlLexer
{
    public const int MaxLength = 4000;

    /// <summary>Characters that end an unquoted word.</summary>
    private static bool IsDelimiter(char c) => char.IsWhiteSpace(c) || c is '(' or ')' or ',' or '=' or '!' or '~' or '<' or '>' or '"' or '\'';

    /// <summary>
    /// Splits <paramref name="text"/> into tokens. With <paramref name="lenient"/> (autocomplete), an unterminated string
    /// becomes a token flagged <see cref="GqlToken.Unterminated"/> and a stray '!' an operator, instead of throwing.
    /// </summary>
    public static List<GqlToken> Tokenize(string text, bool lenient = false)
    {
        if (text.Length > MaxLength && !lenient) throw new GqlException($"The query is too long (at most {MaxLength} characters).", MaxLength);
        var tokens = new List<GqlToken>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            var start = i;
            switch (c)
            {
                case '(': tokens.Add(new(GqlTokenKind.LParen, "(", i++, 1)); continue;
                case ')': tokens.Add(new(GqlTokenKind.RParen, ")", i++, 1)); continue;
                case ',': tokens.Add(new(GqlTokenKind.Comma, ",", i++, 1)); continue;
                case '"' or '\'':
                {
                    var sb = new StringBuilder();
                    i++;
                    var closed = false;
                    while (i < text.Length)
                    {
                        var ch = text[i];
                        if (ch == '\\' && i + 1 < text.Length) { sb.Append(text[i + 1]); i += 2; continue; }
                        if (ch == c) { closed = true; i++; break; }
                        sb.Append(ch);
                        i++;
                    }
                    if (!closed && !lenient) throw new GqlException($"The text starting here has no closing {c}.", start, i - start);
                    tokens.Add(new(GqlTokenKind.String, sb.ToString(), start, i - start, !closed));
                    continue;
                }
                case '=' or '~':
                    tokens.Add(new(GqlTokenKind.Op, c.ToString(), i++, 1));
                    continue;
                case '!' or '<' or '>':
                {
                    var two = i + 1 < text.Length && (text[i + 1] == '=' || (c == '!' && text[i + 1] == '~'));
                    if (c == '!' && !two && !lenient) throw new GqlException("'!' must be followed by '=' or '~' (use != or !~, or NOT).", i);
                    var len = two ? 2 : 1;
                    tokens.Add(new(GqlTokenKind.Op, text.Substring(i, len), i, len));
                    i += len;
                    continue;
                }
            }
            while (i < text.Length && !IsDelimiter(text[i])) i++;
            tokens.Add(new(GqlTokenKind.Word, text[start..i], start, i - start));
        }
        tokens.Add(new(GqlTokenKind.End, "", text.Length, 0));
        return tokens;
    }

    public static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
        { "AND", "OR", "NOT", "IN", "IS", "EMPTY", "NULL", "ORDER", "BY", "ASC", "DESC" };

    /// <summary>
    /// <paramref name="value"/> as a GQL literal: bare when it is a plain word (letters, digits, <c>.-_@:/+</c>) that isn't a
    /// keyword, otherwise double-quoted with <c>"</c> and <c>\</c> escaped.
    /// </summary>
    public static string Quote(string value)
    {
        if (value.Length > 0 && !Keywords.Contains(value) && value.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' or '@' or ':' or '/' or '+')
            && !(value[0] == '-' || value[0] == '+'))
            return value;
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}

// ----- AST -----

public enum GqlOperator { Equals, NotEquals, Contains, NotContains, LessThan, GreaterThan, LessOrEqual, GreaterOrEqual, In, NotIn, Is, IsNot }

public abstract record GqlNode(int Start, int Length);

public sealed record GqlAnd(GqlNode Left, GqlNode Right) : GqlNode(Left.Start, Right.Start + Right.Length - Left.Start);

public sealed record GqlOr(GqlNode Left, GqlNode Right) : GqlNode(Left.Start, Right.Start + Right.Length - Left.Start);

public sealed record GqlNot(GqlNode Inner, int NotStart) : GqlNode(NotStart, Inner.Start + Inner.Length - NotStart);

/// <summary>A literal (<see cref="Quoted"/> or a bare word) or a function call (<see cref="IsFunction"/>, with <see cref="Args"/>).</summary>
public sealed record GqlValue(string Text, bool Quoted, bool IsFunction, IReadOnlyList<GqlValue> Args, int Start, int Length)
{
    public bool IsFunctionNamed(string name) => IsFunction && Text.Equals(name, StringComparison.OrdinalIgnoreCase);
    public override string ToString() => IsFunction ? $"{Text}({string.Join(", ", Args)})" : Quoted ? GqlLexer.Quote(Text) : Text;
}

/// <summary>
/// <c>field op value(s)</c>. For <see cref="GqlOperator.Is"/>/<see cref="GqlOperator.IsNot"/> there are no values (the
/// operand is always EMPTY); <c>= EMPTY</c> and <c>!= EMPTY</c> are parsed into those too.
/// </summary>
public sealed record GqlClause(string Field, int FieldStart, int FieldLength, GqlOperator Operator, int OperatorStart, int OperatorLength,
    IReadOnlyList<GqlValue> Values, int End) : GqlNode(FieldStart, End - FieldStart)
{
    public GqlValue? Value => Values.Count == 1 ? Values[0] : null;
}

public sealed record GqlSort(string Field, bool Descending, int Start, int Length);

public sealed record GqlQuery(GqlNode? Where, IReadOnlyList<GqlSort> OrderBy);

public static class GqlOperators
{
    public static string Text(this GqlOperator op) => op switch
    {
        GqlOperator.Equals => "=",
        GqlOperator.NotEquals => "!=",
        GqlOperator.Contains => "~",
        GqlOperator.NotContains => "!~",
        GqlOperator.LessThan => "<",
        GqlOperator.GreaterThan => ">",
        GqlOperator.LessOrEqual => "<=",
        GqlOperator.GreaterOrEqual => ">=",
        GqlOperator.In => "in",
        GqlOperator.NotIn => "not in",
        GqlOperator.Is => "is",
        GqlOperator.IsNot => "is not",
        _ => op.ToString(),
    };

    public static readonly GqlOperator[] Equality = [GqlOperator.Equals, GqlOperator.NotEquals, GqlOperator.In, GqlOperator.NotIn];
    public static readonly GqlOperator[] Empty = [GqlOperator.Is, GqlOperator.IsNot];
    public static readonly GqlOperator[] Ordering = [GqlOperator.LessThan, GqlOperator.GreaterThan, GqlOperator.LessOrEqual, GqlOperator.GreaterOrEqual];
    public static readonly GqlOperator[] TextSearch = [GqlOperator.Contains, GqlOperator.NotContains];
}

// ----- Parser -----

public sealed class GqlParser
{
    public const int MaxClauses = 100;
    public const int MaxDepth = 32;
    public const int MaxListValues = 200;

    private readonly List<GqlToken> tokens;
    private int pos, clauses, depth;

    private GqlParser(List<GqlToken> tokens) => this.tokens = tokens;

    /// <summary>Parses a whole query. Throws <see cref="GqlException"/> with the position of the first error.</summary>
    public static GqlQuery Parse(string text)
    {
        var p = new GqlParser(GqlLexer.Tokenize(text ?? ""));
        return p.ParseQuery();
    }

    private GqlToken Peek => tokens[pos];
    private GqlToken PeekAt(int offset) => tokens[Math.Min(pos + offset, tokens.Count - 1)];
    private GqlToken Next()
    {
        var t = tokens[pos];
        if (pos < tokens.Count - 1) pos++;
        return t;
    }

    private GqlQuery ParseQuery()
    {
        GqlNode? where = null;
        if (Peek.Kind != GqlTokenKind.End && !Peek.Is("ORDER")) where = ParseOr();
        var sorts = new List<GqlSort>();
        if (Peek.Is("ORDER"))
        {
            var order = Next();
            if (!Peek.Is("BY")) throw new GqlException($"Expected BY after ORDER but found {Peek.Describe()}.", Peek.Start, Peek.Length);
            Next();
            do
            {
                if (sorts.Count > 0) Next(); // the comma
                var f = Peek;
                if (!f.IsValueToken || (f.Kind == GqlTokenKind.Word && GqlLexer.Keywords.Contains(f.Text)))
                    throw new GqlException($"Expected a field to sort by but found {f.Describe()}.", f.Start, f.Length);
                Next();
                var desc = false;
                var end = f.End;
                if (Peek.Is("ASC") || Peek.Is("DESC")) { desc = Peek.Is("DESC"); end = Next().End; }
                sorts.Add(new GqlSort(f.Text, desc, f.Start, end - f.Start));
            } while (Peek.Kind == GqlTokenKind.Comma);
            _ = order;
        }
        if (Peek.Kind != GqlTokenKind.End)
        {
            var t = Peek;
            var hint = t.Kind == GqlTokenKind.RParen ? "There is a ')' without a matching '('."
                : where != null && !Peek.Is("ORDER") ? $"Unexpected {t.Describe()}: join conditions with AND or OR." : $"Unexpected {t.Describe()}.";
            throw new GqlException(hint, t.Start, t.Length);
        }
        return new GqlQuery(where, sorts);
    }

    private GqlNode ParseOr()
    {
        var left = ParseAnd();
        while (Peek.Is("OR")) { Next(); left = new GqlOr(left, ParseAnd()); }
        return left;
    }

    private GqlNode ParseAnd()
    {
        var left = ParseNot();
        while (Peek.Is("AND")) { Next(); left = new GqlAnd(left, ParseNot()); }
        return left;
    }

    private GqlNode ParseNot()
    {
        if (++depth > MaxDepth) throw new GqlException($"The query is nested too deeply (at most {MaxDepth} levels).", Peek.Start, Peek.Length);
        try
        {
            if (Peek.Is("NOT"))
            {
                var not = Next();
                return new GqlNot(ParseNot(), not.Start);
            }
            if (Peek.Kind == GqlTokenKind.LParen)
            {
                var open = Next();
                if (Peek.Kind == GqlTokenKind.RParen) throw new GqlException("Empty parentheses: put a condition inside.", open.Start, Peek.End - open.Start);
                var inner = ParseOr();
                if (Peek.Kind != GqlTokenKind.RParen)
                    throw new GqlException($"Expected ')' to close the '(' at column {open.Start + 1} but found {Peek.Describe()}.", Peek.Start, Peek.Length);
                Next();
                return inner;
            }
            return ParseClause();
        }
        finally { depth--; }
    }

    private GqlNode ParseClause()
    {
        var f = Peek;
        if (f.Kind == GqlTokenKind.End) throw new GqlException("Expected a condition such as status = Done, but the query ends here.", f.Start);
        if (!f.IsValueToken || (f.Kind == GqlTokenKind.Word && GqlLexer.Keywords.Contains(f.Text)))
            throw new GqlException($"Expected a field name but found {f.Describe()}.", f.Start, f.Length);
        if (++clauses > MaxClauses) throw new GqlException($"Too many conditions (at most {MaxClauses}).", f.Start, f.Length);
        Next();

        var opToken = Peek;
        GqlOperator op;
        if (opToken.Kind == GqlTokenKind.Op)
        {
            Next();
            op = opToken.Text switch
            {
                "=" => GqlOperator.Equals,
                "!=" => GqlOperator.NotEquals,
                "~" => GqlOperator.Contains,
                "!~" => GqlOperator.NotContains,
                "<" => GqlOperator.LessThan,
                ">" => GqlOperator.GreaterThan,
                "<=" => GqlOperator.LessOrEqual,
                ">=" => GqlOperator.GreaterOrEqual,
                _ => throw new GqlException($"Unknown operator '{opToken.Text}'.", opToken.Start, opToken.Length),
            };
        }
        else if (opToken.Is("IN")) { Next(); op = GqlOperator.In; }
        else if (opToken.Is("NOT") && PeekAt(1).Is("IN")) { Next(); Next(); op = GqlOperator.NotIn; }
        else if (opToken.Is("IS"))
        {
            Next();
            op = GqlOperator.Is;
            if (Peek.Is("NOT")) { Next(); op = GqlOperator.IsNot; }
            if (!Peek.Is("EMPTY") && !Peek.Is("NULL"))
                throw new GqlException($"Expected EMPTY after '{op.Text()}' but found {Peek.Describe()}.", Peek.Start, Peek.Length);
            var e = Next();
            return new GqlClause(f.Text, f.Start, f.Length, op, opToken.Start, e.Start - opToken.Start, [], e.End);
        }
        else
        {
            throw new GqlException($"Expected an operator (=, !=, ~, !~, <, >, <=, >=, in, not in, is) after '{f.Text}' but found {opToken.Describe()}.",
                opToken.Start, opToken.Length);
        }
        var opLength = tokens[pos - 1].End - opToken.Start;

        if (op is GqlOperator.In or GqlOperator.NotIn)
        {
            if (Peek.Kind != GqlTokenKind.LParen)
                throw new GqlException($"Expected '(' after '{op.Text()}', e.g. {f.Text} {op.Text()} (a, b).", Peek.Start, Peek.Length);
            Next();
            var values = new List<GqlValue> { ParseValue(f.Text) };
            while (Peek.Kind == GqlTokenKind.Comma)
            {
                Next();
                values.Add(ParseValue(f.Text));
                if (values.Count > MaxListValues) throw new GqlException($"Too many values in the list (at most {MaxListValues}).", Peek.Start, Peek.Length);
            }
            if (Peek.Kind != GqlTokenKind.RParen) throw new GqlException($"Expected ',' or ')' in the list but found {Peek.Describe()}.", Peek.Start, Peek.Length);
            var close = Next();
            return new GqlClause(f.Text, f.Start, f.Length, op, opToken.Start, opLength, values, close.End);
        }

        // "= EMPTY" / "!= EMPTY" are the same as "is EMPTY" / "is not EMPTY".
        if (op is GqlOperator.Equals or GqlOperator.NotEquals && (Peek.Is("EMPTY") || Peek.Is("NULL")))
        {
            var e = Next();
            return new GqlClause(f.Text, f.Start, f.Length, op == GqlOperator.Equals ? GqlOperator.Is : GqlOperator.IsNot, opToken.Start, opLength, [], e.End);
        }
        var v = ParseValue(f.Text);
        return new GqlClause(f.Text, f.Start, f.Length, op, opToken.Start, opLength, [v], v.Start + v.Length);
    }

    private GqlValue ParseValue(string field)
    {
        var t = Peek;
        if (t.Kind == GqlTokenKind.String) { Next(); return new GqlValue(t.Text, true, false, [], t.Start, t.Length); }
        if (t.Kind != GqlTokenKind.Word)
            throw new GqlException($"Expected a value for '{field}' but found {t.Describe()}.", t.Start, t.Length);
        if (GqlLexer.Keywords.Contains(t.Text))
            throw new GqlException($"Expected a value for '{field}' but found the keyword {t.Text.ToUpperInvariant()} (put it in quotes to use it as a value).", t.Start, t.Length);
        Next();
        if (Peek.Kind != GqlTokenKind.LParen) return new GqlValue(t.Text, false, false, [], t.Start, t.Length);

        // A function call: name(args)
        Next();
        var args = new List<GqlValue>();
        if (Peek.Kind != GqlTokenKind.RParen)
        {
            args.Add(ParseValue(field));
            while (Peek.Kind == GqlTokenKind.Comma) { Next(); args.Add(ParseValue(field)); }
        }
        if (Peek.Kind != GqlTokenKind.RParen)
            throw new GqlException($"Expected ')' to close {t.Text}( but found {Peek.Describe()}.", Peek.Start, Peek.Length);
        var close = Next();
        return new GqlValue(t.Text, false, true, args, t.Start, close.End - t.Start);
    }
}
