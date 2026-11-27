namespace ByteBench.Services.Workspaces;

public sealed record WorkspaceLimits(string? Memory, string? Cpus, int PidsLimit, string? DiskLimit);

/// <summary>What to start: a locked-down container, nothing published except the IDE port on loopback.</summary>
public sealed record WorkspaceContainerSpec(int WorkspaceId, string Name, string Image, string Network, string HostAlias, int IdePort,
    IReadOnlyDictionary<string, string> Env, WorkspaceLimits Limits)
{
    /// <summary>Publish the IDE port on the host's loopback (false: ByteBench relays through the container instead).</summary>
    public bool PublishIdePort { get; init; } = true;
}

/// <summary>A started container; <see cref="IdeHostPort"/> is the loopback port its IDE port is published on (0: not published, reached through the relay).</summary>
public sealed record WorkspaceContainer(string ContainerId, int IdeHostPort, bool DiskLimitApplied = true);

/// <summary>A command to run inside a container as an unprivileged user (never root).</summary>
public sealed record ExecSpec(IReadOnlyList<string> Command)
{
    /// <summary>Written to the command's stdin (secrets go here, never on a command line).</summary>
    public string? Stdin { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    public string? WorkDir { get; init; }
    /// <summary>"coder" (the developer) or "bbai" (the AI assistant: no access to the developer's git credentials).</summary>
    public string User { get; init; } = "coder";
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Output beyond this is dropped (the process is still drained).</summary>
    public int MaxOutputBytes { get; init; } = 64 * 1024;
}

public sealed record ExecResult(int ExitCode, string Output, bool TimedOut = false, bool Truncated = false)
{
    public bool Ok => ExitCode == 0 && !TimedOut;
}

/// <summary>The egress policy applied to a workspace's network: a mode and the ip:port pairs that stay reachable.</summary>
public sealed record EgressPolicy(string Mode, IReadOnlyList<string> Allow);

/// <summary>Where to reach a workspace container from ByteBench (loopback only).</summary>
public sealed record WorkspaceEndpoint(string ContainerId, int IdeHostPort, int IdeContainerPort);

public sealed record ManagedContainer(string ContainerId, int? WorkspaceId);

/// <summary>
/// The Docker side of workspaces, abstracted so tests can use a fake. The real one (<see cref="DockerWorkspaceBackend"/>) drives the
/// docker CLI the way the CI runner does: every argument through an argument list, containers never get the docker socket,
/// host environment variables or any capability.
/// </summary>
public interface IWorkspaceBackend
{
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
    Task<bool> ImageExistsAsync(string image, CancellationToken ct);
    /// <summary>Builds the image from a directory containing the Dockerfile; every output line goes to <paramref name="onLine"/>. Returns the exit code.</summary>
    Task<int> BuildImageAsync(string contextDir, string image, Action<string> onLine, CancellationToken ct);
    Task<int> PullImageAsync(string image, Action<string> onLine, CancellationToken ct);
    /// <summary>Creates and starts the container. The entrypoint idles until <see cref="MarkReadyAsync"/>.</summary>
    Task<WorkspaceContainer> StartAsync(WorkspaceContainerSpec spec, CancellationToken ct);
    /// <summary>Restarts a stopped or running container (its files stay). The entrypoint idles again until ready.</summary>
    Task<WorkspaceContainer> RestartAsync(string containerId, int idePort, CancellationToken ct);
    /// <summary>Waits until the entrypoint waits for ByteBench's go-ahead (a fresh network namespace is up).</summary>
    Task WaitUntilWaitingAsync(string containerId, CancellationToken ct);
    /// <summary>Applies the egress rules from a helper that shares the network namespace (the workspace never holds NET_ADMIN).</summary>
    Task ApplyEgressAsync(string containerId, string image, EgressPolicy policy, CancellationToken ct);
    Task<ExecResult> ExecAsync(string containerId, ExecSpec spec, CancellationToken ct);
    /// <summary>Lets the entrypoint start the IDE (after the network is locked and the repository is checked out).</summary>
    Task MarkReadyAsync(string containerId, CancellationToken ct);
    Task<bool> IsHealthyAsync(string containerId, CancellationToken ct);
    /// <summary>A byte stream to a TCP port inside the container (the IDE through its loopback publish, other ports through the container).</summary>
    Task<Stream> ConnectAsync(WorkspaceEndpoint endpoint, int port, CancellationToken ct);
    /// <summary>Stops and removes the container (idempotent).</summary>
    Task RemoveAsync(string containerId, CancellationToken ct);
    /// <summary>Every container this ByteBench instance created (running or not).</summary>
    Task<IReadOnlyList<ManagedContainer>> ListManagedAsync(CancellationToken ct);
    /// <summary>Snapshots the container's filesystem as an image with this tag, labelled as one of this instance's prebuilds. False if Docker refused.</summary>
    Task<bool> CommitAsync(string containerId, string imageTag, CancellationToken ct);
    /// <summary>Removes an image (idempotent: a missing image is success). False when it can't go, for instance because a workspace still runs from it.</summary>
    Task<bool> RemoveImageAsync(string imageTag, CancellationToken ct);
    /// <summary>The tags of every prebuild image this instance committed.</summary>
    Task<IReadOnlyList<string>> ListPrebuildImagesAsync(CancellationToken ct);
}
