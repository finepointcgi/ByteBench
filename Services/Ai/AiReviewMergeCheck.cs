using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Ai;

/// <summary>
/// The optional merge check "no unresolved AI blocker findings" (repository setting, off by default). A blocker is an AI
/// finding of severity bug or security whose thread is still unresolved; resolving or dismissing the thread clears it.
/// <see cref="MergeGate"/> applies it whether or not the target branch is protected.
/// </summary>
public static class AiReviewMergeCheck
{
    public const string Name = "AI review";

    public static Task<int> CountBlockersAsync(AppDbContext db, int pullRequestId, CancellationToken ct = default) =>
        db.PullRequestComments.CountAsync(c => c.PullRequestId == pullRequestId && c.ParentId == null && c.AiFingerprint != null && !c.IsResolved
            && (c.AiSeverity == "bug" || c.AiSeverity == "security"), ct);

    /// <summary>The check's line for the merge box (null when the repository doesn't use it) and the reason it blocks (null when it doesn't).</summary>
    public static async Task<(MergeCheckResult? Check, string? Blocker)> EvaluateAsync(AppDbContext db, int pullRequestId, int repositoryId, CancellationToken ct = default)
    {
        var on = await db.RepositoryAiReviewSettings.AnyAsync(s => s.RepositoryId == repositoryId && s.Enabled && s.BlockOnUnresolvedFindings, ct);
        if (!on) return (null, null);
        var n = await CountBlockersAsync(db, pullRequestId, ct);
        return n == 0
            ? (new MergeCheckResult(Name, MergeCheckState.Passed, "No unresolved AI blocker findings"), null)
            : (new MergeCheckResult(Name, MergeCheckState.Failed, $"{n} unresolved AI blocker finding{(n == 1 ? "" : "s")}: resolve or dismiss {(n == 1 ? "it" : "them")}"),
                $"{n} AI blocker finding{(n == 1 ? " is" : "s are")} unresolved");
    }
}
