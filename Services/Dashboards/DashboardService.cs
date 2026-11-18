using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Dashboards;

/// <summary>A dashboard's gadgets or settings changed (open copies of it refresh).</summary>
public sealed record DashboardChanged(int DashboardId) : LiveEvent;

/// <summary>A dashboard as one user sees it in lists.</summary>
public sealed record DashboardSummary(int Id, string Name, string? Description, int OwnerId, string OwnerName, bool IsMine,
    string? SharedWith, string? SharedUrl, bool IsHome, int GadgetCount, DateTime UpdatedAt);

/// <summary>A dashboard with its gadgets (in order) as one user sees it.</summary>
public sealed record DashboardView(Dashboard Dashboard, bool CanEdit, bool IsHome, string? SharedWith, string? SharedUrl)
{
    public IReadOnlyList<DashboardGadget> Gadgets => Dashboard.Gadgets;
}

/// <summary>
/// Personal dashboards. Only the owner changes a dashboard. Sharing it with a project needs Developer+ there; with a
/// group, Developer+ in the group. Everyone who can view the project or group can then open it, read-only. Gadget data
/// is loaded by <see cref="GadgetDataService"/> as the viewer, so a shared dashboard never shows anyone more than they
/// could see anyway. A user may pin one visible dashboard as Home (<see cref="DashboardHome"/>).
/// </summary>
public class DashboardService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, LiveEvents live)
{
    public const int MaxPerUser = 50;
    public const int MaxGadgets = 30;
    public static readonly int[] Widths = [4, 6, 8, 12];

    /// <summary>The gadgets of the built-in Home layout (projects, assigned issues, activity).</summary>
    public static IReadOnlyList<DashboardGadget> DefaultGadgets() =>
    [
        new() { Kind = GadgetKind.Projects, Width = 8, Order = 0 },
        new() { Kind = GadgetKind.ActivityStream, Width = 4, Order = 1, Config = new GadgetConfig { Limit = 15 }.ToJson() },
        new() { Kind = GadgetKind.AssignedToMe, Width = 8, Order = 2, Config = new GadgetConfig { Limit = 25 }.ToJson() },
    ];

    // ---------- Reads ----------

    private async Task<(int Uid, IReadOnlyList<int> Projects, IReadOnlyList<int> Groups)?> ScopeAsync()
    {
        var uid = await access.ResolveUserIdAsync();
        if (uid == null) return null;
        return (uid.Value, await access.VisibleProjectIdsAsync(), await access.VisibleGroupIdsAsync());
    }

    private static IQueryable<Dashboard> Visible(AppDbContext db, int uid, IReadOnlyList<int> projects, IReadOnlyList<int> groups) =>
        db.Dashboards.Where(d => d.OwnerId == uid
                                 || (d.SharedProjectId != null && projects.Contains(d.SharedProjectId.Value))
                                 || (d.SharedGroupId != null && groups.Contains(d.SharedGroupId.Value)));

