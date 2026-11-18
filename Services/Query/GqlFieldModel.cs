using System.Globalization;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Query;

/// <summary>
/// Everything a field needs to translate a clause: the database context the query runs on (expressions may use its
/// sets, e.g. <c>Db.Watches</c>), the user <c>currentUser()</c> stands for, "now", and the projects being searched
/// (already limited to the ones the user can view). Fields resolve names against <see cref="ProjectIds"/> only, so a
/// query can't learn about projects the user can't see.
/// </summary>
public sealed class GqlContext(AppDbContext db, int? userId, DateTime nowUtc, IReadOnlyList<int> projectIds)
{
    public AppDbContext Db { get; } = db;
    public int? UserId { get; } = userId;
    public DateTime NowUtc { get; } = nowUtc;
    public IReadOnlyList<int> ProjectIds { get; } = projectIds;

    /// <summary>Per-query cache for lookups a field repeats (e.g. users by name).</summary>
    public Dictionary<string, object?> Cache { get; } = [];
}

/// <summary>A suggestion for the GQL editor's autocomplete. <see cref="Insert"/> replaces the word being typed.</summary>
public sealed record GqlSuggestion(string Insert, string Label, string? Detail = null, string Kind = "value");

/// <summary>
/// One searchable field. Implement it (usually by deriving from <see cref="QueryField"/>) and return it from an
/// <see cref="IQueryFieldProvider"/> registered in DI to add fields (custom fields, components, versions…).
/// </summary>
public interface IQueryField
{
    /// <summary>The canonical name, shown in autocomplete and used in messages (e.g. <c>assignee</c>).</summary>
    string Name { get; }
    /// <summary>Other names that mean the same field (case-insensitive), e.g. <c>title</c> for <c>summary</c>.</summary>
    IReadOnlyList<string> Aliases { get; }
    /// <summary>One line for autocomplete and the help list.</summary>
    string Description { get; }
    /// <summary>The operators this field accepts; anything else is an error at the operator's position.</summary>
    IReadOnlyCollection<GqlOperator> Operators { get; }
    /// <summary>Sort keys for ORDER BY (each an <c>Expression&lt;Func&lt;Issue, T&gt;&gt;</c>), or empty if the field can't be sorted on.</summary>
    IReadOnlyList<LambdaExpression> SortKeys { get; }
    /// <summary>The issue predicate for <paramref name="clause"/>. Throw <see cref="GqlException"/> for bad values (with the value's position).</summary>
    Task<Expression<Func<Issue, bool>>> BuildAsync(GqlClause clause, GqlContext ctx);
    /// <summary>Value suggestions starting with (or containing) <paramref name="prefix"/>.</summary>
    Task<IReadOnlyList<GqlSuggestion>> SuggestValuesAsync(GqlContext ctx, string prefix);
}

/// <summary>A source of query fields. Register implementations with <c>services.AddSingleton&lt;IQueryFieldProvider, …&gt;()</c>.</summary>
public interface IQueryFieldProvider
{
    IEnumerable<IQueryField> GetFields();
}

/// <summary>All registered fields by name and alias (case-insensitive). A name used twice is a startup error.</summary>
public sealed class GqlFieldRegistry
{
    private readonly Dictionary<string, IQueryField> byName = new(StringComparer.OrdinalIgnoreCase);

    public GqlFieldRegistry(IEnumerable<IQueryFieldProvider> providers)
    {
        var all = new List<IQueryField>();
        foreach (var field in providers.SelectMany(p => p.GetFields()))
        {
            foreach (var name in field.Aliases.Prepend(field.Name))
                if (!byName.TryAdd(name, field))
                    throw new InvalidOperationException($"Two query fields are named '{name}'.");
            all.Add(field);
        }
        Fields = all;
    }

    public IReadOnlyList<IQueryField> Fields { get; }

    public IQueryField? Find(string name) => byName.GetValueOrDefault(name.Trim());
}

