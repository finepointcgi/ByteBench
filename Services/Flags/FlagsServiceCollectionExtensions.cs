namespace GitBench.Services.Flags;

public static class FlagsServiceCollectionExtensions
{
    /// <summary>Feature flags: the management service, SDK evaluation and the live-update feed.</summary>
    public static IServiceCollection AddFeatureFlags(this IServiceCollection services)
    {
        services.AddSingleton<FlagFeed>();
        services.AddScoped<FlagService>();
        services.AddScoped<FlagSdkService>();
        return services;
    }
}
