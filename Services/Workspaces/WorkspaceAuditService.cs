using System.Security.Claims;
using System.Text;
using ByteBench.Data;
using ByteBench.Services.Accounts;
using ByteBench.Services.Audit;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Workspaces;

/// <summary>
/// The workspace audit actions. They are ordinary <see cref="AuditEvent"/> rows (the app's one audit log: append-only, no foreign keys, deleted only by
/// retention, with secrets scrubbed from details), all named <c>workspace.*</c>, with <c>TargetType = "workspace"</c> and the workspace's public id as target.
/// <b>Details never hold secret values</b>: secrets are recorded by name only (<c>names: [...]</c>).
/// </summary>
public static class WorkspaceAuditActions
{
    public const string Prefix = "workspace.";

    // lifecycle
    public const string Created = "workspace.created", Started = "workspace.started", Stopped = "workspace.stopped", Restarted = "workspace.restarted",
        Resumed = "workspace.resumed", Deleted = "workspace.deleted", Failed = "workspace.failed", Commit = "workspace.commit";
    // sharing
    public const string Shared = "workspace.shared", ShareEnded = "workspace.share_ended";
    // access
    public const string TicketRedeemed = "workspace.ticket_redeemed";
    // secrets (names only), tasks and images
    // (the secret editor records workspace_secret.* itself, with the secret's name and scope, never its value)
    public const string SecretCreated = "workspace_secret.created", SecretUpdated = "workspace_secret.updated", SecretDeleted = "workspace_secret.deleted",
        SecretInjected = "workspace.secret_injected", TaskRun = "workspace.task_started", TaskStopped = "workspace.task_stopped", ImageOverride = "workspace.image_override",
        UserSettings = "workspace.user_settings", AdminCustomImages = "workspace.admin_custom_images";
    /// <summary>Everything the workspace audit views show: <c>workspace.*</c> and the secret editor's <c>workspace_secret.*</c>.</summary>
    public const string SecretPrefix = "workspace_secret.";
    // network
    public const string EgressBlocked = "workspace.egress_blocked";
    // configuration (site and project)
    public const string AdminSwitch = "workspace.admin_switch", AdminAiSwitch = "workspace.admin_ai_switch", ProjectSettings = "workspace.project_settings",
        AdminSharing = "workspace.admin_sharing", AdminEgress = "workspace.admin_egress", AdminAuditRetention = "workspace.admin_audit_retention",
        AuditExported = "workspace.audit_exported";
}

public sealed class WorkspaceAuditQuery
{
    /// <summary>One workspace by public id.</summary>
    public string? Workspace { get; set; }
    /// <summary>Username (exact), or "system".</summary>
    public string? Actor { get; set; }
    /// <summary>An action or the start of one (<c>workspace.secret</c>).</summary>
    public string? Action { get; set; }
    /// <summary>Free text in the target, the actor name or the details.</summary>
    public string? Text { get; set; }
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

/// <summary>
/// Reading the workspace audit trail: the admin search over every workspace and each workspace's own history (its owner and admins). This is a view on
/// <see cref="AuditLog"/>'s table, not a second log, and it has no way to change or delete an event.
/// </summary>
public sealed class WorkspaceAuditService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, AuditLog auditLog, SiteSettings siteSettings)
{
    public const string RetentionKey = "workspaces.audit.retention_days";
    public const int MaxRows = 100_000;

    /// <summary>Events across all workspaces (site admins only).</summary>
    public async Task<AuditPage> SearchAsync(int adminId, WorkspaceAuditQuery q, bool all = false)
    {
        if (!await access.IsAdminAsync(adminId)) throw new ForbiddenException("Only site administrators can search the workspace audit log.");
        await using var db = await dbFactory.CreateDbContextAsync();
        return await RunAsync(db, db.AuditEvents.AsNoTracking().Where(e => e.Action.StartsWith(WorkspaceAuditActions.Prefix) || e.Action.StartsWith(WorkspaceAuditActions.SecretPrefix)), q, all);
    }

