using ByteBench.Api;
using ByteBench.Data;
using ByteBench.Services.Audit;
using ByteBench.Services.Git;
using ByteBench.Services.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Releases;

/// <summary>
/// After a push: raises <c>tag.created</c> / <c>tag.deleted</c> webhooks for the tags the push created or deleted (read
/// from <see cref="PushCapture"/>, checked against the repository so refs git refused are skipped), and records
/// <c>tag.deleted</c> in the audit log. Branch refs are left to the other listeners.
/// </summary>
public sealed class TagPushListener(IDbContextFactory<AppDbContext> dbFactory, RepoStorage storage, PushCapture capture,
    WebhookDispatcher dispatcher, PublicBaseUrl baseUrl, AuditLog audit) : IGitPushListener
{
    public async Task OnPushAsync(int repositoryId, int userId, CancellationToken ct)
    {
        var tags = capture.Refs.Where(r => r.Ref.StartsWith("refs/tags/", StringComparison.Ordinal) && (r.Created || r.Deleted)).ToList();
        if (tags.Count == 0) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var repo = await db.Repositories.AsNoTracking().Include(r => r.Project).FirstOrDefaultAsync(r => r.Id == repositoryId, ct);
        if (repo == null) return;
        var path = storage.PathFor(repo.Project.Key, repo.Slug);
        var url = baseUrl.Value;
        var created = await WebhookNotifier.AnyListenerAsync(db, repo.ProjectId, WebhookEventNames.TagCreated);
        var deleted = await WebhookNotifier.AnyListenerAsync(db, repo.ProjectId, WebhookEventNames.TagDeleted);

        foreach (var pushed in tags)
        {
            var name = pushed.Ref["refs/tags/".Length..];
            var now = await Task.Run(() => GitOps.GetTag(path, name), ct);
            if (pushed.Created && now != null && now.ObjectSha == pushed.After)
            {
                if (created)
                    dispatcher.Enqueue(repo.ProjectId, WebhookEventNames.TagCreated,
                        new { tag = ReleaseDtos.Tag(repo, now, url), repository = Dto.Repository(repo, url) }, userId);
            }
            else if (pushed.Deleted && now == null)
            {
                await audit.RecordAsync("tag.deleted", "repository", repo.Id, repo.ProjectId,
                    new { repository = repo.Slug, tag = name, sha = pushed.Before, via = "push" }, userId);
                if (deleted)
                    dispatcher.Enqueue(repo.ProjectId, WebhookEventNames.TagDeleted, new
                    {
                        tag = ReleaseDtos.Tag(repo, new TagInfo(name, pushed.Before, null, false, null, null, null, null), url),
                        repository = Dto.Repository(repo, url),
                    }, userId);
            }
        }
    }
}
