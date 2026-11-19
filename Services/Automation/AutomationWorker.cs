using ByteBench.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Automation;

/// <summary>
/// Background worker: processes queued events one at a time (so rules see changes in order), and runs the scheduled
/// trigger on a timer (<see cref="AutomationOptions.SchedulerIntervalSeconds"/>, via <see cref="TimeProvider"/>).
/// </summary>
public sealed class AutomationWorker(AutomationQueue queue, AutomationEngine engine, IDbContextFactory<AppDbContext> dbFactory, TimeProvider clock,
    IOptionsMonitor<AutomationOptions> options, ILogger<AutomationWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Know the bot's id if it exists already, so its changes count as automation (it is created on first use).
            await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
            var normalized = AutomationBot.UserName.ToUpperInvariant();
            queue.BotId ??= await db.Users.Where(u => u.NormalizedUserName == normalized).Select(u => (int?)u.Id).FirstOrDefaultAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Could not look up the automation bot"); }

        await Task.WhenAll(ProcessQueueAsync(stoppingToken), ScheduleAsync(stoppingToken));
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var e in queue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    if (options.CurrentValue.Enabled) await engine.ProcessAsync(e, ct);
                    else e.Completion?.TrySetResult(null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Automation event failed"); }
                finally { queue.Done(); }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task ScheduleAsync(CancellationToken ct)
    {
        if (!options.CurrentValue.SchedulerEnabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.CurrentValue.SchedulerIntervalSeconds)), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (!options.CurrentValue.Enabled) continue;
                try { await engine.RunDueScheduledAsync(clock.GetUtcNow().UtcDateTime, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Scheduled automation failed"); }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}

public static class AutomationServiceCollectionExtensions
{
    /// <summary>Registers automation rules: queue, engine, conditions, worker (events + scheduler) and the management service.</summary>
    public static IServiceCollection AddAutomation(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AutomationOptions>(config.GetSection("Automation"));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<AutomationQueue>();
        services.AddSingleton<AutomationRateLimiter>();
        services.AddSingleton<AutomationEngine>();
        services.AddSingleton<AutomationConditions>();
        services.AddHostedService<AutomationWorker>();
        services.AddScoped<AutomationService>();
        // Conditions: one line each. A new kind of condition implements IAutomationCondition and is added here.
        services.AddSingleton<IAutomationCondition, IssueTypeCondition>();
        services.AddSingleton<IAutomationCondition, PriorityCondition>();
        services.AddSingleton<IAutomationCondition, StatusCondition>();
        services.AddSingleton<IAutomationCondition, StatusCategoryCondition>();
        services.AddSingleton<IAutomationCondition, LabelCondition>();
        services.AddSingleton<IAutomationCondition, AssigneeCondition>();
        services.AddSingleton<IAutomationCondition, TitleCondition>();
        services.AddSingleton<IAutomationCondition, LinkedPullRequestCondition>();
        services.AddSingleton<IAutomationCondition, SubtasksDoneCondition>();
        services.AddSingleton<IAutomationCondition, GqlCondition>();
        return services;
    }
}
