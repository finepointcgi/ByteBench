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
    /// <summary>The most attachment bytes one import copies (default 500 MB); files past it are skipped with a warning.</summary>
    public long MaxAttachmentBytes { get; set; } = 500L * 1024 * 1024;
    /// <summary>The largest single attachment an import copies. 0 (default) follows <c>Attachments:MaxBytes</c>; a larger value is capped by it.</summary>
    public long MaxAttachmentFileBytes { get; set; }
    /// <summary>File extensions an import never copies (they are skipped with a warning).</summary>
    public string[] BlockedAttachmentExtensions { get; set; } = [".exe", ".dll", ".bat", ".cmd", ".com", ".scr", ".msi", ".ps1", ".vbs"];
}

/// <summary>Test-only: replaces the pause while rate limited, so a test needn't sleep. A registered object, not configuration.</summary>
public sealed class TrackerImportTestHooks
{
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; set; }
    /// <summary>Rewrites an attachment address before it is fetched (every hop), e.g. to point github.com at a local fake. The credential rule uses the original address.</summary>
    public Func<Uri, Uri>? MapUrl { get; set; }
}
