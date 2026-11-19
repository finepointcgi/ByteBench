using GitBench.Services;
using GitBench.Services.Query;
using GitBench.Services.Reports;

namespace GitBench.Api;

public record DoraSettingsDto(string? ProductionEnvironment, int FailureWindowHours, IReadOnlyList<string> Environments);
public record DoraSettingsRequest(string? ProductionEnvironment = null, int? FailureWindowHours = null);

/// <summary>
/// Delivery reports: <c>GET /api/v1/projects/{key}/reports/{report}</c> for cycle-time, time-in-status, cumulative-flow,
/// throughput, wip, dora and pull-requests. Filters (query): type, label, component, assignee ("none" = unassigned),
/// from, to (yyyy-MM-dd, UTC, inclusive; default last 90 days), interval (week or month). <c>format=csv</c> returns CSV;
/// <c>view=</c> picks an alternative table (<c>issues</c> for time-in-status, <c>sprints</c> for throughput, <c>summary</c> for dora).
/// Timestamps are UTC.
/// </summary>
public static class ReportEndpoints
{
    private static readonly Dictionary<string, string> Descriptions = new()
    {
        ["cycle-time"] = "Lead time (created to done) and cycle time (first in progress to done) of issues finished in the range: p50/p85/p95, histograms, weekly or monthly trend and the issues (drill-down).",
        ["time-in-status"] = "Average and median time in each status, bottleneck flags, and the issues that spent the longest in the workflow. Rebuilt from status-change activity.",
        ["cumulative-flow"] = "Issues per status at the end of each day (cumulative flow diagram data), from status history.",
        ["throughput"] = "Issues and story points finished per week or month, issues created, and completed sprints' committed vs completed.",
        ["wip"] = "Open issues per status now, and how many are in progress or in review.",
        ["dora"] = "DORA metrics for the project's production environment: deployment frequency, lead time for changes, change failure rate, time to restore, each with its Elite/High/Medium/Low band.",
        ["pull-requests"] = "Time to first review, time to merge and size of pull requests created in the range.",
    };

    public static void MapReportEndpoints(this RouteGroupBuilder api)
    {
        foreach (var name in ReportService.Names)
        {
            var report = name;
            api.MapGet($"/projects/{{key}}/reports/{report}", (HttpContext http, IssueService issues, ReportService reports, string key) =>
                Run(http, issues, reports, key, report))
                .WithName("Report_" + report.Replace('-', '_')).WithSummary("Delivery report: " + report.Replace('-', ' ')).WithDescription(Descriptions[report])
                .WithTags("Reports");
        }
        api.MapGet("/projects/{key}/reports/dora/settings", GetDoraSettings).WithName("GetDoraSettings").WithSummary("Which environment counts as production")
            .WithTags("Reports");
        api.MapPut("/projects/{key}/reports/dora/settings", SetDoraSettings).WithName("SetDoraSettings").WithSummary("Set the production environment and failure window")
            .WithDescription("Needs ManageProjectSettings. productionEnvironment: the environment name (empty clears it); failureWindowHours: 1-720.")
            .WithTags("Reports").WithJsonBody<DoraSettingsRequest>();
    }

    private static async Task<IResult> Run(HttpContext http, IssueService issues, ReportService reports, string key, string report)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        var q = http.Request.Query;
        ReportFilter filter;
        try { filter = await reports.ParseFilterAsync(project.Id, n => q[n].FirstOrDefault()); }
        catch (InvalidOperationException ex) when (ex is not ForbiddenException) { throw ApiException.BadRequest(ex.Message); }
        var csv = string.Equals(q["format"].FirstOrDefault(), "csv", StringComparison.OrdinalIgnoreCase);
        var view = q["view"].FirstOrDefault();
        var table = report;
        if (view != null)
        {
            table = (report, view) switch
            {
                ("time-in-status", "issues") => "time-in-status-issues",
                ("throughput", "sprints") => "throughput-sprints",
                ("dora", "summary") => "dora-summary",
                _ => throw ApiException.BadRequest($"Unknown view '{view}' for {report}."),
            };
        }
        var data = await reports.RunAsync(table, project.Id, filter);
        if (!csv) return Results.Json(data, statusCode: StatusCodes.Status200OK);
        return Results.File(Csv.Utf8WithBom(ReportService.ToCsv(table, data)), "text/csv; charset=utf-8",
            $"{project.Key.ToLowerInvariant()}-{table}-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
    }

    private static async Task<DoraSettingsDto> GetDoraSettings(IssueService issues, DoraReportService dora, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        return new DoraSettingsDto(project.ProductionEnvironment, project.DoraFailureWindowHours, await dora.EnvironmentNamesAsync(project.Id));
    }

    private static async Task<DoraSettingsDto> SetDoraSettings(HttpContext http, IssueService issues, DoraReportService dora, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        var body = await ApiResults.ReadBodyAsync<DoraSettingsRequest>(http.Request);
        await dora.SetSettingsAsync(project.Id, body.ProductionEnvironment, body.FailureWindowHours);
        return await GetDoraSettings(issues, dora, key);
    }
}
