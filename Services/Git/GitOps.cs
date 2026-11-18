using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using LibGit2Sharp;
using LgRepository = LibGit2Sharp.Repository;

namespace GitBench.Services.Git;

/// <summary>
/// Git access layer over LibGit2Sharp. Every operation opens the bare repository at the given path,
/// works on objects and refs only (no working tree, no index), and closes it again, so callers never hold
/// native handles. Ref-mutating operations are serialised per repository path inside this process.
/// </summary>
public static partial class GitOps
{
    /// <summary>Blobs larger than this are not loaded for display.</summary>
    public const long MaxBlobBytes = 1024 * 1024;
    /// <summary>Per-file patch size beyond which the diff is summarised instead of rendered.</summary>
    private const int MaxFilePatchChars = 512 * 1024;
    /// <summary>Total diff lines rendered across all files before the remaining files are summarised.</summary>
    private const int MaxTotalDiffLines = 20_000;

    private static readonly ConcurrentDictionary<string, object> Locks = new();
    private static object LockFor(string path) => Locks.GetOrAdd(Path.GetFullPath(path), _ => new object());

    // ---------------------------------------------------------------- repository lifecycle

    public static bool Exists(string path) => Directory.Exists(path) && LgRepository.IsValid(path);

    /// <summary>Creates a bare repository whose default branch contains a single commit adding README.md.</summary>
    public static string CreateBare(string path, string defaultBranch, string readme, GitIdentity author)
    {
        InitBare(path, defaultBranch);
        try
        {
            return CommitFiles(path, defaultBranch, new Dictionary<string, string?> { ["README.md"] = readme }, "Initial commit", author, DateTimeOffset.Now);
        }
        catch
        {
            Directory.Delete(path, recursive: true);
            throw;
        }
    }

    /// <summary>Initialises an empty bare repository whose HEAD points at <paramref name="defaultBranch"/>.</summary>
    public static void InitBare(string path, string defaultBranch)
    {
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new InvalidOperationException("A repository already exists at that location on disk.");
        if (!IsValidBranchName(defaultBranch))
            throw new InvalidOperationException($"'{defaultBranch}' is not a valid branch name.");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lock (LockFor(path))
        {
            LgRepository.Init(path, isBare: true);
            using var r = new LgRepository(path);
            r.Refs.UpdateTarget("HEAD", "refs/heads/" + defaultBranch);
        }
    }

    /// <summary>
    /// Commits a set of file changes (path → new content, or null to delete) on top of <paramref name="branch"/>,
    /// creating the branch if needed (from <paramref name="startPoint"/> or as a root commit). Used for seeding.
    /// </summary>
    public static string CommitFiles(string path, string branch, IDictionary<string, string?> files, string message,
        GitIdentity author, DateTimeOffset when, string? startPoint = null)
    {
        lock (LockFor(path))
        {
            using var r = new LgRepository(path);
            if (r.Branches[branch] == null && startPoint != null)
            {
                var start = Resolve(r, startPoint) ?? throw new InvalidOperationException($"Unknown ref '{startPoint}'.");
                r.Branches.Add(branch, start);
            }
            return WriteCommit(r, branch, files, message, Sig(author, when), Sig(author, when)).Sha;
        }
    }

    private static Commit WriteCommit(LgRepository r, string branch, IDictionary<string, string?> files, string message,
        Signature author, Signature committer)
    {
        var parent = r.Branches[branch]?.Tip;
        var td = parent != null ? TreeDefinition.From(parent.Tree) : new TreeDefinition();
        foreach (var (file, content) in files)
        {
            if (content == null) { td.Remove(file); continue; }
            var blob = r.ObjectDatabase.CreateBlob(new MemoryStream(Encoding.UTF8.GetBytes(content)));
            td.Add(file, blob, Mode.NonExecutableFile);
        }
        var tree = r.ObjectDatabase.CreateTree(td);
        var commit = r.ObjectDatabase.CreateCommit(author, committer, message, tree,
            parent != null ? [parent] : [], prettifyMessage: true);
        var refName = "refs/heads/" + branch;
        if (r.Refs[refName] is { } existing) r.Refs.UpdateTarget(existing, commit.Id, "commit: " + FirstLine(message));
        else r.Refs.Add(refName, commit.Id, "commit (initial): " + FirstLine(message));
        return commit;
    }

    // ---------------------------------------------------------------- refs & branches

