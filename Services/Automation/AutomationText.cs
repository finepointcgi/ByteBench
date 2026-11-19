using ByteBench.Data;

namespace ByteBench.Services.Automation;

/// <summary>Human-readable summaries of rules for the UI.</summary>
public static class AutomationText
{
    public static string Trigger(AutomationTrigger trigger, TriggerOptions t)
    {
        var label = AutomationEngine.TriggerLabel(trigger);
        return trigger switch
        {
            AutomationTrigger.IssueTransitioned => label
                + (string.IsNullOrWhiteSpace(t.FromStatus) ? "" : $" from {t.FromStatus}")
                + (string.IsNullOrWhiteSpace(t.ToStatus) ? "" : $" to {t.ToStatus}"),
            AutomationTrigger.FieldChanged => label + ": " + (t.Fields == IssueField.None ? "assignee, priority, labels or sprint" : Fields(t.Fields)),
            AutomationTrigger.Scheduled => label + " " + AutomationSchedule.Describe(t),
            _ => label,
        };
    }

    public static string Fields(IssueField f) => string.Join(", ",
        new[] { IssueField.Assignee, IssueField.Priority, IssueField.Labels, IssueField.Sprint }.Where(x => f.HasFlag(x)).Select(x => x.ToString().ToLowerInvariant()));

    public static string RunAs(AutomationRunAs r) => r == AutomationRunAs.AutomationBot ? AutomationBot.DisplayName : "Rule author";

    public static string Status(AutomationRunStatus s) => s switch
    {
        AutomationRunStatus.Success => "Success",
        AutomationRunStatus.Failure => "Failed",
        AutomationRunStatus.NoActions => "No actions",
        AutomationRunStatus.Throttled => "Throttled",
        _ => "Loop stopped",
    };

    /// <summary>The issue page URL for a logged key like "WEB-12", or null.</summary>
    public static string? IssueUrl(string? key)
    {
        if (key == null) return null;
        var dash = key.LastIndexOf('-');
        return dash > 0 && int.TryParse(key[(dash + 1)..], out var n) ? $"/p/{key[..dash]}/issues/{n}" : null;
    }

    public static string Assign(AssignMode m) => m switch
    {
        AssignMode.User => "a specific user",
        AssignMode.Reporter => "the reporter",
        AssignMode.TriggerUser => "the person who triggered the rule",
        AssignMode.RoundRobin => "developers in turn (round-robin)",
        _ => "nobody (unassign)",
    };

    public static string Notify(NotifyTarget t) => t switch
    {
        NotifyTarget.User => "a specific user",
        NotifyTarget.Assignee => "the assignee",
        NotifyTarget.Reporter => "the reporter",
        NotifyTarget.Watchers => "the issue's watchers",
        _ => "the person who triggered the rule",
    };
}
