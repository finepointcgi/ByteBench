using System.Security.Cryptography;
using System.Text;
using ByteBench.Data;
using ByteBench.Services.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Accounts;

public enum RegistrationMode { Open, Closed }

/// <summary>The effective registration rules and where they come from.</summary>
public sealed record RegistrationState(RegistrationMode Mode, IReadOnlyList<string> AllowedDomains, bool LockedByConfig, bool ApplyToSso)
{
    public bool IsOpen => Mode == RegistrationMode.Open;
}

/// <summary>One invitation as the admin page lists it (the token itself is only shown once, when it is created).</summary>
public sealed record InviteRow(int Id, string? Email, string CreatedBy, DateTime CreatedAt, DateTime ExpiresAt, DateTime? UsedAt, DateTime? RevokedAt)
{
    public bool IsActive(DateTime now) => UsedAt == null && RevokedAt == null && ExpiresAt > now;
}

/// <summary>
/// Who may create an account. The state is the <c>Registration</c> configuration plus (unless <c>Registration:LockedByConfig</c>)
/// the admin's saved values in <see cref="SiteSetting"/>. Applies to <c>/account/register</c> and, unless
/// <c>Registration:ApplyToSso</c> is false, to first-time SSO and LDAP account creation. Admins creating users and existing
/// accounts are never affected. A valid invitation lets one person register while registration is closed.
/// </summary>
public class RegistrationPolicy(SiteSettings settings, IDbContextFactory<AppDbContext> dbFactory, IOptionsMonitor<RegistrationOptions> options,
    ProjectAccess access, AuditLog audit, TimeProvider? time = null)
{
    public const string ModeKey = "registration.mode";
    public const string DomainsKey = "registration.allowed_domains";
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    public async Task<RegistrationState> GetStateAsync(CancellationToken ct = default)
    {
        var o = options.CurrentValue;
        var configMode = ParseMode(o.Mode);
        var configDomains = NormalizeDomains(o.AllowedDomains);
        if (o.LockedByConfig) return new(configMode ?? RegistrationMode.Open, configDomains, true, o.ApplyToSso);

        var stored = await settings.GetAsync(ModeKey, ct);
        var storedDomains = await settings.GetAsync(DomainsKey, ct);
        return new(ParseMode(stored) ?? configMode ?? RegistrationMode.Open,
            storedDomains != null ? NormalizeDomains(storedDomains.Split(['\n', ',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)) : configDomains,
            false, o.ApplyToSso);
    }

    public static RegistrationMode? ParseMode(string? s) =>
        Enum.TryParse<RegistrationMode>(s?.Trim(), ignoreCase: true, out var m) ? m : null;

    public static IReadOnlyList<string> NormalizeDomains(IEnumerable<string> raw) =>
        raw.Select(d => d.Trim().TrimStart('@').ToLowerInvariant()).Where(d => d.Length > 0).Distinct().ToList();

    public static string? DomainOf(string? email) =>
        email is { } e && e.LastIndexOf('@') is > 0 and var at && at < e.Length - 1 ? e[(at + 1)..].Trim().ToLowerInvariant() : null;

    // ------------------------------------------------------------------ checks

    /// <summary>The reason <paramref name="email"/> may not register on the web, or null. A valid invitation lifts both rules.</summary>
    public async Task<string?> CheckRegistrationAsync(string? email, string? inviteToken, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(inviteToken) && await FindActiveInviteAsync(inviteToken, email, ct) != null) return null;
        var state = await GetStateAsync(ct);
        if (!state.IsOpen)
            return string.IsNullOrEmpty(inviteToken)
                ? "Registration is closed on this server. Ask a site admin for an invitation or an account."
                : "This invitation is invalid, expired, already used or for another email address. Ask a site admin for a new one.";
        return DomainError(state, email);
    }

    /// <summary>For showing the register page: the "closed" message when the page should not offer the form (no valid invitation), else null.</summary>
    public async Task<string?> CheckPageAsync(string? inviteToken, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(inviteToken) && await FindActiveInviteAsync(inviteToken, null, ct, ignoreEmail: true) != null) return null;
        var state = await GetStateAsync(ct);
        if (state.IsOpen) return null;
        return string.IsNullOrEmpty(inviteToken)
            ? "Registration is closed on this server. Ask a site admin for an invitation or an account."
            : "This invitation is invalid, expired or already used. Ask a site admin for a new one.";
    }

    /// <summary>The reason SSO or LDAP may not create an account for <paramref name="email"/>, or null.</summary>
    public async Task<string?> CheckProvisioningAsync(string? email, CancellationToken ct = default)
    {
        var state = await GetStateAsync(ct);
        if (!state.ApplyToSso) return null;
        if (!state.IsOpen) return "Registration is closed on this server.";
        return DomainError(state, email);
    }

    private static string? DomainError(RegistrationState state, string? email)
    {
        if (state.AllowedDomains.Count == 0) return null;
        var domain = DomainOf(email);
        return domain != null && state.AllowedDomains.Contains(domain)
            ? null
            : $"Only email addresses at {string.Join(", ", state.AllowedDomains)} can register on this server.";
    }

    // ------------------------------------------------------------------ admin: settings

    /// <summary>Saves the mode and allowed domains (site admins only). Ignored values: see <see cref="RegistrationState.LockedByConfig"/>.</summary>
    public async Task SetAsync(RegistrationMode mode, IEnumerable<string> domains)
    {
        var me = await RequireAdminAsync();
        if ((await GetStateAsync()).LockedByConfig)
            throw new InvalidOperationException("Registration is fixed by the Registration:LockedByConfig setting; change the configuration instead.");
        var list = NormalizeDomains(domains);
        foreach (var d in list)
            if (!d.Contains('.') && d != "localhost" || d.Contains(' ') || d.Contains('@'))
                throw new InvalidOperationException($"'{d}' is not a domain name (use e.g. example.com).");
        await settings.SetAsync(ModeKey, mode.ToString(), me);
        await settings.SetAsync(DomainsKey, string.Join("\n", list), me);
        await audit.RecordAsync("admin.registration_changed", "site", "registration", details: new { mode = mode.ToString(), domains = list }, actorId: me);
    }

    // ------------------------------------------------------------------ invitations

    public async Task<(RegistrationInvite Invite, string Token)> CreateInviteAsync(string? email, int days)
    {
        var me = await RequireAdminAsync();
        email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        if (email != null && (!email.Contains('@') || email.Length > 256)) throw new InvalidOperationException("Enter a valid email address, or leave it empty.");
        days = Math.Clamp(days, 1, 90);
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var invite = new RegistrationInvite
        {
            TokenHash = Hash(token), Email = email, CreatedById = me,
            CreatedAt = clock.GetUtcNow().UtcDateTime, ExpiresAt = clock.GetUtcNow().UtcDateTime.AddDays(days),
        };
        await using var db = await dbFactory.CreateDbContextAsync();
        db.RegistrationInvites.Add(invite);
        await db.SaveChangesAsync();
        await audit.RecordAsync("admin.invite_created", "invite", invite.Id, details: new { email, days }, actorId: me);
        return (invite, token);
    }

    public async Task<List<InviteRow>> ListInvitesAsync(int take = 50)
    {
        await RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.RegistrationInvites.AsNoTracking().OrderByDescending(i => i.Id).Take(take)
            .Select(i => new InviteRow(i.Id, i.Email, db.Users.Where(u => u.Id == i.CreatedById).Select(u => u.UserName).FirstOrDefault() ?? "", i.CreatedAt, i.ExpiresAt, i.UsedAt, i.RevokedAt))
            .ToListAsync();
    }

    public async Task RevokeInviteAsync(int id)
    {
        var me = await RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var n = await db.RegistrationInvites.Where(i => i.Id == id && i.UsedAt == null && i.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.RevokedAt, clock.GetUtcNow().UtcDateTime));
        if (n > 0) await audit.RecordAsync("admin.invite_revoked", "invite", id, actorId: me);
    }

    /// <summary>The invitation for <paramref name="token"/> when it is unused, unrevoked, unexpired and fits <paramref name="email"/>.</summary>
    public async Task<RegistrationInvite?> FindActiveInviteAsync(string? token, string? email, CancellationToken ct = default, bool ignoreEmail = false)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = Hash(token.Trim());
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var invite = await db.RegistrationInvites.AsNoTracking().FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (invite == null || invite.UsedAt != null || invite.RevokedAt != null || invite.ExpiresAt <= now) return null;
        if (!ignoreEmail && invite.Email != null && !string.Equals(invite.Email, email?.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
        return invite;
    }

    /// <summary>Marks the invitation used, once: false when someone else got there first.</summary>
    public async Task<bool> TryConsumeInviteAsync(int inviteId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        return await db.RegistrationInvites.Where(i => i.Id == inviteId && i.UsedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.UsedAt, now), ct) == 1;
    }

    /// <summary>Records who registered with the invitation, or gives it back when the registration failed.</summary>
    public async Task FinishInviteAsync(int inviteId, int? userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (userId is { } id)
        {
            await db.RegistrationInvites.Where(i => i.Id == inviteId).ExecuteUpdateAsync(s => s.SetProperty(i => i.UsedById, id), ct);
            await audit.RecordAsync("auth.invite_used", "invite", inviteId, actorId: id);
        }
        else await db.RegistrationInvites.Where(i => i.Id == inviteId).ExecuteUpdateAsync(s => s.SetProperty(i => i.UsedAt, (DateTime?)null), ct);
    }

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private async Task<int> RequireAdminAsync()
    {
        if (await access.ResolveUserIdAsync() is not { } me || !await access.IsAdminAsync(me))
            throw new ForbiddenException("Only site administrators can change registration.");
        return me;
    }
}
