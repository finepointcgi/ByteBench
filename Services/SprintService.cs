using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services;

/// <summary>A sprint (or the backlog, when <see cref="Sprint"/> is null) with its issues in rank order.</summary>
public record BacklogSection(Sprint? Sprint, List<Issue> Issues)
{
    public int Points => Issues.Sum(i => i.StoryPoints ?? 0);
    public int DoneCount => Issues.Count(i => i.Status == IssueStatus.Done);
    public int DonePoints => Issues.Where(i => i.Status == IssueStatus.Done).Sum(i => i.StoryPoints ?? 0);
    public string DomId => Sprint is { } s ? $"sprint-{s.Id}" : "backlog";
}

/// <summary>Where "Complete sprint" sends the sprint's open issues.</summary>
public enum CarryOver { Backlog, ExistingSprint, NewSprint }

/// <summary>
/// Sprints: planning (backlog ranking, moving issues between sprints), starting, completing, and reports.
/// Managing sprints requires <see cref="Permission.ManageLabelsAndMilestones"/>; moving issues requires
/// <see cref="Permission.EditIssues"/>. Every membership change writes an issue <see cref="Activity"/>
/// (<see cref="SprintHistory.SprintText"/>) so burndowns can be rebuilt later.
/// </summary>
public class SprintService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, LiveEvents live, Webhooks.WebhookNotifier webhooks)
{
    // =====================================================================
    // Reads
    // =====================================================================

