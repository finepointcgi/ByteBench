using System.Text.RegularExpressions;
using GitBench.Data;
using GitBench.Services.Git;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services;

/// <summary>
/// Repository settings: name, description and default branch, rename (with redirects from the old slug),
/// archive/unarchive and delete. Every write requires <see cref="Permission.ManageRepositories"/>.
/// </summary>
public partial class RepositorySettingsService(
    IDbContextFactory<AppDbContext> dbFactory, RepoStorage storage, ProjectAccess access, ILogger<RepositorySettingsService> logger,
    Audit.AuditLog audit)
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$")]
    private static partial Regex SlugRx();

    /// <summary>Throws if the repository is archived (read-only).</summary>
    public static void EnsureNotArchived(Repository repo)
    {
        if (repo.IsArchived)
            throw new InvalidOperationException($"{repo.Name} is archived and read-only. Unarchive it in the repository settings first.");
    }

    /// <summary>Throws if the repository is archived, reading its current state from the database.</summary>
    public static async Task EnsureNotArchivedAsync(IDbContextFactory<AppDbContext> dbFactory, int repoId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var repo = await db.Repositories.AsNoTracking().FirstOrDefaultAsync(r => r.Id == repoId);
        if (repo != null) EnsureNotArchived(repo);
    }

    /// <summary>
    /// If <paramref name="slug"/> is a former slug of a repository the acting user can view, its current slug; else null.
    /// Real repositories take precedence, so call this only after the normal lookup failed.
    /// </summary>
    public async Task<string?> ResolveRedirectAsync(string projectKey, string slug)
    {
        var visible = await access.VisibleProjectIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var key = projectKey.ToUpperInvariant();
        var s = slug.ToLowerInvariant();
        return await db.RepositoryRedirects
            .Where(r => r.OldSlug == s && r.Repository.Project.Key == key && visible.Contains(r.ProjectId))
            .Select(r => r.Repository.Slug).FirstOrDefaultAsync();
    }

    /// <summary>
    /// If the page at <paramref name="uri"/> addresses a renamed repository by its old slug, the same URL with the current
    /// slug (path, query and fragment kept); else null.
    /// </summary>
    public async Task<string?> RedirectUrlAsync(string uri, string projectKey, string slug)
    {
        var current = await ResolveRedirectAsync(projectKey, slug);
        if (current == null) return null;
        var u = new Uri(uri);
        var marker = $"/repos/{slug}";
        var path = u.AbsolutePath;
        var i = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0 || (path.Length > i + marker.Length && path[i + marker.Length] != '/')) return null;
        return path[..i] + "/repos/" + current + path[(i + marker.Length)..] + u.Query + u.Fragment;
    }

    /// <summary>Updates name, description and default branch (also pointing the bare repo's HEAD at it).</summary>
    public async Task UpdateGeneralAsync(int repoId, string name, string? description, string defaultBranch)
    {
        await access.RequireForRepositoryAsync(repoId, Permission.ManageRepositories);
        name = name.Trim();
        if (name.Length is 0 or > 100) throw new InvalidOperationException("Name is required (max 100 characters).");
        await using var db = await dbFactory.CreateDbContextAsync();
        var repo = await db.Repositories.Include(r => r.Project).FirstOrDefaultAsync(r => r.Id == repoId)
                   ?? throw new InvalidOperationException("Repository not found.");

        var before = new { repo.Name, repo.Description, repo.DefaultBranch };
        if (defaultBranch != repo.DefaultBranch)
        {
            var path = storage.PathFor(repo.Project.Key, repo.Slug);
            await Task.Run(() => GitOps.SetHead(path, defaultBranch));
            repo.DefaultBranch = defaultBranch;
        }
        repo.Name = name;
        repo.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        await db.SaveChangesAsync();
        if (before.DefaultBranch != repo.DefaultBranch)
            await audit.RecordAsync("repository.default_branch_changed", "repository", repoId, repo.ProjectId, new { repo.Slug, from = before.DefaultBranch, to = repo.DefaultBranch });
        if (before.Name != repo.Name || before.Description != repo.Description)
            await audit.RecordAsync("repository.settings_changed", "repository", repoId, repo.ProjectId, new { repo.Slug, before = new { before.Name, before.Description }, after = new { repo.Name, repo.Description } });
    }

    /// <summary>
    /// Changes the slug and moves the bare repo directory. The old slug is kept as a <see cref="RepositoryRedirect"/>
    /// so old web and clone URLs keep working. Returns the new slug.
    /// </summary>
    public async Task<string> RenameAsync(int repoId, string newSlug)
    {
        await access.RequireForRepositoryAsync(repoId, Permission.ManageRepositories);
        newSlug = (newSlug ?? "").Trim().ToLowerInvariant();
        if (newSlug.Length is 0 or > 100 || !SlugRx().IsMatch(newSlug) || newSlug.EndsWith(".git") || newSlug.Contains(".."))
            throw new InvalidOperationException("Slug must start with a letter or digit and use only lowercase letters, digits, '.', '_' or '-'.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var repo = await db.Repositories.Include(r => r.Project).FirstOrDefaultAsync(r => r.Id == repoId)
                   ?? throw new InvalidOperationException("Repository not found.");
        var oldSlug = repo.Slug;
        if (newSlug == oldSlug) return oldSlug;
        if (await db.Repositories.AnyAsync(r => r.ProjectId == repo.ProjectId && r.Slug == newSlug))
            throw new InvalidOperationException($"A repository with slug '{newSlug}' already exists in {repo.Project.Key}.");

        var from = storage.PathFor(repo.Project.Key, oldSlug);
        var to = storage.PathFor(repo.Project.Key, newSlug);
        if (!IsUnderRoot(to)) throw new InvalidOperationException("Invalid slug.");
        if (Directory.Exists(to) || File.Exists(to))
            throw new InvalidOperationException($"Something already exists on disk at '{newSlug}.git'. Pick another slug.");

        await using var tx = await db.Database.BeginTransactionAsync();
        // The new slug is a real repository now; an old redirect with that name would only shadow nothing.
        await db.RepositoryRedirects.Where(r => r.ProjectId == repo.ProjectId && r.OldSlug == newSlug).ExecuteDeleteAsync();
        db.RepositoryRedirects.Add(new RepositoryRedirect { ProjectId = repo.ProjectId, OldSlug = oldSlug, RepositoryId = repo.Id });
        repo.Slug = newSlug;
        await db.SaveChangesAsync();

        // Move on disk inside the transaction: if the move fails, nothing is committed; if the commit fails, move back.
        await Task.Run(() => GitOps.MoveRepository(from, to));
        try
        {
            await tx.CommitAsync();
        }
        catch
        {
            try { await Task.Run(() => GitOps.MoveRepository(to, from)); }
            catch (Exception ex) { logger.LogError(ex, "Rename of {Key}/{Old} failed and the directory could not be moved back from {To}", repo.Project.Key, oldSlug, to); }
            throw;
        }
        logger.LogInformation("Renamed repository {Key}/{Old} to {Key}/{New}", repo.Project.Key, oldSlug, repo.Project.Key, newSlug);
        await audit.RecordAsync("repository.renamed", "repository", repoId, repo.ProjectId, new { from = oldSlug, to = newSlug });
        return newSlug;
    }

    public async Task SetArchivedAsync(int repoId, bool archived)
    {
        await access.RequireForRepositoryAsync(repoId, Permission.ManageRepositories);
        await using var db = await dbFactory.CreateDbContextAsync();
        var repo = await db.Repositories.FindAsync(repoId) ?? throw new InvalidOperationException("Repository not found.");
        var changed = repo.IsArchived != archived;
        repo.IsArchived = archived;
        repo.ArchivedAt = archived ? DateTime.UtcNow : null;
        await db.SaveChangesAsync();
        if (changed) await audit.RecordAsync(archived ? "repository.archived" : "repository.unarchived", "repository", repoId, repo.ProjectId, new { repo.Slug });
    }

    /// <summary>
    /// Deletes the repository: database first (its pull requests, with their comments, reviews, activity, watches and AI
    /// reviews, cascade; notifications pointing into it are removed), then the bare repo is moved to a trash name and deleted.
    /// A failure on disk after the database delete leaves only an unreferenced trash directory behind.
    /// </summary>
    public async Task DeleteAsync(int repoId, string confirmName)
    {
        await access.RequireForRepositoryAsync(repoId, Permission.ManageRepositories);
        await using var db = await dbFactory.CreateDbContextAsync();
        var repo = await db.Repositories.Include(r => r.Project).FirstOrDefaultAsync(r => r.Id == repoId)
                   ?? throw new InvalidOperationException("Repository not found.");
        if (!string.Equals(confirmName?.Trim(), repo.Name, StringComparison.Ordinal))
            throw new InvalidOperationException($"Type the repository name ({repo.Name}) to confirm.");

        var urlPrefix = $"/p/{repo.Project.Key}/repos/{repo.Slug}/";
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await db.Notifications.Where(n => n.ProjectId == repo.ProjectId && n.Url.StartsWith(urlPrefix)).ExecuteDeleteAsync();
            db.Repositories.Remove(repo); // FK cascades: pull requests (→ comments, reviews, activities, watches, AI reviews), redirects
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        await audit.RecordAsync("repository.deleted", "repository", repoId, repo.ProjectId, new { repo.Slug, repo.Name });

        var path = storage.PathFor(repo.Project.Key, repo.Slug);
        if (!Directory.Exists(path)) return;
        var trash = Path.Combine(Path.GetDirectoryName(path)!, $".trash-{repo.Slug}-{Guid.NewGuid():N}");
        try
        {
            await Task.Run(() =>
            {
                GitOps.MoveRepository(path, trash);
                DeleteDirectory(trash);
            });
            logger.LogInformation("Deleted repository {Key}/{Slug}", repo.Project.Key, repo.Slug);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogError(ex, "Repository {Key}/{Slug} was deleted from the database but its directory could not be removed ({Path} / {Trash})",
                repo.Project.Key, repo.Slug, path, trash);
        }
    }

    private bool IsUnderRoot(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(storage.Root) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.Ordinal);
    }

    /// <summary>Recursive delete that also clears read-only attributes (git marks pack files read-only).</summary>
    private static void DeleteDirectory(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, recursive: true);
    }
}
