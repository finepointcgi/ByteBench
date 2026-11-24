using System.Security.Cryptography;
using System.Text;
using ByteBench.Data;
using ByteBench.Services.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Workbench;

/// <summary>An issue reduced to what progress needs.</summary>
public sealed record WorkbenchIssueRow(string? Wbs, IssueStatus Status, int? StoryPoints, IssueType Type);

/// <summary>The work state ByteBench derives for one WBS task: % complete and (when it says something) a status kind.</summary>
public sealed record WorkbenchDesired(string WbsCode, decimal Percent, string? Status, int Issues, int DoneIssues);

/// <summary>
/// Work state from issues. ByteBench owns work state (open/closed, progress); Workbench owns the schedule. For each WBS code the
/// % complete is done / total over the issues charged to it (epics excluded), weighted by story points when every issue has some,
/// else by count. Status is "Done" at 100%, "In Progress" once anything is started, else none (a status is never moved backwards).
/// </summary>
public static class WorkbenchProgress
{
    public const string Done = "Done";
    public const string InProgress = "In Progress";

    public static IReadOnlyList<WorkbenchDesired> Compute(IEnumerable<WorkbenchIssueRow> issues) =>
        issues.Where(i => i.Wbs != null && i.Type != IssueType.Epic).GroupBy(i => i.Wbs!, StringComparer.OrdinalIgnoreCase).Select(g =>
        {
            var list = g.ToList();
            var byPoints = list.All(i => i.StoryPoints is > 0);
            decimal Weight(WorkbenchIssueRow i) => byPoints ? i.StoryPoints!.Value : 1;
            var total = list.Sum(Weight);
            var done = list.Where(i => i.Status == IssueStatus.Done).Sum(Weight);
            var percent = total == 0 ? 0 : decimal.Round(done / total * 100m, 2);
            var started = list.Any(i => i.Status is IssueStatus.InProgress or IssueStatus.InReview or IssueStatus.Done);
            var status = percent >= 100m ? Done : started ? InProgress : null;
            return new WorkbenchDesired(g.Key, percent, status, list.Count, list.Count(i => i.Status == IssueStatus.Done));
        }).OrderBy(d => d.WbsCode, StringComparer.OrdinalIgnoreCase).ToList();

    private static readonly string[] DoneAliases = [Done, "Complete", "Completed"];
    private static readonly string[] InProgressAliases = [InProgress, "In-Progress", "Started", "Active", "Working", "In work"];

