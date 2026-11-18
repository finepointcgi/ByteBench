using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services;

/// <summary>
/// Personal access tokens for Git over HTTP. A token is <c>gbp_</c> + 32 random bytes in base64url. Only its SHA-256
/// hash is stored; the plaintext is returned once, from <see cref="CreateAsync"/>.
/// </summary>
public class AccessTokenService(IDbContextFactory<AppDbContext> dbFactory, Audit.AuditLog audit)
{
    public const string TokenPrefix = "gbp_";
    private const int DisplayPrefixLength = 10; // "gbp_" + 6 characters

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public async Task<List<PersonalAccessToken>> ListAsync(int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.PersonalAccessTokens.Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedAt).ToListAsync();
    }

    /// <summary>Creates a token and returns it with its plaintext value, which can't be recovered later.</summary>
    public async Task<(PersonalAccessToken Token, string Plaintext)> CreateAsync(int userId, string name, TokenScopes scopes, DateTime? expiresAtUtc)
    {
        name = name.Trim();
        if (name.Length is 0 or > 100) throw new InvalidOperationException("Name is required (max 100 characters).");
        scopes &= TokenScopes.ReadRepository | TokenScopes.WriteRepository | TokenScopes.ReadApi | TokenScopes.WriteApi;
        if (scopes == TokenScopes.None) throw new InvalidOperationException("Choose at least one scope.");
        if (expiresAtUtc != null && expiresAtUtc <= DateTime.UtcNow) throw new InvalidOperationException("The expiry date must be in the future.");

        var plaintext = TokenPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var token = new PersonalAccessToken
        {
            UserId = userId, Name = name, Scopes = scopes, ExpiresAt = expiresAtUtc,
            TokenHash = Hash(plaintext), Prefix = plaintext[..DisplayPrefixLength],
        };
        await using var db = await dbFactory.CreateDbContextAsync();
        db.PersonalAccessTokens.Add(token);
        await db.SaveChangesAsync();
        await audit.RecordAsync("token.created", "access_token", token.Id, details: new { tokenName = name, scopes = scopes.ToString(), expiresAt = expiresAtUtc }, actorId: userId);
        return (token, plaintext);
    }

    /// <summary>
    /// For <c>Authorization: Bearer</c> (REST API): the active token matching <paramref name="presented"/>, with its user,
    /// or null. The token is looked up by its SHA-256 hash, so it doesn't reveal anything to timing. Updates LastUsedAt.
    /// </summary>
    public async Task<PersonalAccessToken?> FindActiveAsync(string presented)
    {
        if (!presented.StartsWith(TokenPrefix, StringComparison.Ordinal)) return null;
        var hash = Hash(presented);
        var now = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync();
        var token = await db.PersonalAccessTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == hash && t.RevokedAt == null);
        if (token == null || !token.IsActive(now)) return null;
        token.LastUsedAt = now;
        await db.SaveChangesAsync();
        return token;
    }

    public async Task RevokeAsync(int userId, int tokenId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var n = await db.PersonalAccessTokens.Where(t => t.Id == tokenId && t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow));
        if (n > 0) await audit.RecordAsync("token.revoked", "access_token", tokenId, actorId: userId);
    }

    /// <summary>
    /// Returns the user's active (unrevoked, unexpired) token matching <paramref name="presented"/>, or null.
    /// Hashes are compared in constant time, and every candidate is compared. Updates LastUsedAt on success.
    /// </summary>
    public async Task<PersonalAccessToken?> ValidateAsync(int userId, string presented)
    {
        if (!presented.StartsWith(TokenPrefix, StringComparison.Ordinal)) return null;
        var hash = Hash(presented);
        var now = DateTime.UtcNow;

        await using var db = await dbFactory.CreateDbContextAsync();
        var candidates = await db.PersonalAccessTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync();
        PersonalAccessToken? match = null;
        foreach (var t in candidates)
            if (CryptographicOperations.FixedTimeEquals(t.TokenHash, hash) && t.IsActive(now)) match = t;
        if (match == null) return null;

        match.LastUsedAt = now;
        await db.SaveChangesAsync();
        return match;
    }
}
