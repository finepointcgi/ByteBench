using System.Text.RegularExpressions;
using ByteBench.Data;

namespace ByteBench.Services.Automation;

/// <summary>Everything a rule run knows: the rule, the event and the loaded issue / pull request / pipeline / comment.</summary>
public sealed class AutomationContext
{
    public required AutomationRule Rule { get; init; }
    public required RuleDefinition Definition { get; init; }
    public required AutomationEvent Event { get; init; }
    public required Project Project { get; init; }
    /// <summary>The issue the rule runs on (with Project, Assignee, Reporter, Labels, WorkflowStatus and Parent); null if none.</summary>
    public Issue? Issue { get; set; }
    public PullRequest? PullRequest { get; init; }
    public Pipeline? Pipeline { get; init; }
    public Comment? Comment { get; init; }
    /// <summary>The person whose change triggered the rule (null for scheduled runs).</summary>
    public User? Initiator { get; init; }
    /// <summary>Who the actions act as: the bot or the rule's author.</summary>
    public int ActorId { get; set; }
    /// <summary>A label for the log, e.g. "Issue transitioned".</summary>
    public string TriggerLabel { get; init; } = "";
    /// <summary>One line per action taken.</summary>
    public List<string> Taken { get; } = [];
}

/// <summary>
/// Jira-style smart values in action text: <c>{{issue.key}}</c>, <c>{{issue.title}}</c>, <c>{{initiator.name}}</c>,
/// <c>{{pr.title}}</c> and so on (<see cref="Names"/>). Unknown names render as nothing. Values are inserted as plain text.
/// </summary>
public static partial class SmartValues
{
    [GeneratedRegex(@"\{\{\s*([A-Za-z][A-Za-z0-9_.]*)\s*\}\}")]
    private static partial Regex Token();

    /// <summary>Every supported smart value with a short description (shown in the rule editor).</summary>
    public static readonly IReadOnlyList<(string Name, string Description)> Names =
    [
        ("issue.key", "WEB-12"), ("issue.title", "the issue's title"), ("issue.url", "link to the issue"),
        ("issue.status", "workflow status"), ("issue.type", "Bug, Task, …"), ("issue.priority", "priority"),
        ("issue.assignee", "assignee's name"), ("issue.reporter", "reporter's name"), ("issue.labels", "comma-separated labels"),
        ("issue.parent.key", "parent's key"), ("initiator.name", "who triggered the rule"), ("initiator.username", "their username"),
        ("pr.title", "pull request title"), ("pr.number", "pull request number"), ("pr.url", "link to the pull request"),
        ("pr.branch", "source branch"), ("pr.author", "pull request author"), ("pipeline.status", "passed / failed"),
        ("pipeline.ref", "pipeline branch"), ("comment.body", "the new comment"), ("project.key", "project key"),
        ("rule.name", "this rule's name"), ("now", "current UTC time"),
    ];

    public static string Render(string? template, AutomationContext ctx, DateTime now)
    {
        if (string.IsNullOrEmpty(template)) return "";
        var values = Build(ctx, now);
        return Token().Replace(template, m => values.TryGetValue(m.Groups[1].Value.ToLowerInvariant(), out var v) ? v ?? "" : "");
    }

    public static Dictionary<string, string?> Build(AutomationContext ctx, DateTime now)
    {
        var d = new Dictionary<string, string?>
        {
            ["project.key"] = ctx.Project.Key,
            ["project.name"] = ctx.Project.Name,
            ["rule.name"] = ctx.Rule.Name,
            ["now"] = now.ToString("yyyy-MM-dd HH:mm") + " UTC",
            ["initiator.name"] = ctx.Initiator?.DisplayName,
            ["initiator.username"] = ctx.Initiator?.UserName,
        };
        if (ctx.Issue is { } i)
        {
            d["issue.key"] = $"{ctx.Project.Key}-{i.Number}";
            d["issue.number"] = i.Number.ToString();
            d["issue.title"] = i.Title;
            d["issue.url"] = $"/p/{ctx.Project.Key}/issues/{i.Number}";
            d["issue.status"] = Display.StatusName(i);
            d["issue.type"] = i.Type.ToString();
            d["issue.priority"] = i.Priority.ToString();
            d["issue.assignee"] = i.Assignee?.DisplayName;
            d["issue.reporter"] = i.Reporter?.DisplayName;
            d["issue.labels"] = string.Join(", ", i.Labels.Select(l => l.Name));
            d["issue.parent.key"] = i.Parent is { } p ? $"{ctx.Project.Key}-{p.Number}" : null;
        }
        if (ctx.PullRequest is { } pr)
        {
            d["pr.title"] = pr.Title;
            d["pr.number"] = pr.Number.ToString();
            d["pr.branch"] = pr.SourceBranch;
            d["pr.target"] = pr.TargetBranch;
            d["pr.author"] = pr.Author?.DisplayName;
            d["pr.url"] = $"/p/{ctx.Project.Key}/repos/{pr.Repository.Slug}/pull-requests/{pr.Number}";
        }
        if (ctx.Pipeline is { } pl)
        {
            d["pipeline.status"] = pl.Status.ToString().ToLowerInvariant();
            d["pipeline.ref"] = pl.Ref;
            d["pipeline.id"] = pl.Id.ToString();
        }
        if (ctx.Comment is { } c) d["comment.body"] = c.Body;
        return d;
    }
}
