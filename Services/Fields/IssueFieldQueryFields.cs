using System.Globalization;
using System.Linq.Expressions;
using ByteBench.Data;
using ByteBench.Services.Query;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Fields;

/// <summary>
/// GQL fields for components, versions, estimates, the Workbench charge code and custom fields: <c>component</c>,
/// <c>fixVersion</c>, <c>affectsVersion</c>, <c>originalEstimate</c>, <c>remainingEstimate</c>, <c>wbs</c> and <c>cf[key]</c>.
/// Names (components, versions, custom field keys) resolve only in the projects being searched (<see cref="GqlContext.ProjectIds"/>),
/// so a query can't learn about projects the user can't see.
/// </summary>
public sealed class IssueFieldQueryFields : IQueryFieldProvider
{
    public IEnumerable<IQueryField> GetFields() =>
    [
        new ComponentQueryField(),
        new VersionQueryField("fixVersion", ["fixVersions"], "Fix version name or id", VersionLinkKind.Fix),
        new VersionQueryField("affectsVersion", ["affectsVersions", "affectedVersion"], "Affects version name or id", VersionLinkKind.Affects),
        new EstimateQueryField("originalEstimate", "Original estimate", i => i.OriginalEstimateMinutes),
        new EstimateQueryField("remainingEstimate", "Remaining estimate", i => i.RemainingEstimateMinutes),
        new WbsQueryField(),
        new CustomFieldQueryField(),
    ];
}

// ----- Components & versions -----

/// <summary>Matches names (case-insensitive) or ids of project-owned items within the projects searched.</summary>
internal abstract class ProjectItemQueryField : QueryField
{
    public override IReadOnlyCollection<GqlOperator> Operators => [.. GqlOperators.Equality, .. GqlOperators.Empty];

    /// <summary>What the item is called in messages ("component").</summary>
    protected abstract string Noun { get; }

    /// <summary>The ids of items in <paramref name="pids"/> named <paramref name="lower"/> or with id <paramref name="id"/>.</summary>
    protected abstract Task<List<int>> FindAsync(AppDbContext db, IReadOnlyList<int> pids, string lower, int? id);

    protected abstract Expression<Func<Issue, bool>> Has(List<int> ids);
    protected abstract Expression<Func<Issue, bool>> HasNone();

    public override async Task<Expression<Func<Issue, bool>>> BuildAsync(GqlClause c, GqlContext ctx)
    {
        if (c.Operator is GqlOperator.Is or GqlOperator.IsNot) return Polarity(c.Operator, HasNone());
        var ids = new List<int>();
        foreach (var v in c.Values)
        {
            var text = Literal(v).Trim();
            int? id = int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
            var found = await FindAsync(ctx.Db, ctx.ProjectIds, text.ToLowerInvariant(), id);
            if (found.Count == 0) throw ValueError(v, $"No project searched has a {Noun} '{v.Text}'.");
            ids.AddRange(found);
        }
        return Polarity(c.Operator, Has(ids.Distinct().ToList()));
    }
}

internal sealed class ComponentQueryField : ProjectItemQueryField
{
    public override string Name => "component";
    public override IReadOnlyList<string> Aliases => ["components"];
    public override string Description => "Component name or id; = matches issues in the component";
    protected override string Noun => "component";

    protected override Task<List<int>> FindAsync(AppDbContext db, IReadOnlyList<int> pids, string lower, int? id) =>
        db.ProjectComponents.AsNoTracking().Where(x => pids.Contains(x.ProjectId) && (x.Name.ToLower() == lower || (id != null && x.Id == id)))
            .Select(x => x.Id).ToListAsync();

    protected override Expression<Func<Issue, bool>> Has(List<int> ids) => i => i.Components.Any(x => ids.Contains(x.Id));
    protected override Expression<Func<Issue, bool>> HasNone() => i => !i.Components.Any();

    public override async Task<IReadOnlyList<GqlSuggestion>> SuggestValuesAsync(GqlContext ctx, string prefix)
    {
        var pids = ctx.ProjectIds;
        var p = prefix.ToLowerInvariant();
        var names = await ctx.Db.ProjectComponents.AsNoTracking().Where(x => pids.Contains(x.ProjectId) && x.Name.ToLower().StartsWith(p))
            .Select(x => x.Name).Distinct().OrderBy(n => n).Take(15).ToListAsync();
        return names.Select(n => new GqlSuggestion(GqlLexer.Quote(n), n)).ToList();
    }
}

