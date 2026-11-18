using LibGit2Sharp;
using LgRepository = LibGit2Sharp.Repository;

namespace GitBench.Services.Git;

/// <summary>One change for <see cref="GitOps.CommitChanges"/>: new content for a path, or null bytes to delete it.</summary>
public sealed record GitFileChange(string Path, byte[]? Content);

/// <summary>A blob in a tree listing: its path, blob id and size.</summary>
public sealed record GitBlobEntry(string Path, string Sha, long Size);

public static partial class GitOps
{
    /// <summary>
    /// Commits <paramref name="changes"/> on <paramref name="branch"/> (a root commit when it doesn't exist yet), but only if
    /// every path in <paramref name="expected"/> still has the given blob id (null: the path must not exist). Checked and
    /// committed under the repository's lock, so two writers can't both win. Throws <see cref="GitConflictException"/>
    /// naming the first path that changed.
    /// </summary>
    public static string CommitChanges(string path, string branch, IReadOnlyList<GitFileChange> changes, string message,
        GitIdentity author, IReadOnlyDictionary<string, string?>? expected = null)
    {
        lock (LockFor(path))
        {
            using var r = new LgRepository(path);
            var parent = r.Branches[branch]?.Tip;
            if (expected != null)
            {
                foreach (var (file, sha) in expected)
                {
                    var current = parent?[file]?.Target is Blob b ? b.Sha : null;
                    if (current != sha) throw new GitConflictException(file);
                }
            }
            var td = parent != null ? TreeDefinition.From(parent.Tree) : new TreeDefinition();
            foreach (var c in changes)
            {
                if (c.Content == null) { if (parent?[c.Path] != null) td.Remove(c.Path); continue; }
                var blob = r.ObjectDatabase.CreateBlob(new MemoryStream(c.Content));
                td.Add(c.Path, blob, Mode.NonExecutableFile);
            }
            var tree = r.ObjectDatabase.CreateTree(td);
            var sig = Sig(author, DateTimeOffset.Now);
            var commit = r.ObjectDatabase.CreateCommit(sig, sig, message, tree, parent != null ? [parent] : [], prettifyMessage: true);
            var refName = "refs/heads/" + branch;
            if (r.Refs[refName] is { } existing) r.Refs.UpdateTarget(existing, commit.Id, "commit: " + FirstLine(message));
            else r.Refs.Add(refName, commit.Id, "commit (initial): " + FirstLine(message));
            return commit.Sha;
        }
    }

    /// <summary>Every blob reachable from <paramref name="rev"/>'s tree (recursively), or empty when the ref doesn't exist.</summary>
    public static List<GitBlobEntry> ListAllBlobs(string path, string? rev)
    {
        using var r = new LgRepository(path);
        var c = Resolve(r, rev);
        var list = new List<GitBlobEntry>();
        if (c == null) return list;
        void Walk(Tree t)
        {
            foreach (var e in t)
            {
                if (e.Target is Tree sub) Walk(sub);
                else if (e.Target is Blob b) list.Add(new GitBlobEntry(e.Path.Replace('\\', '/'), b.Sha, b.Size));
            }
        }
        Walk(c.Tree);
        return list;
    }

    /// <summary>Raw bytes of the blob at <paramref name="filePath"/> in <paramref name="rev"/> (null if missing or larger than <paramref name="maxBytes"/>).</summary>
    public static byte[]? GetBlobBytes(string path, string? rev, string filePath, long maxBytes)
    {
        using var r = new LgRepository(path);
        var c = Resolve(r, rev);
        if (c?[filePath.Trim('/')]?.Target is not Blob blob || blob.Size > maxBytes) return null;
        using var s = blob.GetContentStream();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>The diff of one file between two commits (either may lack it: added or deleted).</summary>
    public static List<FileDiff> DiffFile(string path, string fromRev, string toRev, string filePath)
    {
        using var r = new LgRepository(path);
        var from = Resolve(r, fromRev);
        var to = Resolve(r, toRev);
        if (from == null || to == null) return [];
        var opts = new CompareOptions { ContextLines = 3, IncludeUnmodified = false };
        var patch = r.Diff.Compare<Patch>(from.Tree, to.Tree, [filePath], opts);
        return patch.Select(e => new FileDiff((e.Status == ChangeKind.Deleted ? e.OldPath : e.Path).Replace('\\', '/'), null,
            e.Status switch { ChangeKind.Added => "added", ChangeKind.Deleted => "deleted", _ => "modified" },
            e.LinesAdded, e.LinesDeleted, e.IsBinaryComparison, e.Patch.Length > MaxFilePatchChars,
            e.IsBinaryComparison || e.Patch.Length > MaxFilePatchChars ? [] : ParseHunks(e.Patch))).ToList();
    }

    /// <summary>UTF-8 text of a blob, with CRLF normalised (null when missing or binary/too large).</summary>
    public static string? GetText(string path, string? rev, string filePath) =>
        GetBlob(path, rev, filePath) is { IsBinary: false, TooLarge: false, Text: { } t } ? t : null;
}
