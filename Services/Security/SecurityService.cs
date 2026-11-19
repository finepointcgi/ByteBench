using GitBench.Data;
using GitBench.Services.Git;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Security;

/// <summary>Filters for <see cref="SecurityService.ListAsync"/>. <see cref="State"/>: open (default), dismissed or all.</summary>
public sealed record SecurityFindingFilter(string? Branch = null, SecuritySeverity? MinSeverity = null, SecurityCategory? Category = null,
    string State = "open", string? Text = null);

/// <summary>A finding with where else it has been seen.</summary>
public sealed record SecurityFindingDetail(SecurityFindingView View, SecurityScan? FirstSeen, SecurityScan? LastSeen, int ScanCount);

/// <summary>
/// Security findings for the UI and the API (scoped). Reads need ViewProject; triage needs PushCode (Developer+), as
/// developers triage in Night Owl too; settings and importing a triage file need ManageRepositories (Maintainer+).
/// Decisions are keyed by fingerprint per repository, so they stick across scans and branches.
/// </summary>
public class SecurityService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, RepositoryService repos, Audit.AuditLog audit)
{
    public async Task<bool> CanViewAsync(Repository repo) => await access.CanAsync(repo.ProjectId, Permission.ViewProject);
    public async Task<bool> CanTriageAsync(Repository repo) => await access.CanAsync(repo.ProjectId, Permission.PushCode);

