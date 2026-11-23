namespace ByteBench.Services.Signing;

public static class SigningServiceCollectionExtensions
{
    /// <summary>Registers signed commit verification: options (section <c>Signing</c>), the tool runner, GPG keys and the verifier.</summary>
    public static IServiceCollection AddSigning(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SigningOptions>(config.GetSection("Signing"));
        services.AddSingleton<SigningTools>();
        services.AddScoped<SigningMaterial>();
        services.AddScoped<GpgKeyService>();
        services.AddScoped<CommitSignatureService>();
        return services;
    }
}
