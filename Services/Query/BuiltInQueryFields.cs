using System.Linq.Expressions;
using System.Text.RegularExpressions;
using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Query;

/// <summary>The fields GitBench itself defines. Other features add theirs with their own <see cref="IQueryFieldProvider"/>.</summary>
public sealed class BuiltInQueryFields : IQueryFieldProvider
{
    public IEnumerable<IQueryField> GetFields() =>
    [
        new ProjectField(),
        new KeyField(),
        new TextField("summary", ["title"], "The issue title", i => i.Title, nullable: false),
        new TextField("description", [], "The issue description", i => i.Description, nullable: true),
        new FullTextField(),
        new CommentField(),
        new EnumField<IssueType>("type", ["issuetype"], "Task, Bug, Feature, Story, Epic or Subtask", i => i.Type, ordered: false, t => t.ToString(),
            ("sub-task", IssueType.Subtask)),
        new StatusField(),
        new EnumField<IssueStatus>("statusCategory", ["category"], "Backlog, \"To Do\", \"In Progress\", \"In Review\" or Done", i => i.Status, ordered: false, s => s.Name()),
        new EnumField<IssuePriority>("priority", [], "Lowest … Highest; also <, >, <=, >=", i => i.Priority, ordered: true, p => p.ToString()),
        new UserField("assignee", "Who the issue is assigned to", i => i.AssigneeId, nullable: true, i => i.Assignee!.DisplayName),
        new UserField("reporter", "Who created the issue", i => (int?)i.ReporterId, nullable: false, i => i.Reporter.DisplayName),
        new WatcherField(),
        new LabelsField(),
        new MilestoneField(),
        new SprintField(),
        new ParentField(),
        new EpicField(),
        new DateField("created", ["createdDate"], "When the issue was created", i => (DateTime?)i.CreatedAt, nullable: false),
        new DateField("updated", ["updatedDate"], "When the issue last changed", i => (DateTime?)i.UpdatedAt, nullable: false),
        new DateField("resolved", ["resolutionDate", "closed"], "When the issue was last closed", i => i.ClosedAt, nullable: true),
        new DateField("due", ["dueDate"], "The due date", i => i.DueDate, nullable: true),
        new NumberField("storyPoints", ["points", "story points", "sp"], "Story points", i => i.StoryPoints),
    ];

    internal static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>The words of a ~ search (each must appear), at most 10.</summary>
    internal static List<string> Terms(GqlValue v)
    {
        var terms = v.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(10).ToList();
        if (terms.Count == 0) throw new GqlException("Give some text to search for.", v.Start, v.Length);
        return terms.Select(t => "%" + EscapeLike(t) + "%").ToList();
    }

    internal static Expression<Func<string?, bool>> Like(string pattern) => s => s != null && EF.Functions.Like(s, pattern, "\\");
}

// ----- Text -----

internal sealed class TextField(string name, string[] aliases, string description, Expression<Func<Issue, string?>> selector, bool nullable) : QueryField
{
    public override string Name => name;
    public override IReadOnlyList<string> Aliases => aliases;
    public override string Description => description + (nullable ? "; ~ contains, = exact, is EMPTY" : "; ~ contains, = exact");
    public override IReadOnlyCollection<GqlOperator> Operators =>
        nullable ? [.. GqlOperators.TextSearch, GqlOperator.Equals, GqlOperator.NotEquals, .. GqlOperators.Empty]
                 : [.. GqlOperators.TextSearch, GqlOperator.Equals, GqlOperator.NotEquals];
    public override IReadOnlyList<LambdaExpression> SortKeys => [selector];

    public override Task<Expression<Func<Issue, bool>>> BuildAsync(GqlClause c, GqlContext ctx)
    {
        Expression<Func<Issue, bool>> positive;
        switch (c.Operator)
        {
            case GqlOperator.Is or GqlOperator.IsNot:
                positive = Gx.Apply(selector, s => s == null || s == "");
                break;
            case GqlOperator.Equals or GqlOperator.NotEquals:
                var lower = Literal(c.Value!).ToLowerInvariant();
                positive = Gx.Apply(selector, s => s != null && s.ToLower() == lower);
                break;
            default:
                Literal(c.Value!);
                positive = BuiltInQueryFields.Terms(c.Value!).Select(p => Gx.Apply(selector, BuiltInQueryFields.Like(p))).Aggregate(Gx.And);
                break;
        }
        return Task.FromResult(Polarity(c.Operator, positive));
    }
}

