using Microsoft.Extensions.Options;

namespace ByteBench.Services.TrackerImport;

/// <summary>Runs queued tracker imports (<c>TrackerImport:MaxConcurrent</c> at a time). <c>TrackerImport:Enabled=false</c> leaves them queued.</summary>
public sealed class TrackerImportWorker(TrackerImportRunner runner, IOptionsMonitor<TrackerImportOptions> options, ILogger<TrackerImportWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await runner.RecoverInterruptedAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Cleaning up interrupted tracker imports failed"); }
        if (!options.CurrentValue.Enabled) { log.LogInformation("Background tracker imports are turned off (TrackerImport:Enabled=false)"); return; }

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
                    try { await runner.RunAsync(id, stoppingToken); }
                    catch (Exception ex) { log.LogError(ex, "Tracker import {Id} crashed", id); }
                    finally { slots.Release(); }
                }, CancellationToken.None));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        await Task.WhenAll(running);
    }
}

public static class TrackerImportServiceCollectionExtensions
{
    public static IServiceCollection AddTrackerImport(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<TrackerImportOptions>(config.GetSection("TrackerImport"));
        services.AddSingleton<TrackerImportTestHooks>();
        services.AddSingleton<TrackerUrlGuard>();
        services.AddSingleton<TrackerHttp>();
        services.AddSingleton<ISourceReaderFactory, SourceReaderFactory>();
        services.AddSingleton<TrackerImportRunner>();
        services.AddHostedService<TrackerImportWorker>();
        services.AddScoped<TrackerImportService>();
        return services;
    }
}
