namespace GitBench.Services.Git;

/// <summary>Plain, LibGit2Sharp-free snapshots of Git data, safe to hold in components after the repo is closed.</summary>
public record CommitInfo(
    string Sha, string MessageShort, string Message,
    string AuthorName, string AuthorEmail, DateTimeOffset When,
    IReadOnlyList<string> ParentShas)
{
    public string ShortSha => Sha[..Math.Min(7, Sha.Length)];
    /// <summary>The message body after the subject line, if any.</summary>
    public string? Body => Message.IndexOf('\n') is var i and >= 0 && Message[(i + 1)..].Trim() is { Length: > 0 } b ? b : null;
}

public record BranchInfo(string Name, CommitInfo? Tip, bool IsDefault);

/// <summary>Ahead/behind counts of a branch relative to another (usually the default branch).</summary>
public record BranchDivergence(string Name, CommitInfo? Tip, bool IsDefault, int Ahead, int Behind);

public record TreeItem(string Name, string Path, bool IsDirectory, bool IsSubmodule, long? Size, CommitInfo? LastCommit);

public record BlobInfo(string Path, string Sha, long Size, bool IsBinary, bool TooLarge, string? Text)
{
    public string[] Lines => Text == null ? [] : Text.TrimEnd('\n').Split('\n');
}

public enum DiffLineKind { Context, Added, Deleted }

public record DiffLine(DiffLineKind Kind, int? OldLine, int? NewLine, string Text);

public record DiffHunk(string Header, IReadOnlyList<DiffLine> Lines);

public record FileDiff(
    string Path, string? OldPath, string Status, int Additions, int Deletions,
    bool IsBinary, bool TooLarge, IReadOnlyList<DiffHunk> Hunks)
{
    public string DisplayPath => OldPath != null && OldPath != Path ? $"{OldPath} → {Path}" : Path;
}

public record CommitDetail(CommitInfo Commit, IReadOnlyList<FileDiff> Files);

/// <summary>
/// Result of comparing <c>head</c> against <c>base</c>: the diff is taken from their merge base (three-dot diff),
/// <see cref="Commits"/> are those on head but not on base, newest first.
/// </summary>
public record CompareResult(
    string BaseSha, string HeadSha, string? MergeBaseSha, int Ahead, int Behind,
    IReadOnlyList<CommitInfo> Commits, IReadOnlyList<FileDiff> Files);

public enum MergeCheck { Clean, Conflicts, UpToDate, MissingBranch }

public record MergeStatus(MergeCheck Check, bool CanFastForward, IReadOnlyList<string> ConflictPaths, string? Message = null);

public record MergeOutcome(
    bool Success, string? Error, string? MergeCommitSha, bool FastForwarded,
    string? SourceSha, string? TargetSha, IReadOnlyList<string> ConflictPaths);

public record GitIdentity(string Name, string Email);