/// <summary><c>text ~ "words"</c>: every word appears in the title, the description or a comment.</summary>
internal sealed class FullTextField : QueryField
{
    public override string Name => "text";
    public override string Description => "Title, description and comments; every word must appear";
    public override IReadOnlyCollection<GqlOperator> Operators => GqlOperators.TextSearch;

    public override Task<Expression<Func<Issue, bool>>> BuildAsync(GqlClause c, GqlContext ctx)
    {
        Literal(c.Value!);
        var positive = BuiltInQueryFields.Terms(c.Value!).Select(p => (Expression<Func<Issue, bool>>)(i =>
                EF.Functions.Like(i.Title, p, "\\") || (i.Description != null && EF.Functions.Like(i.Description, p, "\\"))
                || i.Comments.Any(x => EF.Functions.Like(x.Body, p, "\\"))))
            .Aggregate(Gx.And);
        return Task.FromResult(Polarity(c.Operator, positive));
    }
}

internal sealed class CommentField : QueryField
{
    public override string Name => "comment";
    public override string Description => "Comment text; every word must appear in one comment";
    public override IReadOnlyCollection<GqlOperator> Operators => GqlOperators.TextSearch;

    public override Task<Expression<Func<Issue, bool>>> BuildAsync(GqlClause c, GqlContext ctx)
    {
        Literal(c.Value!);
        var patterns = BuiltInQueryFields.Terms(c.Value!);
        var comment = patterns.Select(p => (Expression<Func<Comment, bool>>)(x => EF.Functions.Like(x.Body, p, "\\")))
            .Aggregate((a, b) => Expression.Lambda<Func<Comment, bool>>(Expression.AndAlso(a.Body, new ParamSwap(b.Parameters[0], a.Parameters[0]).Visit(b.Body)), a.Parameters));
        Expression<Func<Issue, bool>> positive = i => i.Comments.AsQueryable().Any(comment);
        return Task.FromResult(Polarity(c.Operator, positive));
    }

    private sealed class ParamSwap(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}

// ----- Choices -----

internal sealed class EnumField<T>(string name, string[] aliases, string description, Expression<Func<Issue, T>> selector, bool ordered,
    Func<T, string> display, params (string Name, T Value)[] extraNames) : QueryField where T : struct, Enum
{
    private static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private readonly Dictionary<string, T> names = Enum.GetValues<T>().Select(v => (Norm(v.ToString()), v))
        .Concat(Enum.GetValues<T>().Select(v => (Norm(display(v)), v)))
        .Concat(extraNames.Select(e => (Norm(e.Name), e.Value)))
        .DistinctBy(x => x.Item1).ToDictionary(x => x.Item1, x => x.Item2);

    public override string Name => name;
    public override IReadOnlyList<string> Aliases => aliases;
    public override string Description => description;
    public override IReadOnlyCollection<GqlOperator> Operators => ordered ? [.. GqlOperators.Equality, .. GqlOperators.Ordering] : GqlOperators.Equality;
    public override IReadOnlyList<LambdaExpression> SortKeys => [selector];

    private T Resolve(GqlValue v) => names.TryGetValue(Norm(Literal(v)), out var e) ? e
        : throw ValueError(v, $"'{v.Text}' is not a {name}. Use one of: {string.Join(", ", Enum.GetValues<T>().Select(x => GqlLexer.Quote(display(x))))}.");

    public override Task<Expression<Func<Issue, bool>>> BuildAsync(GqlClause c, GqlContext ctx)
    {
        if (GqlOperators.Ordering.Contains(c.Operator)) return Task.FromResult(Gx.Compare(selector, c.Operator, Resolve(c.Value!)));
        var set = c.Values.Select(Resolve).Distinct().ToList();
        return Task.FromResult(Polarity(c.Operator, Gx.Apply(selector, x => set.Contains(x))));
    }

    public override Task<IReadOnlyList<GqlSuggestion>> SuggestValuesAsync(GqlContext ctx, string prefix) =>
        Task.FromResult<IReadOnlyList<GqlSuggestion>>(Enum.GetValues<T>().Select(display)
            .Where(d => d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || Norm(d).StartsWith(Norm(prefix)))
            .Select(d => new GqlSuggestion(GqlLexer.Quote(d), d)).ToList());
}

/// <summary><c>status</c>: a workflow status by name (any project searched that has it); a category name also works.</summary>
internal sealed class StatusField : QueryField
{
    public override string Name => "status";
    public override string Description => "Workflow status name, e.g. \"In Progress\" or Blocked";
    public override IReadOnlyCollection<GqlOperator> Operators => GqlOperators.Equality;
    public override IReadOnlyList<LambdaExpression> SortKeys => [(Expression<Func<Issue, IssueStatus>>)(i => i.Status), (Expression<Func<Issue, int>>)(i => i.WorkflowStatus!.Order)];

