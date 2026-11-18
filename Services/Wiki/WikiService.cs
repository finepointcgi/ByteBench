using GitBench.Data;
using GitBench.Services.Git;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Wiki;

/// <summary>A project's wiki as seen by the acting user.</summary>
public sealed record WikiInfo(int ProjectId, string ProjectKey, string ProjectName, bool Enabled, ProjectPermissions Perms, string RepoPath)
{
    public bool CanEdit => Enabled && Perms.Can(WikiService.EditPermission);
}

public sealed record WikiPageEntry(string Path, string Title);

/// <summary>A page at a revision: its Markdown, the blob id it was read from (the edit's base) and the last commit touching it.</summary>
public sealed record WikiPage(string Path, string Title, string Content, string BlobSha, CommitInfo? LastCommit);

/// <summary>The page changed (or appeared, or was deleted) since the editor opened it. Carries what's there now.</summary>
public sealed class WikiConflictException(string message, string? currentContent, string? currentSha) : InvalidOperationException(message)
{
    public string? CurrentContent { get; } = currentContent;
    public string? CurrentSha { get; } = currentSha;
}

/// <summary>
/// The project wiki, GitLab-style: a bare git repository <c>{KEY}/{key}.wiki.git</c> next to the project's code repositories
/// (cloneable over HTTP and SSH as <c>KEY/wiki.git</c> or <c>KEY/{key}.wiki.git</c>), with one Markdown file per page on
/// <see cref="Branch"/>. Every save is a commit authored by the user; edits name the blob they started from, and a page
/// that changed since is a <see cref="WikiConflictException"/> instead of a silent overwrite.
/// <para>Permissions: ViewProject reads, EditIssues (Developer+) writes, and the project's <see cref="Project.WikiEnabled"/>
/// must be on. Search reads <see cref="WikiPageIndex"/>, rebuilt after every save and push (<see cref="ReindexAsync"/>).</para>
/// </summary>
public class WikiService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, RepoStorage storage, ILogger<WikiService> log)
{
    public const string Branch = "main";
    public const Permission ReadPermission = Permission.ViewProject;
    public const Permission EditPermission = Permission.EditIssues;
    public const long MaxPageBytes = 1024 * 1024;
    public const long MaxUploadBytes = 10 * 1024 * 1024;
    private const int MaxIndexedChars = 100 * 1024;

    /// <summary>The wiki repository's slug in <c>{KEY}/{slug}.git</c>.</summary>
    public static string WikiSlug(string projectKey) => projectKey.ToLowerInvariant() + ".wiki";

    /// <summary>True when <paramref name="slug"/> names the wiki of <paramref name="projectKey"/> ("wiki" or "{key}.wiki").</summary>
    public static bool IsWikiSlug(string projectKey, string slug) =>
        slug.Equals("wiki", StringComparison.OrdinalIgnoreCase) || slug.Equals(WikiSlug(projectKey), StringComparison.OrdinalIgnoreCase);

    /// <summary>Repository slugs that would shadow a wiki remote, refused when creating or renaming repositories.</summary>
    public static bool IsReservedRepositorySlug(string slug) => slug == "wiki" || slug.EndsWith(".wiki", StringComparison.Ordinal);

    public string RepoPathFor(string projectKey) => storage.PathFor(projectKey, WikiSlug(projectKey));

    /// <summary>The wiki of project <paramref name="projectKey"/>, or null if the project doesn't exist or the user can't view it.</summary>
    public async Task<WikiInfo?> GetWikiAsync(string projectKey)
    {
        var key = projectKey.ToUpperInvariant();
        await using var db = await dbFactory.CreateDbContextAsync();
        var p = await db.Projects.AsNoTracking().Where(x => x.Key == key)
            .Select(x => new { x.Id, x.Key, x.Name, x.WikiEnabled }).FirstOrDefaultAsync();
        if (p == null) return null;
        var perms = await access.GetPermissionsAsync(p.Id);
        if (!perms.CanView) return null;
        return new WikiInfo(p.Id, p.Key, p.Name, p.WikiEnabled, perms, RepoPathFor(p.Key));
    }

    private static void RequireRead(WikiInfo w)
    {
        if (!w.Enabled) throw new ForbiddenException("The wiki is turned off for this project.");
    }

