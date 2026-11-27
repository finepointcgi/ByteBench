using System.ComponentModel.DataAnnotations;

namespace ByteBench.Data;

public enum PrebuildState { Queued, Building, Ready, Failed, Superseded, Expired }

/// <summary>
/// Per-repository prebuild switches (a Maintainer or Owner sets them); a row is created on first change. Prebuilds are off until enabled.
/// <see cref="Branches"/> is a comma or newline separated list of branch names (<c>*</c> patterns allowed); empty means the default branch.
/// </summary>
public class RepositoryPrebuildSettings
{
    [Key] public int RepositoryId { get; set; }
    public Repository Repository { get; set; } = null!;
    public bool Enabled { get; set; }
    [MaxLength(500)] public string Branches { get; set; } = "";
    /// <summary>Ready prebuilds kept per branch; older ones are cleaned up.</summary>
    public int KeepLast { get; set; } = 3;
    /// <summary>A prebuild older than this is stale: new workspaces ignore it and cleanup removes it.</summary>
    public int RetentionDays { get; set; } = 14;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One prebuild: a Docker image committed from a container that cloned the repository, fetched its submodules and ran the setup commands
/// of the repository's own workspace environment file. Secrets are never part of it (the build container gets none, and its git credential
/// is deleted before the image is committed).
/// </summary>
public class WorkspacePrebuild
{
    public int Id { get; set; }
    public int RepositoryId { get; set; }
    public Repository Repository { get; set; } = null!;
    public int ProjectId { get; set; }
    [Required, MaxLength(250)] public string Ref { get; set; } = "";
    [MaxLength(64)] public string? CommitSha { get; set; }
    public PrebuildState State { get; set; } = PrebuildState.Queued;
    /// <summary><c>push</c> or <c>manual</c>.</summary>
    [MaxLength(20)] public string Trigger { get; set; } = "push";
    /// <summary>Whose permissions the build clones with (the pusher, or whoever pressed Rebuild now).</summary>
    public int? TriggeredById { get; set; }
    /// <summary>The Docker image holding the prebuilt state; null until Ready and again once removed.</summary>
    [MaxLength(300)] public string? ImageTag { get; set; }
    /// <summary>The workspace image the prebuild is based on; a prebuild is stale when this is no longer the configured image.</summary>
    [MaxLength(300)] public string BaseImage { get; set; } = "";
    /// <summary>Hash of the setup configuration; a prebuild is stale for a commit whose configuration hashes differently.</summary>
    [MaxLength(64)] public string ConfigHash { get; set; } = "";
    /// <summary>Build output, bounded; never contains a token.</summary>
    public string Log { get; set; } = "";
    [MaxLength(500)] public string? Error { get; set; }
    /// <summary>SHA-256 of the read-only <c>gbw_…</c> token the build clones with; null once the build is over.</summary>
    [MaxLength(32)] public byte[]? GitTokenHash { get; set; }
    /// <summary>JSON array of repository ids on this server the build token may additionally read (submodules; never write).</summary>
    public string SubmoduleRepoIdsJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

/// <summary>What a workspace was given: the resources counted against its owner's limits and the prebuild it started from, if any.</summary>
public class WorkspaceAllocation
{
    [Key] public int WorkspaceId { get; set; }
    public Workspace Workspace { get; set; } = null!;
    public int? PrebuildId { get; set; }
    /// <summary>CPUs in thousandths (2000 = two CPUs).</summary>
    public int CpuMillis { get; set; }
    public int MemoryMb { get; set; }
    /// <summary>Disk limit in MB (0: no limit applied).</summary>
    public int DiskMb { get; set; }
}

/// <summary>A user's own workspace preferences (their idle period, within the administrator's maximum).</summary>
public class UserWorkspacePreference
{
    [Key] public int UserId { get; set; }
    public User User { get; set; } = null!;
    public int? IdleTimeoutMinutes { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
