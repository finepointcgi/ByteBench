using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Git;

/// <summary>
/// Jira-style smart links: after a push, any commit whose message mentions an issue key
/// (e.g. "WEB-3") gets an activity entry on that issue. Each commit is recorded once per issue.
/// </summary>
public class IssueCommitPushListener(IDbContextFactory<AppDbContext> dbFactory, RepoStorage storage, LiveEvents live, ILogger<IssueCommitPushListener> log)
    : IGitPushListener
{
    private const int ScanLimit = 300;

    public async Task OnPushAsync(int repositoryId, int userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var repo = await db.Repositories.Include(r => r.Project).FirstOrDefaultAsync(r => r.Id == repositoryId, ct);
        if (repo == null) return;

        var key = repo.Project.Key;
        var path = storage.PathFor(key, repo.Slug);
        var commits = await Task.Run(() => GitOps.FindCommits(path, IssueKeys.Any(key), ScanLimit, take: ScanLimit), ct);
        if (commits.Count == 0) return;

        var numbers = IssueKeys.Numbers(key, commits.Select(c => c.Message));
        var issues = await db.Issues.Where(i => i.ProjectId == repo.ProjectId && numbers.Contains(i.Number))
            .ToDictionaryAsync(i => i.Number, ct);

        var added = 0;
        foreach (var c in commits.OrderBy(c => c.When))
        {
            var link = $"/p/{key}/repos/{repo.Slug}/commits/{c.Sha}";
            foreach (var n in IssueKeys.Numbers(key, [c.Message]))
            {
                if (!issues.TryGetValue(n, out var issue)) continue;
                if (await db.Activities.AnyAsync(a => a.IssueId == issue.Id && a.Text.Contains(link), ct)) continue;

                db.Activities.Add(new Activity
                {
                    IssueId = issue.Id, ActorId = userId, CreatedAt = DateTime.UtcNow,
                    Text = $"referenced this issue in commit [`{c.ShortSha}`]({link}) on **{repo.Slug}**: {c.MessageShort}",
                });
                issue.UpdatedAt = DateTime.UtcNow;
                added++;
            }
        }
        await db.SaveChangesAsync(ct);
        if (added > 0)
        {
            live.Publish([.. issues.Values.Select(i => (LiveEvent)new IssueChanged(repo.ProjectId, i.Id)), new ActivityChanged()]);
            log.LogInformation("Linked {Count} commit reference(s) to issues after push to {Key}/{Slug}", added, key, repo.Slug);
        }
    }
}
