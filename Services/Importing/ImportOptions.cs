namespace ByteBench.Services.Importing;

/// <summary>Configuration section <c>Import</c> (the repository import wizard).</summary>
public sealed class ImportOptions
{
    /// <summary>The background service that runs queued imports. Off: imports stay queued (tests drive them directly).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>An import is cancelled when the cloned data on disk grows beyond this (default 1 GiB).</summary>
    public long MaxRepoBytes { get; set; } = 1024L * 1024 * 1024;
    /// <summary>An import's git fetch is killed after this many minutes.</summary>
    public int TimeoutMinutes { get; set; } = 30;
    /// <summary>How many imports run at the same time.</summary>
    public int MaxConcurrent { get; set; } = 2;
    /// <summary>Imports a project may have queued or running at once.</summary>
    public int MaxActivePerProject { get; set; } = 3;
}

/// <summary>
/// Test-only switch: lets the wizard read <c>file://</c> sources (a bare repository on the test machine). It is a registered
/// object, not configuration, so a production deployment can't turn it on; the SSRF rules for network URLs stay as for mirrors.
/// </summary>
public sealed class ImportTestHooks
{
    public bool AllowFileUrls { get; set; }
}
