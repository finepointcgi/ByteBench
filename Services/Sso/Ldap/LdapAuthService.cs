using System.Text.RegularExpressions;
using GitBench.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GitBench.Services.Sso.Ldap;

public enum LdapLoginStatus { Success, InvalidCredentials, Conflict, NoAccount, Locked, Unavailable }

/// <summary>The outcome; on success, <see cref="Key"/> is the provider key for <c>ExternalLoginSignInAsync("ldap", Key, …)</c>.</summary>
public sealed record LdapLoginResult(LdapLoginStatus Status, User? User = null, string? Key = null);

public enum GitPasswordCheck
{
    /// <summary>Not a directory account: check the GitBench password as usual.</summary>
    NotDirectoryUser,
    Valid,
    Invalid,
    /// <summary>A directory account, but LDAP passwords aren't allowed for git (<see cref="LdapOptions.AllowGitPassword"/>).</summary>
    NotAllowed,
}

/// <summary>
/// LDAP / Active Directory sign-in (<c>Authentication:Ldap</c>): search for the user as the service account with the
/// configured filter (input escaped per RFC 4515), bind as the found DN with the given password, then create or update
/// the GitBench account (linked through an AspNetUserLogins row, provider <c>ldap</c>, key = the lower-cased username
/// attribute) and sync admin and group memberships. Also the git Basic-auth check and the periodic directory sync.
/// <para>
/// A directory login never takes over a local account: if a non-directory account already uses the username or the
/// email, the sign-in is refused (<see cref="LdapLoginStatus.Conflict"/>).
/// </para>
/// </summary>
public class LdapAuthService(IOptionsMonitor<LdapOptions> options, ILdapClient client, UserManager<User> users, ExternalGroupSync groupSync,
    IDbContextFactory<AppDbContext> dbFactory, Audit.AuditLog audit, ILogger<LdapAuthService> log)
{
    public const string LoginProvider = "ldap";
    public const string Prefix = "ldap:";

    public LdapOptions Options => options.CurrentValue;
    public bool Enabled => Options.Enabled && Options.Server.Length > 0;

    /// <summary>
    /// Whether the sign-in form should try LDAP for <paramref name="login"/>: when it has the <c>ldap:</c> prefix, when no
    /// local account matches, or when the matching account is a directory account. Returns the login to look up.
    /// </summary>
    public async Task<(bool UseLdap, string Login)> ShouldTryAsync(string login)
    {
        if (!Enabled) return (false, login);
        if (login.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return (true, login[Prefix.Length..].Trim());
        var local = await users.FindByNameAsync(login) ?? (login.Contains('@') ? await users.FindByEmailAsync(login) : null);
        if (local == null) return (true, login);
        var key = (await users.GetLoginsAsync(local)).FirstOrDefault(l => l.LoginProvider == LoginProvider)?.ProviderKey;
        return key != null ? (true, key) : (false, login);
    }

    public async Task<LdapLoginResult> AuthenticateAsync(string login, string password, CancellationToken ct = default)
    {
        // An empty password would be an unauthenticated bind, which many servers "accept".
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(login)) return new(LdapLoginStatus.InvalidCredentials);
        try
        {
            var entry = await FindEntryAsync(login, ct);
            if (entry == null)
            {
                await audit.RecordAsync("auth.sign_in_failed", details: new { login, method = "ldap", reason = "not_in_directory" }, anonymous: true);
                return new(LdapLoginStatus.InvalidCredentials);
            }
            var key = KeyOf(entry);
            var existing = key == null ? null : await users.FindByLoginAsync(LoginProvider, key);
            if (existing != null && (existing.DeactivatedAt != null || await users.IsLockedOutAsync(existing)))
                return new(LdapLoginStatus.Locked, existing); // don't even try the password: lockout must hold against guessing

            if (!await client.CheckPasswordAsync(entry.Dn, password, ct))
            {
                if (existing != null) await users.AccessFailedAsync(existing);
                await audit.RecordAsync("auth.sign_in_failed", existing == null ? null : "user", existing?.Id,
                    details: new { login, method = "ldap", reason = "invalid_credentials" }, anonymous: true);
                return new(LdapLoginStatus.InvalidCredentials);
            }

            var (user, status) = await ProvisionAsync(entry, create: true, ct);
            if (user == null)
            {
                await audit.RecordAsync("auth.sign_in_failed", details: new { login, method = "ldap", reason = status.ToString() }, anonymous: true);
                return new(status);
            }
            if (user.DeactivatedAt != null || await users.IsLockedOutAsync(user)) return new(LdapLoginStatus.Locked, user);
            if (await users.GetAccessFailedCountAsync(user) > 0) await users.ResetAccessFailedCountAsync(user);
            return new(LdapLoginStatus.Success, user, KeyOf(entry));
        }
        catch (LdapUnavailableException ex)
        {
            log.LogError(ex, "LDAP sign-in for {Login} failed: directory unavailable", login);
            return new(LdapLoginStatus.Unavailable);
        }
    }

    /// <summary>Git over HTTP with a password, for directory accounts.</summary>
    public async Task<GitPasswordCheck> CheckGitPasswordAsync(User user, string password, CancellationToken ct = default)
    {
        var key = (await users.GetLoginsAsync(user)).FirstOrDefault(l => l.LoginProvider == LoginProvider)?.ProviderKey;
        if (key == null) return GitPasswordCheck.NotDirectoryUser;
        if (!Enabled || !Options.AllowGitPassword) return GitPasswordCheck.NotAllowed;
        if (string.IsNullOrEmpty(password)) return GitPasswordCheck.Invalid;
        try
        {
            var entry = await FindEntryAsync(key, ct);
            if (entry == null || !string.Equals(KeyOf(entry), key, StringComparison.Ordinal)) return GitPasswordCheck.Invalid;
            return await client.CheckPasswordAsync(entry.Dn, password, ct) ? GitPasswordCheck.Valid : GitPasswordCheck.Invalid;
        }
        catch (LdapUnavailableException ex)
        {
            log.LogError(ex, "LDAP git auth for {User} failed: directory unavailable", user.UserName);
            return GitPasswordCheck.Invalid;
        }
    }

    public async Task<bool> IsDirectoryUserAsync(User user) =>
        (await users.GetLoginsAsync(user)).Any(l => l.LoginProvider == LoginProvider);

    // ------------------------------------------------------------------ directory

    private string[] AttributesToLoad
    {
        get
        {
            var a = Options.Attributes;
            return new[] { a.UserName, a.Email, a.DisplayName, a.MemberOf }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    /// <summary>The single entry matching the user filter for <paramref name="login"/>, or null (none, or ambiguous).</summary>
    public async Task<LdapDirectoryEntry?> FindEntryAsync(string login, CancellationToken ct = default)
    {
        var filter = LdapFilter.Format(Options.UserFilter, login);
        var found = await client.SearchAsync(Options.UserSearchBase, filter, AttributesToLoad, ct);
        if (found.Count > 1) log.LogWarning("LDAP filter {Filter} matched {Count} entries; refusing an ambiguous login", filter, found.Count);
        return found.Count == 1 ? found[0] : null;
    }

    private string? KeyOf(LdapDirectoryEntry e) => e.First(Options.Attributes.UserName)?.Trim().ToLowerInvariant();

    /// <summary>The user's group DNs: the memberOf attribute, else a group search.</summary>
    public async Task<List<string>> GroupsOfAsync(LdapDirectoryEntry entry, CancellationToken ct = default)
    {
        var o = Options;
        var groups = entry.All(o.Attributes.MemberOf).ToList();
        if (groups.Count == 0 && o.GroupSearchBase.Length > 0 && o.GroupFilter.Length > 0)
            groups = (await client.SearchAsync(o.GroupSearchBase, LdapFilter.Format(o.GroupFilter, entry.Dn), ["cn"], ct)).Select(g => g.Dn).ToList();
        return groups.Select(NormalizeDn).Distinct().ToList();
    }

    private static readonly Regex DnSpaces = new(@"\s*,\s*", RegexOptions.CultureInvariant);
    private static readonly Regex DnEquals = new(@"\s*=\s*", RegexOptions.CultureInvariant);

    /// <summary>For comparing DNs: lower case, no spaces around ',' and '='.</summary>
    public static string NormalizeDn(string dn) => DnEquals.Replace(DnSpaces.Replace(dn.Trim(), ","), "=").ToLowerInvariant();

    /// <summary>
    /// Finds (or, with <paramref name="create"/> and AutoCreateUsers, creates) the GitBench account for a directory entry,
    /// updates its name and email, and syncs admin and groups.
    /// </summary>
    public async Task<(User? User, LdapLoginStatus Status)> ProvisionAsync(LdapDirectoryEntry entry, bool create, CancellationToken ct = default)
    {
        var o = Options;
        var key = KeyOf(entry);
        if (key == null) return (null, LdapLoginStatus.InvalidCredentials);
        var email = entry.First(o.Attributes.Email)?.Trim();
        var displayName = entry.First(o.Attributes.DisplayName)?.Trim() ?? key;
        if (displayName.Length > 80) displayName = displayName[..80];

        var user = await users.FindByLoginAsync(LoginProvider, key);
        if (user == null)
        {
            if (!create || !o.AutoCreateUsers) return (null, LdapLoginStatus.NoAccount);
            if (email == null) { log.LogWarning("LDAP entry {Dn} has no email ({Attr}); can't create an account", entry.Dn, o.Attributes.Email); return (null, LdapLoginStatus.NoAccount); }
            // Never take over a local account with the same username or email.
            if (await users.FindByNameAsync(key) != null || await users.FindByEmailAsync(email) != null) return (null, LdapLoginStatus.Conflict);
            user = new User
            {
                UserName = key, Email = email, EmailConfirmed = true, DisplayName = displayName,
                Color = Components.Account.AvatarPalette.Random(), LockoutEnabled = true,
            };
            var r = await users.CreateAsync(user); // no GitBench password: the directory checks it
            if (!r.Succeeded)
            {
                log.LogWarning("Could not create LDAP user {User}: {Errors}", key, string.Join(" ", r.Errors.Select(e => e.Description)));
                return (null, LdapLoginStatus.Conflict);
            }
            await users.AddLoginAsync(user, new UserLoginInfo(LoginProvider, key, o.DisplayName));
            await audit.RecordAsync("auth.registered", "user", user.Id, details: new { user.UserName, provider = LoginProvider }, actorId: user.Id);
        }
        else
        {
            var changed = false;
            if (user.DisplayName != displayName) { user.DisplayName = displayName; changed = true; }
            if (email != null && !string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase)
                && await users.FindByEmailAsync(email) is null)
            {
                user.Email = email;
                user.NormalizedEmail = users.NormalizeEmail(email);
                user.EmailConfirmed = true;
                changed = true;
            }
            if (changed) await users.UpdateAsync(user);
        }

        var groups = await GroupsOfAsync(entry, ct);
        bool? admin = o.AdminGroupDn.Length > 0 ? groups.Contains(NormalizeDn(o.AdminGroupDn)) : null;
        if (admin != null || o.GroupMappings.Count > 0)
        {
            var mappings = o.GroupMappings.Select(m => new ExternalGroupMapping { ExternalGroup = NormalizeDn(m.GroupDn), Group = m.Group, Role = m.Role });
            await groupSync.ApplyAsync(user.Id, LoginProvider, admin, ExternalGroupSync.DesiredGroups(mappings, groups), ct);
        }
        return (await users.FindByIdAsync(user.Id.ToString()), LdapLoginStatus.Success);
    }

    // ------------------------------------------------------------------ sync & admin

    /// <summary>
    /// Re-reads every directory account: updates name, email, admin and groups, and (with Sync:DeactivateMissingUsers)
    /// deactivates accounts the directory no longer returns. Stops without deactivating anything more if the directory
    /// becomes unreachable.
    /// </summary>
    public async Task<DirectorySyncRun> SyncAllAsync(string trigger, CancellationToken ct = default)
    {
        var run = new DirectorySyncRun { Source = LoginProvider, Trigger = trigger };
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var linked = await db.UserLogins.Where(l => l.LoginProvider == LoginProvider).Select(l => new { l.UserId, l.ProviderKey }).ToListAsync(ct);
            try
            {
                if (!Enabled) throw new LdapUnavailableException("LDAP is not enabled.");
                foreach (var l in linked)
                {
                    ct.ThrowIfCancellationRequested();
                    run.UsersChecked++;
                    var user = await users.FindByIdAsync(l.UserId.ToString());
                    if (user == null) continue;
                    var entry = await FindEntryAsync(l.ProviderKey, ct);
                    if (entry == null || KeyOf(entry) != l.ProviderKey)
                    {
                        if (Options.Sync.DeactivateMissingUsers && user.DeactivatedAt == null && !user.IsAdmin)
                        {
                            await groupSync.DeactivateAsync(users, user, LoginProvider, ct);
                            run.UsersDeactivated++;
                        }
                        else if (user.IsAdmin && user.DeactivatedAt == null)
                            log.LogWarning("LDAP sync: site admin {User} is no longer in the directory; not deactivating an admin automatically", user.UserName);
                        continue;
                    }
                    var before = (user.DisplayName, user.Email, user.IsAdmin);
                    var (after, _) = await ProvisionAsync(entry, create: false, ct);
                    if (after != null && (after.DisplayName, after.Email, after.IsAdmin) != before) run.UsersUpdated++;
                }
                run.Succeeded = true;
            }
            catch (LdapUnavailableException ex)
            {
                run.Error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                log.LogError(ex, "LDAP sync failed");
            }
            run.FinishedAt = DateTime.UtcNow;
            db.DirectorySyncRuns.Add(run);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        await audit.RecordAsync("auth.ldap_sync", details: new { run.Trigger, run.Succeeded, run.UsersChecked, run.UsersUpdated, run.UsersDeactivated, run.Error },
            anonymous: trigger == "scheduled");
        return run;
    }

    public async Task<DirectorySyncRun?> LastSyncAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.DirectorySyncRuns.AsNoTracking().Where(r => r.Source == LoginProvider).OrderByDescending(r => r.Id).FirstOrDefaultAsync();
    }

    /// <summary>Connects, binds as the service account and runs the user filter once. Never echoes the bind password.</summary>
    public async Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        var o = Options;
        if (!Enabled) return (false, "LDAP is not enabled (Authentication:Ldap:Enabled and Server).");
        try
        {
            var filter = LdapFilter.Format(o.UserFilter, "gitbench-connection-test");
            await client.SearchAsync(o.UserSearchBase, filter, AttributesToLoad, ct);
            return (true, $"Connected to {o.Server}:{o.EffectivePort} ({o.Security}), bound as {(o.BindDn.Length > 0 ? o.BindDn : "anonymous")} and searched {o.UserSearchBase}.");
        }
        catch (LdapUnavailableException ex)
        {
            return (false, ex.Message);
        }
    }
}
