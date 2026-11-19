using System.Text;
using LibGit2Sharp;
using LgRepository = LibGit2Sharp.Repository;

namespace ByteBench.Services.Git;

/// <summary>One edit of a tree: write <see cref="Content"/> at <see cref="Path"/>, or remove it when Content is null.</summary>
/// <param name="ModeOf">For a write: keep the file mode (and CRLF line endings) of this path in the parent, if it is there
/// (the old path of a rename); defaults to <see cref="Path"/> itself.</param>
public record TreeEdit(string Path, string? Content, string? ModeOf = null);

/// <summary>The result of merging a workspace's edits (made on a base commit) with a branch that moved on.</summary>
/// <param name="Merged">For every edited path that merged cleanly: its merged content, or null if it ends up deleted.</param>
/// <param name="Conflicts">Edited paths that conflict with the branch's own changes.</param>
public record TreeMergeResult(IReadOnlyDictionary<string, string?> Merged, IReadOnlyList<string> Conflicts);

/// <summary>Web editor support (Services/Editor): listing every file, comparing blobs and committing a change set.</summary>
public static partial class GitOps
{
    /// <summary>Every file (blob) at <paramref name="rev"/>, sorted by path, up to <paramref name="max"/>; null if the revision doesn't exist.</summary>
    public static List<(string Path, long Size)>? ListFiles(string path, string rev, int max = 50_000)
    {
        using var r = new LgRepository(path);
        var c = Resolve(r, rev);
        if (c == null) return null;
        var result = new List<(string, long)>();
        var stack = new Stack<Tree>();
        stack.Push(c.Tree);
        while (stack.Count > 0 && result.Count < max)
        {
            foreach (var e in stack.Pop())
            {
                if (e.TargetType == TreeEntryTargetType.Tree) stack.Push((Tree)e.Target);
                else if (e.TargetType == TreeEntryTargetType.Blob && e.Mode != Mode.SymbolicLink)
                {
                    result.Add((e.Path.Replace('\\', '/'), ((Blob)e.Target).Size));
                    if (result.Count >= max) break;
                }
            }
        }
        result.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return result;
    }

