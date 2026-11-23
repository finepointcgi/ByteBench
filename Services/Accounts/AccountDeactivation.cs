using ByteBench.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Accounts;

/// <summary>
/// The one "deactivated account" mechanism, shared by the admin user page, LDAP sync and SCIM: <see cref="User.DeactivatedAt"/>
/// set, locked indefinitely, security stamp rotated (every browser session ends within a minute) and every personal access
/// token revoked. Password, LDAP, OIDC and SAML sign-in, git over HTTP/SSH, packages and the API all refuse a deactivated
/// or locked account.
/// </summary>
public static class AccountDeactivation
{
    /// <summary>Deactivates <paramref name="user"/>. Returns the number of access tokens revoked.</summary>
    public static async Task<int> DeactivateAsync(UserManager<User> users, AppDbContext db, User user, CancellationToken ct = default)
    {
        user.DeactivatedAt = DateTime.UtcNow;
        Check(await users.SetLockoutEnabledAsync(user, true));
        Check(await users.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue));
        Check(await users.UpdateSecurityStampAsync(user));
        return await db.PersonalAccessTokens.Where(t => t.UserId == user.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow), ct);
    }

    /// <summary>Clears the deactivation and lockout (revoked tokens stay revoked). Returns whether the account was deactivated.</summary>
    public static async Task<bool> ReactivateAsync(UserManager<User> users, User user)
    {
        var was = user.DeactivatedAt != null;
        user.DeactivatedAt = null;
        Check(await users.SetLockoutEndDateAsync(user, null)); // also saves DeactivatedAt
        Check(await users.ResetAccessFailedCountAsync(user));
        return was;
    }

    private static void Check(IdentityResult r)
    {
        if (!r.Succeeded) throw new InvalidOperationException(string.Join(" ", r.Errors.Select(e => e.Description)));
    }
}
