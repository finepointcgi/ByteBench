using System.Text.RegularExpressions;
using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Git;

/// <summary>
/// What git over HTTP (<see cref="GitHttpEndpoints"/>) and over SSH (<see cref="Ssh.SshGitServer"/>) share: strict
/// validation of <c>{KEY}/{slug}.git</c>, the repository lookup (old slugs answer through <see cref="RepositoryRedirect"/>),
/// the on-disk path check, and running the <see cref="IGitPushListener"/>s after a push.
/// </summary>
internal static partial class GitRepoLookup
{
    public sealed record RepoInfo(int Id, string Key, string Slug, int ProjectId, bool IsArchived);

    public const string ArchivedMessage = "This repository is archived and read-only. A Maintainer can unarchive it in the repository settings.";

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{1,9}$")]
    private static partial Regex ProjectKeyPattern();

    // Mirrors Repository.Slug validation; ".." is excluded separately.
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,99}$")]
    private static partial Regex SlugPattern();

    /// <summary>True when the key and slug are well-formed (nothing that could climb out of the repository root).</summary>
    public static bool IsValid(string projectKey, string slug) =>
        ProjectKeyPattern().IsMatch(projectKey) && SlugPattern().IsMatch(slug) && !slug.Contains("..");

    /// <summary>The repository at <c>{projectKey}/{slug}</c> (key case-insensitive), following renames; null if none.</summary>
    public static async Task<RepoInfo?> FindAsync(IDbContextFactory<AppDbContext> dbFactory, string projectKey, string slug, CancellationToken ct)
    {
        var upperKey = projectKey.ToUpperInvariant();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Repositories.AsNoTracking()
                   .Where(r => r.Project.Key.ToUpper() == upperKey && r.Slug == slug)
                   .Select(r => new RepoInfo(r.Id, r.Project.Key, r.Slug, r.ProjectId, r.IsArchived))
                   .FirstOrDefaultAsync(ct)
               // A renamed repository keeps answering at its old slug (RepositoryRedirect).
               ?? await db.RepositoryRedirects.AsNoTracking()
                   .Where(r => r.Repository.Project.Key.ToUpper() == upperKey && r.OldSlug == slug)
                   .Select(r => new RepoInfo(r.Repository.Id, r.Repository.Project.Key, r.Repository.Slug, r.Repository.ProjectId, r.Repository.IsArchived))
                   .FirstOrDefaultAsync(ct);
    }

    /// <summary>The bare repository's full path, or null (logged) if it is missing or outside the repository root.</summary>
    public static string? DiskPath(RepoStorage storage, RepoInfo found, ILogger logger)
    {
        var root = Path.TrimEndingDirectorySeparator(storage.Root) + Path.DirectorySeparatorChar;
        var diskPath = Path.GetFullPath(storage.PathFor(found.Key, found.Slug));
        if (!diskPath.StartsWith(root, StringComparison.Ordinal)) return null;
        if (!Directory.Exists(diskPath))
        {
            logger.LogWarning("Repository {Key}/{Slug} (id {Id}) is in the database but missing on disk at {Path}",
                found.Key, found.Slug, found.Id, diskPath);
            return null;
        }
        return diskPath;
    }

    /// <summary>
    /// Runs every <see cref="IGitPushListener"/> registered in <paramref name="services"/> (a request or command scope) after a
    /// successful push. Failures are logged; the push is already committed, so app shutdown is the only cancellation.
    /// </summary>
    public static async Task NotifyPushListenersAsync(IServiceProvider services, ILogger logger, int repositoryId, int userId)
    {
        var ct = services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        foreach (var listener in services.GetServices<IGitPushListener>())
        {
            try
            {
                await listener.OnPushAsync(repositoryId, userId, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "IGitPushListener {Listener} failed for repository {RepositoryId}",
                    listener.GetType().FullName, repositoryId);
            }
        }
    }
}
