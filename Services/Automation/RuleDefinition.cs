using System.Text.Json;
using System.Text.Json.Serialization;
using GitBench.Data;

namespace GitBench.Services.Automation;

/// <summary>
/// The editable body of an <see cref="AutomationRule"/>, stored as JSON in <see cref="AutomationRule.Definition"/>: trigger
/// options, conditions (all or any) and actions. Statuses and labels are referenced by <b>name</b> (case-insensitive), so a
/// group rule works in every project of the group whatever their ids.
/// </summary>
public sealed class RuleDefinition
{
    public TriggerOptions Trigger { get; set; } = new();
    /// <summary>True: every condition must hold. False: at least one (no conditions always passes).</summary>
    public bool MatchAll { get; set; } = true;
    public List<ConditionDef> Conditions { get; set; } = [];
    public List<ActionDef> Actions { get; set; } = [];

    public const int MaxConditions = 20;
    public const int MaxActions = 20;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static RuleDefinition Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<RuleDefinition>(json, Json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public RuleDefinition Clone() => Parse(ToJson());
}

/// <summary>Issue fields the "field changed" trigger can watch.</summary>
[Flags]
public enum IssueField
{
    None = 0,
    Status = 1,
    Assignee = 2,
    Priority = 4,
    Labels = 8,
    Sprint = 16,
}

public enum ScheduleKind { Daily, Weekly, EveryHours }

/// <summary>What a scheduled rule runs on.</summary>
public enum ScheduleScope
{
    /// <summary>Each open issue of the project that passes the conditions (up to <see cref="TriggerOptions.MaxScheduledIssues"/>).</summary>
    OpenIssues,
    /// <summary>Once, without an issue (for webhooks and notifications).</summary>
    NoIssue,
}

public sealed class TriggerOptions
{
    public const int MaxScheduledIssues = 100;

    /// <summary>Issue transitioned: only from this status (name); null = any.</summary>
    public string? FromStatus { get; set; }
    /// <summary>Issue transitioned: only to this status (name); null = any.</summary>
    public string? ToStatus { get; set; }
    /// <summary>Field changed: the fields to watch (None = any of them; Status is covered by "issue transitioned").</summary>
    public IssueField Fields { get; set; }

    // ----- Scheduled (times are UTC) -----
    public ScheduleKind Schedule { get; set; } = ScheduleKind.Daily;
    /// <summary>"HH:mm", UTC.</summary>
    public string TimeOfDay { get; set; } = "09:00";
    public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Monday;
    /// <summary>Every N hours (1 to 168).</summary>
    public int Hours { get; set; } = 24;
    public ScheduleScope Scope { get; set; } = ScheduleScope.OpenIssues;
}

/// <summary>
/// A structured condition: <see cref="Field"/> names an <see cref="IAutomationCondition"/> (e.g. "priority"),
/// <see cref="Operator"/> one of its operators and <see cref="Value"/> the operand (a name, enum value or user id).
/// </summary>
public sealed class ConditionDef
{
    public string Field { get; set; } = "type";
    public string Operator { get; set; } = "is";
    public string? Value { get; set; }
}

public enum AutomationActionType
{
    Transition,
    Assign,
    SetPriority,
    AddLabel,
    RemoveLabel,
    Comment,
    CreateSubtask,
    LinkIssue,
    TransitionParent,
    Notify,
    SendWebhook,
}

public enum AssignMode { User, Reporter, TriggerUser, RoundRobin, Unassign }

public enum NotifyTarget { User, Assignee, Reporter, Watchers, TriggerUser }

/// <summary>One action. Which properties apply depends on <see cref="Type"/>; text fields accept smart values (<c>{{issue.key}}</c>).</summary>
public sealed class ActionDef
{
    public AutomationActionType Type { get; set; }
    /// <summary>Transition / TransitionParent: the target status name.</summary>
    public string? Status { get; set; }
    public AssignMode Assign { get; set; }
    /// <summary>Assign (User) or Notify (User): the user id.</summary>
    public int? UserId { get; set; }
    public IssuePriority? Priority { get; set; }
    /// <summary>AddLabel / RemoveLabel: the label name.</summary>
    public string? Label { get; set; }
    /// <summary>Comment body, sub-task title or notification message (smart values).</summary>
    public string? Text { get; set; }
    public IssueLinkRelation LinkType { get; set; } = IssueLinkRelation.RelatesTo;
    /// <summary>LinkIssue: the other issue's key (smart values, e.g. <c>{{issue.parent.key}}</c>).</summary>
    public string? IssueKey { get; set; }
    public NotifyTarget Notify { get; set; }
    /// <summary>SendWebhook: the URL (checked by <see cref="Webhooks.WebhookSender.ValidateUrl"/>).</summary>
    public string? Url { get; set; }
}
