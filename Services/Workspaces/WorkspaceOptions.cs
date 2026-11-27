namespace ByteBench.Services.Workspaces;

/// <summary>Configuration section <c>Workspaces</c>. Whether workspaces are on at all is an admin switch (/admin/workspaces), off by default.</summary>
public class WorkspaceOptions
{
    /// <summary>The workspace image (build it from docker/workspace/).</summary>
    public string Image { get; set; } = "bytebench-workspace:2";
    /// <summary>Directory with the image's Dockerfile (default: docker/workspace beside the app). If the image is missing it is built from here automatically; with no Dockerfile it is pulled.</summary>
    public string? BuildContext { get; set; }
    public string DockerExecutable { get; set; } = "docker";
    /// <summary>Docker network the workspaces run on (created on first use with inter-container traffic switched off).</summary>
    public string Network { get; set; } = "bytebench-workspaces";

    /// <summary>
    /// The IDE and previews are served from their own origins, <c>ide-{id}.{BaseDomain}</c> and <c>p{port}-{id}.{BaseDomain}</c>
    /// (a wildcard DNS record and certificate for <c>*.{BaseDomain}</c> pointing at ByteBench; the port of ByteBench's own
    /// address is kept). Cookies are host-only, so the workspace origins never see ByteBench's session cookie and their scripts
    /// can't reach ByteBench's origin. In Development it defaults to <c>localhost</c> (browsers resolve <c>*.localhost</c>).
    /// </summary>
    public string? BaseDomain { get; set; }
    /// <summary>
    /// Without a <see cref="BaseDomain"/>: serve the IDE and previews from ByteBench's own origin under <c>/_ws/{id}/…</c>. Previews are
    /// sandboxed (CSP <c>sandbox</c>, opaque origin) but the IDE cannot be, so code in the workspace can act as the signed-in user on
    /// ByteBench. Off by default; only for installations that cannot have a wildcard domain and trust the code they open.
    /// </summary>
    public bool AllowSharedOrigin { get; set; }

    /// <summary>
    /// How containers reach ByteBench's git endpoint, e.g. <c>http://host.docker.internal:5108</c> or <c>http://bytebench:8080</c>.
    /// Default: <c>http://host.docker.internal:{port of ByteBench's first listen address}</c>. The host must accept connections from the Docker network.
    /// </summary>
    public string? GitBaseUrl { get; set; }

    /// <summary>
    /// How ByteBench reaches the IDE: <c>Published</c> (the IDE port is published on 127.0.0.1 of the Docker host; needs ByteBench on the same host),
    /// <c>Relay</c> (nothing is published; every connection goes through <c>docker exec … socat</c>; works with ByteBench in a container and with a remote
    /// DOCKER_HOST, a little slower) or <c>Auto</c> (default: Relay inside a container or with DOCKER_HOST set, else Published).
    /// </summary>
    public string IdeTransport { get; set; } = "Auto";

    /// <summary>Workspaces running at once per user (counting Starting and Stopping ones).</summary>
    public int MaxPerUser { get; set; } = 2;
    /// <summary>Workspaces running at once on the server.</summary>
    public int MaxTotal { get; set; } = 10;
    /// <summary>A workspace nobody has used (no proxied request, no IDE connection traffic) for this long is stopped.</summary>
    public int IdleTimeoutMinutes { get; set; } = 30;
    /// <summary>A workspace is stopped this long after it started, whatever it is doing.</summary>
    public int MaxLifetimeHours { get; set; } = 8;
    public int StartupTimeoutSeconds { get; set; } = 180;

    // ----- Container limits -----
    public string? Memory { get; set; } = "4g";
    public string? Cpus { get; set; } = "2";
    public int PidsLimit { get; set; } = 2048;
    /// <summary>Writable-layer size (<c>docker run --storage-opt size=</c>). Needs overlay2 on xfs with pquota (or btrfs/zfs); where the daemon refuses it, the container starts without and a warning is logged.</summary>
    public string? DiskLimit { get; set; } = "10g";

    /// <summary><c>public-only</c> (default: the internet yes, private networks and ByteBench's host no), <c>none</c> or <c>open</c>.</summary>
    public string EgressMode { get; set; } = "public-only";

    public int ReaperIntervalSeconds { get; set; } = 30;