    public static bool IsValidBranchName(string name) =>
        !string.IsNullOrWhiteSpace(name) && Reference.IsValidName("refs/heads/" + name);

    public static List<BranchInfo> ListBranches(string path, string defaultBranch)
    {
        using var r = new LgRepository(path);
        return r.Branches.Where(b => !b.IsRemote)
            .Select(b => new BranchInfo(b.FriendlyName, b.Tip is { } t ? Info(t) : null, b.FriendlyName == defaultBranch))
            .OrderByDescending(b => b.IsDefault).ThenByDescending(b => b.Tip?.When).ToList();
    }

    public static List<string> ListTags(string path)
    {
        using var r = new LgRepository(path);
        return r.Tags.Select(t => t.FriendlyName).OrderByDescending(t => t).ToList();
    }

    /// <summary>All branches with ahead/behind counts relative to <paramref name="baseBranch"/>.</summary>
    public static List<BranchDivergence> ListBranchesWithDivergence(string path, string baseBranch)
    {
        using var r = new LgRepository(path);
        var baseTip = r.Branches[baseBranch]?.Tip;
        var result = new List<BranchDivergence>();
        foreach (var b in r.Branches.Where(b => !b.IsRemote))
        {
            int ahead = 0, behind = 0;
            if (baseTip != null && b.Tip != null && b.FriendlyName != baseBranch)
            {
                var d = r.ObjectDatabase.CalculateHistoryDivergence(b.Tip, baseTip);
                ahead = d.AheadBy ?? 0;
                behind = d.BehindBy ?? 0;
            }
            result.Add(new BranchDivergence(b.FriendlyName, b.Tip is { } t ? Info(t) : null, b.FriendlyName == baseBranch, ahead, behind));
        }
        return result.OrderByDescending(b => b.IsDefault).ThenByDescending(b => b.Tip?.When).ToList();
    }

    public static bool BranchExists(string path, string name)
    {
        using var r = new LgRepository(path);
        return r.Branches[name] != null;
    }

    /// <summary>Resolves a branch, tag or commit-ish to a full SHA, or null.</summary>
    public static string? ResolveSha(string path, string? rev)
    {
        using var r = new LgRepository(path);
        return Resolve(r, rev)?.Sha;
    }

    public static void CreateBranch(string path, string name, string fromRev)
    {
        if (!IsValidBranchName(name)) throw new InvalidOperationException($"'{name}' is not a valid branch name.");
        lock (LockFor(path))
        {
            using var r = new LgRepository(path);
            if (r.Branches[name] != null) throw new InvalidOperationException($"Branch '{name}' already exists.");
            var from = Resolve(r, fromRev) ?? throw new InvalidOperationException($"Unknown ref '{fromRev}'.");
            r.Branches.Add(name, from);
        }
    }

    public static void DeleteBranch(string path, string name, string defaultBranch)
    {
        if (name == defaultBranch) throw new InvalidOperationException("The default branch cannot be deleted.");
        lock (LockFor(path))
        {
            using var r = new LgRepository(path);
            if (r.Branches[name] == null) throw new InvalidOperationException($"Branch '{name}' does not exist.");
            r.Branches.Remove(name);
        }
    }

    // ---------------------------------------------------------------- history

    /// <summary>Commits reachable from <paramref name="rev"/>, newest first, optionally limited to those touching <paramref name="filePath"/>.</summary>
    public static (List<CommitInfo> Items, bool HasMore) Log(string path, string? rev, string? filePath, int skip, int take)
    {
        using var r = new LgRepository(path);
        var tip = Resolve(r, rev);
        if (tip == null) return ([], false);
        var filter = new CommitFilter { IncludeReachableFrom = tip, SortBy = CommitSortStrategies.Time | CommitSortStrategies.Topological };
        IEnumerable<Commit> commits = string.IsNullOrEmpty(filePath)
            ? r.Commits.QueryBy(filter)
            : r.Commits.QueryBy(filePath.Trim('/'), filter).Select(e => e.Commit);
        var page = commits.Skip(skip).Take(take + 1).Select(Info).ToList();
        var more = page.Count > take;
        if (more) page.RemoveAt(take);
        return (page, more);
    }

