namespace ByteBench.Services.Workbench;

public static class WorkbenchServiceCollectionExtensions
{
    /// <summary>Workbench integration: connection, API client, cached directory, read-only context, work-state push and plan sync.</summary>
    public static IServiceCollection AddWorkbenchIntegration(this IServiceCollection services)
    {
        services.AddSingleton<WorkbenchUrlGuard>();
        services.AddSingleton<WorkbenchCredentials>();
        services.AddSingleton<WorkbenchClient>();
        services.AddSingleton<WorkbenchDirectory>();
        services.AddSingleton<WorkbenchPushEngine>();
        services.AddHostedService<WorkbenchPushWorker>();
        services.AddScoped<WorkbenchConnectionService>();
        services.AddScoped<WorkbenchContextService>();
        services.AddScoped<WorkbenchPushService>();
        services.AddSingleton<WorkbenchPlanReader>();
        services.AddSingleton<WorkbenchPlanSignal>();
        services.AddScoped<WorkbenchPlanSyncService>();
        services.AddHostedService<WorkbenchPlanWorker>();
        return services;
    }
}
