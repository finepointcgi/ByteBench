using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Fields;

/// <summary>An issue's estimates and time spent, in minutes.</summary>
public sealed record TimeSummary(int? OriginalMinutes, int? RemainingMinutes, int SpentMinutes, int HoursPerDay)
{
    public bool IsEmpty => OriginalMinutes == null && RemainingMinutes == null && SpentMinutes == 0;
    /// <summary>Spent share of spent + remaining (or of the original estimate when there's no remaining), 0–100.</summary>
    public int Percent
    {
        get
        {
            var whole = SpentMinutes + (RemainingMinutes ?? Math.Max(0, (OriginalMinutes ?? 0) - SpentMinutes));
            return whole == 0 ? 0 : (int)Math.Round(SpentMinutes * 100.0 / whole);
        }
    }
    /// <summary>True when spent + remaining exceeds the original estimate.</summary>
    public bool OverEstimate => OriginalMinutes is { } o && SpentMinutes + (RemainingMinutes ?? 0) > o;
    public string Format(int? minutes) => minutes is { } m ? Duration.Format(m, HoursPerDay) : "–";
}

/// <summary>One cell of a timesheet: minutes a user logged on an issue on a day.</summary>
public sealed record TimesheetEntry(int LogId, int IssueId, string IssueKey, string IssueTitle, string ProjectKey, int UserId, string UserName,
    DateTime Date, int Minutes, string? Comment);

/// <summary>
/// Time tracking: original and remaining estimates on issues and work logs (who spent how long, when). Logging work and
/// changing estimates needs <see cref="Permission.EditIssues"/>; a work log can be edited by its author and deleted by
/// its author or a Maintainer+. Changes are recorded in the issue's activity. A project's hours per day (default 8,
/// Maintainer+) turns "1d" into hours.
/// </summary>
public class TimeTrackingService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, IssueService issues)
{
    public const int MaxCommentLength = 1000;

    // ---------- Settings ----------

    public async Task<int> GetHoursPerDayAsync(int projectId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Projects.Where(p => p.Id == projectId).Select(p => (int?)p.HoursPerDay).FirstOrDefaultAsync() ?? Duration.DefaultHoursPerDay;
    }

    public async Task SetHoursPerDayAsync(int projectId, int hours)
    {
        await access.RequireAsync(projectId, Permission.ManageLabelsAndMilestones);
        if (hours is < 1 or > 24) throw new InvalidOperationException("Hours per day must be between 1 and 24.");
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Projects.Where(p => p.Id == projectId).ExecuteUpdateAsync(s => s.SetProperty(p => p.HoursPerDay, hours));
    }

    // ---------- Estimates ----------

    public async Task<TimeSummary?> GetSummaryAsync(int issueId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var i = await db.Issues.Where(x => x.Id == issueId)
            .Select(x => new { x.ProjectId, x.OriginalEstimateMinutes, x.RemainingEstimateMinutes, x.Project.HoursPerDay }).FirstOrDefaultAsync();
        if (i == null || !await access.CanAsync(i.ProjectId, Permission.ViewProject)) return null;
        var spent = await db.WorkLogs.Where(w => w.IssueId == issueId).SumAsync(w => (int?)w.Minutes) ?? 0;
        return new TimeSummary(i.OriginalEstimateMinutes, i.RemainingEstimateMinutes, spent, i.HoursPerDay);
    }

    /// <summary>Time spent per issue (only issues with work logged appear).</summary>
    public async Task<Dictionary<int, int>> GetSpentAsync(IReadOnlyCollection<int> issueIds)
    {
        if (issueIds.Count == 0) return [];
        var visible = await access.VisibleProjectIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var ids = issueIds.Distinct().ToList();
        return await db.WorkLogs.Where(w => ids.Contains(w.IssueId) && visible.Contains(w.Issue.ProjectId))
            .GroupBy(w => w.IssueId).Select(g => new { g.Key, Minutes = g.Sum(w => w.Minutes) })
            .ToDictionaryAsync(x => x.Key, x => x.Minutes);
    }

