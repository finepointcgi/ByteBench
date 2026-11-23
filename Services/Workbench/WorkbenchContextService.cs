using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Workbench;

/// <summary>
/// A mapped WBS task as Workbench has it, for display only. <see cref="State"/> is <see cref="WbStatus.Ok"/> when the task was found;
/// otherwise <see cref="Message"/> says why nothing is shown (not connected, Workbench slow or down, key refused, code not found).
/// <see cref="Hours"/> is null with <see cref="HoursHidden"/> when the key's roles can't read financials (a 403 hides it, it isn't an error).
/// </summary>
public sealed record WorkbenchTaskContext(WbStatus State, string? Message, string? ProjectCode, string? ProjectName, string? ProjectStatus, string WbsCode,
    string? WbsName, string? Title, string? Status, decimal? Percent, DateOnly? Start, DateOnly? Finish, WbHours? Hours, bool HoursHidden,
    string? ProjectUrl, string? PlanUrl)
{
    public bool Found => State == WbStatus.Ok;
    /// <summary>Charged / planned (0.5 = half), null without planned hours.</summary>
    public decimal? HoursUsed => Hours is { PlannedHours: > 0 } h ? h.ActualHours / h.PlannedHours : null;
}

/// <summary>
/// Read-only Workbench context for a mapped project or issue, with the caller's ViewProject check. Everything comes from the
/// cached <see cref="WorkbenchDirectory"/>; it never throws for Workbench problems (the result carries the reason), so pages
/// can load it after the first render and a slow or down Workbench never breaks them.
/// </summary>
public sealed class WorkbenchContextService(WorkbenchDirectory directory, WorkbenchCredentials credentials, IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access)
{
    /// <summary>The context of <paramref name="wbsCode"/> (default: the project's own WBS code) in the project's Workbench project.</summary>
    public async Task<WorkbenchTaskContext?> GetAsync(int projectId, string? wbsCode = null, CancellationToken ct = default)
    {
        await access.RequireAsync(projectId, Permission.ViewProject);
        if (credentials.BaseUrl == null || credentials.ApiKey == null) return null;
        string? projectCode;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var p = await db.Projects.AsNoTracking().Where(x => x.Id == projectId).Select(x => new { x.WorkbenchProjectCode, x.WorkbenchWbs }).FirstOrDefaultAsync(ct);
            if (p == null) return null;
            (projectCode, wbsCode) = (p.WorkbenchProjectCode, wbsCode ?? p.WorkbenchWbs);
        }
        if (wbsCode == null) return null;
        if (projectCode == null)
            return Empty(WbStatus.NotFound, "Pick the Workbench project in the project's Workbench settings to see this task here.", null, wbsCode);

        var project = await directory.FindProjectAsync(projectCode);
        if (!project.Ok) return Empty(project.Status, project.Message, projectCode, wbsCode);
        var p0 = project.Value!;
        var wbs = await directory.FindWbsAsync(p0, wbsCode);
        if (!wbs.Ok) return Empty(wbs.Status, wbs.Message, projectCode, wbsCode, p0);
        var w = wbs.Value!;

        string? title = null, status = null;
        decimal? percent = null;
        DateOnly? start = null, finish = null;
        if (p0.SheetId is { } sheet && w.ItemId is { } itemId)
        {
            var plan = await directory.GetPlanAsync(sheet);
            if (plan.Ok && plan.Value!.Items.TryGetValue(itemId, out var item))
                (title, status, percent, start, finish) = (item.Title, item.Status, item.Percent, item.Start, item.Finish);
        }

        WbHours? hours = null;
        var hidden = false;
        var hoursResult = await directory.GetHoursAsync(p0.Id);
        if (hoursResult.Ok && hoursResult.Value!.TryGetValue(w.Id, out var h)) hours = h;
        else if (!hoursResult.Ok) hidden = true; // 403 (roles can't read financials) or Workbench trouble: hide, don't fail

        var baseUrl = credentials.BaseUrl;
        return new WorkbenchTaskContext(WbStatus.Ok, null, p0.Code, p0.Name, p0.Status, w.Code, w.Name, title ?? w.Name, status, percent, start, finish,
            hours, hidden, $"{baseUrl}/projects/{p0.Id}", p0.SheetId is { } sid ? $"{baseUrl}/sheets/{sid}" : null);
    }

    private static WorkbenchTaskContext Empty(WbStatus state, string? message, string? projectCode, string wbs, WbProject? project = null) =>
        new(state, message, projectCode, project?.Name, project?.Status, wbs, null, null, null, null, null, null, null, false, null, null);
}