/// <summary>Base class with value helpers for fields. Override <see cref="BuildAsync"/> and optionally <see cref="SuggestValuesAsync"/>.</summary>
public abstract class QueryField : IQueryField
{
    public abstract string Name { get; }
    public virtual IReadOnlyList<string> Aliases => [];
    public abstract string Description { get; }
    public abstract IReadOnlyCollection<GqlOperator> Operators { get; }
    public virtual IReadOnlyList<LambdaExpression> SortKeys => [];
    public abstract Task<Expression<Func<Issue, bool>>> BuildAsync(GqlClause clause, GqlContext ctx);
    public virtual Task<IReadOnlyList<GqlSuggestion>> SuggestValuesAsync(GqlContext ctx, string prefix) => Task.FromResult<IReadOnlyList<GqlSuggestion>>([]);

    protected static LambdaExpression[] Sort<T>(params Expression<Func<Issue, T>>[] keys) => keys;

    // ----- Clause helpers -----

    protected static bool IsNegated(GqlOperator op) => op is GqlOperator.NotEquals or GqlOperator.NotIn or GqlOperator.NotContains or GqlOperator.IsNot;

    /// <summary>Applies NOT for the negative operators (!=, not in, !~, is not) to the positive predicate.</summary>
    protected static Expression<Func<Issue, bool>> Polarity(GqlOperator op, Expression<Func<Issue, bool>> positive) =>
        IsNegated(op) ? Gx.Not(positive) : positive;

    protected static GqlException ValueError(GqlValue v, string message) => new(message, v.Start, v.Length);

    /// <summary>Literal text of a value; functions are refused unless the field handles them itself.</summary>
    protected string Literal(GqlValue v)
    {
        if (v.IsFunction) throw ValueError(v, $"{v.Text}() can't be used with {Name}.");
        return v.Text;
    }

    // ----- Value parsing shared by fields -----

