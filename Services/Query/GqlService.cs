using System.Linq.Expressions;
using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Query;

/// <summary>One page of GQL results. <see cref="Total"/> counts every match.</summary>
public sealed record GqlSearchResult(List<Issue> Issues, int Total);

/// <summary>Where the autocomplete inserts: the text between <see cref="Start"/> and <see cref="End"/> is replaced.</summary>
public sealed record GqlCompletion(int Start, int End, IReadOnlyList<GqlSuggestion> Items)
{
    public static readonly GqlCompletion None = new(0, 0, []);
}

/// <summary>
/// Runs GitBench Query Language (GQL) searches. Every query is limited to the projects the user can view (and to
/// <c>scope</c> when given) before any field is translated, and values are always EF parameters (never SQL text).
/// <para>
/// For other features (automation rules, boards…): <see cref="ApplyAsync"/> filters and orders any
/// <c>IQueryable&lt;Issue&gt;</c> of a context you own, and <see cref="MatchesAsync(int, string, int?)"/> evaluates a
/// query against a single issue. Both throw <see cref="GqlException"/> for invalid queries; call
/// <see cref="ValidateAsync"/> first to check one when it is saved.
/// </para>
/// </summary>
public sealed class GqlService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, GqlFieldRegistry fields, TimeProvider clock)
{
    public const int MaxPageSize = 1000;

    public GqlFieldRegistry Fields => fields;