    /// <summary>
    /// Sets the estimates. <paramref name="setOriginal"/>/<paramref name="setRemaining"/> say which to change (null values
    /// clear). Setting only the original estimate on an issue with no remaining estimate also sets the remaining one
    /// (original minus time spent).
    /// </summary>
    public async Task SetEstimatesAsync(int issueId, bool setOriginal, int? original, bool setRemaining, int? remaining, int? actorId = null)
    {
        if (original is < 0 or > Duration.MaxMinutes || remaining is < 0 or > Duration.MaxMinutes) throw new InvalidOperationException("Estimates must be between 0 and 10000 hours.");
        var actor = await access.ResolveUserIdAsync(actorId) ?? throw new ForbiddenException("Sign in to edit issues.");
        await issues.UpdateIssueAsync(issueId, actor, async (issue, db) =>
        {
            var hpd = await db.Projects.Where(p => p.Id == issue.ProjectId).Select(p => p.HoursPerDay).FirstAsync();
            var changes = new List<string>();
            if (setOriginal && issue.OriginalEstimateMinutes != original)
            {
                issue.OriginalEstimateMinutes = original;
                changes.Add(original == null ? "removed the original estimate" : $"set the original estimate to **{Duration.Format(original.Value, hpd)}**");
                if (!setRemaining && issue.RemainingEstimateMinutes == null && original != null)
                {
                    var spent = await db.WorkLogs.Where(w => w.IssueId == issueId).SumAsync(w => (int?)w.Minutes) ?? 0;
                    issue.RemainingEstimateMinutes = Math.Max(0, original.Value - spent);
                }
            }
            if (setRemaining && issue.RemainingEstimateMinutes != remaining)
            {
                issue.RemainingEstimateMinutes = remaining;
                changes.Add(remaining == null ? "removed the remaining estimate" : $"set the remaining estimate to **{Duration.Format(remaining.Value, hpd)}**");
            }
            return changes.Count == 0 ? null : string.Join(", ", changes);
        });
    }

    // ---------- Work logs ----------

    public async Task<List<WorkLog>> GetWorkLogsAsync(int issueId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var projectId = await db.Issues.Where(i => i.Id == issueId).Select(i => (int?)i.ProjectId).FirstOrDefaultAsync();
        if (projectId == null || !await access.CanAsync(projectId.Value, Permission.ViewProject)) return [];
        return await db.WorkLogs.AsNoTracking().Include(w => w.User).Where(w => w.IssueId == issueId)
            .OrderByDescending(w => w.Date).ThenByDescending(w => w.Id).ToListAsync();
    }

    public async Task<WorkLog?> GetWorkLogAsync(int logId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var log = await db.WorkLogs.AsNoTracking().Include(w => w.User).Include(w => w.Issue).FirstOrDefaultAsync(w => w.Id == logId);
        return log != null && await access.CanAsync(log.Issue.ProjectId, Permission.ViewProject) ? log : null;
    }

    private static (int Minutes, DateTime Date, string? Comment) Validate(int minutes, DateTime date, string? comment)
    {
        if (minutes is <= 0 or > Duration.MaxMinutes) throw new InvalidOperationException("Time spent must be more than 0 (and at most 10000 hours).");
        var day = date.Date;
        if (day > DateTime.UtcNow.Date.AddDays(1)) throw new InvalidOperationException("Work can't be logged in the future.");
        if (day < new DateTime(2000, 1, 1)) throw new InvalidOperationException("That date is too far in the past.");
        comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (comment?.Length > MaxCommentLength) throw new InvalidOperationException($"The comment can be at most {MaxCommentLength} characters.");
        return (minutes, DateTime.SpecifyKind(day, DateTimeKind.Unspecified), comment);
    }

    /// <summary>Logs work by the acting user. The remaining estimate (if any) goes down by the time spent, not below zero.</summary>
    public async Task<WorkLog> AddWorkLogAsync(int issueId, int minutes, DateTime date, string? comment, int? actorId = null)
    {
        var actor = await access.ResolveUserIdAsync(actorId) ?? throw new ForbiddenException("Sign in to log work.");
        var (m, d, c) = Validate(minutes, date, comment);
        var log = new WorkLog { IssueId = issueId, UserId = actor, Minutes = m, Date = d, Comment = c };
        await issues.UpdateIssueAsync(issueId, actor, async (issue, db) =>
        {
            var hpd = await db.Projects.Where(p => p.Id == issue.ProjectId).Select(p => p.HoursPerDay).FirstAsync();
            db.WorkLogs.Add(log);
            if (issue.RemainingEstimateMinutes is { } r) issue.RemainingEstimateMinutes = Math.Max(0, r - m);
            return $"logged **{Duration.Format(m, hpd)}** of work";
        });
        return log;
    }

