using ByteBench.Data;
using ByteBench.Data.Postgres;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Hosting;

public enum DatabaseProvider { Sqlite, Postgres }

/// <summary>
/// Chooses the database provider from configuration: <c>Database:Provider</c> = <c>Sqlite</c> (default) or <c>Postgres</c>, with
/// <c>ConnectionStrings:Default</c> as the connection string for either. Everything in the app asks for
/// <see cref="AppDbContext"/> / <c>IDbContextFactory&lt;AppDbContext&gt;</c>; on Postgres the instances are the
/// <see cref="PostgresAppDbContext"/> subclass (same model, own migrations).
/// </summary>
public static class DatabaseSetup
{
    public const string DefaultSqliteConnection = "Data Source=bytebench.db";

    /// <summary>The database file name before the product was renamed from GitBench to ByteBench.</summary>
    public const string LegacySqliteConnection = "Data Source=gitbench.db";

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
                throw new InvalidOperationException("Database:Provider is Postgres: set ConnectionStrings:Default to an Npgsql connection string, e.g. \"Host=db;Database=bytebench;Username=bytebench;Password=...\".");
            return cs;
        }
        return !string.IsNullOrWhiteSpace(cs) ? cs : DefaultFor(Directory.GetCurrentDirectory());
    }

    /// <summary>
    /// The connection string used when none is configured. An install that predates the rename keeps working: with only the old
    /// <c>gitbench.db</c> present in <paramref name="directory"/>, that file is used.
    /// </summary>
    public static string DefaultFor(string directory) =>
        !File.Exists(Path.Combine(directory, "bytebench.db")) && File.Exists(Path.Combine(directory, "gitbench.db"))
            ? LegacySqliteConnection : DefaultSqliteConnection;

    /// <summary>The SQLite database file (absolute), or null on Postgres or for an in-memory database.</summary>
    public static string? SqliteFile(IConfiguration config)
    {
        if (GetProvider(config) != DatabaseProvider.Sqlite) return null;
        var b = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(GetConnectionString(config));
        return string.IsNullOrEmpty(b.DataSource) || b.DataSource == ":memory:" || b.Mode == Microsoft.Data.Sqlite.SqliteOpenMode.Memory
            ? null : Path.GetFullPath(b.DataSource);
    }

    public static IServiceCollection AddByteBenchDatabase(this IServiceCollection services, IConfiguration config)
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
