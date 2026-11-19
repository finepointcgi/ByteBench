using Microsoft.AspNetCore.DataProtection;

namespace GitBench.Services.Pages;

/// <summary>What a Pages token may be used for; each purpose has its own key, so one can never be replayed as another.</summary>
public enum PagesTokenKind
{
    /// <summary>In the URL of a members-only site in path mode (<c>/-/t/{token}/</c>); minted by the sandboxed path itself.</summary>
    PathLink,
    /// <summary>One-time-ish code handed from GitBench to a dedicated host; exchanged there for a cookie. Lives 1 minute.</summary>
    HostExchange,
    /// <summary>The cookie of a dedicated host (host-only, HttpOnly).</summary>
    HostCookie,
}

/// <summary>
/// Signed, expiring, tamper-proof tokens (ASP.NET Data Protection) naming a user and a repository. They only prove "this
/// user was allowed to open this repository's site at <c>issued</c>"; every request still asks ProjectAccess, so removing a
/// member takes effect at once. The GitBench sign-in cookie is never accepted on a dedicated host and never reaches a
/// sandboxed page's scripts.
/// </summary>
public sealed class PagesAccessTokens(IDataProtectionProvider dataProtection)
{
    private ITimeLimitedDataProtector Protector(PagesTokenKind kind) =>
        dataProtection.CreateProtector("GitBench.Pages.v1." + kind).ToTimeLimitedDataProtector();

    public string Create(PagesTokenKind kind, int userId, int repositoryId, TimeSpan lifetime) =>
        Protector(kind).Protect($"{userId}:{repositoryId}", lifetime);

    /// <summary>The user id if the token is valid, unexpired and for <paramref name="repositoryId"/>; else null.</summary>
    public int? Validate(PagesTokenKind kind, string? token, int repositoryId)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 1000) return null;
        try
        {
            var parts = Protector(kind).Unprotect(token).Split(':');
            return parts.Length == 2 && int.TryParse(parts[0], out var u) && int.TryParse(parts[1], out var r) && r == repositoryId ? u : null;
        }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }
}
