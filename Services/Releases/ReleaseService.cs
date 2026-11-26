using System.Text;
using ByteBench.Api;
using ByteBench.Data;
using ByteBench.Services.Attachments;
using ByteBench.Services.Audit;
using ByteBench.Services.Git;
using ByteBench.Services.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Releases;

/// <summary>What the release form (or the API) submits.</summary>
public sealed class ReleaseInput
{
    public string TagName { get; set; } = "";
    /// <summary>Branch or commit to create the tag from when <see cref="TagName"/> doesn't exist yet.</summary>
    public string? Target { get; set; }
    /// <summary>For a new tag: a non-empty message makes it an annotated tag.</summary>
    public string? TagMessage { get; set; }
    public string Title { get; set; } = "";
    public string? Notes { get; set; }
    public bool IsPrerelease { get; set; }
    public bool IsDraft { get; set; }
}

/// <summary>Release notes built from git history: the Markdown, and the previous release tag it started from.</summary>
public sealed record GeneratedNotes(string Markdown, string? PreviousTag, int CommitCount, int PullRequestCount);

/// <summary>
/// Tags and releases of a repository. Permissions (checked here, so the UI and the API behave the same):
/// <list type="bullet">
/// <item>Viewing tags and published releases: ViewProject. Drafts (and their assets): PushCode.</item>
/// <item>Creating tags, creating/editing/publishing releases and managing assets: PushCode (Developer+).</item>
/// <item>Deleting tags and releases: DeleteBranch (Maintainer+). A tag used by a release can't be deleted.</item>
/// <item>Protected tags (<see cref="ProtectedTagService"/>) apply to creating and deleting tags here too, and pull
/// mirrors refuse both.</item>
/// </list>
/// Archived repositories are read-only. Release assets are stored like issue attachments (<see cref="AttachmentStorage"/>);
/// rows are deleted before files.
/// </summary>
public class ReleaseService(IDbContextFactory<AppDbContext> dbFactory, RepositoryService repos, ProjectAccess access,
    AttachmentStorage files, AuditLog audit, WebhookDispatcher webhooks, PublicBaseUrl baseUrl, IConfiguration config,
    ILogger<ReleaseService> log, ProtectedTagService protectedTags, ReleaseAutoAttachService autoAttach)
{
    public const int MaxAssetsPerRelease = 50;
    public const long DefaultMaxAssetBytes = 512L * 1024 * 1024;

    /// <summary><c>Releases:MaxAssetBytes</c>, default 512 MB.</summary>
    public long MaxAssetBytes => config.GetValue<long?>("Releases:MaxAssetBytes") is > 0 and var v ? v : DefaultMaxAssetBytes;

    // ---------------------------------------------------------------- tags

    public Task<List<TagInfo>> ListTagsAsync(Repository repo) =>
        repos.Git(repo, path => GitOps.Exists(path) ? GitOps.ListTagDetails(path) : []);

    /// <summary>Creates a lightweight (no message) or annotated tag. Needs PushCode.</summary>
    public async Task<TagInfo> CreateTagAsync(Repository repo, string name, string fromRev, string? message, int? actorId = null)
    {
        var uid = await RequireWriteAsync(repo, Permission.PushCode, actorId);
        name = (name ?? "").Trim();
        if (!GitOps.IsValidTagName(name))
            throw new InvalidOperationException($"'{name}' is not a valid tag name (no spaces, '..', '~^:?*[\\', and it can't start with '-' or end with '.lock').");
        if (string.IsNullOrWhiteSpace(fromRev)) throw new InvalidOperationException("Choose a branch or commit to tag.");
        await protectedTags.EnsureAllowedAsync(repo, name, RefChangeKind.Create, uid);
        var who = await IdentityAsync(uid);
        var tag = await repos.Git(repo, path => GitOps.CreateTag(path, name, fromRev.Trim(), message, who));
        RaiseTagEvent(repo, WebhookEventNames.TagCreated, tag, uid);
        return tag;
    }

    /// <summary>Deletes a tag. Needs DeleteBranch (Maintainer+); refused while a release uses the tag.</summary>
    public async Task DeleteTagAsync(Repository repo, string name, int? actorId = null)
    {
        var uid = await RequireWriteAsync(repo, Permission.DeleteBranch, actorId);
        await using (var db = await dbFactory.CreateDbContextAsync())
            if (await db.Releases.AnyAsync(r => r.RepositoryId == repo.Id && r.TagName == name))
                throw new InvalidOperationException($"Tag '{name}' is used by a release. Delete the release first.");
        await protectedTags.EnsureAllowedAsync(repo, name, RefChangeKind.Delete, uid);
        var tag = await repos.Git(repo, path => GitOps.GetTag(path, name)) ?? throw new InvalidOperationException($"Tag '{name}' does not exist.");
        await repos.Git(repo, path => GitOps.DeleteTag(path, name));
        await audit.RecordAsync("tag.deleted", "repository", repo.Id, repo.ProjectId,
            new { repository = repo.Slug, tag = name, sha = tag.Commit?.Sha ?? tag.ObjectSha }, uid);
        RaiseTagEvent(repo, WebhookEventNames.TagDeleted, tag, uid);
    }

    /// <summary>Tags that releases of the repository use (they are protected from deletion).</summary>
    public static Task<List<string>> ReleaseTagsAsync(AppDbContext db, int repositoryId) =>
        db.Releases.Where(r => r.RepositoryId == repositoryId).Select(r => r.TagName).ToListAsync();

    // ---------------------------------------------------------------- releases: queries

    /// <summary>Releases of the repository, newest first; drafts only for users who can push.</summary>
    public async Task<List<Release>> ListAsync(Repository repo, int? userId = null)
    {
        if (!await access.CanAsync(repo.ProjectId, Permission.ViewProject, userId)) return [];
        var drafts = await access.CanAsync(repo.ProjectId, Permission.PushCode, userId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var list = await db.Releases.AsNoTracking().Include(r => r.Author).Include(r => r.Assets)
            .Where(r => r.RepositoryId == repo.Id && (drafts || !r.IsDraft)).ToListAsync();
        foreach (var r in list) r.Repository = repo;
        return list.OrderByDescending(r => r.IsDraft).ThenByDescending(r => r.PublishedAt ?? r.CreatedAt).ThenByDescending(r => r.Id).ToList();
    }

    public async Task<Release?> GetAsync(Repository repo, int id, int? userId = null) =>
        (await QueryOneAsync(repo, r => r.Id == id, userId));

    public async Task<Release?> GetByTagAsync(Repository repo, string tag, int? userId = null) =>
        (await QueryOneAsync(repo, r => r.TagName == tag, userId));

    /// <summary>The newest published release that isn't a pre-release, or null.</summary>
    public async Task<Release?> GetLatestAsync(Repository repo, int? userId = null)
    {
        if (!await access.CanAsync(repo.ProjectId, Permission.ViewProject, userId)) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        var r = await db.Releases.AsNoTracking().Where(x => x.RepositoryId == repo.Id && !x.IsDraft && !x.IsPrerelease)
            .OrderByDescending(x => x.PublishedAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync();
        if (r != null) r.Repository = repo;
        return r;
    }

    private async Task<Release?> QueryOneAsync(Repository repo, System.Linq.Expressions.Expression<Func<Release, bool>> where, int? userId)
    {
        if (!await access.CanAsync(repo.ProjectId, Permission.ViewProject, userId)) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        var r = await db.Releases.AsNoTracking().Include(x => x.Author).Include(x => x.Assets).ThenInclude(a => a.Uploader)
            .Where(x => x.RepositoryId == repo.Id).Where(where).AsSplitQuery().FirstOrDefaultAsync();
        if (r == null || (r.IsDraft && !await access.CanAsync(repo.ProjectId, Permission.PushCode, userId))) return null;
        r.Repository = repo;
        r.Assets = r.Assets.OrderBy(a => a.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        return r;
    }

    // ---------------------------------------------------------------- releases: commands

    /// <summary>
    /// Creates a release. If the tag doesn't exist it is created from <see cref="ReleaseInput.Target"/> first (and
    /// removed again if the release can't be saved). Publishes right away unless it's a draft.
    /// </summary>
    public async Task<Release> CreateAsync(Repository repo, ReleaseInput input, int? actorId = null)
    {
        var uid = await RequireWriteAsync(repo, Permission.PushCode, actorId);
        var tagName = (input.TagName ?? "").Trim();
        var title = Validate(input, tagName);

        await using (var db = await dbFactory.CreateDbContextAsync())
            if (await db.Releases.AnyAsync(r => r.RepositoryId == repo.Id && r.TagName == tagName))
                throw new InvalidOperationException($"Tag '{tagName}' already has a release.");

        var createdTag = false;
        if (!await repos.Git(repo, path => GitOps.TagExists(path, tagName)))
        {
            if (string.IsNullOrWhiteSpace(input.Target))
                throw new InvalidOperationException($"Tag '{tagName}' doesn't exist. Choose a branch or commit to create it from.");
            await CreateTagAsync(repo, tagName, input.Target, input.TagMessage, uid);
            createdTag = true;
        }

        var now = DateTime.UtcNow;
        var release = new Release
        {
            RepositoryId = repo.Id, TagName = tagName, Title = title, Notes = Clean(input.Notes),
            IsPrerelease = input.IsPrerelease, IsDraft = input.IsDraft, AuthorId = uid, CreatedAt = now, UpdatedAt = now,
            PublishedAt = input.IsDraft ? null : now,
        };
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            db.Releases.Add(release);
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            log.LogWarning(ex, "Could not save release {Tag} for repository {Id}", tagName, repo.Id);
            if (createdTag) await TryDeleteTagAsync(repo, tagName);
            throw new InvalidOperationException($"Tag '{tagName}' already has a release.");
        }

        await audit.RecordAsync("release.created", "release", release.Id, repo.ProjectId,
            new { repository = repo.Slug, tag = tagName, title, draft = release.IsDraft, prerelease = release.IsPrerelease }, uid);
        if (!release.IsDraft) await OnPublishedAsync(repo, release.Id, uid);
        return release;
    }

    /// <summary>Edits title, notes and flags (the tag can't change). Turning a draft into a release publishes it.</summary>
    public async Task<Release> UpdateAsync(Repository repo, int id, ReleaseInput input, int? actorId = null)
    {
        var uid = await RequireWriteAsync(repo, Permission.PushCode, actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var release = await db.Releases.FirstOrDefaultAsync(r => r.Id == id && r.RepositoryId == repo.Id)
                      ?? throw new InvalidOperationException("Release not found.");
        var title = Validate(input, release.TagName);
        var publishing = release.IsDraft && !input.IsDraft;
        release.Title = title;
        release.Notes = Clean(input.Notes);
        release.IsPrerelease = input.IsPrerelease;
        release.IsDraft = input.IsDraft;
        release.UpdatedAt = DateTime.UtcNow;
        if (publishing) release.PublishedAt ??= release.UpdatedAt;
        await db.SaveChangesAsync();
        if (publishing) await OnPublishedAsync(repo, release.Id, uid);
        return release;
    }

    /// <summary>Publishes a draft.</summary>
    public async Task PublishAsync(Repository repo, int id, int? actorId = null)
    {
        var uid = await RequireWriteAsync(repo, Permission.PushCode, actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        var changed = await db.Releases.Where(r => r.Id == id && r.RepositoryId == repo.Id && r.IsDraft)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsDraft, false).SetProperty(r => r.UpdatedAt, now)
                .SetProperty(r => r.PublishedAt, r => r.PublishedAt ?? now));
        if (changed == 0) throw new InvalidOperationException("Release not found or already published.");
        await OnPublishedAsync(repo, id, uid);
    }

    /// <summary>Deletes a release and its assets (rows first, then files). Needs DeleteBranch (Maintainer+). Optionally deletes its tag too.</summary>
    public async Task DeleteAsync(Repository repo, int id, bool deleteTag = false, int? actorId = null)
    {
        var uid = await RequireWriteAsync(repo, Permission.DeleteBranch, actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var release = await db.Releases.Include(r => r.Assets).FirstOrDefaultAsync(r => r.Id == id && r.RepositoryId == repo.Id)
                      ?? throw new InvalidOperationException("Release not found.");
        if (deleteTag) await protectedTags.EnsureAllowedAsync(repo, release.TagName, RefChangeKind.Delete, uid); // before anything is deleted
        var stored = release.Assets.Select(a => a.StoredName).ToList();
        db.Releases.Remove(release);
        await db.SaveChangesAsync();
        files.Delete(stored);
        await audit.RecordAsync("release.deleted", "release", id, repo.ProjectId,
            new { repository = repo.Slug, tag = release.TagName, release.Title, assets = stored.Count }, uid);
        if (deleteTag && await repos.Git(repo, path => GitOps.TagExists(path, release.TagName)))
            await DeleteTagAsync(repo, release.TagName, uid);
    }

    // ---------------------------------------------------------------- assets

    public async Task<ReleaseAsset> UploadAssetAsync(Repository repo, int releaseId, string? fileName, Stream content, int? actorId = null, CancellationToken ct = default)
    {
        var uid = await RequireWriteAsync(repo, Permission.PushCode, actorId);
        var name = AttachmentStorage.SanitizeFileName(fileName);
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var release = await db.Releases.Where(r => r.Id == releaseId && r.RepositoryId == repo.Id)
                .Select(r => new { Count = r.Assets.Count, Taken = r.Assets.Any(a => a.FileName == name) }).FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("Release not found.");
            if (release.Count >= MaxAssetsPerRelease) throw new InvalidOperationException($"A release can have at most {MaxAssetsPerRelease} assets.");
            if (release.Taken) throw new InvalidOperationException($"This release already has an asset named '{name}'. Delete it first.");
        }

        var stored = await files.SaveAsync(content, name, ct, MaxAssetBytes);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var asset = new ReleaseAsset
            {
                ReleaseId = releaseId, StoredName = stored.StoredName, FileName = name, ContentType = stored.ContentType,
                Size = stored.Size, Sha256 = stored.Sha256, UploaderId = uid, CreatedAt = DateTime.UtcNow,
            };
            db.ReleaseAssets.Add(asset);
            await db.Releases.Where(r => r.Id == releaseId).ExecuteUpdateAsync(s => s.SetProperty(r => r.UpdatedAt, DateTime.UtcNow), CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
            return asset;
        }
        catch
        {
            files.Delete([stored.StoredName]);
            throw;
        }
    }

    public async Task DeleteAssetAsync(Repository repo, int assetId, int? actorId = null)
    {
        await RequireWriteAsync(repo, Permission.PushCode, actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var asset = await db.ReleaseAssets.FirstOrDefaultAsync(a => a.Id == assetId && a.Release.RepositoryId == repo.Id)
                    ?? throw new InvalidOperationException("Asset not found.");
        db.ReleaseAssets.Remove(asset);
        await db.SaveChangesAsync();
        files.Delete([asset.StoredName]);
    }

    /// <summary>An asset for download, if the user can view the project (and push, for drafts); else null. Counts the download.</summary>
    public async Task<(ReleaseAsset Asset, string Path)?> OpenAssetAsync(int assetId, int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var a = await db.ReleaseAssets.AsNoTracking().Include(x => x.Release).ThenInclude(r => r.Repository)
            .FirstOrDefaultAsync(x => x.Id == assetId);
        if (a == null || !await access.CanAsync(a.Release.Repository.ProjectId, Permission.ViewProject, userId)) return null;
        if (a.Release.IsDraft && !await access.CanAsync(a.Release.Repository.ProjectId, Permission.PushCode, userId)) return null;
        await db.ReleaseAssets.Where(x => x.Id == assetId).ExecuteUpdateAsync(s => s.SetProperty(x => x.DownloadCount, x => x.DownloadCount + 1));
        return (a, files.PathFor(a.StoredName));
    }

    /// <summary>Stored file names of every asset in a repository or project (for deleting the files after the rows).</summary>
    public static Task<List<string>> StoredNamesAsync(AppDbContext db, int? repositoryId = null, int? projectId = null) =>
        db.ReleaseAssets.Where(a => (repositoryId == null || a.Release.RepositoryId == repositoryId)
                                    && (projectId == null || a.Release.Repository.ProjectId == projectId))
            .Select(a => a.StoredName).ToListAsync();

    // ---------------------------------------------------------------- generated notes

    /// <summary>
    /// Markdown notes for <paramref name="tagOrTarget"/>: merged pull requests and commits since the previous release's tag
    /// (the closest release tag that is an ancestor), with issue keys linked to their issues.
    /// </summary>
    public async Task<GeneratedNotes> GenerateNotesAsync(Repository repo, string tagOrTarget, string? excludeTag = null)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ViewProject);
        List<string> candidates;
        await using (var db = await dbFactory.CreateDbContextAsync())
            candidates = await db.Releases.Where(r => r.RepositoryId == repo.Id && r.TagName != excludeTag && r.TagName != tagOrTarget)
                .Select(r => r.TagName).ToListAsync();

        const int max = 300;
        var (previous, commits, truncated) = await repos.Git(repo, path =>
        {
            if (GitOps.ResolveSha(path, tagOrTarget) == null) throw new InvalidOperationException($"Unknown tag or branch '{tagOrTarget}'.");
            var prev = GitOps.FindPreviousTag(path, tagOrTarget, candidates);
            var (items, more) = GitOps.CommitsBetween(path, prev, tagOrTarget, max);
            return (prev, items, more);
        });

        var shas = commits.Select(c => c.Sha).ToHashSet();
        List<PullRequest> prs;
        Dictionary<int, string> issueTitles;
        var key = repo.Project.Key;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var merged = await db.PullRequests.AsNoTracking().Include(p => p.Author)
                .Where(p => p.RepositoryId == repo.Id && p.State == PullRequestState.Merged && p.MergeCommitSha != null).ToListAsync();
            prs = merged.Where(p => shas.Contains(p.MergeCommitSha!)).OrderBy(p => p.Number).ToList();
            var numbers = IssueKeys.Numbers(key, commits.Select(c => c.Message).Concat(prs.Select(p => p.Title)).Concat(prs.Select(p => p.SourceBranch)));
            issueTitles = numbers.Count == 0 ? [] : await db.Issues.Where(i => i.ProjectId == repo.ProjectId && numbers.Contains(i.Number))
                .ToDictionaryAsync(i => i.Number, i => i.Title);
        }

        var rx = IssueKeys.Any(key);
        string Link(string text) => rx.Replace(Escape(text), m =>
            int.TryParse(m.Groups[1].Value, out var n) && issueTitles.ContainsKey(n) ? $"[{key}-{n}](/p/{key}/issues/{n})" : m.Value);

        var sb = new StringBuilder();
        sb.AppendLine("## What's changed").AppendLine();
        if (prs.Count > 0)
        {
            sb.AppendLine("### Pull requests").AppendLine();
            foreach (var p in prs)
                sb.AppendLine($"- {Link(p.Title)} ([#{p.Number}]({RepoUrls.PullRequest(repo, p.Number)})) by @{p.Author.UserName}");
            sb.AppendLine();
        }
        // Merge commits are left out: the pull requests they merged are listed above.
        var direct = commits.Where(c => c.ParentShas.Count < 2).ToList();
        if (direct.Count > 0)
        {
            sb.AppendLine("### Commits").AppendLine();
            foreach (var c in direct)
                sb.AppendLine($"- {Link(c.MessageShort)} ([`{c.ShortSha}`]({RepoUrls.Commit(repo, c.Sha)}))");
            if (truncated) sb.AppendLine($"- … and more (only the newest {max} commits are listed)");
            sb.AppendLine();
        }
        if (prs.Count == 0 && direct.Count == 0) sb.AppendLine("No changes since the previous release.").AppendLine();
        if (issueTitles.Count > 0)
        {
            sb.AppendLine("### Issues").AppendLine();
            foreach (var (n, t) in issueTitles.OrderBy(x => x.Key)) sb.AppendLine($"- [{key}-{n}](/p/{key}/issues/{n}) {Escape(t)}");
            sb.AppendLine();
        }
        sb.AppendLine(previous == null
            ? $"**Full history:** [{Escape(tagOrTarget)}]({RepoUrls.Commits(repo, tagOrTarget)})"
            : $"**Changes since {Escape(previous)}:** {commits.Count}{(truncated ? "+" : "")} commit{(commits.Count == 1 ? "" : "s")}");
        return new GeneratedNotes(sb.ToString().TrimEnd() + "\n", previous, commits.Count, prs.Count);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<int> RequireWriteAsync(Repository repo, Permission p, int? actorId)
    {
        await access.RequireAsync(repo.ProjectId, p, actorId);
        await RepositorySettingsService.EnsureNotArchivedAsync(dbFactory, repo.Id);
        return await access.ResolveUserIdAsync(actorId) ?? throw new ForbiddenException("Sign in first.");
    }

    private async Task<GitIdentity> IdentityAsync(int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var u = await db.Users.FindAsync(userId) ?? throw new InvalidOperationException("User not found.");
        return RepositoryService.IdentityOf(u);
    }

    private static string Validate(ReleaseInput input, string tagName)
    {
        if (!GitOps.IsValidTagName(tagName)) throw new InvalidOperationException($"'{tagName}' is not a valid tag name.");
        var title = string.IsNullOrWhiteSpace(input.Title) ? tagName : input.Title.Trim();
        if (title.Length > 200) throw new InvalidOperationException("The title can be at most 200 characters.");
        if (input.Notes?.Length > 200_000) throw new InvalidOperationException("The notes are too long (200,000 characters at most).");
        return title;
    }

    private static string? Clean(string? notes) => string.IsNullOrWhiteSpace(notes) ? null : notes.Replace("\r\n", "\n").TrimEnd();

    /// <summary>Escapes Markdown link/emphasis punctuation in commit messages and titles.</summary>
    private static string Escape(string s) => AttachmentFormat.EscapeMarkdown(s);

    private async Task TryDeleteTagAsync(Repository repo, string name)
    {
        try { await repos.Git(repo, path => GitOps.DeleteTag(path, name)); }
        catch (Exception ex) when (ex is InvalidOperationException or LibGit2Sharp.LibGit2SharpException) { log.LogWarning(ex, "Could not remove tag {Tag}", name); }
    }

    private async Task OnPublishedAsync(Repository repo, int releaseId, int actorId)
    {
        Release? r;
        await using (var db = await dbFactory.CreateDbContextAsync())
            r = await db.Releases.AsNoTracking().Include(x => x.Author).Include(x => x.Assets)
                .FirstOrDefaultAsync(x => x.Id == releaseId);
        if (r == null) return;
        r.Repository = repo;
        await audit.RecordAsync("release.published", "release", releaseId, repo.ProjectId,
            new { repository = repo.Slug, tag = r.TagName, r.Title, prerelease = r.IsPrerelease }, actorId);
        try
        {
            var url = baseUrl.Value;
            webhooks.Enqueue(repo.ProjectId, WebhookEventNames.ReleasePublished,
                new { release = ReleaseDtos.Release(r, url), repository = Dto.Repository(repo, url) }, actorId);
        }
        catch (Exception ex) { log.LogError(ex, "Could not queue release.published for release {Id}", releaseId); }
        // Release hub: copy matching CI artifacts of the tagged commit onto the release (a repository setting; never fails the publish).
        try { await autoAttach.AttachAsync(repo, releaseId, actorId); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Attaching CI artifacts to release {Id} failed", releaseId); }
    }

    private void RaiseTagEvent(Repository repo, string eventName, TagInfo tag, int actorId)
    {
        try
        {
            var url = baseUrl.Value;
            webhooks.Enqueue(repo.ProjectId, eventName, new { tag = ReleaseDtos.Tag(repo, tag, url), repository = Dto.Repository(repo, url) }, actorId);
        }
        catch (Exception ex) { log.LogError(ex, "Could not queue {Event} for tag {Tag}", eventName, tag.Name); }
    }
}
