using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ByteBench.Services.ServiceDesk;

public static class ServiceDeskServiceCollectionExtensions
{
    /// <summary>
    /// The service desk (<c>ServiceDesk</c> section): inbound mail (IMAP poller, off by default; signed webhook, off until
    /// <c>ServiceDesk:WebhookSecret</c> is set), the public request form and tracking page, the queue, and requester emails
    /// (through the app's email queue).
    /// </summary>
    public static IServiceCollection AddServiceDesk(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ServiceDeskOptions>(config.GetSection("ServiceDesk"));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ServiceDeskAddresses>();
        services.AddSingleton<ServiceDeskRateLimiter>();
        services.AddSingleton<ServiceDeskIntake>();
        services.AddScoped<ServiceDeskMailer>();
        services.AddScoped<ServiceDeskService>();
        services.AddSingleton<ServiceDeskStatusQueue>();
        services.AddHostedService<ServiceDeskStatusWorker>();

        services.AddSingleton<ImapMailSource>();
        services.AddSingleton<IInboundMailSource>(sp => sp.GetRequiredService<ImapMailSource>());
        services.AddSingleton<WebhookMailSource>();
        services.AddSingleton<IInboundMailSource>(sp => sp.GetRequiredService<WebhookMailSource>());
        services.AddSingleton<InboundMailPoller>();
        services.AddHostedService(sp => sp.GetRequiredService<InboundMailPoller>());
        return services;
    }

    /// <summary><c>POST /service-desk/inbound</c> (signed raw MIME). The form and tracking pages are Razor components.</summary>
    public static IEndpointRouteBuilder MapServiceDesk(this IEndpointRouteBuilder app)
    {
        WebhookMailSource.Map(app);
        return app;
    }
}
