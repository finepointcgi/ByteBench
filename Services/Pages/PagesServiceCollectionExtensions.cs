namespace ByteBench.Services.Pages;

public static class PagesServiceCollectionExtensions
{
    /// <summary>Static sites from CI output: options (section <c>Pages</c>), storage, deployer, UI service, access tokens and the daily cleanup.</summary>
    public static IServiceCollection AddPages(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<PagesOptions>(config.GetSection("Pages"));
        services.AddSingleton<PagesStorage>();
        services.AddSingleton<PagesLinks>();
        services.AddSingleton<PagesAccessTokens>();
        services.AddSingleton<PagesDeployer>();
        services.AddScoped<PagesService>();
        services.AddHostedService<PagesCleanupService>();
        return services;
    }

    /// <summary>Adds <see cref="PagesMiddleware"/> to the pipeline.</summary>
    public static IApplicationBuilder UsePages(this IApplicationBuilder app) => app.UseMiddleware<PagesMiddleware>();
}

/// <summary>Hourly: removes expired review sites and stray site folders.</summary>
public sealed class PagesCleanupService(PagesDeployer deployer, ILogger<PagesCleanupService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            WorkerPulse.Report("pages-cleanup", true);
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            do
            {
                try { WorkerPulse.Tick("pages-cleanup"); await deployer.CleanupAsync(stoppingToken); WorkerPulse.Ok("pages-cleanup"); }
                catch (Exception ex) when (ex is not OperationCanceledException) { WorkerPulse.Fail("pages-cleanup", ex); log.LogError(ex, "Pages cleanup failed"); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
