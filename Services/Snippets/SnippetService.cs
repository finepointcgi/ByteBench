using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Snippets;

public sealed record SnippetFileInput(string FileName, string Content);

public sealed record SnippetInput(string Title, string? Description, SnippetVisibility Visibility, IReadOnlyList<SnippetFileInput> Files);

/// <summary>A snippet in a list: who wrote it, where it lives and its current files' names.</summary>
public sealed record SnippetSummary(Snippet Snippet, IReadOnlyList<string> FileNames)
{
    public string Url => SnippetService.Url(Snippet.Id);
}

/// <summary>A snippet at one revision, with what the acting user may do with it.</summary>
public sealed record SnippetView(Snippet Snippet, SnippetRevision Revision, IReadOnlyList<SnippetRevision> Revisions,
    IReadOnlyList<SnippetComment> Comments, bool CanEdit, bool CanDelete, bool CanComment, int? ViewerId, bool ViewerIsAdmin)
{
    public bool IsCurrent => Revision.Number == Snippet.CurrentRevision;
    public bool CanDeleteComment(SnippetComment c) => ViewerIsAdmin || c.AuthorId == ViewerId || Snippet.AuthorId == ViewerId;
}

/// <summary>
/// Snippets (GitLab snippets / gists). Stored in the database, not as git repositories: a snippet is small (≤ 10 files,
/// 512 KB each), lists filter and sort by visibility, project and author in SQL, and each save of the files adds a
/// <see cref="SnippetRevision"/> holding a full copy of them, which is all the history a snippet needs. Title, description
/// and visibility are plain metadata, changed in place.
/// <para><b>Visibility</b> (<see cref="CanView"/>): Private, only the author; Project (project snippets), everyone who can
/// view the project; Public (personal snippets), every signed-in user. Site admins see everything. Anyone who can see a
/// snippet can comment on it. Only the author (or an admin) edits; the author, an admin or a project Maintainer+ deletes.
/// Project snippets need Developer+ in the project to create.</para>
/// </summary>
public class SnippetService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, Audit.AuditLog audit)
{
    public const int MaxFiles = 10;
    public const int MaxFileBytes = 512 * 1024;
    public const int MaxTotalBytes = 1024 * 1024;

    public static string Url(int id) => $"/snippets/{id}";
    public static string RawUrl(int id, string fileName, int? revision = null) =>
        $"/snippets/{id}/raw/{Uri.EscapeDataString(fileName)}" + (revision is { } r ? $"?rev={r}" : "");
    public static string EmbedUrl(int id) => $"/snippets/{id}/embed";

    /// <summary>
    /// The visibility rule. <paramref name="canViewProject"/> is whether the user can view the snippet's project (ignored
    /// for personal snippets).
    /// </summary>
    public static bool CanView(Snippet s, int? userId, bool isAdmin, bool canViewProject)
    {
        if (userId == null) return false;
        if (isAdmin || s.AuthorId == userId) return true;
        return s.Visibility switch
        {
            SnippetVisibility.Public => s.ProjectId == null,
            SnippetVisibility.Project => s.ProjectId != null && canViewProject,
            _ => false,
        };
    }

    /// <summary>The visibilities a snippet may have: Private/Public for personal ones, Private/Project for project ones.</summary>
    public static SnippetVisibility[] AllowedVisibilities(bool isProjectSnippet) =>
        isProjectSnippet ? [SnippetVisibility.Private, SnippetVisibility.Project] : [SnippetVisibility.Private, SnippetVisibility.Public];

    public static string Describe(SnippetVisibility v) => v switch
    {
        SnippetVisibility.Private => "Private: only you",
        SnippetVisibility.Project => "Project: everyone who can view the project",
        _ => "Public: every signed-in user",
    };

    private async Task<(int? Uid, bool Admin)> WhoAsync() => (await access.ResolveUserIdAsync(), await access.IsAdminAsync());

    private async Task<bool> CanViewAsync(Snippet s, int? uid, bool admin) =>
        CanView(s, uid, admin, s.ProjectId is { } pid && uid != null && await access.CanAsync(pid, Permission.ViewProject, uid));

    // ---------- Queries ----------

    /// <summary>The snippet at <paramref name="revision"/> (default: current), or null if it doesn't exist or the user can't see it.</summary>
    public async Task<SnippetView?> GetAsync(int id, int? revision = null)
    {
        var (uid, admin) = await WhoAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var s = await db.Snippets.AsNoTracking().Include(x => x.Author).Include(x => x.Project).FirstOrDefaultAsync(x => x.Id == id);
        if (s == null || !await CanViewAsync(s, uid, admin)) return null;
        var number = revision ?? s.CurrentRevision;
        var rev = await db.SnippetRevisions.AsNoTracking().Include(r => r.Author).Include(r => r.Files.OrderBy(f => f.Position))
            .FirstOrDefaultAsync(r => r.SnippetId == id && r.Number == number);
        if (rev == null) return null;
        var revisions = await db.SnippetRevisions.AsNoTracking().Include(r => r.Author).Where(r => r.SnippetId == id)
            .OrderByDescending(r => r.Number).ToListAsync();
        var comments = await db.SnippetComments.AsNoTracking().Include(c => c.Author).Where(c => c.SnippetId == id)
            .OrderBy(c => c.CreatedAt).ToListAsync();
        var canEdit = admin || s.AuthorId == uid;
        var canDelete = canEdit || (s.ProjectId is { } pid && await access.GetRoleAsync(pid, uid) >= ProjectRole.Maintainer);
        return new SnippetView(s, rev, revisions, comments, canEdit, canDelete, uid != null, uid, admin);
    }

