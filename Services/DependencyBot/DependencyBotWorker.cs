using Microsoft.Extensions.Options;

namespace ByteBench.Services.DependencyBot;

/// <summary>Runs scheduled dependency checks: every <c>DependencyBot:PollSeconds</c> the due repositories, one at a time. <c>DependencyBot:Enabled=false</c> turns it off.</summary>
public sealed class DependencyBotWorker(DependencyBotRunner runner, IOptionsMonitor<DependencyBotOptions> options, ILogger<DependencyBotWorker> log) : BackgroundService
{
    /// <summary>One pass over the due repositories (nothing, and no network, when the worker is switched off). Returns how many repositories ran.</summary>
    public async Task<int> TickAsync(DateTime nowUtc, CancellationToken ct) =>
        options.CurrentValue.Enabled ? await runner.RunDueAsync(nowUtc, ct) : 0;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WorkerPulse.Report("dependency-bot", options.CurrentValue.Enabled);
        if (!options.CurrentValue.Enabled) { log.LogInformation("The dependency bot's schedule is turned off (DependencyBot:Enabled=false)"); return; }
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { WorkerPulse.Tick("dependency-bot"); await TickAsync(DateTime.UtcNow, stoppingToken); WorkerPulse.Ok("dependency-bot"); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { WorkerPulse.Fail("dependency-bot", ex); log.LogError(ex, "Dependency bot poll failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, options.CurrentValue.PollSeconds)), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}

public static class DependencyBotServiceCollectionExtensions
{
    /// <summary>The dependency update bot: guarded registry client, runner, settings service and the schedule worker (config section <c>DependencyBot</c>).</summary>
    public static IServiceCollection AddDependencyBot(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<DependencyBotOptions>(config.GetSection("DependencyBot"));
        services.AddSingleton<RegistryUrlGuard>();
        services.AddSingleton<RegistryHttp>();
        services.AddSingleton<RegistryCredentialProtector>();
        services.AddSingleton<RegistryClient>();
        services.AddSingleton<DependencyBotRunner>();
        services.AddSingleton<DependencyBotWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<DependencyBotWorker>());
        services.AddScoped<DependencyBotService>();
        services.AddScoped<DependencyRegistryService>();
        return services;
    }
}