    /// <summary>Your dashboards and those shared with projects and groups you can view: yours first, then by name.</summary>
    public async Task<List<DashboardSummary>> ListAsync()
    {
        if (await ScopeAsync() is not var (uid, projects, groups)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var home = await db.DashboardHomes.Where(h => h.UserId == uid).Select(h => (int?)h.DashboardId).FirstOrDefaultAsync();
        var rows = await Visible(db, uid, projects, groups)
            .Select(d => new
            {
                d.Id, d.Name, d.Description, d.OwnerId, Owner = d.Owner.DisplayName, d.UpdatedAt, Count = d.Gadgets.Count,
                ProjectKey = db.Projects.Where(p => p.Id == d.SharedProjectId).Select(p => p.Key).FirstOrDefault(),
                GroupPath = db.Groups.Where(g => g.Id == d.SharedGroupId).Select(g => g.FullPath).FirstOrDefault(),
            }).ToListAsync();
        return rows
            .Select(r => new DashboardSummary(r.Id, r.Name, r.Description, r.OwnerId, r.Owner, r.OwnerId == uid,
                r.ProjectKey ?? r.GroupPath, SharedUrl(r.ProjectKey, r.GroupPath), r.Id == home, r.Count, r.UpdatedAt))
            .OrderByDescending(d => d.IsMine).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? SharedUrl(string? projectKey, string? groupPath) =>
        projectKey != null ? $"/p/{projectKey}/issues" : groupPath != null ? $"/g/{groupPath}" : null;

    /// <summary>The dashboard, or null when it doesn't exist or isn't visible to the acting user.</summary>
    public async Task<DashboardView?> GetAsync(int id)
    {
        if (await ScopeAsync() is not var (uid, projects, groups)) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        var d = await Visible(db, uid, projects, groups).AsNoTracking()
            .Include(x => x.Owner).Include(x => x.Gadgets.OrderBy(g => g.Order).ThenBy(g => g.Id))
            .FirstOrDefaultAsync(x => x.Id == id);
        if (d == null) return null;
        var isHome = await db.DashboardHomes.AnyAsync(h => h.UserId == uid && h.DashboardId == id);
        var projectKey = d.SharedProjectId is { } pid ? await db.Projects.Where(p => p.Id == pid).Select(p => p.Key).FirstOrDefaultAsync() : null;
        var groupPath = d.SharedGroupId is { } gid ? await db.Groups.Where(g => g.Id == gid).Select(g => g.FullPath).FirstOrDefaultAsync() : null;
        return new DashboardView(d, d.OwnerId == uid, isHome, projectKey ?? groupPath, SharedUrl(projectKey, groupPath));
    }

    /// <summary>The acting user's pinned Home dashboard, if it is still visible to them.</summary>
    public async Task<DashboardView?> GetHomeAsync()
    {
        var uid = await access.ResolveUserIdAsync();
        if (uid == null) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        var id = await db.DashboardHomes.Where(h => h.UserId == uid).Select(h => (int?)h.DashboardId).FirstOrDefaultAsync();
        return id is { } x ? await GetAsync(x) : null;
    }

    // ---------- Writes (owner only) ----------

    public async Task<Dashboard> CreateAsync(string name, string? description = null, bool withDefaultGadgets = false)
    {
        var uid = await access.ResolveUserIdAsync() ?? throw new ForbiddenException("Sign in to create dashboards.");
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.Dashboards.CountAsync(d => d.OwnerId == uid) >= MaxPerUser)
            throw new InvalidOperationException($"You can have at most {MaxPerUser} dashboards.");
        var d = new Dashboard { OwnerId = uid, Name = ValidName(name), Description = ValidDescription(description) };
        if (withDefaultGadgets) d.Gadgets.AddRange(DefaultGadgets());
        db.Dashboards.Add(d);
        await db.SaveChangesAsync();
        return d;
    }

    public Task UpdateAsync(int id, string name, string? description)
    {
        name = ValidName(name);
        description = ValidDescription(description);
        return ChangeAsync(id, (d, _) => { d.Name = name; d.Description = description; return Task.CompletedTask; });
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var d = await RequireOwnedAsync(db, id);
        db.Dashboards.Remove(d);
        await db.SaveChangesAsync();
        live.Publish(new DashboardChanged(id));
    }

    /// <summary>Shares the dashboard with one project or one group (read-only), or stops sharing it (both null).</summary>
    public async Task ShareAsync(int id, int? projectId, int? groupId)
    {
        if (projectId != null && groupId != null) throw new InvalidOperationException("Share a dashboard with a project or a group, not both.");
        if (projectId is { } p) await access.RequireRoleAsync(p, ProjectRole.Developer, "share dashboards with this project");
        if (groupId is { } g && await access.GetGroupRoleAsync(g) is not >= ProjectRole.Developer)
            throw new ForbiddenException("You need at least the Developer role in the group to share dashboards with it.");
        await ChangeAsync(id, (d, _) => { d.SharedProjectId = projectId; d.SharedGroupId = groupId; return Task.CompletedTask; });
    }

    public async Task<DashboardGadget> AddGadgetAsync(int id, GadgetKind kind, string? title = null, int width = 6, GadgetConfig? config = null)
    {
        DashboardGadget? added = null;
        await ChangeAsync(id, async (d, db) =>
        {
            var count = await db.DashboardGadgets.CountAsync(g => g.DashboardId == id);
            if (count >= MaxGadgets) throw new InvalidOperationException($"A dashboard can hold at most {MaxGadgets} gadgets.");
            var order = await db.DashboardGadgets.Where(g => g.DashboardId == id).MaxAsync(g => (int?)g.Order) ?? -1;
            added = new DashboardGadget
            {
                DashboardId = id, Kind = kind, Title = ValidTitle(title), Width = ValidWidth(width), Order = order + 1,
                Config = (config ?? new GadgetConfig()).Validated().ToJson(),
            };
            db.DashboardGadgets.Add(added);
        });
        return added!;
    }

    public async Task UpdateGadgetAsync(int gadgetId, string? title, int width, GadgetConfig config)
    {
        var json = config.Validated().ToJson();
        title = ValidTitle(title);
        width = ValidWidth(width);
        await ChangeGadgetAsync(gadgetId, (g, _) => { g.Title = title; g.Width = width; g.Config = json; return Task.CompletedTask; });
    }

    public Task RemoveGadgetAsync(int gadgetId) =>
        ChangeGadgetAsync(gadgetId, (g, db) => { db.DashboardGadgets.Remove(g); return Task.CompletedTask; });

    /// <summary>Moves a gadget to position <paramref name="index"/> (clamped) and renumbers the others.</summary>
    public Task MoveGadgetAsync(int gadgetId, int index) =>
        ChangeGadgetAsync(gadgetId, async (g, db) =>
        {
            var all = await db.DashboardGadgets.Where(x => x.DashboardId == g.DashboardId).OrderBy(x => x.Order).ThenBy(x => x.Id).ToListAsync();
            all.Remove(g); // same tracked instance: one context
            all.Insert(Math.Clamp(index, 0, all.Count), g);
            for (var k = 0; k < all.Count; k++) all[k].Order = k;
        });

    /// <summary>Pins a visible dashboard as the acting user's Home page, or unpins (null) to get the built-in layout back.</summary>
    public async Task SetHomeAsync(int? dashboardId)
    {
        var uid = await access.ResolveUserIdAsync() ?? throw new ForbiddenException("Sign in first.");
        if (dashboardId is { } id && await GetAsync(id) == null) throw new InvalidOperationException("Dashboard not found.");
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.DashboardHomes.Where(h => h.UserId == uid).ExecuteDeleteAsync();
        if (dashboardId is { } d) db.DashboardHomes.Add(new DashboardHome { UserId = uid, DashboardId = d });
        await db.SaveChangesAsync();
    }

    // ---------- Helpers ----------

    private async Task ChangeAsync(int id, Func<Dashboard, AppDbContext, Task> change)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var d = await RequireOwnedAsync(db, id);
        await change(d, db);
        d.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        live.Publish(new DashboardChanged(id));
    }