    /// <summary>Only the author can edit a work log. The remaining estimate moves by the difference.</summary>
    public async Task UpdateWorkLogAsync(int logId, int minutes, DateTime date, string? comment, int? actorId = null)
    {
        var actor = await access.ResolveUserIdAsync(actorId) ?? throw new ForbiddenException("Sign in to edit work logs.");
        var (m, d, c) = Validate(minutes, date, comment);
        var (issueId, authorId) = await LogInfoAsync(logId);
        if (authorId != actor) throw new ForbiddenException("Only the person who logged this work can edit it.");
        await issues.UpdateIssueAsync(issueId, actor, async (issue, db) =>
        {
            var log = await db.WorkLogs.FirstAsync(w => w.Id == logId);
            var hpd = await db.Projects.Where(p => p.Id == issue.ProjectId).Select(p => p.HoursPerDay).FirstAsync();
            if (log.Minutes == m && log.Date == d && log.Comment == c) return null;
            if (issue.RemainingEstimateMinutes is { } r) issue.RemainingEstimateMinutes = Math.Max(0, r - (m - log.Minutes));
            var text = log.Minutes == m ? "edited a work log" : $"changed logged work from **{Duration.Format(log.Minutes, hpd)}** to **{Duration.Format(m, hpd)}**";
            (log.Minutes, log.Date, log.Comment, log.UpdatedAt) = (m, d, c, DateTime.UtcNow);
            return text;
        });
    }

    /// <summary>The author or a Maintainer+ can delete a work log. The remaining estimate (if any) goes back up.</summary>
    public async Task DeleteWorkLogAsync(int logId, int? actorId = null)
    {
        var actor = await access.ResolveUserIdAsync(actorId) ?? throw new ForbiddenException("Sign in to delete work logs.");
        var (issueId, authorId) = await LogInfoAsync(logId);
        var projectId = await access.ProjectOf(db => db.Issues.Where(i => i.Id == issueId).Select(i => (int?)i.ProjectId));
        if (authorId != actor) await access.RequireRoleAsync(projectId, ProjectRole.Maintainer, "delete other people's work logs", actor);
        await issues.UpdateIssueAsync(issueId, actor, async (issue, db) =>
        {
            var log = await db.WorkLogs.FirstAsync(w => w.Id == logId);
            var hpd = await db.Projects.Where(p => p.Id == issue.ProjectId).Select(p => p.HoursPerDay).FirstAsync();
            if (issue.RemainingEstimateMinutes is { } r) issue.RemainingEstimateMinutes = r + log.Minutes;
            db.WorkLogs.Remove(log);
            var who = log.UserId == actor ? "" : $" by **{await db.Users.Where(u => u.Id == log.UserId).Select(u => u.DisplayName).FirstAsync()}**";
            return $"deleted **{Duration.Format(log.Minutes, hpd)}** of logged work{who}";
        });
    }

    /// <summary>Whether <paramref name="userId"/> may edit / delete <paramref name="log"/> (for showing buttons).</summary>
    public static (bool Edit, bool Delete) Rights(WorkLog log, int userId, ProjectPermissions perms) =>
        (log.UserId == userId && perms.Can(Permission.EditIssues),
         (log.UserId == userId && perms.Can(Permission.EditIssues)) || perms.Role >= ProjectRole.Maintainer);

    private async Task<(int IssueId, int AuthorId)> LogInfoAsync(int logId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var info = await db.WorkLogs.Where(w => w.Id == logId).Select(w => new { w.IssueId, w.UserId, w.Issue.ProjectId }).FirstOrDefaultAsync();
        if (info == null || !await access.CanAsync(info.ProjectId, Permission.ViewProject)) throw new InvalidOperationException("Work log not found.");
        return (info.IssueId, info.UserId);
    }

    // ---------- Timesheets ----------

    /// <summary>Monday of the week containing <paramref name="day"/>.</summary>
    public static DateTime WeekStart(DateTime day)
    {
        var d = day.Date;
        return d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
    }

    /// <summary>A user's work logs from <paramref name="from"/> (inclusive) to <paramref name="to"/> (exclusive), in projects the acting user can view.</summary>
    public async Task<List<TimesheetEntry>> GetUserEntriesAsync(int userId, DateTime from, DateTime to)
    {
        var visible = await access.VisibleProjectIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await Entries(db.WorkLogs.Where(w => w.UserId == userId && visible.Contains(w.Issue.ProjectId)), from, to);
    }

    /// <summary>Everyone's work logs in a project between two dates (empty if the acting user can't view it).</summary>
    public async Task<List<TimesheetEntry>> GetProjectEntriesAsync(int projectId, DateTime from, DateTime to)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        return await Entries(db.WorkLogs.Where(w => w.Issue.ProjectId == projectId), from, to);
    }

    private static Task<List<TimesheetEntry>> Entries(IQueryable<WorkLog> q, DateTime from, DateTime to) =>
        q.Where(w => w.Date >= from.Date && w.Date < to.Date)
            .OrderBy(w => w.Date).ThenBy(w => w.Id)
            .Select(w => new TimesheetEntry(w.Id, w.IssueId, w.Issue.Project.Key + "-" + w.Issue.Number, w.Issue.Title, w.Issue.Project.Key,
                w.UserId, w.User.DisplayName, w.Date, w.Minutes, w.Comment))
            .ToListAsync();
}