    /// <summary>Recent commits across all branches whose message matches <paramref name="pattern"/>.</summary>
    public static List<CommitInfo> FindCommits(string path, Regex pattern, int scanLimit = 500, int take = 20)
    {
        using var r = new LgRepository(path);
        var tips = r.Branches.Where(b => !b.IsRemote && b.Tip != null).Select(b => b.Tip).ToList();
        if (tips.Count == 0) return [];
        var filter = new CommitFilter { IncludeReachableFrom = tips, SortBy = CommitSortStrategies.Time };
        return r.Commits.QueryBy(filter).Take(scanLimit).Where(c => pattern.IsMatch(c.Message)).Take(take).Select(Info).ToList();
    }

    public static CommitInfo? GetCommit(string path, string rev)
    {
        using var r = new LgRepository(path);
        return Resolve(r, rev) is { } c ? Info(c) : null;
    }

    /// <summary>A commit and its diff against its first parent (or against the empty tree for a root commit).</summary>
    public static CommitDetail? GetCommitDetail(string path, string rev)
    {
        using var r = new LgRepository(path);
        var c = Resolve(r, rev);
        if (c == null) return null;
        var parent = c.Parents.FirstOrDefault();
        return new CommitDetail(Info(c), DiffTrees(r, parent?.Tree, c.Tree));
    }

    // ---------------------------------------------------------------- trees & blobs

    /// <summary>Returns "tree", "blob" or null for the object at <paramref name="filePath"/> in <paramref name="rev"/>.</summary>
    public static string? PathKind(string path, string? rev, string? filePath)
    {
        using var r = new LgRepository(path);
        var c = Resolve(r, rev);
        if (c == null) return null;
        if (string.IsNullOrEmpty(filePath?.Trim('/'))) return "tree";
        return c[filePath.Trim('/')]?.TargetType switch
        {
            TreeEntryTargetType.Tree => "tree",
            TreeEntryTargetType.Blob => "blob",
            _ => null,
        };
    }

