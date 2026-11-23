using LibGit2Sharp;
using LgRepository = LibGit2Sharp.Repository;

namespace ByteBench.Services.Git;

/// <summary>Dependency bot support (Services/DependencyBot): exact file bytes in, exact file bytes out, on a new branch.</summary>
public static partial class GitOps
{
    /// <summary>The bytes of a file at <paramref name="rev"/>, or null if it doesn't exist, isn't a regular file or is larger than <paramref name="maxBytes"/>.</summary>
    public static byte[]? ReadFileBytes(string path, string rev, string filePath, long maxBytes)
    {
        using var r = new LgRepository(path);
        var c = Resolve(r, rev);
        if (c?[filePath]?.Target is not Blob blob || blob.Size > maxBytes) return null;
        using var s = blob.GetContentStream();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Creates <paramref name="branch"/> from <paramref name="startPoint"/> with one commit that replaces the content of
    /// <paramref name="files"/> (path → exact new bytes; the files must already exist, their mode is kept). Nothing else in the tree changes.
    /// Fails if the branch exists or the files change nothing. Returns the new commit's SHA.
    /// </summary>
    public static string CommitFileBytesOnNewBranch(string path, string branch, string startPoint, IReadOnlyDictionary<string, byte[]> files, string message, GitIdentity author)
    {
        if (!IsValidBranchName(branch)) throw new InvalidOperationException($"'{branch}' is not a valid branch name.");
        lock (LockFor(path))
        {
            using var r = new LgRepository(path);
            if (r.Branches[branch] != null || r.Refs["refs/heads/" + branch] != null) throw new InvalidOperationException($"Branch '{branch}' already exists.");
            var parent = Resolve(r, startPoint) ?? throw new InvalidOperationException($"Unknown revision '{startPoint}'.");
            var td = TreeDefinition.From(parent.Tree);
            foreach (var (file, bytes) in files)
            {
                var existing = parent[file];
                if (existing is not { TargetType: TreeEntryTargetType.Blob } || existing.Mode == Mode.SymbolicLink) throw new InvalidOperationException($"'{file}' is not a file on '{startPoint}'.");
                td.Add(file, r.ObjectDatabase.CreateBlob(new MemoryStream(bytes)), existing.Mode);
            }
            var tree = r.ObjectDatabase.CreateTree(td);
            if (tree.Id == parent.Tree.Id) throw new InvalidOperationException("There are no changes to commit.");
            var sig = Sig(author, DateTimeOffset.Now);
            var commit = r.ObjectDatabase.CreateCommit(sig, sig, message, tree, [parent], prettifyMessage: true);
            r.Refs.Add("refs/heads/" + branch, commit.Id, "commit (dependency bot): " + FirstLine(message));
            return commit.Sha;
        }
    }
}
