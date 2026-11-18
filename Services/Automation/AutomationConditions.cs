using GitBench.Data;
using GitBench.Services.Git;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Automation;

/// <summary>What kind of operand a condition takes (tells the rule editor which input to show).</summary>
public enum ConditionValueKind { None, IssueType, Priority, Status, StatusCategory, Label, User, Text }

/// <summary>An operator of a condition, e.g. ("is", "is", needs a value) or ("empty", "is empty", no value).</summary>
public sealed record ConditionOperator(string Key, string Label, bool NeedsValue = true);

/// <summary>
/// A condition an automation rule can check. Implementations are registered in DI (<c>AddAutomation</c>) and found by
/// <see cref="Field"/>, so a new kind of condition (e.g. "matches a query") is one class plus one registration line. Conditions
/// that look at an issue are false when the trigger has none.
/// </summary>
public interface IAutomationCondition
{
    /// <summary>The key stored in <see cref="ConditionDef.Field"/>.</summary>
    string Field { get; }
    string Label { get; }
    IReadOnlyList<ConditionOperator> Operators { get; }
    ConditionValueKind ValueKind { get; }
    Task<bool> EvaluateAsync(AutomationContext ctx, ConditionDef condition, CancellationToken ct);
}

/// <summary>Base for conditions on a field of the current issue.</summary>
public abstract class IssueCondition : IAutomationCondition
{
    public abstract string Field { get; }
    public abstract string Label { get; }
    public abstract IReadOnlyList<ConditionOperator> Operators { get; }
    public abstract ConditionValueKind ValueKind { get; }

    public Task<bool> EvaluateAsync(AutomationContext ctx, ConditionDef condition, CancellationToken ct) =>
        ctx.Issue is { } issue ? EvaluateAsync(ctx, issue, condition.Operator, condition.Value?.Trim(), ct) : Task.FromResult(false);

    protected abstract Task<bool> EvaluateAsync(AutomationContext ctx, Issue issue, string op, string? value, CancellationToken ct);

    protected static readonly IReadOnlyList<ConditionOperator> IsIsNot = [new("is", "is"), new("isNot", "is not")];

    protected static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>"is" → <paramref name="match"/>, "isNot" → !match, unknown operator → false.</summary>
    protected static bool IsOrNot(string op, bool match) => op switch { "is" => match, "isNot" => !match, _ => false };
}

public sealed class IssueTypeCondition : IssueCondition
{
    public override string Field => "type";
    public override string Label => "Issue type";
    public override IReadOnlyList<ConditionOperator> Operators => IsIsNot;
    public override ConditionValueKind ValueKind => ConditionValueKind.IssueType;
    protected override Task<bool> EvaluateAsync(AutomationContext ctx, Issue issue, string op, string? value, CancellationToken ct) =>
        Task.FromResult(IsOrNot(op, Same(issue.Type.ToString(), value)));
}

public sealed class PriorityCondition : IssueCondition
{
    public override string Field => "priority";
    public override string Label => "Priority";
    public override IReadOnlyList<ConditionOperator> Operators { get; } =
        [new("is", "is"), new("isNot", "is not"), new("atLeast", "is at least"), new("atMost", "is at most")];
    public override ConditionValueKind ValueKind => ConditionValueKind.Priority;
    protected override Task<bool> EvaluateAsync(AutomationContext ctx, Issue issue, string op, string? value, CancellationToken ct)
    {
        if (!Enum.TryParse<IssuePriority>(value, true, out var p)) return Task.FromResult(false);
        return Task.FromResult(op switch
        {
            "atLeast" => issue.Priority >= p,
            "atMost" => issue.Priority <= p,
            _ => IsOrNot(op, issue.Priority == p),
        });
    }
}

/// <summary>The issue's workflow status, by name.</summary>
public sealed class StatusCondition : IssueCondition
{
    public override string Field => "status";
    public override string Label => "Status";
    public override IReadOnlyList<ConditionOperator> Operators => IsIsNot;
    public override ConditionValueKind ValueKind => ConditionValueKind.Status;
    protected override Task<bool> EvaluateAsync(AutomationContext ctx, Issue issue, string op, string? value, CancellationToken ct) =>
        Task.FromResult(IsOrNot(op, Same(Display.StatusName(issue), value)));
}

public sealed class StatusCategoryCondition : IssueCondition
{
    public override string Field => "statusCategory";
    public override string Label => "Status category";
    public override IReadOnlyList<ConditionOperator> Operators => IsIsNot;
    public override ConditionValueKind ValueKind => ConditionValueKind.StatusCategory;
    protected override Task<bool> EvaluateAsync(AutomationContext ctx, Issue issue, string op, string? value, CancellationToken ct) =>
        Task.FromResult(IsOrNot(op, Same(issue.Status.ToString(), value)));
}

public sealed class LabelCondition : IssueCondition
{
    public override string Field => "label";
    public override string Label => "Labels";
    public override IReadOnlyList<ConditionOperator> Operators { get; } = [new("contains", "contain"), new("notContains", "don't contain")];
    public override ConditionValueKind ValueKind => ConditionValueKind.Label;
    protected override Task<bool> EvaluateAsync(AutomationContext ctx, Issue issue, string op, string? value, CancellationToken ct)
    {
        var has = issue.Labels.Any(l => Same(l.Name, value));
        return Task.FromResult(op switch { "contains" => has, "notContains" => !has, _ => false });
    }
}