    /// <summary>The acting user's own snippets, newest first.</summary>
    public async Task<List<SnippetSummary>> ListMineAsync()
    {
        var uid = await access.ResolveUserIdAsync();
        if (uid == null) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        return await SummariesAsync(db, db.Snippets.Where(s => s.AuthorId == uid));
    }

    /// <summary>A project's snippets the acting user can see.</summary>
    public async Task<List<SnippetSummary>> ListForProjectAsync(int projectId)
    {
        var (uid, admin) = await WhoAsync();
        if (uid == null || !await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        return await SummariesAsync(db, db.Snippets.Where(s => s.ProjectId == projectId
            && (admin || s.AuthorId == uid || s.Visibility == SnippetVisibility.Project)));
    }

    /// <summary>Shared snippets: public personal ones and project ones in projects the user can view.</summary>
    public async Task<List<SnippetSummary>> ExploreAsync(int take = 100)
    {
        var uid = await access.ResolveUserIdAsync();
        if (uid == null) return [];
        var visible = await access.VisibleProjectIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await SummariesAsync(db, db.Snippets.Where(s =>
            (s.ProjectId == null && s.Visibility == SnippetVisibility.Public)
            || (s.ProjectId != null && s.Visibility == SnippetVisibility.Project && visible.Contains(s.ProjectId.Value))), take);
    }

    private static async Task<List<SnippetSummary>> SummariesAsync(AppDbContext db, IQueryable<Snippet> query, int take = 200)
    {
        var list = await query.AsNoTracking().Include(s => s.Author).Include(s => s.Project)
            .OrderByDescending(s => s.UpdatedAt).Take(take).ToListAsync();
        var ids = list.Select(s => s.Id).ToList();
        var files = await (
            from s in db.Snippets
            join r in db.SnippetRevisions on new { SnippetId = s.Id, Number = s.CurrentRevision } equals new { r.SnippetId, r.Number }
            join f in db.SnippetFiles on r.Id equals f.RevisionId
            where ids.Contains(s.Id)
            select new { r.SnippetId, f.Position, f.FileName }).AsNoTracking().ToListAsync();
        var byId = files.GroupBy(f => f.SnippetId).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.OrderBy(f => f.Position).Select(f => f.FileName).ToList());
        return list.Select(s => new SnippetSummary(s, byId.GetValueOrDefault(s.Id) ?? [])).ToList();
    }

    /// <summary>One file of a snippet (current revision unless given), or null.</summary>
    public async Task<SnippetFile?> GetFileAsync(int id, string fileName, int? revision = null)
    {
        var view = await GetAsync(id, revision);
        return view?.Revision.Files.FirstOrDefault(f => f.FileName == fileName);
    }

    // ---------- Writes ----------

    public async Task<Snippet> CreateAsync(SnippetInput input, int? projectId = null)
    {
        var uid = await access.ResolveUserIdAsync() ?? throw new ForbiddenException("Sign in to create snippets.");
        if (projectId is { } pid) await access.RequireRoleAsync(pid, ProjectRole.Developer, "create snippets");
        var files = Validate(input, projectId != null);
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        var s = new Snippet
        {
            Title = input.Title.Trim(), Description = Clean(input.Description), Visibility = input.Visibility,
            AuthorId = uid, ProjectId = projectId, CurrentRevision = 1, CreatedAt = now, UpdatedAt = now,
        };
        s.Revisions.Add(new SnippetRevision { Number = 1, AuthorId = uid, CreatedAt = now, Files = files });
        db.Snippets.Add(s);
        await db.SaveChangesAsync();
        await audit.RecordAsync("snippet.created", "snippet", s.Id, projectId, new { s.Title, s.Visibility }, uid);
        return s;
    }

