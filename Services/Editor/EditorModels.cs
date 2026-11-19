using System.Text;
using ByteBench.Data;
using ByteBench.Services.Git;

namespace ByteBench.Services.Editor;

/// <summary>What the web editor needs to know about an open workspace and what the user may do in it.</summary>
/// <param name="CanEdit">Edit files and commit (PushCode, the repository isn't archived or a pull mirror).</param>
/// <param name="ReadOnlyReason">Why <paramref name="CanEdit"/> is false.</param>
/// <param name="ProtectionNote">Set when branch protection refuses direct commits to the branch: commit to a new branch and open a PR instead.</param>
public sealed record EditorContext(
    int WorkspaceId, Repository Repo, string Branch, string BaseSha, string? BranchTip, bool CanEdit, string? ReadOnlyReason,
    string? ProtectionNote, bool CanCreatePullRequest, int UserId, int ChangeCount)
{
    public bool BranchMoved => BranchTip != null && BranchTip != BaseSha;
    public bool CanCommitToBranch => CanEdit && ProtectionNote == null && BranchTip != null;
}

public enum EditorFileStatus { Unchanged, Added, Modified, Deleted, Renamed }

public sealed record EditorTreeEntry(string Name, string Path, bool IsDirectory, long? Size, EditorFileStatus Status);

/// <summary>A file as the workspace sees it. <see cref="Text"/> is null for binary or large files (shown read-only).</summary>
public sealed record EditorFile(string Path, bool Exists, string? Text, bool IsBinary, bool TooLarge, long Size, EditorFileStatus Status)
{
    public bool IsEditableText => Exists && Text != null;
}

/// <summary>One working change, with the text at the base commit (of the original path for a rename) for the diff view.</summary>
public sealed record EditorChange(string Path, string? OriginalPath, EditorChangeKind Kind, string? BaseText, string? NewText);

/// <summary>
/// The workspace against its branch: whether the branch moved since <see cref="BaseSha"/>, the changed files the branch
/// also changed (<see cref="Overlapping"/>) and which of those don't merge (<see cref="Conflicts"/>).
/// </summary>
public sealed record EditorStatus(string BaseSha, string? TipSha, IReadOnlyList<string> Overlapping, IReadOnlyList<string> Conflicts)
{
    public bool BranchMissing => TipSha == null;
    public bool BranchMoved => TipSha != null && TipSha != BaseSha;
    /// <summary>A commit now would have to merge with the branch's new commits first.</summary>
    public bool NeedsRebase => BranchMoved && Overlapping.Count > 0;
}

public enum ConflictResolution { KeepMine, TakeTheirs }

public sealed record EditorCommitResult(string Sha, string Branch, PullRequest? PullRequest);

/// <summary>A search hit in the workspace (committed files plus working changes).</summary>
public sealed record EditorSearchHit(string Path, int Line, string Text);

/// <summary>Path rules for workspace files: relative, '/'-separated, no '.', '..' or '.git' segments.</summary>
public static class EditorPaths
{
    public const int MaxLength = 1000;

    /// <summary>The normalised path, or throws <see cref="InvalidOperationException"/> with a friendly message.</summary>
    public static string Normalize(string? path)
    {
        var p = (path ?? "").Trim().Replace('\\', '/').Trim('/');
        if (p.Length == 0) throw new InvalidOperationException("Enter a file path.");
        if (p.Length > MaxLength) throw new InvalidOperationException("The path is too long.");
        if (p.Any(ch => ch == '\0' || char.IsControl(ch))) throw new InvalidOperationException("The path contains invalid characters.");
        var parts = p.Split('/');
        if (parts.Any(s => s.Length == 0 || s is "." or ".." || s.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"'{path}' is not a valid file path.");
        return p;
    }

    public static bool TryNormalize(string? path, out string normalized)
    {
        try { normalized = Normalize(path); return true; }
        catch (InvalidOperationException) { normalized = ""; return false; }
    }

    public static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];

    public static string? Directory(string path) => path.LastIndexOf('/') is var i and > 0 ? path[..i] : null;

    public static int Utf8Bytes(string text) => Encoding.UTF8.GetByteCount(text);

    /// <summary>Text the editor can hold: at most <see cref="GitOps.MaxBlobBytes"/> bytes.</summary>
    public static void EnsureEditableSize(string text)
    {
        if (Utf8Bytes(text) > GitOps.MaxBlobBytes)
            throw new InvalidOperationException($"Files larger than {GitOps.MaxBlobBytes / 1024 / 1024} MB can't be edited in the web editor.");
    }
}
