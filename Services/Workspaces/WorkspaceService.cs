using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Services.Accounts;
using ByteBench.Services.Audit;
using ByteBench.Services.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Workspaces;

/// <summary>A running (or recent) workspace as the UI and API see it.</summary>
public sealed record WorkspaceInfo(Workspace Workspace, string ProjectKey, string RepoSlug, string OwnerName)
{
    public string RepositoryPath => $"{ProjectKey}/{RepoSlug}";
}

public sealed record WorkspaceStatus(bool Enabled, bool SiteSwitchOn, string? Problem, WorkspaceOriginMode OriginMode);

/// <summary>
/// Workspaces for the signed-in user: start, stop, restart, list, the commit and push helper and port detection. Permission checks live
/// here (ProjectAccess is the authority; a workspace is private to its owner, site admins may list and stop but never reach one);
/// the Docker work is in <see cref="WorkspaceLifecycle"/>.
/// </summary>
public sealed partial class WorkspaceService(IDbContextFactory<AppDbContext> dbFactory, IWorkspaceBackend backend, WorkspaceRuntime runtime,
    WorkspaceLifecycle lifecycle, WorkspaceUrls urls, IOptionsMonitor<WorkspaceOptions> options, ProjectAccess access, RepositoryService repos,
    RepoStorage storage, WorkspaceImage image, SiteSettings siteSettings, AuditLog audit, WorkspaceLimitService limits, WorkspacePrebuildService prebuilds)
{
    public const string EnabledKey = "workspaces.enabled";
    public const string AiEnabledKey = "workspaces.ai.enabled";

    private WorkspaceOptions Opts => options.CurrentValue;

    // ---------- Switches ----------

    /// <summary>
    /// The admin switch. Unset means on (workspaces are usable as soon as Docker is reachable, bounded by the per-user and server limits and the
    /// container restrictions); an administrator can switch them off, which is stored explicitly.
    /// </summary>
    public async Task<bool> IsEnabledAsync() => await siteSettings.GetAsync(EnabledKey) != "false";

    /// <summary>The admin switch for the assistant (on by default; it still needs the Anthropic API and the workspaces switch).</summary>
    public async Task<bool> IsAiEnabledSiteAsync() => await siteSettings.GetAsync(AiEnabledKey) != "false";

    public async Task SetEnabledAsync(bool on, int adminId)
    {
        await RequireAdminAsync(adminId);
        await siteSettings.SetAsync(EnabledKey, on ? "true" : "false", adminId);
        await audit.RecordAsync("workspace.admin_switch", "workspaces", null, details: new { enabled = on }, actorId: adminId);
    }

    public async Task SetAiEnabledSiteAsync(bool on, int adminId)
    {
        await RequireAdminAsync(adminId);
        await siteSettings.SetAsync(AiEnabledKey, on ? "true" : "false", adminId);
        await audit.RecordAsync("workspace.admin_ai_switch", "workspaces", null, details: new { enabled = on }, actorId: adminId);
    }

    public async Task<ProjectWorkspaceSettings> GetProjectSettingsAsync(int projectId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ProjectWorkspaceSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == projectId) ?? new ProjectWorkspaceSettings { ProjectId = projectId };
    }

    /// <summary>Turns workspaces and their AI assistant on or off for a project (Maintainer+).</summary>
    public async Task SetProjectSettingsAsync(int projectId, bool enabled, bool aiEnabled, int userId)
    {
        await access.RequireRoleAsync(projectId, ProjectRole.Maintainer, "change the workspace settings", userId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.ProjectWorkspaceSettings.FirstOrDefaultAsync(s => s.ProjectId == projectId);
        if (row == null) db.ProjectWorkspaceSettings.Add(row = new ProjectWorkspaceSettings { ProjectId = projectId });
        row.Enabled = enabled;
        row.AiEnabled = aiEnabled;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync("workspace.project_settings", "project", projectId, projectId, new { enabled, aiEnabled }, userId);
    }

    /// <summary>Whether a workspace can be started right now (and why not), for hiding buttons and for the start page.</summary>
    public async Task<WorkspaceStatus> GetStatusAsync(int? projectId = null)
    {
        var on = await IsEnabledAsync();
        if (!on) return new WorkspaceStatus(false, false, "Workspaces are turned off on this server. A site administrator can turn them on under Admin → Workspaces.", urls.Mode);
        if (urls.Mode == WorkspaceOriginMode.Unavailable)
            return new WorkspaceStatus(false, true, "Workspaces need Workspaces:BaseDomain (a wildcard domain for the IDE's own origin) or, less safely, Workspaces:AllowSharedOrigin.", urls.Mode);
        if (projectId is { } pid && !(await GetProjectSettingsAsync(pid)).Enabled)
            return new WorkspaceStatus(false, true, "Workspaces are turned off for this project (project settings).", urls.Mode);
        if (!await backend.IsAvailableAsync()) return new WorkspaceStatus(false, true, "Docker isn't available on this server. Install Docker and make it reachable for ByteBench (see Workspaces in the documentation).", urls.Mode);
        if (image.State != WorkspaceImageState.Ready) await image.EnsureAsync();
        if (image.State != WorkspaceImageState.Ready)
            return new WorkspaceStatus(false, true, image.Problem ?? "The workspace image isn't ready yet; try again in a moment.", urls.Mode);
        return new WorkspaceStatus(true, true, null, urls.Mode);
    }

    // ---------- Start ----------

    /// <summary>
    /// Starts a workspace for the user on a branch, tag or commit. Refused when workspaces are off, for projects that switched them
    /// off, without ViewProject, for an empty repository, and past the per-user or global cap. Returns at once; the container comes up in the background.
    /// </summary>
    public async Task<Workspace> StartAsync(string projectKey, string slug, string? reference, string? source, int userId)
    {
        var status = await GetStatusAsync();
        if (!status.Enabled) throw new InvalidOperationException(status.Problem ?? "Workspaces are not available.");
        access.ActAs(userId);
        var repo = await repos.GetRepositoryAsync(projectKey, slug) ?? throw new InvalidOperationException("Repository not found, or you don't have access to it.");
        if (!(await GetProjectSettingsAsync(repo.ProjectId)).Enabled) throw new InvalidOperationException("Workspaces are turned off for this project.");
        var role = await access.GetRoleAsync(repo.ProjectId, userId) ?? throw new InvalidOperationException("Repository not found, or you don't have access to it.");

        var path = storage.PathFor(repo.Project.Key, repo.Slug);
        var name = string.IsNullOrWhiteSpace(reference) ? repo.DefaultBranch : reference.Trim();
        if (!WorkspaceLifecycle.IsSafeRef(name)) throw new InvalidOperationException("That branch, tag or commit name isn't valid.");
        var sha = await Task.Run(() => GitOps.ResolveSha(path, name));
        if (sha == null) throw new InvalidOperationException($"'{name}' doesn't exist in this repository" + (GitOps.ResolveSha(path, "HEAD") == null ? " (it is empty)." : "."));

        var canPush = ProjectAccess.RoleHas(role, Permission.PushCode) && !repo.IsArchived;
        if (canPush)
        {
            await using var check = await dbFactory.CreateDbContextAsync();
            if (await Mirroring.MirrorService.IsPullMirrorAsync(check, repo.Id)) canPush = false;
        }

        var o = Opts;
        var prebuild = await prebuilds.FindForStartAsync(repo, name); // a snapshot with setup already done, or null: a normal start
        var gitToken = WorkspaceTokens.NewGitToken();
        var connectionToken = WorkspaceTokens.NewConnectionToken();
        Workspace ws;
        await runtime.StartGate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var live = await db.Workspaces.Where(w => w.State == WorkspaceState.Starting || w.State == WorkspaceState.Running || w.State == WorkspaceState.Stopping)
                .Select(w => w.OwnerId).ToListAsync();
            if (live.Count >= o.MaxTotal) throw new InvalidOperationException($"The server is at its limit of {o.MaxTotal} workspaces. Try again later.");
            var resources = await limits.EnforceAsync(db, userId); // the per-user caps (running, total, CPU, memory, disk), with a message that says which
            ws = new Workspace
            {
                PublicId = WorkspaceTokens.NewPublicId(), OwnerId = userId, RepositoryId = repo.Id, ProjectId = repo.ProjectId, Ref = name, CommitSha = sha,
                Source = source is { Length: > 0 } ? (source.Length > 100 ? source[..100] : source) : null,
                State = WorkspaceState.Starting, Image = prebuild?.ImageTag ?? o.Image, EgressMode = o.EgressMode is "none" or "open" ? o.EgressMode : "public-only", CanPush = canPush,
                ConnectionTokenHash = WorkspaceTokens.Hash(connectionToken), GitTokenHash = WorkspaceTokens.Hash(gitToken),
                IdleTimeoutMinutes = await limits.IdleMinutesAsync(db, userId), ExpiresAt = DateTime.UtcNow.AddHours(Math.Max(1, o.MaxLifetimeHours)) + TimeSpan.FromSeconds(o.StartupTimeoutSeconds),
            };
            db.Workspaces.Add(ws);
            db.WorkspaceAllocations.Add(new WorkspaceAllocation { Workspace = ws, PrebuildId = prebuild?.Id, CpuMillis = resources.CpuMillis, MemoryMb = resources.MemoryMb, DiskMb = resources.DiskMb });
            await db.SaveChangesAsync();
        }
        finally { runtime.StartGate.Release(); }

        runtime.Track(Task.Run(() => lifecycle.ProvisionAsync(ws.Id, gitToken, connectionToken)));
        return ws;
    }

    // ---------- Read ----------

    private IQueryable<Workspace> WithRepo(AppDbContext db) => db.Workspaces.AsNoTracking().Include(w => w.Owner).Include(w => w.Repository).ThenInclude(r => r.Project);

    private static WorkspaceInfo View(Workspace w) => new(w, w.Repository.Project.Key, w.Repository.Slug, w.Owner.UserName ?? "");

    /// <summary>The user's own workspaces: running ones first, then the most recent finished ones.</summary>
    public async Task<List<WorkspaceInfo>> ListMineAsync(int userId, int recent = 20)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var all = await WithRepo(db).Where(w => w.OwnerId == userId).OrderByDescending(w => w.CreatedAt).Take(100).ToListAsync();
        var live = all.Where(w => w.IsLive).ToList();
        return live.Concat(all.Where(w => !w.IsLive).Take(recent)).Select(View).ToList();
    }

    /// <summary>The user's workspace with this public id; null for anyone else's (a workspace's existence isn't revealed).</summary>
    public async Task<WorkspaceInfo?> GetMineAsync(string publicId, int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var w = await WithRepo(db).FirstOrDefaultAsync(x => x.PublicId == publicId && x.OwnerId == userId);
        return w == null ? null : View(w);
    }

    /// <summary>Every workspace, for the admin page (site admins only).</summary>
    public async Task<List<WorkspaceInfo>> ListAllAsync(int adminId, int recent = 50)
    {
        await RequireAdminAsync(adminId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var all = await WithRepo(db).OrderByDescending(w => w.CreatedAt).Take(300).ToListAsync();
        return all.Where(w => w.IsLive).Concat(all.Where(w => !w.IsLive).Take(recent)).Select(View).ToList();
    }

    // ---------- Stop & restart ----------

    /// <summary>The owner stops a workspace: the container and everything in it is deleted. Not found for anyone else's.</summary>
    public async Task StopAsync(string publicId, int userId)
    {
        var w = await GetMineAsync(publicId, userId) ?? throw new InvalidOperationException("Workspace not found.");
        await lifecycle.StopAsync(w.Workspace.Id, "stopped by the owner", userId);
    }

    /// <summary>A site admin stops anyone's workspace (the admin page).</summary>
    public async Task AdminStopAsync(string publicId, int adminId)
    {
        await RequireAdminAsync(adminId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var id = await db.Workspaces.Where(w => w.PublicId == publicId).Select(w => (int?)w.Id).FirstOrDefaultAsync() ?? throw new InvalidOperationException("Workspace not found.");
        await lifecycle.StopAsync(id, "stopped by an administrator", adminId);
    }

    public async Task AdminStopAllAsync(int adminId)
    {
        await RequireAdminAsync(adminId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var ids = await db.Workspaces.Where(w => w.State == WorkspaceState.Starting || w.State == WorkspaceState.Running).Select(w => w.Id).ToListAsync();
        foreach (var id in ids) await lifecycle.StopAsync(id, "stopped by an administrator", adminId);
    }

    public async Task RestartAsync(string publicId, int userId)
    {
        var w = await GetMineAsync(publicId, userId) ?? throw new InvalidOperationException("Workspace not found.");
        await lifecycle.RestartAsync(w.Workspace.Id, userId);
    }

    // ---------- Access to the IDE and previews ----------

    /// <summary>
    /// The URL that opens the IDE (<paramref name="port"/> null) or a preview of a port: on a workspace's own origin it carries a
    /// single-use ticket (60 s) that the origin turns into its own cookie, so ByteBench's session never leaves ByteBench's origin.
    /// Null if the workspace isn't running or isn't the user's.
    /// </summary>
    public async Task<string?> EntryUrlAsync(string publicId, int userId, Uri mainBase, int? port = null)
    {
        if (!WorkspaceTokens.PublicIdPattern.IsMatch(publicId)) return null;
        var live = runtime.Find(publicId);
        if (live == null || live.OwnerId != userId) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await db.Workspaces.AnyAsync(w => w.Id == live.Id && w.OwnerId == userId && w.State == WorkspaceState.Running)) return null;
        var audience = port is { } p ? WorkspaceUrls.PreviewAudience(p) : WorkspaceUrls.IdeAudience;
        if (port is < 1 or > 65535) return null;
        live.Touch();
        var ticket = urls.Mode == WorkspaceOriginMode.Subdomain ? runtime.IssueTicket(live.Id, userId, audience, mainBase.GetLeftPart(UriPartial.Authority)) : null;
        return urls.EntryUrl(mainBase, audience, publicId, ticket);
    }

    // ---------- Inside the container ----------

    /// <summary>Ports something in the workspace is listening on (candidates for the preview menu).</summary>
    public async Task<List<int>> ListeningPortsAsync(string publicId, int userId)
    {
        var live = RequireLive(publicId, userId);
        var r = await backend.ExecAsync(live.ContainerId, new ExecSpec(["cat", "/proc/net/tcp", "/proc/net/tcp6"]) { Timeout = TimeSpan.FromSeconds(10) }, CancellationToken.None);
        return r.Ok ? ParseListeningPorts(r.Output, live.IdePort) : [];
    }

    /// <summary>LISTEN sockets (state 0A) from /proc/net/tcp output, without the IDE port, ephemeral ports (32768+) and duplicates.</summary>
    public static List<int> ParseListeningPorts(string procNetTcp, int idePort)
    {
        var ports = new SortedSet<int>();
        foreach (var line in procNetTcp.Split('\n'))
        {
            var cols = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 4 || cols[3] != "0A") continue;
            var colon = cols[1].LastIndexOf(':');
            if (colon < 0 || !int.TryParse(cols[1][(colon + 1)..], System.Globalization.NumberStyles.HexNumber, null, out var port)) continue;
            if (port is >= 1 and < 32768 && port != idePort) ports.Add(port);
        }
        return ports.ToList();
    }

    /// <summary><c>git status</c> of the checkout.</summary>
    public async Task<string> GitStatusAsync(string publicId, int userId)
    {
        var (live, ws) = await RequireRunningAsync(publicId, userId);
        live.Touch();
        var dir = WorkspaceLifecycle.WorkDir(ws.Repository.Slug);
        var r = await backend.ExecAsync(live.ContainerId, new ExecSpec(["git", "status", "--short", "--branch"]) { WorkDir = dir, Timeout = TimeSpan.FromSeconds(30) }, CancellationToken.None);
        return r.Output.Trim();
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/@+-]{0,199}$")]
    private static partial Regex BranchPattern();

    private const string CommitScript = """
        set -e
        cd "$1"
        git add -A
        if git diff --cached --quiet; then echo "Nothing to commit." >&2; exit 3; fi
        git commit -q -m "$2"
        git log -1 --oneline
        """;

    /// <summary>
    /// The commit and push helper (the user's own action: the assistant never commits or pushes). Commits everything with the user as author and, if asked,
    /// pushes <c>HEAD</c> to <paramref name="branch"/> (default: the workspace's branch) with the workspace's git token; the server still applies
    /// branch protection, so a protected branch refuses it and the user can push to a new branch and open a pull request.
    /// </summary>
    public async Task<ExecResult> CommitAsync(string publicId, int userId, string message, bool push, string? branch)
    {
        var (live, ws) = await RequireRunningAsync(publicId, userId);
        message = message?.Trim() ?? "";
        if (message.Length is 0 or > 5000 || message.Contains('\0')) throw new InvalidOperationException("Write a commit message (up to 5,000 characters).");
        if (push && !ws.CanPush) throw new InvalidOperationException("This workspace can't push: you only have read access to this repository (or it is archived or a mirror).");
        if (push)
        {
            // Re-check the live permission too: a role removed since the start must not push (the git endpoint checks again).
            if (!await access.CanAsync(ws.ProjectId, Permission.PushCode, userId)) throw new InvalidOperationException("You no longer have permission to push to this repository.");
            branch = string.IsNullOrWhiteSpace(branch) ? (ws.Ref is var r0 && !IsCommitLike(r0) ? r0 : null) : branch.Trim();
            if (branch == null || !BranchPattern().IsMatch(branch) || branch.Contains("..") || branch.EndsWith('/') || branch.EndsWith(".lock"))
                throw new InvalidOperationException("Give a branch name to push to.");
        }
        live.Touch();
        var dir = WorkspaceLifecycle.WorkDir(ws.Repository.Slug);
        var commit = await backend.ExecAsync(live.ContainerId, new ExecSpec(["sh", "-c", CommitScript, "sh", dir, message]) { Timeout = TimeSpan.FromSeconds(60) }, CancellationToken.None);
        if (!commit.Ok) return commit;
        var output = commit.Output.Trim();
        var pushed = false;
        if (push)
        {
            var p = await backend.ExecAsync(live.ContainerId, new ExecSpec(["git", "push", "origin", $"HEAD:refs/heads/{branch}"])
            {
                WorkDir = dir, Timeout = TimeSpan.FromMinutes(5), Env = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" },
            }, CancellationToken.None);
            output += "\n" + p.Output.Trim();
            pushed = p.Ok;
            if (!p.Ok) commit = new ExecResult(p.ExitCode, WorkspaceLifecycle.Safe(output), p.TimedOut);
        }
        await audit.RecordAsync("workspace.commit", "workspace", ws.PublicId, ws.ProjectId, new { pushed, push, branch }, userId);
        return pushed || !push ? new ExecResult(0, WorkspaceLifecycle.Safe(output)) : commit;
    }

    private static bool IsCommitLike(string r) => r.Length >= 7 && r.All(Uri.IsHexDigit);

    // ---------- helpers ----------

    private LiveWorkspace RequireLive(string publicId, int userId)
    {
        var live = WorkspaceTokens.PublicIdPattern.IsMatch(publicId) ? runtime.Find(publicId) : null;
        if (live == null || live.OwnerId != userId) throw new InvalidOperationException("Workspace not found, or it isn't running.");
        return live;
    }

    /// <summary>The user's running workspace with its repository, or an exception that doesn't distinguish "not yours" from "not there".</summary>
    public Task<(LiveWorkspace Live, Workspace Ws)> GetRunningAsync(string publicId, int userId) => RequireRunningAsync(publicId, userId);

    private async Task<(LiveWorkspace Live, Workspace Ws)> RequireRunningAsync(string publicId, int userId)
    {
        var live = RequireLive(publicId, userId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var ws = await db.Workspaces.Include(w => w.Repository).FirstOrDefaultAsync(w => w.Id == live.Id && w.OwnerId == userId && w.State == WorkspaceState.Running)
                 ?? throw new InvalidOperationException("Workspace not found, or it isn't running.");
        return (live, ws);
    }

    private async Task RequireAdminAsync(int userId)
    {
        if (!await access.IsAdminAsync(userId)) throw new ForbiddenException("Only site administrators can do this.");
    }
}
