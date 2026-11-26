using System.Security.Claims;
using System.Text.RegularExpressions;
using ByteBench.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Sso;

/// <summary>What an identity provider told us about the user, after the claim mappings.</summary>
public sealed record ExternalIdentity(string Provider, string ProviderKey, string? Email, bool EmailVerified, string? UserName,
    string? DisplayName, IReadOnlyList<string> Groups, IReadOnlyList<string> Amr)
{
    public string? EmailDomain => Email is { } e && e.LastIndexOf('@') is var at and > 0 ? e[(at + 1)..].ToLowerInvariant() : null;
}

/// <summary>A provider shown under "Connected accounts" on the profile.</summary>
public sealed record ConnectedAccount(SsoProviderOptions Provider, bool Linked, string? DisplayKey);

/// <summary>Why an SSO sign-in or link was refused. The code goes in the redirect URL; <see cref="Message"/> is shown.</summary>
public static class SsoErrors
{
    public const string Failed = "failed", Cancelled = "cancelled", Domain = "domain", Unverified = "unverified", NoEmail = "no_email",
        Exists = "exists", NoAccount = "no_account", Locked = "locked", NotAllowed = "not_allowed", LinkedElsewhere = "linked_elsewhere",
        UnknownProvider = "unknown_provider", Expired = "expired", LastLogin = "last_login", RegistrationClosed = "registration_closed";

    public static string Message(string? code, string provider) => code switch
    {
        Cancelled => $"Sign-in with {provider} was cancelled.",
        Domain => $"Your {provider} account's email domain isn't allowed to sign in to ByteBench.",
        Unverified => $"Your {provider} account's email address isn't verified, so ByteBench can't use it. Verify it with {provider} and try again.",
        NoEmail => $"{provider} didn't share an email address, which ByteBench needs.",
        Exists => $"A ByteBench account with this email already exists. Sign in with your password, then connect {provider} under Profile → Connected accounts.",
        NoAccount => $"No ByteBench account is connected to this {provider} account, and new accounts can't be created this way. Ask a site admin.",
        Locked => "This account is locked or deactivated.",
        NotAllowed => "This account is not allowed to sign in.",
        LinkedElsewhere => $"That {provider} account is already connected to another ByteBench user.",
        UnknownProvider => "That sign-in provider isn't configured.",
        Expired => "The link expired. Try again.",
        RegistrationClosed => "New accounts can't be created on this server right now: registration is closed, or limited to certain email domains. Ask a site admin.",
        LastLogin => $"You can't disconnect {provider}: it's the only way to sign in to this account. Connect another provider first.",
        _ => $"Sign-in with {provider} failed. Try again.",
    };
}

