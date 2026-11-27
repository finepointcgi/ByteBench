using System.Globalization;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services.Accounts;
using ByteBench.Services.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Workspaces;

/// <summary>Parsing and printing of the CPU, memory and disk amounts workspaces are given (Docker's <c>--cpus</c>, <c>--memory</c>, <c>--storage-opt size</c>).</summary>
public static class WorkspaceUnits
{
    /// <summary>"2" or "0.5" CPUs → thousandths; null for nothing or something that isn't a positive number.</summary>
    public static int? ParseCpuMillis(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || d <= 0 || d > 1024) return null;
        return (int)Math.Round(d * 1000);
    }

    /// <summary>Docker-style sizes ("512m", "4g", "4GB") → MB. A bare number is bytes, as in Docker.</summary>
    public static int? ParseMb(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim().ToLowerInvariant();
        if (v.EndsWith('b') && v.Length > 1 && !char.IsDigit(v[^2])) v = v[..^1];
        var factor = 1.0 / (1024 * 1024);
        if (v.EndsWith('k')) { factor = 1.0 / 1024; v = v[..^1]; }
        else if (v.EndsWith('m')) { factor = 1; v = v[..^1]; }
        else if (v.EndsWith('g')) { factor = 1024; v = v[..^1]; }
        else if (v.EndsWith('t')) { factor = 1024 * 1024; v = v[..^1]; }
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || d < 0) return null;
        var mb = d * factor;
        return mb > int.MaxValue ? null : (int)Math.Round(mb);
    }

    public static string FormatCpu(int millis) => (millis / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + (millis == 1000 ? " CPU" : " CPUs");
    public static string FormatMb(int mb) => mb >= 1024 ? (mb / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " GB" : mb + " MB";
    public static string CpuArg(int millis) => (millis / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// What an administrator allows each user, and what a workspace gets by default. 0 means "no limit" for the caps. Stored as JSON in the site
/// settings; anything never set falls back to <c>Workspaces:*</c> (MaxPerUser, Cpus, Memory, DiskLimit), so an untouched server behaves as before.
/// </summary>
public sealed class WorkspaceLimitSettings
{
    /// <summary>Workspaces running at once per user (Starting, Running and Stopping count).</summary>
    public int MaxRunning { get; set; }
    /// <summary>Workspaces a user may have at all.</summary>
    public int MaxTotal { get; set; }
    /// <summary>CPUs per user across running workspaces (0: no cap).</summary>
    public double MaxCpus { get; set; }
    /// <summary>Memory per user across running workspaces, in MB.</summary>
    public int MaxMemoryMb { get; set; }
    /// <summary>Disk per user across all workspaces, in MB.</summary>
    public int MaxDiskMb { get; set; }
    public double DefaultCpus { get; set; }
    public int DefaultMemoryMb { get; set; }
    /// <summary>Disk limit of one workspace, in MB (0: none applied).</summary>
    public int DefaultDiskMb { get; set; }
}

/// <summary>What a user is using right now.</summary>
public sealed record WorkspaceUsage(int Running, int Total, int CpuMillis, int MemoryMb, int DiskMb);

/// <summary>Usage next to the limits, for the account page and the API. A limit of 0 is "no limit".</summary>
public sealed record WorkspaceLimitsView(WorkspaceLimitSettings Limits, WorkspaceUsage Usage, int IdleTimeoutMinutes, int IdleDefaultMinutes, int IdleMaxMinutes, int? IdleOverrideMinutes);

/// <summary>What a new workspace is given.</summary>
public sealed record WorkspaceResources(int CpuMillis, int MemoryMb, int DiskMb)
{
    public WorkspaceLimits ToLimits(int pidsLimit) => new(MemoryMb > 0 ? MemoryMb + "m" : null, CpuMillis > 0 ? WorkspaceUnits.CpuArg(CpuMillis) : null, pidsLimit, DiskMb > 0 ? DiskMb + "m" : null);
}

/// <param name="TimeoutMinutes">The idle period that applies to the owner.</param>
/// <param name="StopsAt">When it will be stopped if nothing happens; null while a task is running.</param>
/// <param name="Warning">True in the last <paramref name="WarnMinutes"/> before that.</param>
public sealed record WorkspaceIdleStatus(int TimeoutMinutes, DateTime? StopsAt, bool TaskRunning, bool Warning, int WarnMinutes);

/// <summary>
/// The idle period of workspaces (singleton: the reaper and the workspace page use it). The administrator sets a default and a maximum
/// (Admin → Workspaces); a user may choose their own period up to that maximum. Unset: <c>Workspaces:IdleTimeoutMinutes</c> and <c>IdleMaxMinutes</c>.
/// </summary>
public sealed class WorkspaceIdlePolicy(IDbContextFactory<AppDbContext> dbFactory, IOptionsMonitor<WorkspaceOptions> options, WorkspaceRuntime runtime)
{
    public const string DefaultKey = "workspaces.idle.default_minutes";
    public const string MaxKey = "workspaces.idle.max_minutes";

    public async Task<(int Default, int Max)> GetSiteAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await SiteAsync(db);
    }

    private async Task<(int Default, int Max)> SiteAsync(AppDbContext db)
    {
        var rows = await db.SiteSettings.AsNoTracking().Where(s => s.Key == DefaultKey || s.Key == MaxKey).ToListAsync();
        var o = options.CurrentValue;
        var max = int.TryParse(rows.FirstOrDefault(r => r.Key == MaxKey)?.Value, out var m) && m > 0 ? m : Math.Max(1, o.IdleMaxMinutes);
        var def = int.TryParse(rows.FirstOrDefault(r => r.Key == DefaultKey)?.Value, out var d) && d > 0 ? d : Math.Max(1, o.IdleTimeoutMinutes);
        return (Math.Min(def, max), max);
    }

    /// <summary>The idle period that applies to a user: their own choice, else the default, never above the maximum.</summary>
    public async Task<int> EffectiveMinutesAsync(int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await EffectiveAsync(db, userId);
    }

    public async Task<int> EffectiveAsync(AppDbContext db, int userId)
    {
        var (def, max) = await SiteAsync(db);
        var own = await db.UserWorkspacePreferences.AsNoTracking().Where(p => p.UserId == userId).Select(p => p.IdleTimeoutMinutes).FirstOrDefaultAsync();
        return Math.Clamp(own ?? def, 1, max);
    }

    public async Task<int?> UserOverrideAsync(int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.UserWorkspacePreferences.AsNoTracking().Where(p => p.UserId == userId).Select(p => p.IdleTimeoutMinutes).FirstOrDefaultAsync();
    }

    /// <summary>When the workspace is stopped for inactivity, and whether the page should be warning about it now. Null when it isn't the user's running workspace.</summary>
    public async Task<WorkspaceIdleStatus?> StatusAsync(string publicId, int userId)
    {
        var live = runtime.Find(publicId);
        if (live == null || live.OwnerId != userId) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        var ws = await db.Workspaces.AsNoTracking().Where(w => w.Id == live.Id && w.OwnerId == userId && w.State == WorkspaceState.Running)
            .Select(w => new { w.LastActivityAt }).FirstOrDefaultAsync();
        if (ws == null) return null;
        var minutes = await EffectiveAsync(db, userId);
        var last = ws.LastActivityAt > live.LastActivity ? ws.LastActivityAt : live.LastActivity;
        var stopsAt = last.AddMinutes(minutes);
        var taskRunning = runtime.HasRunningTask(live.Id);
        var warn = Math.Min(Math.Max(1, options.CurrentValue.IdleWarningMinutes), Math.Max(1, minutes / 2));
        return new WorkspaceIdleStatus(minutes, taskRunning ? null : stopsAt, taskRunning, !taskRunning && stopsAt - DateTime.UtcNow <= TimeSpan.FromMinutes(warn), warn);
    }

    /// <summary>"I'm still here": counts as activity and restarts the idle period. False when the workspace isn't the user's or isn't running.</summary>
    public async Task<bool> KeepAliveAsync(string publicId, int userId)
    {
        var live = runtime.Find(publicId);
        if (live == null || live.OwnerId != userId) return false;
        live.Touch();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Workspaces.Where(w => w.Id == live.Id && w.OwnerId == userId && w.State == WorkspaceState.Running)
            .ExecuteUpdateAsync(u => u.SetProperty(w => w.LastActivityAt, DateTime.UtcNow));
        return true;
    }
}

/// <summary>
/// Per-user caps and per-workspace defaults: read and changed here, enforced when a workspace starts (<see cref="EnforceAsync"/>, called by
/// <see cref="WorkspaceService.StartAsync"/> under its start gate), shown on the account page and the REST API. Changing a limit never touches
/// workspaces that already run; it applies to the next start.
/// </summary>
public sealed class WorkspaceLimitService(IDbContextFactory<AppDbContext> dbFactory, SiteSettings siteSettings, IOptionsMonitor<WorkspaceOptions> options,
    WorkspaceIdlePolicy idle, ProjectAccess access, AuditLog audit)
{
    public const string LimitsKey = "workspaces.limits";

    /// <summary>
    /// The workspaces that count toward a user's total and disk: everything that still has its container, which today is every live workspace.
    /// A stopped workspace is deleted, so it doesn't count; when stopping starts to keep a workspace's volumes, include <see cref="WorkspaceState.Stopped"/> here
    /// (and make <see cref="UsageAsync"/> count only the live ones as running).
    /// </summary>
    public static IQueryable<Workspace> Existing(IQueryable<Workspace> workspaces) =>
        workspaces.Where(w => w.State == WorkspaceState.Starting || w.State == WorkspaceState.Running || w.State == WorkspaceState.Stopping);

    public async Task<WorkspaceLimitSettings> GetAsync()
    {
        var o = options.CurrentValue;
        var s = new WorkspaceLimitSettings
        {
            MaxRunning = Math.Max(0, o.MaxPerUser),
            DefaultCpus = (WorkspaceUnits.ParseCpuMillis(o.Cpus) ?? 2000) / 1000.0,
            DefaultMemoryMb = WorkspaceUnits.ParseMb(o.Memory) ?? 4096,
            DefaultDiskMb = WorkspaceUnits.ParseMb(o.DiskLimit) ?? 0,
        };
        var json = await siteSettings.GetAsync(LimitsKey);
        if (string.IsNullOrWhiteSpace(json)) return s;
        try { return JsonSerializer.Deserialize<WorkspaceLimitSettings>(json) ?? s; }
        catch (JsonException) { return s; }
    }

    /// <summary>Saves the limits (site administrators only). Refuses numbers that make no sense, such as a default larger than the cap it must fit in.</summary>
    public async Task SetAsync(WorkspaceLimitSettings s, int adminId)
    {
        await RequireAdminAsync(adminId);
        if (s.MaxRunning < 0 || s.MaxTotal < 0 || s.MaxCpus < 0 || s.MaxMemoryMb < 0 || s.MaxDiskMb < 0 || s.DefaultCpus < 0 || s.DefaultMemoryMb < 0 || s.DefaultDiskMb < 0)
            throw new InvalidOperationException("Limits can't be negative (use 0 for no limit).");
        if (s.DefaultCpus < 0.25 || s.DefaultCpus > 256) throw new InvalidOperationException("A workspace's CPUs must be between 0.25 and 256.");
        if (s.DefaultMemoryMb < 256) throw new InvalidOperationException("A workspace needs at least 256 MB of memory.");
        if (s.MaxRunning > 0 && s.MaxTotal > 0 && s.MaxTotal < s.MaxRunning) throw new InvalidOperationException($"The total ({s.MaxTotal}) can't be lower than the running limit ({s.MaxRunning}).");
        if (s.MaxCpus > 0 && s.DefaultCpus > s.MaxCpus) throw new InvalidOperationException($"A workspace's default of {s.DefaultCpus:0.##} CPUs wouldn't fit in the per-user CPU cap of {s.MaxCpus:0.##}.");
        if (s.MaxMemoryMb > 0 && s.DefaultMemoryMb > s.MaxMemoryMb) throw new InvalidOperationException($"A workspace's default memory ({WorkspaceUnits.FormatMb(s.DefaultMemoryMb)}) wouldn't fit in the per-user memory cap ({WorkspaceUnits.FormatMb(s.MaxMemoryMb)}).");
        if (s.MaxDiskMb > 0 && s.DefaultDiskMb > s.MaxDiskMb) throw new InvalidOperationException($"A workspace's default disk ({WorkspaceUnits.FormatMb(s.DefaultDiskMb)}) wouldn't fit in the per-user disk cap ({WorkspaceUnits.FormatMb(s.MaxDiskMb)}).");
        if (s.MaxDiskMb > 0 && s.DefaultDiskMb == 0) throw new InvalidOperationException("With a per-user disk cap, a workspace needs a disk limit of its own too.");
        await siteSettings.SetAsync(LimitsKey, JsonSerializer.Serialize(s), adminId);
        await audit.RecordAsync("workspace.admin_limits", "workspaces", null, details: s, actorId: adminId);
    }

    public async Task SetIdleAsync(int defaultMinutes, int maxMinutes, int adminId)
    {
        await RequireAdminAsync(adminId);
        if (defaultMinutes < 1 || maxMinutes < 1 || maxMinutes > 60 * 24 * 7) throw new InvalidOperationException("The idle period must be between 1 minute and 7 days.");
        if (defaultMinutes > maxMinutes) throw new InvalidOperationException($"The default idle period ({defaultMinutes} min) can't be longer than the maximum ({maxMinutes} min).");
        await siteSettings.SetAsync(WorkspaceIdlePolicy.DefaultKey, defaultMinutes.ToString(CultureInfo.InvariantCulture), adminId);
        await siteSettings.SetAsync(WorkspaceIdlePolicy.MaxKey, maxMinutes.ToString(CultureInfo.InvariantCulture), adminId);
        await audit.RecordAsync("workspace.admin_idle", "workspaces", null, details: new { defaultMinutes, maxMinutes }, actorId: adminId);
    }

    /// <summary>A user's own idle period; null goes back to the default. Never more than the administrator's maximum.</summary>
    public async Task SetMyIdleAsync(int userId, int? minutes)
    {
        var (_, max) = await idle.GetSiteAsync();
        if (minutes is < 1) throw new InvalidOperationException("The idle period must be at least 1 minute.");
        if (minutes > max) throw new InvalidOperationException($"The idle period can't be more than {max} minutes (the administrator's maximum).");
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.UserWorkspacePreferences.FirstOrDefaultAsync(p => p.UserId == userId);
        if (row == null) db.UserWorkspacePreferences.Add(row = new UserWorkspacePreference { UserId = userId });
        row.IdleTimeoutMinutes = minutes;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>The idle period a new workspace of this user gets.</summary>
    public Task<int> IdleMinutesAsync(AppDbContext db, int userId) => idle.EffectiveAsync(db, userId);

    public async Task<WorkspaceUsage> GetUsageAsync(int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await UsageAsync(db, userId, await GetAsync());
    }

    private static async Task<WorkspaceUsage> UsageAsync(AppDbContext db, int userId, WorkspaceLimitSettings s)
    {
        var existing = await Existing(db.Workspaces.AsNoTracking().Where(w => w.OwnerId == userId)).Select(w => w.Id).ToListAsync();
        var alloc = await db.WorkspaceAllocations.AsNoTracking().Where(a => existing.Contains(a.WorkspaceId)).ToDictionaryAsync(a => a.WorkspaceId);
        var fallback = new WorkspaceResources((int)Math.Round(s.DefaultCpus * 1000), s.DefaultMemoryMb, s.DefaultDiskMb);
        int cpu = 0, mem = 0, disk = 0;
        foreach (var id in existing)
        {
            var a = alloc.TryGetValue(id, out var row) ? new WorkspaceResources(row.CpuMillis, row.MemoryMb, row.DiskMb) : fallback;
            cpu += a.CpuMillis;
            mem += a.MemoryMb;
            disk += a.DiskMb;
        }
        return new WorkspaceUsage(existing.Count, existing.Count, cpu, mem, disk); // today every existing workspace is running (see Existing)
    }

    public async Task<WorkspaceLimitsView> GetViewAsync(int userId)
    {
        var limits = await GetAsync();
        var (def, max) = await idle.GetSiteAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        return new WorkspaceLimitsView(limits, await UsageAsync(db, userId, limits), await idle.EffectiveAsync(db, userId), def, max, await idle.UserOverrideAsync(userId));
    }

    /// <summary>
    /// Checks that a new workspace fits the user's limits and returns what it is given. Throws an <see cref="InvalidOperationException"/> whose message says
    /// which limit and how to get under it (it reaches the user in the UI and the REST API as a 422). Call it under <see cref="WorkspaceRuntime.StartGate"/>.
    /// </summary>
    public async Task<WorkspaceResources> EnforceAsync(AppDbContext db, int userId)
    {
        var s = await GetAsync();
        var res = new WorkspaceResources((int)Math.Round(s.DefaultCpus * 1000), s.DefaultMemoryMb, s.DefaultDiskMb);
        var u = await UsageAsync(db, userId, s);
        if (s.MaxRunning > 0 && u.Running >= s.MaxRunning)
            throw new InvalidOperationException($"You already have {u.Running} workspace{(u.Running == 1 ? "" : "s")} running (the limit is {s.MaxRunning}). Stop one first.");
        if (s.MaxTotal > 0 && u.Total >= s.MaxTotal)
            throw new InvalidOperationException($"You already have {u.Total} workspace{(u.Total == 1 ? "" : "s")} (the limit is {s.MaxTotal}). Delete one first.");
        if (s.MaxCpus > 0 && u.CpuMillis + res.CpuMillis > s.MaxCpus * 1000 + 0.5)
            throw new InvalidOperationException($"A new workspace needs {WorkspaceUnits.FormatCpu(res.CpuMillis)} but you are using {WorkspaceUnits.FormatCpu(u.CpuMillis)} of your {s.MaxCpus:0.##} CPU limit. Stop a workspace first.");
        if (s.MaxMemoryMb > 0 && u.MemoryMb + res.MemoryMb > s.MaxMemoryMb)
            throw new InvalidOperationException($"A new workspace needs {WorkspaceUnits.FormatMb(res.MemoryMb)} of memory but you are using {WorkspaceUnits.FormatMb(u.MemoryMb)} of your {WorkspaceUnits.FormatMb(s.MaxMemoryMb)} limit. Stop a workspace first.");
        if (s.MaxDiskMb > 0 && u.DiskMb + res.DiskMb > s.MaxDiskMb)
            throw new InvalidOperationException($"A new workspace needs {WorkspaceUnits.FormatMb(res.DiskMb)} of disk but you are using {WorkspaceUnits.FormatMb(u.DiskMb)} of your {WorkspaceUnits.FormatMb(s.MaxDiskMb)} limit. Delete a workspace first.");
        return res;
    }

    private async Task RequireAdminAsync(int userId)
    {
        if (!await access.IsAdminAsync(userId)) throw new ForbiddenException("Only site administrators can do this.");
    }
}
