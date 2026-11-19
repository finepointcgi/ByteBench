using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Security;

/// <summary>A finding with its effective triage: ByteBench's decision if one is active, else what the report carried.</summary>
public sealed record SecurityFindingView(SecurityFinding Finding, SecurityTriageState State, SecurityTriage? Triage)
{
    /// <summary>New or confirmed: counts as open (Night Owl's quality gate considers exactly these).</summary>
    public bool IsOpen => State is SecurityTriageState.New or SecurityTriageState.Confirmed;
    public string Fingerprint => Finding.Fingerprint;
    public SecuritySeverity Severity => Finding.Severity;
}

/// <summary>
/// The findings of the newest pipeline (on a branch, or at a commit) that published a security report. Several reporting
/// jobs in one pipeline are combined, deduplicated by fingerprint.
/// </summary>
public sealed record SecuritySnapshot(int PipelineId, string Ref, string CommitSha, DateTime CreatedAt, IReadOnlyList<SecurityScan> Scans,
    IReadOnlyList<SecurityFindingView> Findings)
{
    public IEnumerable<SecurityFindingView> Open => Findings.Where(f => f.IsOpen);
    public HashSet<string> Fingerprints => Findings.Select(f => f.Fingerprint).ToHashSet(StringComparer.Ordinal);
}

/// <summary>One point of the trend chart: a pipeline's findings by severity when it was scanned.</summary>
public sealed record SecurityTrendPoint(int PipelineId, DateTime At, string CommitSha, int Critical, int High, int Medium, int Low, int Other, int Dismissed)
{
    public int Total => Critical + High + Medium + Low + Other;
}

/// <summary>
/// What a pull request changes, security-wise: open findings at the PR's head that the target branch's latest scan doesn't
/// have (<see cref="New"/>), and open findings on the target that the head no longer has (<see cref="Fixed"/>).
/// </summary>
public sealed record SecurityPullRequestDiff(string? HeadSha, SecuritySnapshot? Source, SecuritySnapshot? Target,
    IReadOnlyList<SecurityFindingView> New, IReadOnlyList<SecurityFindingView> Fixed, int Unchanged, bool ScanPending);

/// <summary>Snapshot queries shared by the UI service, the merge check and ingestion (no permission checks).</summary>
public static class SecuritySnapshots
{
    /// <summary>The newest pipeline on <paramref name="branch"/> with a report (optionally before <paramref name="beforePipelineId"/>).</summary>
    public static async Task<SecuritySnapshot?> ForBranchAsync(AppDbContext db, int repositoryId, string branch, int? beforePipelineId = null, CancellationToken ct = default)
    {
        var q = db.SecurityScans.Where(s => s.RepositoryId == repositoryId && s.Ref == branch);
        if (beforePipelineId is { } before) q = q.Where(s => s.PipelineId < before);
        var pipelineId = await q.OrderByDescending(s => s.PipelineId).Select(s => (int?)s.PipelineId).FirstOrDefaultAsync(ct);
        return pipelineId is { } id ? await ForPipelineAsync(db, repositoryId, id, ct) : null;
    }

    /// <summary>The newest pipeline at <paramref name="commitSha"/> (any branch) with a report.</summary>
    public static async Task<SecuritySnapshot?> ForCommitAsync(AppDbContext db, int repositoryId, string commitSha, CancellationToken ct = default)
    {
        var pipelineId = await db.SecurityScans.Where(s => s.RepositoryId == repositoryId && s.CommitSha == commitSha)
            .OrderByDescending(s => s.PipelineId).Select(s => (int?)s.PipelineId).FirstOrDefaultAsync(ct);
        return pipelineId is { } id ? await ForPipelineAsync(db, repositoryId, id, ct) : null;
    }

    public static async Task<SecuritySnapshot?> ForPipelineAsync(AppDbContext db, int repositoryId, int pipelineId, CancellationToken ct = default)
    {
        var scans = await db.SecurityScans.AsNoTracking().Include(s => s.Findings)
            .Where(s => s.RepositoryId == repositoryId && s.PipelineId == pipelineId).OrderBy(s => s.Id).ToListAsync(ct);
        if (scans.Count == 0) return null;
        var findings = scans.SelectMany(s => s.Findings).GroupBy(f => f.Fingerprint, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(f => f.Severity).ThenBy(f => f.Id).First()).ToList();
        var triage = await TriageForAsync(db, repositoryId, findings.Select(f => f.Fingerprint).ToList(), ct);
        var now = DateTime.UtcNow;
        var views = findings.Select(f => View(f, triage.GetValueOrDefault(f.Fingerprint), now))
            .OrderByDescending(v => v.IsOpen).ThenByDescending(v => v.Severity).ThenBy(v => v.Finding.Category).ThenBy(v => v.Finding.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var last = scans[^1];
        return new SecuritySnapshot(pipelineId, last.Ref, last.CommitSha, last.CreatedAt, scans, views);
    }

    public static async Task<Dictionary<string, SecurityTriage>> TriageForAsync(AppDbContext db, int repositoryId, IReadOnlyCollection<string> fingerprints, CancellationToken ct = default)
    {
        var result = new Dictionary<string, SecurityTriage>(StringComparer.Ordinal);
        foreach (var chunk in fingerprints.Distinct().Chunk(500))
        {
            var rows = await db.SecurityTriages.AsNoTracking().Include(t => t.User)
                .Where(t => t.RepositoryId == repositoryId && chunk.Contains(t.Fingerprint)).ToListAsync(ct);
            foreach (var r in rows) result[r.Fingerprint] = r;
        }
        return result;
    }

    /// <summary>ByteBench's decision wins while active; an expired one falls back to the report's own state (from its triage.json).</summary>
    public static SecurityFindingView View(SecurityFinding f, SecurityTriage? t, DateTime now) =>
        t != null && t.IsActive(now) ? new(f, t.State, t) : new(f, f.ReportedTriage, t);

    /// <summary>New and fixed findings between a PR head snapshot and its target branch's.</summary>
    public static SecurityPullRequestDiff Diff(string? headSha, SecuritySnapshot? source, SecuritySnapshot? target, bool pending)
    {
        if (source == null) return new(headSha, null, target, [], [], 0, pending);
        var targetIds = target?.Fingerprints ?? [];
        var sourceIds = source.Fingerprints;
        var added = source.Open.Where(f => !targetIds.Contains(f.Fingerprint)).ToList();
        var fixedList = target?.Open.Where(f => !sourceIds.Contains(f.Fingerprint)).ToList() ?? [];
        var unchanged = source.Open.Count(f => targetIds.Contains(f.Fingerprint));
        return new(headSha, source, target, added, fixedList, unchanged, pending);
    }
}
