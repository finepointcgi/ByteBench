using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ByteBench.Services.Query;

/// <summary>Registration of the query language (GQL), CSV export and CSV import.</summary>
public static class QuerySetup
{
    public static IServiceCollection AddByteBenchQuery(this IServiceCollection services, IConfiguration config)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IQueryFieldProvider, BuiltInQueryFields>();
        services.AddSingleton<GqlFieldRegistry>();
        services.AddScoped<GqlService>();
        services.AddScoped<IssueCsvExporter>();
        services.Configure<CsvImportOptions>(config.GetSection("CsvImport"));
        services.AddScoped<CsvImportService>();
        return services;
    }

    /// <summary>
    /// CSV downloads (signed-in browser session): <c>GET /p/{key}/issues/export.csv</c> takes the issue list's own query
    /// string (basic filters or <c>gql=</c>) and <c>GET /issues/export.csv?gql=</c> searches every visible project. Both take
    /// <c>columns=</c> (comma-separated ids from <see cref="IssueCsvExporter.Columns"/>) and return at most
    /// <see cref="IssueCsvExporter.MaxRows"/> rows as UTF-8 with a BOM.
    /// </summary>
    public static void MapIssueCsvExport(this IEndpointRouteBuilder app)
    {
        app.MapGet("/p/{key}/issues/export.csv", async (HttpContext http, string key, ProjectAccess access, IssueService issues, GqlService gql,
            IssueCsvExporter exporter, string? columns) =>
        {
            if (UserIdOf(http.User) is not { } uid) return Results.Unauthorized();
            access.ActAs(uid);
            var project = await issues.GetProjectAsync(key);
            if (project == null) return Results.NotFound();
            var filter = IssueFilterQuery.Parse(http.Request.QueryString.Value);
            List<int> ids;
            if (!string.IsNullOrWhiteSpace(filter.Gql))
            {
                try { ids = await gql.SearchIdsAsync(filter.Gql, [project.Id], IssueCsvExporter.MaxRows); }
                catch (GqlException ex) { return Results.BadRequest(ex.Message); }
            }
            else ids = (await issues.GetIssuesAsync(project.Id, filter)).Select(i => i.Id).ToList();
            var csv = await exporter.ExportAsync(ids, IssueCsvExporter.Pick(columns));
            return Results.File(Csv.Utf8WithBom(csv), "text/csv; charset=utf-8", $"{project.Key.ToLowerInvariant()}-issues-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
        }).RequireAuthorization();

        app.MapGet("/issues/export.csv", async (HttpContext http, ProjectAccess access, GqlService gql, IssueCsvExporter exporter, string? columns) =>
        {
            if (UserIdOf(http.User) is not { } uid) return Results.Unauthorized();
            access.ActAs(uid);
            var text = http.Request.Query["gql"].ToString();
            if (string.IsNullOrWhiteSpace(text)) return Results.BadRequest("gql is required.");
            List<int> ids;
            try { ids = await gql.SearchIdsAsync(text, null, IssueCsvExporter.MaxRows); }
            catch (GqlException ex) { return Results.BadRequest(ex.Message); }
            var csv = await exporter.ExportAsync(ids, IssueCsvExporter.Pick(columns));
            return Results.File(Csv.Utf8WithBom(csv), "text/csv; charset=utf-8", $"issues-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
        }).RequireAuthorization();
    }

    private static int? UserIdOf(ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
