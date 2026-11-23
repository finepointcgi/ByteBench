using System.ComponentModel.DataAnnotations;

namespace ByteBench.Data;

/// <summary>
/// An MCP client registered through OAuth dynamic client registration (<c>POST /oauth/register</c>, RFC 7591). Public
/// clients only: there is no client secret, and every authorization needs PKCE.
/// </summary>
public class OAuthClient
{
    public int Id { get; set; }
    /// <summary>The public client identifier (<c>bbc_…</c>).</summary>
    [Required, MaxLength(64)] public string ClientId { get; set; } = "";
    /// <summary>What the client calls itself. Untrusted text: always HTML-escaped where it is shown.</summary>
    [Required, MaxLength(100)] public string ClientName { get; set; } = "";
    /// <summary>The registered redirect URIs, one per line. An authorization request must name one of them exactly.</summary>
    [Required, MaxLength(20_000)] public string RedirectUris { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public IReadOnlyList<string> RedirectUriList => RedirectUris.Split('\n', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// One user's consent for one client: the scopes (<see cref="TokenScopes.ReadApi"/>, <see cref="TokenScopes.WriteApi"/>) its
/// tokens carry. Created when an authorization code is exchanged. Revoking it (from the profile page, <c>/oauth/revoke</c>,
/// or reuse of a rotated refresh token) kills every token of the grant.
/// </summary>
public class OAuthGrant
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public OAuthClient Client { get; set; } = null!;
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public TokenScopes Scopes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    /// <summary>Why it was revoked: <c>user</c>, <c>client</c>, <c>refresh_reuse</c>, <c>code_reuse</c>.</summary>
    [MaxLength(40)] public string? RevokedReason { get; set; }
}

/// <summary>
/// An authorization code: single use, short-lived, bound to the client, the redirect URI, the PKCE challenge and the user
/// who approved it. Only the SHA-256 of the code is stored.
/// </summary>
public class OAuthCode
{
    public int Id { get; set; }
    [Required, MaxLength(32)] public byte[] CodeHash { get; set; } = [];
    public int ClientId { get; set; }
    public int UserId { get; set; }
    public TokenScopes Scopes { get; set; }
    [Required, MaxLength(2000)] public string RedirectUri { get; set; } = "";
    /// <summary>The S256 code challenge (base64url of SHA-256 of the verifier).</summary>
    [Required, MaxLength(128)] public string CodeChallenge { get; set; } = "";
    /// <summary>The RFC 8707 resource the client named, if any.</summary>
    [MaxLength(2000)] public string? Resource { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    /// <summary>The grant the code was exchanged for (revoked if the code is presented again).</summary>
    public int? GrantId { get; set; }
}

public enum OAuthTokenKind { Access = 0, Refresh = 1 }

/// <summary>
/// An access token (<c>bbo_…</c>, accepted only by <c>/mcp</c>) or a refresh token (<c>bbr_…</c>) of a grant. Only the
/// SHA-256 is stored. A refresh token is rotated on use; presenting a rotated one again revokes the whole grant.
/// </summary>
public class OAuthToken
{
    public int Id { get; set; }
    public int GrantId { get; set; }
    public OAuthGrant Grant { get; set; } = null!;
    public OAuthTokenKind Kind { get; set; }
    [Required, MaxLength(32)] public byte[] TokenHash { get; set; } = [];
    public TokenScopes Scopes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    /// <summary>Refresh tokens: when it was exchanged for a new pair.</summary>
    public DateTime? RotatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