    public override async Task<Expression<Func<Issue, bool>>> BuildAsync(GqlClause c, GqlContext ctx)
    {
        var names = c.Values.Select(v => Literal(v).Trim().ToLowerInvariant()).Distinct().ToList();
        var pids = ctx.ProjectIds;
        var found = await ctx.Db.WorkflowStatuses.AsNoTracking().Where(s => pids.Contains(s.ProjectId) && names.Contains(s.Name.ToLower()))
            .Select(s => new { s.Id, Name = s.Name.ToLower() }).ToListAsync();
        var ids = found.Select(f => f.Id).ToList();
        var categories = new List<IssueStatus>();
        foreach (var v in c.Values)
        {
            var n = v.Text.Trim().ToLowerInvariant();
            if (found.Any(f => f.Name == n)) continue;
            var cat = Enum.GetValues<IssueStatus>().Cast<IssueStatus?>()
                .FirstOrDefault(s => s!.Value.Name().Equals(v.Text.Trim(), StringComparison.OrdinalIgnoreCase) || s.Value.ToString().Equals(v.Text.Trim(), StringComparison.OrdinalIgnoreCase));
            if (cat is not { } category) throw ValueError(v, $"No project searched has a status named '{v.Text}'.");
            categories.Add(category);
        }
        Expression<Func<Issue, bool>> positive = i => (i.WorkflowStatusId != null && ids.Contains(i.WorkflowStatusId.Value)) || categories.Contains(i.Status);
        return Polarity(c.Operator, positive);
    }

    public override async Task<IReadOnlyList<GqlSuggestion>> SuggestValuesAsync(GqlContext ctx, string prefix)
    {
        var pids = ctx.ProjectIds;
        var p = prefix.ToLowerInvariant();
        var rows = await ctx.Db.WorkflowStatuses.AsNoTracking().Where(s => pids.Contains(s.ProjectId) && s.Name.ToLower().StartsWith(p))
            .OrderBy(s => s.Category).ThenBy(s => s.Order).Select(s => new { s.Name, s.Category }).ToListAsync();
        return rows.DistinctBy(r => r.Name.ToLowerInvariant()).Select(r => new GqlSuggestion(GqlLexer.Quote(r.Name), r.Name, r.Category.Name())).ToList();
    }
}

// ----- Projects & issues -----

internal sealed class ProjectField : QueryField
{
    public override string Name => "project";
    public override string Description => "Project key or name";
    public override IReadOnlyCollection<GqlOperator> Operators => GqlOperators.Equality;
    public override IReadOnlyList<LambdaExpression> SortKeys => Sort(i => i.Project.Key);

