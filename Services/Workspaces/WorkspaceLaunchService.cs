using System.Text;
using ByteBench.Data;
using ByteBench.Services.Git;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Workspaces;

/// <summary>A workspace started (or found again) for an issue or a pull request.</summary>
/// <param name="Reused">The user already had a live workspace on that branch, so no second one was started.</param>
public sealed record LaunchResult(Workspace Workspace, string Branch, bool BranchCreated, bool Reused);

/// <summary>An issue or pull request a workspace was opened for.</summary>
public sealed record WorkspaceLinkView(string? IssueKey, string? IssueTitle, string? IssueUrl, int? PullRequestNumber, string? PullRequestTitle, string? PullRequestUrl);

/// <summary>
/// "Start workspace" on an issue or a pull request. For an issue the workspace opens on a branch named from the issue
/// (<c>KEY-12-fix-login</c>): an existing branch of that name is reused, otherwise it is created from the repository's default branch (which needs push
/// permission and respects branch protection). For a pull request it opens on the source branch. Either way the workspace is linked to the issue or
/// pull request, and a user who already has a live workspace on that branch gets that one back instead of a second.
/// </summary>
public sealed class WorkspaceLaunchService(WorkspaceService workspaces, IDbContextFactory<AppDbContext> dbFactory, RepositoryService repos, IssueService issues, ProjectAccess access)
{
    private const int MaxSlugLength = 40;

    /// <summary><c>KEY-12-fix-login</c>: the issue key, then the title as a lower-case slug (at most 40 characters, cut at a word).</summary>
    public static string BranchNameFor(string projectKey, int number, string title)
    {
        var sb = new StringBuilder();
        var lastDash = true;
        foreach (var raw in title.Normalize(NormalizationForm.FormD))
        {
            var c = char.ToLowerInvariant(raw);
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') { sb.Append(c); lastDash = false; }
            else if (!lastDash && char.GetUnicodeCategory(raw) != System.Globalization.UnicodeCategory.NonSpacingMark) { sb.Append('-'); lastDash = true; }
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > MaxSlugLength)
        {
            slug = slug[..MaxSlugLength];
            var cut = slug.LastIndexOf('-');
            slug = (cut > 10 ? slug[..cut] : slug).Trim('-');
        }
        return slug.Length == 0 ? $"{projectKey.ToUpperInvariant()}-{number}" : $"{projectKey.ToUpperInvariant()}-{number}-{slug}";
    }

    /// <summary>Opens a workspace on the issue's branch. <paramref name="repoSlug"/> is needed only when the project has several repositories.</summary>
    public async Task<LaunchResult> StartForIssueAsync(string projectKey, int number, string? repoSlug, int userId)
    {
        access.ActAs(userId);
        var issue = await issues.GetIssueAsync(projectKey, number) ?? throw new InvalidOperationException("Issue not found, or you don't have access to it.");
        var repo = await PickRepositoryAsync(issue.ProjectId, projectKey, repoSlug);
        var branch = BranchNameFor(issue.Project.Key, issue.Number, issue.Title);
        if (!WorkspaceLifecycle.IsSafeRef(branch) || !GitOps.IsValidBranchName(branch)) throw new InvalidOperationException("The issue's title can't be turned into a branch name.");

        var exists = await repos.Git(repo, path => GitOps.BranchExists(path, branch));
        var created = false;
        if (!exists)
        {
            if (await repos.Git(repo, path => GitOps.ResolveSha(path, repo.DefaultBranch)) == null)
                throw new InvalidOperationException("This repository is empty, so there is nothing to branch from. Push a first commit.");
            await repos.CreateBranchAsync(repo, branch, repo.DefaultBranch, userId); // PushCode, branch protection (Create) and archive checks
            created = true;
        }
        var source = $"issue:{issue.Project.Key}-{issue.Number}";
        var result = await StartOrReuseAsync(repo, branch, source, userId, created);
        await AddLinkAsync(result.Workspace.Id, issue.Id, null);
        return result;
    }