    /// <summary>Updates title, description and visibility, and adds a revision when the files changed. Author or admin only.</summary>
    public async Task<Snippet> UpdateAsync(int id, SnippetInput input)
    {
        var (uid, admin) = await WhoAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var s = await db.Snippets.FirstOrDefaultAsync(x => x.Id == id);
        if (s == null || !await CanViewAsync(s, uid, admin)) throw new ForbiddenException("Snippet not found.");
        if (!admin && s.AuthorId != uid) throw new ForbiddenException("Only the snippet's author can edit it.");
        var files = Validate(input, s.ProjectId != null);
        var current = await db.SnippetFiles.AsNoTracking()
            .Where(f => db.SnippetRevisions.Any(r => r.Id == f.RevisionId && r.SnippetId == id && r.Number == s.CurrentRevision))
            .OrderBy(f => f.Position).Select(f => new { f.FileName, f.Content }).ToListAsync();
        var changed = current.Count != files.Count || current.Zip(files).Any(p => p.First.FileName != p.Second.FileName || p.First.Content != p.Second.Content);

        var now = DateTime.UtcNow;
        s.Title = input.Title.Trim();
        s.Description = Clean(input.Description);
        s.Visibility = input.Visibility;
        s.UpdatedAt = now;
        if (changed)
        {
            s.CurrentRevision++;
            db.SnippetRevisions.Add(new SnippetRevision { SnippetId = s.Id, Number = s.CurrentRevision, AuthorId = uid!.Value, CreatedAt = now, Files = files });
        }
        await db.SaveChangesAsync();
        await audit.RecordAsync("snippet.updated", "snippet", s.Id, s.ProjectId, new { s.Title, s.Visibility, Revision = s.CurrentRevision }, uid);
        return s;
    }

    public async Task DeleteAsync(int id)
    {
        var view = await GetAsync(id) ?? throw new ForbiddenException("Snippet not found.");
        if (!view.CanDelete) throw new ForbiddenException("You can't delete this snippet.");
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Snippets.Where(s => s.Id == id).ExecuteDeleteAsync();
        await audit.RecordAsync("snippet.deleted", "snippet", id, view.Snippet.ProjectId, new { view.Snippet.Title }, view.ViewerId);
    }

    public async Task<SnippetComment> AddCommentAsync(int id, string body)
    {
        var view = await GetAsync(id) ?? throw new ForbiddenException("Snippet not found.");
        body = (body ?? "").Trim();
        if (body.Length == 0) throw new InvalidOperationException("Write a comment first.");
        if (body.Length > 20000) throw new InvalidOperationException("Comments can be at most 20,000 characters.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var c = new SnippetComment { SnippetId = id, AuthorId = view.ViewerId!.Value, Body = body };
        db.SnippetComments.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    public async Task DeleteCommentAsync(int id, int commentId)
    {
        var view = await GetAsync(id) ?? throw new ForbiddenException("Snippet not found.");
        var c = view.Comments.FirstOrDefault(x => x.Id == commentId) ?? throw new InvalidOperationException("Comment not found.");
        if (!view.CanDeleteComment(c)) throw new ForbiddenException("You can't delete this comment.");
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.SnippetComments.Where(x => x.Id == commentId).ExecuteDeleteAsync();
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Checks the input and returns the files to store (names trimmed, CRLF normalised, positions set).</summary>
    public static List<SnippetFile> Validate(SnippetInput input, bool isProjectSnippet)
    {
        if (string.IsNullOrWhiteSpace(input.Title)) throw new InvalidOperationException("Title is required.");
        if (input.Title.Trim().Length > 200) throw new InvalidOperationException("Titles can be at most 200 characters.");
        if (input.Description?.Length > 4000) throw new InvalidOperationException("Descriptions can be at most 4,000 characters.");
        if (!AllowedVisibilities(isProjectSnippet).Contains(input.Visibility))
            throw new InvalidOperationException(isProjectSnippet
                ? "Project snippets are Private or Project."
                : "Personal snippets are Private or Public.");
        if (input.Files is not { Count: > 0 }) throw new InvalidOperationException("Add at least one file.");
        if (input.Files.Count > MaxFiles) throw new InvalidOperationException($"Snippets can have at most {MaxFiles} files.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<SnippetFile>();
        long total = 0;
        foreach (var (f, i) in input.Files.Select((f, i) => (f, i)))
        {
            var name = (f.FileName ?? "").Trim();
            if (name.Length == 0) name = $"snippet{(i == 0 ? "" : i + 1)}.txt";
            if (name.Length > 255 || name.IndexOfAny(['/', '\\', '\0', '\r', '\n']) >= 0 || name is "." or "..")
                throw new InvalidOperationException($"'{name}' isn't a valid file name (no slashes, at most 255 characters).");
            if (!names.Add(name)) throw new InvalidOperationException($"Two files are named '{name}'.");
            var content = (f.Content ?? "").Replace("\r\n", "\n");
            var bytes = System.Text.Encoding.UTF8.GetByteCount(content);
            if (bytes > MaxFileBytes) throw new InvalidOperationException($"{name} is larger than {MaxFileBytes / 1024} KB.");
            total += bytes;
            files.Add(new SnippetFile { Position = i, FileName = name, Content = content });
        }
        if (total > MaxTotalBytes) throw new InvalidOperationException("A snippet can be at most 1 MB in total.");
        if (files.All(f => string.IsNullOrWhiteSpace(f.Content))) throw new InvalidOperationException("The snippet is empty.");
        return files;
    }
}
