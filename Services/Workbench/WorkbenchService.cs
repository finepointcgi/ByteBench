using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Services.Audit;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Workbench;

/// <summary>Site settings for the link to Workbench, the company's ERP, where time is recorded (config section <c>Workbench</c>).</summary>
public class WorkbenchOptions
{
    /// <summary>Workbench's address, e.g. <c>https://workbench.example.com</c>. Unset (the default) turns the link off.</summary>
    public string? BaseUrl { get; set; }
    /// <summary>
    /// Allow the Workbench address to be loopback, private, link-local or a metadata address (the SSRF guard). Null (the default)
    /// follows <c>Webhooks:AllowPrivateNetworks</c>, which appsettings.Development.json turns on.
    /// </summary>
    public bool? AllowPrivateNetworks { get; set; }
    /// <summary>Seconds one call to the Workbench API may take before it is given up on.</summary>
    public int TimeoutSeconds { get; set; } = 5;
    /// <summary>How long Workbench data (project and WBS lists, task context) is cached.</summary>
    public int CacheSeconds { get; set; } = 60;
    /// <summary>Runs the background worker that pushes work state to Workbench for projects that opted in.</summary>
    public bool PushWorkerEnabled { get; set; } = true;
    /// <summary>Seconds between the push worker's sweeps.</summary>
    public int PushIntervalSeconds { get; set; } = 30;
    /// <summary>Minutes between plan-to-issues reconciles of the projects that enabled it (0 turns the periodic reconcile off).</summary>
    public int ReconcileMinutes { get; set; } = 15;
    /// <summary>The most plan tasks one sync will handle; a bigger plan is refused (choose a WBS subtree) rather than cut off.</summary>
    public int MaxTasksPerSync { get; set; } = 2000;
}

/// <summary>Where to log time for an issue: its effective charge code and the Workbench timesheet to open.</summary>
public sealed record WorkbenchLink(string Wbs, string? ProjectCode, bool IsIssueOverride, string TimesheetUrl);