    private async Task ChangeGadgetAsync(int gadgetId, Func<DashboardGadget, AppDbContext, Task> change)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var g = await db.DashboardGadgets.FirstOrDefaultAsync(x => x.Id == gadgetId) ?? throw new InvalidOperationException("Gadget not found.");
        var d = await RequireOwnedAsync(db, g.DashboardId);
        await change(g, db);
        d.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        live.Publish(new DashboardChanged(d.Id));
    }

    /// <summary>The dashboard if the acting user owns it. Others who can see it get Forbidden; anyone else "not found".</summary>
    private async Task<Dashboard> RequireOwnedAsync(AppDbContext db, int id)
    {
        var uid = await access.ResolveUserIdAsync();
        var d = await db.Dashboards.FirstOrDefaultAsync(x => x.Id == id);
        if (d != null && d.OwnerId == uid) return d;
        if (d != null && uid != null && await GetAsync(id) != null) throw new ForbiddenException("Only the dashboard's owner can change it.");
        throw new InvalidOperationException("Dashboard not found.");
    }

    private static string ValidName(string? name)
    {
        name = name?.Trim() ?? "";
        if (name.Length is 0 or > 100) throw new InvalidOperationException("Give the dashboard a name (at most 100 characters).");
        return name;
    }

    private static string? ValidDescription(string? d)
    {
        d = string.IsNullOrWhiteSpace(d) ? null : d.Trim();
        if (d is { Length: > 500 }) throw new InvalidOperationException("The description can be at most 500 characters.");
        return d;
    }

    private static string? ValidTitle(string? t)
    {
        t = string.IsNullOrWhiteSpace(t) ? null : t.Trim();
        if (t is { Length: > 100 }) throw new InvalidOperationException("A gadget title can be at most 100 characters.");
        return t;
    }

    private static int ValidWidth(int w) => Widths.Contains(w) ? w : 6;
}
