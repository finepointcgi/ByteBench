using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Fields;

/// <summary>An issue's original and remaining estimates, in minutes.</summary>
public sealed record EstimateSummary(int? OriginalMinutes, int? RemainingMinutes, int HoursPerDay)
{
    public bool IsEmpty => OriginalMinutes == null && RemainingMinutes == null;
    public string Format(int? minutes) => minutes is { } m ? Duration.Format(m, HoursPerDay) : "–";
}

/// <summary>
/// Planning estimates on issues: an original and a remaining estimate, both plain editable fields (time actually spent is
/// recorded in Workbench timesheets, not here; see <see cref="Workbench.WorkbenchService"/>). Changing estimates needs
/// <see cref="Permission.EditIssues"/> and is recorded in the issue's activity. A project's hours per day (default 8,
/// Maintainer+) turns "1d" into hours.
/// </summary>
public class EstimateService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, IssueService issues)
{
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

    public async Task<EstimateSummary?> GetSummaryAsync(int issueId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var i = await db.Issues.Where(x => x.Id == issueId)
            .Select(x => new { x.ProjectId, x.OriginalEstimateMinutes, x.RemainingEstimateMinutes, x.Project.HoursPerDay }).FirstOrDefaultAsync();
        if (i == null || !await access.CanAsync(i.ProjectId, Permission.ViewProject)) return null;
        return new EstimateSummary(i.OriginalEstimateMinutes, i.RemainingEstimateMinutes, i.HoursPerDay);
    }

    /// <summary>
    /// Sets the estimates. <paramref name="setOriginal"/>/<paramref name="setRemaining"/> say which to change (null values
    /// clear). Nothing changes either estimate automatically, except that setting only the original estimate on an issue
    /// with no remaining estimate starts the remaining one at the same value.
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
                if (!setRemaining && issue.RemainingEstimateMinutes == null && original != null) issue.RemainingEstimateMinutes = original;
            }
            if (setRemaining && issue.RemainingEstimateMinutes != remaining)
            {
                issue.RemainingEstimateMinutes = remaining;
                changes.Add(remaining == null ? "removed the remaining estimate" : $"set the remaining estimate to **{Duration.Format(remaining.Value, hpd)}**");
            }
            return changes.Count == 0 ? null : string.Join(", ", changes);
        });
    }
}
