using System.ComponentModel.DataAnnotations;

namespace ByteBench.Data;

/// <summary>
/// A ByteBench group that SCIM manages: one the identity provider created through <c>POST /scim/v2/Groups</c>, or an
/// existing group adopted through an explicit <c>Scim:GroupMappings</c> entry. SCIM may change only these groups.
/// </summary>
public class ScimGroup
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public Group Group { get; set; } = null!;
    /// <summary>The IdP's displayName (the group's name may be cut to 100 characters; this is what SCIM sees and filters on).</summary>
    [Required, MaxLength(256)] public string DisplayName { get; set; } = "";
    /// <summary>Upper-cased <see cref="DisplayName"/>, unique, for case-insensitive lookups.</summary>
    [Required, MaxLength(256)] public string NormalizedDisplayName { get; set; } = "";
    [MaxLength(256)] public string? ExternalId { get; set; }
    /// <summary>True when an existing group was adopted through a mapping (DELETE then only drops the association).</summary>
    public bool Adopted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