/// <summary>
/// The ByteBench side of SSO: after the OIDC/OAuth handler validated the provider's response (state, nonce, PKCE, token
/// signature) into the external cookie, this finds, links or creates the ByteBench account, applies the provider's
/// admin and group claims, and signs in (honouring lockout, deactivation and the two-factor policy).
/// <para>
/// <b>Account matching</b>, in order: an existing link (AspNetUserLogins); else, with <c>LinkByVerifiedEmail</c>, an account
/// with the same email, but only if the provider says the email is verified and the ByteBench account's own email is
/// confirmed (or it has no password), so nobody can pre-register someone's address and wait for their SSO sign-in;
/// else a new account (<c>AutoCreateUsers</c>, verified email required). Anything else is refused with a message telling
/// the user to sign in and link from the profile.
/// </para>
/// </summary>
public class SsoService(SsoSettings settings, UserManager<User> users, SignInManager<User> signIn, IDbContextFactory<AppDbContext> dbFactory,
    ExternalGroupSync groupSync, IDataProtectionProvider dataProtection, Audit.AuditLog audit, ILogger<SsoService> log,
    Accounts.RegistrationPolicy registration)
{
    /// <summary>
    /// RFC 8176 <c>amr</c> values that mean the provider did more than a password (Entra sends <c>mfa</c>; others list the
    /// factors).
    /// </summary>
    private static readonly HashSet<string> MfaMethods = new(["mfa", "otp", "hwk", "swk", "sms", "tel", "fpt", "face", "iris", "retina", "vbm", "sc", "pop"],
        StringComparer.OrdinalIgnoreCase);

    public SsoSettings Settings => settings;

    // ------------------------------------------------------------------ claims

    public static ExternalIdentity Map(SsoProviderOptions p, ClaimsPrincipal principal, string providerKey)
    {
        var m = p.ClaimMappings;
        string? First(string type) => principal.FindFirst(type)?.Value is { Length: > 0 } v ? v.Trim() : null;
        var verifiedClaim = First(m.EmailVerified);
        var verified = p.TrustEmailVerified || string.Equals(verifiedClaim, "true", StringComparison.OrdinalIgnoreCase);
        var groups = principal.FindAll(m.Groups).Select(c => c.Value.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var amr = principal.FindAll("amr").Select(c => c.Value).ToList();
        return new ExternalIdentity(p.Name, providerKey, First(m.Email), verified, First(m.UserName), First(m.DisplayName), groups, amr);
    }

    public static bool IdpDidMfa(ExternalIdentity id) => id.Amr.Any(MfaMethods.Contains);

    // ------------------------------------------------------------------ sign-in

    /// <summary>
    /// Finds, links or creates the account for <paramref name="id"/> and syncs admin and groups. Doesn't sign in.
    /// Returns the user, or an <see cref="SsoErrors"/> code.
    /// </summary>
    public async Task<(User? User, string? Error, bool Created)> ResolveUserAsync(SsoProviderOptions p, ExternalIdentity id)
    {
        if (DomainError(p, id) is { } domainError) return await FailAsync(p, id, domainError);

        var created = false;
        var user = await users.FindByLoginAsync(p.Name, id.ProviderKey);
        if (user == null)
        {
            if (id.Email == null) return await FailAsync(p, id, SsoErrors.NoEmail);
            var byEmail = await users.FindByEmailAsync(id.Email);
            if (byEmail != null)
            {
                // Only take over a matching account when both sides vouch for the address.
                var localVerified = byEmail.EmailConfirmed || !await users.HasPasswordAsync(byEmail);
                if (!p.LinkByVerifiedEmail || !id.EmailVerified || !localVerified || Ai.AiBot.IsBot(byEmail))
                    return await FailAsync(p, id, id.EmailVerified ? SsoErrors.Exists : SsoErrors.Unverified);
                var linked = await users.AddLoginAsync(byEmail, new UserLoginInfo(p.Name, id.ProviderKey, p.Title));
                if (!linked.Succeeded) return await FailAsync(p, id, SsoErrors.Failed, string.Join(" ", linked.Errors.Select(e => e.Description)));
                await audit.RecordAsync("auth.sso_link", "user", byEmail.Id, details: new { provider = p.Name, user = byEmail.UserName, how = "verified_email" }, actorId: byEmail.Id);
                user = byEmail;
            }
            else
            {
                if (!p.AutoCreateUsers) return await FailAsync(p, id, SsoErrors.NoAccount);
                if (!id.EmailVerified) return await FailAsync(p, id, SsoErrors.Unverified);
                // Registration:Mode / AllowedDomains apply to first-time provisioning too (Registration:ApplyToSso, default on).
                if (await registration.CheckProvisioningAsync(id.Email) != null) return await FailAsync(p, id, SsoErrors.RegistrationClosed);
                user = await CreateUserAsync(p, id);
                if (user == null) return await FailAsync(p, id, SsoErrors.Failed, "could not create the account");
                created = true;
            }
        }

        if (user.DeactivatedAt != null || await users.IsLockedOutAsync(user)) return await FailAsync(p, id, SsoErrors.Locked, user: user);
        if (Ai.AiBot.IsBot(user)) return await FailAsync(p, id, SsoErrors.NotAllowed, user: user);

        await SyncAsync(p, id, user);
        return (await users.FindByIdAsync(user.Id.ToString()), null, created);
    }

    /// <summary>Admin and group mappings. Only applied when the provider configures them.</summary>
    public async Task SyncAsync(SsoProviderOptions p, ExternalIdentity id, User user)
    {
        bool? admin = p.AdminGroups.Count > 0 ? id.Groups.Any(g => p.AdminGroups.Contains(g, StringComparer.OrdinalIgnoreCase)) : null;
        var desired = ExternalGroupSync.DesiredGroups(p.GroupMappings, id.Groups);
        if (admin == null && p.GroupMappings.Count == 0) return;
        await groupSync.ApplyAsync(user.Id, p.Source, admin, desired);
    }

    /// <summary>
    /// Finishes a sign-in after the provider's callback: reads the external cookie, resolves the account and signs in.
    /// Returns the URL to redirect to.
    /// </summary>
    public async Task<string> CompleteSignInAsync(HttpContext http, string? returnUrl)
    {
        var info = await signIn.GetExternalLoginInfoAsync();
        var p = settings.Find(info?.LoginProvider);
        if (info == null || p == null)
        {
            await audit.RecordAsync("auth.sso_failed", details: new { provider = info?.LoginProvider, reason = "no_external_login" }, anonymous: true);
            return LoginError(SsoErrors.Failed, info?.LoginProvider);
        }
        await http.SignOutAsync(IdentityConstants.ExternalScheme);
        return await SignInExternalAsync(p, Map(p, info.Principal, info.ProviderKey), returnUrl);
    }

    /// <summary>
    /// Resolves the account for an identity a provider vouched for (OIDC callback, SAML assertion) and signs in through
    /// Identity's external login, so lockout, deactivation and ByteBench two-factor apply. Returns the URL to redirect to.
    /// </summary>
    public async Task<string> SignInExternalAsync(SsoProviderOptions p, ExternalIdentity id, string? returnUrl)
    {
        var (user, error, created) = await ResolveUserAsync(p, id);
        if (user == null) return LoginError(error, p.Name);

        var bypass = p.TwoFactor == SsoTwoFactorPolicy.TrustIdp && IdpDidMfa(id);
        var result = await signIn.ExternalLoginSignInAsync(p.Name, id.ProviderKey, isPersistent: false, bypassTwoFactor: bypass);
        var safe = SafeReturnUrl(returnUrl);
        if (result.Succeeded)
        {
            log.LogInformation("User {User} signed in with {Provider}", user.UserName, p.Name);
            await audit.RecordAsync("auth.sso_sign_in", "user", user.Id, details: new { provider = p.Name, created, idpMfa = IdpDidMfa(id) }, actorId: user.Id);
            return safe;
        }
        if (result.RequiresTwoFactor)
            return $"/account/login-2fa?rememberMe=False&returnUrl={Uri.EscapeDataString(safe)}";
        var reason = result.IsLockedOut ? SsoErrors.Locked : SsoErrors.NotAllowed;
        await audit.RecordAsync("auth.sso_failed", "user", user.Id, details: new { provider = p.Name, reason, user = user.UserName }, anonymous: true);
        return LoginError(reason, p.Name);
    }

    // ------------------------------------------------------------------ linking from the profile

    // Stored identifier: keep "GitBench" (the product was renamed to ByteBench). Changing it would make existing encrypted data/tokens unreadable.
    private const string LinkPurpose = "GitBench.SsoLink.v1";

    /// <summary>A short-lived token for the profile's "Connect" link, so another site can't start a link for the signed-in user.</summary>
    public string CreateLinkToken(int userId) =>
        dataProtection.CreateProtector(LinkPurpose).ToTimeLimitedDataProtector().Protect(userId.ToString(), TimeSpan.FromMinutes(15));

    public bool ValidateLinkToken(string? token, int userId)
    {
        if (string.IsNullOrEmpty(token)) return false;
        try { return dataProtection.CreateProtector(LinkPurpose).ToTimeLimitedDataProtector().Unprotect(token) == userId.ToString(); }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
    }

    /// <summary>Finishes "Connect {provider}" for the signed-in user. Returns the URL to redirect to.</summary>
    public async Task<string> CompleteLinkAsync(HttpContext http, int userId)
    {
        // The expected XSRF value is the user id that started the link (set by ConfigureExternalAuthenticationProperties).
        var info = await signIn.GetExternalLoginInfoAsync(userId.ToString());
        var p = settings.Find(info?.LoginProvider);
        await http.SignOutAsync(IdentityConstants.ExternalScheme);
        if (info == null || p == null) return ProfileResult(SsoErrors.Failed, info?.LoginProvider);

        var user = await users.FindByIdAsync(userId.ToString());
        if (user == null) return "/account/login";
        var id = Map(p, info.Principal, info.ProviderKey);
        if (DomainError(p, id) is { } domainError)
        {
            await audit.RecordAsync("auth.sso_failed", "user", userId, details: new { provider = p.Name, reason = domainError, step = "link" }, actorId: userId);
            return ProfileResult(domainError, p.Name);
        }
        var owner = await users.FindByLoginAsync(p.Name, id.ProviderKey);
        if (owner != null && owner.Id != userId)
        {
            await audit.RecordAsync("auth.sso_failed", "user", userId, details: new { provider = p.Name, reason = SsoErrors.LinkedElsewhere, step = "link" }, actorId: userId);
            return ProfileResult(SsoErrors.LinkedElsewhere, p.Name);
        }
        if (owner == null)
        {
            // One account per provider: replace an older link to a different external account.
            foreach (var old in (await users.GetLoginsAsync(user)).Where(l => l.LoginProvider == p.Name))
                await users.RemoveLoginAsync(user, old.LoginProvider, old.ProviderKey);
            var r = await users.AddLoginAsync(user, new UserLoginInfo(p.Name, id.ProviderKey, p.Title));
            if (!r.Succeeded) return ProfileResult(SsoErrors.Failed, p.Name);
            await audit.RecordAsync("auth.sso_link", "user", userId, details: new { provider = p.Name, user = user.UserName, how = "profile" }, actorId: userId);
        }
        await SyncAsync(p, id, user);
        return $"/account/profile?sso=linked&provider={Uri.EscapeDataString(p.Name)}#connected-accounts";
    }

    /// <summary>Disconnects a provider, unless it's the account's only way to sign in.</summary>
    public async Task<string?> UnlinkAsync(int userId, string provider)
    {
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new InvalidOperationException("Your account no longer exists.");
        var logins = await users.GetLoginsAsync(user);
        // Only SSO providers can be disconnected here (a directory account's LDAP login is managed by LDAP).
        var login = settings.Find(provider) == null ? null : logins.FirstOrDefault(l => l.LoginProvider.Equals(provider, StringComparison.OrdinalIgnoreCase));
        if (login == null) return null;
        // Another login (an SSO provider or the LDAP directory) or a password must remain.
        if (!await users.HasPasswordAsync(user) && logins.Count <= 1) return SsoErrors.LastLogin;
        var r = await users.RemoveLoginAsync(user, login.LoginProvider, login.ProviderKey);
        if (!r.Succeeded) return SsoErrors.Failed;
        await audit.RecordAsync("auth.sso_unlink", "user", userId, details: new { provider = login.LoginProvider, user = user.UserName }, actorId: userId);
        return null;
    }

    public async Task<List<ConnectedAccount>> ConnectedAsync(User user)
    {
        var logins = await users.GetLoginsAsync(user);
        return settings.Providers.Select(p => new ConnectedAccount(p, logins.Any(l => l.LoginProvider == p.Name), null)).ToList();
    }

    // ------------------------------------------------------------------ helpers

    private static string? DomainError(SsoProviderOptions p, ExternalIdentity id)
    {
        if (p.AllowedEmailDomains.Count == 0) return null;
        if (id.Email == null) return SsoErrors.NoEmail;
        // A domain allow-list means nothing for an address nobody verified.
        if (!id.EmailVerified) return SsoErrors.Unverified;
        return p.AllowedEmailDomains.Contains(id.EmailDomain!) ? null : SsoErrors.Domain;
    }

    private async Task<(User?, string?, bool)> FailAsync(SsoProviderOptions p, ExternalIdentity id, string reason, string? detail = null, User? user = null)
    {
        log.LogWarning("SSO sign-in with {Provider} refused ({Reason}) for {Email}", p.Name, reason, id.Email);
        await audit.RecordAsync("auth.sso_failed", user == null ? null : "user", user?.Id,
            details: new { provider = p.Name, reason, email = id.Email, emailVerified = id.EmailVerified, detail }, anonymous: true);
        return (null, reason, false);
    }

    private async Task<User?> CreateUserAsync(SsoProviderOptions p, ExternalIdentity id)
    {
        var user = new User
        {
            UserName = await UniqueUserNameAsync(id.UserName, id.Email),
            Email = id.Email,
            EmailConfirmed = true, // the provider verified it
            DisplayName = Truncate(id.DisplayName ?? id.UserName ?? id.Email!.Split('@')[0], 80),
            Color = Components.Account.AvatarPalette.Random(),
            LockoutEnabled = true,
        };
        var r = await users.CreateAsync(user); // no password: git over HTTP uses access tokens
        if (!r.Succeeded)
        {
            log.LogWarning("Could not create SSO user: {Errors}", string.Join(" ", r.Errors.Select(e => e.Description)));
            return null;
        }
        var l = await users.AddLoginAsync(user, new UserLoginInfo(p.Name, id.ProviderKey, p.Title));
        if (!l.Succeeded)
        {
            await users.DeleteAsync(user);
            return null;
        }
        log.LogInformation("Created account {User} from {Provider}", user.UserName, p.Name);
        await audit.RecordAsync("auth.registered", "user", user.Id, details: new { user.UserName, provider = p.Name }, actorId: user.Id);
        return user;
    }

    private static readonly Regex NotUserNameChar = new("[^A-Za-z0-9._-]", RegexOptions.CultureInvariant);

    /// <summary>A free username from the provider's username (or the email's local part): letters, digits, '.', '_', '-'.</summary>
    public async Task<string> UniqueUserNameAsync(string? preferred, string? email)
    {
        static string Clean(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            if (s.Contains('@')) s = s[..s.IndexOf('@')];
            s = NotUserNameChar.Replace(s, "").Trim('.', '-', '_');
            return s.Length > 36 ? s[..36] : s;
        }
        var stem = Clean(preferred) is { Length: >= 2 } a ? a : Clean(email) is { Length: >= 2 } b ? b : "user";
        await using var db = await dbFactory.CreateDbContextAsync();
        for (var i = 1; ; i++)
        {
            var candidate = i == 1 ? stem : stem + i;
            var norm = candidate.ToUpperInvariant();
            if (!await db.Users.AnyAsync(u => u.NormalizedUserName == norm)) return candidate;
        }
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;

    /// <summary>
    /// A same-site path, else "/". Control characters are refused too: browsers drop tab/CR/LF from URLs, so
    /// <c>/&lt;TAB&gt;/evil.example</c> would become <c>//evil.example</c>.
    /// </summary>
    public static string SafeReturnUrl(string? url) => ByteBench.Components.Account.AccountUrls.SafeReturnUrl(url);

    public static string LoginError(string? code, string? provider) =>
        $"/account/login?sso_error={Uri.EscapeDataString(code ?? SsoErrors.Failed)}" + (provider == null ? "" : "&provider=" + Uri.EscapeDataString(provider));

    private static string ProfileResult(string code, string? provider) =>
        $"/account/profile?sso_error={Uri.EscapeDataString(code)}" + (provider == null ? "" : "&provider=" + Uri.EscapeDataString(provider)) + "#connected-accounts";
}