    /// <summary>Parses and translates without running; returns the error or null if the query is valid.</summary>
    public async Task<GqlException?> ValidateAsync(string gql, IReadOnlyCollection<int>? scope = null, int? userId = null)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            await ApplyAsync(db, db.Issues, gql, scope, userId);
            return null;
        }
        catch (GqlException ex) { return ex; }
    }

    /// <summary>A page of matching issues (with project, assignee, labels, milestone and workflow status), in the query's order.</summary>
    public async Task<GqlSearchResult> SearchAsync(string gql, IReadOnlyCollection<int>? scope = null, int skip = 0, int take = 100, int? userId = null)
    {
        take = Math.Clamp(take, 1, MaxPageSize);
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = await ApplyAsync(db, db.Issues.AsNoTracking(), gql, scope, userId);
        var total = await q.CountAsync();
        var page = await q.Skip(Math.Max(0, skip)).Take(take)
            .Include(i => i.Project).Include(i => i.Assignee).Include(i => i.Labels).Include(i => i.Milestone).Include(i => i.WorkflowStatus)
            .AsSplitQuery().ToListAsync();
        return new GqlSearchResult(await HierarchyRules.AnnotateAsync(db, access, page), total);
    }

    /// <summary>The ids of every matching issue, in order (at most <paramref name="max"/>).</summary>
    public async Task<List<int>> SearchIdsAsync(string gql, IReadOnlyCollection<int>? scope = null, int max = 10_000, int? userId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = await ApplyAsync(db, db.Issues.AsNoTracking(), gql, scope, userId);
        return await q.Select(i => i.Id).Take(max).ToListAsync();
    }

    /// <summary>True if the issue is visible to the user and matches the query (ORDER BY is ignored).</summary>
    public async Task<bool> MatchesAsync(int issueId, string gql, int? userId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = await ApplyAsync(db, db.Issues.Where(i => i.Id == issueId), gql, null, userId, order: false);
        return await q.AnyAsync();
    }

    public Task<bool> MatchesAsync(Issue issue, string gql, int? userId = null) => MatchesAsync(issue.Id, gql, userId);

    /// <summary>
    /// Filters <paramref name="source"/> (a query on <paramref name="db"/>) to the issues matching <paramref name="gql"/> that the
    /// user can view (within <paramref name="scope"/> if given), ordered by its ORDER BY (default: updated, newest first).
    /// </summary>
    public async Task<IQueryable<Issue>> ApplyAsync(AppDbContext db, IQueryable<Issue> source, string gql, IReadOnlyCollection<int>? scope = null,
        int? userId = null, bool order = true)
    {
        var query = GqlParser.Parse(gql);
        var ctx = await ContextAsync(db, scope, userId);
        var pids = ctx.ProjectIds;
        var q = source.Where(i => pids.Contains(i.ProjectId));
        if (query.Where != null) q = q.Where(await TranslateAsync(query.Where, ctx));
        return order ? Order(q, query.OrderBy) : q;
    }

    private async Task<GqlContext> ContextAsync(AppDbContext db, IReadOnlyCollection<int>? scope, int? userId)
    {
        var visible = await access.VisibleProjectIdsAsync(userId);
        var pids = scope == null ? visible.ToList() : visible.Where(scope.Contains).ToList();
        return new GqlContext(db, await access.ResolveUserIdAsync(userId), clock.GetUtcNow().UtcDateTime, pids);
    }

    private async Task<Expression<Func<Issue, bool>>> TranslateAsync(GqlNode node, GqlContext ctx) => node switch
    {
        GqlAnd a => Gx.And(await TranslateAsync(a.Left, ctx), await TranslateAsync(a.Right, ctx)),
        GqlOr o => Gx.Or(await TranslateAsync(o.Left, ctx), await TranslateAsync(o.Right, ctx)),
        GqlNot n => Gx.Not(await TranslateAsync(n.Inner, ctx)),
        GqlClause c => await TranslateClauseAsync(c, ctx),
        _ => throw new GqlException("Unsupported expression.", node.Start, node.Length),
    };

    private async Task<Expression<Func<Issue, bool>>> TranslateClauseAsync(GqlClause c, GqlContext ctx)
    {
        var field = fields.Find(c.Field) ?? throw new GqlException($"Unknown field '{c.Field}'.{Hint(c.Field)}", c.FieldStart, c.FieldLength);
        if (!field.Operators.Contains(c.Operator))
            throw new GqlException($"{field.Name} doesn't support '{c.Operator.Text()}'. Use {string.Join(", ", field.Operators.Select(o => o.Text()))}.",
                c.OperatorStart, c.OperatorLength);
        return await field.BuildAsync(c, ctx);
    }

    private string Hint(string name)
    {
        var close = fields.Fields.Select(f => f.Name)
            .OrderBy(n => Distance(n.ToLowerInvariant(), name.ToLowerInvariant())).First();
        return Distance(close.ToLowerInvariant(), name.ToLowerInvariant()) <= 3 ? $" Did you mean {close}?" : "";
    }

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    private IQueryable<Issue> Order(IQueryable<Issue> q, IReadOnlyList<GqlSort> sorts)
    {
        var first = true;
        foreach (var s in sorts)
        {
            var field = fields.Find(s.Field) ?? throw new GqlException($"Unknown field '{s.Field}'.{Hint(s.Field)}", s.Start, s.Field.Length);
            if (field.SortKeys.Count == 0) throw new GqlException($"Results can't be sorted by {field.Name}.", s.Start, s.Length);
            foreach (var key in field.SortKeys)
            {
                q = OrderBy(q, key, s.Descending, first);
                first = false;
            }
        }
        if (first) q = q.OrderByDescending(i => i.UpdatedAt);
        return OrderBy(q, (Expression<Func<Issue, int>>)(i => i.Id), true, false); // stable pages
    }

    private static IQueryable<Issue> OrderBy(IQueryable<Issue> q, LambdaExpression key, bool desc, bool first)
    {
        var method = (first, desc) switch
        {
            (true, false) => nameof(Queryable.OrderBy),
            (true, true) => nameof(Queryable.OrderByDescending),
            (false, false) => nameof(Queryable.ThenBy),
            _ => nameof(Queryable.ThenByDescending),
        };
        var call = Expression.Call(typeof(Queryable), method, [typeof(Issue), key.ReturnType], q.Expression, Expression.Quote(key));
        return q.Provider.CreateQuery<Issue>(call);
    }

    // ----- Autocomplete -----

    private enum Expect { Field, Operator, Value, InOpen, ListValue, ListNext, AfterValue, By, SortField, AfterSortField, AfterSortDir, IsEmpty, IsNotEmpty, NotIn, None }

    /// <summary>Suggestions for the word at <paramref name="caret"/>: fields, operators, values (statuses, users, labels…) or keywords.</summary>
    public async Task<GqlCompletion> CompleteAsync(string text, int caret, IReadOnlyCollection<int>? scope = null, int? userId = null)
    {
        text ??= "";
        if (text.Length > GqlLexer.MaxLength) return GqlCompletion.None;
        caret = Math.Clamp(caret, 0, text.Length);
        var tokens = GqlLexer.Tokenize(text[..caret], lenient: true);
        tokens.RemoveAt(tokens.Count - 1); // End
        string partial = "";
        var start = caret;
        if (tokens.Count > 0 && tokens[^1].End == caret && tokens[^1].Kind is GqlTokenKind.Word or GqlTokenKind.String or GqlTokenKind.Op)
        {
            var last = tokens[^1];
            if (last.Kind == GqlTokenKind.String && !last.Unterminated) return GqlCompletion.None; // just closed a quote
            partial = last.Text;
            start = last.Start;
            tokens.RemoveAt(tokens.Count - 1);
        }

        var (expect, field) = Walk(tokens);
        var items = new List<GqlSuggestion>();
        void Keywords(params (string Insert, string? Detail)[] words) =>
            items.AddRange(words.Where(w => w.Insert.StartsWith(partial, StringComparison.OrdinalIgnoreCase)).Select(w => new GqlSuggestion(w.Insert, w.Insert, w.Detail, "keyword")));

        switch (expect)
        {
            case Expect.Field:
                items.AddRange(fields.Fields.Where(f => Prefix(f, partial)).Select(f => new GqlSuggestion(GqlLexer.Quote(f.Name), f.Name, f.Description, "field")));
                if (partial.Length > 0) Keywords(("NOT", "negates the next condition"));
                break;
            case Expect.Operator:
                var f = field == null ? null : fields.Find(field);
                var ops = f?.Operators ?? Enum.GetValues<GqlOperator>();
                items.AddRange(ops.Select(o => o switch
                    {
                        GqlOperator.Is => "is EMPTY",
                        GqlOperator.IsNot => "is not EMPTY",
                        GqlOperator.In => "in (",
                        GqlOperator.NotIn => "not in (",
                        _ => o.Text(),
                    })
                    .Where(o => o.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                    .Select(o => new GqlSuggestion(o, o, OperatorHelp(o), "operator")));
                break;
            case Expect.Value or Expect.ListValue:
                if (field != null && fields.Find(field) is { } vf)
                {
                    await using var db = await dbFactory.CreateDbContextAsync();
                    var ctx = await ContextAsync(db, scope, userId);
                    items.AddRange(await vf.SuggestValuesAsync(ctx, partial));
                }
                if (expect == Expect.Value && (field == null || fields.Find(field)?.Operators.Contains(GqlOperator.Is) == true)) Keywords(("EMPTY", "no value"));
                break;
            case Expect.InOpen: Keywords(("(", null)); break;
            case Expect.ListNext: Keywords((",", null), (")", null)); break;
            case Expect.AfterValue: Keywords(("AND", null), ("OR", null), ("ORDER BY", "sort the results")); break;
            case Expect.By: Keywords(("BY", null)); break;
            case Expect.SortField:
                items.AddRange(fields.Fields.Where(x => x.SortKeys.Count > 0 && Prefix(x, partial)).Select(x => new GqlSuggestion(GqlLexer.Quote(x.Name), x.Name, null, "field")));
                break;
            case Expect.AfterSortField: Keywords(("DESC", "newest / highest first"), ("ASC", "oldest / lowest first"), (",", "then by")); break;
            case Expect.AfterSortDir: Keywords((",", "then by")); break;
            case Expect.IsEmpty: Keywords(("EMPTY", null), ("NOT", null)); break;
            case Expect.IsNotEmpty: Keywords(("EMPTY", null)); break;
            case Expect.NotIn: Keywords(("IN", null)); break;
        }
        // A quoted word in progress is replaced together with its opening quote.
        return items.Count == 0 ? GqlCompletion.None : new GqlCompletion(start, caret, items.Take(15).ToList());
    }

    private static bool Prefix(IQueryField f, string partial) =>
        partial.Length == 0 || f.Aliases.Prepend(f.Name).Any(n => n.StartsWith(partial, StringComparison.OrdinalIgnoreCase));

    private static string? OperatorHelp(string op) => op switch
    {
        "=" => "is", "!=" => "is not", "~" => "contains", "!~" => "doesn't contain", "in (" => "any of", "not in (" => "none of",
        "is EMPTY" => "has no value", "is not EMPTY" => "has a value", _ => null,
    };

    /// <summary>What may come after <paramref name="tokens"/>, and the field of the clause being written.</summary>
    private static (Expect, string?) Walk(List<GqlToken> tokens)
    {
        var expect = Expect.Field;
        string? field = null;
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            expect = expect switch
            {
                Expect.Field when t.Is("NOT") || t.Kind == GqlTokenKind.LParen => Expect.Field,
                Expect.Field when t.Is("ORDER") => Expect.By,
                Expect.Field when t.IsValueToken => SetField(t.Text, Expect.Operator),
                Expect.Operator when t.Kind == GqlTokenKind.Op => Expect.Value,
                Expect.Operator when t.Is("IN") => Expect.InOpen,
                Expect.Operator when t.Is("NOT") => Expect.NotIn,
                Expect.Operator when t.Is("IS") => Expect.IsEmpty,
                Expect.NotIn when t.Is("IN") => Expect.InOpen,
                Expect.IsEmpty when t.Is("NOT") => Expect.IsNotEmpty,
                Expect.IsEmpty or Expect.IsNotEmpty when t.Is("EMPTY") || t.Is("NULL") => Expect.AfterValue,
                Expect.InOpen when t.Kind == GqlTokenKind.LParen => Expect.ListValue,
                Expect.Value when t.IsValueToken => SkipCall(ref i, Expect.AfterValue),
                Expect.ListValue when t.IsValueToken => SkipCall(ref i, Expect.ListNext),
                Expect.ListNext when t.Kind == GqlTokenKind.Comma => Expect.ListValue,
                Expect.ListNext when t.Kind == GqlTokenKind.RParen => Expect.AfterValue,
                Expect.AfterValue when t.Is("AND") || t.Is("OR") => Expect.Field,
                Expect.AfterValue when t.Kind == GqlTokenKind.RParen => Expect.AfterValue,
                Expect.AfterValue when t.Is("ORDER") => Expect.By,
                Expect.By when t.Is("BY") => Expect.SortField,
                Expect.SortField when t.IsValueToken => Expect.AfterSortField,
                Expect.AfterSortField when t.Is("ASC") || t.Is("DESC") => Expect.AfterSortDir,
                Expect.AfterSortField or Expect.AfterSortDir when t.Kind == GqlTokenKind.Comma => Expect.SortField,
                _ => Expect.None,
            };
            if (expect == Expect.None) return (Expect.None, null);
        }
        return (expect, field);

        Expect SetField(string name, Expect next) { field = name; return next; }

        // A function call value: skip "(" … ")".
        Expect SkipCall(ref int i, Expect next)
        {
            if (i + 1 >= tokens.Count || tokens[i + 1].Kind != GqlTokenKind.LParen) return next;
            var depth = 0;
            for (i++; i < tokens.Count; i++)
            {
                if (tokens[i].Kind == GqlTokenKind.LParen) depth++;
                else if (tokens[i].Kind == GqlTokenKind.RParen && --depth == 0) return next;
            }
            return Expect.None; // inside the call's arguments
        }
    }

    // ----- Basic filter → GQL -----

    /// <summary>The GQL equivalent of a basic issue-list filter in project <paramref name="projectId"/> ("Switch to advanced").</summary>
    public async Task<string> FromFilterAsync(int projectId, IssueFilter f)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var key = await db.Projects.Where(p => p.Id == projectId).Select(p => p.Key).FirstOrDefaultAsync() ?? "";
        var parts = new List<string> { $"project = {GqlLexer.Quote(key)}" };
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var s = f.Search.Trim();
            parts.Add(System.Text.RegularExpressions.Regex.IsMatch(s, @"^[A-Za-z][A-Za-z0-9]*-\d+$") ? $"key = {s.ToUpperInvariant()}" : $"text ~ {GqlLexer.Quote(s)}");
        }
        if (f.WorkflowStatusId is { } ws && await db.WorkflowStatuses.Where(x => x.Id == ws).Select(x => x.Name).FirstOrDefaultAsync() is { } wsName)
            parts.Add($"status = {GqlLexer.Quote(wsName)}");
        if (f.Status is { } st) parts.Add($"statusCategory = {GqlLexer.Quote(st.Name())}");
        else if (f.WorkflowStatusId == null && f.Open == true) parts.Add("statusCategory != Done");
        else if (f.WorkflowStatusId == null && f.Open == false) parts.Add("statusCategory = Done");
        if (f.AssigneeId is { } a)
            parts.Add(a == 0 ? "assignee is EMPTY" : $"assignee = {GqlLexer.Quote(await db.Users.Where(u => u.Id == a).Select(u => u.UserName).FirstOrDefaultAsync() ?? a.ToString())}");
        if (f.LabelId is { } l && await db.Labels.Where(x => x.Id == l).Select(x => x.Name).FirstOrDefaultAsync() is { } label)
            parts.Add($"labels = {GqlLexer.Quote(label)}");
        if (f.MilestoneId is { } m)
            parts.Add(m == 0 ? "milestone is EMPTY"
                : await db.Milestones.Where(x => x.Id == m).Select(x => x.Title).FirstOrDefaultAsync() is { } title ? $"milestone = {GqlLexer.Quote(title)}" : "");
        if (f.SprintId is { } sp) parts.Add(sp == 0 ? "sprint is EMPTY" : $"sprint = {sp}");
        if (f.Type is { } t) parts.Add($"type = {t}");
        if (f.Priority is { } p) parts.Add($"priority = {p}");
        if (f.EpicId is { } e && await db.Issues.Where(i => i.Id == e).Select(i => i.Project.Key + "-" + i.Number).FirstOrDefaultAsync() is { } epicKey)
            parts.Add($"epic = {epicKey}");
        if (f.HideSubtasks) parts.Add("type != Subtask");
        var order = f.Sort switch
        {
            "created" => "created DESC",
            "priority" => "priority DESC, updated DESC",
            "due" => "due ASC",
            "number" => "key ASC",
            _ => "updated DESC",
        };
        return string.Join(" AND ", parts.Where(x => x.Length > 0)) + " ORDER BY " + order;
    }
}
