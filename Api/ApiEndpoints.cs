using ByteBench.Data;
using ByteBench.Services;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

namespace ByteBench.Api;

/// <summary>
/// The REST API at /api/v1 (minimal APIs) and its OpenAPI document at /api/v1/openapi.json.
/// Auth, scopes and error handling: <see cref="ApiFilter"/>. Permissions: the services, via <see cref="ProjectAccess"/>.
/// A project the caller can't see is 404; a visible project with a missing permission is 403.
/// </summary>
public static class ApiEndpoints
{
    public const string DocumentName = "v1";

    public static IServiceCollection AddByteBenchApi(this IServiceCollection services)
    {
        services.AddScoped<ApiCaller>();
        services.ConfigureHttpJsonOptions(o => Dto.Configure(o.SerializerOptions));
        services.AddOpenApi(DocumentName, o =>
        {
            o.ShouldInclude = d => d.RelativePath?.StartsWith("api/v1/", StringComparison.Ordinal) == true;
            o.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Info = new OpenApiInfo
                {
                    Title = "ByteBench API",
                    Version = "v1",
                    Description = "Authenticate with 'Authorization: Bearer gbp_…' (a personal access token with read_api or write_api), " +
                                  "or a signed-in browser session. Errors are RFC 9457 problem details. Lists take ?page= and ?perPage= (max 100) " +
                                  "and return X-Total-Count and Link headers.",
                };
                doc.Components ??= new OpenApiComponents();
                doc.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                doc.Components.SecuritySchemes["bearer"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http, Scheme = "bearer",
                    Description = "Personal access token (gbp_…) from Profile → Access tokens.",
                };
                doc.Security ??= [];
                doc.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("bearer", doc)] = [] });
                return Task.CompletedTask;
            });
            // Request bodies are read by hand (so bad JSON is a ProblemDetails 400); describe them here instead of with Accepts<T>(),
            // whose content-type routing constraint would send body-less or mistyped requests to the catch-all 404.
            o.AddOperationTransformer(async (op, ctx, ct) =>
            {
                if (ctx.Description.ActionDescriptor.EndpointMetadata.OfType<JsonBodyMetadata>().FirstOrDefault() is not { } body) return;
                op.RequestBody = new OpenApiRequestBody
                {
                    Required = !body.Optional,
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new() { Schema = await ctx.GetOrCreateSchemaAsync(body.Type, null, ct) },
                    },
                };
            });
        });
        return services;
    }

    public static IEndpointRouteBuilder MapByteBenchApi(this IEndpointRouteBuilder app)
    {
        app.MapOpenApi("/api/{documentName}/openapi.json");

        var api = app.MapGroup("/api/v1");
        api.DisableAntiforgery();
        api.AddEndpointFilter<ApiFilter>();
        api.ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/user", (ApiCaller caller) => new CurrentUserDto(caller.User.Id, caller.User.UserName ?? "", caller.User.DisplayName,
                caller.User.Email, caller.User.Color, caller.User.IsAdmin, caller.Scopes is { } s ? ScopeNames(s) : null))
            .WithName("GetCurrentUser").WithSummary("The authenticated user").WithTags("Users");

        api.MapGet("/projects", ListProjects).WithName("ListProjects").WithSummary("List projects you can see").WithTags("Projects");
        api.MapGet("/projects/{key}", GetProject).WithName("GetProject").WithSummary("Get a project").WithTags("Projects");

        api.MapIssueEndpoints();
        api.MapLinkEndpoints();
        api.MapRepoEndpoints();
        api.MapBlameEndpoints(); // blame for a file
        api.MapPullRequestFeatureEndpoints(); // drafts, threads, suggestions, compare
        api.MapAttachmentEndpoints();
        api.MapSprintEndpoints();
        api.MapReleaseEndpoints();
        api.MapExploreEndpoints(); // search, user profiles, stars
        api.MapGroupEndpoints();
        api.MapBoardEndpoints(); // boards: list, columns with issues
        api.MapRoadmapDashboardEndpoints(); // roadmap JSON, issue dates, dashboards
        api.MapWikiSnippetEndpoints(); // wiki pages and snippets
        api.MapGqlSearchEndpoints(); // advanced (GQL) issue search
        api.MapForkEndpoints(); // forks, sync fork
        api.MapFieldEndpoints(); // custom fields, components, versions, issue templates, Workbench mapping
        api.MapMergeQueueEndpoints(); // merge queue: view, enqueue, dequeue, move to front
        api.MapSecurityEndpoints(); // security findings (Night Owl), triage, PR new/fixed
        api.MapServiceDeskEndpoints(); // service desk queue, replies to requesters
        api.MapReportEndpoints(); // delivery reports: cycle/lead time, flow, DORA, pull requests
        api.MapFlagEndpoints(); // feature flags: management API
        api.MapEnvironmentEndpoints(); // environments, deployments, approvals, rollback, traceability, deploy freezes
        api.MapTriageEndpoints(); // AI triage suggestions for issues
        api.MapPipelineEndpoints(); // CI pipelines, jobs, logs, artifacts, CI variables
        api.MapPackageEndpoints(); // package registry: packages, versions, settings
        api.MapPagesEndpoints(); // Pages sites: status, deployments, rollback, settings
        api.MapAiReviewEndpoints(); // AI pull request review: settings, reviews, findings
        api.MapAutoMergeEndpoints(); // pull requests: merge when ready
        api.MapReleaseNotesEndpoints(); // draft release notes (AI and plain)

        // Anything else under /api: JSON 404 instead of the HTML not-found page.
        app.Map("/api/{**rest}", (HttpContext http) =>
        {
            ApiResults.PrepareResponse(http);
            return ApiResults.Problem(StatusCodes.Status404NotFound, "Not Found", "No such API endpoint.");
        }).ExcludeFromDescription();
        return app;
    }

    /// <summary>Documents a JSON request body in OpenAPI (see the operation transformer in <see cref="AddByteBenchApi"/>).</summary>
    public static RouteHandlerBuilder WithJsonBody<T>(this RouteHandlerBuilder b, bool optional = false) =>
        b.WithMetadata(new JsonBodyMetadata(typeof(T), optional));

    public sealed record JsonBodyMetadata(Type Type, bool Optional);

    public static List<string> ScopeNames(TokenScopes s) =>
        new[] { (TokenScopes.ReadRepository, "read_repository"), (TokenScopes.WriteRepository, "write_repository"),
                (TokenScopes.ReadApi, "read_api"), (TokenScopes.WriteApi, "write_api") }
            .Where(x => s.HasFlag(x.Item1)).Select(x => x.Item2).ToList();

    private static async Task<List<ProjectDto>> ListProjects(HttpContext http, ProjectAccess access, IDbContextFactory<AppDbContext> dbFactory, PublicBaseUrl url)
    {
        var paging = Paging.From(http.Request);
        var visible = await access.VisibleProjectIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var projects = await db.Projects.AsNoTracking().Where(p => visible.Contains(p.Id)).OrderBy(p => p.Key).ToListAsync();
        var page = projects.Skip(paging.Skip).Take(paging.PerPage).ToList();
        paging.WriteHeaders(http, projects.Count);
        var baseUrl = url.For(http.Request);
        var result = new List<ProjectDto>();
        foreach (var p in page) result.Add(Dto.Project(p, await access.GetRoleAsync(p.Id), baseUrl));
        return result;
    }

    private static async Task<ProjectDto> GetProject(HttpContext http, IssueService issues, ProjectAccess access, PublicBaseUrl url, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        return Dto.Project(project, await access.GetRoleAsync(project.Id), url.For(http.Request));
    }
}
