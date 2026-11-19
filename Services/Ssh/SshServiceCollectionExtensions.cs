namespace ByteBench.Services.Ssh;

public static class SshServiceCollectionExtensions
{
    /// <summary>SSH keys, deploy keys and the embedded git SSH server (<c>Ssh:*</c>; off unless <c>Ssh:Enabled</c>).</summary>
    public static IServiceCollection AddSshGit(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SshOptions>(config.GetSection(SshOptions.Section));
        services.AddScoped<SshKeyService>();
        services.AddSingleton<SshGitServer>();
        services.AddHostedService(sp => sp.GetRequiredService<SshGitServer>());
        return services;
    }
}
