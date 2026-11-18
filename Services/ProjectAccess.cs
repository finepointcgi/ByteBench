using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services;

/// <summary>Things a user can do in a project. Which roles grant which permission: <see cref="ProjectAccess.MinimumRole"/>.</summary>
public enum Permission
{
    ViewProject,
    EditIssues,
    Comment,
    ManageLabelsAndMilestones,
    PushCode,
    CreatePullRequest,
    ReviewPullRequest,
    MergePullRequest,
    DeleteBranch,
    ManageRepositories,
    ManageMembers,
    ManageProjectSettings,
    DeleteProject,
    /// <summary>Push packages and container images to the project's registry (Developer+).</summary>
    PublishPackages,
    /// <summary>Delete packages, versions and image tags from the project's registry (Maintainer+).</summary>
    DeletePackages,
}

/// <summary>
/// Thrown when the acting user lacks a permission. Derives from <see cref="InvalidOperationException"/> so pages that
/// already catch that and show <c>ex.Message</c> display a friendly message without further changes.
/// </summary>
public class ForbiddenException(string message) : InvalidOperationException(message);

/// <summary>A user's effective role in one project, for checking several permissions without more queries (UI flags).</summary>
public sealed record ProjectPermissions(int ProjectId, ProjectRole? Role)
{
    public static readonly ProjectPermissions None = new(0, null);
    public bool CanView => Role != null;
    public bool Can(Permission p) => Role is { } r && ProjectAccess.RoleHas(r, p);
}

/// <summary>
/// The one place that decides who may do what in a project. Scoped; safe to use inside and outside a Blazor circuit.
/// <para>
/// <b>Rules.</b> A user's effective role in a project is: <see cref="ProjectRole.Owner"/> for site admins
/// (<see cref="User.IsAdmin"/>); otherwise the highest of their <see cref="ProjectMember"/> role, the roles inherited from
/// <see cref="GroupMember"/>ships in the project's group and its ancestor groups, and <see cref="ProjectRole.Viewer"/> if the
/// project is <see cref="ProjectVisibility.Internal"/>; with none of these the project is invisible to them. Group
/// permissions (view, create in, manage, delete a group) are in <c>ProjectAccess.Groups.cs</c>. Each role has all the permissions of the roles below it:
/// Viewer: ViewProject. Developer: + EditIssues, Comment, PushCode, CreatePullRequest, ReviewPullRequest, PublishPackages.
/// Maintainer: + ManageLabelsAndMilestones, MergePullRequest, DeleteBranch, ManageRepositories, DeletePackages.
/// Owner: + ManageMembers, ManageProjectSettings, DeleteProject.
/// </para>
/// <para>
/// <b>Who is checked.</b> Every method takes an optional <c>userId</c>. When given, that user is checked. When null,
/// the "acting user" is used: whoever <see cref="ActAs"/>/<see cref="ActAsSystem"/> set for this scope, else the
/// signed-in <see cref="CurrentUser"/>. Outside a circuit/page (Git HTTP, background jobs) there is no signed-in user,
/// so either pass <c>userId</c> or call <see cref="ActAs"/> (or <see cref="ActAsSystem"/> for trusted system work)
/// before calling services; otherwise reads come back empty and writes throw <see cref="ForbiddenException"/>.
/// </para>
/// </summary>
public partial class ProjectAccess(IDbContextFactory<AppDbContext> dbFactory, CurrentUser me)
{
    private int? actAs;
    private bool system;

    /// <summary>Makes <paramref name="userId"/> the acting user for this scope (e.g. in a background job or HTTP endpoint).</summary>
    public void ActAs(int userId) { actAs = userId; system = false; }

    private int? systemActor;

    /// <summary>
    /// Grants every permission to calls in this scope that don't name a user. For trusted background work only.
    /// <paramref name="onBehalfOf"/> lets the system also vouch for one service account named explicitly as the actor
    /// (e.g. the GitBench AI bot posting review comments), without making that account a project member.
    /// </summary>
    public void ActAsSystem(int? onBehalfOf = null) { system = true; actAs = null; systemActor = onBehalfOf; }

