using GitBench.Data;
using LibGit2Sharp;
using LgRepository = LibGit2Sharp.Repository;

namespace GitBench.Services.Git;

/// <summary>Merge queue plumbing: speculative merge commits under <c>refs/merge-queue/</c> and compare-and-swap fast-forwards.</summary>
public static partial class GitOps
{
    /// <summary>Prefix of the refs that hold merge queue speculative commits (refused on git push).</summary>
    public const string MergeQueueRefPrefix = "refs/merge-queue/";

    /// <summary>The ref holding the speculative commit of queue entry <paramref name="entryId"/> for <paramref name="branch"/>.</summary>
    public static string MergeQueueRef(string branch, int entryId) => $"{MergeQueueRefPrefix}{branch}/{entryId}";

    /// <summary>
    /// Writes the commit the target would get if <paramref name="headSha"/> were merged onto <paramref name="baseSha"/> with
    /// <paramref name="strategy"/> (merge: a two-parent merge commit, never a fast-forward; squash: one commit; rebase: the
    /// commits replayed), and points <paramref name="refName"/> at it. No branch moves. Fails with the conflicted paths if
    /// the changes don't apply, or if <paramref name="baseSha"/> already contains the head.
    /// </summary>
    public static MergeOutcome BuildSpeculativeMerge(string path, string baseSha, string headSha, MergeStrategy strategy, string message,
        GitIdentity author, GitIdentity committer, string refName)
    {
        lock (LockFor(path))
        {
            using var r = new LgRepository(path);
            if (r.Lookup<Commit>(baseSha) is not { } t) return Failed($"Commit {Short(baseSha)} no longer exists.");
            if (r.Lookup<Commit>(headSha) is not { } s) return Failed($"Commit {Short(headSha)} no longer exists.");
            var mb = r.ObjectDatabase.FindMergeBase(s, t);
            if (mb?.Sha == s.Sha) return Failed("The target already contains every commit of this pull request.");
            var now = DateTimeOffset.Now;
            Commit result;
            if (strategy == MergeStrategy.Rebase)
            {
                var commits = r.Commits.QueryBy(new CommitFilter
                {
                    IncludeReachableFrom = s, ExcludeReachableFrom = t,
                    SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Reverse,
                }).Where(c => c.Parents.Count() <= 1).ToList();
                var onto = t;
                foreach (var c in commits)
                {
                    var picked = r.ObjectDatabase.CherryPickCommit(c, onto, 0, new MergeTreeOptions { FailOnConflict = false, SkipReuc = true });
                    if (picked.Status == MergeTreeStatus.Conflicts)
                        return new MergeOutcome(false, $"Commit {c.Sha[..7]} doesn't apply cleanly on top of the target and the pull requests ahead of it.",
                            null, false, s.Sha, t.Sha, ConflictPaths(picked));
                    if (picked.Tree.Id == onto.Tree.Id) continue;
                    onto = r.ObjectDatabase.CreateCommit(c.Author, Sig(committer, now), c.Message, picked.Tree, [onto], prettifyMessage: false);
                }
                if (onto.Sha == t.Sha) return Failed("Every change of this pull request is already on the target.");
                result = onto;
            }
            else
            {
                var merged = r.ObjectDatabase.MergeCommits(t, s, new MergeTreeOptions { FailOnConflict = false, SkipReuc = true });
                if (merged.Status == MergeTreeStatus.Conflicts)
                    return new MergeOutcome(false, "This pull request conflicts with the target and the pull requests ahead of it.", null, false, s.Sha, t.Sha, ConflictPaths(merged));
                result = strategy == MergeStrategy.Squash
                    ? r.ObjectDatabase.CreateCommit(Sig(author, now), Sig(committer, now), message, merged.Tree, [t], prettifyMessage: true)
                    : r.ObjectDatabase.CreateCommit(Sig(committer, now), Sig(committer, now), message, merged.Tree, [t, s], prettifyMessage: true);
            }
            r.Refs.Add(refName, result.Id, "merge queue: speculative merge", allowOverwrite: true);
            return new MergeOutcome(true, null, result.Sha, false, s.Sha, t.Sha, []);
        }
    }

    /// <summary>
    /// Moves <paramref name="branch"/> from <paramref name="expectedTip"/> to <paramref name="newSha"/>, only if it is still at
    /// <paramref name="expectedTip"/> and the move is a fast-forward. False (nothing changed) otherwise.
    /// </summary>
    public static bool FastForwardBranch(string path, string branch, string expectedTip, string newSha, string logMessage)
    {
        lock (LockFor(path))
        {
            using var r = new LgRepository(path);
            var b = r.Branches[branch];
            if (b?.Tip == null || b.Tip.Sha != expectedTip) return false;
            if (r.Lookup<Commit>(newSha) is not { } target) return false;
            if (r.ObjectDatabase.FindMergeBase(b.Tip, target)?.Sha != b.Tip.Sha) return false;
            r.Refs.UpdateTarget(r.Refs[b.CanonicalName], target.Id, logMessage);
            return true;
        }
    }

    /// <summary>True if <paramref name="ancestor"/> is reachable from (or equal to) <paramref name="descendant"/>.</summary>
    public static bool IsAncestor(string path, string ancestor, string descendant)
    {
        using var r = new LgRepository(path);
        if (r.Lookup<Commit>(ancestor) is not { } a || r.Lookup<Commit>(descendant) is not { } d) return false;
        return a.Sha == d.Sha || r.ObjectDatabase.FindMergeBase(a, d)?.Sha == a.Sha;
    }

    /// <summary>Full names of the refs under <paramref name="prefix"/>, with their target SHAs.</summary>
    public static Dictionary<string, string> ListRefs(string path, string prefix)
    {
        using var r = new LgRepository(path);
        return r.Refs.Where(x => x.CanonicalName.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(x => x.CanonicalName, x => x.ResolveToDirectReference()?.TargetIdentifier ?? "");
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
