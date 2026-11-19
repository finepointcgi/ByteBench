using System.Security.Claims;
using System.Text;

namespace GitBench.Services.Security;

public static class SecurityServiceCollectionExtensions
{
    /// <summary>
    /// Registers security scanning (Night Owl): options (section <c>NightOwl</c>), report ingestion for the CI runner, the
    /// UI/API service and the "Security" merge check.
    /// </summary>
    public static IServiceCollection AddSecurityScanning(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<NightOwlOptions>(config.GetSection("NightOwl"));
        services.AddSingleton<SecurityIngestor>();
        services.AddScoped<SecurityService>();
        services.AddScoped<IMergeCheck, SecurityMergeCheck>();
        return services;
    }

    /// <summary>GET {repo}/security/triage.json: the repository's decisions as Night Owl's <c>.nightowl/triage.json</c> (a download).</summary>
    public static IEndpointRouteBuilder MapSecurityDownloads(this IEndpointRouteBuilder app)
    {
        app.MapGet("/p/{key}/repos/{slug}/security/triage.json",
            async (HttpContext ctx, string key, string slug, ProjectAccess access, RepositoryService repos, SecurityService security) =>
            {
                if (!int.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Results.Unauthorized();
                access.ActAs(userId);
                var repo = await repos.GetRepositoryAsync(key, slug);
                if (repo == null || !await security.CanViewAsync(repo)) return Results.NotFound();
                var h = ctx.Response.Headers;
                h.XContentTypeOptions = "nosniff";
                h.CacheControl = "private, no-store";
                return Results.File(Encoding.UTF8.GetBytes(await security.ExportTriageAsync(repo)), "application/json", "triage.json");
            }).RequireAuthorization();
        return app;
    }
}
