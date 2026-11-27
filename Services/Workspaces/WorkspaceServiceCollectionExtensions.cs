using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ByteBench.Services.Workspaces;

public static class WorkspaceServiceCollectionExtensions
{
    /// <summary>Workspaces: Docker dev environments with OpenVSCode Server (options section <c>Workspaces</c>).</summary>
    public static IServiceCollection AddWorkspaces(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<WorkspaceOptions>(config.GetSection("Workspaces"));
        services.AddSingleton<IWorkspaceBackend, DockerWorkspaceBackend>();
        services.AddSingleton<WorkspaceRuntime>();
        services.AddSingleton<WorkspaceUrls>();
        services.AddSingleton<WorkspaceAudit>();
        services.AddSingleton<WorkspaceImage>();
        services.AddSingleton<WorkspaceTasks>();
        services.AddSingleton<WorkspaceSecretStore>();
        services.AddSingleton<SubmoduleResolver>();
        services.AddScoped<WorkspaceSecretService>();
        services.AddScoped<UserWorkspaceSettingsService>();
        services.TryAddSingleton<Api.PublicBaseUrl>();
        services.AddSingleton<WorkspaceLifecycle>();
        services.AddSingleton<WorkspaceReaper>();
        services.AddHostedService(sp => sp.GetRequiredService<WorkspaceReaper>());
        services.AddHostedService<WorkspaceImageStartup>();
        services.AddScoped<WorkspaceService>();
        services.AddScoped<WorkspaceAiService>();
        services.AddScoped<AiChangeService>(); // review mode: pending change sets, accept/reject, the per-user/workspace setting
        services.AddScoped<AiRunService>(); // task-driven AI runs: request, read, cancel
        services.AddSingleton<AiRunQueue>();
        services.AddSingleton<AiRunExecutor>();
        services.AddSingleton<AiRunWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<AiRunWorker>());
        return services;
    }
}
