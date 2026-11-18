using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Security;

/// <summary>
/// Merge check "Security", when the repository turns it on (Settings → Security): the pull request's head must not
/// introduce open findings at or above the configured severity compared with the target branch's latest scan.
/// Failed when it does; Pending while a pipeline for the head is still running; Neutral (informational) when the head has
/// no security report. Enforcement comes from branch protection's "require checks to pass", like every other check.
/// </summary>
public class SecurityMergeCheck(IDbContextFactory<AppDbContext> dbFactory, RepositoryService repos) : IMergeCheck
{
    public const string Name = "Security";

    public async Task<IReadOnlyList<MergeCheckResult>> EvaluateAsync(int pullRequestId, CancellationToken ct = default)
    {
        PullRequest? pr;
        RepositorySecuritySettings? settings;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            pr = await db.PullRequests.AsNoTracking().Include(p => p.Repository).ThenInclude(r => r.Project)
                .FirstOrDefaultAsync(p => p.Id == pullRequestId, ct);
            if (pr == null) return [];
            settings = await db.RepositorySecuritySettings.AsNoTracking().FirstOrDefaultAsync(s => s.RepositoryId == pr.RepositoryId, ct);
        }
        if (settings is not { MergeCheckEnabled: true }) return [];
        return [Evaluate(await SecurityService.PullRequestDiffAsync(dbFactory, repos, pr, ct), settings.MergeCheckSeverity, pr)];
    }

    /// <summary>The check line for a diff and threshold.</summary>
    public static MergeCheckResult Evaluate(SecurityPullRequestDiff diff, SecuritySeverity threshold, PullRequest pr)
    {
        var url = Git.RepoUrls.PullRequest(pr) + "?tab=security";
        var level = threshold.ToString().ToLowerInvariant();
        if (diff.Source == null)
        {
            var sha = diff.HeadSha is { Length: >= 7 } h ? h[..7] : "the head";
            return diff.ScanPending
                ? new(Name, MergeCheckState.Pending, $"Waiting for the security scan of {sha}.", url)
                : new(Name, MergeCheckState.Neutral, $"No security report for {sha} (add `include: [nightowl]` to {Ci.CiFile.FileName}).", url);
        }
        var blocking = diff.New.Count(f => f.Severity >= threshold);
        if (blocking > 0)
            return new(Name, MergeCheckState.Failed, $"{blocking} new finding{(blocking == 1 ? "" : "s")} at or above {level}.", url);
        var below = diff.New.Count;
        return new(Name, MergeCheckState.Passed,
            $"No new findings at or above {level}" + (below > 0 ? $" ({below} new below it)" : "") + (diff.Fixed.Count > 0 ? $", {diff.Fixed.Count} fixed." : "."), url);
    }
}
