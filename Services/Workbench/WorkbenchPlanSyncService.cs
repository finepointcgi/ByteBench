using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Workbench;

public sealed record PlanPreviewRow(string Title, IssueType Type, string? Wbs, string? Parent, string Action, string? Detail);

public sealed record PlanReport(int Created, int Updated, int Unchanged, int Flagged, int Links, IReadOnlyList<string> UnmatchedPeople, IReadOnlyList<string> Notes, DateTime At, string? Error = null);

public sealed record PlanPreview(IReadOnlyList<PlanPreviewRow> Rows, int ToCreate, int ToUpdate, int Unchanged, int ToFlag, IReadOnlyList<string> UnmatchedPeople, IReadOnlyList<string> Notes, string? Error);

/// <summary>Nudges the background worker when a sync is queued from the UI.</summary>
public sealed class WorkbenchPlanSignal
{
    public readonly SemaphoreSlim Semaphore = new(0);
    public void Nudge() { if (Semaphore.CurrentCount == 0) Semaphore.Release(); }
}

/// <summary>
/// Plan to epics and issues (Workbench to ByteBench). Workbench owns schedule and structure: WBS code, nesting, dates, planned hours and
/// dependencies are put back if edited in ByteBench (and noted in the issue's activity). ByteBench owns work state: status, progress and
/// resolution are never written here. Title is shared (the last change wins); the description's managed block is rewritten, the rest of
/// the description is ByteBench's. Tasks removed or moved in Workbench are flagged, never deleted. Nothing is written by preview.
/// Every write goes through <see cref="IssueService"/> as the "Workbench sync" account, so workflow, audit, automation and webhooks run as normal.
/// The sync is idempotent (the Workbench task id is the key), so a run interrupted by a restart is simply run again.
/// </summary>
public sealed class WorkbenchPlanSyncService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, AuditLog audit, WorkbenchPlanReader reader,
    IServiceScopeFactory scopes, WorkbenchPlanSignal signal, IOptionsMonitor<WorkbenchOptions> options)
{
    public const string BotText = "Workbench sync";

    public async Task<WorkbenchPlanSync> GetAsync(int projectId)
    {
        await access.RequireRoleAsync(projectId, ProjectRole.Maintainer, "see the Workbench plan sync");
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.WorkbenchPlanSyncs.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == projectId) ?? new WorkbenchPlanSync { ProjectId = projectId };
    }

    public static PlanReport? ReportOf(WorkbenchPlanSync s)
    {
        if (s.ReportJson == null) return null;
        try { return JsonSerializer.Deserialize<PlanReport>(s.ReportJson, WorkbenchClient.JsonOptions); } catch (JsonException) { return null; }
    }

    public async Task SaveSettingsAsync(int projectId, string? rootWbs, int epicLevels, bool reconcile, int? actorId = null)
    {
        await access.RequireRoleAsync(projectId, ProjectRole.Maintainer, "change the Workbench plan sync", actorId);
        var root = WorkbenchService.CleanCode(rootWbs, "WBS subtree");
        if (epicLevels is < 0 or > 3) throw new InvalidOperationException("Epic levels must be between 0 and 3.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.WorkbenchPlanSyncs.FirstOrDefaultAsync(s => s.ProjectId == projectId);
        if (row == null) db.WorkbenchPlanSyncs.Add(row = new WorkbenchPlanSync { ProjectId = projectId });
        (row.RootWbsCode, row.EpicLevels, row.ReconcileEnabled) = (root, epicLevels, reconcile);
        await db.SaveChangesAsync();
        await audit.RecordAsync("project.workbench_plan_sync_changed", "project", projectId, projectId, new { rootWbs = root, epicLevels, reconcile }, actorId);
    }

    /// <summary>What a run would create, update and flag. Writes nothing.</summary>
    public async Task<PlanPreview> PreviewAsync(int projectId, CancellationToken ct = default)
    {
        await access.RequireRoleAsync(projectId, ProjectRole.Maintainer, "preview the Workbench plan sync");
        var (project, settings, snap, error) = await LoadAsync(projectId, ct);
        if (error != null) return new([], 0, 0, 0, 0, [], [], error);
        var (items, notes, mapError) = WorkbenchPlanMapper.Map(snap!, settings.RootWbsCode, settings.EpicLevels);
        if (mapError != null) return new([], 0, 0, 0, 0, [], [], mapError);
        if (items.Count > options.CurrentValue.MaxTasksPerSync)
            return new([], 0, 0, 0, 0, [], notes, $"The plan has {items.Count} tasks; the limit is {options.CurrentValue.MaxTasksPerSync} (Workbench:MaxTasksPerSync). Choose a WBS subtree.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var links = await db.WorkbenchIssueLinks.AsNoTracking().Where(l => l.ProjectId == projectId).ToListAsync(ct);
        var linked = links.ToDictionary(l => l.TaskId);
        var issues = await db.Issues.AsNoTracking().Where(i => links.Select(l => l.IssueId).Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var titles = items.ToDictionary(i => i.Node.Id, i => i.Title);
        var rows = new List<PlanPreviewRow>();
        int create = 0, update = 0, same = 0;
        foreach (var d in items)
        {
            var parent = d.ParentTaskId is { } p ? titles[p] : null;
            if (!linked.TryGetValue(d.Node.Id, out var link) || !issues.TryGetValue(link.IssueId, out var issue)) { create++; rows.Add(new(d.Title, d.Type, d.Wbs, parent, "Create", null)); continue; }
            var changes = Diff(issue, d, link, null);
            if (changes.Count == 0) { same++; if (rows.Count < 500) rows.Add(new(d.Title, d.Type, d.Wbs, parent, "Unchanged", null)); }
            else { update++; rows.Add(new(d.Title, d.Type, d.Wbs, parent, "Update", string.Join("; ", changes))); }
        }
        var current = items.Select(i => i.Node.Id).ToHashSet();
        var flag = links.Count(l => !l.Missing && !current.Contains(l.TaskId));
        return new(rows.Take(1000).ToList(), create, update, same, flag, await UnmatchedAsync(db, items, ct), notes, null);
    }

    /// <summary>Queues a run for the background worker (Maintainer+). The settings card shows its progress.</summary>
    public async Task QueueAsync(int projectId, int? actorId = null)
    {
        await access.RequireRoleAsync(projectId, ProjectRole.Maintainer, "run the Workbench plan sync", actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.WorkbenchPlanSyncs.FirstOrDefaultAsync(s => s.ProjectId == projectId);
        if (row == null) db.WorkbenchPlanSyncs.Add(row = new WorkbenchPlanSync { ProjectId = projectId });
        if (row.State == WorkbenchPlanSyncState.Running && row.StartedAt > DateTime.UtcNow.AddMinutes(-10)) throw new InvalidOperationException("A sync is already running.");
        row.State = WorkbenchPlanSyncState.Queued;
        await db.SaveChangesAsync();
        await audit.RecordAsync("project.workbench_plan_sync_queued", "project", projectId, projectId, new { }, actorId);
        signal.Nudge();
    }

    /// <summary>Runs the sync now and returns its report (Maintainer+).</summary>
    public async Task<PlanReport> RunNowAsync(int projectId, int? actorId = null, CancellationToken ct = default)
    {
        await access.RequireRoleAsync(projectId, ProjectRole.Maintainer, "run the Workbench plan sync", actorId);
        await audit.RecordAsync("project.workbench_plan_sync_run", "project", projectId, projectId, new { }, actorId);
        return await RunAsync(projectId, ct);
    }

    private async Task<(Project? Project, WorkbenchPlanSync Settings, PlanSnapshot? Snap, string? Error)> LoadAsync(int projectId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct);
        var settings = await db.WorkbenchPlanSyncs.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == projectId, ct) ?? new WorkbenchPlanSync { ProjectId = projectId };
        if (project == null) return (null, settings, null, "Project not found.");
        if (string.IsNullOrEmpty(project.WorkbenchProjectCode)) return (project, settings, null, "Pick the Workbench project first.");
        var snap = await reader.ReadAsync(project.WorkbenchProjectCode, ct);
        return snap.Ok ? (project, settings, snap.Value, null) : (project, settings, null, snap.Message);
    }

    private static List<string> Diff(Issue issue, DesiredIssue d, WorkbenchIssueLink link, int? parentIssueId, bool checkParent = false)
    {
        var c = new List<string>();
        if (link.LastTitle != d.Title && issue.Title != d.Title) c.Add("title");
        if (issue.StartDate?.Date != d.Start) c.Add("start date");
        if (issue.DueDate?.Date != d.Due) c.Add("due date");
        if (d.EstimateMinutes != null && issue.OriginalEstimateMinutes != d.EstimateMinutes) c.Add("estimate");
        if (d.Wbs != null && issue.WorkbenchWbs != d.Wbs) c.Add("WBS code");
        if (WorkbenchPlanMapper.ApplyBlock(issue.Description, d.Block) != (issue.Description ?? "")) c.Add("plan block");
        if (checkParent && issue.ParentId != parentIssueId) c.Add("parent");
        if (link.Missing) c.Add("back in the plan");
        return c;
    }

    private static async Task<IReadOnlyList<string>> UnmatchedAsync(AppDbContext db, IEnumerable<DesiredIssue> items, CancellationToken ct)
    {
        var emails = items.SelectMany(i => i.Node.Emails).Select(e => e.Trim().ToUpperInvariant()).Distinct().ToList();
        if (emails.Count == 0) return [];
        var known = await db.Users.Where(u => u.NormalizedEmail != null && emails.Contains(u.NormalizedEmail)).Select(u => u.NormalizedEmail!).ToListAsync(ct);
        return emails.Except(known).Order().Select(e => e.ToLowerInvariant()).ToList();
    }

    /// <summary>Runs the sync in its own DI scope as the Workbench sync account. Used by the page, the worker and tests. No permission check: callers did it.</summary>
    public async Task<PlanReport> RunAsync(int projectId, CancellationToken ct = default)
    {
        await SetStateAsync(projectId, s => (s.State, s.StartedAt, s.LastError, s.Processed) = (WorkbenchPlanSyncState.Running, DateTime.UtcNow, null, 0), ct);
        PlanReport report;
        try { report = await RunCoreAsync(projectId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            report = new PlanReport(0, 0, 0, 0, 0, [], [], DateTime.UtcNow, ex.Message);
        }
        await SetStateAsync(projectId, s =>
        {
            (s.State, s.LastRunAt, s.LastError) = (report.Error == null ? WorkbenchPlanSyncState.Idle : WorkbenchPlanSyncState.Failed, report.At, report.Error?[..Math.Min(report.Error.Length, 1000)]);
            s.ReportJson = JsonSerializer.Serialize(report, WorkbenchClient.JsonOptions);
        }, ct);
        await audit.RecordAsync(report.Error == null ? "workbench.plan_sync" : "workbench.plan_sync_failed", "project", projectId, projectId,
            new { report.Created, report.Updated, report.Unchanged, report.Flagged, report.Links, error = report.Error });
        return report;
    }

    private async Task SetStateAsync(int projectId, Action<WorkbenchPlanSync> change, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.WorkbenchPlanSyncs.FirstOrDefaultAsync(s => s.ProjectId == projectId, ct);
        if (row == null) db.WorkbenchPlanSyncs.Add(row = new WorkbenchPlanSync { ProjectId = projectId });
        change(row);
        await db.SaveChangesAsync(ct);
    }

    private async Task<PlanReport> RunCoreAsync(int projectId, CancellationToken ct)
    {
        var (project, settings, snap, error) = await LoadAsync(projectId, ct);
        if (error != null) return new(0, 0, 0, 0, 0, [], [], DateTime.UtcNow, error);
        var (items, notes0, mapError) = WorkbenchPlanMapper.Map(snap!, settings.RootWbsCode, settings.EpicLevels);
        var notes = new List<string>(notes0);
        if (mapError != null) return new(0, 0, 0, 0, 0, [], notes, DateTime.UtcNow, mapError);
        var max = options.CurrentValue.MaxTasksPerSync;
        if (items.Count > max) return new(0, 0, 0, 0, 0, [], notes, DateTime.UtcNow, $"The plan has {items.Count} tasks; the limit is {max} (Workbench:MaxTasksPerSync). Choose a WBS subtree.");
        await SetStateAsync(projectId, s => s.Total = items.Count, ct);

        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var botId = await WorkbenchBot.EnsureAsync(db, ct);
        sp.GetRequiredService<ProjectAccess>().ActAsSystem(botId);
        var issues = sp.GetRequiredService<IssueService>();
        var linksSvc = sp.GetRequiredService<LinkedWorkService>();

        var emails = items.SelectMany(i => i.Node.Emails).Select(e => e.Trim().ToUpperInvariant()).Distinct().ToList();
        var userByEmail = (await db.Users.Where(u => u.NormalizedEmail != null && emails.Contains(u.NormalizedEmail)).Select(u => new { u.Id, u.NormalizedEmail, u.DisplayName }).ToListAsync(ct))
            .ToDictionary(u => u.NormalizedEmail!, u => (u.Id, u.DisplayName));
        var links = await db.WorkbenchIssueLinks.Where(l => l.ProjectId == projectId).ToListAsync(ct);
        var byTask = links.ToDictionary(l => l.TaskId);
        var issueOf = links.ToDictionary(l => l.TaskId, l => l.IssueId);
        int created = 0, updated = 0, same = 0, n = 0;
        var wbsNote = project!.WorkbenchWbs;

        foreach (var d in items)
        {
            ct.ThrowIfCancellationRequested();
            int? parentIssue = d.ParentTaskId is { } pt && issueOf.TryGetValue(pt, out var pi) ? pi : null;
            var assignee = d.Node.Emails.Select(e => e.Trim().ToUpperInvariant()).Where(userByEmail.ContainsKey).Select(e => (int?)userByEmail[e].Id).FirstOrDefault();
            if (!byTask.TryGetValue(d.Node.Id, out var link))
            {
                var issue = new Issue
                {
                    Title = d.Title, Type = d.Type, ParentId = parentIssue, Description = WorkbenchPlanMapper.ApplyBlock(null, d.Block), StartDate = d.Start, DueDate = d.Due,
                    OriginalEstimateMinutes = d.EstimateMinutes, RemainingEstimateMinutes = d.EstimateMinutes, WorkbenchWbs = d.Wbs,
                };
                try { await issues.CreateIssueAsync(projectId, issue, [], botId); }
                catch (InvalidOperationException ex) { notes.Add($"Could not create \"{d.Title}\": {ex.Message}"); continue; }
                await using (var w = await dbFactory.CreateDbContextAsync(ct))
                {
                    // Estimates and the assignee are set directly: no per-issue "assigned to you" notification for a bulk creation.
                    await w.Issues.Where(i => i.Id == issue.Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.OriginalEstimateMinutes, d.EstimateMinutes)
                        .SetProperty(i => i.RemainingEstimateMinutes, d.EstimateMinutes).SetProperty(i => i.AssigneeId, assignee), ct);
                    w.WorkbenchIssueLinks.Add(link = new WorkbenchIssueLink { ProjectId = projectId, IssueId = issue.Id, TaskId = d.Node.Id, WbsCode = d.Wbs, LastTitle = d.Title });
                    await w.SaveChangesAsync(ct);
                }
                byTask[d.Node.Id] = link;
                issueOf[d.Node.Id] = issue.Id;
                created++;
            }
            else
            {
                issueOf[d.Node.Id] = link.IssueId;
                var changes = new List<string>();
                var text = "";
                await issues.UpdateIssueAsync(link.IssueId, botId, (i, _) =>
                {
                    changes = Diff(i, d, link, parentIssue, checkParent: true);
                    if (changes.Count == 0 && !(i.AssigneeId == null && assignee != null)) return Task.FromResult<string?>(null);
                    var parts = new List<string>();
                    if (link.LastTitle != d.Title) { if (i.Title != d.Title) { parts.Add($"title changed in Workbench: \"{i.Title}\" → \"{d.Title}\""); i.Title = d.Title; } }
                    else if (i.Title != d.Title && !notes.Contains($"Kept the title edited in ByteBench for {i.Number}: \"{i.Title}\" (Workbench has \"{d.Title}\"; the last change wins).")) notes.Add($"Kept the title edited in ByteBench for {i.Number}: \"{i.Title}\" (Workbench has \"{d.Title}\"; the last change wins).");
                    if (i.StartDate?.Date != d.Start) { parts.Add($"put back the start date ({Fmt(i.StartDate)} → {Fmt(d.Start)})"); i.StartDate = d.Start; }
                    if (i.DueDate?.Date != d.Due) { parts.Add($"put back the due date ({Fmt(i.DueDate)} → {Fmt(d.Due)})"); i.DueDate = d.Due; }
                    if (d.EstimateMinutes != null && i.OriginalEstimateMinutes != d.EstimateMinutes) { parts.Add("put back the original estimate from the plan's hours"); i.OriginalEstimateMinutes = d.EstimateMinutes; }
                    if (d.Wbs != null && i.WorkbenchWbs != d.Wbs) { parts.Add($"put back the WBS code ({d.Wbs})"); i.WorkbenchWbs = d.Wbs; }
                    if (i.ParentId != parentIssue) { parts.Add("put back the parent (the plan's nesting)"); i.ParentId = parentIssue; }
                    var desc = WorkbenchPlanMapper.ApplyBlock(i.Description, d.Block);
                    if (desc != (i.Description ?? "")) { i.Description = desc; parts.Add("updated the Workbench plan block"); }
                    if (i.AssigneeId == null && assignee != null) { i.AssigneeId = assignee; parts.Add("assigned from the plan's staffing"); }
                    if (link.Missing) parts.Add("the task is back in the Workbench plan");
                    text = parts.Count == 0 ? "" : "Workbench sync: " + string.Join("; ", parts);
                    return Task.FromResult<string?>(parts.Count == 0 ? null : text);
                });
                if (text.Length > 0) updated++; else same++;
                await using var w = await dbFactory.CreateDbContextAsync(ct);
                await w.WorkbenchIssueLinks.Where(l => l.Id == link.Id).ExecuteUpdateAsync(s => s.SetProperty(l => l.LastTitle, d.Title).SetProperty(l => l.WbsCode, d.Wbs)
                    .SetProperty(l => l.Missing, false).SetProperty(l => l.MissingSince, (DateTime?)null).SetProperty(l => l.LastSyncedAt, DateTime.UtcNow), ct);
            }
            if (++n % 25 == 0) await SetStateAsync(projectId, s => s.Processed = n, ct);
        }

        // Dependencies: "blocks" links between the synced issues (added only when missing; Workbench's lag and link type aren't mapped).
        var linkCount = 0;
        var current0 = items.Select(i => i.Node.Id).ToHashSet();
        foreach (var (pred, succ) in snap!.Dependencies)
        {
            if (!issueOf.TryGetValue(pred, out var a) || !issueOf.TryGetValue(succ, out var b) || !current0.Contains(pred) || !current0.Contains(succ)) continue;
            await using var w = await dbFactory.CreateDbContextAsync(ct);
            if (await w.IssueLinks.AnyAsync(l => l.Type == IssueLinkType.Blocks && l.SourceIssueId == a && l.TargetIssueId == b, ct)) continue;
            try { await linksSvc.AddLinkAsync(a, b, IssueLinkRelation.Blocks, botId); linkCount++; }
            catch (InvalidOperationException ex) { notes.Add($"Dependency not linked: {ex.Message}"); }
        }

        // Removed or moved in Workbench: flag, never delete.
        var current = items.Select(i => i.Node.Id).ToHashSet();
        var flagged = 0;
        foreach (var l in await db.WorkbenchIssueLinks.Where(l => l.ProjectId == projectId && !l.Missing).ToListAsync(ct))
        {
            if (current.Contains(l.TaskId)) continue;
            await issues.UpdateIssueAsync(l.IssueId, botId, (_, _) => Task.FromResult<string?>(
                "Workbench sync: Workbench no longer lists this task in the synced plan (removed or moved). It was not deleted: close or delete it, or move the task back."));
            l.Missing = true; l.MissingSince = DateTime.UtcNow;
            flagged++;
        }
        await db.SaveChangesAsync(ct);

        var unmatched = await UnmatchedAsync(db, items, ct);
        return new PlanReport(created, updated, same, flagged, linkCount, unmatched, notes, DateTime.UtcNow);
    }

    private static string Fmt(DateTime? d) => d?.ToString("yyyy-MM-dd") ?? "none";
    private static string Fmt(DateTime d) => d.ToString("yyyy-MM-dd");

    /// <summary>The issues flagged because their Workbench task is gone, for the project panel.</summary>
    public async Task<IReadOnlyList<(int IssueId, int Number, string Title, DateTime? Since)>> FlaggedAsync(int projectId)
    {
        await access.RequireRoleAsync(projectId, ProjectRole.Maintainer, "see the Workbench plan sync");
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await (from l in db.WorkbenchIssueLinks.AsNoTracking() join i in db.Issues on l.IssueId equals i.Id where l.ProjectId == projectId && l.Missing select new { i.Id, i.Number, i.Title, l.MissingSince }).ToListAsync();
        return rows.Select(r => (r.Id, r.Number, r.Title, r.MissingSince)).ToList();
    }
}

/// <summary>Runs queued syncs (the "Create epics and issues" button) and, every <c>Workbench:ReconcileMinutes</c>, reconciles the projects that enabled it.</summary>
public sealed class WorkbenchPlanWorker(IServiceScopeFactory scopes, WorkbenchPlanSignal signal, IOptionsMonitor<WorkbenchOptions> options, ILogger<WorkbenchPlanWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var lastReconcile = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (options.CurrentValue.PushWorkerEnabled)
                {
                    var reconcile = options.CurrentValue.ReconcileMinutes > 0 && DateTime.UtcNow - lastReconcile >= TimeSpan.FromMinutes(options.CurrentValue.ReconcileMinutes);
                    if (reconcile) lastReconcile = DateTime.UtcNow;
                    await using var scope = scopes.CreateAsyncScope();
                    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
                    var svc = scope.ServiceProvider.GetRequiredService<WorkbenchPlanSyncService>();
                    await using var db = await factory.CreateDbContextAsync(stoppingToken);
                    var stale = DateTime.UtcNow.AddMinutes(-10);
                    // Queued, or "running" when the process died mid-run (resumed: the sync is idempotent).
                    var due = await db.WorkbenchPlanSyncs.Where(s => s.State == WorkbenchPlanSyncState.Queued || (s.State == WorkbenchPlanSyncState.Running && s.StartedAt < stale)
                        || (reconcile && s.ReconcileEnabled && s.State != WorkbenchPlanSyncState.Running)).Select(s => s.ProjectId).ToListAsync(stoppingToken);
                    foreach (var id in due) await svc.RunAsync(id, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Workbench plan sync sweep failed"); }
            try { await signal.Semaphore.WaitAsync(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
