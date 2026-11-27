using ByteBench.Services.Git;
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
        services.AddSingleton<IEgressConnector, DnsEgressConnector>();
        services.AddSingleton<WorkspaceEgress>();
        services.AddSingleton<WorkspaceShareStore>();
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
        services.AddSingleton<WorkspaceIdlePolicy>();
        services.AddSingleton<WorkspacePrebuildService>();
        services.AddHostedService(sp => sp.GetRequiredService<WorkspacePrebuildService>());
        services.AddScoped<WorkspacePrebuildManager>();
        services.AddScoped<IGitPushListener, PrebuildPushListener>();
        services.AddScoped<WorkspaceLimitService>();
        services.AddScoped<WorkspaceService>();
        services.AddScoped<WorkspaceAiService>();
        services.AddSingleton<WorkspaceEditorTunnels>();
        services.AddScoped<WorkspaceEditorService>(); // local editors over an SSH tunnel (VS Code Remote-SSH, JetBrains Gateway)
        services.AddScoped<WorkspaceGitService>(); // commit, push and open a pull request from the workspace bar
        services.AddScoped<WorkspaceLaunchService>(); // start a workspace from an issue or a pull request
        services.AddScoped<WorkspaceSharingService>();
        services.AddScoped<WorkspaceAuditService>();
        services.AddScoped<WorkspaceEgressReader>();
        services.AddScoped<WorkspaceEgressAdmin>();
        services.AddSingleton<WorkspaceMaintenanceService>();
        services.AddHostedService(sp => sp.GetRequiredService<WorkspaceMaintenanceService>());
        services.AddScoped<AiChangeService>(); // review mode: pending change sets, accept/reject, the per-user/workspace setting
        services.AddScoped<AiRunService>(); // task-driven AI runs: request, read, cancel
        services.AddScoped<AiRunBackendResolver>(); // API or Claude Code CLI: which one runs use, and why not
        services.AddSingleton<AiRunQueue>();
        services.AddSingleton<AiRunExecutor>();
        services.AddSingleton<AiRunWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<AiRunWorker>());
        return services;
    }
}