    /// <summary>The Workbench Status option for <paramref name="canonical"/>; null when the sheet has no such option (percent is pushed alone).</summary>
    public static string? MapStatus(string? canonical, IReadOnlyList<string> options, bool hasStatusColumn)
    {
        if (canonical == null || !hasStatusColumn) return null;
        if (options.Count == 0) return canonical;
        var aliases = canonical == Done ? DoneAliases : InProgressAliases;
        return aliases.Select(a => options.FirstOrDefault(o => o.Equals(a, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(o => o != null);
    }

    public static string Key(int projectId, string wbs, decimal percent, string? status) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{projectId}|{wbs.ToLowerInvariant()}|{percent:0.##}|{status}")))[..32];
}

/// <summary>
/// The durable outbox for pushing work state to Workbench (the app's webhook dispatcher pattern: a row per job, a worker that
/// delivers with retry and backoff). <see cref="ReconcileAsync"/> queues what differs from the last push; <see cref="DeliverDueAsync"/>
/// sends it with <c>POST /api/v1/sheets/{sheet}/tasks/{item}/status</c> (status and % complete only: no dates, no WBS structure,
/// and never time). Transient failures (timeouts, 5xx) retry with backoff; refusals (403, 404, unknown status) fail at once
/// with the reason in the log. Writing an absolute value is idempotent, and a row whose value Workbench already has is skipped.
/// </summary>
public sealed class WorkbenchPushEngine(IDbContextFactory<AppDbContext> dbFactory, WorkbenchDirectory directory, WorkbenchClient client, WorkbenchCredentials credentials,
    IServiceScopeFactory scopes)
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan[] Backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1)];
    private static readonly TimeSpan Claim = TimeSpan.FromMinutes(2);

    /// <summary>Queues a job for every WBS task of the project (or of every opted-in project) whose derived state differs from the last push. Returns how many were queued.</summary>
    public async Task<int> ReconcileAsync(int? projectId = null, CancellationToken ct = default)
    {
        if (credentials.BaseUrl == null || credentials.ApiKey == null) return 0;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var projects = await db.Projects.Where(p => p.WorkbenchPushEnabled && p.WorkbenchProjectCode != null && (projectId == null || p.Id == projectId))
            .Select(p => new { p.Id, p.WorkbenchWbs, p.WorkbenchProjectCode }).ToListAsync(ct);
        var queued = 0;
        foreach (var p in projects)
        {
            var rows = await db.Issues.AsNoTracking().Where(i => i.ProjectId == p.Id)
                .Select(i => new WorkbenchIssueRow(i.WorkbenchWbs ?? p.WorkbenchWbs, i.Status, i.StoryPoints, i.Type)).ToListAsync(ct);
            var syncs = await db.WorkbenchTaskSyncs.Where(s => s.ProjectId == p.Id).ToListAsync(ct);
            foreach (var d in WorkbenchProgress.Compute(rows))
            {
                var sync = syncs.FirstOrDefault(s => s.WbsCode.Equals(d.WbsCode, StringComparison.OrdinalIgnoreCase));
                var unchanged = sync != null ? sync.LastPercent == d.Percent && (d.Status == null || sync.LastStatus == d.Status) : d.Percent == 0 && d.Status == null;
                if (unchanged) continue;
                var key = WorkbenchProgress.Key(p.Id, d.WbsCode, d.Percent, d.Status);
                var open = await db.WorkbenchOutbox.Where(o => o.ProjectId == p.Id && o.State == WorkbenchOutboxState.Pending && o.WbsCode == d.WbsCode).ToListAsync(ct);
                if (open.Any(o => o.IdempotencyKey == key)) continue; // the same desired state is already queued
                foreach (var old in open) (old.State, old.Note, old.CompletedAt) = (WorkbenchOutboxState.Skipped, "Superseded by a newer state.", DateTime.UtcNow);
                db.WorkbenchOutbox.Add(new WorkbenchOutboxItem
                {
                    ProjectId = p.Id, WbsCode = d.WbsCode, WorkbenchProjectCode = p.WorkbenchProjectCode, Percent = d.Percent, Status = d.Status, IdempotencyKey = key,
                });
                queued++;
            }
            await db.SaveChangesAsync(ct);
        }
        return queued;
    }

    /// <summary>Sends the jobs that are due. Returns how many were attempted.</summary>
    public async Task<int> DeliverDueAsync(CancellationToken ct = default, int max = 25)
    {
        if (credentials.BaseUrl == null || credentials.ApiKey == null) return 0;
        var attempted = 0;
        List<int> ids;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var now = DateTime.UtcNow;
            ids = await db.WorkbenchOutbox.Where(o => o.State == WorkbenchOutboxState.Pending && o.NextAttemptAt <= now).OrderBy(o => o.Id).Select(o => o.Id).Take(max).ToListAsync(ct);
        }
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var now = DateTime.UtcNow;
            // Claim the row so a second sweep (or a second node) doesn't send it too.
            var claimed = await db.WorkbenchOutbox.Where(o => o.Id == id && o.State == WorkbenchOutboxState.Pending && o.NextAttemptAt <= now)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAt, now + Claim), ct);
            if (claimed == 0) continue;
            attempted++;
            var item = await db.WorkbenchOutbox.FirstAsync(o => o.Id == id, ct);
            await DeliverAsync(db, item, ct);
        }
        return attempted;
    }

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        await ReconcileAsync(null, ct);
        await DeliverDueAsync(ct);
    }

    private async Task DeliverAsync(AppDbContext db, WorkbenchOutboxItem item, CancellationToken ct)
    {
        item.Attempts++;
        var outcome = await TryPushAsync(db, item, ct);
        switch (outcome.Result)
        {
            case PushResult.Done:
            case PushResult.AlreadyThere:
                (item.State, item.CompletedAt, item.LastError, item.Note) = (outcome.Result == PushResult.Done ? WorkbenchOutboxState.Done : WorkbenchOutboxState.Skipped, DateTime.UtcNow, null, outcome.Note);
                var sync = await db.WorkbenchTaskSyncs.FirstOrDefaultAsync(s => s.ProjectId == item.ProjectId && s.WbsCode == item.WbsCode, ct);
                if (sync == null) db.WorkbenchTaskSyncs.Add(sync = new WorkbenchTaskSync { ProjectId = item.ProjectId, WbsCode = item.WbsCode });
                (sync.LastPercent, sync.LastStatus, sync.LastPushedAt) = (item.Percent, item.Status, DateTime.UtcNow);
                break;
            case PushResult.Retry when item.Attempts < MaxAttempts:
                (item.LastError, item.NextAttemptAt) = (outcome.Error, DateTime.UtcNow + Backoff[Math.Min(item.Attempts - 1, Backoff.Length - 1)]);
                break;
            default:
                (item.State, item.CompletedAt, item.LastError) = (WorkbenchOutboxState.Failed, DateTime.UtcNow, outcome.Error);
                break;
        }
        await db.SaveChangesAsync(ct);
        await AuditAsync(item, outcome);
    }

    private async Task AuditAsync(WorkbenchOutboxItem item, PushOutcome outcome)
    {
        if (outcome.Result == PushResult.Retry && item.State == WorkbenchOutboxState.Pending) return; // audited when it finally succeeds or fails
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ProjectAccess>().ActAsSystem();
        await scope.ServiceProvider.GetRequiredService<AuditLog>().RecordAsync(
            item.State == WorkbenchOutboxState.Failed ? "workbench.push_failed" : "workbench.push", "project", item.ProjectId, item.ProjectId,
            new { wbs = item.WbsCode, percent = item.Percent, status = item.Status, attempts = item.Attempts, result = item.State.ToString(), note = item.Note, error = item.LastError });
    }

    private enum PushResult { Done, AlreadyThere, Retry, Fail }
    private sealed record PushOutcome(PushResult Result, string? Error = null, string? Note = null);

    private async Task<PushOutcome> TryPushAsync(AppDbContext db, WorkbenchOutboxItem item, CancellationToken ct)
    {
        if (item.WorkbenchProjectCode == null) return new(PushResult.Fail, "The project has no Workbench project code.");
        var project = await directory.FindProjectAsync(item.WorkbenchProjectCode);
        if (project.Status is WbStatus.Unreachable or WbStatus.NotConfigured) return new(PushResult.Retry, project.Message);
        if (!project.Ok) return new(PushResult.Fail, project.Message);
        if (project.Value!.SheetId is not { } sheetId) return new(PushResult.Fail, $"Workbench project {project.Value.Code} has no plan sheet.");
        var wbs = await directory.FindWbsAsync(project.Value, item.WbsCode, fresh: true);
        if (wbs.Status is WbStatus.Unreachable or WbStatus.NotConfigured) return new(PushResult.Retry, wbs.Message);
        if (!wbs.Ok) return new(PushResult.Fail, wbs.Message);
        if (wbs.Value!.ItemId is not { } itemId) return new(PushResult.Fail, $"WBS element {item.WbsCode} has no plan task in Workbench.");

        var plan = await directory.GetPlanAsync(sheetId, fresh: true);
        if (plan.Status is WbStatus.Unreachable or WbStatus.NotConfigured) return new(PushResult.Retry, plan.Message);
        if (!plan.Ok) return new(PushResult.Fail, plan.Message);
        if (!plan.Value!.HasPercentColumn) return new(PushResult.Fail, "The plan sheet has no % Complete column.");
        if (!plan.Value.Items.TryGetValue(itemId, out var current)) return new(PushResult.Fail, "The task isn't on the plan sheet any more.");

        var status = WorkbenchProgress.MapStatus(item.Status, plan.Value.StatusOptions, plan.Value.HasStatusColumn);
        var notes = new List<string>();
        if (item.Status != null && status == null) notes.Add($"The plan's Status column has no \"{item.Status}\" option; only % complete was set.");
        var statusSame = status == null || string.Equals(current.Status, status, StringComparison.OrdinalIgnoreCase);
        if (current.Percent == item.Percent && statusSame) return new(PushResult.AlreadyThere, null, "Workbench already had this value.");

        // Conflict note: Workbench changed the work state since the last push. ByteBench owns work state, so it wins, visibly.
        var sync = await db.WorkbenchTaskSyncs.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == item.ProjectId && s.WbsCode == item.WbsCode, ct);
        if (sync?.LastPercent != null && current.Percent != null && current.Percent != sync.LastPercent && current.Percent != item.Percent)
            notes.Add($"Workbench had {current.Percent:0.##}% (ByteBench last set {sync.LastPercent:0.##}%); replaced with {item.Percent:0.##}%. Work state is owned by ByteBench.");
        else if (sync == null && current.Percent is > 0 && current.Percent != item.Percent)
            notes.Add($"Workbench had {current.Percent:0.##}%; replaced with {item.Percent:0.##}%. Work state is owned by ByteBench.");

        var r = await client.PostAsync($"/sheets/{sheetId}/tasks/{itemId}/status", new { status, percentComplete = item.Percent }, ct);
        var note = notes.Count == 0 ? null : string.Join(" ", notes);
        return r.Status switch
        {
            WbStatus.Ok => new PushOutcome(PushResult.Done, null, note),
            WbStatus.Unreachable or WbStatus.NotConfigured => new PushOutcome(PushResult.Retry, r.Message),
            WbStatus.Forbidden => new PushOutcome(PushResult.Fail, "Workbench refused the update: the key needs sheets:write and an Editor role on the plan sheet (or be assigned the task)."),
            _ => new PushOutcome(PushResult.Fail, r.Message),
        };
    }
}

