namespace ByteBench.Services.Tests;

public static class TestsServiceCollectionExtensions
{
    /// <summary>Test management: cases, plans, runs, results, automated report ingestion (JUnit, TRX, xUnit, CTRF) and the results matrix.</summary>
    public static IServiceCollection AddTestManagement(this IServiceCollection services)
    {
        services.AddScoped<TestService>();
        services.AddScoped<TestReportIngestor>();
        services.AddScoped<Releases.ReleaseHubService>();
        services.AddScoped<Releases.ReleaseAutoAttachService>();
        return services;
    }
}
