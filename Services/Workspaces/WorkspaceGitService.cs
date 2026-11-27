using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Services.Audit;
using ByteBench.Services.Git;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Workspaces;

public sealed record GitChange(string Status, string Path);

/// <summary>What the workspace bar shows: the branch, how many files changed and where the branch stands against its remote.</summary>
public sealed record WorkspaceGitStatus(string? Branch, bool Detached, int ChangedFiles, IReadOnlyList<GitChange> Changes, bool ChangesTruncated, bool RemoteBranchExists,
    int Ahead, int Behind, string? LastSubject, bool CanPush, string? IssueKey, string? IssueTitle);

public sealed record WorkspacePullRequestResult(int Number, string Title, string Url, string SourceBranch, string TargetBranch, bool AlreadyExisted, string? IssueKey);

/// <summary>
/// The git actions of the workspace bar, for the owner of a running workspace: status, commit, push, and opening a pull request from the pushed branch.
/// Everything runs as the developer's user inside the container with the workspace's own git token; the server still applies branch protection and the
/// owner's current role to every push. The AI assistant has no access to any of it (it can't read the credential and never commits or pushes).
/// </summary>
public sealed partial class WorkspaceGitService(WorkspaceService workspaces, IWorkspaceBackend backend, IDbContextFactory<AppDbContext> dbFactory,
    RepositoryService repos, PullRequestService pullRequests, ProjectAccess access, AuditLog audit)
{
    public const int MaxListedChanges = 200;

    private const string StatusScript = """
        cd "$1"
        git status --porcelain=v1 -b 2>&1 || true
        echo "@@BB"
        b=$(git symbolic-ref --short -q HEAD || true)
        echo "branch=$b"
        if [ -n "$b" ] && git show-ref --verify --quiet "refs/remotes/origin/$b"; then
          echo "remote=1"
          echo "ahead=$(git rev-list --count "origin/$b..HEAD" 2>/dev/null || echo 0)"
          echo "behind=$(git rev-list --count "HEAD..origin/$b" 2>/dev/null || echo 0)"
        else
          echo "remote=0"
        fi
        echo "subject=$(git log -1 --format=%s 2>/dev/null | head -c 300 || true)"
        """;

    /// <summary>The branch, change count and sync state of the checkout, plus the issue the workspace was opened for.</summary>
    public async Task<WorkspaceGitStatus> GetStatusAsync(string publicId, int userId)
    {
        var (live, ws) = await workspaces.GetRunningAsync(publicId, userId);
        live.Touch();
        var dir = WorkspaceLifecycle.WorkDir(ws.Repository.Slug);
        var r = await backend.ExecAsync(live.ContainerId, new ExecSpec(["sh", "-c", StatusScript, "sh", dir]) { Timeout = TimeSpan.FromSeconds(30), MaxOutputBytes = 256 * 1024 }, CancellationToken.None);
        var (issueKey, issueTitle) = await LinkedIssueAsync(ws.Id);
        return Parse(r.Output, ws.CanPush, issueKey, issueTitle);
    }

    /// <summary>Parses the output of the status script. Pure, unit tested.</summary>
    public static WorkspaceGitStatus Parse(string output, bool canPush, string? issueKey = null, string? issueTitle = null)
    {
        var marker = output.IndexOf("@@BB", StringComparison.Ordinal);
        var statusPart = marker < 0 ? output : output[..marker];
        var meta = new Dictionary<string, string>();
        if (marker >= 0)
            foreach (var line in output[(marker + 4)..].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = line.IndexOf('=');
                if (eq > 0) meta[line[..eq]] = line[(eq + 1)..];
            }
        var changes = new List<GitChange>();
        var total = 0;
        foreach (var line in statusPart.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("##", StringComparison.Ordinal) || line.Length < 4 || line[2] != ' ') continue;
            total++;
            if (changes.Count < MaxListedChanges) changes.Add(new GitChange(line[..2].Trim().Length == 0 ? "?" : line[..2].Trim(), Plain(line[3..])));
        }
        var branch = meta.GetValueOrDefault("branch") is { Length: > 0 } b ? b : null;
        int Int(string k) => meta.TryGetValue(k, out var v) && int.TryParse(v, out var n) ? n : 0;
        return new WorkspaceGitStatus(branch, branch == null, total, changes, total > changes.Count, meta.GetValueOrDefault("remote") == "1", Int("ahead"), Int("behind"),
            meta.GetValueOrDefault("subject") is { Length: > 0 } s ? s : null, canPush, issueKey, issueTitle);
    }

    /// <summary>A path from <c>git status</c> without control characters (it is shown in the UI and returned by the API).</summary>
    private static string Plain(string path) => new(path.Where(c => !char.IsControl(c)).ToArray());

    /// <summary>Commits everything (optionally pushing it): the existing commit helper, so there is one code path for it.</summary>
    public Task<ExecResult> CommitAsync(string publicId, int userId, string message, bool push, string? branch) =>
        workspaces.CommitAsync(publicId, userId, message, push, branch);

    /// <summary>Pushes the current branch to the same-named branch on ByteBench. Needs push permission now, not only when the workspace started.</summary>
    public async Task<ExecResult> PushAsync(string publicId, int userId)
    {
        var (live, ws) = await workspaces.GetRunningAsync(publicId, userId);
        var branch = await RequireBranchAsync(live, ws);
        await RequirePushAsync(ws, userId);
        live.Touch();
        var push = await backend.ExecAsync(live.ContainerId, new ExecSpec(["git", "push", "origin", $"HEAD:refs/heads/{branch}"])
        {
            WorkDir = WorkspaceLifecycle.WorkDir(ws.Repository.Slug), Timeout = TimeSpan.FromMinutes(5),
            Env = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" },
        }, CancellationToken.None);
        await audit.RecordAsync("workspace.push", "workspace", ws.PublicId, ws.ProjectId, new { pushed = push.Ok, branch }, userId);
        return new ExecResult(push.ExitCode, WorkspaceLifecycle.Safe(push.Output), push.TimedOut);
    }

    /// <summary>
    /// Opens a pull request from the workspace's pushed branch into <paramref name="target"/> (default: the repository's default branch), pushing first so the
    /// branch is current. When the workspace was opened for an issue the description names it (<c>Fixes KEY-12</c>, or <c>Related to KEY-12</c> when
    /// <paramref name="closeIssue"/> is false), which links the pull request to the issue. An open pull request for the same branch is returned as is.
    /// </summary>
    public async Task<WorkspacePullRequestResult> OpenPullRequestAsync(string publicId, int userId, string? title, string? description, string? target, bool closeIssue = true, bool draft = false)
    {
        var (live, ws) = await workspaces.GetRunningAsync(publicId, userId);
        var repo = await repos.GetRepositoryAsync(ws.Repository.Project?.Key ?? await ProjectKeyAsync(ws.ProjectId), ws.Repository.Slug)
                   ?? throw new InvalidOperationException("Repository not found, or you don't have access to it.");
        var branch = await RequireBranchAsync(live, ws);
        target = string.IsNullOrWhiteSpace(target) ? repo.DefaultBranch : target.Trim();
        if (!WorkspaceLifecycle.IsSafeRef(target)) throw new InvalidOperationException("That target branch name isn't valid.");
        if (branch == target) throw new InvalidOperationException($"You are on '{target}', the branch a pull request would merge into. Create a branch first (git switch -c my-change) and commit there.");
        await RequirePushAsync(ws, userId);

        var status = await GetStatusAsync(publicId, userId);
        if (status.ChangedFiles > 0) throw new InvalidOperationException($"You have {status.ChangedFiles} uncommitted file(s). Commit them first.");

        var pushed = await PushAsync(publicId, userId);
        if (!pushed.Ok) throw new InvalidOperationException("Pushing the branch failed: " + pushed.Output);

        await using var db = await dbFactory.CreateDbContextAsync();
        var existing = await db.PullRequests.AsNoTracking().Include(p => p.Repository).ThenInclude(r => r.Project)
            .FirstOrDefaultAsync(p => p.RepositoryId == repo.Id && p.State == PullRequestState.Open && !p.IsCrossFork && p.SourceBranch == branch && p.TargetBranch == target);
        var (issueKey, issueTitle, issueId) = await LinkedIssueForOpenAsync(ws.Id);
        if (existing != null)
        {
            await LinkPullRequestAsync(ws.Id, existing.Id, issueId);
            return new WorkspacePullRequestResult(existing.Number, existing.Title, RepoUrls.PullRequest(existing), branch, target, true, issueKey);
        }

        title = string.IsNullOrWhiteSpace(title)
            ? (issueKey != null ? $"{issueKey}: {issueTitle}" : status.LastSubject ?? branch)
            : title.Trim();
        if (title.Length > 200) title = title[..200];
        var body = description?.Trim() ?? "";
        if (issueKey != null && !Regex.IsMatch(body, @"(?<![A-Za-z0-9])" + Regex.Escape(issueKey) + @"(?!\d)", RegexOptions.IgnoreCase))
            body = (body.Length > 0 ? body + "\n\n" : "") + (closeIssue ? "Fixes " : "Related to ") + issueKey;

        var pr = await pullRequests.CreateAsync(repo, title, body.Length == 0 ? null : body, branch, target, userId, null, draft);
        await LinkPullRequestAsync(ws.Id, pr.Id, issueId);
        await audit.RecordAsync("workspace.pull_request_opened", "workspace", ws.PublicId, ws.ProjectId, new { pr.Number, source = branch, target, issue = issueKey }, userId);
        pr.Repository = repo;
        return new WorkspacePullRequestResult(pr.Number, pr.Title, RepoUrls.PullRequest(repo, pr.Number), branch, target, false, issueKey);
    }

    // ---------- helpers ----------

    private async Task<string> ProjectKeyAsync(int projectId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Projects.Where(p => p.Id == projectId).Select(p => p.Key).FirstAsync();
    }

    private async Task<string> RequireBranchAsync(LiveWorkspace live, Workspace ws)
    {
        var r = await backend.ExecAsync(live.ContainerId, new ExecSpec(["git", "symbolic-ref", "--short", "-q", "HEAD"])
            { WorkDir = WorkspaceLifecycle.WorkDir(ws.Repository.Slug), Timeout = TimeSpan.FromSeconds(15) }, CancellationToken.None);
        var branch = r.Output.Trim();
        if (!r.Ok || branch.Length == 0)
            throw new InvalidOperationException("The workspace isn't on a branch (a tag or commit is checked out). Create one first: git switch -c my-change.");
        if (!WorkspaceLifecycle.IsSafeRef(branch)) throw new InvalidOperationException("The current branch name isn't one this can push.");
        return branch;
    }

    private async Task RequirePushAsync(Workspace ws, int userId)
    {
        if (!ws.CanPush) throw new InvalidOperationException("This workspace can't push: you only have read access to this repository (or it is archived or a mirror).");
        if (!await access.CanAsync(ws.ProjectId, Permission.PushCode, userId)) throw new InvalidOperationException("You no longer have permission to push to this repository.");
    }

    private async Task<(string? Key, string? Title)> LinkedIssueAsync(int workspaceId)
    {
        var (key, title, _) = await LinkedIssueForOpenAsync(workspaceId);
        return (key, title);
    }

    private async Task<(string? Key, string? Title, int? IssueId)> LinkedIssueForOpenAsync(int workspaceId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.WorkspaceLinks.AsNoTracking().Where(l => l.WorkspaceId == workspaceId && l.IssueId != null)
            .Select(l => new { l.IssueId, l.Issue!.Number, l.Issue.Title, Key = l.Issue.Project.Key }).FirstOrDefaultAsync();
        return row == null ? (null, null, null) : ($"{row.Key}-{row.Number}", row.Title, row.IssueId);
    }

    private async Task LinkPullRequestAsync(int workspaceId, int prId, int? issueId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.WorkspaceLinks.AnyAsync(l => l.WorkspaceId == workspaceId && l.PullRequestId == prId)) return;
        var row = await db.WorkspaceLinks.FirstOrDefaultAsync(l => l.WorkspaceId == workspaceId && l.PullRequestId == null && l.IssueId == issueId);
        if (row != null) row.PullRequestId = prId;
        else db.WorkspaceLinks.Add(new WorkspaceLink { WorkspaceId = workspaceId, IssueId = issueId, PullRequestId = prId });
        await db.SaveChangesAsync();
    }
}
