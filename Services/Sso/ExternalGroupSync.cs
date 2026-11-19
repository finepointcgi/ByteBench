using ByteBench.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Sso;

/// <summary>
/// Applies an identity provider's (or LDAP's) view of a user to ByteBench: site admin from the admin groups, and group
/// memberships from the group mappings. The external source only manages what it created:
/// <list type="bullet">
/// <item>Admin: granted when the user is in an admin group; revoked only if this source granted it
/// (<see cref="User.AdminGrantedBy"/>), and never for the last site admin.</item>
/// <item>Groups: memberships it added are marked with an <see cref="ExternalGroupMembership"/> row; their role follows
/// the mapping and they're removed when the user leaves the external group. A membership added by hand is left alone.</item>
/// </list>
/// Runs as the system (no permission checks): it's called after the provider authenticated the user.
/// </summary>
public class ExternalGroupSync(IDbContextFactory<AppDbContext> dbFactory, Audit.AuditLog audit, ILogger<ExternalGroupSync> log)
{
    /// <summary>The highest mapped role per ByteBench group path for a user in <paramref name="externalGroups"/>.</summary>
    public static Dictionary<string, ProjectRole> DesiredGroups(IEnumerable<ExternalGroupMapping> mappings, IEnumerable<string> externalGroups)
    {
        var mine = new HashSet<string>(externalGroups, StringComparer.OrdinalIgnoreCase);
        var desired = new Dictionary<string, ProjectRole>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mappings.Where(m => m.Group.Length > 0 && mine.Contains(m.ExternalGroup)))
        {
            var path = m.Group.Trim().Trim('/').ToLowerInvariant();
            if (!desired.TryGetValue(path, out var role) || m.Role > role) desired[path] = m.Role;
        }
        return desired;
    }

    /// <summary>Returns true when anything changed.</summary>
    public async Task<bool> ApplyAsync(int userId, string source, bool? isAdmin, IReadOnlyDictionary<string, ProjectRole> desired,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        var changes = new List<string>();

        if (isAdmin == true && !user.IsAdmin)
        {
            user.IsAdmin = true;
            user.AdminGrantedBy = source;
            changes.Add("admin_granted");
        }
        else if (isAdmin == false && user.IsAdmin && user.AdminGrantedBy == source)
        {
            if (await db.Users.AnyAsync(u => u.IsAdmin && u.Id != userId, ct))
            {
                user.IsAdmin = false;
                user.AdminGrantedBy = null;
                changes.Add("admin_revoked");
            }
            else log.LogWarning("{Source}: {User} left the admin group but is the last site admin; keeping admin", source, user.UserName);
        }

        var paths = desired.Keys.ToList();
        var groups = await db.Groups.Where(g => paths.Contains(g.FullPath)).ToDictionaryAsync(g => g.FullPath, g => g.Id, StringComparer.OrdinalIgnoreCase, ct);
        foreach (var missing in paths.Where(p => !groups.ContainsKey(p)))
            log.LogWarning("{Source}: group mapping points at '{Group}', which doesn't exist", source, missing);

        var managed = await db.ExternalGroupMemberships.Where(m => m.UserId == userId && m.Source == source).ToListAsync(ct);
        var members = await db.GroupMembers.Where(m => m.UserId == userId).ToDictionaryAsync(m => m.GroupId, ct);

        foreach (var (path, role) in desired)
        {
            if (!groups.TryGetValue(path, out var groupId)) continue;
            var isManaged = managed.Any(m => m.GroupId == groupId);
            if (!members.TryGetValue(groupId, out var member))
            {
                db.GroupMembers.Add(new GroupMember { GroupId = groupId, UserId = userId, Role = role });
                if (!isManaged) db.ExternalGroupMemberships.Add(new ExternalGroupMembership { UserId = userId, GroupId = groupId, Source = source });
                changes.Add($"added:{path}:{role}");
            }
            else if (isManaged && member.Role != role)
            {
                changes.Add($"role:{path}:{member.Role}->{role}");
                member.Role = role;
            }
            // else: a membership added by hand: not ours to change.
        }

        var wanted = groups.Where(g => desired.ContainsKey(g.Key)).Select(g => g.Value).ToHashSet();
        foreach (var m in managed.Where(m => !wanted.Contains(m.GroupId)))
        {
            if (members.TryGetValue(m.GroupId, out var member))
            {
                if (member.Role == ProjectRole.Owner && !await db.GroupMembers.AnyAsync(x => x.GroupId == m.GroupId && x.UserId != userId && x.Role == ProjectRole.Owner, ct)
                    && await db.Groups.AnyAsync(g => g.Id == m.GroupId && g.ParentId == null, ct))
                {
                    log.LogWarning("{Source}: not removing {User} from group {Group}: they're its last Owner", source, user.UserName, m.GroupId);
                    continue;
                }
                db.GroupMembers.Remove(member);
            }
            db.ExternalGroupMemberships.Remove(m);
            changes.Add($"removed:{m.GroupId}");
        }

        if (changes.Count == 0) return false;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("auth.external_sync", "user", userId, details: new { source, user = user.UserName, changes }, actorId: userId);
        return true;
    }

    /// <summary>
    /// Deactivates a user the directory no longer has, like <see cref="AdminUserService.DeactivateAsync"/>: locked
    /// indefinitely, signed out everywhere, access tokens revoked.
    /// </summary>
    public async Task DeactivateAsync(UserManager<User> users, User user, string source, CancellationToken ct = default)
    {
        if (user.DeactivatedAt != null) return;
        user.DeactivatedAt = DateTime.UtcNow;
        await users.SetLockoutEnabledAsync(user, true);
        await users.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
        await users.UpdateSecurityStampAsync(user);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var revoked = await db.PersonalAccessTokens.Where(t => t.UserId == user.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow), ct);
        await audit.RecordAsync("auth.directory_user_deactivated", "user", user.Id, details: new { source, user = user.UserName, tokensRevoked = revoked }, anonymous: true);
    }
}