    public override async Task<Expression<Func<Issue, bool>>> BuildAsync(GqlClause c, GqlContext ctx)
    {
        var pids = ctx.ProjectIds;
        var ids = new List<int>();
        foreach (var v in c.Values)
        {
            var text = Literal(v).Trim();
            var (upper, lower) = (text.ToUpperInvariant(), text.ToLowerInvariant());
            var matches = await ctx.Db.Projects.AsNoTracking().Where(p => pids.Contains(p.Id) && (p.Key == upper || p.Name.ToLower() == lower))
                .Select(p => p.Id).ToListAsync();
            if (matches.Count == 0) throw ValueError(v, $"There is no project '{v.Text}' that you can see.");
            ids.AddRange(matches);
        }
        return Polarity(c.Operator, i => ids.Contains(i.ProjectId));
    }

    public override async Task<IReadOnlyList<GqlSuggestion>> SuggestValuesAsync(GqlContext ctx, string prefix)
    {
        var pids = ctx.ProjectIds;
        var (upper, lower) = (prefix.ToUpperInvariant(), prefix.ToLowerInvariant());
        var rows = await ctx.Db.Projects.AsNoTracking().Where(p => pids.Contains(p.Id) && (p.Key.StartsWith(upper) || p.Name.ToLower().Contains(lower)))
            .OrderBy(p => p.Key).Take(10).Select(p => new { p.Key, p.Name }).ToListAsync();
        return rows.Select(r => new GqlSuggestion(r.Key, r.Key, r.Name)).ToList();
    }
}

internal static partial class IssueKeys
{
    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9]{0,19})-(\d{1,9})$")]
    private static partial Regex KeyPattern();

    public static (string Key, int Number) Parse(GqlValue v)
    {
        var m = v.IsFunction ? Match.Empty : KeyPattern().Match(v.Text.Trim());
        if (!m.Success) throw new GqlException($"'{v.Text}' is not an issue key such as WEB-12.", v.Start, v.Length);
        return (m.Groups[1].Value.ToUpperInvariant(), int.Parse(m.Groups[2].Value));
    }

    /// <summary>The ids of issues with these keys among the projects searched; an unknown key is an error.</summary>
    public static async Task<List<int>> ResolveAsync(GqlContext ctx, IEnumerable<GqlValue> values)
    {
        var ids = new List<int>();
        var pids = ctx.ProjectIds;
        foreach (var v in values)
        {
            var (key, number) = Parse(v);
            var id = await ctx.Db.Issues.AsNoTracking().Where(i => pids.Contains(i.ProjectId) && i.Project.Key == key && i.Number == number)
                .Select(i => (int?)i.Id).FirstOrDefaultAsync();
            ids.Add(id ?? throw new GqlException($"There is no issue {key}-{number} that you can see.", v.Start, v.Length));
        }
        return ids;
    }
}

internal sealed class KeyField : QueryField
{
    public override string Name => "key";
    public override IReadOnlyList<string> Aliases => ["issueKey", "issue"];
    public override string Description => "Issue key, e.g. WEB-12; < and > compare numbers within a project";
    public override IReadOnlyCollection<GqlOperator> Operators => [.. GqlOperators.Equality, .. GqlOperators.Ordering];
    public override IReadOnlyList<LambdaExpression> SortKeys => [(Expression<Func<Issue, string>>)(i => i.Project.Key), (Expression<Func<Issue, int>>)(i => i.Number)];

    public override Task<Expression<Func<Issue, bool>>> BuildAsync(GqlClause c, GqlContext ctx)
    {
        if (GqlOperators.Ordering.Contains(c.Operator))
        {
            var (key, number) = IssueKeys.Parse(c.Value!);
            Expression<Func<Issue, bool>> sameProject = i => i.Project.Key == key;
            return Task.FromResult(Gx.And(sameProject, Gx.Compare(i => i.Number, c.Operator, number)));
        }
        var parts = c.Values.Select(IssueKeys.Parse).Distinct()
            .Select(k => (Expression<Func<Issue, bool>>)(i => i.Project.Key == k.Key && i.Number == k.Number));
        return Task.FromResult(Polarity(c.Operator, Gx.Any(parts)));
    }
}

