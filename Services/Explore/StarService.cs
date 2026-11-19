using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Explore;

/// <summary>
/// Project stars. Anyone who can view a project can star it; stars on projects a user can no longer see are kept but
/// never shown (every read is filtered by <see cref="ProjectAccess.VisibleProjectIdsAsync"/>). Scoped: <see cref="Changed"/>
/// lets the layout (sidebar) refresh when a star button on the page toggles.
/// </summary>
public class StarService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access)
{
    /// <summary>Raised after the acting user stars or unstars a project in this scope (one Blazor circuit).</summary>
    public event Action? Changed;

    /// <summary>Stars the project for the acting user (no-op if already starred). Returns the new star count.</summary>
    public async Task<int> StarAsync(int projectId, int? userId = null)
    {
        var uid = await RequireUserAsync(userId);
        await access.RequireAsync(projectId, Permission.ViewProject, uid);
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await db.ProjectStars.AnyAsync(s => s.ProjectId == projectId && s.UserId == uid))
        {
            db.ProjectStars.Add(new ProjectStar { ProjectId = projectId, UserId = uid });
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { /* starred concurrently: the unique index kept one */ }
        }
        Changed?.Invoke();
        return await db.ProjectStars.CountAsync(s => s.ProjectId == projectId);
    }

    /// <summary>Removes the acting user's star (no-op if not starred). Returns the new star count.</summary>
    public async Task<int> UnstarAsync(int projectId, int? userId = null)
    {
        var uid = await RequireUserAsync(userId);
        await access.RequireAsync(projectId, Permission.ViewProject, uid);
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.ProjectStars.Where(s => s.ProjectId == projectId && s.UserId == uid).ExecuteDeleteAsync();
        Changed?.Invoke();
        return await db.ProjectStars.CountAsync(s => s.ProjectId == projectId);
    }

    public async Task<bool> IsStarredAsync(int projectId, int? userId = null)
    {
        var uid = await access.ResolveUserIdAsync(userId);
        if (uid == null) return false;
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ProjectStars.AnyAsync(s => s.ProjectId == projectId && s.UserId == uid);
    }

    /// <summary>Star count of one project (0 when the acting user can't see it).</summary>
    public async Task<int> CountAsync(int projectId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return 0;
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ProjectStars.CountAsync(s => s.ProjectId == projectId);
    }

    /// <summary>Ids of the projects the user has starred and can still see, most recently starred first.</summary>
    public async Task<List<int>> StarredProjectIdsAsync(int? userId = null)
    {
        var uid = await access.ResolveUserIdAsync(userId);
        if (uid == null) return [];
        var visible = await access.VisibleProjectIdsAsync(uid);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ProjectStars.Where(s => s.UserId == uid && visible.Contains(s.ProjectId))
            .OrderByDescending(s => s.CreatedAt).Select(s => s.ProjectId).ToListAsync();
    }

    private async Task<int> RequireUserAsync(int? userId) =>
        await access.ResolveUserIdAsync(userId) ?? throw new ForbiddenException("Sign in to star projects.");
}