    private async Task RequireEditAsync(WikiInfo w)
    {
        RequireRead(w);
        await access.RequireAsync(w.ProjectId, EditPermission); // re-checked against the database, not the cached flags
    }

    /// <summary>Creates the empty bare repository (HEAD → <see cref="Branch"/>) if it isn't there yet.</summary>
    public static void EnsureRepository(string repoPath)
    {
        if (GitOps.Exists(repoPath)) return;
        try { GitOps.InitBare(repoPath, Branch); }
        catch (InvalidOperationException) when (GitOps.Exists(repoPath)) { } // created concurrently
    }

    // ---------- Reading ----------

    public Task<List<WikiPageEntry>> ListPagesAsync(WikiInfo w)
    {
        RequireRead(w);
        return Task.Run(() => ListPages(w.RepoPath));
    }

    private static List<WikiPageEntry> ListPages(string repoPath) =>
        !GitOps.Exists(repoPath) ? [] : GitOps.ListAllBlobs(repoPath, Branch)
            .Select(b => WikiPaths.PageOfFile(b.Path)).OfType<string>()
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => new WikiPageEntry(p, WikiPaths.TitleOf(p))).ToList();

    /// <summary>A page at <paramref name="rev"/> (default: the branch tip), or null when it doesn't exist there.</summary>
    public Task<WikiPage?> GetPageAsync(WikiInfo w, string pagePath, string? rev = null)
    {
        RequireRead(w);
        return Task.Run(() => GetPage(w.RepoPath, pagePath, rev));
    }

    private static WikiPage? GetPage(string repoPath, string pagePath, string? rev)
    {
        if (!GitOps.Exists(repoPath)) return null;
        var file = WikiPaths.FileOf(pagePath);
        var blob = GitOps.GetBlob(repoPath, rev ?? Branch, file);
        if (blob == null) return null;
        var last = GitOps.Log(repoPath, rev ?? Branch, file, 0, 1).Items.FirstOrDefault();
        return new WikiPage(pagePath, WikiPaths.TitleOf(pagePath), blob.Text ?? "", blob.Sha, last);
    }

    public Task<(List<CommitInfo> Items, bool HasMore)> HistoryAsync(WikiInfo w, string pagePath, int skip = 0, int take = 50)
    {
        RequireRead(w);
        return Task.Run(() => GitOps.Exists(w.RepoPath) ? GitOps.Log(w.RepoPath, Branch, WikiPaths.FileOf(pagePath), skip, take) : ([], false));
    }

    /// <summary>The page's diff between two commits (<paramref name="from"/> null: against the commit's parent).</summary>
    public Task<List<FileDiff>> DiffAsync(WikiInfo w, string pagePath, string? from, string to)
    {
        RequireRead(w);
        return Task.Run(() =>
        {
            if (!GitOps.Exists(w.RepoPath)) return [];
            var file = WikiPaths.FileOf(pagePath);
            if (from == null)
            {
                var commit = GitOps.GetCommit(w.RepoPath, to);
                if (commit == null) return [];
                if (commit.ParentShas.Count == 0)
                {
                    // Root commit: everything is added.
                    var text = GitOps.GetText(w.RepoPath, to, file) ?? "";
                    var lines = text.TrimEnd('\n').Split('\n');
                    return [new FileDiff(file, null, "added", lines.Length, 0, false, false,
                        [new DiffHunk($"@@ -0,0 +1,{lines.Length} @@", lines.Select((l, i) => new DiffLine(DiffLineKind.Added, null, i + 1, l)).ToList())])];
                }
                from = commit.ParentShas[0];
            }
            return GitOps.DiffFile(w.RepoPath, from, to, file);
        });
    }

    /// <summary>A file of the wiki repository (uploads), for the file endpoint. Null when missing or too large.</summary>
    public Task<byte[]?> GetFileAsync(WikiInfo w, string filePath)
    {
        RequireRead(w);
        return Task.Run(() => GitOps.Exists(w.RepoPath) ? GitOps.GetBlobBytes(w.RepoPath, Branch, filePath, MaxUploadBytes) : null);
    }

    // ---------- Writing ----------

    /// <summary>
    /// Creates or updates a page, optionally moving it. <paramref name="originalPath"/> is the page the editor opened (null for a
    /// new page) and <paramref name="baseSha"/> the blob it read; if the page changed since, nothing is written and a
    /// <see cref="WikiConflictException"/> is thrown. Returns the saved page path.
    /// </summary>
    public async Task<string> SavePageAsync(WikiInfo w, string? originalPath, string newPath, string content, string? message, string? baseSha)
    {
        await RequireEditAsync(w);
        var target = WikiPaths.Normalize(newPath) ?? throw new InvalidOperationException(
            "Page names may use letters, digits, spaces, '-', '_', '.', '(', ')' and '/' for folders (at most 8 levels).");
        var original = originalPath == null ? null : WikiPaths.Normalize(originalPath) ?? throw new InvalidOperationException("Invalid page path.");
        content = content.Replace("\r\n", "\n");
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        if (bytes.Length > MaxPageBytes) throw new InvalidOperationException("Pages can be at most 1 MB.");

        var me = await ActingUserAsync();
        var moving = original != null && !string.Equals(original, target, StringComparison.Ordinal);
        var expected = new Dictionary<string, string?>();
        var changes = new List<GitFileChange>();
        if (original == null) expected[WikiPaths.FileOf(target)] = null;
        else
        {
            expected[WikiPaths.FileOf(original)] = baseSha;
            if (moving)
            {
                if (!string.Equals(original, target, StringComparison.OrdinalIgnoreCase)) expected[WikiPaths.FileOf(target)] = null;
                changes.Add(new GitFileChange(WikiPaths.FileOf(original), null));
            }
        }
        changes.Add(new GitFileChange(WikiPaths.FileOf(target), bytes));
        var title = WikiPaths.TitleOf(target);
        var subject = string.IsNullOrWhiteSpace(message)
            ? original == null ? $"Create {title}" : moving ? $"Move {original} to {target}" : $"Update {title}"
            : message.Trim();

        await CommitAsync(w, changes, subject, me, expected, original ?? target);
        return target;
    }

    public async Task DeletePageAsync(WikiInfo w, string pagePath, string? baseSha, string? message = null)
    {
        await RequireEditAsync(w);
        var page = WikiPaths.Normalize(pagePath) ?? throw new InvalidOperationException("Invalid page path.");
        var me = await ActingUserAsync();
        var file = WikiPaths.FileOf(page);
        // Without a base version (API), delete whatever is there now; a missing page is still a conflict.
        baseSha ??= (await Task.Run(() => GetPage(w.RepoPath, page, null)))?.BlobSha ?? "";
        await CommitAsync(w, [new GitFileChange(file, null)],
            string.IsNullOrWhiteSpace(message) ? $"Delete {WikiPaths.TitleOf(page)}" : message.Trim(), me,
            new Dictionary<string, string?> { [file] = baseSha ?? "" }, page);
    }

    /// <summary>Restores the page's content from <paramref name="commitSha"/> as a new commit.</summary>
    public async Task RevertPageAsync(WikiInfo w, string pagePath, string commitSha)
    {
        await RequireEditAsync(w);
        var old = await Task.Run(() => GetPage(w.RepoPath, pagePath, commitSha))
                  ?? throw new InvalidOperationException("The page doesn't exist in that version.");
        var current = await Task.Run(() => GetPage(w.RepoPath, pagePath, null));
        var shortSha = commitSha[..Math.Min(7, commitSha.Length)];
        if (current == null)
            await SavePageAsync(w, null, pagePath, old.Content, $"Restore {old.Title} from {shortSha}", null);
        else
            await SavePageAsync(w, pagePath, pagePath, old.Content, $"Revert {old.Title} to {shortSha}", current.BlobSha);
    }

    /// <summary>Stores an image under <c>uploads/</c> (png, jpeg, gif or webp, checked by content). Returns its path in the wiki.</summary>
    public async Task<string> UploadAsync(WikiInfo w, string fileName, Stream content)
    {
        await RequireEditAsync(w);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await content.ReadAsync(buffer)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > MaxUploadBytes) throw new InvalidOperationException("Images can be at most 10 MB.");
        }
        var bytes = ms.ToArray();
        if (ImageType(bytes) == null) throw new InvalidOperationException("Only PNG, JPEG, GIF and WebP images can be uploaded to the wiki.");
        var path = $"{WikiPaths.UploadsFolder}/{Guid.NewGuid().ToString("N")[..8]}-{WikiPaths.UploadName(fileName)}";
        var me = await ActingUserAsync();
        await CommitAsync(w, [new GitFileChange(path, bytes)], $"Upload {WikiPaths.UploadName(fileName)}", me,
            new Dictionary<string, string?> { [path] = null }, null);
        return path;
    }

    /// <summary>The image type of <paramref name="bytes"/> (by magic number), or null.</summary>
    public static string? ImageType(ReadOnlySpan<byte> h)
    {
        if (h.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return "image/png";
        if (h.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])) return "image/jpeg";
        if (h.StartsWith("GIF87a"u8) || h.StartsWith("GIF89a"u8)) return "image/gif";
        if (h.Length >= 12 && h.StartsWith("RIFF"u8) && h[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }

    private async Task CommitAsync(WikiInfo w, IReadOnlyList<GitFileChange> changes, string message, User me,
        IReadOnlyDictionary<string, string?> expected, string? conflictPage)
    {
        try
        {
            await Task.Run(() =>
            {
                EnsureRepository(w.RepoPath);
                GitOps.CommitChanges(w.RepoPath, Branch, changes, message, RepositoryService.IdentityOf(me), expected);
            });
        }
        catch (GitConflictException ex)
        {
            var current = conflictPage == null ? null : await Task.Run(() => GetPage(w.RepoPath, conflictPage, null));
            var file = ex.Message;
            var what = conflictPage != null && file == WikiPaths.FileOf(conflictPage)
                ? current == null ? "This page was deleted since you opened it." : "Someone else changed this page since you opened it."
                : $"A page already exists at {WikiPaths.PageOfFile(file) ?? file}.";
            throw new WikiConflictException(what, current?.Content, current?.BlobSha);
        }
        log.LogInformation("Wiki {Key}: {Message} by {User}", w.ProjectKey, message, me.UserName);
        await ReindexAsync(w.ProjectId, w.ProjectKey);
    }

    private async Task<User> ActingUserAsync()
    {
        var id = await access.ResolveUserIdAsync() ?? throw new ForbiddenException("Sign in to edit the wiki.");
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Users.AsNoTracking().FirstAsync(u => u.Id == id);
    }

    // ---------- Settings & search ----------

    /// <summary>Turns the wiki on or off (Owner: ManageProjectSettings). The repository is kept either way.</summary>
    public async Task SetEnabledAsync(int projectId, bool enabled)
    {
        await access.RequireAsync(projectId, Permission.ManageProjectSettings);
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Projects.Where(p => p.Id == projectId).ExecuteUpdateAsync(s => s.SetProperty(p => p.WikiEnabled, enabled));
    }

    /// <summary>Rebuilds the search index of a project's wiki from the repository (after a save or a push). Trusted: no permission check.</summary>
    public async Task ReindexAsync(int projectId, string projectKey)
    {
        var repoPath = RepoPathFor(projectKey);
        var pages = await Task.Run(() =>
        {
            if (!GitOps.Exists(repoPath)) return [];
            return ListPages(repoPath).Select(p => (p.Path, p.Title, Text: GitOps.GetText(repoPath, Branch, WikiPaths.FileOf(p.Path)) ?? "")).ToList();
        });
        await using var db = await dbFactory.CreateDbContextAsync();
        var existing = await db.WikiPageIndex.Where(x => x.ProjectId == projectId).ToListAsync();
        var byPath = existing.ToDictionary(x => x.Path, StringComparer.Ordinal);
        var now = DateTime.UtcNow;
        foreach (var (path, title, text) in pages)
        {
            if (path.Length > 400) continue;
            var content = text.Length > MaxIndexedChars ? text[..MaxIndexedChars] : text;
            var t = title.Length > 200 ? title[..200] : title;
            if (byPath.Remove(path, out var row))
            {
                if (row.Content == content && row.Title == t) continue;
                row.Content = content;
                row.Title = t;
                row.UpdatedAt = now;
            }
            else db.WikiPageIndex.Add(new WikiPageIndex { ProjectId = projectId, Path = path, Title = t, Content = content, UpdatedAt = now });
        }
        db.WikiPageIndex.RemoveRange(byPath.Values);
        await db.SaveChangesAsync();
    }
}
