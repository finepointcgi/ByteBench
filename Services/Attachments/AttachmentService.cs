using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Attachments;

/// <summary>
/// Issue attachments: list, upload, delete and download lookups. Uploads need EditIssues (or Comment when made from a
/// comment box); deletes need to be the uploader (with Comment) or Maintainer+. Rows are written after the file is on
/// disk (the file is removed again if the row can't be saved); deletes remove the row first, then the file.
/// </summary>
public class AttachmentService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, AttachmentStorage storage, LiveEvents live)
{
    public long MaxBytes => storage.MaxBytes;
    public int MaxPerIssue => storage.MaxPerIssue;

    public async Task<List<Attachment>> ListAsync(int issueId)
    {
        var projectId = await access.ProjectOf(d => d.Issues.Where(i => i.Id == issueId).Select(i => (int?)i.ProjectId));
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Attachments.AsNoTracking().Include(a => a.Uploader)
            .Where(a => a.IssueId == issueId).OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync();
    }

    /// <summary>The attachment if the acting user (or <paramref name="userId"/>) can view its project; else null.</summary>
    public async Task<Attachment?> FindAsync(int attachmentId, int? userId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var a = await db.Attachments.AsNoTracking().Include(x => x.Uploader).Include(x => x.Issue).ThenInclude(i => i.Project)
            .FirstOrDefaultAsync(x => x.Id == attachmentId);
        if (a == null || !await access.CanAsync(a.Issue.ProjectId, Permission.ViewProject, userId)) return null;
        return a;
    }

    public string PathFor(Attachment a) => storage.PathFor(a.StoredName);

    /// <summary>Streams <paramref name="content"/> to disk and records it on the issue (with an activity entry).</summary>
    public async Task<Attachment> UploadAsync(int issueId, string? fileName, Stream content, bool fromComment = false, int? actorId = null, CancellationToken ct = default)
    {
        var uid = await access.ResolveUserIdAsync(actorId) ?? throw new ForbiddenException("Sign in to upload files.");
        await access.RequireForIssueAsync(issueId, fromComment ? Permission.Comment : Permission.EditIssues, uid);
        var name = AttachmentStorage.SanitizeFileName(fileName);

        await using (var db = await dbFactory.CreateDbContextAsync())
            if (await db.Attachments.CountAsync(a => a.IssueId == issueId, ct) >= storage.MaxPerIssue)
                throw new InvalidOperationException($"An issue can have at most {storage.MaxPerIssue} attachments.");

        var stored = await storage.SaveAsync(content, name, ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            await using var tx = await db.Database.BeginTransactionAsync(CancellationToken.None);
            // Re-check under the transaction so concurrent uploads can't overshoot the limit by much.
            if (await db.Attachments.CountAsync(a => a.IssueId == issueId, CancellationToken.None) >= storage.MaxPerIssue)
                throw new InvalidOperationException($"An issue can have at most {storage.MaxPerIssue} attachments.");
            var attachment = new Attachment
            {
                IssueId = issueId, StoredName = stored.StoredName, FileName = name, ContentType = stored.ContentType,
                Size = stored.Size, Sha256 = stored.Sha256, UploaderId = uid, FromComment = fromComment, CreatedAt = DateTime.UtcNow,
            };
            db.Attachments.Add(attachment);
            db.Activities.Add(new Activity { IssueId = issueId, ActorId = uid, Text = $"attached **{AttachmentFormat.EscapeMarkdown(name)}**" });
            await db.Issues.Where(i => i.Id == issueId).ExecuteUpdateAsync(s => s.SetProperty(i => i.UpdatedAt, DateTime.UtcNow), CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
            await tx.CommitAsync(CancellationToken.None);
            attachment.Uploader = (await db.Users.FindAsync(uid))!;
            await PublishAsync(issueId);
            return attachment;
        }
        catch
        {
            storage.Delete([stored.StoredName]);
            throw;
        }
    }

    /// <summary>The uploader (with Comment) or a Maintainer+ may delete; the row goes first, then the file.</summary>
    public async Task DeleteAsync(int attachmentId, int? actorId = null)
    {
        var uid = await access.ResolveUserIdAsync(actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var a = await db.Attachments.Include(x => x.Issue).FirstOrDefaultAsync(x => x.Id == attachmentId)
                ?? throw new InvalidOperationException("Attachment not found.");
        if (uid != null && a.UploaderId == uid) await access.RequireAsync(a.Issue.ProjectId, Permission.Comment, actorId);
        else await access.RequireRoleAsync(a.Issue.ProjectId, ProjectRole.Maintainer, "delete other people's attachments", actorId);

        db.Attachments.Remove(a);
        if (uid is { } actor)
            db.Activities.Add(new Activity { IssueId = a.IssueId, ActorId = actor, Text = $"removed attachment **{AttachmentFormat.EscapeMarkdown(a.FileName)}**" });
        a.Issue.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        storage.Delete([a.StoredName]);
        await PublishAsync(a.IssueId);
    }

    public bool CanDelete(Attachment a, int userId, ProjectPermissions perms) =>
        (a.UploaderId == userId && perms.Can(Permission.Comment)) || perms.Role >= ProjectRole.Maintainer;

    private async Task PublishAsync(int issueId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.Issues.Where(i => i.Id == issueId).Select(i => (int?)i.ProjectId).FirstOrDefaultAsync() is { } p)
            live.Publish(new IssueChanged(p, issueId), new ActivityChanged());
    }

    // ----- Cleanup helpers for issue/project deletion (rows first, then files) -----

    public static Task<List<string>> StoredNamesForIssuesAsync(AppDbContext db, IReadOnlyCollection<int> issueIds) =>
        db.Attachments.Where(a => issueIds.Contains(a.IssueId)).Select(a => a.StoredName).ToListAsync();

    public static Task<List<string>> StoredNamesForProjectAsync(AppDbContext db, int projectId) =>
        db.Attachments.Where(a => a.Issue.ProjectId == projectId).Select(a => a.StoredName).ToListAsync();
}