internal sealed class ParentField : QueryField
{
    public override string Name => "parent";
    public override string Description => "The parent issue's key (an epic, or a sub-task's parent)";
    public override IReadOnlyCollection<GqlOperator> Operators => [.. GqlOperators.Equality, .. GqlOperators.Empty];

    public override async Task<Expression<Func<Issue, bool>>> BuildAsync(GqlClause c, GqlContext ctx)
    {
        if (c.Operator is GqlOperator.Is or GqlOperator.IsNot) return Polarity(c.Operator, i => i.ParentId == null);
        var ids = await IssueKeys.ResolveAsync(ctx, c.Values);
        return Polarity(c.Operator, i => i.ParentId != null && ids.Contains(i.ParentId.Value));
    }
}

/// <summary><c>epic = WEB-9</c>: the epic's children and their sub-tasks.</summary>
internal sealed class EpicField : QueryField
{
    public override string Name => "epic";
    public override IReadOnlyList<string> Aliases => ["epicLink"];
    public override string Description => "An epic's key: its children and their sub-tasks";
    public override IReadOnlyCollection<GqlOperator> Operators => [.. GqlOperators.Equality, .. GqlOperators.Empty];

    public override async Task<Expression<Func<Issue, bool>>> BuildAsync(GqlClause c, GqlContext ctx)
    {
        if (c.Operator is GqlOperator.Is or GqlOperator.IsNot)
            return Polarity(c.Operator, i => !(i.Parent != null && (i.Parent.Type == IssueType.Epic || (i.Parent.Parent != null && i.Parent.Parent.Type == IssueType.Epic))));
        var ids = await IssueKeys.ResolveAsync(ctx, c.Values);
        return Polarity(c.Operator, i => i.ParentId != null && (ids.Contains(i.ParentId.Value) || (i.Parent!.ParentId != null && ids.Contains(i.Parent.ParentId.Value))));
    }

    public override async Task<IReadOnlyList<GqlSuggestion>> SuggestValuesAsync(GqlContext ctx, string prefix)
    {
        var pids = ctx.ProjectIds;
        var p = prefix.ToLowerInvariant();
        var rows = await ctx.Db.Issues.AsNoTracking().Where(i => pids.Contains(i.ProjectId) && i.Type == IssueType.Epic && i.Status != IssueStatus.Done
                && ((i.Project.Key + "-" + i.Number).ToLower().StartsWith(p) || i.Title.ToLower().Contains(p)))
            .OrderByDescending(i => i.UpdatedAt).Take(10).Select(i => new { i.Project.Key, i.Number, i.Title }).ToListAsync();
        return rows.Select(r => new GqlSuggestion($"{r.Key}-{r.Number}", $"{r.Key}-{r.Number}", r.Title)).ToList();
    }
}

// ----- People -----

internal sealed class UserField(string name, string description, Expression<Func<Issue, int?>> selector, bool nullable, Expression<Func<Issue, string>> sortKey) : QueryField
{
    public override string Name => name;
    public override string Description => description + ": username, email or currentUser()";
    public override IReadOnlyCollection<GqlOperator> Operators => nullable ? [.. GqlOperators.Equality, .. GqlOperators.Empty] : GqlOperators.Equality;
    public override IReadOnlyList<LambdaExpression> SortKeys => [sortKey];

    public override async Task<Expression<Func<Issue, bool>>> BuildAsync(GqlClause c, GqlContext ctx)
    {
        if (c.Operator is GqlOperator.Is or GqlOperator.IsNot) return Polarity(c.Operator, Gx.Apply(selector, x => x == null));
        var ids = new List<int>();
        foreach (var v in c.Values) ids.Add(await ResolveUserAsync(v, ctx, name));
        return Polarity(c.Operator, Gx.Apply(selector, x => x != null && ids.Contains(x.Value)));
    }

    public override Task<IReadOnlyList<GqlSuggestion>> SuggestValuesAsync(GqlContext ctx, string prefix) => SuggestUsersAsync(ctx, prefix);
}