internal sealed class VersionQueryField(string name, string[] aliases, string description, VersionLinkKind kind) : ProjectItemQueryField
{
    public override string Name => name;
    public override IReadOnlyList<string> Aliases => aliases;
    public override string Description => description;
    protected override string Noun => "version";

    protected override Task<List<int>> FindAsync(AppDbContext db, IReadOnlyList<int> pids, string lower, int? id) =>
        db.ProjectVersions.AsNoTracking().Where(x => pids.Contains(x.ProjectId) && (x.Name.ToLower() == lower || (id != null && x.Id == id)))
            .Select(x => x.Id).ToListAsync();

    protected override Expression<Func<Issue, bool>> Has(List<int> ids) => i => i.VersionLinks.Any(l => l.Kind == kind && ids.Contains(l.VersionId));
    protected override Expression<Func<Issue, bool>> HasNone() => i => !i.VersionLinks.Any(l => l.Kind == kind);

    public override async Task<IReadOnlyList<GqlSuggestion>> SuggestValuesAsync(GqlContext ctx, string prefix)
    {
        var pids = ctx.ProjectIds;
        var p = prefix.ToLowerInvariant();
        var rows = await ctx.Db.ProjectVersions.AsNoTracking().Where(x => pids.Contains(x.ProjectId) && x.Name.ToLower().StartsWith(p))
            .OrderBy(x => x.IsReleased).ThenBy(x => x.Order).Take(15).Select(x => new { x.Name, x.IsReleased }).ToListAsync();
        return rows.DistinctBy(r => r.Name.ToLowerInvariant())
            .Select(r => new GqlSuggestion(GqlLexer.Quote(r.Name), r.Name, r.IsReleased ? "released" : "unreleased")).ToList();
    }
}

// ----- Estimates -----

/// <summary>Durations (<c>2h</c>, <c>"1d 4h"</c>, <c>30m</c>; a bare number is hours) compared in minutes.</summary>
internal abstract class DurationQueryField : QueryField
{
    public override string Description => "Duration such as 2h, 30m or \"1d 4h\" (a day is the project's hours per day)";
    public override IReadOnlyCollection<GqlOperator> Operators => [GqlOperator.Equals, GqlOperator.NotEquals, .. GqlOperators.Ordering, .. GqlOperators.Empty];

    /// <summary>
    /// <c>selector op duration</c>. Days and weeks depend on each issue's project (<see cref="Project.HoursPerDay"/>), so the
    /// duration is split into fixed minutes plus minutes per working hour of a day, using <see cref="Duration"/> itself:
    /// parsing with 1 and 2 hours per day gives both parts.
    /// </summary>
    protected Expression<Func<Issue, bool>> Compare(Expression<Func<Issue, int?>> selector, GqlClause c)
    {
        var v = c.Value!;
        var text = Literal(v);
        if (!Duration.TryParse(text, 1, out var oneHourDays) || !Duration.TryParse(text, 2, out var twoHourDays))
            throw ValueError(v, $"'{v.Text}' isn't a duration. Use something like 2h, 30m or \"1d 4h\".");
        var perDayHour = twoHourDays - oneHourDays;
        var fixedMinutes = oneHourDays - perDayHour;
        if (perDayHour == 0) return Gx.Compare(selector, c.Operator, (int?)fixedMinutes);
        Expression<Func<Issue, int?>> target = i => fixedMinutes + perDayHour * i.Project.HoursPerDay;
        var right = new Rebind(target.Parameters[0], selector.Parameters[0]).Visit(target.Body);
        Expression body = c.Operator switch
        {
            GqlOperator.Equals => Expression.Equal(selector.Body, right),
            GqlOperator.NotEquals => Expression.NotEqual(selector.Body, right),
            GqlOperator.LessThan => Expression.LessThan(selector.Body, right),
            GqlOperator.GreaterThan => Expression.GreaterThan(selector.Body, right),
            GqlOperator.LessOrEqual => Expression.LessThanOrEqual(selector.Body, right),
            _ => Expression.GreaterThanOrEqual(selector.Body, right),
        };
        return Expression.Lambda<Func<Issue, bool>>(body, selector.Parameters);
    }

