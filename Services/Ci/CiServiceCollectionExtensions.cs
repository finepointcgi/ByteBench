using System.Security.Claims;
using GitBench.Data;
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
        services.AddSingleton<CiJobTokens>(); // job tokens for the package registry
        services.AddScoped<CiTriggerService>();
        services.AddScoped<PipelineService>();
        services.AddScoped<IGitPushListener, CiPushListener>();
        services.AddScoped<IMergeCheck, PipelineMergeCheck>();
        services.AddHostedService<CiRunner>();
        services.AddHostedService<CiPullRequestWatcher>();
        // CI extras: artifacts, caches, schedules, environments
        services.AddSingleton<CiStorage>();
        services.AddSingleton<CiArtifactStore>();
        services.AddSingleton<CiCacheStore>();
        services.AddSingleton<CiDeploymentRecorder>();
        services.AddScoped<CiEnvironmentService>();
        services.AddScoped<DeployFreezeService>();
        services.AddScoped<DeploymentTraceService>();
        services.AddScoped<PipelineScheduleService>();
        services.AddHostedService<CiScheduleRunner>();
        services.AddHostedService<CiCleanupService>();
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

        // Artifacts: the whole zip, or one file from it. Always downloads (never rendered), like attachments.
        app.MapGet("/p/{key}/repos/{slug}/pipelines/{id:int}/jobs/{jobId:int}/artifacts.zip",
            async (HttpContext ctx, string key, string slug, int id, int jobId, ProjectAccess access, RepositoryService repos, PipelineService pipelines) =>
            {
                if (!int.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Results.Unauthorized();
                access.ActAs(userId);
                var repo = await repos.GetRepositoryAsync(key, slug);
                if (repo == null || await pipelines.GetArtifactAsync(repo, id, jobId) is not { } a) return Results.NotFound();
                SafeDownloadHeaders(ctx, $"{repo.Slug}-{id}-{a.JobName}-artifacts.zip");
                return Results.File(new FileStream(a.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true),
                    "application/octet-stream", enableRangeProcessing: true);
            }).RequireAuthorization();

        app.MapGet("/p/{key}/repos/{slug}/pipelines/{id:int}/jobs/{jobId:int}/artifacts/file",
            async (HttpContext ctx, string key, string slug, int id, int jobId, string path, ProjectAccess access, RepositoryService repos, PipelineService pipelines) =>
            {
                if (!int.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Results.Unauthorized();
                access.ActAs(userId);
                var repo = await repos.GetRepositoryAsync(key, slug);
                if (repo == null || await pipelines.GetArtifactAsync(repo, id, jobId) is not { } a) return Results.NotFound();
                var zip = System.IO.Compression.ZipFile.OpenRead(a.Path);
                var entry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/') == path && !e.FullName.EndsWith('/'));
                if (entry == null) { zip.Dispose(); return Results.NotFound(); }
                SafeDownloadHeaders(ctx, path.Split('/')[^1]);
                ctx.Response.RegisterForDispose(zip);
                ctx.Response.ContentLength = entry.Length;
                return Results.Stream(entry.Open(), "application/octet-stream");
            }).RequireAuthorization();
        return app;
    }

    /// <summary>Headers for a CI download: an attachment that is never sniffed, rendered or cached by shared caches.</summary>
    private static void SafeDownloadHeaders(HttpContext ctx, string fileName)
    {
        var h = ctx.Response.Headers;
        h.XContentTypeOptions = "nosniff";
        h.ContentSecurityPolicy = "sandbox; default-src 'none'";
        h["Cross-Origin-Resource-Policy"] = "same-origin";
        h.CacheControl = "private, no-store";
        var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(fileName);
        h.ContentDisposition = disposition.ToString();
    }

    /// <summary>URL of a job's artifact zip.</summary>
    public static string ArtifactZipUrl(Repository repo, int pipelineId, int jobId) => $"{PipelineUrls.Detail(repo, pipelineId)}/jobs/{jobId}/artifacts.zip";

    /// <summary>URL of one file inside a job's artifacts.</summary>
    public static string ArtifactFileUrl(Repository repo, int pipelineId, int jobId, string path) =>
        $"{PipelineUrls.Detail(repo, pipelineId)}/jobs/{jobId}/artifacts/file?path={Uri.EscapeDataString(path)}";
}
