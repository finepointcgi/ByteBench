using ByteBench.Services.Git;

namespace ByteBench.Services.Releases;

public static class ReleaseServiceCollectionExtensions
{
    /// <summary>Tags, releases, release assets, source archives and the tag webhooks of pushes.</summary>
    public static IServiceCollection AddReleases(this IServiceCollection services)
    {
        services.AddScoped<ReleaseService>();
        services.AddScoped<ReleaseNotesService>(); // draft notes: plain and AI
        services.AddOptions<ReleaseNotesOptions>().BindConfiguration("Ai:ReleaseNotes");
        services.AddScoped<IGitPushListener, TagPushListener>();
        return services;
    }

    /// <summary>Source archive and release asset downloads.</summary>
    public static IEndpointRouteBuilder MapReleases(this IEndpointRouteBuilder app) => app.MapReleaseDownloads();
}
