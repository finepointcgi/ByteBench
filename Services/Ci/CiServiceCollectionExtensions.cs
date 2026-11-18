using System.Security.Claims;
using GitBench.Services.Git;

namespace GitBench.Services.Ci;

public static class CiServiceCollectionExtensions
{
    /// <summary>Registers CI pipelines: options (section <c>Ci</c>), runner, triggers, UI service and the merge check.</summary>
    public static IServiceCollection AddCiPipelines(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<CiOptions>(config.GetSection("Ci"));
        services.AddSingleton<CiQueue>();
        services.AddSingleton<CiDocker>();
        services.AddSingleton<CiRefTracker>();
        services.AddScoped<CiTriggerService>();
        services.AddScoped<PipelineService>();
        services.AddScoped<IGitPushListener, CiPushListener>();
        services.AddScoped<IMergeCheck, PipelineMergeCheck>();
        services.AddHostedService<CiRunner>();
        services.AddHostedService<CiPullRequestWatcher>();
        return services;
    }

    /// <summary>GET {pipeline}/steps/{stepId}/log: a step's full log as plain text (the page shows only the tail of long logs).</summary>
    public static IEndpointRouteBuilder MapCiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/p/{key}/repos/{slug}/pipelines/{id:int}/steps/{stepId:int}/log",
            async (HttpContext ctx, string key, string slug, int id, int stepId, ProjectAccess access, RepositoryService repos, PipelineService pipelines) =>
            {
                if (!int.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Results.Unauthorized();
                access.ActAs(userId);
                var repo = await repos.GetRepositoryAsync(key, slug);
                if (repo == null || await pipelines.GetLogAsync(repo, id, stepId) is not { } log) return Results.NotFound();
                ctx.Response.Headers.CacheControl = "no-store";
                ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                return Results.Text(log.Text, "text/plain; charset=utf-8");
            }).RequireAuthorization();
        return app;
    }
}
