namespace ByteBench.Services.Workspaces;

public sealed record WorkspaceLimits(string? Memory, string? Cpus, int PidsLimit, string? DiskLimit);

/// <summary>What to start: a locked-down container, nothing published except the IDE port on loopback.</summary>
public sealed record WorkspaceContainerSpec(int WorkspaceId, string Name, string Image, string Network, string HostAlias, int IdePort,
    IReadOnlyDictionary<string, string> Env, WorkspaceLimits Limits)
{
    /// <summary>Publish the IDE port on the host's loopback (false: ByteBench relays through the container instead).</summary>
    public bool PublishIdePort { get; init; } = true;
    /// <summary>Named volumes mounted into the container (the project and the dependency caches); they outlive it.</summary>
    public IReadOnlyList<WorkspaceMount> Mounts { get; init; } = [];
}

/// <summary>A named volume and where it is mounted inside the container.</summary>
public sealed record WorkspaceMount(string Volume, string Target);

/// <summary>A sidecar service container (postgres, redis, …) for one workspace, on its private internal network.</summary>
public sealed record WorkspaceSidecarSpec(int WorkspaceId, string Name, string Image, string Network, string Alias, IReadOnlyDictionary<string, string> Env, WorkspaceLimits Limits);

public sealed record WorkspaceSidecarContainer(string ContainerId, string IpAddress);

/// <summary>A volume ByteBench created for a workspace; <see cref="SizeBytes"/> is -1 when Docker doesn't say.</summary>
public sealed record ManagedVolume(string Name, int? WorkspaceId, long SizeBytes);

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
    Task<int> BuildImageAsync(string contextDir, string image, Action<string> onLine, CancellationToken ct, IReadOnlyDictionary<string, string>? buildArgs = null);
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
    /// <summary>
    /// Like <see cref="ExecAsync"/> for long-running commands: every output line (stdout and stderr) goes to <paramref name="onLine"/> as it appears; the result
    /// carries only a capped tail. Cancelling kills the docker client; the caller ends the in-container process itself (see WorkspaceTasks).
    /// </summary>
    Task<ExecResult> ExecStreamAsync(string containerId, ExecSpec spec, Action<string> onLine, CancellationToken ct);
    /// <summary>Creates a labelled named volume for a workspace (idempotent).</summary>
    Task CreateVolumeAsync(string name, int workspaceId, CancellationToken ct);
    /// <summary>Removes the volumes (idempotent; ones that are still in use are retried after the container is gone).</summary>
    Task RemoveVolumesAsync(IEnumerable<string> names, CancellationToken ct);
    Task<IReadOnlyList<ManagedVolume>> ListVolumesAsync(CancellationToken ct);
    /// <summary>Creates a private internal network (no outside access) for a workspace's sidecars; returns its name.</summary>
    Task CreateInternalNetworkAsync(string name, int workspaceId, CancellationToken ct);
    Task ConnectNetworkAsync(string containerId, string network, CancellationToken ct);
    /// <summary>Pulls (progress to <paramref name="onLine"/>) and starts a sidecar container.</summary>
    Task<WorkspaceSidecarContainer> StartSidecarAsync(WorkspaceSidecarSpec spec, Action<string> onLine, CancellationToken ct);
    /// <summary>Removes a workspace's sidecar containers and its private network (idempotent).</summary>
    Task RemoveAuxiliaryAsync(int workspaceId, CancellationToken ct);
    /// <summary>The last lines a sidecar printed (for failure messages).</summary>
    Task<string> SidecarLogsAsync(string containerId, CancellationToken ct);
    /// <summary>Lets the entrypoint start the IDE (after the network is locked and the repository is checked out).</summary>
    Task MarkReadyAsync(string containerId, CancellationToken ct);
    Task<bool> IsHealthyAsync(string containerId, CancellationToken ct);
    /// <summary>A byte stream to a TCP port inside the container (the IDE through its loopback publish, other ports through the container).</summary>
    Task<Stream> ConnectAsync(WorkspaceEndpoint endpoint, int port, CancellationToken ct);
    /// <summary>Stops and removes the container (idempotent).</summary>
    Task RemoveAsync(string containerId, CancellationToken ct);
    /// <summary>Every container this ByteBench instance created (running or not).</summary>
    Task<IReadOnlyList<ManagedContainer>> ListManagedAsync(CancellationToken ct);
}
