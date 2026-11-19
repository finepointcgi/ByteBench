using System.Globalization;
using ByteBench.Data;
using ByteBench.Services.Query;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Reports;

/// <summary>
/// Runs a delivery report by name for the REST API and the CSV download, and turns query-string filters into a
/// <see cref="ReportFilter"/>. Permissions are enforced by the report services themselves (ViewProject).
/// </summary>
public class ReportService(DeliveryReportService delivery, DoraReportService dora, PullRequestReportService pullRequests,
    IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>Reports with their own JSON endpoint (<c>/api/v1/projects/{key}/reports/{name}</c>).</summary>
    public static readonly string[] Names = ["cycle-time", "time-in-status", "cumulative-flow", "throughput", "wip", "dora", "pull-requests"];

    /// <summary>CSV downloads: the reports above plus alternative tables of some of them.</summary>
    public static readonly string[] CsvNames = [.. Names, "time-in-status-issues", "throughput-sprints", "dora-summary"];

    public async Task<object> RunAsync(string report, int projectId, ReportFilter f) => report switch
    {
        "cycle-time" => await delivery.CycleTimeAsync(projectId, f),
        "time-in-status" or "time-in-status-issues" => await delivery.TimeInStatusAsync(projectId, f),
        "cumulative-flow" => await delivery.CumulativeFlowAsync(projectId, f),
        "throughput" or "throughput-sprints" => await delivery.ThroughputAsync(projectId, f),
        "wip" => await delivery.WipAsync(projectId, f),
        "dora" or "dora-summary" => await dora.ReportAsync(projectId, f),
        "pull-requests" => await pullRequests.ReportAsync(projectId, f),
        _ => throw new InvalidOperationException($"Unknown report '{report}'."),
    };

    public static string ToCsv(string report, object data) => (report, data) switch
    {
        ("cycle-time", CycleTimeReport r) => ReportCsv.CycleTime(r),
        ("time-in-status", TimeInStatusReport r) => ReportCsv.TimeInStatus(r),
        ("time-in-status-issues", TimeInStatusReport r) => ReportCsv.TimeInStatusIssues(r),
        ("cumulative-flow", CumulativeFlowReport r) => ReportCsv.CumulativeFlow(r),
        ("throughput", ThroughputReport r) => ReportCsv.Throughput(r),
        ("throughput-sprints", ThroughputReport r) => ReportCsv.ThroughputSprints(r),
        ("wip", WipReport r) => ReportCsv.Wip(r),
        ("dora", DoraReport r) => ReportCsv.Dora(r),
        ("dora-summary", DoraReport r) => ReportCsv.DoraSummary(r),
        ("pull-requests", PullRequestReport r) => ReportCsv.PullRequests(r),
        _ => throw new InvalidOperationException($"Unknown report '{report}'."),
    };

    public async Task<string> CsvAsync(string report, int projectId, ReportFilter f) => ToCsv(report, await RunAsync(report, projectId, f));

    /// <summary>
    /// Filter from query values: <c>type</c> (issue type), <c>label</c> and <c>component</c> (id or name), <c>assignee</c>
    /// (username, id or "none"), <c>from</c>/<c>to</c> (yyyy-MM-dd) and <c>interval</c> (week or month).
    /// Throws <see cref="InvalidOperationException"/> for a value that doesn't match anything.
    /// </summary>
    public async Task<ReportFilter> ParseFilterAsync(int projectId, Func<string, string?> get)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var f = new ReportFilter();
        if (get("type") is { Length: > 0 } t)
            f = f with { Type = Enum.TryParse<IssueType>(t, true, out var ty) ? ty : throw new InvalidOperationException($"Unknown type '{t}'.") };
        if (get("label") is { Length: > 0 } l)
        {
            var id = int.TryParse(l, out var n) ? n
                : await db.Labels.Where(x => x.ProjectId == projectId && x.Name == l).Select(x => (int?)x.Id).FirstOrDefaultAsync()
                  ?? await db.Labels.Where(x => x.Name == l && x.ProjectId == null && db.Projects.Any(p => p.Id == projectId && p.GroupId == x.GroupId)).Select(x => (int?)x.Id).FirstOrDefaultAsync()
                  ?? throw new InvalidOperationException($"Unknown label '{l}'.");
            f = f with { LabelId = id };
        }
        if (get("component") is { Length: > 0 } c)
        {
            var id = int.TryParse(c, out var n) ? n
                : await db.ProjectComponents.Where(x => x.ProjectId == projectId && x.Name == c).Select(x => (int?)x.Id).FirstOrDefaultAsync()
                  ?? throw new InvalidOperationException($"Unknown component '{c}'.");
            f = f with { ComponentId = id };
        }
        if (get("assignee") is { Length: > 0 } a)
        {
            var id = a.Equals("none", StringComparison.OrdinalIgnoreCase) ? 0 : int.TryParse(a, out var n) ? n
                : await db.Users.Where(x => x.UserName == a).Select(x => (int?)x.Id).FirstOrDefaultAsync()
                  ?? throw new InvalidOperationException($"Unknown user '{a}'.");
            f = f with { AssigneeId = id };
        }
        f = f with { From = Date(get("from"), "from"), To = Date(get("to"), "to") };
        if (get("interval") is { Length: > 0 } iv)
            f = f with { Monthly = iv.Equals("month", StringComparison.OrdinalIgnoreCase) ? true : iv.Equals("week", StringComparison.OrdinalIgnoreCase) ? false
                : throw new InvalidOperationException("interval must be 'week' or 'month'.") };
        return f;
    }

    private static DateTime? Date(string? v, string name) => string.IsNullOrWhiteSpace(v) ? null
        : DateTime.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d
        : throw new InvalidOperationException($"{name} must be a date like 2026-11-30.");
}

public static class ReportsServiceCollectionExtensions
{
    public static IServiceCollection AddDeliveryReports(this IServiceCollection services)
    {
        services.AddScoped<DeliveryReportService>();
        services.AddScoped<DoraReportService>();
        services.AddScoped<PullRequestReportService>();
        services.AddScoped<ReportService>();
        return services;
    }

    /// <summary>
    /// CSV downloads for a signed-in browser session: <c>GET /p/{key}/reports/{report}.csv</c> with the filter's query
    /// string (see <see cref="ReportService.ParseFilterAsync"/>). The report is one of <see cref="ReportService.CsvNames"/>.
    /// </summary>
    public static void MapReportDownloads(this IEndpointRouteBuilder app)
    {
        app.MapGet("/p/{key}/reports/{report}.csv", async (HttpContext http, string key, string report, ProjectAccess access, IssueService issues, ReportService reports) =>
        {
            if (!int.TryParse(http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var uid)) return Results.Unauthorized();
            access.ActAs(uid);
            var project = await issues.GetProjectAsync(key);
            if (project == null) return Results.NotFound();
            if (!ReportService.CsvNames.Contains(report)) return Results.NotFound();
            try
            {
                var filter = await reports.ParseFilterAsync(project.Id, n => http.Request.Query[n].FirstOrDefault());
                var csv = await reports.CsvAsync(report, project.Id, filter);
                return Results.File(Csv.Utf8WithBom(csv), "text/csv; charset=utf-8", $"{project.Key.ToLowerInvariant()}-{report}-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
            }
            catch (ForbiddenException) { return Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
        }).RequireAuthorization();
    }
}
