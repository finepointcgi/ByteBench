using ByteBench.Services.Git;

namespace ByteBench.Services;

/// <summary>
/// The one-line merge status on the pull request page. It never says "Ready to merge" while branch protection or a
/// merge check blocks the merge: a clean (conflict-free) branch reads "No conflicts." plus what is in the way.
/// </summary>
public static class MergeStatusText
{
    public enum Level { Normal, Success, Warning, Error }

    /// <param name="gate">Branch protection and merge checks, or null before they have been evaluated.</param>
    public static string Describe(MergeCheck? check, bool canFastForward, string? checkMessage, MergeGateResult? gate, bool archived)
    {
        if (archived) return "This repository is archived (read-only), so pull requests can't be merged.";
        return check switch
        {
            MergeCheck.Clean when gate is { Blocked: true, CanBypass: false } => "No conflicts, but merging is blocked: see the checks below.",
            MergeCheck.Clean when gate is { Blocked: true } => "No conflicts. Protection rules aren't met, but you can bypass them.",
            MergeCheck.Clean => canFastForward ? "Ready to merge (can fast-forward)." : "Ready to merge. No conflicts.",
            MergeCheck.Conflicts => "This pull request has conflicts and can't be merged. Resolve them locally and push.",
            MergeCheck.UpToDate => "Nothing to merge: " + checkMessage,
            MergeCheck.MissingBranch => checkMessage ?? "",
            _ => "Checking…",
        };
    }

    /// <summary>Success is only for a merge that can actually go ahead.</summary>
    public static Level Severity(MergeCheck? check, MergeGateResult? gate) => check switch
    {
        MergeCheck.Clean when gate is { Blocked: true } => Level.Warning,
        MergeCheck.Clean => Level.Success,
        MergeCheck.Conflicts or MergeCheck.MissingBranch => Level.Error,
        _ => Level.Normal,
    };
}
