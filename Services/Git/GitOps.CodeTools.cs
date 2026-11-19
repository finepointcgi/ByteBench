using System.Text;
using LibGit2Sharp;
using LgRepository = LibGit2Sharp.Repository;

namespace ByteBench.Services.Git;

// Repository settings and code search helpers.
public static partial class GitOps
{
    /// <summary>Points the bare repository's HEAD at <paramref name="branch"/> (which must exist).</summary>
    public static void SetHead(string path, string branch)
    {
        lock (LockFor(path))
        {
            using var r = new LgRepository(path);
            if (r.Branches[branch] == null) throw new InvalidOperationException($"Branch '{branch}' does not exist.");
            r.Refs.UpdateTarget("HEAD", "refs/heads/" + branch);
        }
    }

    /// <summary>
    /// Moves a bare repository directory, holding the per-path locks of both locations so no ref update
    /// in this process runs against it mid-move. Fails if <paramref name="to"/> already exists.
    /// </summary>
    public static void MoveRepository(string from, string to)
    {
        lock (LockFor(from))
        lock (LockFor(to))
        {
            if (Directory.Exists(to) || File.Exists(to))
                throw new InvalidOperationException("A repository already exists at the new location on disk.");
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            Directory.Move(from, to);
        }
    }

    /// <summary>The lines of a text file at a commit (null if missing, binary or too large).</summary>
    public static string[]? GetTextLines(string path, string rev, string filePath)
    {
        using var r = new LgRepository(path);
        var c = Resolve(r, rev);
        if (c?[filePath]?.Target is not Blob blob || blob.Size > MaxBlobBytes || blob.IsBinary) return null;
        return blob.GetContentText(Encoding.UTF8).Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
    }
}
