namespace GitBench.Services.Ci;

/// <summary>Configuration section <c>Ci</c>.</summary>
public class CiOptions
{
    /// <summary>When false, no pipelines are created and the "Run pipeline" button is hidden.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Jobs run at the same time across all pipelines.</summary>
    public int MaxConcurrentJobs { get; set; } = 2;
    /// <summary>A job (all its steps) is killed after this long.</summary>
    public int JobTimeoutMinutes { get; set; } = 20;
    /// <summary>Docker network for step containers (<c>bridge</c>, or <c>none</c> to cut off network access).</summary>
    public string Network { get; set; } = "bridge";
    /// <summary>Docker <c>--memory</c> limit per step container (e.g. <c>2g</c>); empty for no limit.</summary>
    public string? Memory { get; set; } = "2g";
    /// <summary>Docker <c>--cpus</c> limit per step container (e.g. <c>2</c>); empty for no limit.</summary>
    public string? Cpus { get; set; } = "2";
    /// <summary>Docker <c>--pids-limit</c> per step container; 0 for no limit.</summary>
    public int PidsLimit { get; set; } = 1024;
    /// <summary>The docker CLI to run.</summary>
    public string DockerExecutable { get; set; } = "docker";
    /// <summary>Where job workspaces are exported. Must be shareable with the Docker daemon. Default: {temp}/gitbench-ci/{instance}.</summary>
    public string? WorkDirectory { get; set; }
    /// <summary>Per-step log cap, in characters (about 1 MB by default).</summary>
    public int MaxLogChars { get; set; } = 1024 * 1024;
}
