using LibGit2Sharp;
using LgRepository = LibGit2Sharp.Repository;

namespace ByteBench.Services.Git;

/// <summary>The version of a deployed commit: a tag at it, or the nearest tag below it and how many commits follow.</summary>
public sealed record CommitVersion(string Tag, int Distance)
{
    public override string ToString() => Distance == 0 ? Tag : $"{Tag}+{Distance}";
}

public static partial class GitOps
{
    /// <summary>
    /// For each tip, which of <paramref name="revs"/> it contains (the rev itself or an ancestor). Unknown revs and tips
    /// contain nothing. Keys of the result are the tips as given; values hold the revs as given.
    /// </summary>
    public static Dictionary<string, HashSet<string>> Contained(string path, IReadOnlyCollection<string> revs, IReadOnlyCollection<string> tips)
    {
        var result = tips.Distinct().ToDictionary(t => t, _ => new HashSet<string>(StringComparer.Ordinal));
        if (revs.Count == 0 || tips.Count == 0 || !Exists(path)) return result;
        using var r = new LgRepository(path);
        var commits = revs.Distinct().Select(x => (Rev: x, Commit: Resolve(r, x))).Where(x => x.Commit != null).ToList();
        foreach (var tip in result.Keys.ToList())
        {
            var t = Resolve(r, tip);
            if (t == null) continue;
            foreach (var (rev, c) in commits)
            {
                if (c!.Sha == t.Sha || r.ObjectDatabase.FindMergeBase(c, t)?.Sha == c.Sha) result[tip].Add(rev);
            }
        }
        return result;
    }

    /// <summary>A tag at <paramref name="rev"/> (newest first), else the closest tag below it with the distance, else null.</summary>
    public static CommitVersion? DescribeVersion(string path, string rev)
    {
        if (!Exists(path)) return null;
        using var r = new LgRepository(path);
        var head = Resolve(r, rev);
        if (head == null) return null;
        var tagged = new Dictionary<string, List<Tag>>(StringComparer.Ordinal);
        foreach (var t in r.Tags)
        {
            if (t.PeeledTarget is not Commit c) continue;
            if (!tagged.TryGetValue(c.Sha, out var list)) tagged[c.Sha] = list = [];
            list.Add(t);
        }
        if (tagged.Count == 0) return null;
        // Walk back from the commit (newest first) up to a limit; the first tagged commit is the closest one.
        var distance = 0;
        foreach (var c in r.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = head, SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time }).Take(1000))
        {
            if (tagged.TryGetValue(c.Sha, out var tags))
                return new CommitVersion(tags.OrderByDescending(t => t.Annotation?.Tagger?.When ?? c.Committer.When).ThenByDescending(t => t.FriendlyName, StringComparer.Ordinal).First().FriendlyName, distance);
            distance++;
        }
        return null;
    }

    /// <summary>The commit a tag points at, or null.</summary>
    public static string? TagCommitSha(string path, string tagName)
    {
        if (!IsValidTagName(tagName) || !Exists(path)) return null;
        using var r = new LgRepository(path);
        return (r.Tags[tagName]?.PeeledTarget as Commit)?.Sha;
    }
}