public sealed class AssigneeCondition : IssueCondition
{
    public override string Field => "assignee";
    public override string Label => "Assignee";
    public override IReadOnlyList<ConditionOperator> Operators { get; } =
    [
        new("is", "is"), new("isNot", "is not"), new("empty", "is empty", false), new("notEmpty", "is not empty", false),
        new("triggerUser", "is the person who triggered the rule", false),
    ];
    public override ConditionValueKind ValueKind => ConditionValueKind.User;
    protected override Task<bool> EvaluateAsync(AutomationContext ctx, Issue issue, string op, string? value, CancellationToken ct)
    {
        var id = int.TryParse(value, out var v) ? v : (int?)null;
        return Task.FromResult(op switch
        {
            "empty" => issue.AssigneeId == null,
            "notEmpty" => issue.AssigneeId != null,
            "triggerUser" => issue.AssigneeId != null && issue.AssigneeId == ctx.Event.ActorId,
            "is" => id != null && issue.AssigneeId == id,
            "isNot" => id != null && issue.AssigneeId != id,
            _ => false,
        });
    }
}

public sealed class TitleCondition : IssueCondition
{
    public override string Field => "title";
    public override string Label => "Title";
    public override IReadOnlyList<ConditionOperator> Operators { get; } = [new("contains", "contains"), new("notContains", "doesn't contain")];
    public override ConditionValueKind ValueKind => ConditionValueKind.Text;
    protected override Task<bool> EvaluateAsync(AutomationContext ctx, Issue issue, string op, string? value, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(value)) return Task.FromResult(false);
        var has = issue.Title.Contains(value, StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(op switch { "contains" => has, "notContains" => !has, _ => false });
    }
}

/// <summary>A pull request of the issue's project mentions its key in the title or source branch (like the Development panel).</summary>
public sealed class LinkedPullRequestCondition(IDbContextFactory<AppDbContext> dbFactory) : IssueCondition
{
    public override string Field => "linkedPullRequest";
    public override string Label => "Linked pull request";
    public override IReadOnlyList<ConditionOperator> Operators { get; } =
        [new("exists", "exists", false), new("none", "doesn't exist", false), new("openExists", "an open one exists", false)];
    public override ConditionValueKind ValueKind => ConditionValueKind.None;

    protected override async Task<bool> EvaluateAsync(AutomationContext ctx, Issue issue, string op, string? value, CancellationToken ct)
    {
        var key = ctx.Project.Key;
        var token = $"{key}-{issue.Number}";
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Narrow in SQL by substring, then match the key exactly (WEB-1 must not match WEB-12).
        var candidates = await db.PullRequests.Where(p => p.Repository.ProjectId == issue.ProjectId
                && (EF.Functions.Like(p.Title, "%" + token + "%") || EF.Functions.Like(p.SourceBranch, "%" + token + "%")))
            .Select(p => new { p.Title, p.SourceBranch, p.State }).ToListAsync(ct);
        var rx = IssueKeys.Exact(key, issue.Number);
        var linked = candidates.Where(p => rx.IsMatch(p.Title) || rx.IsMatch(p.SourceBranch)).ToList();
        return op switch
        {
            "exists" => linked.Count > 0,
            "none" => linked.Count == 0,
            "openExists" => linked.Any(p => p.State == PullRequestState.Open),
            _ => false,
        };
    }
}

/// <summary>The issue's children (sub-tasks, or an epic's issues): all in a Done-category status. No children: false.</summary>
public sealed class SubtasksDoneCondition(IDbContextFactory<AppDbContext> dbFactory) : IssueCondition
{
    public override string Field => "subtasksDone";
    public override string Label => "Sub-tasks";
    public override IReadOnlyList<ConditionOperator> Operators { get; } =
        [new("allDone", "are all done", false), new("notAllDone", "are not all done", false)];
    public override ConditionValueKind ValueKind => ConditionValueKind.None;

    protected override async Task<bool> EvaluateAsync(AutomationContext ctx, Issue issue, string op, string? value, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var statuses = await db.Issues.Where(i => i.ParentId == issue.Id).Select(i => i.Status).ToListAsync(ct);
        var allDone = statuses.Count > 0 && statuses.All(s => s == IssueStatus.Done);
        return op switch { "allDone" => allDone, "notAllDone" => !allDone, _ => false };
    }
}

/// <summary>The registered conditions, by <see cref="IAutomationCondition.Field"/>.</summary>
public sealed class AutomationConditions(IEnumerable<IAutomationCondition> all)
{
    public IReadOnlyList<IAutomationCondition> All { get; } = all.ToList();

    public IAutomationCondition? Find(string? field) => All.FirstOrDefault(c => c.Field == field);

    /// <summary>Evaluates <paramref name="conditions"/> with all/any semantics. An unknown field or operator counts as false.</summary>
    public async Task<bool> EvaluateAsync(AutomationContext ctx, bool matchAll, IReadOnlyList<ConditionDef> conditions, CancellationToken ct)
    {
        if (conditions.Count == 0) return true;
        foreach (var c in conditions)
        {
            var ok = Find(c.Field) is { } impl && await impl.EvaluateAsync(ctx, c, ct);
            if (matchAll && !ok) return false;
            if (!matchAll && ok) return true;
        }
        return matchAll;
    }

    /// <summary>Null if the condition is well-formed, else why not.</summary>
    public string? Validate(ConditionDef c)
    {
        if (Find(c.Field) is not { } impl) return $"Unknown condition \"{c.Field}\".";
        if (impl.Operators.FirstOrDefault(o => o.Key == c.Operator) is not { } op) return $"{impl.Label}: pick an operator.";
        if (op.NeedsValue && string.IsNullOrWhiteSpace(c.Value)) return $"{impl.Label} {op.Label}: pick a value.";
        return null;
    }
}
