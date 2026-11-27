using ByteBench.Data;
using ByteBench.Services.Accounts;
using ByteBench.Services.Ai;
using ByteBench.Services.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Workspaces;

public sealed record AiRunAvailability(bool Available, string? Reason, bool BotIsMember);

public sealed record AiRunView(AiRun Run, string ProjectKey, int IssueNumber, string IssueTitle, string RepoSlug, string RequestedBy, int? PullRequestNumber)
{
    public string IssueKey => $"{ProjectKey}-{IssueNumber}";
}

/// <summary>
/// Task-driven AI runs as the people and the API see them: ask for one (assign the issue to the AI user, "Let AI work on this", REST), look at it,
/// cancel it. A request is refused unless runs are on (site switch and <c>Workspaces:AiRuns:Enabled</c>), the AI user is a member of the project
/// with a role that may push, open pull requests and comment (it never does more than that role allows), the issue is open and has no run in
/// flight, and the queue and the per-project daily limit have room. The work itself is <see cref="AiRunExecutor"/>, started by <see cref="AiRunWorker"/>.
/// </summary>
public sealed class AiRunService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, AiRunQueue queue, SiteSettings site,
    IOptionsMonitor<WorkspaceOptions> options, WorkspaceAiService workspaceAi, MemberService members, AuditLog audit)
{
    public const string EnabledKey = "workspaces.airuns.enabled";

    private AiRunOptions Opts => options.CurrentValue.AiRuns;

    public async Task<bool> IsEnabledSiteAsync() => await site.GetAsync(EnabledKey) != "false";

    public async Task SetEnabledSiteAsync(bool on, int adminId)
    {
        if (!await access.IsAdminAsync(adminId)) throw new ForbiddenException("Only site administrators can do this.");
        await site.SetAsync(EnabledKey, on ? "true" : "false", adminId);
        await audit.RecordAsync("ai_run.admin_switch", "ai_runs", null, details: new { enabled = on }, actorId: adminId);
    }

    /// <summary>Whether a run can be asked for in this project right now, and why not.</summary>
    public async Task<AiRunAvailability> GetAvailabilityAsync(int projectId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var botId = await AiBot.EnsureAsync(db);
        var role = await access.GetRoleAsync(projectId, botId);
        var member = role != null && await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == botId);
        if (!Opts.Enabled) return new(false, "AI runs are turned off in the server's configuration (Workspaces:AiRuns:Enabled).", member);
        if (!await IsEnabledSiteAsync()) return new(false, "AI runs are turned off by a site administrator.", member);
        var status = await workspaceAi.GetStatusAsync(projectId);
        if (!status.Enabled) return new(false, status.DisabledReason, member);
        if (role == null) return new(false, $"{AiBot.DisplayName} isn't a member of this project. Add it as a Developer to let it work on issues.", false);
        foreach (var (p, what) in new[] { (Permission.PushCode, "push branches"), (Permission.CreatePullRequest, "open pull requests"), (Permission.Comment, "comment on issues") })
            if (!RoleHas(role.Value, p)) return new(false, $"{AiBot.DisplayName} has the {role} role in this project, which can't {what}. Make it a Developer.", member);
        return new(true, null, member);
    }

    private static bool RoleHas(ProjectRole role, Permission p) => ProjectAccess.RoleHas(role, p);

    /// <summary>The AI user, if it can be assigned issues in this project (so the issue page can offer it); null otherwise.</summary>
    public async Task<User?> GetAssignableBotAsync(int projectId)
    {
        if (!(await GetAvailabilityAsync(projectId)).Available) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        var id = await AiBot.EnsureAsync(db);
        return await db.Users.AsNoTracking().FirstAsync(u => u.Id == id);
    }

    /// <summary>Adds the AI user to the project as a Developer (needs ManageMembers).</summary>
    public async Task EnableForProjectAsync(int projectId, int actorId)
    {
        await access.RequireAsync(projectId, Permission.ManageMembers, actorId);
        var current = await GetAvailabilityAsync(projectId);
        if (current.BotIsMember && current.Available) return;
        if (current.BotIsMember) throw new InvalidOperationException(current.Reason ?? $"{AiBot.DisplayName} is already a member.");
        await members.AddMemberAsync(projectId, AiBot.UserName, ProjectRole.Developer, actorId);
    }

    // ---------- request ----------

    /// <summary>
    /// Asks for a run on an issue. <paramref name="repositorySlug"/> null: the project's oldest repository that isn't archived. When the issue
    /// already has a run in flight that run is returned (<c>Created</c> false). Needs EditIssues on the issue.
    /// </summary>
    public async Task<(AiRun Run, bool Created)> RequestAsync(int issueId, int actorId, string trigger, string? repositorySlug = null)
    {
        await access.RequireForIssueAsync(issueId, Permission.EditIssues, actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var issue = await db.Issues.Include(i => i.Project).FirstOrDefaultAsync(i => i.Id == issueId) ?? throw new InvalidOperationException("Issue not found.");
        var availability = await GetAvailabilityAsync(issue.ProjectId);
        if (!availability.Available) throw new InvalidOperationException(availability.Reason ?? "AI runs aren't available.");
        if (issue.Status.IsClosed()) throw new InvalidOperationException("This issue is closed. Reopen it first.");

        if (await db.AiRuns.AsNoTracking().FirstOrDefaultAsync(r => r.IssueId == issueId && (r.Status == AiRunStatus.Queued || r.Status == AiRunStatus.Starting || r.Status == AiRunStatus.Running)) is { } active)
            return (active, false);

        var repos = db.Repositories.Where(r => r.ProjectId == issue.ProjectId && !r.IsArchived);
        var repo = repositorySlug is { Length: > 0 } slug
            ? await repos.FirstOrDefaultAsync(r => r.Slug == slug.ToLowerInvariant()) ?? throw new InvalidOperationException($"Repository '{slug}' not found in this project, or it is archived.")
            : await repos.OrderBy(r => r.Id).FirstOrDefaultAsync() ?? throw new InvalidOperationException("This project has no repository the AI could work in.");

        var o = Opts;
        if (await db.AiRuns.CountAsync(r => r.Status == AiRunStatus.Queued) >= o.MaxQueued)
            throw new InvalidOperationException($"The AI run queue is full ({o.MaxQueued} waiting). Try again later.");
        var since = DateTime.UtcNow.AddHours(-24);
        if (await db.AiRuns.CountAsync(r => r.ProjectId == issue.ProjectId && r.CreatedAt >= since) >= o.MaxRunsPerProjectPerDay)
            throw new InvalidOperationException($"This project reached its limit of {o.MaxRunsPerProjectPerDay} AI runs per day.");

        var run = new AiRun
        {
            PublicId = WorkspaceTokens.NewPublicId(), ProjectId = issue.ProjectId, RepositoryId = repo.Id, IssueId = issueId, RequestedById = actorId,
            Trigger = trigger is "assignment" or "api" ? trigger : "button", BaseBranch = repo.DefaultBranch,
        };
        run.Events.Add(new AiRunEvent { Kind = "status", Message = $"Queued ({run.Trigger})" });
        db.AiRuns.Add(run);
        await db.SaveChangesAsync();
        await audit.RecordAsync("ai_run.requested", "ai_run", run.PublicId, run.ProjectId,
            new { issue = $"{issue.Project.Key}-{issue.Number}", repository = repo.Slug, trigger = run.Trigger }, actorId);
        queue.Signal();
        return (run, true);
    }

    // ---------- read & cancel ----------

    public async Task<AiRunView?> GetAsync(string publicId, int? userId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var run = await Query(db).FirstOrDefaultAsync(r => r.PublicId == publicId);
        if (run == null || !await access.CanAsync(run.ProjectId, Permission.ViewProject, userId)) return null;
        return await ToViewAsync(db, run);
    }

    public async Task<List<AiRunView>> ListForIssueAsync(int issueId, int? userId = null)
    {
        await access.RequireForIssueAsync(issueId, Permission.ViewProject, userId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var runs = await Query(db).Where(r => r.IssueId == issueId).OrderByDescending(r => r.Id).Take(20).ToListAsync();
        var list = new List<AiRunView>();
        foreach (var r in runs) list.Add(await ToViewAsync(db, r));
        return list;
    }

    /// <summary>The runs of the projects the user can see, newest first (an optional project filter).</summary>
    public async Task<List<AiRunView>> ListAsync(int? userId, string? projectKey = null, int take = 50)
    {
        var visible = await access.VisibleProjectIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = Query(db).Where(r => visible.Contains(r.ProjectId));
        if (projectKey != null) q = q.Where(r => r.Issue.Project.Key == projectKey.ToUpper());
        var runs = await q.OrderByDescending(r => r.Id).Take(Math.Clamp(take, 1, 200)).ToListAsync();
        var list = new List<AiRunView>();
        foreach (var r in runs) list.Add(await ToViewAsync(db, r));
        return list;
    }

    public async Task<List<AiRunEvent>> GetEventsAsync(string publicId, int? userId, int afterId = 0)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var run = await db.AiRuns.AsNoTracking().Where(r => r.PublicId == publicId).Select(r => new { r.Id, r.ProjectId }).FirstOrDefaultAsync();
        if (run == null || !await access.CanAsync(run.ProjectId, Permission.ViewProject, userId)) throw new InvalidOperationException("Run not found.");
        return await db.AiRunEvents.AsNoTracking().Where(e => e.RunId == run.Id && e.Id > afterId).OrderBy(e => e.Id).Take(500).ToListAsync();
    }

    /// <summary>Cancels a queued or running run: the person who asked for it, or anyone who can edit issues in the project.</summary>
    public async Task<AiRun> CancelAsync(string publicId, int actorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var run = await db.AiRuns.FirstOrDefaultAsync(r => r.PublicId == publicId);
        if (run == null || !await access.CanAsync(run.ProjectId, Permission.ViewProject, actorId)) throw new InvalidOperationException("Run not found.");
        if (run.RequestedById != actorId) await access.RequireAsync(run.ProjectId, Permission.EditIssues, actorId);
        if (!run.IsActive) throw new InvalidOperationException("This run has already finished.");
        run.CancelRequested = true;
        var wasQueued = run.Status == AiRunStatus.Queued;
        if (wasQueued)
        {
            run.Status = AiRunStatus.Cancelled;
            run.StopReason = "cancelled";
            run.FinishedAt = DateTime.UtcNow;
            run.Events.Add(new AiRunEvent { Kind = "status", Message = "Cancelled before it started" });
        }
        await db.SaveChangesAsync();
        queue.Cancel(run.Id); // a run in flight notices at once
        await audit.RecordAsync("ai_run.cancel_requested", "ai_run", run.PublicId, run.ProjectId, new { wasQueued }, actorId);
        return run;
    }

    private static IQueryable<AiRun> Query(AppDbContext db) => db.AiRuns.AsNoTracking().Include(r => r.Issue).ThenInclude(i => i.Project).Include(r => r.Repository).Include(r => r.RequestedBy);

    private static async Task<AiRunView> ToViewAsync(AppDbContext db, AiRun r)
    {
        int? number = r.PullRequestId is { } id ? await db.PullRequests.Where(p => p.Id == id).Select(p => (int?)p.Number).FirstOrDefaultAsync() : null;
        return new AiRunView(r, r.Issue.Project.Key, r.Issue.Number, r.Issue.Title, r.Repository.Slug, r.RequestedBy.UserName ?? "", number);
    }
}
