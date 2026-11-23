namespace ByteBench.Services.TrackerImport;

/// <summary>Configuration section <c>TrackerImport</c> (importing issues from GitHub, GitLab and Jira).</summary>
public sealed class TrackerImportOptions
{
    /// <summary>The background service that runs queued imports. Off: imports stay queued (tests drive them directly).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>How many imports run at the same time.</summary>
    public int MaxConcurrent { get; set; } = 2;
    /// <summary>Imports a project may have queued or running at once.</summary>
    public int MaxActivePerProject { get; set; } = 1;
    /// <summary>Most issues one import reads (default 20000).</summary>
    public int MaxIssues { get; set; } = 20000;
    /// <summary>Allow sources on loopback, private, link-local and metadata addresses. Null follows <c>Webhooks:AllowPrivateNetworks</c>.</summary>
    public bool? AllowPrivateNetworks { get; set; }
    /// <summary>One API request is abandoned after this many seconds.</summary>
    public int RequestTimeoutSeconds { get; set; } = 60;
    /// <summary>When the source says "rate limited, retry in N seconds", wait up to this long (else the import fails with the reset time).</summary>
    public int MaxRateLimitWaitSeconds { get; set; } = 120;
}

/// <summary>Test-only: replaces the pause while rate limited, so a test needn't sleep. A registered object, not configuration.</summary>
public sealed class TrackerImportTestHooks
{
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; set; }
}