    /// <summary>A stopped workspace keeps its project and cache volumes (so it can be resumed with its dependencies) this many days; then they are deleted.</summary>
    public int VolumeRetentionDays { get; set; } = 14;
    /// <summary>The repository's <c>setup:</c> commands may run this long, each.</summary>
    public int SetupTimeoutMinutes { get; set; } = 20;
    /// <summary>Each submodule may take this long to fetch before it is reported as failed (the workspace still opens).</summary>
    public int SubmoduleTimeoutSeconds { get; set; } = 180;
    /// <summary>A sidecar service must accept connections within this time or the workspace fails with the service's last output.</summary>
    public int ServiceStartTimeoutSeconds { get; set; } = 120;

    /// <summary>The AI assistant next to the IDE.</summary>
    public WorkspaceAiOptions Ai { get; set; } = new();

    /// <summary>Task-driven AI runs on issues.</summary>
    public AiRunOptions AiRuns { get; set; } = new();
}

/// <summary>Configuration section <c>Workspaces:Ai</c>. The assistant also needs the Anthropic API (<c>Ai:Enabled</c> and a key).</summary>
public class WorkspaceAiOptions
{
    /// <summary>Assistant requests per user per minute (a separate budget from the web editor's).</summary>
    public int RequestsPerMinute { get; set; } = 6;
    public int MaxToolCalls { get; set; } = 25;
    public int MaxTokensPerTurn { get; set; } = 16000;
    public int MaxOutputTokensPerRequest { get; set; } = 48000;
    /// <summary>A command the assistant runs is killed after this long.</summary>
    public int CommandTimeoutSeconds { get; set; } = 60;
    /// <summary>Output of a command, a file read or a search is cut at this many bytes.</summary>
    public int MaxOutputBytes { get; set; } = 32 * 1024;
    public int MaxFileReadBytes { get; set; } = 200 * 1024;
    public int MaxFileWriteBytes { get; set; } = 1024 * 1024;
}

/// <summary>
/// Configuration section <c>Workspaces:AiRuns</c>: task-driven AI runs (the AI user works an issue in its own workspace and opens a pull request).
/// Every limit is enforced by the executor; a run that hits one stops, says so on the issue and, if it already changed files, opens a draft pull
/// request so the work isn't lost.
/// </summary>
public class AiRunOptions
{
    /// <summary>Turns task-driven runs on (also needs a site admin's switch, the workspace assistant and the AI user as a member of the project).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Runs working at once across the server (each holds one workspace container).</summary>
    public int MaxConcurrent { get; set; } = 2;
    /// <summary>Runs waiting in the queue before new requests are refused.</summary>
    public int MaxQueued { get; set; } = 20;
    /// <summary>Runs started per project per 24 hours.</summary>
    public int MaxRunsPerProjectPerDay { get; set; } = 30;
    /// <summary>A run is stopped after this long, however far it got (includes the workspace start).</summary>
    public double MaxDurationMinutes { get; set; } = 30;
    /// <summary>Model turns (requests to the model) per run.</summary>
    public int MaxSteps { get; set; } = 40;
    /// <summary>Tool calls per run.</summary>
    public int MaxToolCalls { get; set; } = 150;
    /// <summary>Input plus output tokens per run (cache reads count a tenth).</summary>
    public long MaxTokens { get; set; } = 1_500_000;
    /// <summary>Cost cap per run in US dollars, from the token prices below. 0 turns the cost cap off.</summary>
    public decimal MaxCostUsd { get; set; } = 5m;
    /// <summary>Token prices per million tokens, for the cost cap and the cost shown on the run (set them for your model).</summary>
    public decimal InputUsdPerMTok { get; set; } = 3m;
    public decimal OutputUsdPerMTok { get; set; } = 15m;
    public decimal CacheReadUsdPerMTok { get; set; } = 0.30m;
    public decimal CacheWriteUsdPerMTok { get; set; } = 3.75m;
    /// <summary>How often the worker looks for queued runs (they are also picked up at once when one is requested).</summary>
    public int PollSeconds { get; set; } = 10;
    /// <summary>A command the AI runs during a run (a build or a test suite) is stopped after this long (the chat assistant's limit is Workspaces:Ai:CommandTimeoutSeconds).</summary>
    public int CommandTimeoutSeconds { get; set; } = 600;
    /// <summary>Branch prefix for the AI's branches.</summary>
    public string BranchPrefix { get; set; } = "ai/";
}