    /// <summary>Opens a workspace on the source branch of a pull request.</summary>
    public async Task<LaunchResult> StartForPullRequestAsync(string projectKey, string repoSlug, int number, int userId)
    {
        access.ActAs(userId);
        var repo = await repos.GetRepositoryAsync(projectKey, repoSlug) ?? throw new InvalidOperationException("Pull request not found, or you don't have access to it.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var pr = await db.PullRequests.AsNoTracking().FirstOrDefaultAsync(p => p.RepositoryId == repo.Id && p.Number == number)
                 ?? throw new InvalidOperationException("Pull request not found, or you don't have access to it.");
        if (pr.IsCrossFork) throw new InvalidOperationException("This pull request comes from a fork. Clone the fork's branch yourself, or start a workspace in the fork.");
        if (!await repos.Git(repo, path => GitOps.BranchExists(path, pr.SourceBranch)))
            throw new InvalidOperationException($"The source branch '{pr.SourceBranch}' no longer exists.");
        var result = await StartOrReuseAsync(repo, pr.SourceBranch, $"pr:{pr.Number}", userId, false);
        await AddLinkAsync(result.Workspace.Id, null, pr.Id);
        return result;
    }

    /// <summary>The issue or pull request behind a workspace (owner only: null for anyone else's).</summary>
    public async Task<WorkspaceLinkView?> GetLinksAsync(string publicId, int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var ws = await db.Workspaces.AsNoTracking().Where(w => w.PublicId == publicId && w.OwnerId == userId).Select(w => new { w.Id }).FirstOrDefaultAsync();
        if (ws == null) return null;
        var links = await db.WorkspaceLinks.AsNoTracking().Where(l => l.WorkspaceId == ws.Id)
            .Select(l => new
            {
                IssueKey = l.Issue == null ? null : l.Issue.Project.Key, IssueNumber = l.Issue == null ? 0 : l.Issue.Number, IssueTitle = l.Issue == null ? null : l.Issue.Title,
                PrKey = l.PullRequest == null ? null : l.PullRequest.Repository.Project.Key, PrSlug = l.PullRequest == null ? null : l.PullRequest.Repository.Slug,
                PrNumber = l.PullRequest == null ? 0 : l.PullRequest.Number, PrTitle = l.PullRequest == null ? null : l.PullRequest.Title,
            }).ToListAsync();
        var issue = links.FirstOrDefault(l => l.IssueKey != null);
        var pr = links.FirstOrDefault(l => l.PrKey != null);
        if (issue == null && pr == null) return new WorkspaceLinkView(null, null, null, null, null, null);
        return new WorkspaceLinkView(
            issue == null ? null : $"{issue.IssueKey}-{issue.IssueNumber}", issue?.IssueTitle, issue == null ? null : $"/p/{issue.IssueKey}/issues/{issue.IssueNumber}",
            pr == null ? null : pr.PrNumber, pr?.PrTitle, pr == null ? null : $"/p/{pr.PrKey}/repos/{pr.PrSlug}/pull-requests/{pr.PrNumber}");
    }

    /// <summary>The user's own live or recent workspaces linked to an issue (for the issue page).</summary>
    public Task<List<WorkspaceInfo>> ListForIssueAsync(int issueId, int userId) => ListLinkedAsync(l => l.IssueId == issueId, userId);

    /// <summary>The user's own live or recent workspaces linked to a pull request.</summary>
    public Task<List<WorkspaceInfo>> ListForPullRequestAsync(int pullRequestId, int userId) => ListLinkedAsync(l => l.PullRequestId == pullRequestId, userId);

    private async Task<List<WorkspaceInfo>> ListLinkedAsync(System.Linq.Expressions.Expression<Func<WorkspaceLink, bool>> predicate, int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var ids = await db.WorkspaceLinks.Where(predicate).Select(l => l.WorkspaceId).Distinct().ToListAsync();
        if (ids.Count == 0) return [];
        var mine = await workspaces.ListMineAsync(userId, 50);
        var idSet = (await db.Workspaces.AsNoTracking().Where(w => ids.Contains(w.Id)).Select(w => w.PublicId).ToListAsync()).ToHashSet();
        return mine.Where(w => idSet.Contains(w.Workspace.PublicId)).ToList();
    }

    // ---------- helpers ----------

    private async Task<Repository> PickRepositoryAsync(int projectId, string projectKey, string? repoSlug)
    {
        if (!string.IsNullOrWhiteSpace(repoSlug))
        {
            var one = await repos.GetRepositoryAsync(projectKey, repoSlug);
            return one != null && one.ProjectId == projectId ? one : throw new InvalidOperationException($"Repository '{repoSlug}' not found in {projectKey.ToUpperInvariant()}.");
        }
        var all = await repos.GetRepositoriesAsync(projectId);
        return all.Count switch
        {
            0 => throw new InvalidOperationException("This project has no repository to open a workspace on."),
            1 => all[0].Repository,
            _ => throw new InvalidOperationException("This project has several repositories: choose which one to open (repo=<slug>)."),
        };
    }

    private async Task<LaunchResult> StartOrReuseAsync(Repository repo, string branch, string source, int userId, bool created)
    {
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var live = await db.Workspaces.AsNoTracking().Where(w => w.OwnerId == userId && w.RepositoryId == repo.Id && w.Ref == branch
                                                                     && (w.State == WorkspaceState.Starting || w.State == WorkspaceState.Running))
                .OrderByDescending(w => w.CreatedAt).FirstOrDefaultAsync();
            if (live != null) return new LaunchResult(live, branch, created, true);
        }
        var ws = await workspaces.StartAsync(repo.Project.Key, repo.Slug, branch, source, userId);
        return new LaunchResult(ws, branch, created, false);
    }

    private async Task AddLinkAsync(int workspaceId, int? issueId, int? pullRequestId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.WorkspaceLinks.AnyAsync(l => l.WorkspaceId == workspaceId && l.IssueId == issueId && l.PullRequestId == pullRequestId)) return;
        db.WorkspaceLinks.Add(new WorkspaceLink { WorkspaceId = workspaceId, IssueId = issueId, PullRequestId = pullRequestId });
        await db.SaveChangesAsync();
    }
}
