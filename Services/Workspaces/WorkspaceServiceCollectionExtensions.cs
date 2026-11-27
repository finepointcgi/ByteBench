using ByteBench.Services.Git;

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
        services.AddSingleton<WorkspaceLifecycle>();
        services.AddSingleton<WorkspaceReaper>();
        services.AddHostedService(sp => sp.GetRequiredService<WorkspaceReaper>());
        services.AddHostedService<WorkspaceImageStartup>();
        services.AddSingleton<WorkspaceIdlePolicy>();
        services.AddSingleton<WorkspacePrebuildService>();
        services.AddHostedService(sp => sp.GetRequiredService<WorkspacePrebuildService>());
        services.AddScoped<WorkspacePrebuildManager>();
        services.AddScoped<IGitPushListener, PrebuildPushListener>();
        services.AddScoped<WorkspaceLimitService>();
        services.AddScoped<WorkspaceService>();
        services.AddScoped<WorkspaceAiService>();
        return services;
    }
}
