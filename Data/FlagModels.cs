using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Data;

/// <summary>The kind of value a feature flag serves. <see cref="Json"/> covers objects and arrays (and any valid JSON).</summary>
public enum FlagType { Boolean, String, Number, Json }

/// <summary>A client key can only evaluate flags; a server key can also download the rules and segments.</summary>
public enum SdkKeyKind { Client, Server }

/// <summary>
/// A feature flag defined per project. Its state (on or off, the value served when on, targeting rules) is per environment
/// (<see cref="FlagEnvironmentState"/>). Values are stored as JSON text.
/// </summary>
public class FeatureFlag
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    /// <summary>Stable identifier used by clients: lowercase letters, digits, '.', '_' and '-'.</summary>
    [MaxLength(64)] public string Key { get; set; } = "";
    [MaxLength(120)] public string Name { get; set; } = "";
    [MaxLength(2000)] public string? Description { get; set; }
    public FlagType Type { get; set; } = FlagType.Boolean;
    /// <summary>The value served in an environment where the flag is off (JSON).</summary>
    [MaxLength(4000)] public string OffValue { get; set; } = "false";
    /// <summary>Comma-separated numbers of issues of the same project this flag is linked to.</summary>
    [MaxLength(500)] public string? IssueNumbers { get; set; }
    public bool IsArchived { get; set; }
    public DateTime? ArchivedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int? CreatedById { get; set; }
    public User? CreatedBy { get; set; }
    /// <summary>When the flag's definition or any state of it last changed.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<FlagEnvironmentState> States { get; set; } = [];

    public IReadOnlyList<int> LinkedIssues =>
        (IssueNumbers ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).ToList();
}

/// <summary>An environment of a project's flags, named like the CI environments (<see cref="CiEnvironment"/>) of its repositories.</summary>
public class FlagEnvironment
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    [MaxLength(64)] public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Bumped whenever anything that decides an evaluation here changes (ETag and live-update basis).</summary>
    public long Version { get; set; } = 1;
}

/// <summary>A flag's state in one environment.</summary>
public class FlagEnvironmentState
{
    public int Id { get; set; }
    public int FlagId { get; set; }
    public FeatureFlag Flag { get; set; } = null!;
    public int EnvironmentId { get; set; }
    public FlagEnvironment Environment { get; set; } = null!;
    public bool Enabled { get; set; }
    /// <summary>Served (JSON) when the flag is on and no rule matches.</summary>
    [MaxLength(4000)] public string DefaultValue { get; set; } = "true";
    /// <summary>The ordered targeting rules as JSON (see <c>FlagRule</c>).</summary>
    public string RulesJson { get; set; } = "[]";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int? UpdatedById { get; set; }
    public User? UpdatedBy { get; set; }
    /// <summary>The last time an SDK evaluated the flag here (written at most about once an hour).</summary>
    public DateTime? LastEvaluatedAt { get; set; }
}

/// <summary>A named list of user ids (matched against the evaluation context's user id) that rules can target.</summary>
public class FlagSegment
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    [MaxLength(64)] public string Key { get; set; } = "";
    [MaxLength(120)] public string Name { get; set; } = "";
    [MaxLength(2000)] public string? Description { get; set; }
    /// <summary>JSON array of user id strings.</summary>
    public string MembersJson { get; set; } = "[]";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A read-only credential for evaluating flags in one environment. Only the SHA-256 hash of the secret is stored; the
/// plaintext is shown once, when the key is created or rotated.
/// </summary>
public class FlagSdkKey
{
    public int Id { get; set; }
    public int EnvironmentId { get; set; }
    public FlagEnvironment Environment { get; set; } = null!;
    [MaxLength(100)] public string Name { get; set; } = "";
    public SdkKeyKind Kind { get; set; }
    public byte[] KeyHash { get; set; } = [];
    /// <summary>The first characters of the secret, to recognise it.</summary>
    [MaxLength(16)] public string Prefix { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int? CreatedById { get; set; }
    public DateTime? RotatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public bool IsActive => RevokedAt == null;
}

/// <summary>One entry of a flag's history: who changed what, when, and the old and new value.</summary>
public class FlagChange
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int FlagId { get; set; }
    public FeatureFlag Flag { get; set; } = null!;
    public int? EnvironmentId { get; set; }
    [MaxLength(64)] public string? EnvironmentName { get; set; }
    public int? ActorId { get; set; }
    [MaxLength(100)] public string? ActorName { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    /// <summary>created, enabled, disabled, default_value, off_value, rules, details, archived, unarchived, issues.</summary>
    [MaxLength(32)] public string Kind { get; set; } = "";
    [MaxLength(8000)] public string? OldValue { get; set; }
    [MaxLength(8000)] public string? NewValue { get; set; }
}

public partial class AppDbContext
{
    public DbSet<FeatureFlag> FeatureFlags => Set<FeatureFlag>();
    public DbSet<FlagEnvironment> FlagEnvironments => Set<FlagEnvironment>();
    public DbSet<FlagEnvironmentState> FlagEnvironmentStates => Set<FlagEnvironmentState>();
    public DbSet<FlagSegment> FlagSegments => Set<FlagSegment>();
    public DbSet<FlagSdkKey> FlagSdkKeys => Set<FlagSdkKey>();
    public DbSet<FlagChange> FlagChanges => Set<FlagChange>();

    private static void ConfigureFlags(ModelBuilder b)
    {
        b.Entity<FeatureFlag>().HasIndex(f => new { f.ProjectId, f.Key }).IsUnique();
        b.Entity<FeatureFlag>().HasOne<Project>().WithMany().HasForeignKey(f => f.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<FeatureFlag>().HasOne(f => f.CreatedBy).WithMany().HasForeignKey(f => f.CreatedById).OnDelete(DeleteBehavior.SetNull);
        b.Entity<FeatureFlag>().HasMany(f => f.States).WithOne(s => s.Flag).HasForeignKey(s => s.FlagId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<FlagEnvironment>().HasIndex(e => new { e.ProjectId, e.Name }).IsUnique();
        b.Entity<FlagEnvironment>().HasOne<Project>().WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<FlagEnvironmentState>().HasIndex(s => new { s.FlagId, s.EnvironmentId }).IsUnique();
        b.Entity<FlagEnvironmentState>().HasOne(s => s.Environment).WithMany().HasForeignKey(s => s.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<FlagEnvironmentState>().HasOne(s => s.UpdatedBy).WithMany().HasForeignKey(s => s.UpdatedById).OnDelete(DeleteBehavior.SetNull);

        b.Entity<FlagSegment>().HasIndex(s => new { s.ProjectId, s.Key }).IsUnique();
        b.Entity<FlagSegment>().HasOne<Project>().WithMany().HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<FlagSdkKey>().HasIndex(k => k.KeyHash).IsUnique();
        b.Entity<FlagSdkKey>().HasOne(k => k.Environment).WithMany().HasForeignKey(k => k.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<FlagSdkKey>().HasOne<User>().WithMany().HasForeignKey(k => k.CreatedById).OnDelete(DeleteBehavior.SetNull);

        b.Entity<FlagChange>().HasIndex(c => new { c.FlagId, c.At });
        b.Entity<FlagChange>().HasOne(c => c.Flag).WithMany().HasForeignKey(c => c.FlagId).OnDelete(DeleteBehavior.Cascade);
    }
}
