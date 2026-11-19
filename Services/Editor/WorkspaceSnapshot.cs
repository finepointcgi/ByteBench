using ByteBench.Data;
using ByteBench.Services.Git;

namespace ByteBench.Services.Editor;

/// <summary>
/// A read-only view of a workspace at one moment: the files at <see cref="BaseSha"/> with the working changes laid over
/// them. Used by the explorer, search and the AI assistant (whose tools can only see this view, so only this repository and
/// branch). Methods hit the bare repository synchronously: call them off the UI thread.
/// </summary>
public sealed class WorkspaceSnapshot
{
    private readonly string repoPath;
    private readonly Dictionary<string, EditorWorkspaceChange> changes;
    private readonly HashSet<string> hidden; // base paths that are gone: deleted, or renamed away

    internal WorkspaceSnapshot(string repoPath, Repository repository, string branch, string baseSha, IEnumerable<EditorWorkspaceChange> changes)
    {
        this.repoPath = repoPath;
        Repository = repository;
        Branch = branch;
        BaseSha = baseSha;
        this.changes = changes.ToDictionary(c => c.Path, StringComparer.Ordinal);
        hidden = this.changes.Values.Where(c => c.Kind == EditorChangeKind.Renamed && c.OriginalPath != null)
            .Select(c => c.OriginalPath!).ToHashSet(StringComparer.Ordinal);
    }

    public Repository Repository { get; }
    public string Branch { get; }
    public string BaseSha { get; }
    internal string RepoPath => repoPath;
    public IReadOnlyCollection<EditorWorkspaceChange> Changes => changes.Values;

    public EditorWorkspaceChange? ChangeAt(string path) => changes.GetValueOrDefault(path);

    /// <summary>Whether a base path is hidden by a rename (its content now lives at the rename's new path).</summary>
    public bool IsRenamedAway(string path) => hidden.Contains(path) && !changes.ContainsKey(path);

    /// <summary>Whether a rename moved the base file at <paramref name="path"/> away (whatever is at the path now).</summary>
    public bool IsHiddenByRename(string path) => hidden.Contains(path);

    public EditorFileStatus StatusOf(string path) => changes.GetValueOrDefault(path)?.Kind switch
    {
        EditorChangeKind.Added => EditorFileStatus.Added,
        EditorChangeKind.Modified => EditorFileStatus.Modified,
        EditorChangeKind.Renamed => EditorFileStatus.Renamed,
        EditorChangeKind.Deleted => EditorFileStatus.Deleted,
        _ => EditorFileStatus.Unchanged,
    };

    /// <summary>The file at <paramref name="path"/> (already normalised) as the workspace sees it.</summary>
    public EditorFile Read(string path)
    {
        if (changes.TryGetValue(path, out var c))
        {
            if (c.Kind == EditorChangeKind.Deleted || c.Content == null) return Missing(path);
            return new EditorFile(path, true, c.Content, false, false, EditorPaths.Utf8Bytes(c.Content), StatusOf(path));
        }
        if (hidden.Contains(path)) return Missing(path);
        var blob = GitOps.GetBlob(repoPath, BaseSha, path);
        if (blob == null) return Missing(path);
        return new EditorFile(path, true, blob.Text, blob.IsBinary, blob.TooLarge, blob.Size, EditorFileStatus.Unchanged);
    }

    /// <summary>The text of the file at the base commit (for diffs), or null.</summary>
    public string? BaseText(string path) => GitOps.GetBlob(repoPath, BaseSha, path)?.Text;

    public bool Exists(string path) => Read(path).Exists;

    /// <summary>Every file in the workspace (sorted), up to <paramref name="max"/>.</summary>
    public List<(string Path, long Size)> ListFiles(int max = 50_000)
    {
        var result = new List<(string Path, long Size)>();
        foreach (var (p, size) in GitOps.ListFiles(repoPath, BaseSha, max) ?? [])
        {
            if (hidden.Contains(p) && !changes.ContainsKey(p)) continue;
            if (changes.TryGetValue(p, out var c))
            {
                if (c.Kind == EditorChangeKind.Deleted || c.Content == null) continue;
                result.Add((p, EditorPaths.Utf8Bytes(c.Content)));
            }
            else result.Add((p, size));
        }
        var seen = result.Select(r => r.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var c in changes.Values)
            if (c.Kind != EditorChangeKind.Deleted && c.Content != null && !seen.Contains(c.Path))
                result.Add((c.Path, EditorPaths.Utf8Bytes(c.Content)));
        result.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return result.Count > max ? result[..max] : result;
    }

    /// <summary>The direct children of <paramref name="dir"/> ("" for the root): folders first, then files.</summary>
    public List<EditorTreeEntry> ListDirectory(string dir)
    {
        dir = dir.Trim('/');
        var prefix = dir.Length == 0 ? "" : dir + "/";
        var dirs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<EditorTreeEntry>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        // Lazy: only this folder of the base tree, with the working changes laid over it.
        foreach (var e in GitOps.ListTree(repoPath, BaseSha, dir, withLastCommit: false) ?? [])
        {
            if (e.IsSubmodule) continue;
            if (e.IsDirectory) { dirs.Add(e.Name); continue; }
            if (changes.TryGetValue(e.Path, out var c))
            {
                if (c.Kind == EditorChangeKind.Deleted || c.Content == null) continue;
                files.Add(new EditorTreeEntry(e.Name, e.Path, false, EditorPaths.Utf8Bytes(c.Content), StatusOf(e.Path)));
            }
            else if (!hidden.Contains(e.Path)) files.Add(new EditorTreeEntry(e.Name, e.Path, false, e.Size, EditorFileStatus.Unchanged));
            names.Add(e.Path);
        }
        foreach (var c in changes.Values)
        {
            if (c.Kind == EditorChangeKind.Deleted || c.Content == null || names.Contains(c.Path)
                || !c.Path.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = c.Path[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash >= 0) dirs.Add(rest[..slash]);
            else files.Add(new EditorTreeEntry(rest, c.Path, false, EditorPaths.Utf8Bytes(c.Content), StatusOf(c.Path)));
        }
        var changedDirs = changes.Keys.Concat(hidden).ToList();
        return dirs.Select(d =>
            {
                var full = prefix + d;
                var changed = changedDirs.Any(c => c.StartsWith(full + "/", StringComparison.Ordinal));
                return new EditorTreeEntry(d, full, true, null, changed ? EditorFileStatus.Modified : EditorFileStatus.Unchanged);
            })
            .Concat(files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>The tree edits that turn the base commit into this workspace (removes and writes).</summary>
    public List<TreeEdit> TreeEdits() => ToTreeEdits(changes.Values);

    internal static List<TreeEdit> ToTreeEdits(IEnumerable<EditorWorkspaceChange> changes)
    {
        var edits = new List<TreeEdit>();
        foreach (var c in changes.OrderBy(c => c.Path, StringComparer.Ordinal))
        {
            switch (c.Kind)
            {
                case EditorChangeKind.Deleted:
                    edits.Add(new TreeEdit(c.Path, null));
                    break;
                case EditorChangeKind.Renamed:
                    if (c.OriginalPath != null) edits.Add(new TreeEdit(c.OriginalPath, null));
                    edits.Add(new TreeEdit(c.Path, c.Content ?? "", c.OriginalPath));
                    break;
                default:
                    edits.Add(new TreeEdit(c.Path, c.Content ?? ""));
                    break;
            }
        }
        return edits;
    }

    private static EditorFile Missing(string path) => new(path, false, null, false, false, 0, EditorFileStatus.Deleted);
}
