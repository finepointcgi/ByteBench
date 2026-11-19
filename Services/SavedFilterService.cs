using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services;

/// <summary>A user's saved filters or stars in a project changed (sidebar and Filters dropdown refresh).</summary>
public sealed record SavedFiltersChanged(int ProjectId) : LiveEvent;

/// <summary>A saved filter as one user sees it.</summary>
public sealed record SavedFilterView(int Id, int ProjectId, string Name, string Query, bool IsShared, bool IsStarred,
    int OwnerId, string OwnerName, bool IsMine, bool CanManage, DateTime UpdatedAt);

/// <summary>
/// Saved issue-list filters. Anyone who can view a project can save private filters there; sharing a filter with the
/// project needs Developer+. Owners rename/delete/share their own filters; Maintainers+ can also unshare or delete
/// shared filters. Anyone who can see a filter can star it (starred filters show in the sidebar).
/// </summary>
public class SavedFilterService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, LiveEvents live)
{
    public const int MaxPerUserPerProject = 100;

    /// <summary>The acting user's own filters plus the project's shared ones; starred first, then by name.</summary>
    public async Task<List<SavedFilterView>> ListAsync(int projectId, bool starredOnly = false)
    {
        var uid = await access.ResolveUserIdAsync();
        var perms = await access.GetPermissionsAsync(projectId);
        if (uid == null || !perms.CanView) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.SavedFilters.AsNoTracking()
            .Where(f => f.ProjectId == projectId && (f.OwnerId == uid || f.IsShared))
            .Select(f => new
            {
                f, f.Owner.DisplayName,
                Starred = db.SavedFilterStars.Any(s => s.SavedFilterId == f.Id && s.UserId == uid),
            })
            .ToListAsync();
        return rows
            .Where(r => !starredOnly || r.Starred)
            .Select(r => new SavedFilterView(r.f.Id, r.f.ProjectId, r.f.Name, r.f.Query, r.f.IsShared, r.Starred, r.f.OwnerId, r.DisplayName,
                r.f.OwnerId == uid, r.f.OwnerId == uid || (r.f.IsShared && perms.Role >= ProjectRole.Maintainer), r.f.UpdatedAt))
            .OrderByDescending(v => v.IsStarred).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<SavedFilterView> CreateAsync(int projectId, string name, string query, bool shared)
    {
        await access.RequireAsync(projectId, Permission.ViewProject);
        if (shared) await access.RequireRoleAsync(projectId, ProjectRole.Developer, "share filters");
        var uid = (await access.ResolveUserIdAsync())!.Value;
        name = ValidName(name);
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.SavedFilters.CountAsync(f => f.ProjectId == projectId && f.OwnerId == uid) >= MaxPerUserPerProject)
            throw new InvalidOperationException($"You can save at most {MaxPerUserPerProject} filters per project.");
        var f = new SavedFilter { ProjectId = projectId, OwnerId = uid, Name = name, Query = ValidQuery(query), IsShared = shared };
        db.SavedFilters.Add(f);
        await db.SaveChangesAsync();
        live.Publish(new SavedFiltersChanged(projectId));
        return (await ListAsync(projectId)).First(v => v.Id == f.Id);
    }

    public Task RenameAsync(int id, string name)
    {
        name = ValidName(name);
        return ChangeAsync(id, f => f.Name = name);
    }

    /// <summary>Replaces the stored query (owner only).</summary>
    public Task UpdateQueryAsync(int id, string query)
    {
        query = ValidQuery(query);
        return ChangeAsync(id, f => f.Query = query, ownerOnly: true);
    }

    public async Task SetSharedAsync(int id, bool shared)
    {
        if (shared)
        {
            var projectId = await access.ProjectOf(d => d.SavedFilters.Where(f => f.Id == id).Select(f => (int?)f.ProjectId));
            await access.RequireRoleAsync(projectId, ProjectRole.Developer, "share filters");
        }
        await ChangeAsync(id, f => f.IsShared = shared);
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var f = await RequireManageableAsync(db, id);
        db.SavedFilters.Remove(f);
        await db.SaveChangesAsync();
        live.Publish(new SavedFiltersChanged(f.ProjectId));
    }

    /// <summary>Stars or unstars a filter the acting user can see.</summary>
    public async Task SetStarAsync(int id, bool starred)
    {
        var uid = await access.ResolveUserIdAsync() ?? throw new ForbiddenException("Sign in to star filters.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var f = await db.SavedFilters.FirstOrDefaultAsync(x => x.Id == id && (x.OwnerId == uid || x.IsShared))
                ?? throw new InvalidOperationException("Filter not found.");
        await access.RequireAsync(f.ProjectId, Permission.ViewProject);
        var existing = await db.SavedFilterStars.FirstOrDefaultAsync(s => s.SavedFilterId == id && s.UserId == uid);
        if (starred && existing == null) db.SavedFilterStars.Add(new SavedFilterStar { SavedFilterId = id, UserId = uid });
        else if (!starred && existing != null) db.SavedFilterStars.Remove(existing);
        else return;
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { /* raced with a concurrent star: fine */ }
        live.Publish(new SavedFiltersChanged(f.ProjectId));
    }

    private async Task ChangeAsync(int id, Action<SavedFilter> change, bool ownerOnly = false)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var f = await RequireManageableAsync(db, id, ownerOnly);
        change(f);
        f.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        live.Publish(new SavedFiltersChanged(f.ProjectId));
    }

    /// <summary>Owner (who can still view the project), or Maintainer+ for shared filters.</summary>
    private async Task<SavedFilter> RequireManageableAsync(AppDbContext db, int id, bool ownerOnly = false)
    {
        var uid = await access.ResolveUserIdAsync();
        var f = await db.SavedFilters.FirstOrDefaultAsync(x => x.Id == id && (x.OwnerId == uid || x.IsShared))
                ?? throw new InvalidOperationException("Filter not found.");
        if (f.OwnerId == uid) await access.RequireAsync(f.ProjectId, Permission.ViewProject);
        else if (ownerOnly) throw new ForbiddenException("Only the filter's owner can change its conditions.");
        else await access.RequireRoleAsync(f.ProjectId, ProjectRole.Maintainer, "manage other people's shared filters");
        return f;
    }

    private static string ValidName(string? name)
    {
        name = name?.Trim() ?? "";
        if (name.Length is 0 or > 100) throw new InvalidOperationException("Give the filter a name (at most 100 characters).");
        return name;
    }

    private static string ValidQuery(string? query)
    {
        query = (query ?? "").TrimStart('?');
        if (query.Length > 2000) throw new InvalidOperationException("That filter is too long to save.");
        return query;
    }
}
