using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services;

/// <summary>Project membership: list, add, change role, remove. Changes require <see cref="Permission.ManageMembers"/>.</summary>
public class MemberService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, Audit.AuditLog audit)
{
    public async Task<List<ProjectMember>> GetMembersAsync(int projectId, int? userId = null)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject, userId)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ProjectMembers.Include(m => m.User).Where(m => m.ProjectId == projectId)
            .OrderByDescending(m => m.Role).ThenBy(m => m.User.DisplayName).ToListAsync();
    }

    /// <summary>Adds the user with this username or e-mail address.</summary>
    public async Task<ProjectMember> AddMemberAsync(int projectId, string userNameOrEmail, ProjectRole role, int? actorId = null)
    {
        await access.RequireAsync(projectId, Permission.ManageMembers, actorId);
        var login = userNameOrEmail.Trim().ToUpperInvariant();
        if (login.Length == 0) throw new InvalidOperationException("Enter a username or e-mail address.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == login || u.NormalizedEmail == login)
                   ?? throw new InvalidOperationException($"No user '{userNameOrEmail.Trim()}' was found.");
        if (await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == user.Id))
            throw new InvalidOperationException($"{user.DisplayName} is already a member.");

        var member = new ProjectMember { ProjectId = projectId, UserId = user.Id, Role = role };
        db.ProjectMembers.Add(member);
        await db.SaveChangesAsync();
        await audit.RecordAsync("member.added", "user", user.Id, projectId, new { user = user.UserName, role = role.ToString() }, actorId);
        member.User = user;
        return member;
    }

    public async Task ChangeRoleAsync(int projectId, int memberUserId, ProjectRole role, int? actorId = null)
    {
        await access.RequireAsync(projectId, Permission.ManageMembers, actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var m = await db.ProjectMembers.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.UserId == memberUserId)
                ?? throw new InvalidOperationException("That user is not a member of this project.");
        if (m.Role == role) return;
        if (m.Role == ProjectRole.Owner && await IsLastOwnerAsync(db, projectId))
            throw new InvalidOperationException("The last Owner can't be demoted. Make someone else an Owner first.");
        var oldRole = m.Role;
        m.Role = role;
        await db.SaveChangesAsync();
        await audit.RecordAsync("member.role_changed", "user", memberUserId, projectId, new { from = oldRole.ToString(), to = role.ToString() }, actorId);
    }

    public async Task RemoveMemberAsync(int projectId, int memberUserId, int? actorId = null)
    {
        await access.RequireAsync(projectId, Permission.ManageMembers, actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var m = await db.ProjectMembers.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.UserId == memberUserId)
                ?? throw new InvalidOperationException("That user is not a member of this project.");
        if (m.Role == ProjectRole.Owner && await IsLastOwnerAsync(db, projectId))
            throw new InvalidOperationException("The last Owner can't be removed. Make someone else an Owner first.");
        db.ProjectMembers.Remove(m);
        await db.SaveChangesAsync();
        await audit.RecordAsync("member.removed", "user", memberUserId, projectId, new { role = m.Role.ToString() }, actorId);
    }

    private static async Task<bool> IsLastOwnerAsync(AppDbContext db, int projectId) =>
        await db.ProjectMembers.CountAsync(x => x.ProjectId == projectId && x.Role == ProjectRole.Owner) <= 1;
}
