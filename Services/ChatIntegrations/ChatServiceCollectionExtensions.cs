using GitBench.Services.Webhooks;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GitBench.Services.ChatIntegrations;

public static class ChatServiceCollectionExtensions
{
    /// <summary>
    /// Registers Slack and Microsoft Teams integrations: the chat transport for the webhook pipeline (call after
    /// <c>AddWebhooks</c>), the integrations settings service, the Slack app (signature checks, slash command,
    /// interactivity, unfurls, account links) and Slack DMs. Config: <c>Slack:*</c> (<see cref="SlackOptions"/>) and
    /// <c>ChatIntegrations:*</c> (<see cref="ChatIntegrationOptions"/>).
    /// </summary>
    public static IServiceCollection AddChatIntegrations(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SlackOptions>(config.GetSection("Slack"));
        services.Configure<ChatIntegrationOptions>(config.GetSection("ChatIntegrations"));
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient(ChatHttp.ClientName, c =>
            {
                c.Timeout = Timeout.InfiniteTimeSpan; // ChatHttp applies its own per-request timeout
                c.DefaultRequestHeaders.UserAgent.ParseAdd("GitBench-Chat/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<WebhookSender>().CreateGuardedHandler());
        services.AddSingleton<ChatHttp>();
        services.AddSingleton<IWebhookTransport, ChatTransport>();
        services.AddSingleton<SlackSignature>();
        services.AddScoped<ChatIntegrationService>();
        services.AddScoped<SlackAccountLinks>();
        services.AddScoped<SlackCommandService>();
        services.AddHostedService<SlackDmWorker>();
        return services;
    }
}
