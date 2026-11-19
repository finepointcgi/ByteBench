using ByteBench.Data;
using ByteBench.Services.Ci;
using ByteBench.Services.Git;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Reports;

/// <summary>One production deployment. <see cref="LeadTimeHours"/> is the median over the commits it shipped (null for an environment's first deployment).</summary>
public sealed record DoraDeployment(int Id, string Repository, string Environment, string CommitSha, DateTime FinishedAt, int Commits, double? LeadTimeHours,
    bool Failed, string? FailureSignal, DateTime? SignalAt, double? RestoreHours);

public sealed record DoraMetricValue(double? Value, string? Band);

public sealed record DoraFrequencyPoint(DateTime Period, int Deployments);

/// <summary>The four DORA metrics for one project's production environment. See <see cref="DoraReportService"/> for the definitions.</summary>
public sealed record DoraReport(string? ProductionEnvironment, bool Configured, string? Notice, int FailureWindowHours,
    int Deployments, double PerDay, double PerWeek, string? FrequencyBand,
    Percentiles? LeadTimeHours, string? LeadTimeBand, int CommitsMeasured,
    int Failures, double? FailureRate, string? FailureBand,
    int Restored, int Unrestored, double? RestoreMedianHours, string? RestoreBand,
    IReadOnlyList<DoraFrequencyPoint> Frequency, IReadOnlyList<DoraDeployment> Rows,
    IReadOnlyDictionary<string, IReadOnlyList<DoraBand>> Bands);

/// <summary>
/// DORA metrics from deployments. The production environment is the project's <see cref="Project.ProductionEnvironment"/>
/// (matched by name, case-insensitively, in every repository of the project; unset: an environment called "production" or
/// "prod"). Definitions:
/// <list type="bullet">
/// <item><b>Deployment frequency</b>: successful (<c>Passed</c>) start deployments to production that finished in the range,
/// excluding rollbacks, per day and per week.</item>
/// <item><b>Lead time for changes</b>: for each of those deployments, every commit it shipped (those between the environment's
/// previous successful deployment and this one) waited from its commit date until the deployment finished. Reported as
/// percentiles over all commits; an environment's first deployment has no baseline and is skipped; at most
/// <see cref="MaxLeadTimeDeployments"/> recent deployments are measured.</item>
/// <item><b>Change failure rate</b>: share of those deployments that were <i>followed within the failure window</i>
/// (<see cref="Project.DoraFailureWindowHours"/>, default 24 h) by a failure signal: a rollback to production, a failed
/// production deployment, or an issue labelled "hotfix" created in the project. A signal is attributed to the latest
/// successful production deployment before it (same environment for deployments; any production environment for hotfixes).</item>
/// <item><b>Time to restore</b>: for each failed deployment, from the moment it finished until the first successful production
/// deployment (the rollback or the fix) that finished at or after the first failure signal. Failures without such a
/// deployment yet are counted as not restored.</item>
/// </list>
/// Bands are the commonly published Elite/High/Medium/Low ranges (<see cref="ReportMath.Bands"/>).
/// </summary>
public class DoraReportService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, RepoStorage storage, Audit.AuditLog audit)
{
    public const int MaxLeadTimeDeployments = 200;
    public const int MaxCommitsPerDeployment = 100;
    public const string HotfixLabel = "hotfix";

    private sealed record Dep(int Id, int EnvironmentId, string CommitSha, PipelineStatus Status, bool IsRollback, DateTime CreatedAt, DateTime FinishedAt);

    private sealed record Signal(DateTime At, string Kind, int? EnvironmentId);

    /// <summary>Names of every environment in the project's repositories (for the setting's picker).</summary>
    public async Task<List<string>> EnvironmentNamesAsync(int projectId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.CiEnvironments.AsNoTracking().Where(e => db.Repositories.Any(r => r.Id == e.RepositoryId && r.ProjectId == projectId))
            .Select(e => e.Name).Distinct().OrderBy(n => n).ToListAsync();
    }

