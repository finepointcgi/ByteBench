using System.Text;
using LibGit2Sharp;
using LgRepository = LibGit2Sharp.Repository;

namespace GitBench.Services.Git;

/// <summary>A replacement of lines <see cref="StartLine"/>..<see cref="EndLine"/> (1-based, inclusive) of a file.</summary>
/// <param name="ExpectedText">What those lines must currently read (joined with \n); the edit is refused otherwise.</param>
/// <param name="Replacement">The new lines; none deletes the lines.</param>
public record LineEdit(string FilePath, int StartLine, int EndLine, string ExpectedText, IReadOnlyList<string> Replacement);

/// <summary>A git change was refused because the content it expected has changed (for example a stale suggestion).</summary>
public class GitConflictException(string message) : InvalidOperationException(message);

/// <summary>Pull request extras: squash and rebase merges, applying suggested changes, changed paths.</summary>
public static partial class GitOps
{
    /// <summary>Paths changed between the merge base of the two revisions and <paramref name="headRev"/> (old paths of renames included).</summary>
    public static List<string> ChangedPaths(string path, string baseRev, string headRev)
    {
        using var r = new LgRepository(path);
        var b = Resolve(r, baseRev);
        var h = Resolve(r, headRev);
        if (b == null || h == null) return [];
        var mb = r.ObjectDatabase.FindMergeBase(h, b);
        var changes = r.Diff.Compare<TreeChanges>(mb?.Tree, h.Tree, new CompareOptions { Similarity = SimilarityOptions.Renames });
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var c in changes)
        {
            set.Add(c.Path.Replace('\\', '/'));
            if (!string.IsNullOrEmpty(c.OldPath)) set.Add(c.OldPath.Replace('\\', '/'));
        }
        return [.. set];
    }

    /// <summary>
    /// Writes one commit on top of <paramref name="target"/> whose tree is the merge of <paramref name="source"/> into it
    /// (parent: the target tip only). Refuses with the conflicted paths if the branches conflict.
    /// </summary>
    public static MergeOutcome Squash(string path, string source, string target, string message, GitIdentity author, GitIdentity committer)
    {
        lock (LockFor(path))
        {
            using var r = new LgRepository(path);
            var sb = r.Branches[source];
            var tb = r.Branches[target];
            if (sb?.Tip == null || tb?.Tip == null)
                return Failed($"Branch '{(sb?.Tip == null ? source : target)}' no longer exists.");
            Commit s = sb.Tip, t = tb.Tip;
            var mb = r.ObjectDatabase.FindMergeBase(s, t);
            if (mb?.Sha == s.Sha) return Failed($"'{target}' already contains every commit from '{source}'.");

            var result = r.ObjectDatabase.MergeCommits(t, s, new MergeTreeOptions { FailOnConflict = false, SkipReuc = true });
            if (result.Status == MergeTreeStatus.Conflicts)
                return new MergeOutcome(false, "This pull request has conflicts and can't be squashed automatically.", null, false, s.Sha, t.Sha, ConflictPaths(result));

            var now = DateTimeOffset.Now;
            var commit = r.ObjectDatabase.CreateCommit(Sig(author, now), Sig(committer, now), message, result.Tree, [t], prettifyMessage: true);
            r.Refs.UpdateTarget(r.Refs[tb.CanonicalName], commit.Id, $"merge {source}: Squashed by GitBench");
            return new MergeOutcome(true, null, commit.Sha, false, s.Sha, t.Sha, []);
        }
    }

    /// <summary>
    /// Replays the commits of <paramref name="source"/> that aren't on <paramref name="target"/> (oldest first, merge commits
    /// dropped, original authors and messages kept) onto the target tip, then fast-forwards the target to the result.
    /// Nothing is moved if any commit conflicts. The source branch itself is left alone.
    /// </summary>
    public static MergeOutcome Rebase(string path, string source, string target, GitIdentity committer)
    {
        lock (LockFor(path))
        {
            using var r = new LgRepository(path);
            var sb = r.Branches[source];
            var tb = r.Branches[target];
            if (sb?.Tip == null || tb?.Tip == null)
                return Failed($"Branch '{(sb?.Tip == null ? source : target)}' no longer exists.");
            Commit s = sb.Tip, t = tb.Tip;
            var mb = r.ObjectDatabase.FindMergeBase(s, t);
            if (mb?.Sha == s.Sha) return Failed($"'{target}' already contains every commit from '{source}'.");

            var commits = r.Commits.QueryBy(new CommitFilter
            {
                IncludeReachableFrom = s, ExcludeReachableFrom = t,
                SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Reverse,
            }).Where(c => c.Parents.Count() <= 1).ToList();

            var onto = t;
            var when = DateTimeOffset.Now;
            foreach (var c in commits)
            {
                var picked = r.ObjectDatabase.CherryPickCommit(c, onto, 0, new MergeTreeOptions { FailOnConflict = false, SkipReuc = true });
                if (picked.Status == MergeTreeStatus.Conflicts)
                    return new MergeOutcome(false, $"Commit {c.Sha[..7]} doesn't apply cleanly on '{target}', so this pull request can't be rebased. Rebase it locally and push, or use another merge strategy.",
                        null, false, s.Sha, t.Sha, ConflictPaths(picked));
                if (picked.Tree.Id == onto.Tree.Id) continue; // already applied upstream: drop it, like git rebase
                onto = r.ObjectDatabase.CreateCommit(c.Author, Sig(committer, when), c.Message, picked.Tree, [onto], prettifyMessage: false);
            }
            if (onto.Sha == t.Sha) return Failed($"Every change from '{source}' is already on '{target}'.");
            r.Refs.UpdateTarget(r.Refs[tb.CanonicalName], onto.Id, $"merge {source}: Rebased by GitBench");
            return new MergeOutcome(true, null, onto.Sha, false, s.Sha, t.Sha, []);
        }
    }

    /// <summary>
    /// Applies line replacements as one commit on <paramref name="branch"/>. Every edit's lines must still read
    /// <see cref="LineEdit.ExpectedText"/> at the branch tip, edits in one file must not overlap, and with
    /// <paramref name="expectedHead"/> the tip must not have moved; otherwise nothing is written. Line endings
    /// (LF or CRLF), the final newline and the file mode are kept. Returns the new commit's SHA.
    /// </summary>
    public static string ApplyLineEdits(string path, string branch, IReadOnlyList<LineEdit> edits, string message,
        GitIdentity author, GitIdentity committer, string? expectedHead = null)
    {
        if (edits.Count == 0) throw new InvalidOperationException("There is nothing to apply.");
        lock (LockFor(path))
        {
            using var r = new LgRepository(path);
            var b = r.Branches[branch];
            if (b?.Tip == null) throw new InvalidOperationException($"Branch '{branch}' no longer exists.");
            var tip = b.Tip;
            if (expectedHead != null && tip.Sha != expectedHead)
                throw new GitConflictException($"'{branch}' has moved on since; reload and try again.");

            var td = TreeDefinition.From(tip.Tree);
            foreach (var file in edits.GroupBy(e => e.FilePath.Trim('/')))
            {
                if (tip[file.Key] is not { TargetType: TreeEntryTargetType.Blob } entry || entry.Target is not Blob blob)
                    throw new InvalidOperationException($"'{file.Key}' no longer exists on '{branch}'.");
                if (blob.IsBinary || blob.Size > MaxBlobBytes) throw new InvalidOperationException($"'{file.Key}' can't be edited here.");
                var raw = blob.GetContentText(Encoding.UTF8);
                var eol = raw.Contains("\r\n") ? "\r\n" : "\n";
                var finalNewline = raw.EndsWith('\n');
                var lines = raw.Replace("\r\n", "\n").Split('\n').ToList();
                if (finalNewline) lines.RemoveAt(lines.Count - 1);

                var ordered = file.OrderByDescending(e => e.StartLine).ToList();
                for (var i = 1; i < ordered.Count; i++)
                    if (ordered[i].EndLine >= ordered[i - 1].StartLine)
                        throw new GitConflictException($"Two suggestions change the same lines of '{file.Key}'; apply them one at a time.");
                foreach (var e in ordered)
                {
                    if (e.StartLine < 1 || e.EndLine < e.StartLine || e.EndLine > lines.Count
                        || string.Join("\n", lines.GetRange(e.StartLine - 1, e.EndLine - e.StartLine + 1)) != e.ExpectedText.Replace("\r\n", "\n"))
                        throw new GitConflictException($"Lines {Range(e)} of '{file.Key}' have changed since the suggestion was made, so it can't be applied.");
                    lines.RemoveRange(e.StartLine - 1, e.EndLine - e.StartLine + 1);
                    lines.InsertRange(e.StartLine - 1, e.Replacement.Select(l => l.TrimEnd('\r')));
                }
                var text = string.Join(eol, lines) + (finalNewline && lines.Count > 0 ? eol : "");
                var newBlob = r.ObjectDatabase.CreateBlob(new MemoryStream(Encoding.UTF8.GetBytes(text)));
                td.Add(file.Key, newBlob, entry.Mode);
            }

            var when = DateTimeOffset.Now;
            var tree = r.ObjectDatabase.CreateTree(td);
            if (tree.Id == tip.Tree.Id) throw new InvalidOperationException("The suggestion doesn't change anything.");
            var commit = r.ObjectDatabase.CreateCommit(Sig(author, when), Sig(committer, when), message, tree, [tip], prettifyMessage: true);
            r.Refs.UpdateTarget(r.Refs[b.CanonicalName], commit.Id, "commit: " + FirstLine(message));
            return commit.Sha;
        }

        static string Range(LineEdit e) => e.StartLine == e.EndLine ? e.StartLine.ToString() : $"{e.StartLine}-{e.EndLine}";
    }

    /// <summary>Lines <paramref name="startLine"/>..<paramref name="endLine"/> (1-based) of a text file at <paramref name="rev"/>, joined with \n; null if unavailable.</summary>
    public static string? ReadLines(string path, string rev, string filePath, int startLine, int endLine)
    {
        var blob = GetBlob(path, rev, filePath);
        if (blob?.Text == null) return null;
        var lines = blob.Lines;
        if (startLine < 1 || endLine < startLine || endLine > lines.Length) return null;
        return string.Join("\n", lines[(startLine - 1)..endLine]);
    }

    private static MergeOutcome Failed(string error) => new(false, error, null, false, null, null, []);
}
