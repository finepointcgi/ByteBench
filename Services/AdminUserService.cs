using GitBench.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services;

/// <summary>One row of the user administration list.</summary>
public sealed record AdminUserRow(int Id, string UserName, string DisplayName, string? Email, string Color, bool IsAdmin,
    DateTimeOffset? LockoutEnd, DateTime? DeactivatedAt, bool TwoFactorEnabled, DateTime? LastSignIn, int ProjectCount, bool IsBot)
{
    public bool IsLockedOut(DateTimeOffset now) => LockoutEnd is { } end && end > now;
    public bool IsDeactivated => DeactivatedAt != null;
}

public sealed record AdminUserPage(List<AdminUserRow> Users, int Total);

/// <summary>
/// Site-admin user management for <c>/admin/users</c>: list, grant or revoke admin, lock, unlock, force sign-out,
/// reset two-factor and deactivate. Every action requires the acting user to be a site admin, and records an
/// <c>admin.user_*</c> audit event. Each action runs in its own DI scope so a long-lived circuit never writes through
/// a stale, tracked <see cref="User"/>.
/// </summary>
public class AdminUserService(IDbContextFactory<AppDbContext> dbFactory, IServiceScopeFactory scopes, ProjectAccess access, Audit.AuditLog audit)
{
    public async Task<AdminUserPage> SearchAsync(string? search, int page, int pageSize)
    {
        await RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.Users.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToUpperInvariant();
            q = q.Where(u => u.NormalizedUserName!.Contains(s) || u.NormalizedEmail!.Contains(s) || u.DisplayName.ToUpper().Contains(s));
        }
        var total = await q.CountAsync();
        pageSize = Math.Clamp(pageSize, 1, 200);
        var rows = await q.OrderBy(u => u.UserName).Skip((Math.Max(page, 1) - 1) * pageSize).Take(pageSize)
            .Select(u => new
            {
                u.Id, u.UserName, u.DisplayName, u.Email, u.Color, u.IsAdmin, u.LockoutEnd, u.DeactivatedAt, u.TwoFactorEnabled,
                Projects = db.ProjectMembers.Count(m => m.UserId == u.Id),
            }).ToListAsync();
        var ids = rows.Select(r => r.Id).ToList();
        // Last sign-in comes from the audit log (auth.sign_in), so it's as old as the log's retention allows.
        var lastSignIn = await db.AuditEvents.Where(e => e.Action == "auth.sign_in" && e.ActorId != null && ids.Contains(e.ActorId.Value))
            .GroupBy(e => e.ActorId!.Value).Select(g => new { Id = g.Key, At = g.Max(e => e.CreatedAt) })
            .ToDictionaryAsync(x => x.Id, x => x.At);
        return new(rows.Select(r => new AdminUserRow(r.Id, r.UserName ?? "", r.DisplayName, r.Email, r.Color, r.IsAdmin, r.LockoutEnd,
            r.DeactivatedAt, r.TwoFactorEnabled, lastSignIn.TryGetValue(r.Id, out var at) ? at : null, r.Projects,
            r.UserName == Ai.AiBot.UserName)).ToList(), total);
    }

    /// <summary>Grants or revokes site admin. The last remaining admin can't be revoked.</summary>
    public Task SetAdminAsync(int userId, bool isAdmin) => ActAsync(userId, isAdmin ? "admin.user_admin_granted" : "admin.user_admin_revoked",
        async (users, user, db, me) =>
        {
            if (user.IsAdmin == isAdmin) return null;
            if (!isAdmin && await db.Users.CountAsync(u => u.IsAdmin && u.Id != user.Id) == 0)
                throw new InvalidOperationException(user.Id == me
                    ? "You are the last site admin. Make someone else an admin before revoking your own admin rights."
                    : "This is the last site admin; GitBench needs at least one.");
            user.IsAdmin = isAdmin;
            Check(await users.UpdateAsync(user));
            return null;
        });

    /// <summary>Locks the account until <paramref name="until"/> (null: indefinitely) and signs it out everywhere.</summary>
    public Task LockAsync(int userId, DateTimeOffset? until) => ActAsync(userId, "admin.user_locked", async (users, user, _, me) =>
    {
        if (user.Id == me) throw new InvalidOperationException("You can't lock your own account.");
        Check(await users.SetLockoutEnabledAsync(user, true));
        Check(await users.SetLockoutEndDateAsync(user, until ?? DateTimeOffset.MaxValue));
        Check(await users.UpdateSecurityStampAsync(user)); // ends existing sessions
        return new { until = until?.UtcDateTime };
    });

    /// <summary>Clears the lockout (and deactivation) and the failed sign-in counter.</summary>
    public Task UnlockAsync(int userId) => ActAsync(userId, "admin.user_unlocked", async (users, user, _, _) =>
    {
        var wasDeactivated = user.DeactivatedAt != null;
        user.DeactivatedAt = null;
        Check(await users.SetLockoutEndDateAsync(user, null)); // also saves DeactivatedAt
        Check(await users.ResetAccessFailedCountAsync(user));
        return new { reactivated = wasDeactivated };
    });

    /// <summary>Signs the user out of every browser session by rotating the security stamp (tokens keep working).</summary>
    public Task ForceSignOutAsync(int userId) => ActAsync(userId, "admin.user_signed_out", async (users, user, _, _) =>
    {
        Check(await users.UpdateSecurityStampAsync(user));
        return null;
    });

    /// <summary>Turns off two-factor sign-in for a user who lost their device: new authenticator key, no recovery codes.</summary>
    public Task ResetTwoFactorAsync(int userId) => ActAsync(userId, "admin.user_2fa_reset", async (users, user, _, _) =>
    {
        await TwoFactor.ResetAsync(users, user);
        return null;
    });

    /// <summary>Deactivates the account: locked indefinitely, signed out everywhere, every access token revoked.</summary>
    public Task DeactivateAsync(int userId) => ActAsync(userId, "admin.user_deactivated", async (users, user, db, me) =>
    {
        if (user.Id == me) throw new InvalidOperationException("You can't deactivate your own account.");
        user.DeactivatedAt = DateTime.UtcNow;
        Check(await users.SetLockoutEnabledAsync(user, true));
        Check(await users.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue));
        Check(await users.UpdateSecurityStampAsync(user));
        var revoked = await db.PersonalAccessTokens.Where(t => t.UserId == user.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow));
        return new { tokensRevoked = revoked };
    });

    private async Task ActAsync(int userId, string action, Func<UserManager<User>, User, AppDbContext, int, Task<object?>> op)
    {
        var me = await RequireAdminAsync();
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new InvalidOperationException("That user no longer exists.");
        if (Ai.AiBot.IsBot(user)) throw new InvalidOperationException("The GitBench AI bot account can't be changed.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var details = await op(users, user, db, me);
        await audit.RecordAsync(action, "user", user.Id, details: new { user = user.UserName, extra = details }, actorId: me);
    }

    private async Task<int> RequireAdminAsync()
    {
        if (await access.ResolveUserIdAsync() is not { } me || !await access.IsAdminAsync(me))
            throw new ForbiddenException("Only site administrators can manage users.");
        return me;
    }

    private static void Check(IdentityResult r)
    {
        if (!r.Succeeded) throw new InvalidOperationException(string.Join(" ", r.Errors.Select(e => e.Description)));
    }
}

/// <summary>Shared two-factor helpers (used by the profile page and the admin reset).</summary>
public static class TwoFactor
{
    /// <summary>Identity's internal login provider name under which it stores the authenticator key and recovery codes.</summary>
    private const string InternalLoginProvider = "[AspNetUserStore]";

    /// <summary>Disables 2FA, replaces the authenticator key (so a lost device's codes stop working) and deletes the recovery codes.</summary>
    public static async Task ResetAsync(UserManager<User> users, User user)
    {
        Check(await users.SetTwoFactorEnabledAsync(user, false));
        Check(await users.ResetAuthenticatorKeyAsync(user)); // also rotates the security stamp
        Check(await users.RemoveAuthenticationTokenAsync(user, InternalLoginProvider, "RecoveryCodes"));
    }

    private static void Check(IdentityResult r)
    {
        if (!r.Succeeded) throw new InvalidOperationException(string.Join(" ", r.Errors.Select(e => e.Description)));
    }
}
