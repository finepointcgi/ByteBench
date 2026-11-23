using System.ComponentModel.DataAnnotations;

namespace ByteBench.Data;

public partial class User
{
    /// <summary>
    /// Set when an admin gave the account a temporary password. Web sign-in then lands on the "choose a new password" page
    /// and nothing else is reachable until it is changed. Cleared by a password change or reset.
    /// </summary>
    public bool MustChangePassword { get; set; }
}

/// <summary>A site-wide setting an admin can change without a restart (key/value, see <see cref="ByteBench.Services.Accounts.SiteSettings"/>).</summary>
public class SiteSetting
{
    [Key, MaxLength(100)] public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int? UpdatedById { get; set; }
}

/// <summary>
/// An admin-created, single-use, expiring registration link that works while registration is closed. Only the SHA-256 of the
/// token is stored. When <see cref="Email"/> is set the invitation can only be used with that address.
/// </summary>
public class RegistrationInvite
{
    public int Id { get; set; }
    [Required, MaxLength(64)] public string TokenHash { get; set; } = "";
    [MaxLength(256)] public string? Email { get; set; }
    public int CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public int? UsedById { get; set; }
    public DateTime? RevokedAt { get; set; }
}
