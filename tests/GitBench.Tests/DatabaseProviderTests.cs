using GitBench.Data;
using GitBench.Data.Postgres;
using GitBench.Services.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GitBench.Tests;

/// <summary>Database:Provider selection, and the two migration sets (SQLite in Data/Migrations, Postgres in Data/MigrationsPostgres).</summary>
public class DatabaseProviderTests
{
    private static IConfiguration Config(params (string, string?)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Item1, v.Item2))).Build();

    [Theory]
    [InlineData(null, DatabaseProvider.Sqlite)]
    [InlineData("", DatabaseProvider.Sqlite)]
    [InlineData("Sqlite", DatabaseProvider.Sqlite)]
    [InlineData("sqlite", DatabaseProvider.Sqlite)]
    [InlineData("Postgres", DatabaseProvider.Postgres)]
    [InlineData("PostgreSQL", DatabaseProvider.Postgres)]
    [InlineData("npgsql", DatabaseProvider.Postgres)]
    public void Provider_is_read_from_configuration(string? value, DatabaseProvider expected) =>
        Assert.Equal(expected, DatabaseSetup.GetProvider(Config(("Database:Provider", value))));

    [Fact]
    public void An_unknown_provider_is_an_error()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DatabaseSetup.GetProvider(Config(("Database:Provider", "Oracle"))));
        Assert.Contains("Sqlite or Postgres", ex.Message);
    }

    [Fact]
    public void Postgres_needs_a_postgres_connection_string_and_sqlite_has_a_default()
    {
        Assert.Equal("Data Source=gitbench.db", DatabaseSetup.GetConnectionString(Config()));
        Assert.Throws<InvalidOperationException>(() => DatabaseSetup.GetConnectionString(Config(("Database:Provider", "Postgres"))));
        // A leftover SQLite connection string must not be handed to Npgsql.
        Assert.Throws<InvalidOperationException>(() => DatabaseSetup.GetConnectionString(Config(("Database:Provider", "Postgres"), ("ConnectionStrings:Default", "Data Source=gitbench.db"))));
        Assert.Equal("Host=db;Database=g", DatabaseSetup.GetConnectionString(Config(("Database:Provider", "Postgres"), ("ConnectionStrings:Default", "Host=db;Database=g"))));
        Assert.Null(DatabaseSetup.SqliteFile(Config(("Database:Provider", "Postgres"), ("ConnectionStrings:Default", "Host=db;Database=g"))));
        Assert.EndsWith("x.db", DatabaseSetup.SqliteFile(Config(("ConnectionStrings:Default", "Data Source=sub/x.db"))));
    }

    [Fact]
    public async Task The_container_hands_out_the_right_context_for_each_provider()
    {
        // Building contexts never opens a connection, so no Postgres server is needed.
        await using (var sqlite = new ServiceCollection().AddGitBenchDatabase(Config(("ConnectionStrings:Default", "Data Source=:memory:"))).BuildServiceProvider())
        {
            using var db = sqlite.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext();
            Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", db.Database.ProviderName);
            Assert.IsNotType<PostgresAppDbContext>(db);
        }
        await using var pg = new ServiceCollection()
            .AddGitBenchDatabase(Config(("Database:Provider", "Postgres"), ("ConnectionStrings:Default", "Host=localhost;Database=unused;Username=u;Password=p"))).BuildServiceProvider();
        using (var db = pg.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext())
        {
            Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
            Assert.IsType<PostgresAppDbContext>(db);
        }
        using var scoped = pg.CreateScope();
        Assert.IsType<PostgresAppDbContext>(scoped.ServiceProvider.GetRequiredService<AppDbContext>()); // what Identity resolves
    }

    [Fact]
    public async Task Both_providers_translate_like_case_insensitively_and_dates_as_utc()
    {
        // SQL translation needs no server either: look at the generated SQL.
        await using var pg = new ServiceCollection()
            .AddGitBenchDatabase(Config(("Database:Provider", "Postgres"), ("ConnectionStrings:Default", "Host=localhost;Database=unused"))).BuildServiceProvider();
        using var db = pg.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext();
        var sql = db.Issues.Where(i => EF.Functions.Like(i.Title, "%x%")).ToQueryString();
        Assert.Contains("ILIKE", sql); // SQLite's LIKE ignores case; so must Postgres
        var created = db.Model.FindEntityType(typeof(Issue))!.FindProperty(nameof(Issue.CreatedAt))!;
        Assert.Equal("timestamp with time zone", created.GetRelationalTypeMapping().StoreType);
        Assert.Equal(DateTimeKind.Utc, ((DateTime)created.GetValueConverter()!.ConvertFromProvider(new DateTime(2026, 1, 1))!).Kind);
    }

    [Fact]
    public void The_sqlite_migrations_match_the_model() => AssertNoPendingModelChanges(new AppDbContext(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=:memory:").Options));

    [Fact]
    public void The_postgres_migrations_match_the_same_model() => AssertNoPendingModelChanges(new PostgresAppDbContext(
        new DbContextOptionsBuilder<PostgresAppDbContext>().UseNpgsql("Host=localhost;Database=unused").Options));

    private static void AssertNoPendingModelChanges(DbContext db)
    {
        using (db)
        {
            Assert.False(db.Database.HasPendingModelChanges(),
                $"{db.GetType().Name}: the model changed since the last migration. Add one per provider:\n" +
                "  dotnet ef migrations add <Name> --context AppDbContext\n" +
                "  dotnet ef migrations add <Name> --context PostgresAppDbContext --output-dir Data/MigrationsPostgres");
            Assert.NotEmpty(db.GetService<IMigrationsAssembly>().Migrations);
        }
    }

    [Fact]
    public void Each_provider_has_its_own_migrations_and_snapshot_for_the_same_entities()
    {
        using var sqlite = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=:memory:").Options);
        using var pg = new PostgresAppDbContext(new DbContextOptionsBuilder<PostgresAppDbContext>().UseNpgsql("Host=localhost;Database=unused").Options);
        var a = sqlite.GetService<IMigrationsAssembly>();
        var b = pg.GetService<IMigrationsAssembly>();
        Assert.DoesNotContain(b.Migrations.Keys, a.Migrations.ContainsKey); // no migration id is shared
        Assert.NotNull(a.ModelSnapshot);
        Assert.NotNull(b.ModelSnapshot);
        // Same entity set in both (snapshots are generated from the one model).
        static string[] Tables(DbContext c) => c.Model.GetEntityTypes().Select(e => e.GetTableName() ?? e.DisplayName()).OrderBy(n => n).ToArray();
        Assert.Equal(Tables(sqlite), Tables(pg));
        Assert.Equal(a.ModelSnapshot!.Model.GetEntityTypes().Count(), b.ModelSnapshot!.Model.GetEntityTypes().Count());
    }

    [Fact]
    public void Hosting_review_flags_what_an_operator_should_fix()
    {
        var prod = new FakeEnv("Production");
        var weak = ProductionChecks.Evaluate(Config(("Seed:DemoData", "true"), ("Security:SecureCookies", "false")), prod);
        Assert.Contains(weak, c => c is { Key: "Seed:DemoData", Severity: CheckSeverity.Problem });
        Assert.Contains(weak, c => c is { Key: "Security:SecureCookies", Severity: CheckSeverity.Warning });
        Assert.Contains(weak, c => c.Key == "PublicBaseUrl");
        Assert.Contains(weak, c => c.Key == "DataProtection:KeysPath");
        Assert.Contains(weak, c => c.Key == "Backup:Enabled");

        var proxyless = ProductionChecks.Evaluate(Config(("PublicBaseUrl", "https://git.example.com")), prod);
        Assert.Contains(proxyless, c => c.Key == "ReverseProxy:Enabled");

        var tidy = ProductionChecks.Evaluate(Config(("PublicBaseUrl", "https://git.example.com"), ("ReverseProxy:Enabled", "true"), ("ReverseProxy:KnownProxies:0", "10.0.0.2"),
            ("DataProtection:KeysPath", Path.Combine(Path.GetTempPath(), "gb-keys-" + Guid.NewGuid().ToString("N"))), ("Backup:Enabled", "true"), ("AllowedHosts", "git.example.com"),
            ("Database:Provider", "Postgres"), ("ConnectionStrings:Default", "Host=x;Database=y"), ("Ci:Enabled", "false")), prod);
        Assert.DoesNotContain(tidy, c => c.Severity != CheckSeverity.Info);

        // An unwritable key ring is a hard problem (and AddGitBenchHosting refuses to start with it).
        if (!OperatingSystem.IsWindows())
            Assert.NotNull(ProductionChecks.KeysProblem("/proc/gitbench-keys"));
    }

    private sealed class FakeEnv(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "GitBench";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