    /// <summary>One workspace's history: its owner and site admins. Other people get "not found".</summary>
    public async Task<AuditPage> HistoryAsync(string publicId, int userId, WorkspaceAuditQuery? q = null, bool all = false)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var ws = await db.Workspaces.AsNoTracking().Where(w => w.PublicId == publicId).Select(w => new { w.OwnerId }).FirstOrDefaultAsync();
        if (ws == null || (ws.OwnerId != userId && !await access.IsAdminAsync(userId))) throw new InvalidOperationException("Workspace not found.");
        return await RunAsync(db, db.AuditEvents.AsNoTracking().Where(e => e.TargetType == "workspace" && e.TargetId == publicId), q ?? new WorkspaceAuditQuery(), all);
    }

    private static async Task<AuditPage> RunAsync(AppDbContext db, IQueryable<AuditEvent> query, WorkspaceAuditQuery q, bool all)
    {
        if (!string.IsNullOrWhiteSpace(q.Workspace)) { var id = q.Workspace.Trim(); query = query.Where(e => e.TargetType == "workspace" && e.TargetId == id); }
        if (!string.IsNullOrWhiteSpace(q.Actor))
        {
            var actor = q.Actor.Trim();
            if (actor.Equals("system", StringComparison.OrdinalIgnoreCase)) query = query.Where(e => e.ActorId == null);
            else
            {
                var norm = actor.ToUpperInvariant();
                var ids = await db.Users.Where(u => u.NormalizedUserName == norm).Select(u => u.Id).ToListAsync();
                query = query.Where(e => (e.ActorId != null && ids.Contains(e.ActorId.Value)) || e.ActorName == actor);
            }
        }
        if (!string.IsNullOrWhiteSpace(q.Action))
        {
            var prefix = q.Action.Trim().ToLowerInvariant();
            query = query.Where(e => e.Action.StartsWith(prefix));
        }
        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            var text = q.Text.Trim();
            query = query.Where(e => (e.Details != null && e.Details.Contains(text)) || (e.TargetId != null && e.TargetId.Contains(text)) || (e.ActorName != null && e.ActorName.Contains(text)));
        }
        if (q.FromUtc is { } from) query = query.Where(e => e.CreatedAt >= from);
        if (q.ToUtc is { } to) query = query.Where(e => e.CreatedAt < to);

        var total = await query.CountAsync();
        query = query.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id);
        if (!all)
        {
            var size = Math.Clamp(q.PageSize, 1, 500);
            query = query.Skip((Math.Max(q.Page, 1) - 1) * size).Take(size);
        }
        else query = query.Take(MaxRows);
        var events = await query.ToListAsync();
        var projectIds = events.Where(e => e.ProjectId != null).Select(e => e.ProjectId!.Value).Distinct().ToList();
        var keys = await db.Projects.Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Key);
        return new AuditPage(events, total, keys);
    }

    /// <summary>The action names recorded so far (the filter's suggestions), admin only.</summary>
    public async Task<List<string>> ActionNamesAsync(int adminId)
    {
        if (!await access.IsAdminAsync(adminId)) throw new ForbiddenException("Only site administrators can search the workspace audit log.");
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.AuditEvents.Where(e => e.Action.StartsWith(WorkspaceAuditActions.Prefix) || e.Action.StartsWith(WorkspaceAuditActions.SecretPrefix)).Select(e => e.Action).Distinct().OrderBy(a => a).ToListAsync();
    }

    /// <summary>Days workspace events are kept (0: as long as the site-wide audit retention, <c>Audit:RetentionDays</c>, allows).</summary>
    public async Task<int> GetRetentionDaysAsync() => int.TryParse(await siteSettings.GetAsync(RetentionKey), out var d) && d > 0 ? d : 0;

    public async Task SetRetentionDaysAsync(int days, int adminId)
    {
        if (!await access.IsAdminAsync(adminId)) throw new ForbiddenException("Only site administrators can do this.");
        if (days is < 0 or > 3650) throw new InvalidOperationException("Retention is 0 (site default) to 3650 days.");
        await siteSettings.SetAsync(RetentionKey, days.ToString(), adminId);
        await auditLog.RecordAsync(WorkspaceAuditActions.AdminAuditRetention, "workspaces", null, details: new { days }, actorId: adminId);
    }

    /// <summary>CSV of a page of events (same format and formula protection as the main audit export).</summary>
    public static string ToCsv(AuditPage page) => AuditLog.ToCsv(page);

    /// <summary>Records that someone exported the log (exports are audited like in the main audit log).</summary>
    public Task RecordExportAsync(int userId, string? workspace, int rows) =>
        auditLog.RecordAsync(WorkspaceAuditActions.AuditExported, workspace == null ? "workspaces" : "workspace", workspace, details: new { rows }, actorId: userId);
}

