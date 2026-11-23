using System.Text;
using LibGit2Sharp;
using LgRepository = LibGit2Sharp.Repository;

namespace ByteBench.Services.Git;

/// <summary>One commit that owns at least one line of a blamed file, with where to step back to.</summary>
/// <param name="Path">The file's path as of this commit (differs from the blamed path when it was renamed since).</param>
/// <param name="ParentSha">First parent, if the file existed there (the target of "blame before this commit").</param>
/// <param name="ParentPath">The file's path in <see cref="ParentSha"/>.</param>
public sealed record BlameCommitInfo(string Sha, string Subject, string Message, string AuthorName, string AuthorEmail,
    DateTimeOffset When, string Path, string? ParentSha, string? ParentPath)
{
    public string ShortSha => Sha[..Math.Min(7, Sha.Length)];
}

/// <summary>Lines <see cref="StartLine"/>..<see cref="StartLine"/>+<see cref="LineCount"/>-1 (1-based) belong to commit <see cref="CommitIndex"/>.</summary>
public sealed record BlameRange(int StartLine, int LineCount, int CommitIndex);

public enum BlameStatus { Ok, NotFound, Binary, TooLarge }

public sealed record BlameData(BlameStatus Status, string? Sha, string Path, long Size, string[] Lines,
    IReadOnlyList<BlameCommitInfo> Commits, IReadOnlyList<BlameRange> Ranges);

public static partial class GitOps
{
    /// <summary>
    /// Blames <paramref name="filePath"/> as of <paramref name="rev"/> (branch, tag or commit): who last changed each line.
    /// libgit2 follows the file across renames. Consecutive hunks of one commit are merged into a single range. Files over
    /// <paramref name="maxBytes"/> or <paramref name="maxLines"/> and binary files aren't blamed (the status says why).
    /// </summary>
    public static BlameData Blame(string path, string rev, string filePath, long maxBytes, int maxLines)
    {
        filePath = filePath.Trim('/');
        BlameData Empty(BlameStatus s, string? sha = null, long size = 0) => new(s, sha, filePath, size, [], [], []);

        using var r = new LgRepository(path);
        var commit = Resolve(r, rev);
        if (commit == null || string.IsNullOrEmpty(filePath) || commit[filePath]?.Target is not Blob blob) return Empty(BlameStatus.NotFound, commit?.Sha);
        if (blob.IsBinary) return Empty(BlameStatus.Binary, commit.Sha, blob.Size);
        if (blob.Size > maxBytes) return Empty(BlameStatus.TooLarge, commit.Sha, blob.Size);

        var text = blob.GetContentText(Encoding.UTF8).Replace("\r\n", "\n");
        var lines = text.Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0) lines = lines[..^1]; // the final newline doesn't start a line
        if (lines.Length > maxLines) return Empty(BlameStatus.TooLarge, commit.Sha, blob.Size);
        if (lines.Length == 0) return new BlameData(BlameStatus.Ok, commit.Sha, filePath, blob.Size, [], [], []);

        var hunks = r.Blame(filePath, new BlameOptions { StartingAt = commit });
        var commits = new List<BlameCommitInfo>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var ranges = new List<BlameRange>();
        foreach (var h in hunks.OrderBy(h => h.FinalStartLineNumber))
        {
            var c = h.FinalCommit;
            if (!index.TryGetValue(c.Sha, out var ci))
            {
                ci = commits.Count;
                index[c.Sha] = ci;
                commits.Add(DescribeBlameCommit(r, c, h.InitialPath));
            }
            var start = h.FinalStartLineNumber + 1; // LibGit2Sharp line numbers are 0-based
            if (start < 1 || start > lines.Length) continue;
            var count = Math.Min(h.LineCount, lines.Length - start + 1);
            if (ranges.Count > 0 && ranges[^1] is { } last && last.CommitIndex == ci && last.StartLine + last.LineCount == start)
                ranges[^1] = last with { LineCount = last.LineCount + count };
            else ranges.Add(new BlameRange(start, count, ci));
        }
        return new BlameData(BlameStatus.Ok, commit.Sha, filePath, blob.Size, lines, commits, ranges);
    }

    private static BlameCommitInfo DescribeBlameCommit(LgRepository r, Commit c, string pathInCommit)
    {
        string? parentSha = null, parentPath = null;
        if (c.Parents.FirstOrDefault() is { } parent)
        {
            if (parent[pathInCommit] != null) { parentSha = parent.Sha; parentPath = pathInCommit; }
            else
            {
                // Not there under that name: the commit may have renamed it.
                var changes = r.Diff.Compare<TreeChanges>(parent.Tree, c.Tree, new CompareOptions { Similarity = SimilarityOptions.Renames });
                if (changes.FirstOrDefault(x => x.Status == ChangeKind.Renamed && x.Path == pathInCommit) is { } renamed && parent[renamed.OldPath] != null)
                {
                    parentSha = parent.Sha;
                    parentPath = renamed.OldPath;
                }
            }
        }
        return new BlameCommitInfo(c.Sha, c.MessageShort, c.Message.TrimEnd(), c.Author.Name, c.Author.Email, c.Author.When, pathInCommit, parentSha, parentPath);
    }
}
