using ByteBench.Data;
using ByteBench.Services.Ci;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Security;

/// <summary>What one ingestion did.</summary>
public sealed record SecurityIngestResult(int? ScanId, int FindingCount, int NewAlerts, string? Error)
{
    public bool Ok => Error == null;
}

/// <summary>
/// Reads the security reports a CI job published (<c>artifacts.reports</c>) into <see cref="SecurityScan"/> and
/// <see cref="SecurityFinding"/> rows (singleton, no permission checks: called by the runner). When the scan is on the
/// repository's default branch, findings at or above the repository's notify severity that the previous default-branch scan
/// didn't have raise the <c>security.new_findings</c> webhook and notify the project's Maintainers and Owners (and whoever
/// triggered the pipeline).
/// </summary>
public class SecurityIngestor(IDbContextFactory<AppDbContext> dbFactory, IServiceScopeFactory scopes, Webhooks.WebhookDispatcher webhooks,
    Api.PublicBaseUrl baseUrl, LiveEvents live, ILogger<SecurityIngestor> log)
{
    public const long MaxReportBytes = 32L * 1024 * 1024;

    /// <summary>
    /// Reads the job's report files from its workspace (Night Owl JSON preferred: a SARIF file written by the same scan holds
    /// the same findings) and ingests them. Returns notes for the job log.
    /// </summary>
    public async Task<List<string>> IngestWorkspaceAsync(int pipelineId, int jobId, string workspace, IReadOnlyDictionary<string, string> reports)
    {
        var notes = new List<string>();
        var nightOwlRead = false;
        foreach (var (type, file) in reports.OrderBy(r => r.Key == SecurityReportParser.NightOwlFormat ? 0 : 1))
        {
            if (type == SecurityReportParser.SarifFormat && nightOwlRead)
            {
                notes.Add($"[bytebench] Security report {file} (sarif): kept as an artifact; findings were read from the Night Owl JSON report.");
                continue;
            }
            var text = ReadReport(workspace, file, out var problem);
            if (text == null) { notes.Add($"[bytebench] Security report '{file}' ({type}): {problem}"); continue; }
            var r = await IngestAsync(pipelineId, jobId, type, text);
            if (r.Ok && type == SecurityReportParser.NightOwlFormat) nightOwlRead = true;
            notes.Add(r.Ok
                ? $"[bytebench] Security report {file} ({type}): {r.FindingCount} finding(s)" + (r.NewAlerts > 0 ? $", {r.NewAlerts} new at the alert threshold." : ".")
                : $"[bytebench] Security report {file} ({type}) could not be read: {r.Error}");
        }
        return notes;
    }

    /// <summary>The file's text, or null with a reason (missing, a link, outside the workspace, too large).</summary>
    internal static string? ReadReport(string workspace, string relative, out string problem)
    {
        problem = "";
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) { problem = "is outside the workspace"; return null; }
        // Never follow a symbolic link the job created (it could point anywhere on the server).
        var current = root;
        foreach (var part in full[(root.Length + 1)..].Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            var info = new FileInfo(current);
            if (info.LinkTarget != null || (info.Exists || Directory.Exists(current)) && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            { problem = "is a symbolic link; not read"; return null; }
        }
        var fi = new FileInfo(full);
        if (!fi.Exists) { problem = "was not written by the job"; return null; }
        if (fi.Length > MaxReportBytes) { problem = $"is larger than {CiArtifacts.Format(MaxReportBytes)}"; return null; }
        return File.ReadAllText(full);
    }

    /// <summary>Parses <paramref name="content"/> (format "nightowl" or "sarif") and stores it as a scan of the job.</summary>
    public async Task<SecurityIngestResult> IngestAsync(int pipelineId, int jobId, string format, string content)
    {
        ParsedReport report;
        try { report = SecurityReportParser.Parse(content, format); }
        catch (InvalidDataException ex) { return new(null, 0, 0, ex.Message); }

        Pipeline? p;
        SecurityScan scan;
        List<SecurityFindingView> alerts;
        SecuritySeverity notifyAt;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            p = await db.Pipelines.AsNoTracking().Include(x => x.Repository).ThenInclude(r => r.Project).FirstOrDefaultAsync(x => x.Id == pipelineId);
            if (p == null) return new(null, 0, 0, "The pipeline no longer exists.");

            // A retried job replaces its earlier report.
            await db.SecurityScans.Where(s => s.JobId == jobId && s.PipelineId == pipelineId && s.Format == report.Format).ExecuteDeleteAsync();

            var triage = await SecuritySnapshots.TriageForAsync(db, p.RepositoryId, report.Findings.Select(f => f.Fingerprint).ToList());
            var now = DateTime.UtcNow;
            scan = new SecurityScan
            {
                RepositoryId = p.RepositoryId, PipelineId = p.Id, JobId = jobId, Ref = p.Ref, CommitSha = p.CommitSha, Format = report.Format,
                ToolName = Trim(report.ToolName, 100), ToolVersion = Trim(report.ToolVersion, 50), CreatedAt = now,
            };
            foreach (var f in report.Findings)
            {
                var row = ToRow(f, p.RepositoryId);
                scan.Findings.Add(row);
                var view = SecuritySnapshots.View(row, triage.GetValueOrDefault(f.Fingerprint), now);
                if (!view.IsOpen) { scan.Dismissed++; continue; }
                switch (f.Severity)
                {
                    case SecuritySeverity.Critical: scan.Critical++; break;
                    case SecuritySeverity.High: scan.High++; break;
                    case SecuritySeverity.Medium: scan.Medium++; break;
                    case SecuritySeverity.Low: scan.Low++; break;
                    default: scan.Other++; break;
                }
            }
            db.SecurityScans.Add(scan);
            await db.SaveChangesAsync();

            alerts = [];
            notifyAt = (await db.RepositorySecuritySettings.AsNoTracking().FirstOrDefaultAsync(s => s.RepositoryId == p.RepositoryId))?.NotifySeverity
                       ?? SecuritySeverity.High;
            if (p.Ref == p.Repository.DefaultBranch && !p.IsForkPullRequest)
            {
                var previous = await SecuritySnapshots.ForBranchAsync(db, p.RepositoryId, p.Ref, beforePipelineId: p.Id);
                var known = previous?.Fingerprints ?? [];
                // Findings another reporting job of this pipeline already alerted on.
                var sameRun = await db.SecurityFindings.Where(f => f.Scan.PipelineId == p.Id && f.ScanId != scan.Id).Select(f => f.Fingerprint).ToListAsync();
                known.UnionWith(sameRun);
                alerts = scan.Findings.Select(f => SecuritySnapshots.View(f, triage.GetValueOrDefault(f.Fingerprint), now))
                    .Where(v => v.IsOpen && v.Severity >= notifyAt && !known.Contains(v.Fingerprint))
                    .OrderByDescending(v => v.Severity).ToList();
            }
        }

        log.LogInformation("Security report ({Format}) of pipeline {Id} job {Job}: {Count} finding(s), {Alerts} new alert(s)",
            report.Format, pipelineId, jobId, report.Findings.Count, alerts.Count);
        live.Publish(new PipelineChanged(p.Id, p.RepositoryId));
        if (alerts.Count > 0) await AlertAsync(p, alerts, notifyAt);
        return new(scan.Id, report.Findings.Count, alerts.Count, null);
    }

    private async Task AlertAsync(Pipeline p, List<SecurityFindingView> alerts, SecuritySeverity threshold)
    {
        var repo = p.Repository;
        var url = SecurityUrls.Repo(repo, branch: null);
        try
        {
            webhooks.Enqueue(repo.ProjectId, Webhooks.WebhookEventNames.SecurityNewFindings, new
            {
                repository = new { id = repo.Id, slug = repo.Slug, name = repo.Name, defaultBranch = repo.DefaultBranch },
                pipeline = new { id = p.Id, @ref = p.Ref, commitSha = p.CommitSha, url = baseUrl.Value + PipelineUrls.Detail(repo, p.Id) },
                threshold = threshold.ToString().ToLowerInvariant(),
                findings = alerts.Take(100).Select(a => new
                {
                    fingerprint = a.Fingerprint,
                    severity = a.Severity.ToString().ToLowerInvariant(),
                    category = SecurityLabels.CategoryKey(a.Finding.Category),
                    ruleId = a.Finding.RuleId,
                    title = a.Finding.Title,
                    package = a.Finding.PackageName == null ? null : new { name = a.Finding.PackageName, version = a.Finding.PackageVersion, fixedVersions = a.Finding.FixedVersions },
                    file = a.Finding.FilePath,
                    line = a.Finding.Line,
                    url = baseUrl.Value + SecurityUrls.Finding(repo, a.Fingerprint),
                }),
                total = alerts.Count,
                url = baseUrl.Value + url,
            }, p.TriggeredById);
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not raise the security webhook for pipeline {Id}", p.Id); }

        try
        {
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<ProjectAccess>().ActAsSystem();
            List<int> recipients;
            await using (var db = await dbFactory.CreateDbContextAsync())
                recipients = await db.ProjectMembers.Where(m => m.ProjectId == repo.ProjectId && m.Role >= ProjectRole.Maintainer).Select(m => m.UserId).ToListAsync();
            if (p.TriggeredById is { } t) recipients.Add(t);
            var worst = alerts[0].Severity.ToString().ToLowerInvariant();
            var title = $"{repo.Slug}: {alerts.Count} new {worst}{(alerts.Count > 1 ? "+" : "")} security finding{(alerts.Count == 1 ? "" : "s")} on {p.Ref}";
            var excerpt = string.Join("\n", alerts.Take(5).Select(a => $"[{a.Severity}] {a.Finding.Title}")) + (alerts.Count > 5 ? $"\n… and {alerts.Count - 5} more" : "");
            await scope.ServiceProvider.GetRequiredService<NotificationService>().OnSecurityFindingsAsync(repo.ProjectId, recipients, title, url, excerpt);
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not notify about new security findings of pipeline {Id}", p.Id); }
    }

    private static SecurityFinding ToRow(ParsedFinding f, int repositoryId) => new()
    {
        RepositoryId = repositoryId,
        Fingerprint = f.Fingerprint,
        Category = f.Category,
        Severity = f.Severity,
        RuleId = Trim(f.RuleId, 200)!,
        Title = Trim(f.Title, 500)!,
        Message = f.Message is { Length: > 20_000 } m ? m[..20_000] : f.Message,
        PackageName = Trim(f.PackageName, 200),
        PackageVersion = Trim(f.PackageVersion, 100),
        Ecosystem = Trim(f.Ecosystem, 200),
        FixedVersions = Trim(f.FixedVersions.Count == 0 ? null : string.Join(", ", f.FixedVersions), 500),
        FilePath = Trim(f.FilePath, 1000),
        Line = f.Line,
        Engines = Trim(f.Engines.Count == 0 ? null : string.Join(",", f.Engines), 300),
        Aliases = Trim(f.Aliases.Count == 0 ? null : string.Join(",", f.Aliases), 2000),
        HelpUrl = Trim(f.HelpUrl, 1000),
        Context = Trim(f.Context, 20),
        ReportedTriage = f.ReportedTriage,
    };

    private static string? Trim(string? s, int max) => s == null ? null : s.Length <= max ? s : s[..(max - 1)] + "…";
}
