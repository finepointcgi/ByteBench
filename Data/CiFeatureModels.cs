namespace ByteBench.Data;

/// <summary>CI additions: coverage and restart recovery.</summary>
public partial class Pipeline
{
    /// <summary>The average of the jobs' coverage (percent, 0 to 100); null when no job reported coverage.</summary>
    public double? Coverage { get; set; }
    /// <summary>How many times the runner put this pipeline back in the queue after a server restart interrupted it (at most once).</summary>
    public int RecoveryAttempts { get; set; }
}

public partial class PipelineJob
{
    /// <summary>The job's coverage (percent), from its <c>coverage:</c> regex or a Cobertura / LCOV report.</summary>
    public double? Coverage { get; set; }
}