/// <summary>
/// Housekeeping for sharing and the audit trail (a minute's tick): ends shares of workspaces that are no longer running or whose invitee
/// was deactivated or lost access to the repository, and deletes <c>workspace.*</c> events older than the workspace retention setting once a day.
/// </summary>
public sealed class WorkspaceMaintenanceService(IServiceScopeFactory scopes, ILogger<WorkspaceMaintenanceService> log) : BackgroundService
{
    private DateTime lastRetention = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Workspace maintenance failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One pass (public for tests). Retention runs when a day has passed since the last pass, or when <paramref name="forceRetention"/>.</summary>
    public async Task RunOnceAsync(CancellationToken ct, bool forceRetention = false)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<WorkspaceShareStore>();
        await store.EndOrphansAsync();
        await RevalidateSharesAsync(sp, store, ct);
        if (forceRetention || DateTime.UtcNow - lastRetention > TimeSpan.FromDays(1))
        {
            lastRetention = DateTime.UtcNow;
            var days = int.TryParse(await sp.GetRequiredService<SiteSettings>().GetAsync(WorkspaceAuditService.RetentionKey, ct), out var d) ? d : 0;
            if (days > 0)
            {
                var cutoff = DateTime.UtcNow.AddDays(-days);
                await using var db = await sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(ct);
                var n = await db.AuditEvents.Where(e => (e.Action.StartsWith(WorkspaceAuditActions.Prefix) || e.Action.StartsWith(WorkspaceAuditActions.SecretPrefix)) && e.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
                if (n > 0) log.LogInformation("Workspace audit retention: deleted {Count} events older than {Days} days", n, days);
            }
        }
    }

    private static async Task RevalidateSharesAsync(IServiceProvider sp, WorkspaceShareStore store, CancellationToken ct)
    {
        var access = sp.GetRequiredService<ProjectAccess>();
        await using var db = await sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(ct);
        var active = await db.WorkspaceShares.AsNoTracking().Include(s => s.User).Include(s => s.Workspace).Where(s => s.EndedAt == null).ToListAsync(ct);
        foreach (var s in active)
        {
            if (s.User.DeactivatedAt != null) await store.EndAsync(s, s.Workspace.PublicId, s.Workspace.ProjectId, "invitee deactivated", null);
            else if (!await access.CanAsync(s.Workspace.ProjectId, Permission.ViewProject, s.UserId)) await store.EndAsync(s, s.Workspace.PublicId, s.Workspace.ProjectId, "invitee lost access", null);
        }
    }
}

public static class WorkspaceAuditEndpoints
{
    /// <summary>
    /// <c>GET /admin/workspaces/audit/export.csv</c> (site admins; filters workspace, actor, action, q, from, to) and
    /// <c>GET /workspaces/{id}/audit/export.csv</c> (the workspace's owner, or an admin). Both are audited.
    /// </summary>
    public static void MapWorkspaceAuditExport(this IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/workspaces/audit/export.csv", async (HttpContext http, WorkspaceAuditService svc, ProjectAccess access,
            string? workspace, string? actor, string? action, string? q, DateOnly? from, DateOnly? to) =>
        {
            if (UserIdOf(http.User) is not { } uid) return Results.Unauthorized();
            access.ActAs(uid);
            var query = Query(workspace, actor, action, q, from, to);
            AuditPage page;
            try { page = await svc.SearchAsync(uid, query, all: true); }
            catch (ForbiddenException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            await svc.RecordExportAsync(uid, workspace, page.Events.Count);
            return Results.File(Encoding.UTF8.GetBytes(WorkspaceAuditService.ToCsv(page)), "text/csv; charset=utf-8", $"bytebench-workspace-audit-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
        }).RequireAuthorization();

        app.MapGet("/workspaces/{id}/audit/export.csv", async (HttpContext http, WorkspaceAuditService svc, ProjectAccess access, string id,
            string? actor, string? action, string? q, DateOnly? from, DateOnly? to) =>
        {
            if (UserIdOf(http.User) is not { } uid) return Results.Unauthorized();
            access.ActAs(uid);
            AuditPage page;
            try { page = await svc.HistoryAsync(id, uid, Query(null, actor, action, q, from, to), all: true); }
            catch (InvalidOperationException) { return Results.NotFound(); }
            await svc.RecordExportAsync(uid, id, page.Events.Count);
            return Results.File(Encoding.UTF8.GetBytes(WorkspaceAuditService.ToCsv(page)), "text/csv; charset=utf-8", $"bytebench-workspace-{id[..Math.Min(8, id.Length)]}-audit.csv");
        }).RequireAuthorization();
    }

    public static WorkspaceAuditQuery Query(string? workspace, string? actor, string? action, string? q, DateOnly? from, DateOnly? to) => new()
    {
        Workspace = workspace, Actor = actor, Action = action, Text = q,
        FromUtc = from?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), ToUtc = to?.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
    };

    private static int? UserIdOf(ClaimsPrincipal user) => int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