    /// <summary>The lowest role that has <paramref name="p"/>.</summary>
    public static ProjectRole MinimumRole(Permission p) => p switch
    {
        Permission.ViewProject => ProjectRole.Viewer,
        Permission.EditIssues or Permission.Comment or Permission.PushCode
            or Permission.CreatePullRequest or Permission.ReviewPullRequest or Permission.PublishPackages => ProjectRole.Developer,
        Permission.ManageLabelsAndMilestones or Permission.MergePullRequest
            or Permission.DeleteBranch or Permission.ManageRepositories or Permission.DeletePackages => ProjectRole.Maintainer,
        _ => ProjectRole.Owner, // ManageMembers, ManageProjectSettings, DeleteProject
    };

    public static bool RoleHas(ProjectRole role, Permission p) => role >= MinimumRole(p);

    /// <summary>The acting user's id (see class remarks), or null if there is none / it's the system.</summary>
    public async Task<int?> ResolveUserIdAsync(int? userId = null)
    {
        if (userId != null) return userId;
        if (system) return null;
        if (actAs != null) return actAs;
        try
        {
            await me.EnsureLoadedAsync();
            return me.User?.Id;
        }
        catch (InvalidOperationException)
        {
            // No authentication state in this scope (minimal API endpoint, background job).
            return null;
        }
    }

    private bool IsSystem(int? userId) => system && (userId == null || userId == systemActor);

    public async Task<bool> IsAdminAsync(int? userId = null)
    {
        if (IsSystem(userId)) return true;
        var uid = await ResolveUserIdAsync(userId);
        if (uid == null) return false;
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Users.AnyAsync(u => u.Id == uid && u.IsAdmin);
    }

    /// <summary>The user's effective role in the project (see class remarks); null if they can't see it or it doesn't exist.</summary>
    public async Task<ProjectRole?> GetRoleAsync(int projectId, int? userId = null)
    {
        var system = IsSystem(userId);
        var uid = await ResolveUserIdAsync(userId);
        if (uid == null && !system) return null;

        await using var db = await dbFactory.CreateDbContextAsync();
        var info = await db.Projects.Where(p => p.Id == projectId)
            .Select(p => new
            {
                p.Visibility,
                Role = p.Members.Where(m => m.UserId == uid).Select(m => (ProjectRole?)m.Role).FirstOrDefault(),
                // Inherited: memberships in the project's group or any ancestor (groups nest at most Group.MaxDepth = 4 deep).
                GroupRole = p.GroupId == null ? null : db.GroupMembers
                    .Where(gm => gm.UserId == uid && (gm.GroupId == p.GroupId || gm.GroupId == p.Group!.ParentId
                        || gm.GroupId == p.Group!.Parent!.ParentId || gm.GroupId == p.Group!.Parent!.Parent!.ParentId))
                    .Max(gm => (ProjectRole?)gm.Role),
            })
            .FirstOrDefaultAsync();
        if (info == null) return null;
        if (system || await db.Users.AnyAsync(u => u.Id == uid && u.IsAdmin)) return ProjectRole.Owner;
        if (Max(info.Role, info.GroupRole) is { } member) return member; // Viewer is the lowest role, so Internal never raises it
        if (info.Visibility == ProjectVisibility.Internal && await db.Users.AnyAsync(u => u.Id == uid)) return ProjectRole.Viewer;
        return null;
    }

    public async Task<ProjectPermissions> GetPermissionsAsync(int projectId, int? userId = null) =>
        new(projectId, await GetRoleAsync(projectId, userId));

    public async Task<bool> CanAsync(int projectId, Permission p, int? userId = null) =>
        await GetRoleAsync(projectId, userId) is { } role && RoleHas(role, p);

    /// <summary>Throws <see cref="ForbiddenException"/> unless the user has <paramref name="p"/> in the project.</summary>
    public async Task RequireAsync(int projectId, Permission p, int? userId = null)
    {
        var role = await GetRoleAsync(projectId, userId);
        if (role == null) throw new ForbiddenException("Project not found, or you don't have access to it.");
        if (!RoleHas(role.Value, p))
            throw new ForbiddenException($"You don't have permission to {Describe(p)} (requires {MinimumRole(p)}; you are {role}).");
    }

