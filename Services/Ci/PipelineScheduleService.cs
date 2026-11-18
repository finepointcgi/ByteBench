using GitBench.Data;
using GitBench.Services.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GitBench.Services.Ci;

/// <summary>
/// Pipeline schedules (scoped). Listing and managing them needs ManageRepositories (Maintainer+). Saving a schedule
/// makes you its owner: scheduled pipelines run as the owner, and only while the owner can still push (PushCode).
/// <see cref="RunDueAsync"/> is the scheduler's entry point (<see cref="CiScheduleRunner"/>), with no permission check
/// of its own beyond the owner's.
/// </summary>
public class PipelineScheduleService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, RepoStorage storage,
    IServiceScopeFactory scopes, Audit.AuditLog audit, ILogger<PipelineScheduleService> log)
{
    public const int MaxVariables = 50;

    public async Task<List<PipelineSchedule>> ListAsync(Repository repo)
    {
        if (!await access.CanAsync(repo.ProjectId, Permission.ManageRepositories)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.PipelineSchedules.AsNoTracking().Include(s => s.Owner)
            .Where(s => s.RepositoryId == repo.Id).OrderBy(s => s.Description).ThenBy(s => s.Id).ToListAsync();
    }

    /// <summary>Creates (<paramref name="id"/> null) or updates a schedule; the acting user becomes its owner.</summary>
    public async Task<PipelineSchedule> SaveAsync(Repository repo, int? id, string description, string cron, string branch,
        IReadOnlyDictionary<string, string>? variables, bool active)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        var me = await access.ResolveUserIdAsync() ?? throw new ForbiddenException("Sign in to manage schedules.");
        description = description.Trim();
        branch = branch.Trim();
        if (description.Length is 0 or > 200) throw new InvalidOperationException("Enter a description (up to 200 characters).");
        var parsed = CiCron.TryParse(cron, out var cronError) ?? throw new InvalidOperationException(cronError);
        if (branch.Length == 0) throw new InvalidOperationException("Choose a branch.");
        var path = storage.PathFor(repo.Project.Key, repo.Slug);
        if (!await Task.Run(() => GitOps.BranchExists(path, branch))) throw new InvalidOperationException($"Branch '{branch}' does not exist.");
        var vars = ValidateVariables(variables);

        await using var db = await dbFactory.CreateDbContextAsync();
        PipelineSchedule s;
        if (id == null)
        {
            s = new PipelineSchedule { RepositoryId = repo.Id, CreatedAt = DateTime.UtcNow };
            db.PipelineSchedules.Add(s);
        }
        else
        {
            s = await db.PipelineSchedules.FirstOrDefaultAsync(x => x.Id == id && x.RepositoryId == repo.Id)
                ?? throw new InvalidOperationException("That schedule no longer exists.");
        }
        s.Description = description;
        s.Cron = parsed.Expression;
        s.Branch = branch;
        s.VariablesJson = CiJobExtras.SerializeVariables(vars);
        s.Active = active;
        s.OwnerId = me;
        s.NextRunAt = active ? parsed.Next(DateTime.UtcNow) : null;
        s.LastError = null;
        await db.SaveChangesAsync();
        await audit.RecordAsync(id == null ? "pipeline_schedule.created" : "pipeline_schedule.updated", "pipeline_schedule", s.Id, repo.ProjectId,
            new { repositoryId = repo.Id, description, cron = s.Cron, branch, active, variables = vars.Keys.ToList() });
        return s;
    }

    public async Task DeleteAsync(Repository repo, int id)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        await using var db = await dbFactory.CreateDbContextAsync();
        var s = await db.PipelineSchedules.FirstOrDefaultAsync(x => x.Id == id && x.RepositoryId == repo.Id);
        if (s == null) return;
        db.PipelineSchedules.Remove(s);
        await db.SaveChangesAsync();
        await audit.RecordAsync("pipeline_schedule.deleted", "pipeline_schedule", id, repo.ProjectId, new { repositoryId = repo.Id, s.Description });
    }

    /// <summary>"Run now": starts the schedule's pipeline at once, as the acting user. Needs ManageRepositories.</summary>
    public async Task<int> RunNowAsync(Repository repo, int id)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        var me = await access.ResolveUserIdAsync() ?? throw new ForbiddenException("Sign in to run schedules.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var s = await db.PipelineSchedules.FirstOrDefaultAsync(x => x.Id == id && x.RepositoryId == repo.Id)
            ?? throw new InvalidOperationException("That schedule no longer exists.");
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ProjectAccess>().ActAs(me);
        var pipelineId = await scope.ServiceProvider.GetRequiredService<PipelineService>().RunScheduledAsync(repo, s, me);
        s.LastPipelineId = pipelineId;
        s.LastRunAt = DateTime.UtcNow;
        s.LastError = null;
        await db.SaveChangesAsync();
        return pipelineId;
    }

    /// <summary>
    /// Starts every active schedule due at <paramref name="nowUtc"/> as its owner, and moves it to its next time. A
    /// schedule whose owner can no longer push records an error instead. Returns the number of pipelines started.
    /// </summary>
    public async Task<int> RunDueAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        List<PipelineSchedule> due;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            due = await db.PipelineSchedules.AsNoTracking().Include(s => s.Repository).ThenInclude(r => r.Project)
                .Where(s => s.Active && s.NextRunAt != null && s.NextRunAt <= nowUtc).OrderBy(s => s.NextRunAt).Take(100).ToListAsync(ct);

        var started = 0;
        foreach (var s in due)
        {
            var next = CiCron.TryParse(s.Cron, out _)?.Next(nowUtc);
            // Claim the run (another server instance, or a slow previous tick, may be looking at the same row).
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                var claimed = await db.PipelineSchedules.Where(x => x.Id == s.Id && x.NextRunAt == s.NextRunAt)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.NextRunAt, next).SetProperty(x => x.LastRunAt, nowUtc), ct);
                if (claimed == 0) continue;
            }

            string? error = null;
            int? pipelineId = null;
            using (var scope = scopes.CreateScope())
            {
                var scopedAccess = scope.ServiceProvider.GetRequiredService<ProjectAccess>();
                if (s.OwnerId is not { } owner || !await scopedAccess.CanAsync(s.Repository.ProjectId, Permission.PushCode, owner))
                    error = "The schedule's owner can no longer push to this repository. A Maintainer must save the schedule to take it over.";
                else if (s.Repository.IsArchived)
                    error = "The repository is archived.";
                else
                {
                    scopedAccess.ActAs(owner);
                    try
                    {
                        pipelineId = await scope.ServiceProvider.GetRequiredService<PipelineService>().RunScheduledAsync(s.Repository, s, owner);
                        started++;
                    }
                    catch (InvalidOperationException ex) { error = ex.Message; }
                }
            }
            if (error != null) log.LogWarning("Schedule {Id} ({Desc}) did not start a pipeline: {Error}", s.Id, s.Description, error);

            await using (var db = await dbFactory.CreateDbContextAsync(ct))
                await db.PipelineSchedules.Where(x => x.Id == s.Id).ExecuteUpdateAsync(u => u
                    .SetProperty(x => x.LastError, error == null ? null : error.Length > 500 ? error[..500] : error)
                    .SetProperty(x => x.LastPipelineId, x => pipelineId ?? x.LastPipelineId), ct);
        }
        return started;
    }

    private static Dictionary<string, string> ValidateVariables(IReadOnlyDictionary<string, string>? variables)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (variables == null) return result;
        if (variables.Count > MaxVariables) throw new InvalidOperationException($"Too many variables (max {MaxVariables}).");
        foreach (var (rawName, value) in variables)
        {
            var name = rawName.Trim();
            if (name.Length == 0 && string.IsNullOrEmpty(value)) continue;
            if (CiVariableService.ValidateName(name) is { } bad) throw new InvalidOperationException($"Variable '{name}': {bad}");
            if (value.Length > CiVariableService.MaxValueLength) throw new InvalidOperationException($"Variable '{name}' is too long.");
            if (value.Contains('\n') || value.Contains('\r')) throw new InvalidOperationException($"Variable '{name}' must be a single line.");
            if (!result.TryAdd(name, value)) throw new InvalidOperationException($"Variable '{name}' appears twice.");
        }
        return result;
    }
}

/// <summary>Hosted scheduler: every 30 seconds starts the pipeline schedules that are due (<see cref="PipelineScheduleService.RunDueAsync"/>).</summary>
public class CiScheduleRunner(IServiceScopeFactory scopes, IOptionsMonitor<CiOptions> options, ILogger<CiScheduleRunner> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            do
            {
                if (!options.CurrentValue.Enabled || !options.CurrentValue.SchedulesEnabled) continue;
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<PipelineScheduleService>().RunDueAsync(DateTime.UtcNow, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Running pipeline schedules failed"); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

/// <summary>Hourly: deletes expired artifacts and orphaned artifact and cache files.</summary>
public class CiCleanupService(CiArtifactStore artifacts, CiCacheStore caches, ILogger<CiCleanupService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            do
            {
                try
                {
                    await artifacts.CleanupAsync(stoppingToken);
                    await caches.CleanupAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "CI artifact cleanup failed"); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
