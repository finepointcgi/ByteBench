using GitBench.Data;
using GitBench.Services.Code;
using GitBench.Services.Git;
using GitBench.Services.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Editor;

/// <summary>
/// The web editor's workspaces (<see cref="EditorWorkspace"/>): one per user, repository and branch, holding uncommitted
/// changes in the database so they survive reloads. Reading needs <see cref="Permission.ViewProject"/>; editing and committing
/// need <see cref="Permission.PushCode"/> on a repository that isn't archived or a pull mirror, and commits follow the same
/// rules as a git push: branch protection (<see cref="BranchProtectionService"/>), then the push listeners
/// (<see cref="EditorPushNotifier"/>). Only branches are written; tags are never touched.
/// Every method takes the acting user's id explicitly and checks it owns the workspace.
/// </summary>
public class EditorWorkspaceService(
    IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, RepositoryService repos, BranchProtectionService protection,
    PullRequestService pullRequests, CodeSearchService codeSearch, EditorPushNotifier pushNotifier, Audit.AuditLog audit)
{
    public const int MaxCommitMessage = 5000;

    // ---------- Opening ----------

    /// <summary>
    /// Opens (creating if needed) the user's workspace for <paramref name="branch"/>. A workspace without changes follows the
    /// branch: its base moves to the current tip. Throws if the branch doesn't exist and there is no workspace for it.
    /// </summary>
    public async Task<EditorContext> OpenAsync(Repository repo, string branch, int userId)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ViewProject, userId);
        var tip = await repos.Git(repo, p => GitOps.Exists(p) && GitOps.IsValidBranchName(branch) ? GitOps.ResolveSha(p, "refs/heads/" + branch) : null);
        await using var db = await dbFactory.CreateDbContextAsync();
        var ws = await db.EditorWorkspaces.Include(w => w.Changes)
            .FirstOrDefaultAsync(w => w.UserId == userId && w.RepositoryId == repo.Id && w.Branch == branch);
        if (ws == null)
        {
            if (tip == null) throw new InvalidOperationException($"Branch '{branch}' doesn't exist in {repo.Name}.");
            ws = new EditorWorkspace { UserId = userId, RepositoryId = repo.Id, Branch = branch, BaseSha = tip };
            db.EditorWorkspaces.Add(ws);
            await db.SaveChangesAsync();
        }
        else if (tip != null && ws.BaseSha != tip && ws.Changes.Count == 0)
        {
            ws.BaseSha = tip;
            ws.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        return await ContextAsync(repo, ws, tip, userId);
    }

    /// <summary>The context of an existing workspace (permissions are re-evaluated).</summary>
    public async Task<EditorContext> GetContextAsync(int workspaceId, int userId)
    {
        var (ws, repo) = await LoadAsync(workspaceId, userId);
        var tip = await repos.Git(repo, p => GitOps.ResolveSha(p, "refs/heads/" + ws.Branch));
        return await ContextAsync(repo, ws, tip, userId);
    }

    private async Task<EditorContext> ContextAsync(Repository repo, EditorWorkspace ws, string? tip, int userId)
    {
        var readOnly = await ReadOnlyReasonAsync(repo, userId);
        string? protectionNote = null;
        if (readOnly == null)
        {
            try { await protection.EnsureAllowedAsync(repo, ws.Branch, RefChangeKind.Update, userId); }
            catch (BranchProtectedException ex) { protectionNote = ex.Message; }
        }
        var canPr = readOnly == null && await access.CanAsync(repo.ProjectId, Permission.CreatePullRequest, userId);
        return new EditorContext(ws.Id, repo, ws.Branch, ws.BaseSha, tip, readOnly == null, readOnly, protectionNote, canPr, userId, ws.Changes.Count);
    }

    /// <summary>Why the user can't edit in this repository (null when they can).</summary>
    public async Task<string?> ReadOnlyReasonAsync(Repository repo, int userId)
    {
        if (!await access.CanAsync(repo.ProjectId, Permission.PushCode, userId))
            return "You can browse this repository, but editing needs Developer access to the project.";
        await using var db = await dbFactory.CreateDbContextAsync();
        var current = await db.Repositories.AsNoTracking().Where(r => r.Id == repo.Id).Select(r => new { r.IsArchived }).FirstOrDefaultAsync();
        if (current?.IsArchived ?? repo.IsArchived) return "This repository is archived and read-only.";
        if (await Mirroring.MirrorService.IsPullMirrorAsync(db, repo.Id)) return Mirroring.MirrorService.PullMirrorReadOnlyMessage;
        return null;
    }

    // ---------- Reading ----------

    /// <summary>The workspace as a read-only view (checks the user owns it and can view the project).</summary>
    public async Task<WorkspaceSnapshot> GetSnapshotAsync(int workspaceId, int userId)
    {
        var (ws, repo) = await LoadAsync(workspaceId, userId);
        return new WorkspaceSnapshot(repos.PathOf(repo), repo, ws.Branch, ws.BaseSha, ws.Changes);
    }

    public async Task<List<EditorTreeEntry>> ListDirectoryAsync(int workspaceId, int userId, string? dir)
    {
        var snap = await GetSnapshotAsync(workspaceId, userId);
        var d = string.IsNullOrWhiteSpace(dir) ? "" : EditorPaths.Normalize(dir);
        return await Task.Run(() => snap.ListDirectory(d));
    }

    public async Task<List<string>> ListAllFilesAsync(int workspaceId, int userId, int max = 20_000)
    {
        var snap = await GetSnapshotAsync(workspaceId, userId);
        return await Task.Run(() => snap.ListFiles(max).Select(f => f.Path).ToList());
    }

    public async Task<EditorFile> ReadFileAsync(int workspaceId, int userId, string path)
    {
        var snap = await GetSnapshotAsync(workspaceId, userId);
        var p = EditorPaths.Normalize(path);
        return await Task.Run(() => snap.Read(p));
    }

    /// <summary>Every working change with its base text, for the Source Control panel and its diff view.</summary>
    public async Task<List<EditorChange>> GetChangesAsync(int workspaceId, int userId)
    {
        var snap = await GetSnapshotAsync(workspaceId, userId);
        return await Task.Run(() => snap.Changes.OrderBy(c => c.Path, StringComparer.Ordinal)
            .Select(c => new EditorChange(c.Path, c.OriginalPath, c.Kind,
                c.Kind == EditorChangeKind.Added ? null : snap.BaseText(c.OriginalPath ?? c.Path), c.Content))
            .ToList());
    }

    /// <summary>
    /// Searches the workspace: <c>git grep</c> at the base commit (<see cref="CodeSearchService"/>) for unchanged files, plus
    /// the working copies of changed files.
    /// </summary>
    public async Task<(List<EditorSearchHit> Hits, string? Error)> SearchAsync(int workspaceId, int userId, string query,
        bool ignoreCase = true, bool regex = false, int max = 200)
    {
        var snap = await GetSnapshotAsync(workspaceId, userId);
        access.ActAs(userId); // CodeSearchService checks the acting user
        var result = await codeSearch.SearchRepositoryAsync(snap.Repository, snap.BaseSha, new CodeSearchOptions(query, ignoreCase, regex), max);
        if (result.Error != null) return ([], result.Error);
        var touched = snap.Changes.Select(c => c.Path).Concat(snap.Changes.Where(c => c.OriginalPath != null).Select(c => c.OriginalPath!))
            .ToHashSet(StringComparer.Ordinal);
        var hits = result.Files.Where(f => !touched.Contains(f.Path))
            .SelectMany(f => f.Snippets.SelectMany(s => s).Where(l => l.IsMatch).Select(l => new EditorSearchHit(f.Path, l.Number, l.Text)))
            .ToList();
        var matcher = new System.Text.RegularExpressions.Regex(regex ? query : System.Text.RegularExpressions.Regex.Escape(query),
            (ignoreCase ? System.Text.RegularExpressions.RegexOptions.IgnoreCase : 0) | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(200));
        foreach (var c in snap.Changes.Where(c => c.Content != null).OrderBy(c => c.Path, StringComparer.Ordinal))
        {
            var lines = c.Content!.Split('\n');
            for (var i = 0; i < lines.Length && hits.Count < max; i++)
            {
                bool match;
                try { match = matcher.IsMatch(lines[i]); } catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { match = false; }
                if (match) hits.Add(new EditorSearchHit(c.Path, i + 1, lines[i].Length > 500 ? lines[i][..500] + "…" : lines[i]));
            }
        }
        return (hits.OrderBy(h => h.Path, StringComparer.Ordinal).ThenBy(h => h.Line).Take(max).ToList(), null);
    }

    // ---------- Editing ----------

    /// <summary>Saves the working copy of a file (creating it if it doesn't exist). Saving the base content drops the change.</summary>
    public async Task SaveFileAsync(int workspaceId, int userId, string path, string content)
    {
        var p = EditorPaths.Normalize(path);
        EditorPaths.EnsureEditableSize(content);
        content = content.Replace("\r\n", "\n");
        await MutateAsync(workspaceId, userId, (db, ws, snap) =>
        {
            var change = ws.Changes.FirstOrDefault(c => c.Path == p);
            var baseFile = BaseFile(snap, p);
            var hidden = snap.IsHiddenByRename(p); // the base file moved away: p is free
            if (change is { Kind: EditorChangeKind.Added or EditorChangeKind.Renamed })
            {
                Put(db, ws, p, change.Kind, content, change.OriginalPath);
                return;
            }
            if (baseFile == null) { Put(db, ws, p, EditorChangeKind.Added, content); return; }
            if (change == null && !hidden && baseFile.Text == null)
                throw new InvalidOperationException($"'{p}' is a binary or large file and can't be edited here.");
            if (!hidden && baseFile.Text == content) Drop(db, ws, p);
            else Put(db, ws, p, EditorChangeKind.Modified, content);
        });
    }

    /// <summary>Creates an empty (or given) file; refused if a file already exists there.</summary>
    public async Task CreateFileAsync(int workspaceId, int userId, string path, string content = "")
    {
        var p = EditorPaths.Normalize(path);
        var snap = await GetSnapshotAsync(workspaceId, userId);
        if (await Task.Run(() => snap.Exists(p))) throw new InvalidOperationException($"'{p}' already exists.");
        if (await Task.Run(() => snap.ListFiles().Any(f => f.Path.StartsWith(p + "/", StringComparison.Ordinal))))
            throw new InvalidOperationException($"'{p}' is a folder.");
        await SaveFileAsync(workspaceId, userId, p, content);
    }

    public async Task DeleteFileAsync(int workspaceId, int userId, string path)
    {
        var p = EditorPaths.Normalize(path);
        await MutateAsync(workspaceId, userId, (db, ws, snap) =>
        {
            if (!snap.Exists(p)) throw new InvalidOperationException($"'{p}' doesn't exist.");
            var change = ws.Changes.FirstOrDefault(c => c.Path == p);
            if (change is { Kind: EditorChangeKind.Renamed, OriginalPath: { } origin } && ws.Changes.All(c => c.Path != origin))
                Put(db, ws, origin, EditorChangeKind.Deleted, null); // deleting a renamed file deletes the original
            if (BaseFile(snap, p) != null && !snap.IsHiddenByRename(p)) Put(db, ws, p, EditorChangeKind.Deleted, null);
            else Drop(db, ws, p);
        });
    }

    /// <summary>Renames (moves) a text file; refused if the target exists.</summary>
    public async Task RenameFileAsync(int workspaceId, int userId, string from, string to)
    {
        var src = EditorPaths.Normalize(from);
        var dst = EditorPaths.Normalize(to);
        if (src == dst) return;
        await MutateAsync(workspaceId, userId, (db, ws, snap) =>
        {
            var file = snap.Read(src);
            if (!file.Exists) throw new InvalidOperationException($"'{src}' doesn't exist.");
            if (file.Text == null) throw new InvalidOperationException("Binary and large files can't be renamed in the web editor.");
            if (snap.Exists(dst)) throw new InvalidOperationException($"'{dst}' already exists.");
            var change = ws.Changes.FirstOrDefault(c => c.Path == src);
            var origin = change?.Kind switch
            {
                EditorChangeKind.Renamed => change.OriginalPath,
                EditorChangeKind.Added => null,
                _ => BaseFile(snap, src) != null && !snap.IsHiddenByRename(src) ? src : null,
            };
            Drop(db, ws, src); // the rename below hides the original base file
            if (origin == null) Put(db, ws, dst, EditorChangeKind.Added, file.Text);
            else if (origin == dst)
            {
                if (BaseFile(snap, dst)?.Text != file.Text) Put(db, ws, dst, EditorChangeKind.Modified, file.Text);
                else Drop(db, ws, dst);
            }
            else Put(db, ws, dst, EditorChangeKind.Renamed, file.Text, origin);
        });
    }

    /// <summary>Drops the working change of one file (a rename is undone: the original comes back).</summary>
    public async Task DiscardAsync(int workspaceId, int userId, string path)
    {
        var p = EditorPaths.Normalize(path);
        await MutateAsync(workspaceId, userId, (db, ws, _) =>
        {
            if (ws.Changes.All(c => c.Path != p)) throw new InvalidOperationException($"'{p}' has no changes.");
            Drop(db, ws, p);
        });
    }

    public async Task DiscardAllAsync(int workspaceId, int userId) =>
        await MutateAsync(workspaceId, userId, (db, ws, _) =>
        {
            foreach (var p in ws.Changes.Select(c => c.Path).ToList()) Drop(db, ws, p);
        });

    // ---------- Branch moved: status and rebase ----------

    public async Task<EditorStatus> GetStatusAsync(int workspaceId, int userId)
    {
        var snap = await GetSnapshotAsync(workspaceId, userId);
        return await Task.Run(() => Status(snap));
    }

    private static EditorStatus Status(WorkspaceSnapshot snap)
    {
        var tip = GitOps.ResolveSha(snap.RepoPath, "refs/heads/" + snap.Branch);
        if (tip == null || tip == snap.BaseSha || snap.Changes.Count == 0) return new EditorStatus(snap.BaseSha, tip, [], []);
        var touched = Touched(snap.Changes);
        var overlapping = GitOps.ChangedFiles(snap.RepoPath, snap.BaseSha, tip, touched);
        IReadOnlyList<string> conflicts = overlapping.Count == 0 ? []
            : GitOps.MergeTreeEdits(snap.RepoPath, snap.BaseSha, tip, snap.TreeEdits()).Conflicts;
        return new EditorStatus(snap.BaseSha, tip, overlapping, conflicts);
    }

    /// <summary>
    /// Moves the workspace onto the branch's current tip, merging each changed file three-way with what changed on the branch.
    /// Conflicted files need a <paramref name="resolutions"/> entry (keep my version, or take the branch's and drop mine);
    /// otherwise nothing changes and the conflicts are returned.
    /// </summary>
    public async Task<EditorStatus> RebaseAsync(int workspaceId, int userId, IReadOnlyDictionary<string, ConflictResolution>? resolutions = null)
    {
        await RequireEditAsync(workspaceId, userId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var (ws, repo) = await LoadAsync(db, workspaceId, userId);
        var path = repos.PathOf(repo);
        var snap = new WorkspaceSnapshot(path, repo, ws.Branch, ws.BaseSha, ws.Changes);
        var tip = await repos.Git(repo, p => GitOps.ResolveSha(p, "refs/heads/" + ws.Branch))
                  ?? throw new InvalidOperationException($"Branch '{ws.Branch}' no longer exists. Commit your changes to a new branch instead.");
        if (tip == ws.BaseSha) return new EditorStatus(ws.BaseSha, tip, [], []);

        var edits = snap.TreeEdits();
        var merge = await Task.Run(() => GitOps.MergeTreeEdits(path, ws.BaseSha, tip, edits));
        resolutions ??= new Dictionary<string, ConflictResolution>();
        var unresolved = merge.Conflicts.Where(c => !resolutions.ContainsKey(c)).ToList();
        if (unresolved.Count > 0)
        {
            var overlapping = await Task.Run(() => GitOps.ChangedFiles(path, ws.BaseSha, tip, Touched(ws.Changes)));
            return new EditorStatus(ws.BaseSha, tip, overlapping, merge.Conflicts);
        }

        // What each edited path should contain on top of the new tip.
        var mine = edits.GroupBy(e => e.Path).ToDictionary(g => g.Key, g => g.Last().Content, StringComparer.Ordinal);
        var desired = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (p, content) in merge.Merged) desired[p] = content;
        foreach (var c in merge.Conflicts)
            if (resolutions[c] == ConflictResolution.KeepMine) desired[c] = mine.GetValueOrDefault(c);

        var renames = ws.Changes.Where(c => c.Kind == EditorChangeKind.Renamed && c.OriginalPath != null)
            .ToDictionary(c => c.Path, c => c.OriginalPath!, StringComparer.Ordinal);
        var newChanges = await Task.Run(() =>
        {
            var list = new List<EditorWorkspaceChange>();
            foreach (var (p, content) in desired.OrderBy(d => d.Key, StringComparer.Ordinal))
            {
                var theirs = GitOps.GetBlob(path, tip, p);
                if (content == null)
                {
                    if (theirs != null) list.Add(new EditorWorkspaceChange { Path = p, Kind = EditorChangeKind.Deleted });
                    continue;
                }
                if (theirs?.Text == content) continue;
                list.Add(new EditorWorkspaceChange { Path = p, Kind = theirs == null ? EditorChangeKind.Added : EditorChangeKind.Modified, Content = content });
            }
            // Keep renames as renames when both halves survived.
            foreach (var added in list.Where(c => c.Kind == EditorChangeKind.Added && renames.ContainsKey(c.Path)).ToList())
            {
                var origin = renames[added.Path];
                if (list.FirstOrDefault(c => c.Path == origin && c.Kind == EditorChangeKind.Deleted) is { } deleted)
                {
                    list.Remove(deleted);
                    added.Kind = EditorChangeKind.Renamed;
                    added.OriginalPath = origin;
                }
            }
            return list;
        });

        foreach (var p in ws.Changes.Select(c => c.Path).Where(p => newChanges.All(n => n.Path != p)).ToList()) Drop(db, ws, p);
        foreach (var c in newChanges) Put(db, ws, c.Path, c.Kind, c.Content, c.OriginalPath);
        ws.BaseSha = tip;
        ws.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return new EditorStatus(tip, tip, [], []);
    }

    // ---------- Committing ----------

    /// <summary>
    /// Commits every working change to the workspace's branch (author: the user) and runs the push listeners. Refused like a
    /// push would be: PushCode, archived/mirror, branch protection; and when the branch changed the same files since the base
    /// (rebase the workspace first).
    /// </summary>
    public async Task<EditorCommitResult> CommitAsync(int workspaceId, int userId, string message)
    {
        message = CleanMessage(message);
        await RequireEditAsync(workspaceId, userId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var (ws, repo) = await LoadAsync(db, workspaceId, userId);
        if (ws.Changes.Count == 0) throw new InvalidOperationException("There are no changes to commit.");
        await protection.EnsureAllowedAsync(repo, ws.Branch, RefChangeKind.Update, userId);
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId);
        var path = repos.PathOf(repo);
        var snap = new WorkspaceSnapshot(path, repo, ws.Branch, ws.BaseSha, ws.Changes);

        var (before, sha) = await Task.Run(() =>
        {
            var status = Status(snap);
            if (status.BranchMissing) throw new InvalidOperationException($"Branch '{ws.Branch}' no longer exists. Commit to a new branch instead.");
            if (status.NeedsRebase)
                throw new GitConflictException($"'{ws.Branch}' has new commits that change {string.Join(", ", status.Overlapping)}. Update your workspace (rebase) first.");
            var tip = status.TipSha!;
            return (tip, GitOps.CommitTreeEdits(path, ws.Branch, tip, null, snap.TreeEdits(), message, RepositoryService.IdentityOf(user)));
        });

        db.EditorWorkspaceChanges.RemoveRange(ws.Changes);
        ws.BaseSha = sha;
        ws.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync("editor.commit", "repository", repo.Id, repo.ProjectId, new { branch = ws.Branch, sha }, userId);
        await pushNotifier.NotifyAsync(repo.Id, userId, ws.Branch, before, sha);
        return new EditorCommitResult(sha, ws.Branch, null);
    }

    /// <summary>
    /// Commits every working change to a new branch made from the workspace's base, runs the push listeners and (with
    /// <paramref name="pullRequestTitle"/>) opens a pull request into the workspace's branch. The workspace is emptied.
    /// </summary>
    public async Task<EditorCommitResult> CommitToNewBranchAsync(int workspaceId, int userId, string newBranch, string message,
        string? pullRequestTitle, string? pullRequestDescription = null)
    {
        message = CleanMessage(message);
        newBranch = (newBranch ?? "").Trim();
        if (!GitOps.IsValidBranchName(newBranch)) throw new InvalidOperationException($"'{newBranch}' is not a valid branch name.");
        await RequireEditAsync(workspaceId, userId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var (ws, repo) = await LoadAsync(db, workspaceId, userId);
        if (ws.Changes.Count == 0) throw new InvalidOperationException("There are no changes to commit.");
        if (newBranch == ws.Branch) throw new InvalidOperationException("Choose a branch name different from the current branch.");
        await protection.EnsureAllowedAsync(repo, newBranch, RefChangeKind.Create, userId);
        if (pullRequestTitle != null) await access.RequireAsync(repo.ProjectId, Permission.CreatePullRequest, userId);
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId);
        var path = repos.PathOf(repo);
        var edits = WorkspaceSnapshot.ToTreeEdits(ws.Changes);
        var sha = await Task.Run(() =>
        {
            if (GitOps.BranchExists(path, newBranch)) throw new InvalidOperationException($"Branch '{newBranch}' already exists.");
            return GitOps.CommitTreeEdits(path, newBranch, null, ws.BaseSha, edits, message, RepositoryService.IdentityOf(user));
        });

        db.EditorWorkspaceChanges.RemoveRange(ws.Changes);
        ws.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync("editor.commit", "repository", repo.Id, repo.ProjectId, new { branch = newBranch, sha, created = true }, userId);
        await pushNotifier.NotifyAsync(repo.Id, userId, newBranch, null, sha);

        PullRequest? pr = null;
        if (pullRequestTitle != null)
        {
            var target = await repos.Git(repo, p => GitOps.BranchExists(p, ws.Branch)) ? ws.Branch : repo.DefaultBranch;
            pr = await pullRequests.CreateAsync(repo, string.IsNullOrWhiteSpace(pullRequestTitle) ? FirstLine(message) : pullRequestTitle,
                pullRequestDescription, newBranch, target, userId);
            pr.Repository ??= repo;
        }
        return new EditorCommitResult(sha, newBranch, pr);
    }

    // ---------- Helpers ----------

    private async Task RequireEditAsync(int workspaceId, int userId)
    {
        var (_, repo) = await LoadAsync(workspaceId, userId);
        await access.RequireAsync(repo.ProjectId, Permission.PushCode, userId);
        if (await ReadOnlyReasonAsync(repo, userId) is { } why) throw new ForbiddenException(why);
    }

    private async Task MutateAsync(int workspaceId, int userId, Action<AppDbContext, EditorWorkspace, WorkspaceSnapshot> change)
    {
        await RequireEditAsync(workspaceId, userId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var (ws, repo) = await LoadAsync(db, workspaceId, userId);
        var snap = new WorkspaceSnapshot(repos.PathOf(repo), repo, ws.Branch, ws.BaseSha, ws.Changes);
        await Task.Run(() => change(db, ws, snap));
        ws.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task<(EditorWorkspace, Repository)> LoadAsync(int workspaceId, int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await LoadAsync(db, workspaceId, userId);
    }

    private async Task<(EditorWorkspace, Repository)> LoadAsync(AppDbContext db, int workspaceId, int userId)
    {
        var ws = await db.EditorWorkspaces.Include(w => w.Changes).Include(w => w.Repository).ThenInclude(r => r.Project)
            .FirstOrDefaultAsync(w => w.Id == workspaceId && w.UserId == userId)
            ?? throw new InvalidOperationException("Workspace not found.");
        await access.RequireAsync(ws.Repository.ProjectId, Permission.ViewProject, userId);
        return (ws, ws.Repository);
    }

    private static EditorFile? BaseFile(WorkspaceSnapshot snap, string path)
    {
        var blob = GitOps.GetBlob(snap.RepoPath, snap.BaseSha, path);
        return blob == null ? null : new EditorFile(path, true, blob.Text, blob.IsBinary, blob.TooLarge, blob.Size, EditorFileStatus.Unchanged);
    }

    /// <summary>Sets the change at <paramref name="path"/>, updating the tracked row if there is one (never delete + insert).</summary>
    private static void Put(AppDbContext db, EditorWorkspace ws, string path, EditorChangeKind kind, string? content, string? originalPath = null)
    {
        var c = ws.Changes.FirstOrDefault(x => x.Path == path);
        if (c == null)
        {
            c = new EditorWorkspaceChange { WorkspaceId = ws.Id, Path = path };
            ws.Changes.Add(c);
            db.EditorWorkspaceChanges.Add(c);
        }
        c.Kind = kind;
        c.Content = content;
        c.OriginalPath = kind == EditorChangeKind.Renamed ? originalPath : null;
        c.UpdatedAt = DateTime.UtcNow;
    }

    private static void Drop(AppDbContext db, EditorWorkspace ws, string path)
    {
        if (ws.Changes.FirstOrDefault(x => x.Path == path) is not { } c) return;
        ws.Changes.Remove(c);
        db.EditorWorkspaceChanges.Remove(c);
    }

    private static List<string> Touched(IEnumerable<EditorWorkspaceChange> changes) =>
        changes.Select(c => c.Path).Concat(changes.Where(c => c.OriginalPath != null).Select(c => c.OriginalPath!)).Distinct().ToList();

    private static string CleanMessage(string? message)
    {
        var m = (message ?? "").Replace("\r\n", "\n").Trim();
        if (m.Length == 0) throw new InvalidOperationException("Enter a commit message.");
        if (m.Length > MaxCommitMessage) throw new InvalidOperationException($"The commit message is too long (max {MaxCommitMessage} characters).");
        return m;
    }

    private static string FirstLine(string s) => s.Split('\n')[0].Trim() is { Length: > 0 } l ? (l.Length > 200 ? l[..200] : l) : "Changes from the web editor";
}

/// <summary>
/// Runs the <see cref="IGitPushListener"/>s after a web editor commit exactly as a git push does: in a fresh scope acting as
/// the committer, with <see cref="PushCapture"/> holding the ref update, through <see cref="GitRepoLookup.NotifyPushListenersAsync"/>
/// (CI, webhooks, smart commits, automation, stale approvals, AI review…).
/// </summary>
public class EditorPushNotifier(IServiceScopeFactory scopes, ILogger<EditorPushNotifier> log)
{
    public async Task NotifyAsync(int repositoryId, int userId, string branch, string? before, string after)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ProjectAccess>().ActAs(userId);
        scope.ServiceProvider.GetRequiredService<PushCapture>().SetRefs([WebhookPushEvents.Branch(branch, before, after)]);
        log.LogInformation("Web editor commit to repository {RepositoryId} branch {Branch} by user {UserId}", repositoryId, branch, userId);
        await GitRepoLookup.NotifyPushListenersAsync(scope.ServiceProvider, log, repositoryId, userId);
    }
}
