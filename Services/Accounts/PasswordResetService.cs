using System.Diagnostics;
using ByteBench.Data;
using ByteBench.Services.Audit;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Accounts;

public enum ResetOutcome { Done, InvalidLink, WeakPassword, TooManyAttempts }

public sealed record ResetResult(ResetOutcome Outcome, IReadOnlyList<string> Errors);

/// <summary>
/// "Forgot password": a neutral request step that emails a single-use, time-limited link, and a reset step that enforces the
/// password policy, signs out every other session (security stamp), clears the lockout and tells the user by email.
/// <para>
/// Which accounts may be reset: the account exists, has a local password (SSO and LDAP accounts don't), isn't a system
/// account, isn't deactivated and isn't locked by an admin (a lockout longer than the automatic five-minute one). Failing any of
/// these changes nothing the requester can see. Two-factor sign-in is untouched: after a reset the user still needs the
/// authenticator code (or a recovery code) to sign in.
/// </para>
/// </summary>
public class PasswordResetService(UserManager<User> users, AccountEmails emails, AccountThrottle throttle, IOptionsMonitor<AccountOptions> options,
    IOptions<IdentityOptions> identity, AuditLog audit, ILogger<PasswordResetService> log, TimeProvider? time = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private const string Purpose = "ResetPassword"; // UserManager.ResetPasswordTokenPurpose

    /// <summary>Whether the account may be reset by anyone (itself, or an admin sending the link).</summary>
    public bool CanReset(User? user)
    {
        if (user == null || string.IsNullOrEmpty(user.Email) || user.PasswordHash == null || SystemAccounts.IsSystem(user) || user.DeactivatedAt != null) return false;
        // A brute-force lockout ends within the default lockout span; anything longer was set by an admin.
        if (user.LockoutEnd is { } end && end - clock.GetUtcNow() > identity.Value.Lockout.DefaultLockoutTimeSpan + TimeSpan.FromSeconds(30)) return false;
        return true;
    }

    /// <summary>
    /// Handles the "forgot password" form. Always looks the same to the caller: nothing says whether the address has an account.
    /// The one visible refusal is the per-IP limit (it says nothing about any account). Per-account limits are silent.
    /// </summary>
    public async Task<bool> RequestAsync(string? email, string? ip)
    {
        var started = Stopwatch.GetTimestamp();
        var o = options.CurrentValue;
        try
        {
            if (!throttle.TryHit("reset:ip:" + (ip ?? "?"), o.MaxRequestsPerIp, o.ThrottleWindow)) return false;
            email = email?.Trim();
            var user = string.IsNullOrEmpty(email) ? null : await users.FindByEmailAsync(email);
            if (!CanReset(user))
            {
                if (user != null) log.LogInformation("Password reset requested for an account that can't be reset by email ({Reason})", user.PasswordHash == null ? "no local password" : "locked, deactivated or system account");
                return true;
            }
            if (!throttle.TryHit("reset:acct:" + user!.Id, o.MaxRequestsPerAccount, o.ThrottleWindow)) return true;
            var token = await users.GeneratePasswordResetTokenAsync(user);
            var sent = emails.SendPasswordReset(user, token, o.PasswordResetMinutes, byAdmin: false);
            await audit.RecordAsync("auth.password_reset_requested", "user", user.Id, details: new { emailSent = sent }, actorId: user.Id);
            return true;
        }
        finally { await PadAsync(started, o.ResponseFloorMs); }
    }

    /// <summary>True when <paramref name="token"/> is currently valid for the user (used to show the form or "link expired").</summary>
    public async Task<bool> IsLinkValidAsync(int userId, string? token)
    {
        if (string.IsNullOrEmpty(token) || await users.FindByIdAsync(userId.ToString()) is not { } user || !CanReset(user)) return false;
        return await users.VerifyUserTokenAsync(user, PasswordResetTokenProvider.ProviderName, Purpose, token);
    }

    public async Task<ResetResult> ResetAsync(int userId, string? token, string newPassword, string? ip)
    {
        var o = options.CurrentValue;
        if (!throttle.TryHit("reset-use:ip:" + (ip ?? "?"), Math.Max(o.MaxRequestsPerIp * 3, 1), o.ThrottleWindow))
            return new(ResetOutcome.TooManyAttempts, ["Too many attempts. Try again in a few minutes."]);
        var user = await users.FindByIdAsync(userId.ToString());
        if (string.IsNullOrEmpty(token) || !CanReset(user)) return new(ResetOutcome.InvalidLink, []);

        var result = await users.ResetPasswordAsync(user!, token, newPassword);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken))) return new(ResetOutcome.InvalidLink, []);
            return new(ResetOutcome.WeakPassword, result.Errors.Select(e => e.Description).ToList());
        }

        // The reset proved control of the mailbox, ends other sessions, and lifts a failed-attempts lockout.
        user!.MustChangePassword = false;
        user.EmailConfirmed = true;
        await users.UpdateAsync(user);
        await users.UpdateSecurityStampAsync(user);
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        await audit.RecordAsync("auth.password_reset", "user", user.Id, actorId: user.Id);
        emails.SendPasswordChanged(user, "Your ByteBench password was just changed using a password reset link.");
        return new(ResetOutcome.Done, []);
    }

    /// <summary>Sends the reset link for an admin (the caller has checked the admin's rights). Throws when it can't be sent.</summary>
    public async Task SendForAdminAsync(User user)
    {
        if (!emails.IsEnabled) throw new InvalidOperationException("Email is switched off on this server (Email:Enabled), so no reset email can be sent. Set a temporary password instead.");
        if (user.PasswordHash == null) throw new InvalidOperationException("This account has no ByteBench password (it signs in with SSO or LDAP), so there is nothing to reset.");
        if (!CanReset(user)) throw new InvalidOperationException("This account is deactivated or locked by an admin, or has no email address. Unlock it first.");
        var token = await users.GeneratePasswordResetTokenAsync(user);
        if (!emails.SendPasswordReset(user, token, options.CurrentValue.PasswordResetMinutes, byAdmin: true))
            throw new InvalidOperationException("The email couldn't be queued (the queue is full). Try again in a moment.");
    }

    internal static async Task PadAsync(long startedTimestamp, int floorMs)
    {
        if (floorMs <= 0) return;
        var left = TimeSpan.FromMilliseconds(floorMs) - Stopwatch.GetElapsedTime(startedTimestamp);
        // A little jitter so the floor itself isn't a fingerprint.
        left += TimeSpan.FromMilliseconds(Random.Shared.Next(0, Math.Max(1, floorMs / 8)));
        if (left > TimeSpan.Zero) await Task.Delay(left);
    }
}