    public override Task<IReadOnlyList<GqlSuggestion>> SuggestValuesAsync(GqlContext ctx, string prefix) =>
        Task.FromResult<IReadOnlyList<GqlSuggestion>>(new[] { "30m", "1h", "2h", "4h", "1d", "1w" }
            .Where(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(s => new GqlSuggestion(s, s)).ToList());

    private sealed class Rebind(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}

internal sealed class EstimateQueryField(string name, string label, Expression<Func<Issue, int?>> selector) : DurationQueryField
{
    public override string Name => name;
    public override string Description => label + ": " + base.Description;
    public override IReadOnlyList<LambdaExpression> SortKeys => [selector];

    public override Task<Expression<Func<Issue, bool>>> BuildAsync(GqlClause c, GqlContext ctx) =>
        Task.FromResult(c.Operator is GqlOperator.Is or GqlOperator.IsNot
            ? Polarity(c.Operator, Gx.Apply(selector, x => x == null))
            : Compare(selector, c));
}

// ----- Workbench -----

/// <summary>
/// <c>wbs</c>: the issue's Workbench charge code (its override, else its project's WBS code), compared case-insensitively.
/// <c>is EMPTY</c> matches issues without one.
/// </summary>
internal sealed class WbsQueryField : QueryField
{
    public override string Name => "wbs";
    public override IReadOnlyList<string> Aliases => ["workbenchWbs", "chargeCode"];
    public override string Description => "Workbench charge code (WBS), e.g. wbs = \"1.2.3\"";
    public override IReadOnlyCollection<GqlOperator> Operators => [.. GqlOperators.Equality, .. GqlOperators.Empty];

    private static readonly Expression<Func<Issue, string?>> Effective = i => i.WorkbenchWbs ?? i.Project.WorkbenchWbs;
    public override IReadOnlyList<LambdaExpression> SortKeys => [Effective];

    public override Task<Expression<Func<Issue, bool>>> BuildAsync(GqlClause c, GqlContext ctx)
    {
        if (c.Operator is GqlOperator.Is or GqlOperator.IsNot)
            return Task.FromResult(Polarity(c.Operator, i => (i.WorkbenchWbs ?? i.Project.WorkbenchWbs) == null));
        var codes = c.Values.Select(v => Literal(v).Trim().ToLowerInvariant()).ToList();
        Expression<Func<Issue, bool>> match = i => (i.WorkbenchWbs ?? i.Project.WorkbenchWbs) != null
            && codes.Contains((i.WorkbenchWbs ?? i.Project.WorkbenchWbs)!.ToLower());
        return Task.FromResult(Polarity(c.Operator, match));
    }

    public override async Task<IReadOnlyList<GqlSuggestion>> SuggestValuesAsync(GqlContext ctx, string prefix)
    {
        var pids = ctx.ProjectIds;
        var p = prefix.ToLowerInvariant();
        var projectCodes = await ctx.Db.Projects.AsNoTracking().Where(x => pids.Contains(x.Id) && x.WorkbenchWbs != null && x.WorkbenchWbs.ToLower().StartsWith(p))
            .Select(x => x.WorkbenchWbs!).Distinct().Take(15).ToListAsync();
        var issueCodes = await ctx.Db.Issues.AsNoTracking().Where(x => pids.Contains(x.ProjectId) && x.WorkbenchWbs != null && x.WorkbenchWbs.ToLower().StartsWith(p))
            .Select(x => x.WorkbenchWbs!).Distinct().Take(15).ToListAsync();
        return projectCodes.Concat(issueCodes).Distinct(StringComparer.OrdinalIgnoreCase).Order().Take(15)
            .Select(code => new GqlSuggestion(GqlLexer.Quote(code), code)).ToList();
    }
}

// ----- Custom fields -----

/// <summary>
/// <c>cf[key]</c>: the custom field with that key in each project searched (keys are unique per project, so one key can
/// name fields in several projects, possibly of different types). Bracketed names keep custom fields apart from built-in
/// fields, so a custom field can never collide with (or shadow) one.
/// </summary>
internal sealed class CustomFieldQueryField : QueryField, IParameterizedQueryField
{
    public override string Name => "cf";
    public override string Description => "A custom field by key, e.g. cf[severity] = Major";
    public override IReadOnlyCollection<GqlOperator> Operators => AllOperators;

    internal static readonly GqlOperator[] AllOperators = Enum.GetValues<GqlOperator>();

    public override Task<Expression<Func<Issue, bool>>> BuildAsync(GqlClause c, GqlContext ctx) =>
        throw new GqlException("Name the custom field by its key in brackets, e.g. cf[severity] = Major.", c.FieldStart, c.FieldLength);

    public IQueryField? Bind(string argument)
    {
        var key = argument.Trim().ToLowerInvariant();
        return key.Length is > 0 and <= 40 && key.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_') ? new BoundCustomField(key) : null;
    }

    public async Task<IReadOnlyList<GqlSuggestion>> SuggestArgumentsAsync(GqlContext ctx, string prefix)
    {
        var pids = ctx.ProjectIds;
        var p = prefix.ToLowerInvariant();
        var rows = await ctx.Db.CustomFields.AsNoTracking().Where(f => pids.Contains(f.ProjectId) && f.Key.StartsWith(p))
            .OrderBy(f => f.Key).Select(f => new { f.Key, f.Name, f.Type }).Take(50).ToListAsync();
        return rows.DistinctBy(r => r.Key).Take(15)
            .Select(r => new GqlSuggestion($"cf[{r.Key}]", $"cf[{r.Key}]", $"{r.Name} ({CustomFieldRules.TypeName(r.Type)})", "field")).ToList();
    }
}

/// <summary>One custom field key, e.g. <c>cf[severity]</c>. Which operators apply depends on the field's type.</summary>
internal sealed class BoundCustomField(string key) : QueryField
{
    private enum Kind { Text, Number, Date, Select, User, Checkbox }

    private sealed record Def(int Id, int ProjectId, CustomFieldType Type, List<string> Options);

    private static Kind KindOf(CustomFieldType t) => t switch
    {
        CustomFieldType.Number => Kind.Number,
        CustomFieldType.Date => Kind.Date,
        CustomFieldType.SingleSelect or CustomFieldType.MultiSelect => Kind.Select,
        CustomFieldType.User => Kind.User,
        CustomFieldType.Checkbox => Kind.Checkbox,
        _ => Kind.Text, // text, long text, URL
    };

    private static IReadOnlyCollection<GqlOperator> OperatorsOf(Kind k) => k switch
    {
        Kind.Text => [.. GqlOperators.TextSearch, GqlOperator.Equals, GqlOperator.NotEquals, .. GqlOperators.Empty],
        Kind.Number or Kind.Date => [GqlOperator.Equals, GqlOperator.NotEquals, .. GqlOperators.Ordering, .. GqlOperators.Empty],
        Kind.Select or Kind.User => [.. GqlOperators.Equality, .. GqlOperators.Empty],
        _ => [GqlOperator.Equals, GqlOperator.NotEquals],
    };

    public override string Name => $"cf[{key}]";
    public override string Description => "Custom field " + key;
    // The type is only known per project at query time; BuildAsync checks the operator against it.
    public override IReadOnlyCollection<GqlOperator> Operators => CustomFieldQueryField.AllOperators;

    /// <summary>The fields with this key in the projects searched.</summary>
    private async Task<List<Def>> DefsAsync(GqlContext ctx)
    {
        var cacheKey = "cf:" + key;
        if (ctx.Cache.TryGetValue(cacheKey, out var cached)) return (List<Def>)cached!;
        var pids = ctx.ProjectIds;
        var k = key;
        var rows = await ctx.Db.CustomFields.AsNoTracking().Where(f => pids.Contains(f.ProjectId) && f.Key == k)
            .Select(f => new { f.Id, f.ProjectId, f.Type, f.Options }).ToListAsync();
        var defs = rows.Select(r => new Def(r.Id, r.ProjectId, r.Type, r.Options)).ToList();
        ctx.Cache[cacheKey] = defs;
        return defs;
    }

    public override async Task<Expression<Func<Issue, bool>>> BuildAsync(GqlClause c, GqlContext ctx)
    {
        var defs = await DefsAsync(ctx);
        if (defs.Count == 0) throw new GqlException($"No project searched has a custom field '{key}'.", c.FieldStart, c.FieldLength);

        if (c.Operator is GqlOperator.Is or GqlOperator.IsNot)
        {
            var allIds = defs.Select(d => d.Id).ToList();
            var withField = defs.Select(d => d.ProjectId).Distinct().ToList();
            if (c.Operator == GqlOperator.Is) return i => withField.Contains(i.ProjectId) && !i.CustomFieldValues.Any(v => allIds.Contains(v.FieldId));
            return i => i.CustomFieldValues.Any(v => allIds.Contains(v.FieldId));
        }

        var parts = new List<Expression<Func<Issue, bool>>>();
        GqlException? valueError = null;
        var groups = defs.GroupBy(d => KindOf(d.Type)).ToList();
        foreach (var g in groups)
        {
            if (!OperatorsOf(g.Key).Contains(c.Operator)) continue;
            Expression<Func<CustomFieldValue, bool>> match;
            try { match = await MatchAsync(g.Key, g.ToList(), c, ctx); }
            catch (GqlException ex) { valueError ??= ex; continue; }
            var ids = g.Select(d => d.Id).ToList();
            var projects = g.Select(d => d.ProjectId).Distinct().ToList();
            var inField = And((Expression<Func<CustomFieldValue, bool>>)(v => ids.Contains(v.FieldId)), match);
            Expression<Func<Issue, bool>> positive = i => i.CustomFieldValues.AsQueryable().Any(inField);
            if (g.Key == Kind.Checkbox && IsFalse(c.Value!)) positive = Gx.Not(positive); // unchecked: false or never set
            // Negative operators match issues of projects that have the field (including those without a value), like != elsewhere.
            parts.Add(IsNegated(c.Operator) ? Gx.And(i => projects.Contains(i.ProjectId), Gx.Not(positive))
                : g.Key == Kind.Checkbox ? Gx.And(i => projects.Contains(i.ProjectId), positive) : positive);
        }
        if (parts.Count > 0) return Gx.Any(parts);
        if (valueError != null) throw valueError;
        var kinds = groups.Select(g => g.Key).ToList();
        var allowed = kinds.SelectMany(OperatorsOf).Distinct().Select(o => o.Text());
        var types = string.Join(" / ", defs.Select(d => CustomFieldRules.TypeName(d.Type)).Distinct());
        throw new GqlException($"{Name} ({types}) doesn't support '{c.Operator.Text()}'. Use {string.Join(", ", allowed)}.", c.OperatorStart, c.OperatorLength);
    }

    private static bool IsFalse(GqlValue v) => !v.IsFunction && v.Text.Trim().ToLowerInvariant() is "false" or "no" or "0" or "off";

    /// <summary>The value condition (positive form) for the fields of one kind.</summary>
    private async Task<Expression<Func<CustomFieldValue, bool>>> MatchAsync(Kind kind, List<Def> defs, GqlClause c, GqlContext ctx)
    {
        switch (kind)
        {
            case Kind.Text when c.Operator is GqlOperator.Contains or GqlOperator.NotContains:
            {
                Literal(c.Value!);
                return BuiltInQueryFields.Terms(c.Value!)
                    .Select(p => (Expression<Func<CustomFieldValue, bool>>)(v => v.TextValue != null && EF.Functions.Like(v.TextValue, p, "\\")))
                    .Aggregate(And);
            }
            case Kind.Text:
            {
                var lower = Literal(c.Value!).Trim().ToLowerInvariant();
                return v => v.TextValue != null && v.TextValue.ToLower() == lower;
            }
            case Kind.Number:
            {
                var text = Literal(c.Value!);
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n))
                    throw ValueError(c.Value!, $"'{c.Value!.Text}' is not a number.");
                double? x = n;
                return c.Operator switch
                {
                    GqlOperator.LessThan => v => v.NumberValue < x,
                    GqlOperator.GreaterThan => v => v.NumberValue > x,
                    GqlOperator.LessOrEqual => v => v.NumberValue <= x,
                    GqlOperator.GreaterOrEqual => v => v.NumberValue >= x,
                    _ => v => v.NumberValue == x,
                };
            }
            case Kind.Date:
            {
                var date = ParseDate(c.Value!, ctx.NowUtc);
                DateTime? start = date.Value, next = date.Value.AddDays(1);
                return (c.Operator, date.IsDay) switch
                {
                    (GqlOperator.Equals or GqlOperator.NotEquals, true) => v => v.DateValue >= start && v.DateValue < next,
                    (GqlOperator.Equals or GqlOperator.NotEquals, false) => v => v.DateValue == start,
                    (GqlOperator.LessThan, _) => v => v.DateValue < start,
                    (GqlOperator.LessOrEqual, true) => v => v.DateValue < next,
                    (GqlOperator.LessOrEqual, false) => v => v.DateValue <= start,
                    (GqlOperator.GreaterThan, true) => v => v.DateValue >= next,
                    (GqlOperator.GreaterThan, false) => v => v.DateValue > start,
                    _ => v => v.DateValue >= start,
                };
            }
            case Kind.Select:
            {
                var options = defs.SelectMany(d => d.Options).Select(o => o.ToLowerInvariant()).ToHashSet();
                var chosen = new List<string>();
                foreach (var value in c.Values)
                {
                    var lower = Literal(value).Trim().ToLowerInvariant();
                    if (!options.Contains(lower))
                        throw ValueError(value, $"'{value.Text}' is not an option of {Name}. Use one of: {string.Join(", ", defs.SelectMany(d => d.Options).Distinct(StringComparer.OrdinalIgnoreCase).Select(GqlLexer.Quote))}.");
                    chosen.Add(lower);
                }
                return v => v.TextValue != null && chosen.Contains(v.TextValue.ToLower());
            }
            case Kind.User:
            {
                var users = new List<int>();
                foreach (var value in c.Values) users.Add(await ResolveUserAsync(value, ctx, Name));
                return v => v.UserId != null && users.Contains(v.UserId.Value);
            }
            default: // checkbox: "= false" is the negation of "= true" (see BuildAsync)
            {
                var text = Literal(c.Value!).Trim().ToLowerInvariant();
                if (text is not ("true" or "yes" or "1" or "on" or "false" or "no" or "0" or "off"))
                    throw ValueError(c.Value!, $"{Name} is a checkbox: use true or false.");
                return v => v.BoolValue == true;
            }
        }
    }

