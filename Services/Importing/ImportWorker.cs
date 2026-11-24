using Microsoft.Extensions.Options;

namespace ByteBench.Services.Importing;

/// <summary>
/// Runs queued imports (<c>Import:MaxConcurrent</c> at a time). On start it marks imports a previous process left queued or
/// running as failed and removes their temporary data. <c>Import:Enabled=false</c> leaves imports queued.
/// </summary>
public sealed class ImportWorker(ImportRunner runner, IOptionsMonitor<ImportOptions> options, ILogger<ImportWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await runner.RecoverInterruptedAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Cleaning up interrupted imports failed"); }
        WorkerPulse.Report("repo-import", options.CurrentValue.Enabled);
        if (!options.CurrentValue.Enabled)
        {
            log.LogInformation("Background repository imports are turned off (Import:Enabled=false)");
            return;
        }

        using var slots = new SemaphoreSlim(Math.Max(1, options.CurrentValue.MaxConcurrent));
        var running = new List<Task>();
        try
        {
            await foreach (var id in runner.ReadQueueAsync(stoppingToken))
            {
                await slots.WaitAsync(stoppingToken);
                running.RemoveAll(t => t.IsCompleted);
                running.Add(Task.Run(async () =>
                {
                    try { WorkerPulse.Tick("repo-import"); await runner.RunAsync(id, stoppingToken); WorkerPulse.Ok("repo-import"); }
                    catch (Exception ex) { WorkerPulse.Fail("repo-import", ex); log.LogError(ex, "Import {Id} crashed", id); }
                    finally { slots.Release(); }
                }, CancellationToken.None));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        await Task.WhenAll(running);
    }
}

public static class ImportServiceCollectionExtensions
{
    public static IServiceCollection AddRepositoryImport(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ImportOptions>(config.GetSection("Import"));
        services.AddSingleton<ImportTestHooks>();
        services.AddSingleton<ImportRunner>();
        services.AddHostedService<ImportWorker>();
        services.AddScoped<ImportService>();
        return services;
    }
}
