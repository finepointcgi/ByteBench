namespace GitBench.Services;

/// <summary>Outcome of one check that can gate merging a pull request.</summary>
public enum MergeCheckState { Passed, Pending, Failed, Neutral }

/// <summary>One line in a pull request's merge box (e.g. "Pipeline #12", "2 of 2 approvals").</summary>
/// <param name="Name">Short label shown in the merge box.</param>
/// <param name="State">Passed/Pending/Failed block or allow merging per branch protection; Neutral is informational only.</param>
/// <param name="Detail">Optional one-line explanation.</param>
/// <param name="Url">Optional link (relative URL) to more detail.</param>
public sealed record MergeCheckResult(string Name, MergeCheckState State, string? Detail = null, string? Url = null);

/// <summary>
/// A source of merge checks for pull requests (pipelines, approvals, ...). Register implementations with
/// <c>services.AddScoped&lt;IMergeCheck, MyCheck&gt;()</c>; whoever renders the merge box or merges a PR
/// asks every registered check. Implementations must not throw for PRs they don't apply to: return an empty list.
/// </summary>
public interface IMergeCheck
{
    Task<IReadOnlyList<MergeCheckResult>> EvaluateAsync(int pullRequestId, CancellationToken ct = default);
}
