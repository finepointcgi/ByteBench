using System.ComponentModel.DataAnnotations;

namespace ByteBench.Data;

/// <summary>What a person invited to a workspace may do. The owner is neither: the owner is the <see cref="Workspace.OwnerId"/>.</summary>
public enum WorkspaceShareRole
{
    /// <summary>Read-only: browses files and reads terminal output through ByteBench. Never gets an IDE session (no edit, no exec).</summary>
    Viewer = 0,
    /// <summary>Full IDE access: the same OpenVSCode server as the owner through the proxy with their own ticket and session (pair programming).</summary>
    Collaborator = 1,
}

/// <summary>
/// An invitation to a running workspace (Services/Workspaces/WorkspaceSharing). Active while <see cref="EndedAt"/> is null; it ends when the
/// owner revokes it, the invitee leaves, the invitee loses access to the repository, or the workspace stops (sharing never outlives a workspace).
/// Rows are kept after they end, as history.
/// </summary>
public class WorkspaceShare
{
    public int Id { get; set; }
    public int WorkspaceId { get; set; }
    public Workspace Workspace { get; set; } = null!;
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public WorkspaceShareRole Role { get; set; }
    public int InvitedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
    [MaxLength(100)] public string? EndReason { get; set; }

    public bool IsActive => EndedAt == null;
}

/// <summary>
/// The network policy a workspace was started with, after the admin policy and the repository's <c>egress:</c> were combined
/// (<c>WorkspaceEgressPolicy.Narrow</c>). One row per workspace; <see cref="RulesJson"/> is the allow-list (<c>host[:port]</c> strings) and
/// <see cref="WarningsJson"/> says what the repository asked for that was refused (it may only narrow).
/// </summary>
public class WorkspaceEgressRecord
{
    [Key] public int WorkspaceId { get; set; }
    public Workspace Workspace { get; set; } = null!;
    /// <summary><c>open</c>, <c>public-only</c>, <c>allowlist</c> or <c>none</c>.</summary>
    [Required, MaxLength(20)] public string Mode { get; set; } = "public-only";
    public string RulesJson { get; set; } = "[]";
    public string WarningsJson { get; set; } = "[]";
    /// <summary>The admin's mode before the repository narrowed it.</summary>
    [MaxLength(20)] public string AdminMode { get; set; } = "public-only";
    public bool RepoNarrowed { get; set; }
}