/// <summary>
/// The Workbench link. ByteBench doesn't track time: hours are recorded in Workbench timesheets, which are the cost record.
/// A project maps to a Workbench charge code (WBS element) and optionally a Workbench project code (Maintainer+); an issue
/// can override the WBS code. Issue pages then offer "Log time in Workbench", which opens the user's timesheet
/// (<c>{BaseUrl}/time</c>, the current week) in a new tab. Workbench's timesheet route takes no charge code, so the code
/// is shown next to the button for the user to pick in the grid.
/// </summary>
public partial class WorkbenchService(WorkbenchCredentials credentials, WorkbenchDirectory directory, IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access,
    IssueService issues, AuditLog audit)
{
    public const int MaxCodeLength = 50;

    /// <summary>Workbench's timesheet page (<c>Workbench.Ui/Time/TimePage.razor</c>); it also takes <c>?date=yyyy-MM-dd</c>.</summary>
    public const string TimesheetPath = "/time";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-/]*$")]
    private static partial Regex CodePattern();

    /// <summary>
    /// The Workbench address without a trailing slash: the one saved on the admin page, else <c>Workbench:BaseUrl</c>; null when
    /// neither is set (or not an absolute http(s) URL).
    /// </summary>
    public string? BaseUrl => credentials.BaseUrl;

    public bool IsConfigured => BaseUrl != null;

    /// <summary>True when an API key is stored too, so pickers, context and push can talk to the Workbench API.</summary>
    public bool IsConnected => BaseUrl != null && credentials.ApiKey != null;

    public static string? Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var trimmed = url.Trim().TrimEnd('/');
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? trimmed : null;
    }

    /// <summary>The user's Workbench timesheet: the period containing <paramref name="date"/>, or the current one when null.</summary>
    public string? TimesheetUrl(DateOnly? date = null) =>
        BaseUrl == null ? null : BaseUrl + TimesheetPath + (date is { } d ? $"?date={d:yyyy-MM-dd}" : "");

    /// <summary>The issue's override, else its project's WBS code.</summary>
    public static string? EffectiveWbs(string? issueWbs, string? projectWbs) => issueWbs ?? projectWbs;

    /// <summary>The link for an issue (with its Project loaded), or null when Workbench isn't configured or there's no WBS code.</summary>
    public WorkbenchLink? LinkFor(Issue issue, Project project)
    {
        // Workbench's timesheet route takes only ?date=: for finished work, open the period of the day it was closed.
        if (TimesheetUrl(issue.ClosedAt is { } closed ? DateOnly.FromDateTime(closed) : null) is not { } url || EffectiveWbs(issue.WorkbenchWbs, project.WorkbenchWbs) is not { } wbs) return null;
        return new WorkbenchLink(wbs, project.WorkbenchProjectCode, issue.WorkbenchWbs != null, url);
    }

    /// <summary>Trims a code; blank means none. Throws for anything too long or with unexpected characters.</summary>
    public static string? CleanCode(string? code, string what)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = code.Trim();
        if (c.Length > MaxCodeLength) throw new InvalidOperationException($"The {what} can be at most {MaxCodeLength} characters.");
        if (!CodePattern().IsMatch(c)) throw new InvalidOperationException($"The {what} can only contain letters, digits, '.', '-', '_' and '/'.");
        return c;
    }

    /// <summary>Sets a project's Workbench WBS and project codes (Maintainer+). Recorded in the audit log when anything changes.</summary>
    public async Task SetProjectMappingAsync(int projectId, string? wbs, string? projectCode, int? actorId = null)
    {
        await access.RequireRoleAsync(projectId, ProjectRole.Maintainer, "change the Workbench charge code", actorId);
        var (newWbs, newCode) = (CleanCode(wbs, "WBS code"), CleanCode(projectCode, "Workbench project code"));
        await ValidateCodesAsync(newCode, newWbs);
        await using var db = await dbFactory.CreateDbContextAsync();
        var p = await db.Projects.FirstOrDefaultAsync(x => x.Id == projectId) ?? throw new InvalidOperationException("Project not found.");
        var before = new { wbs = p.WorkbenchWbs, projectCode = p.WorkbenchProjectCode };
        if (before.wbs == newWbs && before.projectCode == newCode) return;
        (p.WorkbenchWbs, p.WorkbenchProjectCode) = (newWbs, newCode);
        await db.SaveChangesAsync();
        await audit.RecordAsync("project.workbench_changed", "project", projectId, projectId,
            new { before, after = new { wbs = newWbs, projectCode = newCode } }, actorId);
    }

    /// <summary>Sets or clears an issue's WBS override (needs EditIssues). Recorded in the issue's activity and the audit log.</summary>
    public async Task SetIssueWbsAsync(int issueId, string? wbs, int? actorId = null)
    {
        var actor = await access.ResolveUserIdAsync(actorId) ?? throw new ForbiddenException("Sign in to edit issues.");
        var code = CleanCode(wbs, "WBS code");
        if (code != null && await IsConnectedAsync())
        {
            string? projectCode;
            await using (var db = await dbFactory.CreateDbContextAsync())
                projectCode = await db.Issues.Where(i => i.Id == issueId).Select(i => i.Project.WorkbenchProjectCode).FirstOrDefaultAsync();
            await ValidateCodesAsync(projectCode, code);
        }
        string? old = null;
        var projectId = 0;
        var changed = false;
        await issues.UpdateIssueAsync(issueId, actor, (issue, _) =>
        {
            (old, projectId) = (issue.WorkbenchWbs, issue.ProjectId);
            if (old == code) return Task.FromResult<string?>(null);
            issue.WorkbenchWbs = code;
            changed = true;
            return Task.FromResult<string?>(code == null ? "removed the Workbench charge code override"
                : $"set the Workbench charge code to **{code}**");
        });
        if (changed)
            await audit.RecordAsync("issue.workbench_changed", "issue", issueId, projectId, new { before = old, after = code }, actor);
    }

    private Task<bool> IsConnectedAsync() => Task.FromResult(IsConnected);

    /// <summary>
    /// When connected, checks that the Workbench project code and WBS element exist, so a typo is caught here rather than when
    /// someone charges time. Not connected, or Workbench unreachable or refusing the key: the codes are accepted as free text.
    /// </summary>
    public async Task ValidateCodesAsync(string? projectCode, string? wbs)
    {
        if (!IsConnected || (projectCode == null && wbs == null)) return;
        if (projectCode == null) return; // a WBS code alone can't be checked: which project's?
        var project = await directory.FindProjectAsync(projectCode);
        if (project.Status == WbStatus.NotFound) throw new InvalidOperationException(project.Message);
        if (!project.Ok || wbs == null) return;
        var element = await directory.FindWbsAsync(project.Value!, wbs, fresh: true);
        if (element.Status == WbStatus.NotFound) throw new InvalidOperationException(element.Message);
    }
}
