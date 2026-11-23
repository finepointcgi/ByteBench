using LibGit2Sharp;
using LgRepository = LibGit2Sharp.Repository;

namespace ByteBench.Services.Git;

public static partial class GitOps
{
    /// <summary>
    /// Shas of the commits a merge commit brought in (reachable from its second parent but not its first), for each of
    /// <paramref name="mergeShas"/>; at most <paramref name="perMerge"/> per merge. Used to list a release's pull requests instead
    /// of their individual commits.
    /// </summary>
    public static HashSet<string> CommitsBroughtInBy(string path, IEnumerable<string> mergeShas, int perMerge = 500)
    {
        var covered = new HashSet<string>(StringComparer.Ordinal);
        using var r = new LgRepository(path);
        foreach (var sha in mergeShas)
        {
            if (r.Lookup<Commit>(sha) is not { } merge || merge.Parents.Count() < 2) continue;
            var parents = merge.Parents.ToList();
            var filter = new CommitFilter { IncludeReachableFrom = parents[1], ExcludeReachableFrom = parents[0] };
            foreach (var c in r.Commits.QueryBy(filter).Take(perMerge)) covered.Add(c.Sha);
        }
        return covered;
    }
}
