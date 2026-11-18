using LibGit2Sharp;
using LgRepository = LibGit2Sharp.Repository;

namespace GitBench.Services.Git;

/// <summary>A tag: the commit it points at and, for annotated tags, the tagger, date and message.</summary>
public record TagInfo(string Name, string ObjectSha, CommitInfo? Commit, bool IsAnnotated,
    string? TaggerName, string? TaggerEmail, DateTimeOffset? TaggedAt, string? Message)
{
    /// <summary>The tagger date for annotated tags, else the commit date (for sorting).</summary>
    public DateTimeOffset? When => TaggedAt ?? Commit?.When;
}

public static partial class GitOps
{
    /// <summary>Longest tag name accepted (same as a release's TagName column).</summary>
    public const int MaxTagNameLength = 250;

    /// <summary>
    /// A valid tag name: a valid git ref name under <c>refs/tags/</c> (no spaces, <c>~^:?*[\</c>, <c>..</c>, <c>@{</c>,
    /// control characters, trailing <c>.lock</c> …), not starting with <c>-</c> (so it can never be read as an option),
    /// at most <see cref="MaxTagNameLength"/> characters.
    /// </summary>
    public static bool IsValidTagName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= MaxTagNameLength && !name.StartsWith('-') && name.Trim() == name
        && Reference.IsValidName("refs/tags/" + name);

    public static List<TagInfo> ListTagDetails(string path)
    {
        using var r = new LgRepository(path);
        return r.Tags.Select(Describe).OrderByDescending(t => t.When).ThenByDescending(t => t.Name, StringComparer.Ordinal).ToList();
    }

    public static TagInfo? GetTag(string path, string name)
    {
        if (!IsValidTagName(name)) return null;
        using var r = new LgRepository(path);
        return r.Tags[name] is { } t ? Describe(t) : null;
    }

    public static bool TagExists(string path, string name)
    {
        if (!IsValidTagName(name)) return false;
        using var r = new LgRepository(path);
        return r.Tags[name] != null;
    }

    /// <summary>
    /// Creates tag <paramref name="name"/> at the commit <paramref name="targetRev"/> resolves to. With a non-empty
    /// <paramref name="message"/> it is an annotated tag by <paramref name="tagger"/>, otherwise a lightweight one.
    /// </summary>
    public static TagInfo CreateTag(string path, string name, string targetRev, string? message, GitIdentity tagger)
    {
        if (!IsValidTagName(name)) throw new InvalidOperationException($"'{name}' is not a valid tag name.");
        lock (LockFor(path))
        {
            using var r = new LgRepository(path);
            if (r.Tags[name] != null) throw new InvalidOperationException($"Tag '{name}' already exists.");
            if (r.Branches[name] != null) throw new InvalidOperationException($"'{name}' is already a branch name; pick a different tag name.");
            var commit = Resolve(r, targetRev) ?? throw new InvalidOperationException($"Unknown ref '{targetRev}'.");
            var tag = string.IsNullOrWhiteSpace(message)
                ? r.Tags.Add(name, commit)
                : r.Tags.Add(name, commit, Sig(tagger, DateTimeOffset.Now), message.Trim().Replace("\r\n", "\n") + "\n");
            return Describe(tag);
        }
    }

    public static void DeleteTag(string path, string name)
    {
        if (!IsValidTagName(name)) throw new InvalidOperationException($"Tag '{name}' does not exist.");
        lock (LockFor(path))
        {
            using var r = new LgRepository(path);
            if (r.Tags[name] == null) throw new InvalidOperationException($"Tag '{name}' does not exist.");
            r.Tags.Remove(name);
        }
    }

    /// <summary>Commits reachable from <paramref name="toRev"/> but not from <paramref name="fromRev"/> (all history if null), newest first.</summary>
    public static (List<CommitInfo> Items, bool Truncated) CommitsBetween(string path, string? fromRev, string toRev, int max = 500)
    {
        using var r = new LgRepository(path);
        var to = Resolve(r, toRev);
        if (to == null) return ([], false);
        var from = fromRev == null ? null : Resolve(r, fromRev);
        var filter = new CommitFilter
        {
            IncludeReachableFrom = to,
            ExcludeReachableFrom = from,
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time,
        };
        var list = r.Commits.QueryBy(filter).Take(max + 1).Select(Info).ToList();
        var truncated = list.Count > max;
        if (truncated) list.RemoveAt(max);
        return (list, truncated);
    }

    /// <summary>
    /// Of <paramref name="candidates"/> (tag names), the one whose commit is the closest ancestor of <paramref name="rev"/>
    /// (the newest by commit date among ancestors, the commit itself included), or null.
    /// </summary>
    public static string? FindPreviousTag(string path, string rev, IEnumerable<string> candidates)
    {
        using var r = new LgRepository(path);
        var head = Resolve(r, rev);
        if (head == null) return null;
        string? best = null;
        DateTimeOffset bestWhen = default;
        foreach (var name in candidates.Where(IsValidTagName))
        {
            if (r.Tags[name]?.PeeledTarget is not Commit c) continue;
            var mb = r.ObjectDatabase.FindMergeBase(c, head);
            if (mb?.Sha != c.Sha) continue; // not an ancestor
            if (best == null || c.Committer.When > bestWhen) (best, bestWhen) = (name, c.Committer.When);
        }
        return best;
    }

    private static TagInfo Describe(Tag t)
    {
        var commit = t.PeeledTarget as Commit;
        var a = t.Annotation;
        return new TagInfo(t.FriendlyName, t.Target.Sha, commit == null ? null : Info(commit), t.IsAnnotated,
            a?.Tagger?.Name, a?.Tagger?.Email, a?.Tagger?.When, a?.Message?.TrimEnd());
    }
}
