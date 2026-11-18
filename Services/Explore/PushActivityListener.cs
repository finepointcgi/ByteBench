using GitBench.Data;
using GitBench.Services.Git;
using GitBench.Services.Webhooks;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Explore;

/// <summary>
/// After a push, records a <see cref="PushEvent"/> per branch or tag that actually moved (checked against the repository,
/// so refs git refused are skipped), with the number of new commits, for profile feeds and the contribution calendar.
/// </summary>
public sealed class PushActivityListener(IDbContextFactory<AppDbContext> dbFactory, RepoStorage storage, PushCapture capture, LiveEvents live)
    : IGitPushListener
{
    public const int MaxCommits = 100;

    public async Task OnPushAsync(int repositoryId, int userId, CancellationToken ct)
    {
        var refs = capture.Refs.Where(r => r.Ref.StartsWith("refs/heads/", StringComparison.Ordinal) || r.Ref.StartsWith("refs/tags/", StringComparison.Ordinal)).ToList();
        if (refs.Count == 0) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var repo = await db.Repositories.AsNoTracking().Include(r => r.Project).FirstOrDefaultAsync(r => r.Id == repositoryId, ct);
        if (repo == null) return;
        var path = storage.PathFor(repo.Project.Key, repo.Slug);

        var events = await Task.Run(() => Describe(path, refs), ct);
        if (events.Count == 0) return;
        var now = DateTime.UtcNow;
        foreach (var (pushed, commits) in events)
            db.PushEvents.Add(new PushEvent
            {
                ProjectId = repo.ProjectId, RepositoryId = repo.Id, UserId = userId, Ref = pushed.Ref,
                Before = pushed.Created ? null : pushed.Before, After = pushed.Deleted ? null : pushed.After,
                CommitCount = commits, CreatedAt = now,
            });
        await db.SaveChangesAsync(ct);
        live.Publish(new ActivityChanged());
    }

    /// <summary>The refs that now point where the push said (or are gone, for deletions), with their new commit counts.</summary>
    public static List<(PushedRef Ref, int Commits)> Describe(string path, IReadOnlyList<PushedRef> refs)
    {
        var result = new List<(PushedRef, int)>();
        if (!GitOps.Exists(path)) return result;
        using var r = new LibGit2Sharp.Repository(path);
        foreach (var p in refs)
        {
            var current = r.Refs[p.Ref];
            if (p.Deleted)
            {
                if (current == null) result.Add((p, 0));
                continue;
            }
            if (current?.ResolveToDirectReference()?.TargetIdentifier != p.After) continue;
            if (p.Ref.StartsWith("refs/tags/", StringComparison.Ordinal))
            {
                result.Add((p, 0));
                continue;
            }
            result.Add((p, CountNewCommits(r, p)));
        }
        return result;
    }

    private static int CountNewCommits(LibGit2Sharp.Repository r, PushedRef p)
    {
        // Commits reachable from the new tip but not from the old one (or, for a new branch, from any other branch).
        object exclude = p.Created
            ? r.Branches.Where(b => !b.IsRemote && b.CanonicalName != p.Ref && b.Tip != null).Select(b => b.Tip).ToList()
            : p.Before;
        try
        {
            return r.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = p.After, ExcludeReachableFrom = exclude })
                .Take(MaxCommits).Count();
        }
        catch (LibGit2SharpException)
        {
            return 1; // e.g. the old tip was garbage-collected after a force push
        }
    }
}
