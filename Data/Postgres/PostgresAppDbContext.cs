using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ByteBench.Data.Postgres;

/// <summary>
/// The same model as <see cref="AppDbContext"/> on PostgreSQL (<c>Database:Provider=Postgres</c>). The SQLite migrations in
/// <c>Data/Migrations</c> belong to <see cref="AppDbContext"/>; this context type has its own migrations in
/// <c>Data/MigrationsPostgres</c> and its own model snapshot, generated from the one model (no entity is declared twice).
/// The app resolves <see cref="AppDbContext"/> either way: see <see cref="Services.Hosting.DatabaseSetup"/>.
/// </summary>
/// <remarks>
/// After a model change, add one migration per provider:
/// <code>
/// dotnet ef migrations add Name --context AppDbContext
/// dotnet ef migrations add Name --context PostgresAppDbContext --output-dir Data/MigrationsPostgres
/// </code>
/// <c>ProviderMigrationsTests</c> fails when either snapshot is behind the model.
/// </remarks>
public sealed class PostgresAppDbContext(DbContextOptions<PostgresAppDbContext> options) : AppDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        // The model's raw SQL fragments are written for SQLite (unquoted identifiers, booleans as 1). PostgreSQL folds unquoted
        // names to lower case and has a real boolean type, so the few affected index filters and checks are restated here.
        b.Entity<Watch>().ToTable(t => t.HasCheckConstraint("CK_Watch_OneTarget", "(\"IssueId\" IS NULL) <> (\"PullRequestId\" IS NULL)"));
        b.Entity<Notification>().HasIndex(n => n.EmailPending).HasFilter("\"EmailPending\" = TRUE");
        b.Entity<Board>().HasIndex(x => x.ProjectId, "IX_Boards_OneDefaultPerProject").IsUnique().HasFilter("\"IsDefault\" = TRUE");
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        base.ConfigureConventions(b);
        // ByteBench stores UTC. SQLite keeps no kind (values come back Unspecified); Npgsql's timestamptz insists on UTC kinds,
        // so every DateTime is written as UTC and read back as UTC.
        b.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        b.Properties<DateTime?>().HaveConversion<UtcNullableDateTimeConverter>();
    }
}

/// <summary>Writes a DateTime as UTC (Unspecified is taken to be UTC, Local is converted) and reads it back as UTC.</summary>
public sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => v.Kind == DateTimeKind.Utc ? v : v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

public sealed class UtcNullableDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
    v => v == null ? null : v.Value.Kind == DateTimeKind.Utc ? v : v.Value.Kind == DateTimeKind.Local ? v.Value.ToUniversalTime() : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc),
    v => v == null ? null : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));

/// <summary>Lets <c>dotnet ef</c> build the Postgres model without a database (the connection string is never opened).</summary>
public sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<PostgresAppDbContext>
{
    public PostgresAppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PostgresAppDbContext>().UseNpgsql("Host=localhost;Database=bytebench_design").Options);
}

/// <summary>Same for the SQLite context, so <c>--context AppDbContext</c> works while there are two contexts.</summary>
public sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=bytebench-design.db").Options);
}
