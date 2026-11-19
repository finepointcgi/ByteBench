using GitBench.Data;

namespace GitBench.Components.Pipelines;

public static class PipelineFormat
{
    /// <summary>"42s", "3m 05s", "1h 02m"; running items count up to now; empty if never started.</summary>
    public static string Duration(DateTime? started, DateTime? finished)
    {
        if (started is not { } s) return "";
        var d = (finished ?? DateTime.UtcNow) - s;
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        return d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes:00}m"
            : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m {d.Seconds:00}s"
            : $"{(int)d.TotalSeconds}s";
    }

    public static string Trigger(Pipeline p) => p.Trigger switch
    {
        PipelineTrigger.Push => "push",
        PipelineTrigger.PullRequest => "pull request",
        PipelineTrigger.Manual => "manual run",
        PipelineTrigger.Retry => "re-run",
        PipelineTrigger.Schedule => "schedule",
        PipelineTrigger.MergeQueue => "merge queue",
        _ => p.Trigger.ToString(),
    };
}
