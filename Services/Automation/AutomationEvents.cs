using System.Threading.Channels;
using GitBench.Data;

namespace GitBench.Services.Automation;

public enum AutomationEventKind
{
    IssueCreated, IssueUpdated, IssueCommented,
    PullRequestOpened, PullRequestMerged, PullRequestDeclined,
    PipelineCompleted,
    Manual,
}

/// <summary>
/// Something that may start rules, queued by the service hooks after their change is saved. <see cref="Depth"/> is 0 for a
/// change made by a person and n+1 for a change made by an action of a rule running at depth n (loop protection).
/// </summary>
public sealed record AutomationEvent(AutomationEventKind Kind, int ActorId)
{
    public int? IssueId { get; init; }
    public int? PullRequestId { get; init; }
    public int? PipelineId { get; init; }
    public int? CommentId { get; init; }
    /// <summary>Issue updates: which fields changed and, for status, from/to which workflow status.</summary>
    public IssueFieldChanges? Changes { get; init; }
    /// <summary>Pipelines: whether it passed (false: failed or errored; other outcomes are not queued).</summary>
    public bool? PipelinePassed { get; init; }
    /// <summary>Manual runs: the one rule to run.</summary>
    public int? RuleId { get; init; }
    public int Depth { get; init; }
    /// <summary>The rule whose action caused this event (for the log).</summary>
    public int? CausedByRuleId { get; init; }
    /// <summary>Manual runs: completed with the run's log entry once processed.</summary>
    public TaskCompletionSource<AutomationRun?>? Completion { get; init; }
}

/// <summary>The issue fields automation watches, captured before a change (see <see cref="Diff"/>).</summary>
public readonly record struct IssueFieldState(int? WorkflowStatusId, IssueStatus Status, int? AssigneeId, IssuePriority Priority, string Labels, int? SprintId)
{
    public static IssueFieldState Of(Issue i) =>
        new(i.WorkflowStatusId, i.Status, i.AssigneeId, i.Priority, string.Join(',', i.Labels.Select(l => l.Id).Order()), i.SprintId);

    /// <summary>What changed between this state and <paramref name="after"/>.</summary>
    public IssueFieldChanges Diff(Issue after)
    {
        var now = Of(after);
        var f = IssueField.None;
        if (now.WorkflowStatusId != WorkflowStatusId || now.Status != Status) f |= IssueField.Status;
        if (now.AssigneeId != AssigneeId) f |= IssueField.Assignee;
        if (now.Priority != Priority) f |= IssueField.Priority;
        if (now.Labels != Labels) f |= IssueField.Labels;
        if (now.SprintId != SprintId) f |= IssueField.Sprint;
        return new IssueFieldChanges(f, WorkflowStatusId, now.WorkflowStatusId);
    }
}

/// <summary>Changed fields of one issue update, plus the workflow statuses before and after.</summary>
public sealed record IssueFieldChanges(IssueField Fields, int? FromStatusId = null, int? ToStatusId = null)
{
    public static readonly IssueFieldChanges Sprint = new(IssueField.Sprint);
}

/// <summary>Ambient marker for code running inside a rule's actions, so the events it causes carry the next depth.</summary>
public static class AutomationScope
{
    private static readonly AsyncLocal<(int Depth, int RuleId)?> current = new();

    public static (int Depth, int RuleId)? Current => current.Value;

    /// <summary>Marks the calling async flow as "actions of <paramref name="ruleId"/> at <paramref name="depth"/>" until disposed.</summary>
    public static IDisposable Enter(int depth, int ruleId)
    {
        var previous = current.Value;
        current.Value = (depth, ruleId);
        return new Restore(previous);
    }

    private sealed class Restore((int, int)? previous) : IDisposable
    {
        public void Dispose() => current.Value = previous;
    }
}

/// <summary>
/// The in-memory queue between the service hooks and <see cref="AutomationWorker"/> (singleton). <see cref="Enqueue"/> never
/// blocks or throws, and stamps each event with its depth from <see cref="AutomationScope"/>.
/// </summary>
public sealed class AutomationQueue(ILogger<AutomationQueue> log)
{
    private readonly Channel<AutomationEvent> channel = Channel.CreateBounded<AutomationEvent>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private int pending;

    public ChannelReader<AutomationEvent> Reader => channel.Reader;

    /// <summary>The bot's user id once known (set at startup): its changes count as automation even outside a rule's scope.</summary>
    public int? BotId { get; set; }

    public bool Enqueue(AutomationEvent e)
    {
        if (AutomationScope.Current is { } scope) e = e with { Depth = scope.Depth, CausedByRuleId = scope.RuleId };
        else if (BotId is { } bot && e.ActorId == bot && e.Depth == 0) e = e with { Depth = 1 };
        Interlocked.Increment(ref pending);
        if (channel.Writer.TryWrite(e)) return true;
        Interlocked.Decrement(ref pending);
        log.LogWarning("Automation queue is full; dropped {Kind} event", e.Kind);
        e.Completion?.TrySetResult(null);
        return false;
    }

    /// <summary>Called by the worker when it has finished an event.</summary>
    internal void Done() => Interlocked.Decrement(ref pending);

    /// <summary>Events queued or being processed.</summary>
    public int Pending => Volatile.Read(ref pending);

    /// <summary>Waits until nothing is queued or running (tests, and manual runs that want the outcome).</summary>
    public async Task<bool> WaitIdleAsync(TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (Pending > 0)
        {
            if (DateTime.UtcNow > end) return false;
            await Task.Delay(20);
        }
        return true;
    }
}
