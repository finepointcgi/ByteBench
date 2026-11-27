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
        services.AddSingleton<WorkspaceEditorTunnels>();
        services.AddScoped<WorkspaceEditorService>(); // local editors over an SSH tunnel (VS Code Remote-SSH, JetBrains Gateway)
        services.AddScoped<WorkspaceGitService>(); // commit, push and open a pull request from the workspace bar
        services.AddScoped<WorkspaceLaunchService>(); // start a workspace from an issue or a pull request
        return services;
    }
}
