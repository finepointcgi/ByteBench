namespace ByteBench.Services.Packages;

public static class PackagesServiceCollectionExtensions
{
    /// <summary>
    /// The package and container registry: blob storage (<c>Packages:Root</c>, <c>Packages:MaxBlobBytes</c>), the daily
    /// sweep of unreferenced blobs, auth (personal access tokens and CI job tokens) and the registry service.
    /// </summary>
    public static IServiceCollection AddPackageRegistry(this IServiceCollection services)
    {
        services.AddSingleton<PackageStorage>();
        services.AddHostedService<PackageSweeper>();
        services.AddScoped<RegistryAuth>();
        services.AddScoped<PackageService>();
        return services;
    }

    /// <summary>
    /// NuGet (<c>/packages/{KEY}/nuget/index.json</c>), npm (<c>/packages/{KEY}/npm/</c>) and the OCI registry (<c>/v2/</c>).
    /// </summary>
    public static IEndpointRouteBuilder MapPackageRegistry(this IEndpointRouteBuilder app)
    {
        app.MapNuGetFeed();
        app.MapNpmRegistry();
        app.MapContainerRegistry();
        return app;
    }
}