    private static Expression<Func<CustomFieldValue, bool>> And(Expression<Func<CustomFieldValue, bool>> a, Expression<Func<CustomFieldValue, bool>> b)
    {
        var body = new Swap(b.Parameters[0], a.Parameters[0]).Visit(b.Body);
        return Expression.Lambda<Func<CustomFieldValue, bool>>(Expression.AndAlso(a.Body, body), a.Parameters);
    }

    private sealed class Swap(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }

    public override async Task<IReadOnlyList<GqlSuggestion>> SuggestValuesAsync(GqlContext ctx, string prefix)
    {
        var defs = await DefsAsync(ctx);
        var list = new List<GqlSuggestion>();
        foreach (var kind in defs.Select(d => KindOf(d.Type)).Distinct())
        {
            switch (kind)
            {
                case Kind.Select:
                    list.AddRange(defs.Where(d => KindOf(d.Type) == Kind.Select).SelectMany(d => d.Options).Distinct(StringComparer.OrdinalIgnoreCase)
                        .Where(o => o.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(o => new GqlSuggestion(GqlLexer.Quote(o), o)));
                    break;
                case Kind.User:
                    list.AddRange(await SuggestUsersAsync(ctx, prefix));
                    break;
                case Kind.Checkbox:
                    list.AddRange(new[] { "true", "false" }.Where(b => b.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(b => new GqlSuggestion(b, b)));
                    break;
                case Kind.Date:
                    list.AddRange(new[] { ("-7d", "a week ago"), ("startOfWeek()", "Monday"), ("startOfMonth()", (string?)null), (ctx.NowUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "today") }
                        .Where(s => s.Item1.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(s => new GqlSuggestion(s.Item1, s.Item1, s.Item2)));
                    break;
            }
        }
        return list;
    }
}