    private static readonly Regex Relative = new(@"^([+-])?((?:\d{1,6}[yMwdhm])+)$", RegexOptions.CultureInvariant);
    private static readonly Regex RelativePart = new(@"(\d{1,6})([yMwdhm])", RegexOptions.CultureInvariant);
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "yyyy/MM/dd", "yyyy-MM-dd HH:mm", "yyyy/MM/dd HH:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss"];

    /// <summary>
    /// A date value: <c>2026-01-31</c> (a whole UTC day, <see cref="GqlDate.IsDay"/>), <c>"2026-01-31 14:30"</c>, a relative
    /// offset from now (<c>-7d</c>, <c>-2w</c>, <c>+1M</c>, <c>-1w2d</c>; units y, M, w, d, h, m), or a function: <c>now()</c>,
    /// <c>startOfDay()</c>, <c>endOfDay()</c>, <c>startOfWeek()</c> (Monday), <c>endOfWeek()</c>, <c>startOfMonth()</c>,
    /// <c>endOfMonth()</c>, <c>startOfYear()</c>, <c>endOfYear()</c>; each optionally with an offset, e.g. <c>startOfWeek(-1w)</c>.
    /// The end functions are the start of the next period.
    /// </summary>
    public static GqlDate ParseDate(GqlValue v, DateTime nowUtc)
    {
        if (v.IsFunction)
        {
            var name = v.Text.ToLowerInvariant();
            var offset = v.Args.Count switch
            {
                0 => (Func<DateTime, DateTime>)(d => d),
                1 when !v.Args[0].IsFunction && TryRelative(v.Args[0].Text, out var f) => f,
                _ => throw ValueError(v, $"{v.Text}() takes at most one offset such as -1w."),
            };
            var today = nowUtc.Date;
            var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            var month = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var year = new DateTime(today.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime? at = name switch
            {
                "now" => nowUtc,
                "startofday" => today,
                "endofday" => today.AddDays(1),
                "startofweek" => monday,
                "endofweek" => monday.AddDays(7),
                "startofmonth" => month,
                "endofmonth" => month.AddMonths(1),
                "startofyear" => year,
                "endofyear" => year.AddYears(1),
                _ => null,
            };
            if (at is not { } a) throw ValueError(v, $"Unknown date function {v.Text}(). Use now(), startOfDay(), startOfWeek(), startOfMonth(), startOfYear() or their endOf… versions.");
            return new GqlDate(DateTime.SpecifyKind(offset(a), DateTimeKind.Utc), false);
        }
        if (TryRelative(v.Text, out var rel)) return new GqlDate(rel(nowUtc), false);
        if (DateTime.TryParseExact(v.Text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
            return new GqlDate(DateTime.SpecifyKind(d, DateTimeKind.Utc), v.Text.Length <= 10);
        throw ValueError(v, $"'{v.Text}' is not a date. Use 2026-01-31, \"2026-01-31 14:30\", a relative date such as -7d or -2w, or a function such as startOfWeek().");
    }

    private static bool TryRelative(string text, out Func<DateTime, DateTime> apply)
    {
        apply = d => d;
        var m = Relative.Match(text);
        if (!m.Success) return false;
        var sign = m.Groups[1].Value == "-" ? -1 : 1;
        var parts = RelativePart.Matches(m.Groups[2].Value).Select(p => (n: int.Parse(p.Groups[1].Value, CultureInfo.InvariantCulture) * sign, unit: p.Groups[2].Value[0])).ToList();
        apply = d =>
        {
            foreach (var (n, unit) in parts)
                d = unit switch
                {
                    'y' => d.AddYears(Math.Clamp(n, -1000, 1000)),
                    'M' => d.AddMonths(Math.Clamp(n, -12000, 12000)),
                    'w' => d.AddDays(n * 7.0),
                    'd' => d.AddDays(n),
                    'h' => d.AddHours(n),
                    _ => d.AddMinutes(n),
                };
            return d;
        };
        return true;
    }

    /// <summary>Resolves a user value (<c>currentUser()</c>, a username, an email or a display name) to a user id.</summary>
    protected static async Task<int> ResolveUserAsync(GqlValue v, GqlContext ctx, string field)
    {
        if (v.IsFunctionNamed("currentUser"))
            return ctx.UserId ?? throw ValueError(v, "currentUser() needs a signed-in user.");
        if (v.IsFunction) throw ValueError(v, $"{v.Text}() can't be used with {field}; use currentUser() or a username.");
        var key = "user:" + v.Text.ToLowerInvariant();
        if (ctx.Cache.TryGetValue(key, out var cached)) return (int?)cached ?? throw ValueError(v, NoUser(v.Text));
        var text = v.Text.Trim();
        var lower = text.ToLowerInvariant();
        var id = await ctx.Db.Users.AsNoTracking()
                     .Where(u => u.NormalizedUserName == text.ToUpperInvariant() || u.NormalizedEmail == text.ToUpperInvariant())
                     .Select(u => (int?)u.Id).FirstOrDefaultAsync()
                 ?? await ctx.Db.Users.AsNoTracking().Where(u => u.DisplayName.ToLower() == lower).Select(u => (int?)u.Id).FirstOrDefaultAsync();
        ctx.Cache[key] = id;
        return id ?? throw ValueError(v, NoUser(v.Text));
    }

    private static string NoUser(string name) => $"There is no user '{name}'. Use a username, an email or currentUser().";

    protected static async Task<IReadOnlyList<GqlSuggestion>> SuggestUsersAsync(GqlContext ctx, string prefix)
    {
        var p = prefix.ToLowerInvariant();
        var users = await ctx.Db.Users.AsNoTracking()
            .Where(u => u.UserName != Ai.AiBot.UserName && (p == "" || u.UserName!.ToLower().StartsWith(p) || u.DisplayName.ToLower().Contains(p)))
            .OrderBy(u => u.UserName).Take(10).Select(u => new { u.UserName, u.DisplayName }).ToListAsync();
        var list = new List<GqlSuggestion>();
        if ("currentuser()".StartsWith(p)) list.Add(new("currentUser()", "currentUser()", "you", "function"));
        list.AddRange(users.Select(u => new GqlSuggestion(GqlLexer.Quote(u.UserName!), u.UserName!, u.DisplayName)));
        return list;
    }
}

/// <summary>A resolved date value. <see cref="IsDay"/>: a date without a time, which <c>=</c> matches as the whole day.</summary>
public readonly record struct GqlDate(DateTime Value, bool IsDay);

/// <summary>Expression helpers: combine predicates and plug selectors into templates (values stay EF parameters).</summary>
public static class Gx
{
    public static Expression<Func<Issue, bool>> True { get; } = _ => true;
    public static Expression<Func<Issue, bool>> False { get; } = _ => false;

    public static Expression<Func<Issue, bool>> And(Expression<Func<Issue, bool>> a, Expression<Func<Issue, bool>> b) => Combine(a, b, Expression.AndAlso);
    public static Expression<Func<Issue, bool>> Or(Expression<Func<Issue, bool>> a, Expression<Func<Issue, bool>> b) => Combine(a, b, Expression.OrElse);

    public static Expression<Func<Issue, bool>> Not(Expression<Func<Issue, bool>> a) => Expression.Lambda<Func<Issue, bool>>(Expression.Not(a.Body), a.Parameters);

    public static Expression<Func<Issue, bool>> Any(IEnumerable<Expression<Func<Issue, bool>>> parts) =>
        parts.Aggregate((Expression<Func<Issue, bool>>?)null, (acc, p) => acc == null ? p : Or(acc, p)) ?? False;

    private static Expression<Func<Issue, bool>> Combine(Expression<Func<Issue, bool>> a, Expression<Func<Issue, bool>> b, Func<Expression, Expression, BinaryExpression> op)
    {
        var body = new Rebind(b.Parameters[0], a.Parameters[0]).Visit(b.Body);
        return Expression.Lambda<Func<Issue, bool>>(op(a.Body, body), a.Parameters);
    }

    /// <summary><paramref name="template"/> applied to the value <paramref name="selector"/> picks from an issue.</summary>
    public static Expression<Func<Issue, TOut>> Apply<TIn, TOut>(Expression<Func<Issue, TIn>> selector, Expression<Func<TIn, TOut>> template)
    {
        var body = new Rebind(template.Parameters[0], selector.Body).Visit(template.Body);
        return Expression.Lambda<Func<Issue, TOut>>(body, selector.Parameters);
    }

    /// <summary><c>selector op value</c> for = != &lt; &gt; &lt;= &gt;= (enums compare by their numeric value; nullable values are lifted).</summary>
    public static Expression<Func<Issue, bool>> Compare<T>(Expression<Func<Issue, T>> selector, GqlOperator op, T value)
    {
        Expression left = selector.Body;
        Expression right = Expression.Property(Expression.Constant(new Box<T>(value)), nameof(Box<T>.Value)); // an EF parameter, not a literal
        var type = typeof(T);
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying.IsEnum)
        {
            var asInt = type == underlying ? typeof(int) : typeof(int?);
            left = Expression.Convert(left, asInt);
            right = Expression.Convert(right, asInt);
        }
        Expression body = op switch
        {
            GqlOperator.Equals => Expression.Equal(left, right),
            GqlOperator.NotEquals => Expression.NotEqual(left, right),
            GqlOperator.LessThan => Expression.LessThan(left, right),
            GqlOperator.GreaterThan => Expression.GreaterThan(left, right),
            GqlOperator.LessOrEqual => Expression.LessThanOrEqual(left, right),
            GqlOperator.GreaterOrEqual => Expression.GreaterThanOrEqual(left, right),
            _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Not a comparison."),
        };
        return Expression.Lambda<Func<Issue, bool>>(body, selector.Parameters);
    }

    private sealed class Box<T>(T value)
    {
        public T Value { get; } = value;
    }

    private sealed class Rebind(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