    /// <summary>The project's sprints: active first, then planned (by start date), then completed (newest first).</summary>
    public async Task<List<Sprint>> GetSprintsAsync(int projectId, bool includeCompleted = true)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var list = await db.Sprints.AsNoTracking().Where(s => s.ProjectId == projectId && (includeCompleted || s.State != SprintState.Completed)).ToListAsync();
        return Order(list);
    }

    public static List<Sprint> Order(IEnumerable<Sprint> sprints) => sprints
        .OrderBy(s => s.State == SprintState.Active ? 0 : s.State == SprintState.Planned ? 1 : 2)
        .ThenBy(s => s.State == SprintState.Completed ? -(s.CompletedAt ?? s.CreatedAt).Ticks : (s.StartDate ?? DateTime.MaxValue).Ticks)
        .ThenBy(s => s.Id).ToList();

    public async Task<Sprint?> GetSprintAsync(int projectId, int sprintId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Sprints.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sprintId && s.ProjectId == projectId);
    }

    public async Task<Sprint?> GetActiveAsync(int projectId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Sprints.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == projectId && s.State == SprintState.Active);
    }

    /// <summary>Planning view: the active sprint, the planned sprints, then the backlog (open issues in no sprint).</summary>
    public async Task<List<BacklogSection>> GetBacklogAsync(int projectId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var sprints = Order(await db.Sprints.AsNoTracking().Where(s => s.ProjectId == projectId && s.State != SprintState.Completed).ToListAsync());
        var ids = sprints.Select(s => s.Id).ToList();
        var issues = await db.Issues.AsNoTracking()
            .Include(i => i.Project).Include(i => i.Assignee).Include(i => i.Labels).AsSplitQuery()
            .Where(i => i.ProjectId == projectId &&
                        ((i.SprintId != null && ids.Contains(i.SprintId.Value)) || (i.SprintId == null && i.Status != IssueStatus.Done)))
            .OrderBy(i => i.SortOrder).ThenBy(i => i.Number).ToListAsync();
        var sections = sprints.Select(s => new BacklogSection(s, issues.Where(i => i.SprintId == s.Id).ToList())).ToList();
        sections.Add(new BacklogSection(null, issues.Where(i => i.SprintId == null).ToList()));
        return sections;
    }

    // =====================================================================
    // Managing sprints (Maintainer+)
    // =====================================================================

    public async Task<Sprint> CreateSprintAsync(int projectId, string? name, string? goal = null, DateTime? start = null, DateTime? end = null)
    {
        await access.RequireAsync(projectId, Permission.ManageLabelsAndMilestones);
        await using var db = await dbFactory.CreateDbContextAsync();
        if (string.IsNullOrWhiteSpace(name)) name = await SuggestNameAsync(db, projectId);
        var sprint = new Sprint { ProjectId = projectId, Name = Clean(name, 100), Goal = CleanOrNull(goal, 500), StartDate = start?.Date, EndDate = end?.Date };
        ValidateDates(sprint.StartDate, sprint.EndDate);
        db.Sprints.Add(sprint);
        await db.SaveChangesAsync();
        live.Publish(new SprintChanged(projectId));
        return sprint;
    }

    /// <summary>"WEB Sprint 4" for the fourth sprint of WEB.</summary>
    public async Task<string> SuggestNameAsync(int projectId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await SuggestNameAsync(db, projectId);
    }

    private static async Task<string> SuggestNameAsync(AppDbContext db, int projectId)
    {
        var key = await db.Projects.Where(p => p.Id == projectId).Select(p => p.Key).FirstAsync();
        var names = await db.Sprints.Where(s => s.ProjectId == projectId).Select(s => s.Name).ToListAsync();
        var n = names.Count + 1;
        while (names.Contains($"{key} Sprint {n}")) n++;
        return $"{key} Sprint {n}";
    }

    public async Task UpdateSprintAsync(int sprintId, string name, string? goal, DateTime? start, DateTime? end)
    {
        var (db, sprint) = await LoadForManageAsync(sprintId);
        await using (db)
        {
            ValidateDates(start, end);
            sprint.Name = Clean(name, 100);
            sprint.Goal = CleanOrNull(goal, 500);
            sprint.StartDate = start?.Date;
            sprint.EndDate = end?.Date;
            await db.SaveChangesAsync();
            Publish(sprint.ProjectId);
        }
    }

    /// <summary>Starts a planned sprint (only one may be active) and snapshots its committed scope.</summary>
    public async Task StartSprintAsync(int sprintId, DateTime start, DateTime end, string? goal)
    {
        var (db, sprint) = await LoadForManageAsync(sprintId);
        await using (db)
        {
            if (sprint.State != SprintState.Planned) throw new InvalidOperationException("Only a planned sprint can be started.");
            if (await db.Sprints.AnyAsync(s => s.ProjectId == sprint.ProjectId && s.State == SprintState.Active))
                throw new InvalidOperationException("Another sprint is already active. Complete it first.");
            ValidateDates(start, end);
            var scope = await db.Issues.Where(i => i.SprintId == sprintId).Select(i => i.StoryPoints).ToListAsync();
            sprint.State = SprintState.Active;
            sprint.StartDate = start.Date;
            sprint.EndDate = end.Date;
            sprint.Goal = CleanOrNull(goal, 500);
            sprint.StartedAt = DateTime.UtcNow;
            sprint.CommittedCount = scope.Count;
            sprint.CommittedPoints = scope.Sum(p => p ?? 0);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { throw new InvalidOperationException("Another sprint is already active. Complete it first."); }
            Publish(sprint.ProjectId);
        }
    }

    /// <summary>
    /// Completes the active sprint: snapshots what was done, then moves its open issues to the backlog, to
    /// <paramref name="targetSprintId"/>, or to a new sprint called <paramref name="newSprintName"/>.
    /// </summary>
    public async Task<Sprint?> CompleteSprintAsync(int sprintId, CarryOver carryOver, int? targetSprintId = null, string? newSprintName = null, int? actorId = null)
    {
        var (db, sprint) = await LoadForManageAsync(sprintId);
        await using (db)
        {
            if (sprint.State != SprintState.Active) throw new InvalidOperationException("Only the active sprint can be completed.");
            var uid = await access.ResolveUserIdAsync(actorId) ?? throw new ForbiddenException("Sign in to complete a sprint.");
            var key = await db.Projects.Where(p => p.Id == sprint.ProjectId).Select(p => p.Key).FirstAsync();

            Sprint? target = null;
            if (carryOver == CarryOver.ExistingSprint)
            {
                target = await db.Sprints.FirstOrDefaultAsync(s => s.Id == targetSprintId && s.ProjectId == sprint.ProjectId && s.State == SprintState.Planned)
                         ?? throw new InvalidOperationException("Pick a planned sprint to move the open issues to.");
            }
            else if (carryOver == CarryOver.NewSprint)
            {
                target = new Sprint { ProjectId = sprint.ProjectId, Name = string.IsNullOrWhiteSpace(newSprintName) ? await SuggestNameAsync(db, sprint.ProjectId) : Clean(newSprintName, 100) };
                db.Sprints.Add(target);
            }

            var issues = await db.Issues.Where(i => i.SprintId == sprintId).ToListAsync();
            var done = issues.Where(i => i.Status == IssueStatus.Done).ToList();
            var open = issues.Where(i => i.Status != IssueStatus.Done).ToList();
            var now = DateTime.UtcNow;
            sprint.State = SprintState.Completed;
            sprint.CompletedAt = now;
            sprint.CompletedCount = done.Count;
            sprint.CompletedPoints = done.Sum(i => i.StoryPoints ?? 0);
            await db.SaveChangesAsync(); // assigns the new sprint's id, used in activity links

            var texts = new Dictionary<int, string>();
            foreach (var i in open)
            {
                var text = SprintHistory.SprintText(key, sprint, target)!;
                i.SprintId = target?.Id;
                i.UpdatedAt = now;
                db.Activities.Add(new Activity { IssueId = i.Id, ActorId = uid, Text = text, CreatedAt = now });
                texts[i.Id] = text;
            }
            await db.SaveChangesAsync();
            await AfterMovesAsync(sprint.ProjectId, texts, uid);
            return target;
        }
    }

    /// <summary>Deletes a planned or active sprint; its issues go back to the backlog. Completed sprints are kept for reports.</summary>
    public async Task DeleteSprintAsync(int sprintId, int? actorId = null)
    {
        var (db, sprint) = await LoadForManageAsync(sprintId);
        await using (db)
        {
            if (sprint.State == SprintState.Completed) throw new InvalidOperationException("Completed sprints are kept for reports and can't be deleted.");
            var uid = await access.ResolveUserIdAsync(actorId) ?? throw new ForbiddenException("Sign in to delete a sprint.");
            var key = await db.Projects.Where(p => p.Id == sprint.ProjectId).Select(p => p.Key).FirstAsync();
            var texts = new Dictionary<int, string>();
            foreach (var i in await db.Issues.Where(i => i.SprintId == sprintId).ToListAsync())
            {
                var text = SprintHistory.SprintText(key, sprint, null)!;
                i.SprintId = null;
                i.UpdatedAt = DateTime.UtcNow;
                db.Activities.Add(new Activity { IssueId = i.Id, ActorId = uid, Text = text });
                texts[i.Id] = text;
            }
            db.Sprints.Remove(sprint);
            await db.SaveChangesAsync();
            await AfterMovesAsync(sprint.ProjectId, texts, uid);
        }
    }

    // =====================================================================
    // Planning: moving and ranking issues (Developer+)
    // =====================================================================

    /// <summary>
    /// Puts an issue in <paramref name="sprintId"/> (null = backlog). With <paramref name="reorder"/>, also ranks it just
    /// before <paramref name="beforeIssueId"/> in that section (null = at the end); otherwise its rank is kept.
    /// </summary>
    public async Task MoveIssueAsync(int issueId, int? sprintId, int? beforeIssueId, bool reorder = true, int? actorId = null)
    {
        await access.RequireForIssueAsync(issueId, Permission.EditIssues, actorId);
        var uid = await access.ResolveUserIdAsync(actorId) ?? throw new ForbiddenException("Sign in to plan sprints.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var issue = await db.Issues.Include(i => i.Project).Include(i => i.Sprint).FirstAsync(i => i.Id == issueId);
        Sprint? target = null;
        if (sprintId is { } sid)
        {
            target = await db.Sprints.FirstOrDefaultAsync(s => s.Id == sid && s.ProjectId == issue.ProjectId)
                     ?? throw new InvalidOperationException("Sprint not found in this project.");
            if (target.State == SprintState.Completed && issue.SprintId != sid)
                throw new InvalidOperationException("Issues can't be added to a completed sprint.");
        }

        if (reorder)
        {
            var section = await db.Issues
                .Where(i => i.ProjectId == issue.ProjectId && i.Id != issueId && i.SprintId == sprintId && (sprintId != null || i.Status != IssueStatus.Done))
                .OrderBy(i => i.SortOrder).ThenBy(i => i.Number).Select(i => new { i.Id, i.SortOrder }).ToListAsync();
            var idx = beforeIssueId is { } b ? section.FindIndex(c => c.Id == b) : -1;
            if (idx < 0) { if (section.Count > 0) issue.SortOrder = section[^1].SortOrder + 1; }
            else if (idx == 0) issue.SortOrder = section[0].SortOrder - 1;
            else issue.SortOrder = (section[idx - 1].SortOrder + section[idx].SortOrder) / 2;
        }

        var texts = new Dictionary<int, string>();
        if (issue.SprintId != sprintId)
        {
            var text = SprintHistory.SprintText(issue.Project.Key, issue.Sprint, target)!;
            issue.SprintId = sprintId;
            issue.UpdatedAt = DateTime.UtcNow;
            db.Activities.Add(new Activity { IssueId = issueId, ActorId = uid, Text = text });
            texts[issueId] = text;
        }
        await db.SaveChangesAsync();
        if (texts.Count > 0) await AfterMovesAsync(issue.ProjectId, texts, uid);
        else live.Publish(new SprintChanged(issue.ProjectId), new BoardChanged(issue.ProjectId));
    }

    // =====================================================================
    // Reports
    // =====================================================================

    public async Task<Burndown?> GetBurndownAsync(int projectId, int sprintId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        return await BuildBurndownAsync(db, projectId, sprintId, DateTime.UtcNow);
    }

    /// <summary>Loads every issue that is or was in the sprint, with its activities, and replays them (no permission check).</summary>
    public static async Task<Burndown?> BuildBurndownAsync(AppDbContext db, int projectId, int sprintId, DateTime now)
    {
        var sprint = await db.Sprints.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sprintId && s.ProjectId == projectId);
        if (sprint == null) return null;
        var marker = SprintHistory.Marker(sprintId);
        var issues = await db.Issues.AsNoTracking()
            .Where(i => i.ProjectId == projectId && (i.SprintId == sprintId || db.Activities.Any(a => a.IssueId == i.Id && a.Text.Contains(marker))))
            .ToListAsync();
        var ids = issues.Select(i => i.Id).ToList();
        var activities = (await db.Activities.AsNoTracking().Where(a => ids.Contains(a.IssueId)).ToListAsync()).ToLookup(a => a.IssueId);
        // Status activity names workflow statuses; any status in the Done category (plus the built-in name) counts as done.
        var doneNames = new HashSet<string>(await db.WorkflowStatuses.Where(w => w.ProjectId == projectId && w.Category == IssueStatus.Done)
            .Select(w => w.Name).ToListAsync(), StringComparer.OrdinalIgnoreCase) { IssueStatus.Done.Name() };
        return SprintHistory.Build(sprint, issues, activities, now, doneNames);
    }

    /// <summary>Committed vs completed for the last <paramref name="take"/> completed sprints, oldest first.</summary>
    public async Task<List<VelocityEntry>> GetVelocityAsync(int projectId, int take = 8)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var sprints = await db.Sprints.AsNoTracking()
            .Where(s => s.ProjectId == projectId && s.State == SprintState.Completed)
            .OrderByDescending(s => s.CompletedAt).Take(take).ToListAsync();
        sprints.Reverse();
        return sprints.Select(s => new VelocityEntry(s, s.CommittedPoints ?? 0, s.CompletedPoints ?? 0, s.CommittedCount ?? 0, s.CompletedCount ?? 0)).ToList();
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private async Task<(AppDbContext, Sprint)> LoadForManageAsync(int sprintId)
    {
        await access.RequireAsync(await access.ProjectOf(d => d.Sprints.Where(s => s.Id == sprintId).Select(s => (int?)s.ProjectId)),
            Permission.ManageLabelsAndMilestones);
        var db = await dbFactory.CreateDbContextAsync();
        var sprint = await db.Sprints.FirstAsync(s => s.Id == sprintId);
        return (db, sprint);
    }

    private async Task AfterMovesAsync(int projectId, Dictionary<int, string> texts, int actorId)
    {
        live.Publish([new SprintChanged(projectId), new BoardChanged(projectId), new ActivityChanged(),
            .. texts.Keys.Select(id => (LiveEvent)new IssueChanged(projectId, id))]);
        foreach (var (id, text) in texts) await webhooks.IssueUpdatedAsync(id, actorId, text);
    }

    private void Publish(int projectId) => live.Publish(new SprintChanged(projectId), new BoardChanged(projectId));

    private static void ValidateDates(DateTime? start, DateTime? end)
    {
        if (start is { } s && end is { } e && e.Date < s.Date) throw new InvalidOperationException("The end date must be on or after the start date.");
    }

    private static string Clean(string? s, int max)
    {
        var t = (s ?? "").Trim();
        if (t.Length == 0) throw new InvalidOperationException("A sprint needs a name.");
        return t.Length > max ? t[..max] : t;
    }

    private static string? CleanOrNull(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : Clean(s, max);
}