    /// <summary>
    /// Lists a directory (directories first). With <paramref name="withLastCommit"/>, also finds the most recent
    /// commit touching each entry by walking up to <paramref name="scanLimit"/> commits of history.
    /// </summary>
    public static List<TreeItem>? ListTree(string path, string? rev, string? dirPath, bool withLastCommit = true, int scanLimit = 400)
    {
        using var r = new LgRepository(path);
        var tip = Resolve(r, rev);
        if (tip == null) return null;
        var dir = (dirPath ?? "").Trim('/');
        var tree = TreeAt(tip, dir);
        if (tree == null) return null;

        var last = new Dictionary<string, CommitInfo>();
        if (withLastCommit && tree.Count > 0)
        {
            var pending = tree.ToDictionary(e => e.Name, e => e.Target.Id);
            var filter = new CommitFilter { IncludeReachableFrom = tip, SortBy = CommitSortStrategies.Time };
            foreach (var c in r.Commits.QueryBy(filter).Take(scanLimit))
            {
                var here = TreeAt(c, dir);
                if (here == null) continue;
                var parentTrees = c.Parents.Select(p => TreeAt(p, dir)).ToList();
                foreach (var (name, id) in pending.ToList())
                {
                    if (here[name]?.Target.Id != id) continue;
                    // Last modified here if no parent already had this exact version.
                    if (parentTrees.All(pt => pt?[name]?.Target.Id != id))
                    {
                        last[name] = Info(c);
                        pending.Remove(name);
                    }
                }
                if (pending.Count == 0) break;
            }
        }

        return tree
            .OrderBy(e => e.TargetType == TreeEntryTargetType.Tree ? 0 : 1)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new TreeItem(e.Name, e.Path.Replace('\\', '/'),
                e.TargetType == TreeEntryTargetType.Tree,
                e.TargetType == TreeEntryTargetType.GitLink,
                e.Target is Blob b ? b.Size : null,
                last.GetValueOrDefault(e.Name)))
            .ToList();
    }

    public static BlobInfo? GetBlob(string path, string? rev, string filePath)
    {
        using var r = new LgRepository(path);
        var c = Resolve(r, rev);
        if (c?[filePath.Trim('/')]?.Target is not Blob blob) return null;
        var tooLarge = blob.Size > MaxBlobBytes;
        var binary = !tooLarge && blob.IsBinary;
        string? text = null;
        if (!tooLarge && !binary)
        {
            text = blob.GetContentText(Encoding.UTF8).Replace("\r\n", "\n");
        }
        return new BlobInfo(filePath.Trim('/'), blob.Sha, blob.Size, binary, tooLarge, text);
    }

    // ---------------------------------------------------------------- compare & merge

    /// <summary>
    /// Compares <paramref name="headRev"/> against <paramref name="baseRev"/> (both branch names, tags or SHAs).
    /// Returns null if either side can't be resolved.
    /// </summary>
    public static CompareResult? Compare(string path, string baseRev, string headRev, int maxCommits = 250)
    {
        using var r = new LgRepository(path);
        var b = Resolve(r, baseRev);
        var h = Resolve(r, headRev);
        if (b == null || h == null) return null;

        var mergeBase = r.ObjectDatabase.FindMergeBase(h, b);
        var div = r.ObjectDatabase.CalculateHistoryDivergence(h, b);
        var commits = r.Commits.QueryBy(new CommitFilter
        {
            IncludeReachableFrom = h,
            ExcludeReachableFrom = b,
            SortBy = CommitSortStrategies.Time | CommitSortStrategies.Topological,
        }).Take(maxCommits).Select(Info).ToList();

        var files = DiffTrees(r, mergeBase?.Tree, h.Tree);
        return new CompareResult(b.Sha, h.Sha, mergeBase?.Sha, div.AheadBy ?? 0, div.BehindBy ?? 0, commits, files);
    }

    /// <summary>Checks, without changing any ref, whether <paramref name="source"/> merges cleanly into <paramref name="target"/>.</summary>
    public static MergeStatus CheckMerge(string path, string source, string target)
    {
        using var r = new LgRepository(path);
        var s = r.Branches[source]?.Tip;
        var t = r.Branches[target]?.Tip;
        if (s == null || t == null)
            return new MergeStatus(MergeCheck.MissingBranch, false, [], $"Branch '{(s == null ? source : target)}' no longer exists.");
        var mb = r.ObjectDatabase.FindMergeBase(s, t);
        if (mb?.Sha == s.Sha) return new MergeStatus(MergeCheck.UpToDate, false, [], $"'{target}' already contains every commit from '{source}'.");
        var ff = mb?.Sha == t.Sha;
        var result = r.ObjectDatabase.MergeCommits(t, s, new MergeTreeOptions { FailOnConflict = false, SkipReuc = true });
        return result.Status == MergeTreeStatus.Conflicts
            ? new MergeStatus(MergeCheck.Conflicts, false, ConflictPaths(result))
            : new MergeStatus(MergeCheck.Clean, ff, []);
    }

    /// <summary>
    /// Merges branch <paramref name="source"/> into branch <paramref name="target"/> inside the bare repository.
    /// Fast-forwards when allowed and possible, otherwise writes a two-parent merge commit. If the merge has
    /// conflicts nothing is written and the conflicted paths are returned.
    /// </summary>
    public static MergeOutcome Merge(string path, string source, string target, string message, GitIdentity who, bool allowFastForward)
    {
        lock (LockFor(path))
        {
            using var r = new LgRepository(path);
            var sb = r.Branches[source];
            var tb = r.Branches[target];
            if (sb?.Tip == null || tb?.Tip == null)
                return Fail($"Branch '{(sb?.Tip == null ? source : target)}' no longer exists.");
            Commit s = sb.Tip, t = tb.Tip;

            var mb = r.ObjectDatabase.FindMergeBase(s, t);
            if (mb?.Sha == s.Sha) return Fail($"'{target}' already contains every commit from '{source}'.");

            var targetRef = r.Refs[tb.CanonicalName];
            if (allowFastForward && mb?.Sha == t.Sha)
            {
                r.Refs.UpdateTarget(targetRef, s.Id, $"merge {source}: Fast-forward");
                return new MergeOutcome(true, null, s.Sha, true, s.Sha, t.Sha, []);
            }

            var result = r.ObjectDatabase.MergeCommits(t, s, new MergeTreeOptions { FailOnConflict = false, SkipReuc = true });
            if (result.Status == MergeTreeStatus.Conflicts)
                return new MergeOutcome(false, "This pull request has conflicts and can't be merged automatically.", null, false, s.Sha, t.Sha, ConflictPaths(result));

            var sig = Sig(who, DateTimeOffset.Now);
            var merge = r.ObjectDatabase.CreateCommit(sig, sig, message, result.Tree, [t, s], prettifyMessage: true);
            r.Refs.UpdateTarget(targetRef, merge.Id, $"merge {source}: Merge made by GitBench");
            return new MergeOutcome(true, null, merge.Sha, false, s.Sha, t.Sha, []);
        }

        static MergeOutcome Fail(string error) => new(false, error, null, false, null, null, []);
    }

    private static List<string> ConflictPaths(MergeTreeResult result) =>
        result.Conflicts.Select(c => (c.Ours ?? c.Theirs ?? c.Ancestor)?.Path.Replace('\\', '/') ?? "?").Distinct().OrderBy(p => p).ToList();

    // ---------------------------------------------------------------- diff

    private static List<FileDiff> DiffTrees(LgRepository r, Tree? oldTree, Tree newTree)
    {
        var opts = new CompareOptions { ContextLines = 3, Similarity = SimilarityOptions.Renames, IncludeUnmodified = false };
        var patch = r.Diff.Compare<Patch>(oldTree, newTree, opts);
        var files = new List<FileDiff>();
        var budget = MaxTotalDiffLines;
        foreach (var e in patch)
        {
            var status = e.Status switch
            {
                ChangeKind.Added => "added",
                ChangeKind.Deleted => "deleted",
                ChangeKind.Renamed => "renamed",
                ChangeKind.Copied => "copied",
                ChangeKind.TypeChanged => "type changed",
                _ => "modified",
            };
            var oldPath = e.Status is ChangeKind.Renamed or ChangeKind.Copied ? e.OldPath.Replace('\\', '/') : null;
            var newPath = (e.Status == ChangeKind.Deleted ? e.OldPath : e.Path).Replace('\\', '/');
            var tooLarge = e.Patch.Length > MaxFilePatchChars || budget <= 0;
            var hunks = e.IsBinaryComparison || tooLarge ? [] : ParseHunks(e.Patch);
            budget -= hunks.Sum(h => h.Lines.Count);
            files.Add(new FileDiff(newPath, oldPath, status, e.LinesAdded, e.LinesDeleted, e.IsBinaryComparison, tooLarge, hunks));
        }
        return files;
    }

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@(.*)$")]
    private static partial Regex HunkHeader();

    /// <summary>Parses the hunks of a single-file unified diff (headers before the first @@ are skipped).</summary>
    internal static List<DiffHunk> ParseHunks(string patch)
    {
        var hunks = new List<DiffHunk>();
        List<DiffLine>? lines = null;
        string header = "";
        int oldNo = 0, newNo = 0;
        foreach (var raw in patch.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var m = HunkHeader().Match(line);
            if (m.Success)
            {
                if (lines != null) hunks.Add(new DiffHunk(header, lines));
                lines = [];
                header = line;
                oldNo = int.Parse(m.Groups[1].Value);
                newNo = int.Parse(m.Groups[3].Value);
                continue;
            }
            if (lines == null) continue;          // file headers before the first hunk
            if (line.Length == 0) continue;       // trailing split artefact (context lines always start with ' ')
            if (line[0] == '\\') continue;        // "\ No newline at end of file"
            switch (line[0])
            {
                case '+': lines.Add(new DiffLine(DiffLineKind.Added, null, newNo++, line[1..])); break;
                case '-': lines.Add(new DiffLine(DiffLineKind.Deleted, oldNo++, null, line[1..])); break;
                case ' ': lines.Add(new DiffLine(DiffLineKind.Context, oldNo++, newNo++, line[1..])); break;
            }
        }
        if (lines != null) hunks.Add(new DiffHunk(header, lines));
        return hunks;
    }

    // ---------------------------------------------------------------- helpers

    private static Commit? Resolve(LgRepository r, string? rev)
    {
        if (string.IsNullOrWhiteSpace(rev)) return r.Head?.Tip;
        try
        {
            // Branch/tag lookups throw for strings that aren't valid ref names (e.g. "main~3"); fall through to rev-parse.
            if (r.Branches[rev]?.Tip is { } bt) return bt;
            if (r.Tags[rev]?.PeeledTarget is Commit tc) return tc;
        }
        catch (InvalidSpecificationException) { }
        try { return r.Lookup<Commit>(rev); }
        catch (LibGit2SharpException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private static Tree? TreeAt(Commit c, string dir) =>
        dir.Length == 0 ? c.Tree : c[dir]?.Target as Tree;

    private static CommitInfo Info(Commit c) => new(
        c.Sha, c.MessageShort, c.Message.TrimEnd(), c.Author.Name, c.Author.Email, c.Author.When,
        c.Parents.Select(p => p.Sha).ToList());

    private static Signature Sig(GitIdentity who, DateTimeOffset when) => new(who.Name, who.Email, when);

    private static string FirstLine(string s) => s.Split('\n')[0];
}