internal sealed class WatcherField : QueryField
{
    public override string Name => "watcher";
    public override IReadOnlyList<string> Aliases => ["watchers"];
    public override string Description => "Someone watching the issue: username, email or currentUser()";
    public override IReadOnlyCollection<GqlOperator> Operators => [.. GqlOperators.Equality, .. GqlOperators.Empty];

    public override async Task<Expression<Func<Issue, bool>>> BuildAsync(GqlClause c, GqlContext ctx)
    {
        var db = ctx.Db;
        if (c.Operator is GqlOperator.Is or GqlOperator.IsNot) return Polarity(c.Operator, i => !db.Watches.Any(w => w.IssueId == i.Id));
        var ids = new List<int>();
        foreach (var v in c.Values) ids.Add(await ResolveUserAsync(v, ctx, Name));
        return Polarity(c.Operator, i => db.Watches.Any(w => w.IssueId == i.Id && ids.Contains(w.UserId)));
    }

    public override Task<IReadOnlyList<GqlSuggestion>> SuggestValuesAsync(GqlContext ctx, string prefix) => SuggestUsersAsync(ctx, prefix);
}

// ----- Planning -----

internal sealed class LabelsField : QueryField
{
    public override string Name => "labels";
    public override IReadOnlyList<string> Aliases => ["label"];
    public override string Description => "Label names; = matches issues that have the label";
    public override IReadOnlyCollection<GqlOperator> Operators => [.. GqlOperators.Equality, .. GqlOperators.Empty];

    public override Task<Expression<Func<Issue, bool>>> BuildAsync(GqlClause c, GqlContext ctx)
    {
        if (c.Operator is GqlOperator.Is or GqlOperator.IsNot) return Task.FromResult(Polarity(c.Operator, i => !i.Labels.Any()));
        var names = c.Values.Select(v => Literal(v).Trim().ToLowerInvariant()).Distinct().ToList();
        return Task.FromResult(Polarity(c.Operator, i => i.Labels.Any(l => names.Contains(l.Name.ToLower()))));
    }

    public override async Task<IReadOnlyList<GqlSuggestion>> SuggestValuesAsync(GqlContext ctx, string prefix)
    {
        var pids = ctx.ProjectIds;
        var p = prefix.ToLowerInvariant();
        var groupIds = new List<int>();
        foreach (var g in await ctx.Db.Projects.Where(x => pids.Contains(x.Id) && x.GroupId != null).Select(x => x.GroupId!.Value).Distinct().ToListAsync())
            groupIds.AddRange(await ProjectAccess.GroupChainAsync(ctx.Db, g));
        var names = await ctx.Db.Labels.AsNoTracking()
            .Where(l => ((l.ProjectId != null && pids.Contains(l.ProjectId.Value)) || (l.GroupId != null && groupIds.Contains(l.GroupId.Value))) && l.Name.ToLower().StartsWith(p))
            .Select(l => l.Name).Distinct().OrderBy(n => n).Take(15).ToListAsync();
        return names.Select(n => new GqlSuggestion(GqlLexer.Quote(n), n)).ToList();
    }
}

internal sealed class MilestoneField : QueryField
{
    public override string Name => "milestone";
    public override IReadOnlyList<string> Aliases => ["fixVersion"];
    public override string Description => "Milestone title";
    public override IReadOnlyCollection<GqlOperator> Operators => [.. GqlOperators.Equality, .. GqlOperators.Empty];
    public override IReadOnlyList<LambdaExpression> SortKeys => Sort(i => i.Milestone!.Title);

    public override Task<Expression<Func<Issue, bool>>> BuildAsync(GqlClause c, GqlContext ctx)
    {
        if (c.Operator is GqlOperator.Is or GqlOperator.IsNot) return Task.FromResult(Polarity(c.Operator, i => i.MilestoneId == null));
        var titles = c.Values.Select(v => Literal(v).Trim().ToLowerInvariant()).Distinct().ToList();
        return Task.FromResult(Polarity(c.Operator, i => i.Milestone != null && titles.Contains(i.Milestone.Title.ToLower())));
    }