    /// <summary>The paths among <paramref name="files"/> whose blob differs between <paramref name="baseRev"/> and <paramref name="headRev"/> (added, removed or changed).</summary>
    public static List<string> ChangedFiles(string path, string baseRev, string headRev, IEnumerable<string> files)
    {
        using var r = new LgRepository(path);
        var b = Resolve(r, baseRev) ?? throw new InvalidOperationException($"Unknown revision '{baseRev}'.");
        var h = Resolve(r, headRev) ?? throw new InvalidOperationException($"Unknown revision '{headRev}'.");
        return files.Distinct().Where(f => b[f]?.Target.Id != h[f]?.Target.Id).OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Writes one commit applying <paramref name="edits"/> and moves <paramref name="branch"/> to it. Either the branch exists
    /// and its tip must be <paramref name="expectedTip"/> (the commit's parent), or <paramref name="createFrom"/> is given and
    /// the branch is created by this commit, whose parent is that revision. Nothing is written if the tip moved or the edits
    /// change nothing. Returns the new commit's SHA.
    /// </summary>
    public static string CommitTreeEdits(string path, string branch, string? expectedTip, string? createFrom,
        IReadOnlyList<TreeEdit> edits, string message, GitIdentity author)
    {
        if (!IsValidBranchName(branch)) throw new InvalidOperationException($"'{branch}' is not a valid branch name.");
        lock (LockFor(path))
        {
            using var r = new LgRepository(path);
            var existing = r.Branches[branch];
            Commit parent;
            if (createFrom != null)
            {
                if (existing != null) throw new InvalidOperationException($"Branch '{branch}' already exists.");
                parent = Resolve(r, createFrom) ?? throw new InvalidOperationException($"Unknown revision '{createFrom}'.");
            }
            else
            {
                parent = existing?.Tip ?? throw new InvalidOperationException($"Branch '{branch}' no longer exists.");
                if (expectedTip != null && parent.Sha != expectedTip)
                    throw new GitConflictException($"'{branch}' has moved on since; update your workspace and try again.");
            }

            var tree = BuildTree(r, parent, edits);
            if (tree.Id == parent.Tree.Id) throw new InvalidOperationException("There are no changes to commit.");
            var sig = Sig(author, DateTimeOffset.Now);
            var commit = r.ObjectDatabase.CreateCommit(sig, sig, message, tree, [parent], prettifyMessage: true);
            if (existing != null) r.Refs.UpdateTarget(r.Refs[existing.CanonicalName], commit.Id, "commit (web editor): " + FirstLine(message));
            else r.Refs.Add("refs/heads/" + branch, commit.Id, "commit (web editor, new branch): " + FirstLine(message));
            return commit.Sha;
        }
    }

    /// <summary>
    /// Three-way merge of <paramref name="edits"/> (made on <paramref name="baseRev"/>) into <paramref name="headRev"/>, without
    /// writing any ref: the edits are committed on the base (an unreferenced commit) and merged with the head. Returns the merged
    /// content of every edited path that merged cleanly, and the conflicted ones.
    /// </summary>
    public static TreeMergeResult MergeTreeEdits(string path, string baseRev, string headRev, IReadOnlyList<TreeEdit> edits)
    {
        using var r = new LgRepository(path);
        var b = Resolve(r, baseRev) ?? throw new InvalidOperationException($"Unknown revision '{baseRev}'.");
        var h = Resolve(r, headRev) ?? throw new InvalidOperationException($"Unknown revision '{headRev}'.");
        var (tree, conflicts) = Merge(r, b, h, edits);
        var merged = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var p in edits.Select(e => e.Path).Distinct())
        {
            if (conflicts.Contains(p)) continue;
            var resultTree = tree;
            if (resultTree == null)
            {
                // The whole merge conflicted elsewhere (and then has no tree): merge this path on its own.
                (resultTree, var own) = Merge(r, b, h, edits.Where(e => e.Path == p).ToList());
                if (own.Contains(p) || resultTree == null) { conflicts.Add(p); continue; }
            }
            merged[p] = resultTree[p]?.Target is Blob blob ? blob.GetContentText(Encoding.UTF8).Replace("\r\n", "\n") : null;
        }
        return new TreeMergeResult(merged, conflicts.Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList());
    }

    private static (Tree? Tree, List<string> Conflicts) Merge(LgRepository r, Commit b, Commit h, IReadOnlyList<TreeEdit> edits)
    {
        var tree = BuildTree(r, b, edits);
        var sig = new Signature("ByteBench", "bytebench@localhost", DateTimeOffset.Now);
        var mine = r.ObjectDatabase.CreateCommit(sig, sig, "web editor workspace", tree, [b], prettifyMessage: false);
        var result = r.ObjectDatabase.MergeCommits(h, mine, new MergeTreeOptions { FailOnConflict = false, SkipReuc = true });
        return result.Status == MergeTreeStatus.Conflicts ? (null, ConflictPaths(result)) : (result.Tree, []);
    }

    private static Tree BuildTree(LgRepository r, Commit parent, IReadOnlyList<TreeEdit> edits)
    {
        var td = TreeDefinition.From(parent.Tree);
        foreach (var e in edits.Where(e => e.Content == null)) td.Remove(e.Path);
        foreach (var e in edits.Where(e => e.Content != null))
        {
            var original = parent[e.ModeOf ?? e.Path];
            var mode = original is { TargetType: TreeEntryTargetType.Blob } && original.Mode != Mode.SymbolicLink ? original.Mode : Mode.NonExecutableFile;
            var text = e.Content!.Replace("\r\n", "\n");
            // Keep Windows line endings in files that had them (the editor works with \n).
            if (original?.Target is Blob ob && !ob.IsBinary && ob.Size <= MaxBlobBytes && ob.GetContentText(Encoding.UTF8).Contains("\r\n"))
                text = text.Replace("\n", "\r\n");
            var blob = r.ObjectDatabase.CreateBlob(new MemoryStream(Encoding.UTF8.GetBytes(text)));
            td.Add(e.Path, blob, mode);
        }
        return r.ObjectDatabase.CreateTree(td);
    }
}
