using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Ci;

/// <summary>
/// Deploy freeze windows of a project (scoped). Reading needs ViewProject; adding or removing one needs
/// ManageRepositories (Maintainer+). While a window is active, deploy jobs to protected environments can't be approved,
/// run, redeployed or rolled back.
/// </summary>
public class DeployFreezeService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, Audit.AuditLog audit)
{
    /// <summary>
    /// True if <paramref name="w"/> is active at <paramref name="nowUtc"/>. A date range is active from its start
    /// (inclusive) to its end (exclusive); either side may be open. A cron window is active when its next end comes before
    /// its next start, i.e. the last thing that happened was a start.
    /// </summary>
    public static bool IsActive(DeployFreezeWindow w, DateTime nowUtc)
    {
        if (w.CronStart != null && w.CronEnd != null)
        {
            var start = CiCron.TryParse(w.CronStart, out _);
            var end = CiCron.TryParse(w.CronEnd, out _);
            if (start == null || end == null) return false;
            var nextStart = start.Next(nowUtc.AddMinutes(-1)); // a start at this very minute counts
            var nextEnd = end.Next(nowUtc);
            return nextEnd != null && (nextStart == null || nextEnd < nextStart || nextStart <= nowUtc);
        }
        if (w.StartsAt == null && w.EndsAt == null) return false;
        return (w.StartsAt == null || nowUtc >= w.StartsAt) && (w.EndsAt == null || nowUtc < w.EndsAt);
    }

    /// <summary>The project's active freeze window at <paramref name="nowUtc"/> (no permission check), or null.</summary>
    public static async Task<DeployFreezeWindow?> ActiveAsync(AppDbContext db, int projectId, DateTime nowUtc)
    {
        var windows = await db.DeployFreezeWindows.AsNoTracking().Where(f => f.ProjectId == projectId).ToListAsync();
        return windows.FirstOrDefault(w => IsActive(w, nowUtc));
    }

    public static string Describe(DeployFreezeWindow w) =>
        (w.Description is { Length: > 0 } d ? d + ": " : "") + (w.CronStart != null
            ? $"from \"{w.CronStart}\" to \"{w.CronEnd}\" (UTC)"
            : $"{(w.StartsAt is { } s ? s.ToString("yyyy-MM-dd HH:mm") : "…")} – {(w.EndsAt is { } e ? e.ToString("yyyy-MM-dd HH:mm") : "…")} UTC");

    public async Task<List<DeployFreezeWindow>> ListAsync(int projectId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.DeployFreezeWindows.AsNoTracking().Where(f => f.ProjectId == projectId).OrderBy(f => f.Id).ToListAsync();
    }

    /// <summary>The active window, if the user can view the project.</summary>
    public async Task<DeployFreezeWindow?> ActiveAsync(int projectId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        return await ActiveAsync(db, projectId, DateTime.UtcNow);
    }

    /// <summary>Adds a window: both cron expressions, or a date range (at least one side, start before end).</summary>
    public async Task<DeployFreezeWindow> CreateAsync(int projectId, string? description, string? cronStart, string? cronEnd, DateTime? startsAtUtc, DateTime? endsAtUtc)
    {
        await access.RequireAsync(projectId, Permission.ManageRepositories);
        cronStart = string.IsNullOrWhiteSpace(cronStart) ? null : cronStart.Trim();
        cronEnd = string.IsNullOrWhiteSpace(cronEnd) ? null : cronEnd.Trim();
        var w = new DeployFreezeWindow
        {
            ProjectId = projectId, Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            CreatedById = await access.ResolveUserIdAsync(),
        };
        if (w.Description is { Length: > 200 }) throw new InvalidOperationException("Keep the description under 200 characters.");
        if (cronStart != null || cronEnd != null)
        {
            if (cronStart == null || cronEnd == null) throw new InvalidOperationException("A recurring freeze needs both a start and an end cron expression.");
            if (CiCron.TryParse(cronStart, out var e1) == null) throw new InvalidOperationException($"Freeze start: {e1}");
            if (CiCron.TryParse(cronEnd, out var e2) == null) throw new InvalidOperationException($"Freeze end: {e2}");
            if (startsAtUtc != null || endsAtUtc != null) throw new InvalidOperationException("Give either cron expressions or dates, not both.");
            (w.CronStart, w.CronEnd) = (cronStart, cronEnd);
        }
        else
        {
            if (startsAtUtc == null && endsAtUtc == null) throw new InvalidOperationException("Give a start or end date, or two cron expressions.");
            if (startsAtUtc != null && endsAtUtc != null && endsAtUtc <= startsAtUtc) throw new InvalidOperationException("The freeze must end after it starts.");
            w.StartsAt = startsAtUtc is { } s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : null;
            w.EndsAt = endsAtUtc is { } e ? DateTime.SpecifyKind(e, DateTimeKind.Utc) : null;
        }
        await using var db = await dbFactory.CreateDbContextAsync();
        db.DeployFreezeWindows.Add(w);
        await db.SaveChangesAsync();
        await audit.RecordAsync("deploy_freeze.created", "deploy_freeze", w.Id, projectId, new { w.Description, w.CronStart, w.CronEnd, w.StartsAt, w.EndsAt });
        return w;
    }

    public async Task DeleteAsync(int projectId, int id)
    {
        await access.RequireAsync(projectId, Permission.ManageRepositories);
        await using var db = await dbFactory.CreateDbContextAsync();
        var w = await db.DeployFreezeWindows.FirstOrDefaultAsync(f => f.Id == id && f.ProjectId == projectId);
        if (w == null) return;
        db.DeployFreezeWindows.Remove(w);
        await db.SaveChangesAsync();
        await audit.RecordAsync("deploy_freeze.deleted", "deploy_freeze", id, projectId, new { w.Description });
    }
}