    /// <summary>Throws <see cref="ForbiddenException"/> unless the user's role is at least <paramref name="minimum"/>.</summary>
    public async Task RequireRoleAsync(int projectId, ProjectRole minimum, string action, int? userId = null)
    {
        var role = await GetRoleAsync(projectId, userId);
        if (role == null) throw new ForbiddenException("Project not found, or you don't have access to it.");
        if (role < minimum) throw new ForbiddenException($"You don't have permission to {action} in this project (requires {minimum}; you are {role}).");
    }

    /// <summary>Ids of every project the user can view.</summary>
    public async Task<IReadOnlyList<int>> VisibleProjectIdsAsync(int? userId = null)
    {
        var system = IsSystem(userId);
        var uid = await ResolveUserIdAsync(userId);
        if (uid == null && !system) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        if (system || await db.Users.AnyAsync(u => u.Id == uid && u.IsAdmin))
            return await db.Projects.Select(p => p.Id).ToListAsync();
        if (!await db.Users.AnyAsync(u => u.Id == uid)) return [];
        return await db.Projects
            .Where(p => p.Visibility == ProjectVisibility.Internal || p.Members.Any(m => m.UserId == uid)
                || (p.GroupId != null && db.GroupMembers.Any(gm => gm.UserId == uid && (gm.GroupId == p.GroupId || gm.GroupId == p.Group!.ParentId
                    || gm.GroupId == p.Group!.Parent!.ParentId || gm.GroupId == p.Group!.Parent!.Parent!.ParentId))))
            .Select(p => p.Id).ToListAsync();
    }

    private static ProjectRole? Max(ProjectRole? a, ProjectRole? b) => a == null ? b : b == null ? a : a > b ? a : b;

    // ----- Convenience: check by entity id -----

    public async Task RequireForIssueAsync(int issueId, Permission p, int? userId = null) =>
        await RequireAsync(await ProjectOf(db => db.Issues.Where(i => i.Id == issueId).Select(i => (int?)i.ProjectId)), p, userId);

    public async Task RequireForRepositoryAsync(int repositoryId, Permission p, int? userId = null) =>
        await RequireAsync(await ProjectOf(db => db.Repositories.Where(r => r.Id == repositoryId).Select(r => (int?)r.ProjectId)), p, userId);

    public async Task RequireForPullRequestAsync(int pullRequestId, Permission p, int? userId = null) =>
        await RequireAsync(await ProjectOf(db => db.PullRequests.Where(x => x.Id == pullRequestId).Select(x => (int?)x.Repository.ProjectId)), p, userId);

    /// <summary>The project id from <paramref name="query"/>, or 0 (which no project has, so checks fail) if not found.</summary>
    internal async Task<int> ProjectOf(Func<AppDbContext, IQueryable<int?>> query)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await query(db).FirstOrDefaultAsync() ?? 0;
    }

    private static string Describe(Permission p) => p switch
    {
        Permission.ViewProject => "view this project",
        Permission.EditIssues => "create or edit issues in this project",
        Permission.Comment => "comment in this project",
        Permission.ManageLabelsAndMilestones => "manage labels and milestones in this project",
        Permission.PushCode => "push code or create branches in this project",
        Permission.CreatePullRequest => "create or update pull requests in this project",
        Permission.ReviewPullRequest => "review pull requests in this project",
        Permission.MergePullRequest => "merge pull requests in this project",
        Permission.DeleteBranch => "delete branches in this project",
        Permission.ManageRepositories => "manage repositories in this project",
        Permission.ManageMembers => "manage members of this project",
        Permission.ManageProjectSettings => "change this project's settings",
        Permission.DeleteProject => "delete this project",
        Permission.PublishPackages => "publish packages in this project",
        Permission.DeletePackages => "delete packages in this project",
        _ => p.ToString(),
    };
}
