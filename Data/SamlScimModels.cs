using System.ComponentModel.DataAnnotations;

namespace ByteBench.Data;

/// <summary>What a <see cref="SamlMessageRecord"/> remembers.</summary>
public enum SamlMessageKind
{
    /// <summary>An AuthnRequest this server sent; a response must answer it (InResponseTo) exactly once.</summary>
    AuthnRequest = 0,
    /// <summary>An assertion ID that was used to sign in; the same assertion is refused until it expires (replay).</summary>
    Assertion = 1,
    /// <summary>A LogoutRequest this server sent.</summary>
    LogoutRequest = 2,
}

/// <summary>
/// SAML message ids the server must remember for a short time: the AuthnRequests it sent (so a response is accepted only
/// in reply to one of them, once) and the assertion ids it accepted (so a captured assertion can't be replayed). Rows
/// expire after a few minutes and are pruned on the next sign-in.
/// </summary>
public class SamlMessageRecord
{
    /// <summary>The XML ID of the request or assertion (unique per kind).</summary>
    [Key, MaxLength(160)] public string Id { get; set; } = "";
    public SamlMessageKind Kind { get; set; }
    /// <summary>AuthnRequest: the same-site path to return to after sign-in.</summary>
    [MaxLength(2048)] public string? ReturnUrl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    /// <summary>AuthnRequest: when a response answered it (it can't be answered twice).</summary>
    public DateTime? UsedAt { get; set; }
}

/// <summary>
/// A bearer token for the SCIM 2.0 provisioning API (<c>/scim/v2</c>). Only the SHA-256 of the token is stored; the token
/// is shown once when it is created or rotated.
/// </summary>
public class ScimToken
{
    public int Id { get; set; }
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    /// <summary>Lowercase hex SHA-256 of the whole token.</summary>
    [Required, MaxLength(64)] public string TokenHash { get; set; } = "";
    /// <summary>The first characters of the token (<c>bbscim_ab12</c>), to tell tokens apart.</summary>
    [Required, MaxLength(16)] public string Prefix { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int? CreatedById { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

/// <summary>
/// The SCIM view of a ByteBench account that an identity provider provisioned or matched: the IdP's <c>userName</c>
/// (often an email address, which isn't a valid ByteBench username) and its <c>externalId</c>.
/// </summary>
public class ScimIdentity
{
    [Key, System.ComponentModel.DataAnnotations.Schema.DatabaseGenerated(System.ComponentModel.DataAnnotations.Schema.DatabaseGeneratedOption.None)]
    public int UserId { get; set; }
    public User? User { get; set; }
    [Required, MaxLength(256)] public string UserName { get; set; } = "";
    /// <summary>Upper-cased <see cref="UserName"/>, for case-insensitive lookups (SCIM userName is case-insensitive).</summary>
    [Required, MaxLength(256)] public string NormalizedUserName { get; set; } = "";
    [MaxLength(256)] public string? ExternalId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