    public override async Task<IReadOnlyList<GqlSuggestion>> SuggestValuesAsync(GqlContext ctx, string prefix)
    {
        var pids = ctx.ProjectIds;
        var p = prefix.ToLowerInvariant();
        var rows = await ctx.Db.Milestones.AsNoTracking()
            .Where(m => ((m.ProjectId != null && pids.Contains(m.ProjectId.Value)) || m.Issues.Any(i => pids.Contains(i.ProjectId))) && m.Title.ToLower().Contains(p))
            .OrderBy(m => m.IsClosed).ThenBy(m => m.Title).Take(15).Select(m => new { m.Title, m.IsClosed }).ToListAsync();
        return rows.DistinctBy(r => r.Title).Select(r => new GqlSuggestion(GqlLexer.Quote(r.Title), r.Title, r.IsClosed ? "closed" : null)).ToList();
    }
}

internal sealed class SprintField : QueryField
{
    public override string Name => "sprint";
    public override string Description => "Sprint name or id, openSprints() (active), futureSprints() or closedSprints()";
    public override IReadOnlyCollection<GqlOperator> Operators => [.. GqlOperators.Equality, .. GqlOperators.Empty];
    public override IReadOnlyList<LambdaExpression> SortKeys => Sort(i => i.Sprint!.Name);

    private static readonly Dictionary<string, SprintState> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["openSprints"] = SprintState.Active, ["futureSprints"] = SprintState.Planned, ["closedSprints"] = SprintState.Completed,
    };

    public override async Task<Expression<Func<Issue, bool>>> BuildAsync(GqlClause c, GqlContext ctx)
    {
        if (c.Operator is GqlOperator.Is or GqlOperator.IsNot) return Polarity(c.Operator, i => i.SprintId == null);
        var pids = ctx.ProjectIds;
        var states = new List<SprintState>();
        var ids = new List<int>();
        foreach (var v in c.Values)
        {
            if (v.IsFunction)
            {
                if (v.Args.Count > 0 || !Functions.TryGetValue(v.Text, out var state))
                    throw ValueError(v, $"Unknown sprint function {v.Text}(). Use openSprints(), futureSprints() or closedSprints().");
                states.Add(state);
                continue;
            }
            var lower = v.Text.Trim().ToLowerInvariant();
            var isId = int.TryParse(v.Text, out var sid);
            var matches = await ctx.Db.Sprints.AsNoTracking().Where(s => pids.Contains(s.ProjectId) && (s.Name.ToLower() == lower || (isId && s.Id == sid)))
                .Select(s => s.Id).ToListAsync();
            if (matches.Count == 0) throw ValueError(v, $"No project searched has a sprint '{v.Text}'.");
            ids.AddRange(matches);
        }
        return Polarity(c.Operator, i => i.SprintId != null && (ids.Contains(i.SprintId.Value) || states.Contains(i.Sprint!.State)));
    }

    public override async Task<IReadOnlyList<GqlSuggestion>> SuggestValuesAsync(GqlContext ctx, string prefix)
    {
        var list = Functions.Keys.Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(f => new GqlSuggestion(f + "()", f + "()", Functions[f] switch { SprintState.Active => "the active sprint", SprintState.Planned => "planned sprints", _ => "completed sprints" }, "function"))
            .ToList();
        var pids = ctx.ProjectIds;
        var p = prefix.ToLowerInvariant();
        var rows = await ctx.Db.Sprints.AsNoTracking().Where(s => pids.Contains(s.ProjectId) && s.Name.ToLower().Contains(p))
            .OrderBy(s => s.State == SprintState.Completed).ThenByDescending(s => s.CreatedAt).Take(10).Select(s => new { s.Name, s.State }).ToListAsync();
        list.AddRange(rows.Select(r => new GqlSuggestion(GqlLexer.Quote(r.Name), r.Name, r.State.ToString().ToLowerInvariant())));
        return list;
    }
}

// ----- Dates & numbers -----

