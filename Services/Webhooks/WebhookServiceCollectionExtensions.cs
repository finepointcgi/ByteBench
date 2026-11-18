using GitBench.Api;
using GitBench.Services.Git;

namespace GitBench.Services.Webhooks;

public static class WebhookServiceCollectionExtensions
{
    /// <summary>Registers outgoing webhooks: dispatcher (queue), sender, background worker, notifier hooks and the push listener.</summary>
    public static IServiceCollection AddWebhooks(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<WebhookOptions>(config.GetSection("Webhooks"));
        services.AddSingleton<PublicBaseUrl>();
        services.AddSingleton<WebhookDispatcher>();
        services.AddSingleton<WebhookSender>();
        services.AddHostedService<WebhookWorker>();
        services.AddScoped<WebhookService>();
        services.AddScoped<WebhookNotifier>();
        services.AddSingleton<WebhookPushEvents>();
        services.AddScoped<PushCapture>();
        services.AddScoped<IGitPushListener, WebhookPushListener>();
        return services;
    }

    /// <summary>Captures the ref updates of git pushes for the <c>push</c> event. Must run before the Git HTTP endpoints.</summary>
    public static IApplicationBuilder UseWebhookPushCapture(this IApplicationBuilder app) =>
        app.UseMiddleware<WebhookPushCaptureMiddleware>();
}
