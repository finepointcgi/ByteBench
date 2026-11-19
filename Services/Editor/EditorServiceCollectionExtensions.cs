using ByteBench.Services.Editor.Ai;

namespace ByteBench.Services.Editor;

public static class EditorServiceCollectionExtensions
{
    /// <summary>The web editor (workspaces, commits) and its AI assistant (Anthropic API or Claude Code backends).</summary>
    public static IServiceCollection AddWebEditor(this IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<EditorWorkspaceService>();
        services.AddScoped<EditorPushNotifier>();

        services.Configure<EditorAiOptions>(config.GetSection("Ai:Editor"));
        services.Configure<ClaudeCodeOptions>(config.GetSection("Ai:ClaudeCode"));
        services.AddSingleton<EditorAiRateLimiter>();
        services.AddSingleton<IEditorChatModel, AnthropicEditorChatModel>();
        services.AddSingleton<ClaudeCodeDetector>();
        services.AddSingleton<IEditorAiBackend, ApiEditorBackend>();
        services.AddSingleton<IEditorAiBackend, ClaudeCodeEditorBackend>();
        services.AddHostedService<ClaudeCodeStartupCheck>();
        services.AddScoped<EditorAiService>();
        return services;
    }
}