/// <summary>Background sweep: queues and delivers work-state pushes every <c>Workbench:PushIntervalSeconds</c>. Does nothing until the integration is connected and a project opts in.</summary>
public sealed class WorkbenchPushWorker(WorkbenchPushEngine engine, IOptionsMonitor<WorkbenchOptions> options, ILogger<WorkbenchPushWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try { WorkerPulse.Report("workbench-push", options.CurrentValue.PushWorkerEnabled); WorkerPulse.Tick("workbench-push"); if (options.CurrentValue.PushWorkerEnabled) await engine.RunOnceAsync(stoppingToken); WorkerPulse.Ok("workbench-push"); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { WorkerPulse.Fail("workbench-push", ex); log.LogError(ex, "Workbench push sweep failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, options.CurrentValue.PushIntervalSeconds)), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}

/// <summary>A row of the project's push log.</summary>
public sealed record WorkbenchPushLogRow(int Id, string WbsCode, decimal Percent, string? Status, WorkbenchOutboxState State, int Attempts, DateTime CreatedAt,
    DateTime? CompletedAt, DateTime NextAttemptAt, string? Error, string? Note);

/// <summary>The project-level controls for pushing work state (Maintainer+): the opt-in, "sync now", retry and the visible log.</summary>
public sealed class WorkbenchPushService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, AuditLog audit, WorkbenchPushEngine engine, WorkbenchCredentials credentials)
{
    public async Task<bool> IsEnabledAsync(int projectId)
    {
        await access.RequireRoleAsync(projectId, ProjectRole.Maintainer, "see Workbench sync");
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Projects.Where(p => p.Id == projectId).Select(p => p.WorkbenchPushEnabled).FirstOrDefaultAsync();
    }

    public async Task SetEnabledAsync(int projectId, bool enabled, int? actorId = null)
    {
        await access.RequireRoleAsync(projectId, ProjectRole.Maintainer, "change Workbench sync", actorId);
        if (enabled && (credentials.BaseUrl == null || credentials.ApiKey == null))
            throw new InvalidOperationException("Workbench isn't connected: a site administrator sets the address and API key on the Workbench admin page first.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var p = await db.Projects.FirstOrDefaultAsync(x => x.Id == projectId) ?? throw new InvalidOperationException("Project not found.");
        if (enabled && string.IsNullOrEmpty(p.WorkbenchProjectCode))
            throw new InvalidOperationException("Pick the Workbench project first: work state is written to that project's plan.");
        if (p.WorkbenchPushEnabled == enabled) return;
        p.WorkbenchPushEnabled = enabled;
        await db.SaveChangesAsync();
        await audit.RecordAsync("project.workbench_push_changed", "project", projectId, projectId, new { enabled }, actorId);
    }

    /// <summary>Queues and sends what differs now, without waiting for the sweep.</summary>
    public async Task<int> SyncNowAsync(int projectId, int? actorId = null, CancellationToken ct = default)
    {
        await access.RequireRoleAsync(projectId, ProjectRole.Maintainer, "sync with Workbench", actorId);
        var queued = await engine.ReconcileAsync(projectId, ct);
        await engine.DeliverDueAsync(ct);
        return queued;
    }

    public async Task<IReadOnlyList<WorkbenchPushLogRow>> RecentAsync(int projectId, int count = 20)
    {
        await access.RequireRoleAsync(projectId, ProjectRole.Maintainer, "see Workbench sync");
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.WorkbenchOutbox.AsNoTracking().Where(o => o.ProjectId == projectId).OrderByDescending(o => o.Id).Take(count)
            .Select(o => new WorkbenchPushLogRow(o.Id, o.WbsCode, o.Percent, o.Status, o.State, o.Attempts, o.CreatedAt, o.CompletedAt, o.NextAttemptAt, o.LastError, o.Note)).ToListAsync();
    }

    /// <summary>Puts a failed job back in the queue (Maintainer+).</summary>
    public async Task RetryAsync(int projectId, int outboxId, int? actorId = null)
    {
        await access.RequireRoleAsync(projectId, ProjectRole.Maintainer, "retry a Workbench sync", actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.WorkbenchOutbox.FirstOrDefaultAsync(o => o.Id == outboxId && o.ProjectId == projectId && o.State == WorkbenchOutboxState.Failed);
        if (item == null) return;
        (item.State, item.Attempts, item.NextAttemptAt, item.CompletedAt, item.LastError) = (WorkbenchOutboxState.Pending, 0, DateTime.UtcNow, null, null);
        await db.SaveChangesAsync();
    }
}
