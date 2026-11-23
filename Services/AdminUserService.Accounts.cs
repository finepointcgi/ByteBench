using System.Security.Cryptography;
using ByteBench.Data;
using ByteBench.Services.Accounts;
using Microsoft.AspNetCore.Identity;

namespace ByteBench.Services;

/// <summary>The one-time result of creating a user or setting a temporary password. The password is never stored or logged.</summary>
public sealed record TemporaryPassword(string UserName, string Password);

public partial class AdminUserService
{
    /// <summary>Emails the user a password reset link (the same single-use, time-limited link as "Forgot password").</summary>
    public Task SendPasswordResetAsync(int userId) => ActScopedAsync(userId, "admin.user_reset_email_sent", async (sp, user, _) =>
    {
        await sp.GetRequiredService<PasswordResetService>().SendForAdminAsync(user);
        return null;
    });

    /// <summary>
    /// Replaces the password with a generated one, returned once. The user must choose their own at the next web sign-in, and every
    /// session ends. Refused for accounts without a local password (SSO, LDAP) and for the admin's own account.
    /// </summary>
    public async Task<TemporaryPassword> SetTemporaryPasswordAsync(int userId)
    {
        string? password = null;
        string name = "";
        await ActScopedAsync(userId, "admin.user_temp_password", async (sp, user, me) =>
        {
            if (user.Id == me) throw new InvalidOperationException("Change your own password on your profile page.");
            var users = sp.GetRequiredService<UserManager<User>>();
            if (user.PasswordHash == null) throw new InvalidOperationException("This account has no ByteBench password (it signs in with SSO or LDAP).");
            password = GeneratePassword();
            var token = await users.GeneratePasswordResetTokenAsync(user);
            Check(await users.ResetPasswordAsync(user, token, password));
            user.MustChangePassword = true;
            Check(await users.UpdateAsync(user));
            Check(await users.UpdateSecurityStampAsync(user));
            sp.GetRequiredService<AccountEmails>().SendPasswordChanged(user, "A site administrator set a temporary password on your account. You'll choose a new one the next time you sign in.");
            name = user.UserName ?? "";
            return new { mustChange = true };
        });
        return new TemporaryPassword(name, password!);
    }

    /// <summary>Marks the user's email address as confirmed (for when the confirmation email can't reach them).</summary>
    public Task MarkEmailConfirmedAsync(int userId) => ActAsync(userId, "admin.user_email_confirmed", async (users, user, _, _) =>
    {
        if (user.EmailConfirmed) return new { already = true };
        user.EmailConfirmed = true;
        Check(await users.UpdateAsync(user));
        return null;
    });

    /// <summary>
    /// Creates an account (not subject to closed registration or the domain list): email counts as confirmed, a generated
    /// temporary password is returned once and must be changed at first sign-in.
    /// </summary>
    public async Task<TemporaryPassword> CreateUserAsync(string userName, string displayName, string email, bool isAdmin)
    {
        var me = await RequireAdminAsync();
        userName = userName.Trim(); email = email.Trim(); displayName = displayName.Trim();
        if (userName.Length is < 2 or > 40 || !System.Text.RegularExpressions.Regex.IsMatch(userName, "^[A-Za-z0-9._-]+$"))
            throw new InvalidOperationException("The username needs 2 to 40 letters, digits, '.', '_' or '-'.");
        if (displayName.Length is 0 or > 80) throw new InvalidOperationException("Enter a display name (up to 80 characters).");
        if (!email.Contains('@') || email.Length > 256) throw new InvalidOperationException("Enter a valid email address.");
        if (SystemAccounts.IsSystem(userName)) throw new InvalidOperationException("That username is reserved for a ByteBench system account.");
        await using var scope = Scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = new User
        {
            UserName = userName, Email = email, DisplayName = displayName, Color = Components.Account.AvatarPalette.Random(),
            EmailConfirmed = true, MustChangePassword = true, IsAdmin = isAdmin, LockoutEnabled = true,
        };
        var password = GeneratePassword();
        Check(await users.CreateAsync(user, password));
        await AuditLog.RecordAsync("admin.user_created", "user", user.Id, details: new { user = user.UserName, admin = isAdmin }, actorId: me);
        return new TemporaryPassword(user.UserName!, password);
    }

    /// <summary>A random 16-character password with upper- and lowercase letters and digits (no look-alike characters).</summary>
    public static string GeneratePassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnpqrstuvwxyz", digits = "23456789";
        var all = upper + lower + digits;
        var chars = new List<char> { upper[RandomNumberGenerator.GetInt32(upper.Length)], lower[RandomNumberGenerator.GetInt32(lower.Length)], digits[RandomNumberGenerator.GetInt32(digits.Length)] };
        while (chars.Count < 16) chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);
        // Fisher-Yates so the three required characters aren't always first.
        for (var i = chars.Count - 1; i > 0; i--) { var j = RandomNumberGenerator.GetInt32(i + 1); (chars[i], chars[j]) = (chars[j], chars[i]); }
        return new string(chars.ToArray());
    }

    /// <summary>Like <c>ActAsync</c> but hands the operation the whole scope (for services that must share the scope's <see cref="UserManager{User}"/>).</summary>
    private async Task ActScopedAsync(int userId, string action, Func<IServiceProvider, User, int, Task<object?>> op)
    {
        var me = await RequireAdminAsync();
        await using var scope = Scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new InvalidOperationException("That user no longer exists.");
        if (SystemAccounts.IsSystem(user)) throw new InvalidOperationException("ByteBench's system bot accounts can't be changed.");
        var details = await op(scope.ServiceProvider, user, me);
        await AuditLog.RecordAsync(action, "user", user.Id, details: new { user = user.UserName, extra = details }, actorId: me);
    }
}
