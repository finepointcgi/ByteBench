using ByteBench.Api;
using ByteBench.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ByteBench.Services.Email;

public static class EmailServiceCollectionExtensions
{
    /// <summary>
    /// Email delivery (<c>Email</c> section), notification preferences and the notification email worker.
    /// The sender is <c>Email:Sender</c> (<c>Pickup</c> or <c>Smtp</c>); it defaults to Pickup in Development so nothing is
    /// sent for real, and to Smtp elsewhere.
    /// </summary>
    public static IServiceCollection AddEmail(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        services.Configure<EmailOptions>(config.GetSection("Email"));
        var sender = config["Email:Sender"] is { Length: > 0 } s ? s : env.IsDevelopment() ? "Pickup" : "Smtp";
        if (sender.Equals("Smtp", StringComparison.OrdinalIgnoreCase)) services.AddSingleton<IEmailSender, SmtpEmailSender>();
        else services.AddSingleton<IEmailSender, PickupDirectoryEmailSender>();

        services.TryAddSingleton<PublicBaseUrl>();
        services.AddSingleton<EmailQueue>();
        services.AddHostedService<EmailWorker>();
        services.AddSingleton<UnsubscribeTokens>();
        services.AddSingleton<NotificationEmailComposer>();
        services.AddHostedService<NotificationEmailWorker>();
        services.AddScoped<NotificationPreferences>();
        services.AddScoped<MentionLookup>();
        services.AddSingleton<IEmailSender<User>, IdentityEmailSender>();
        return services;
    }
}
