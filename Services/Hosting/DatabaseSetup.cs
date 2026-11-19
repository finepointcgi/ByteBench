using GitBench.Data;
using GitBench.Data.Postgres;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Hosting;

public enum DatabaseProvider { Sqlite, Postgres }

/// <summary>
/// Chooses the database provider from configuration: <c>Database:Provider</c> = <c>Sqlite</c> (default) or <c>Postgres</c>, with
/// <c>ConnectionStrings:Default</c> as the connection string for either. Everything in the app asks for
/// <see cref="AppDbContext"/> / <c>IDbContextFactory&lt;AppDbContext&gt;</c>; on Postgres the instances are the
/// <see cref="PostgresAppDbContext"/> subclass (same model, own migrations).
/// </summary>
public static class DatabaseSetup
{
    public const string DefaultSqliteConnection = "Data Source=gitbench.db";

    public static DatabaseProvider GetProvider(IConfiguration config)
    {
        var value = config["Database:Provider"]?.Trim();
        if (string.IsNullOrEmpty(value)) return DatabaseProvider.Sqlite;
        return value.ToLowerInvariant() switch
        {
            "sqlite" => DatabaseProvider.Sqlite,
            "postgres" or "postgresql" or "npgsql" => DatabaseProvider.Postgres,
            _ => throw new InvalidOperationException($"Database:Provider must be Sqlite or Postgres, not '{value}'."),
        };
    }

    public static string GetConnectionString(IConfiguration config)
    {
        var cs = config.GetConnectionString("Default");
        if (GetProvider(config) == DatabaseProvider.Postgres)
        {
            if (string.IsNullOrWhiteSpace(cs) || cs.TrimStart().StartsWith("Data Source", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Database:Provider is Postgres: set ConnectionStrings:Default to an Npgsql connection string, e.g. \"Host=db;Database=gitbench;Username=gitbench;Password=...\".");
            return cs;
        }
        return string.IsNullOrWhiteSpace(cs) ? DefaultSqliteConnection : cs;
    }

    /// <summary>The SQLite database file (absolute), or null on Postgres or for an in-memory database.</summary>
    public static string? SqliteFile(IConfiguration config)
    {
        if (GetProvider(config) != DatabaseProvider.Sqlite) return null;
        var b = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(GetConnectionString(config));
        return string.IsNullOrEmpty(b.DataSource) || b.DataSource == ":memory:" || b.Mode == Microsoft.Data.Sqlite.SqliteOpenMode.Memory
            ? null : Path.GetFullPath(b.DataSource);
    }

    public static IServiceCollection AddGitBenchDatabase(this IServiceCollection services, IConfiguration config)
    {
        var cs = GetConnectionString(config);
        if (GetProvider(config) == DatabaseProvider.Sqlite)
        {
            services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(cs));
            return services;
        }

        services.AddDbContextFactory<PostgresAppDbContext>(o => o.UseNpgsql(cs).AddInterceptors(PostgresLikeInterceptor.Instance));
        services.AddSingleton<IDbContextFactory<AppDbContext>>(sp => new PostgresFactoryAdapter(sp.GetRequiredService<IDbContextFactory<PostgresAppDbContext>>()));
        services.AddScoped<AppDbContext>(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());
        return services;
    }

    private sealed class PostgresFactoryAdapter(IDbContextFactory<PostgresAppDbContext> inner) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => inner.CreateDbContext();
        public async Task<AppDbContext> CreateDbContextAsync(CancellationToken ct = default) => await inner.CreateDbContextAsync(ct);
    }
}
