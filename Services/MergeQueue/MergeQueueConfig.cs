using GitBench.Data;

namespace GitBench.Services.MergeQueue;

/// <summary>A branch's merge queue changed: entries added, removed, reordered, built, tested or merged.</summary>
public sealed record MergeQueueChanged(int RepositoryId, string TargetBranch) : LiveEvent;

/// <summary>Configuration section <c>MergeQueue</c>.</summary>
public class MergeQueueOptions
{
    /// <summary>Run the background processor (tests turn it off and drive <see cref="MergeQueueEngine"/> themselves).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>How often queues are re-checked without a triggering event (timeouts, missed events).</summary>
    public int PollSeconds { get; set; } = 15;
}

/// <summary>
/// The merge queue settings that apply to a branch: from the branch protection rules matching it that enable the queue.
/// Several rules combine strictly (smallest batch and timeout, CI required if any rule requires it); the merge method is
/// taken from the most specific rule that sets one (exact names before globs).
/// </summary>
public sealed record MergeQueueConfig(int MaxBatch, bool RequireCi, MergeStrategy? Strategy, int TimeoutMinutes, string Patterns, int ApprovalTimeoutMinutes = 1440)
{
    public static MergeQueueConfig? For(IEnumerable<BranchProtectionRule> rules, string branch)
    {
        var matching = rules.Where(r => r.MergeQueueEnabled && BranchPatterns.Matches(r.Pattern, branch))
            .OrderBy(r => BranchPatterns.IsGlob(r.Pattern)).ThenBy(r => r.Pattern, StringComparer.Ordinal).ToList();
        if (matching.Count == 0) return null;
        return new MergeQueueConfig(
            Math.Clamp(matching.Min(r => r.MergeQueueMaxBatch), 1, 20),
            matching.Any(r => r.MergeQueueRequireCi),
            matching.Select(r => r.MergeQueueStrategy).FirstOrDefault(s => s != null),
            Math.Clamp(matching.Min(r => r.MergeQueueTimeoutMinutes), 1, 1440),
            string.Join(", ", matching.Select(r => r.Pattern)),
            Math.Clamp(matching.Min(r => r.MergeQueueApprovalTimeoutMinutes), 1, 10080));
    }

    /// <summary>The strategy the queue merges with in <paramref name="repo"/>: the configured one if the repository allows it, else its default.</summary>
    public MergeStrategy StrategyFor(Repository repo) =>
        Strategy is { } s && MergeStrategySettings.Allowed(repo).HasFlag(MergeStrategySettings.Flag(s)) ? s : MergeStrategySettings.Default(repo);

    public string Describe => $"up to {MaxBatch} tested at once" + (RequireCi ? ", pipeline must pass" : ", no pipeline required");
}

/// <summary>Page URLs of the merge queue.</summary>
public static class MergeQueueUrls
{
    public static string Queue(Repository r, string? branch = null) =>
        Git.RepoUrls.Base(r) + "/merge-queue" + (branch == null ? "" : "?branch=" + Uri.EscapeDataString(branch));
}
