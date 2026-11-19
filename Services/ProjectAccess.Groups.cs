using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services;

/// <summary>Things a user can do in a group. Which roles grant which: <see cref="ProjectAccess.MinimumRole(GroupPermission)"/>.</summary>
public enum GroupPermission
{
    ViewGroup,
    /// <summary>Create projects and subgroups in the group, transfer projects into it.</summary>
    CreateInGroup,
    ManageGroupLabelsAndMilestones,
    ManageGroupMembers,
    ManageGroupSettings,
    DeleteGroup,
}

/// <summary>A user's access to one group, for UI flags without more queries.</summary>
public sealed record GroupPermissions(int GroupId, ProjectRole? Role, bool CanView)
{
    public static readonly GroupPermissions None = new(0, null, false);
    public bool Can(GroupPermission p) => p == GroupPermission.ViewGroup ? CanView : Role is { } r && r >= ProjectAccess.MinimumRole(p);
}

/// <summary>
/// Group permissions. <b>Rules.</b> A user's role in a group is <see cref="ProjectRole.Owner"/> for site admins; otherwise
/// the highest <see cref="GroupMember"/> role they hold in the group or any ancestor group; otherwise none. Roles flow
/// down: they also apply to every subgroup and project below (see <see cref="GetRoleAsync"/>).
/// <para>
/// A user can <b>view</b> a group if they have a role in it, or it is <see cref="ProjectVisibility.Internal"/>, or it
/// contains (at any depth) a group they are a member of or a project they can view. Creating projects and subgroups needs
/// Maintainer; managing members and settings and deleting the group need Owner.
/// </para>
/// </summary>
public partial class ProjectAccess
{
    public static ProjectRole MinimumRole(GroupPermission p) => p switch
    {
        GroupPermission.ViewGroup => ProjectRole.Viewer,
        GroupPermission.CreateInGroup or GroupPermission.ManageGroupLabelsAndMilestones => ProjectRole.Maintainer,
        _ => ProjectRole.Owner,
    };

    /// <summary>The group and its ancestors' ids, nearest first (one query; groups nest at most <see cref="Group.MaxDepth"/> deep).</summary>
    public static async Task<List<int>> GroupChainAsync(AppDbContext db, int groupId)
    {
        var row = await db.Groups.Where(g => g.Id == groupId)
            .Select(g => new { g.Id, P1 = g.ParentId, P2 = g.Parent!.ParentId, P3 = g.Parent!.Parent!.ParentId })
            .FirstOrDefaultAsync();
        if (row == null) return [];
        return new int?[] { row.Id, row.P1, row.P2, row.P3 }.Where(x => x != null).Select(x => x!.Value).ToList();
    }

    /// <summary>The user's role in the group (inherited from ancestors too); null if they have none or the group doesn't exist.</summary>
    public async Task<ProjectRole?> GetGroupRoleAsync(int groupId, int? userId = null)
    {
        var system = IsSystem(userId);
        var uid = await ResolveUserIdAsync(userId);
        if (uid == null && !system) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        var chain = await GroupChainAsync(db, groupId);
        if (chain.Count == 0) return null;
        if (system || await db.Users.AnyAsync(u => u.Id == uid && u.IsAdmin)) return ProjectRole.Owner;
        return await db.GroupMembers.Where(m => m.UserId == uid && chain.Contains(m.GroupId)).MaxAsync(m => (ProjectRole?)m.Role);
    }

    public async Task<GroupPermissions> GetGroupPermissionsAsync(int groupId, int? userId = null)
    {
        var role = await GetGroupRoleAsync(groupId, userId);
        var view = role != null || (await VisibleGroupIdsAsync(userId)).Contains(groupId);
        return new GroupPermissions(groupId, role, view);
    }

    public async Task<bool> CanAsync(int groupId, GroupPermission p, int? userId = null) =>
        (await GetGroupPermissionsAsync(groupId, userId)).Can(p);

    /// <summary>Throws <see cref="ForbiddenException"/> unless the user has <paramref name="p"/> in the group.</summary>
    public async Task RequireAsync(int groupId, GroupPermission p, int? userId = null)
    {
        var perms = await GetGroupPermissionsAsync(groupId, userId);
        if (!perms.CanView) throw new ForbiddenException("Group not found, or you don't have access to it.");
        if (!perms.Can(p))
            throw new ForbiddenException($"You don't have permission to {Describe(p)} (requires {MinimumRole(p)}; you are {perms.Role?.ToString() ?? "not a member"}).");
    }

    /// <summary>Ids of every group the user can view (see class remarks). A few small queries, no per-group queries.</summary>
    public async Task<IReadOnlyList<int>> VisibleGroupIdsAsync(int? userId = null)
    {
        var system = IsSystem(userId);
        var uid = await ResolveUserIdAsync(userId);
        if (uid == null && !system) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        if (system || await db.Users.AnyAsync(u => u.Id == uid && u.IsAdmin))
            return await db.Groups.Select(g => g.Id).ToListAsync();
        if (!await db.Users.AnyAsync(u => u.Id == uid)) return [];

        var groups = await db.Groups.Select(g => new { g.Id, g.ParentId, g.Visibility }).ToListAsync();
        var parent = groups.ToDictionary(g => g.Id, g => g.ParentId);
        var memberOf = await db.GroupMembers.Where(m => m.UserId == uid).Select(m => m.GroupId).ToListAsync();
        var visibleProjects = await VisibleProjectIdsAsync(uid);
        var projectGroups = await db.Projects.Where(p => p.GroupId != null && visibleProjects.Contains(p.Id)).Select(p => p.GroupId!.Value).Distinct().ToListAsync();

        var visible = groups.Where(g => g.Visibility == ProjectVisibility.Internal).Select(g => g.Id).ToHashSet();
        // A group and its ancestors are visible when the user is a member of it or can see a project in it.
        foreach (var id in memberOf.Concat(projectGroups))
            for (int? g = id; g != null && parent.ContainsKey(g.Value); g = parent[g.Value]) visible.Add(g.Value);
        // Membership flows down to every subgroup.
        var member = memberOf.ToHashSet();
        foreach (var g in groups)
            for (int? a = g.Id; a != null && parent.ContainsKey(a.Value); a = parent[a.Value])
                if (member.Contains(a.Value)) { visible.Add(g.Id); break; }
        return visible.ToList();
    }

    private static string Describe(GroupPermission p) => p switch
    {
        GroupPermission.ViewGroup => "view this group",
        GroupPermission.CreateInGroup => "create projects or subgroups in this group",
        GroupPermission.ManageGroupLabelsAndMilestones => "manage this group's labels and milestones",
        GroupPermission.ManageGroupMembers => "manage members of this group",
        GroupPermission.ManageGroupSettings => "change this group's settings",
        GroupPermission.DeleteGroup => "delete this group",
        _ => p.ToString(),
    };
}
