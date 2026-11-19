using ByteBench.Data;
using ByteBench.Services.Git;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services;

/// <summary>Everything the merge box shows, and whether merging is allowed.</summary>
/// <param name="Checks">Branch protection lines (approvals, change requests) followed by every external <see cref="IMergeCheck"/> result.</param>
/// <param name="Blockers">Why the PR can't be merged under the target branch's rules; empty when it can.</param>
/// <param name="Protection">Rules matching the target branch.</param>
/// <param name="CanBypass">The actor is blocked by rules only, and every blocking rule lets admins bypass it.</param>
public sealed record MergeGateResult(
    IReadOnlyList<MergeCheckResult> Checks, IReadOnlyList<string> Blockers, EffectiveProtection Protection, bool CanBypass)
{
    public bool Blocked => Blockers.Count > 0;
    public string BlockedMessage => "This pull request can't be merged yet: " + string.Join("; ", Blockers) + ".";
}

/// <summary>
/// Collects every registered <see cref="IMergeCheck"/> plus ByteBench's own branch-protection results for a pull
/// request, and decides whether it may be merged. Protection applies to the PR's target branch:
/// <list type="bullet">
/// <item>Approvals: distinct reviewers other than the author with an "approved" verdict (on the current head when
/// "dismiss stale approvals" is on) must reach the required number.</item>
/// <item>Any "changes requested" verdict blocks.</item>
/// <item>With "require checks to pass", any external check that is Pending or Failed blocks. Neutral never blocks.</item>
/// <item>With "require all threads resolved", any unresolved inline comment thread blocks.</item>
/// <item>With "require code owner approval", each changed file with CODEOWNERS owners needs an approval from one of them
/// (files owned only by the PR's author need none).</item>
/// </list>
/// Draft pull requests can never be merged, protected branch or not, and admins can't bypass that.
/// External checks are resolved lazily per call, so a check may itself depend on PR services without a DI cycle.
/// </summary>
public class MergeGate(IDbContextFactory<AppDbContext> dbFactory, RepositoryService repos, BranchProtectionService protection,
    IServiceProvider services, ILogger<MergeGate> log, CodeOwnersService codeOwners)
{
    /// <summary>Evaluates the PR for <paramref name="actorId"/> (used only to work out admin bypass).</summary>
    public async Task<MergeGateResult> EvaluateAsync(int pullRequestId, int? actorId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pr = await db.PullRequests.AsNoTracking()
            .Include(p => p.Repository).ThenInclude(r => r.Project)
            .Include(p => p.Reviews).ThenInclude(r => r.User)
            .FirstOrDefaultAsync(p => p.Id == pullRequestId, ct) ?? throw new InvalidOperationException("Pull request not found.");

        var external = await ExternalChecksAsync(pullRequestId, ct);
        // Per-repository, optional, independent of branch protection: no unresolved AI blocker findings.
        var (aiCheck, aiBlocker) = await Ai.AiReviewMergeCheck.EvaluateAsync(db, pullRequestId, pr.RepositoryId, ct);
        var rules = await protection.RulesForAsync(pr.RepositoryId);
        var all = BranchProtectionService.ApplicableTo(rules, pr.TargetBranch, isAdmin: false);
        var checks = new List<MergeCheckResult>();
        // Not a branch rule: a draft is never mergeable (and can't be bypassed).
        List<string> draftBlockers = pr.IsDraft ? ["it is a draft (mark it ready for review first)"] : [];
        if (pr.IsDraft) checks.Add(new MergeCheckResult("Draft", MergeCheckState.Failed, "Draft pull requests can't be merged. Mark it ready for review first."));
        if (aiBlocker != null) draftBlockers.Add(aiBlocker);
        if (!all.IsProtected)
            return new MergeGateResult([.. checks, .. external, .. (aiCheck == null ? [] : new[] { aiCheck })], draftBlockers, all, false);

        var head = await repos.Git(pr.Repository, path => GitOps.ResolveSha(path, pr.SourceRef));
        var approvers = pr.Reviews.Where(r => r.State == ReviewState.Approved && r.UserId != pr.AuthorId).ToList();
        // A null CommitSha is a verdict from before heads were recorded: treated as current, never auto-dismissed.
        var freshApprovers = approvers.Where(r => r.CommitSha == null || r.CommitSha == head).ToList();
        var changes = pr.Reviews.Where(r => r.State == ReviewState.ChangesRequested).ToList();

        int CountApprovals(EffectiveProtection p) => (p.DismissStaleApprovals ? freshApprovers : approvers).Select(r => r.UserId).Distinct().Count();

        if (all.RequiredApprovals > 0)
        {
            var n = CountApprovals(all);
            var stale = all.DismissStaleApprovals ? approvers.Count - freshApprovers.Count : 0;
            checks.Add(new MergeCheckResult("Approvals",
                n >= all.RequiredApprovals ? MergeCheckState.Passed : MergeCheckState.Pending,
                $"{n} of {all.RequiredApprovals} required approval{(all.RequiredApprovals == 1 ? "" : "s")}" +
                (stale > 0 ? $" ({stale} dismissed after new commits)" : "")));
        }
        checks.Add(changes.Count > 0
            ? new MergeCheckResult("Change requests", MergeCheckState.Failed,
                "Changes requested by " + string.Join(", ", changes.Select(c => c.User.DisplayName)))
            : new MergeCheckResult("Change requests", MergeCheckState.Passed, "No changes requested"));

        var unresolved = all.RequireThreadResolution
            ? await db.PullRequestComments.CountAsync(c => c.PullRequestId == pullRequestId && c.FilePath != null && c.ParentId == null && !c.IsResolved, ct)
            : 0;
        if (all.RequireThreadResolution)
            checks.Add(new MergeCheckResult("Threads", unresolved == 0 ? MergeCheckState.Passed : MergeCheckState.Pending,
                unresolved == 0 ? "All threads resolved" : $"{unresolved} unresolved thread{(unresolved == 1 ? "" : "s")}"));

        // Code owners: changed files (per CODEOWNERS on the target) whose owners haven't approved yet.
        List<string> unownedApproval = [];
        if (all.RequireCodeOwnerApproval)
        {
            var report = await codeOwners.ForPullRequestAsync(pr);
            var approverIds = (all.DismissStaleApprovals ? freshApprovers : approvers).Select(r => r.UserId).ToHashSet();
            foreach (var (path, owners) in report.Owners.OrderBy(o => o.Key, StringComparer.Ordinal))
            {
                if (owners.All(o => o.Id == pr.AuthorId)) continue; // only the author owns it: nobody else can approve
                if (!owners.Any(o => approverIds.Contains(o.Id))) unownedApproval.Add(path);
            }
            checks.Add(new MergeCheckResult("Code owners",
                unownedApproval.Count == 0 ? MergeCheckState.Passed : MergeCheckState.Pending,
                report.FilePath == null ? "No CODEOWNERS file on the target branch"
                : unownedApproval.Count == 0 ? "Every owned file is approved by an owner"
                : $"Needs owner approval for {unownedApproval.Count} file{(unownedApproval.Count == 1 ? "" : "s")}: " + string.Join(", ", unownedApproval.Take(5)) + (unownedApproval.Count > 5 ? ", …" : "")));
        }
        checks.AddRange(external);
        if (aiCheck != null) checks.Add(aiCheck);

        List<string> BlockersFor(EffectiveProtection p)
        {
            var b = new List<string>();
            if (!p.IsProtected) return b;
            if (CountApprovals(p) is var n && n < p.RequiredApprovals)
                b.Add($"it needs {p.RequiredApprovals} approval{(p.RequiredApprovals == 1 ? "" : "s")} (has {n})");
            if (changes.Count > 0) b.Add("changes were requested");
            if (p.RequireChecksToPass)
            {
                foreach (var c in external.Where(c => c.State is MergeCheckState.Pending or MergeCheckState.Failed))
                    b.Add($"check \"{c.Name}\" is {c.State.ToString().ToLowerInvariant()}");
            }
            if (p.RequireThreadResolution && unresolved > 0)
                b.Add($"{unresolved} comment thread{(unresolved == 1 ? " is" : "s are")} unresolved");
            if (p.RequireCodeOwnerApproval && unownedApproval.Count > 0)
                b.Add($"code owners haven't approved {unownedApproval.Count} file{(unownedApproval.Count == 1 ? "" : "s")}");
            return b;
        }

        var blockers = BlockersFor(all);
        var canBypass = false;
        if (blockers.Count > 0 && all.Rules.Any(r => r.AllowAdminBypass)
            && await protection.IsBypassAdminAsync(pr.Repository.ProjectId, actorId))
        {
            canBypass = BlockersFor(BranchProtectionService.ApplicableTo(rules, pr.TargetBranch, isAdmin: true)).Count == 0;
        }
        if (draftBlockers.Count > 0) { blockers.InsertRange(0, draftBlockers); canBypass = false; }
        return new MergeGateResult(checks, blockers, all, canBypass);
    }

    /// <summary>
    /// Throws <see cref="BranchProtectedException"/> with a clear message when the actor may not merge the PR.
    /// Returns true when the merge only proceeds because the actor bypasses protection.
    /// </summary>
    public async Task<bool> EnsureCanMergeAsync(int pullRequestId, int actorId, CancellationToken ct = default)
    {
        var result = await EvaluateAsync(pullRequestId, actorId, ct);
        if (!result.Blocked) return false;
        if (result.CanBypass) return true;
        throw new BranchProtectedException(result.BlockedMessage);
    }

    private async Task<List<MergeCheckResult>> ExternalChecksAsync(int pullRequestId, CancellationToken ct)
    {
        var list = new List<MergeCheckResult>();
        foreach (var check in services.GetServices<IMergeCheck>())
        {
            try
            {
                list.AddRange(await check.EvaluateAsync(pullRequestId, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Merge check {Check} failed for pull request {Id}", check.GetType().Name, pullRequestId);
                list.Add(new MergeCheckResult(check.GetType().Name, MergeCheckState.Failed, "This check couldn't be evaluated."));
            }
        }
        return list;
    }
}
