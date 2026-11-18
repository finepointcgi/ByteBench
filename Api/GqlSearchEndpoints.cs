using GitBench.Data;
using GitBench.Services.Query;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Api;

/// <summary>GET /api/v1/search/issues?gql=: advanced issue search across every project the caller can view.</summary>
public static class GqlSearchEndpoints
{
    public static void MapGqlSearchEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/search/issues", SearchIssues).WithName("SearchIssuesGql").WithTags("Search")
            .WithSummary("Search issues with GQL")
            .WithDescription("gql is a GitBench Query Language query, e.g. project = WEB AND status in (\"In Progress\", Blocked) AND assignee = currentUser() " +
                             "ORDER BY priority DESC. Only projects you can view are searched. Paginated (page, perPage). A query error is a 400 whose " +
                             "problem details carry position (0-based) and length.");
    }

    private static async Task<List<IssueDto>> SearchIssues(HttpContext http, GqlService search, IDbContextFactory<AppDbContext> dbFactory, PublicBaseUrl url, string? gql)
    {
        if (string.IsNullOrWhiteSpace(gql)) throw ApiException.BadRequest("gql is required.");
        var paging = Paging.From(http.Request);
        GqlSearchResult result;
        try { result = await search.SearchAsync(gql, null, paging.Skip, paging.PerPage); }
        catch (GqlException ex)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "Invalid query", ex.Message,
                new Dictionary<string, object?> { ["position"] = ex.Position, ["length"] = ex.Length, ["reason"] = ex.Reason });
        }
        paging.WriteHeaders(http, result.Total);
        // SearchAsync doesn't load reporters; fetch them for this page only.
        var reporterIds = result.Issues.Select(i => i.ReporterId).Distinct().ToList();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var reporters = await db.Users.AsNoTracking().Where(u => reporterIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id);
            foreach (var i in result.Issues) i.Reporter = reporters[i.ReporterId];
        }
        var baseUrl = url.For(http.Request);
        return result.Issues.Select(i => Dto.Issue(i, baseUrl)).ToList();
    }
}
