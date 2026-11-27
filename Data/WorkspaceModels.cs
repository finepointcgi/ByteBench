using System.ComponentModel.DataAnnotations;

namespace ByteBench.Data;

public enum WorkspaceState { Starting, Running, Stopping, Stopped, Failed }

/// <summary>
/// A per-user development workspace: one Docker container with a repository checked out and OpenVSCode Server running
/// (Services/Workspaces). Only hashes of its secrets are stored: the connection token OpenVSCode Server is started with and the
/// short-lived git token the container clones and pushes with (<c>gbw_…</c>, valid only for <see cref="RepositoryId"/>, only while
/// <see cref="State"/> is Starting/Running; cleared on stop).
/// </summary>
public class Workspace
{
    public int Id { get; set; }
    /// <summary>Unguessable id used in URLs and in the workspace's own origin (<c>ide-{PublicId}.{BaseDomain}</c>).</summary>
    [Required, MaxLength(32)] public string PublicId { get; set; } = "";
    public int OwnerId { get; set; }
    public User Owner { get; set; } = null!;
    public int RepositoryId { get; set; }
    public Repository Repository { get; set; } = null!;
    public int ProjectId { get; set; }

    /// <summary>The branch, tag or commit the workspace was opened on.</summary>
    [Required, MaxLength(250)] public string Ref { get; set; } = "";
    [MaxLength(64)] public string? CommitSha { get; set; }
    /// <summary>What it was opened from: <c>pr:12</c>, <c>issue:WEB-3</c>, or empty.</summary>
    [MaxLength(100)] public string? Source { get; set; }

    public WorkspaceState State { get; set; } = WorkspaceState.Starting;
    [MaxLength(100)] public string? ContainerId { get; set; }
    [MaxLength(100)] public string? ContainerName { get; set; }
    [MaxLength(300)] public string Image { get; set; } = "";
    /// <summary>JSON object: container port → loopback host port the container's port is published on (the IDE port).</summary>
    public string PortMapJson { get; set; } = "{}";
    [MaxLength(20)] public string EgressMode { get; set; } = "public-only";
    /// <summary>Whether the git token may push (the owner had PushCode on a writable repository); branch protection still applies on the server.</summary>
    public bool CanPush { get; set; }

    /// <summary>SHA-256 of the OpenVSCode connection token (the plaintext lives only in memory and in the container).</summary>
    [MaxLength(32)] public byte[] ConnectionTokenHash { get; set; } = [];
    /// <summary>SHA-256 of the <c>gbw_…</c> git token; null once revoked (stop, failure, restart of the server).</summary>
    [MaxLength(32)] public byte[]? GitTokenHash { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
    /// <summary>Maximum lifetime: the workspace is stopped at this time whatever it is doing.</summary>
    public DateTime ExpiresAt { get; set; }
    public int IdleTimeoutMinutes { get; set; } = 30;
    public DateTime? StoppedAt { get; set; }
    [MaxLength(100)] public string? StopReason { get; set; }
    [MaxLength(500)] public string? Error { get; set; }

    /// <summary>
    /// What the workspace was built from: <c>toolchain:node@22</c>, <c>image:…</c> or empty (the default image). Shown on the workspace page.
    /// </summary>
    [MaxLength(300)] public string? EnvironmentSummary { get; set; }
    /// <summary>
    /// The workspace's project and cache volumes exist (they outlive the container, so a stopped workspace can be resumed with its dependencies).
    /// Deleting the workspace, or the retention period (<c>Workspaces:VolumeRetentionDays</c>), removes them.
    /// </summary>
    public bool VolumesKept { get; set; }
    /// <summary>JSON array of repository ids on this server that the workspace token may additionally read (submodules; never write).</summary>
    public string SubmoduleRepoIdsJson { get; set; } = "[]";

    public bool IsLive => State is WorkspaceState.Starting or WorkspaceState.Running or WorkspaceState.Stopping;
}

/// <summary>Per-project workspace switches (a Maintainer or Owner sets them); rows are created on first change, defaults are on.</summary>
public class ProjectWorkspaceSettings
{
    [Key] public int ProjectId { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>The AI assistant panel next to the IDE.</summary>
    public bool AiEnabled { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A secret for workspaces: either one user's (<see cref="UserId"/> set: reaches every workspace of that user) or one repository's
/// (<see cref="RepositoryId"/> set: reaches every workspace opened on it, managed by Maintainers and Owners). The value is encrypted
/// with Data Protection and never returned by any API; it is injected as an environment variable inside the container when it starts.
/// </summary>
public class WorkspaceSecret
{
    public int Id { get; set; }
    public int? UserId { get; set; }
    public int? RepositoryId { get; set; }
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    public string EncryptedValue { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int? UpdatedById { get; set; }
}

/// <summary>A user's workspace preferences: extra VS Code settings.json content and a shell snippet, applied when their workspaces start.</summary>
public class UserWorkspaceSettings
{
    [Key] public int UserId { get; set; }
    /// <summary>A JSON object merged into the IDE's machine settings (size-capped).</summary>
    public string SettingsJson { get; set; } = "";
    /// <summary>Shell commands sourced by the user's terminals in their own workspaces (size-capped).</summary>
    public string ShellSnippet { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