    /// <summary>The latest scan of <paramref name="branch"/> (default: the repository's default branch), or null.</summary>
    public async Task<SecuritySnapshot?> SnapshotAsync(Repository repo, string? branch = null, CancellationToken ct = default)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ViewProject);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await SecuritySnapshots.ForBranchAsync(db, repo.Id, branch ?? repo.DefaultBranch, ct: ct);
    }

    /// <summary>Branches with at least one scan, the default branch first.</summary>
    public async Task<List<string>> BranchesAsync(Repository repo)
    {
        if (!await CanViewAsync(repo)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var refs = await db.SecurityScans.Where(s => s.RepositoryId == repo.Id).Select(s => s.Ref).Distinct().ToListAsync();
        return refs.OrderBy(r => r == repo.DefaultBranch ? 0 : 1).ThenBy(r => r, StringComparer.Ordinal).ToList();
    }

    /// <summary>Findings of the branch's latest scan, filtered.</summary>
    public async Task<(SecuritySnapshot? Snapshot, List<SecurityFindingView> Findings)> ListAsync(Repository repo, SecurityFindingFilter filter, CancellationToken ct = default)
    {
        var snap = await SnapshotAsync(repo, filter.Branch, ct);
        if (snap == null) return (null, []);
        return (snap, Apply(snap.Findings, filter).ToList());
    }

    public static IEnumerable<SecurityFindingView> Apply(IEnumerable<SecurityFindingView> findings, SecurityFindingFilter filter)
    {
        var q = findings;
        q = (filter.State ?? "open").ToLowerInvariant() switch
        {
            "all" => q,
            "dismissed" => q.Where(f => !f.IsOpen),
            _ => q.Where(f => f.IsOpen),
        };
        if (filter.MinSeverity is { } min) q = q.Where(f => f.Severity >= min);
        if (filter.Category is { } c) q = q.Where(f => f.Finding.Category == c);
        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            var t = filter.Text.Trim();
            q = q.Where(f => f.Finding.Title.Contains(t, StringComparison.OrdinalIgnoreCase) || f.Finding.RuleId.Contains(t, StringComparison.OrdinalIgnoreCase)
                             || (f.Finding.PackageName?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
                             || (f.Finding.FilePath?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
                             || (f.Finding.Aliases?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        return q;
    }

    /// <summary>Open findings by severity per pipeline on <paramref name="branch"/>, oldest first (at most <paramref name="take"/>).</summary>
    public async Task<List<SecurityTrendPoint>> TrendAsync(Repository repo, string? branch = null, int take = 30)
    {
        if (!await CanViewAsync(repo)) return [];
        branch ??= repo.DefaultBranch;
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.SecurityScans.AsNoTracking().Where(s => s.RepositoryId == repo.Id && s.Ref == branch)
            .GroupBy(s => s.PipelineId)
            .Select(g => new
            {
                PipelineId = g.Key, At = g.Max(s => s.CreatedAt), Sha = g.Max(s => s.CommitSha),
                Critical = g.Sum(s => s.Critical), High = g.Sum(s => s.High), Medium = g.Sum(s => s.Medium),
                Low = g.Sum(s => s.Low), Other = g.Sum(s => s.Other), Dismissed = g.Sum(s => s.Dismissed),
            })
            .OrderByDescending(x => x.PipelineId).Take(Math.Clamp(take, 1, 200)).ToListAsync();
        return rows.OrderBy(r => r.PipelineId)
            .Select(r => new SecurityTrendPoint(r.PipelineId, r.At, r.Sha ?? "", r.Critical, r.High, r.Medium, r.Low, r.Other, r.Dismissed)).ToList();
    }

    /// <summary>The finding in the branch's latest scan (else its most recent occurrence anywhere), with history. Null if unknown.</summary>
    public async Task<SecurityFindingDetail?> GetAsync(Repository repo, string fingerprint, string? branch = null)
    {
        if (!await CanViewAsync(repo)) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        var snap = await SecuritySnapshots.ForBranchAsync(db, repo.Id, branch ?? repo.DefaultBranch);
        var view = snap?.Findings.FirstOrDefault(f => f.Fingerprint == fingerprint);
        var occurrences = db.SecurityFindings.AsNoTracking().Where(f => f.RepositoryId == repo.Id && f.Fingerprint == fingerprint);
        if (view == null)
        {
            var latest = await occurrences.OrderByDescending(f => f.ScanId).FirstOrDefaultAsync();
            if (latest == null) return null;
            var t = await db.SecurityTriages.AsNoTracking().Include(x => x.User).FirstOrDefaultAsync(x => x.RepositoryId == repo.Id && x.Fingerprint == fingerprint);
            view = SecuritySnapshots.View(latest, t, DateTime.UtcNow);
        }
        var first = await occurrences.OrderBy(f => f.ScanId).Select(f => f.Scan).FirstOrDefaultAsync();
        var last = await occurrences.OrderByDescending(f => f.ScanId).Select(f => f.Scan).FirstOrDefaultAsync();
        var count = await occurrences.CountAsync();
        return new SecurityFindingDetail(view, first, last, count);
    }

    /// <summary>Scans of the repository, newest first.</summary>
    public async Task<List<SecurityScan>> ScansAsync(Repository repo, string? branch = null, int take = 50)
    {
        if (!await CanViewAsync(repo)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.SecurityScans.AsNoTracking().Where(s => s.RepositoryId == repo.Id);
        if (!string.IsNullOrEmpty(branch)) q = q.Where(s => s.Ref == branch);
        return await q.OrderByDescending(s => s.Id).Take(Math.Clamp(take, 1, 200)).ToListAsync();
    }

    // ------------------------------------------------------------------ triage

    /// <summary>
    /// Records a decision for <paramref name="fingerprint"/>: false positive or accepted risk (a reason is required,
    /// <paramref name="expires"/> optional), confirmed or fixed. <see cref="SecurityTriageState.New"/> reopens (removes the decision).
    /// </summary>
    public async Task<SecurityTriage?> TriageAsync(Repository repo, string fingerprint, SecurityTriageState state, string? reason,
        DateTime? expires = null, int? actorId = null)
    {
        await access.RequireAsync(repo.ProjectId, Permission.PushCode, actorId);
        var userId = await access.ResolveUserIdAsync(actorId);
        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (reason is { Length: > 2000 }) throw new InvalidOperationException("The reason is too long (max 2000 characters).");
        if (state is SecurityTriageState.FalsePositive or SecurityTriageState.AcceptedRisk && reason == null)
            throw new InvalidOperationException("Give a reason for dismissing the finding.");
        if (expires is { } e && e.ToUniversalTime() <= DateTime.UtcNow) throw new InvalidOperationException("The expiry date must be in the future.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var finding = await db.SecurityFindings.AsNoTracking().Where(f => f.RepositoryId == repo.Id && f.Fingerprint == fingerprint)
            .OrderByDescending(f => f.ScanId).FirstOrDefaultAsync() ?? throw new InvalidOperationException("Finding not found.");
        var row = await db.SecurityTriages.FirstOrDefaultAsync(t => t.RepositoryId == repo.Id && t.Fingerprint == fingerprint);
        var previous = row?.State;
        if (state == SecurityTriageState.New)
        {
            if (row != null) db.SecurityTriages.Remove(row);
        }
        else
        {
            row ??= db.SecurityTriages.Add(new SecurityTriage { RepositoryId = repo.Id, Fingerprint = fingerprint }).Entity;
            row.State = state;
            row.Reason = reason;
            row.ExpiresAt = expires?.ToUniversalTime();
            row.Title = finding.Title;
            row.UserId = userId;
            row.By = null;
            row.At = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        await audit.RecordAsync("security.finding_triaged", "repository", repo.Id, repo.ProjectId, new
        {
            repository = repo.Slug, fingerprint, ruleId = finding.RuleId, title = finding.Title,
            from = previous is { } p ? SecurityReportParser.TriageStateName(p) : "new",
            to = SecurityReportParser.TriageStateName(state), reason, expires,
        }, actorId);
        return state == SecurityTriageState.New ? null : row;
    }

    /// <summary>The repository's decisions as Night Owl's <c>.nightowl/triage.json</c>.</summary>
    public async Task<string> ExportTriageAsync(Repository repo)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ViewProject);
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.SecurityTriages.AsNoTracking().Include(t => t.User).Where(t => t.RepositoryId == repo.Id).ToListAsync();
        return NightOwlTriageFile.Write(rows);
    }

    /// <summary>
    /// Imports a Night Owl triage file: each decision replaces GitBench's for that fingerprint ("new" removes it).
    /// Returns how many decisions were applied.
    /// </summary>
    public async Task<int> ImportTriageAsync(Repository repo, string json, int? actorId = null, string source = "upload")
    {
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories, actorId);
        if (json.Length > 5 * 1024 * 1024) throw new InvalidOperationException("The triage file is too large (max 5 MB).");
        List<(NightOwlTriageFile.Decision Decision, SecurityTriageState State)> decisions;
        try { decisions = NightOwlTriageFile.Read(json); }
        catch (InvalidDataException ex) { throw new InvalidOperationException(ex.Message); }

        await using var db = await dbFactory.CreateDbContextAsync();
        var existing = await db.SecurityTriages.Where(t => t.RepositoryId == repo.Id).ToDictionaryAsync(t => t.Fingerprint, StringComparer.Ordinal);
        var userNames = decisions.Select(d => d.Decision.By).Where(b => !string.IsNullOrEmpty(b)).Distinct().ToList();
        var users = await db.Users.Where(u => userNames.Contains(u.UserName!)).ToDictionaryAsync(u => u.UserName!, u => u.Id);
        var applied = 0;
        foreach (var (d, state) in decisions)
        {
            existing.TryGetValue(d.FindingId, out var row);
            if (state == SecurityTriageState.New)
            {
                if (row != null) { db.SecurityTriages.Remove(row); existing.Remove(d.FindingId); applied++; }
                continue;
            }
            if (row == null)
            {
                row = db.SecurityTriages.Add(new SecurityTriage { RepositoryId = repo.Id, Fingerprint = d.FindingId }).Entity;
                existing[d.FindingId] = row;
            }
            row.State = state;
            row.Reason = d.Reason is { Length: > 2000 } r ? r[..2000] : d.Reason;
            row.ExpiresAt = d.Expires?.UtcDateTime;
            row.Title = d.Title is { Length: > 500 } t ? t[..500] : d.Title;
            row.At = d.At.UtcDateTime;
            row.UserId = d.By != null && users.TryGetValue(d.By, out var uid) ? uid : null;
            row.By = row.UserId == null ? (d.By is { Length: > 200 } b ? b[..200] : d.By) : null;
            applied++;
        }
        await db.SaveChangesAsync();
        await audit.RecordAsync("security.triage_imported", "repository", repo.Id, repo.ProjectId, new { repository = repo.Slug, decisions = applied, source }, actorId);
        return applied;
    }

    /// <summary>Imports <c>.nightowl/triage.json</c> from the head of the repository's default branch.</summary>
    public async Task<int> ImportTriageFromRepositoryAsync(Repository repo, int? actorId = null)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories, actorId);
        var blob = await repos.Git(repo, path => GitOps.BranchExists(path, repo.DefaultBranch) ? GitOps.GetBlob(path, repo.DefaultBranch, NightOwlTriageFile.RepositoryPath) : null);
        if (blob?.Text == null) throw new InvalidOperationException($"There is no {NightOwlTriageFile.RepositoryPath} on {repo.DefaultBranch}.");
        return await ImportTriageAsync(repo, blob.Text, actorId, $"{repo.DefaultBranch}:{NightOwlTriageFile.RepositoryPath}");
    }

    // ------------------------------------------------------------------ settings

    public async Task<RepositorySecuritySettings> GetSettingsAsync(int repositoryId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.RepositorySecuritySettings.AsNoTracking().FirstOrDefaultAsync(s => s.RepositoryId == repositoryId)
               ?? new RepositorySecuritySettings { RepositoryId = repositoryId };
    }

    public async Task SaveSettingsAsync(Repository repo, bool mergeCheckEnabled, SecuritySeverity mergeCheckSeverity, SecuritySeverity notifySeverity, int? actorId = null)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories, actorId);
        if (mergeCheckSeverity is SecuritySeverity.Unknown || notifySeverity is SecuritySeverity.Unknown)
            throw new InvalidOperationException("Choose a severity.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.RepositorySecuritySettings.FirstOrDefaultAsync(s => s.RepositoryId == repo.Id);
        if (row == null) row = db.RepositorySecuritySettings.Add(new RepositorySecuritySettings { RepositoryId = repo.Id }).Entity;
        row.MergeCheckEnabled = mergeCheckEnabled;
        row.MergeCheckSeverity = mergeCheckSeverity;
        row.NotifySeverity = notifySeverity;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync("repository.security_settings_changed", "repository", repo.Id, repo.ProjectId, new
        {
            repository = repo.Slug, mergeCheck = mergeCheckEnabled, mergeCheckSeverity = mergeCheckSeverity.ToString().ToLowerInvariant(),
            notifySeverity = notifySeverity.ToString().ToLowerInvariant(),
        }, actorId);
    }

    // ------------------------------------------------------------------ pull requests

    /// <summary>New and fixed findings of a pull request (its head's latest scan against the target branch's).</summary>
    public async Task<SecurityPullRequestDiff?> PullRequestAsync(PullRequest pr, CancellationToken ct = default)
    {
        if (!await access.CanAsync(pr.Repository.ProjectId, Permission.ViewProject)) return null;
        return await PullRequestDiffAsync(dbFactory, repos, pr, ct);
    }

    /// <summary>The diff for <paramref name="pr"/> without permission checks (used by the merge check). Needs pr.Repository.Project.</summary>
    internal static async Task<SecurityPullRequestDiff> PullRequestDiffAsync(IDbContextFactory<AppDbContext> dbFactory, RepositoryService repos,
        PullRequest pr, CancellationToken ct)
    {
        var head = pr.State == PullRequestState.Open || pr.SourceCommitSha == null
            ? await repos.Git(pr.Repository, path => GitOps.BranchExists(path, pr.SourceRef) ? GitOps.ResolveSha(path, pr.SourceRef) : null)
            : pr.SourceCommitSha;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var source = head == null ? null : await SecuritySnapshots.ForCommitAsync(db, pr.RepositoryId, head, ct);
        var target = await SecuritySnapshots.ForBranchAsync(db, pr.RepositoryId, pr.TargetBranch, ct: ct);
        var pending = source == null && head != null
            && await db.Pipelines.AnyAsync(p => p.RepositoryId == pr.RepositoryId && p.CommitSha == head
                                                && (p.Status == PipelineStatus.Queued || p.Status == PipelineStatus.Running), ct);
        return SecuritySnapshots.Diff(head, source, target, pending);
    }
}
