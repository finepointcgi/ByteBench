namespace ByteBench.Services.Ci;

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
    /// <summary>Docker <c>--user</c> for step containers (e.g. <c>1000:1000</c>); empty runs as the image's user, often root.</summary>
    public string? User { get; set; }
    /// <summary>Adds <c>--cap-drop ALL</c>: step containers lose every Linux capability (chown, binding low ports, raw sockets...). Off by default because many images need some.</summary>
    public bool DropCapabilities { get; set; }
    /// <summary>The docker CLI to run.</summary>
    public string DockerExecutable { get; set; } = "docker";
    /// <summary>Where job workspaces are exported. Must be shareable with the Docker daemon. Default: {temp}/bytebench-ci/{instance}.</summary>
    public string? WorkDirectory { get; set; }
    /// <summary>Per-step log cap, in characters (about 1 MB by default).</summary>
    public int MaxLogChars { get; set; } = 1024 * 1024;

    // ----- CI extras -----

    /// <summary>Where job artifacts (zips) are stored. Default: {ContentRoot}/ci-artifacts.</summary>
    public string? ArtifactsRoot { get; set; }
    /// <summary>Largest artifact archive per job (bytes, uncompressed and zipped). Default 100 MB.</summary>
    public long MaxArtifactBytes { get; set; } = 100L * 1024 * 1024;
    /// <summary>Where caches are stored. Default: {ContentRoot}/ci-cache.</summary>
    public string? CacheRoot { get; set; }
    /// <summary>Largest single cache (bytes). Default 500 MB.</summary>
    public long MaxCacheBytes { get; set; } = 500L * 1024 * 1024;
    /// <summary>Total cache size per repository; least recently used caches are evicted past it. Default 2 GB.</summary>
    public long CacheBytesPerRepository { get; set; } = 2L * 1024 * 1024 * 1024;
    /// <summary>Run pipeline schedules (only when <see cref="Enabled"/> too).</summary>
    public bool SchedulesEnabled { get; set; } = true;
}