internal sealed class DateField(string name, string[] aliases, string description, Expression<Func<Issue, DateTime?>> selector, bool nullable) : QueryField
{
    public override string Name => name;
    public override IReadOnlyList<string> Aliases => aliases;
    public override string Description => description + ": 2026-01-31, -7d, startOfWeek()…";
    public override IReadOnlyCollection<GqlOperator> Operators =>
        nullable ? [GqlOperator.Equals, GqlOperator.NotEquals, .. GqlOperators.Ordering, .. GqlOperators.Empty] : [GqlOperator.Equals, GqlOperator.NotEquals, .. GqlOperators.Ordering];
    public override IReadOnlyList<LambdaExpression> SortKeys => [selector];

    public override Task<Expression<Func<Issue, bool>>> BuildAsync(GqlClause c, GqlContext ctx)
    {
        if (c.Operator is GqlOperator.Is or GqlOperator.IsNot) return Task.FromResult(Polarity(c.Operator, Gx.Apply(selector, d => d == null)));
        var date = ParseDate(c.Value!, ctx.NowUtc);
        DateTime? start = date.Value, next = date.Value.AddDays(1);
        Expression<Func<Issue, bool>> result = (c.Operator, date.IsDay) switch
        {
            (GqlOperator.Equals or GqlOperator.NotEquals, true) => Polarity(c.Operator, Gx.Apply(selector, d => d >= start && d < next)),
            (GqlOperator.LessOrEqual, true) => Gx.Compare(selector, GqlOperator.LessThan, next),
            (GqlOperator.GreaterThan, true) => Gx.Compare(selector, GqlOperator.GreaterOrEqual, next),
            _ => Gx.Compare(selector, c.Operator, start),
        };
        return Task.FromResult(result);
    }

    public override Task<IReadOnlyList<GqlSuggestion>> SuggestValuesAsync(GqlContext ctx, string prefix)
    {
        GqlSuggestion[] all =
        [
            new("-1d", "-1d", "a day ago"), new("-7d", "-7d", "a week ago"), new("-2w", "-2w", "two weeks ago"), new("-30d", "-30d", "30 days ago"),
            new("startOfDay()", "startOfDay()", "today 00:00 UTC", "function"), new("startOfWeek()", "startOfWeek()", "Monday", "function"),
            new("startOfMonth()", "startOfMonth()", null, "function"), new("startOfYear()", "startOfYear()", null, "function"),
            new("endOfWeek()", "endOfWeek()", "next Monday", "function"), new("now()", "now()", null, "function"),
            new(ctx.NowUtc.ToString("yyyy-MM-dd"), ctx.NowUtc.ToString("yyyy-MM-dd"), "today"),
        ];
        return Task.FromResult<IReadOnlyList<GqlSuggestion>>(all.Where(s => s.Insert.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList());
    }
}

internal sealed class NumberField(string name, string[] aliases, string description, Expression<Func<Issue, int?>> selector) : QueryField
{
    public override string Name => name;
    public override IReadOnlyList<string> Aliases => aliases;
    public override string Description => description;
    public override IReadOnlyCollection<GqlOperator> Operators => [.. GqlOperators.Equality, .. GqlOperators.Ordering, .. GqlOperators.Empty];
    public override IReadOnlyList<LambdaExpression> SortKeys => [selector];

    private int? Number(GqlValue v) => int.TryParse(Literal(v), out var n) ? n : throw ValueError(v, $"'{v.Text}' is not a whole number.");

    public override Task<Expression<Func<Issue, bool>>> BuildAsync(GqlClause c, GqlContext ctx)
    {
        if (c.Operator is GqlOperator.Is or GqlOperator.IsNot) return Task.FromResult(Polarity(c.Operator, Gx.Apply(selector, x => x == null)));
        if (c.Operator is GqlOperator.In or GqlOperator.NotIn)
        {
            var set = c.Values.Select(Number).ToList();
            return Task.FromResult(Polarity(c.Operator, Gx.Apply(selector, x => x != null && set.Contains(x))));
        }
        return Task.FromResult(Gx.Compare(selector, c.Operator, Number(c.Value!)));
    }
}