    /// <summary>Sets which environment is production and the failure window (hours, 1–720). Needs ManageProjectSettings.</summary>
    public async Task SetSettingsAsync(int projectId, string? environment, int? failureWindowHours)
    {
        await access.RequireAsync(projectId, Permission.ManageProjectSettings);
        environment = string.IsNullOrWhiteSpace(environment) ? null : environment.Trim();
        if (environment != null && !CiFile.IsValidEnvironmentName(environment)) throw new InvalidOperationException("That is not a valid environment name.");
        if (failureWindowHours is < 1 or > 720) throw new InvalidOperationException("The failure window must be between 1 and 720 hours.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId) ?? throw new InvalidOperationException("Project not found.");
        project.ProductionEnvironment = environment;
        if (failureWindowHours is { } h) project.DoraFailureWindowHours = h;
        await db.SaveChangesAsync();
        await audit.RecordAsync("project.dora_settings_changed", "project", projectId, projectId,
            new { productionEnvironment = environment, failureWindowHours = project.DoraFailureWindowHours });
    }

    public async Task<DoraReport> ReportAsync(int projectId, ReportFilter filter)
    {
        await access.RequireAsync(projectId, Permission.ViewProject);
        var (from, to, toEx, now) = filter.Resolve();
        await using var db = await dbFactory.CreateDbContextAsync();
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId);
        var window = TimeSpan.FromHours(Math.Clamp(project.DoraFailureWindowHours, 1, 720));
        var configured = !string.IsNullOrWhiteSpace(project.ProductionEnvironment);

        var repos = await db.Repositories.AsNoTracking().Where(r => r.ProjectId == projectId).ToListAsync();
        var repoIds = repos.Select(r => r.Id).ToList();
        var allEnvs = await db.CiEnvironments.AsNoTracking().Where(e => repoIds.Contains(e.RepositoryId)).ToListAsync();
        var name = configured ? project.ProductionEnvironment
            : allEnvs.Select(e => e.Name).FirstOrDefault(n => n.Equals("production", StringComparison.OrdinalIgnoreCase))
              ?? allEnvs.Select(e => e.Name).FirstOrDefault(n => n.Equals("prod", StringComparison.OrdinalIgnoreCase));
        var envs = name == null ? [] : allEnvs.Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        var notice = name == null ? "No production environment is set. Choose one below (or name an environment \"production\")."
            : envs.Count == 0 ? $"No repository of this project has an environment named \"{name}\"." : null;

        var days = Math.Max(1, (to - from).TotalDays + 1);
        if (envs.Count == 0) return Empty(project, name, configured, notice, window, from, to, days);

        var envIds = envs.Select(e => e.Id).ToList();
        var deps = (await db.Deployments.AsNoTracking()
            .Where(d => envIds.Contains(d.EnvironmentId) && d.Action == DeploymentAction.Start && (d.Status == PipelineStatus.Passed || d.Status == PipelineStatus.Failed))
            .Select(d => new { d.Id, d.EnvironmentId, d.CommitSha, d.Status, d.IsRollback, d.CreatedAt, d.FinishedAt }).ToListAsync())
            .Select(d => new Dep(d.Id, d.EnvironmentId, d.CommitSha, d.Status, d.IsRollback, d.CreatedAt, d.FinishedAt ?? d.CreatedAt))
            .OrderBy(d => d.FinishedAt).ThenBy(d => d.Id).ToList();
        var hotfixes = await db.Issues.AsNoTracking()
            .Where(i => i.ProjectId == projectId && i.CreatedAt >= from && i.Labels.Any(l => l.Name.ToLower() == HotfixLabel))
            .Select(i => i.CreatedAt).ToListAsync();

        var passed = deps.Where(d => d.Status == PipelineStatus.Passed).ToList();
        var good = passed.Where(d => !d.IsRollback).ToList(); // the changes that count as deployments
        var inRange = good.Where(d => d.FinishedAt >= from && d.FinishedAt < toEx).ToList();

        // Failure signals, then attribution to the latest good deployment before each.
        var signals = deps.Where(d => d.IsRollback || d.Status == PipelineStatus.Failed)
            .Select(d => new Signal(d.IsRollback ? d.CreatedAt : d.FinishedAt, d.IsRollback ? "rollback" : "failed deployment", d.EnvironmentId))
            .Concat(hotfixes.Select(h => new Signal(h, "hotfix issue", null))).ToList();
        var failedBy = new Dictionary<int, Signal>(); // deployment id → earliest signal
        foreach (var s in signals.OrderBy(s => s.At))
        {
            var target = good.LastOrDefault(d => d.FinishedAt <= s.At && (s.EnvironmentId == null || d.EnvironmentId == s.EnvironmentId));
            if (target == null || s.At - target.FinishedAt > window) continue;
            failedBy.TryAdd(target.Id, s);
        }

        // Lead time: commits shipped by the most recent deployments in range.
        var repoOfEnv = envs.ToDictionary(e => e.Id, e => repos.First(r => r.Id == e.RepositoryId));
        var envName = envs.ToDictionary(e => e.Id, e => e.Name);
        var leadByDep = new Dictionary<int, List<double>>();
        var toMeasure = inRange.OrderByDescending(d => d.FinishedAt).Take(MaxLeadTimeDeployments).ToList();
        await Task.Run(() =>
        {
            foreach (var d in toMeasure)
            {
                var prev = passed.LastOrDefault(p => p.EnvironmentId == d.EnvironmentId && p.Id < d.Id);
                if (prev == null) continue;
                var repo = repoOfEnv[d.EnvironmentId];
                try
                {
                    var (commits, _) = GitOps.CommitsBetween(storage.PathFor(project.Key, repo.Slug), prev.CommitSha, d.CommitSha, MaxCommitsPerDeployment);
                    leadByDep[d.Id] = commits.Select(c => ReportMath.Hours(d.FinishedAt - c.When.UtcDateTime)).ToList();
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* repository missing on disk: no lead time for this deployment */ }
            }
        });

        var rows = new List<DoraDeployment>();
        var restoreHours = new List<double>();
        var failures = 0;
        var unrestored = 0;
        foreach (var d in inRange)
        {
            var failed = failedBy.TryGetValue(d.Id, out var sig);
            double? restore = null;
            if (failed)
            {
                failures++;
                var fix = passed.FirstOrDefault(p => p.Id != d.Id && p.FinishedAt >= sig!.At && p.FinishedAt > d.FinishedAt
                                                      && (sig.EnvironmentId == null || p.EnvironmentId == sig.EnvironmentId));
                if (fix != null) { restore = Math.Round(ReportMath.Hours(fix.FinishedAt - d.FinishedAt), 2); restoreHours.Add(restore.Value); }
                else unrestored++;
            }
            var leads = leadByDep.GetValueOrDefault(d.Id);
            rows.Add(new DoraDeployment(d.Id, repoOfEnv[d.EnvironmentId].Slug, envName[d.EnvironmentId], d.CommitSha, d.FinishedAt, leads?.Count ?? 0,
                leads is { Count: > 0 } ? Math.Round(ReportMath.Percentile(leads, 50), 2) : null, failed, failed ? sig!.Kind : null, failed ? sig!.At : null, restore));
        }
        rows.Reverse(); // newest first

        var allLeads = leadByDep.Values.SelectMany(x => x).ToList();
        var lead = ReportMath.Summarize(allLeads);
        var perDay = Math.Round(inRange.Count / days, 3);
        double? rate = inRange.Count == 0 ? null : Math.Round((double)failures / inRange.Count, 4);
        double? restoreMedian = restoreHours.Count == 0 ? null : Math.Round(ReportMath.Percentile(restoreHours, 50), 2);

        var freq = ReportMath.Periods(from, to, filter.Monthly).Select(p =>
        {
            var next = ReportMath.NextPeriod(p, filter.Monthly);
            return new DoraFrequencyPoint(p, inRange.Count(d => d.FinishedAt >= p && d.FinishedAt < next));
        }).ToList();

        return new DoraReport(name, configured, notice, (int)window.TotalHours, inRange.Count, perDay, Math.Round(perDay * 7, 2),
            inRange.Count == 0 ? null : ReportMath.FrequencyBand(perDay),
            lead, lead == null ? null : ReportMath.LeadTimeBand(lead.P50), allLeads.Count,
            failures, rate, rate == null ? null : ReportMath.FailureRateBand(rate.Value),
            restoreHours.Count, unrestored, restoreMedian, restoreMedian == null ? null : ReportMath.RestoreBand(restoreMedian.Value),
            freq, rows, ReportMath.Bands);
    }

    private static DoraReport Empty(Project p, string? name, bool configured, string? notice, TimeSpan window, DateTime from, DateTime to, double days) =>
        new(name, configured, notice, (int)window.TotalHours, 0, 0, 0, null, null, null, 0, 0, null, null, 0, 0, null, null,
            [], [], ReportMath.Bands);
}
