using System.Globalization;
using System.Security.Cryptography;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Ci;
using ByteBench.Services.Hosting.DbMigration;
using ByteBench.Services.Webhooks;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NpgsqlTypes;

namespace ByteBench.Tests;

/// <summary>Always on SQLite (even when the suite runs on Postgres): the source database of a migration, with a shared key ring.</summary>
public sealed class SqliteSourceApp : TestApp
{
    public string SqliteFile => Path.Combine(DataRoot, "bytebench-src.db");
    public string KeysRoot => Path.Combine(DataRoot, "keys");

    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["ConnectionStrings:Default"] = $"Data Source={SqliteFile}"; // a different file than the default keeps the fixture on SQLite
        settings["DataProtection:KeysPath"] = KeysRoot;
    }
}

/// <summary>The app running on a migrated PostgreSQL database, with the source's Data Protection keys.</summary>
public sealed class MigratedApp(string connectionString, string keysPath) : TestApp
{
    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["Database:Provider"] = "Postgres";
        settings["ConnectionStrings:Default"] = connectionString;
        settings["DataProtection:KeysPath"] = keysPath;
    }
}

public class MigrateDbUnitTests(SqliteSourceApp app) : IClassFixture<SqliteSourceApp>
{
    // ---------------------------------------------------------------- ordering

    private static TableEdge Edge(string from, string to, bool nullable = false) => new(from, to, nullable, $"{from}->{to}");

    [Fact]
    public void Tables_come_after_the_tables_they_reference()
    {
        var (order, deferred) = TableOrdering.Sort(["Comments", "Issues", "Projects", "Users"],
            [Edge("Comments", "Issues"), Edge("Issues", "Projects"), Edge("Issues", "Users"), Edge("Projects", "Users")]);
        Assert.Empty(deferred);
        Assert.Equal(["Users", "Projects", "Issues", "Comments"], order);
    }

    [Fact]
    public void A_nullable_self_reference_is_deferred()
    {
        var (order, deferred) = TableOrdering.Sort(["Issues", "Users"], [Edge("Issues", "Issues", nullable: true), Edge("Issues", "Users")]);
        Assert.Equal(["Users", "Issues"], order);
        Assert.Equal("Issues->Issues", Assert.Single(deferred).Name);
    }

    [Fact]
    public void A_cycle_is_broken_at_a_nullable_foreign_key_and_never_at_a_required_one()
    {
        var (order, deferred) = TableOrdering.Sort(["Boards", "Projects"], [Edge("Projects", "Boards", nullable: true), Edge("Boards", "Projects")]);
        Assert.Equal(["Projects", "Boards"], order); // Boards needs a project; the project's board pointer is filled in later
        Assert.Equal("Projects->Boards", Assert.Single(deferred).Name);

        var ex = Assert.Throws<MigrationException>(() => TableOrdering.Sort(["A", "B"], [Edge("A", "B"), Edge("B", "A")]));
        Assert.Contains("required foreign keys", ex.Message);
    }

    [Fact]
    public void The_plan_covers_every_table_of_the_model_in_dependency_order()
    {
        var plan = MigrationPlan.Build();
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=:memory:").Options);
        var expected = db.Model.GetEntityTypes().Select(e => e.GetTableName()).Where(n => n != null).Distinct().OrderBy(n => n).ToArray();
        Assert.Equal(expected, plan.Tables.Select(t => t.Name).OrderBy(n => n).ToArray());
        Assert.Contains(plan.Tables, t => t.Name == "IssueComponents"); // the many-to-many join table is not a DbSet

        var position = plan.Tables.Select((t, i) => (t.Name, i)).ToDictionary(x => x.Name, x => x.i);
        foreach (var t in plan.Tables)
            foreach (var fk in t.ForeignKeys.Where(f => !f.Deferred && f.PrincipalTable != t.Name))
                Assert.True(position[fk.PrincipalTable] < position[t.Name], $"{t.Name} is copied before {fk.PrincipalTable}, which it references");
        foreach (var fk in plan.Tables.SelectMany(t => t.Deferred)) Assert.True(fk.Nullable);
        Assert.Contains(plan.Tables.Single(t => t.Name == "Issues").Deferred, f => f.PrincipalTable == "Issues"); // sub-tasks: ParentId
    }

    // ---------------------------------------------------------------- conversion

    [Fact]
    public void SQLite_values_are_converted_to_the_exact_types_postgres_needs()
    {
        // integers, booleans (stored as 0/1), enums (as numbers)
        Assert.Equal(5, ValueConversion.FromSqlite(5L, typeof(int)));
        Assert.Equal(true, ValueConversion.FromSqlite(1L, typeof(bool)));
        Assert.Equal(false, ValueConversion.FromSqlite(0L, typeof(bool)));
        Assert.Equal((IssueStatus)1, ValueConversion.FromSqlite(1L, typeof(IssueStatus)));
        Assert.Null(ValueConversion.FromSqlite(DBNull.Value, typeof(int)));
        Assert.Equal(3, ValueConversion.ToPostgres(3L, NpgsqlDbType.Integer));
        Assert.Equal((short)3, ValueConversion.ToPostgres(3, NpgsqlDbType.Smallint));
        Assert.Throws<OverflowException>(() => ValueConversion.ToPostgres(long.MaxValue, NpgsqlDbType.Integer));

        // text keeps unicode and length; a NUL is refused instead of being dropped
        var text = new string('x', 300_000) + " é\U0001F600";
        Assert.Equal(text, ValueConversion.ToPostgres(ValueConversion.FromSqlite(text, typeof(string))!, NpgsqlDbType.Text));
        Assert.Contains("NUL", Assert.Throws<MigrationException>(() => ValueConversion.ToPostgres("a\0b", NpgsqlDbType.Text)).Message);

        // byte arrays, guids, decimals, doubles
        Assert.Equal(new byte[] { 1, 2, 3 }, ValueConversion.ToPostgres(ValueConversion.FromSqlite(new byte[] { 1, 2, 3 }, typeof(byte[]))!, NpgsqlDbType.Bytea));
        var guid = Guid.NewGuid();
        Assert.Equal(guid, ValueConversion.FromSqlite(guid.ToString().ToUpperInvariant(), typeof(Guid)));
        Assert.Equal(12.50m, ValueConversion.FromSqlite("12.50", typeof(decimal)));
        Assert.Equal(1.5, ValueConversion.FromSqlite(1.5, typeof(double)));
    }

    [Theory]
    [InlineData("2026-03-04 05:06:07")]
    [InlineData("2026-03-04T05:06:07")]
    [InlineData("2026-03-04T05:06:07.1234567")]
    [InlineData("2026-03-04T05:06:07Z")]
    [InlineData("2026-03-04T05:06:07+00:00")]
    public void Dates_come_out_as_UTC(string stored)
    {
        var d = (DateTime)ValueConversion.ToPostgres(ValueConversion.FromSqlite(stored, typeof(DateTime))!, NpgsqlDbType.TimestampTz);
        Assert.Equal(DateTimeKind.Utc, d.Kind);
        Assert.Equal(new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc).Date, d.Date);
        Assert.Equal(5, d.Hour);
        Assert.Throws<MigrationException>(() => ValueConversion.FromSqlite("not a date", typeof(DateTime)));
    }

    [Fact]
    public void Store_types_map_to_npgsql_types()
    {
        Assert.Equal(NpgsqlDbType.TimestampTz, ValueConversion.PgTypeOf("timestamp with time zone"));
        Assert.Equal(NpgsqlDbType.Varchar, ValueConversion.PgTypeOf("character varying(200)"));
        Assert.Equal(NpgsqlDbType.Numeric, ValueConversion.PgTypeOf("numeric(18,2)"));
        Assert.Equal(NpgsqlDbType.Bytea, ValueConversion.PgTypeOf("bytea"));
        Assert.Throws<MigrationException>(() => ValueConversion.PgTypeOf("geometry"));
    }

    [Fact]
    public void Every_column_of_the_model_has_a_conversion_to_postgres()
    {
        // Building the plan resolves a store type for every column, so a new column type nobody taught migrate-db fails here.
        foreach (var t in MigrationPlan.Build().Tables)
            foreach (var c in t.Columns)
                Assert.NotEqual(default, c.PgType);
        // List<int> and friends are JSON text in SQLite and arrays in PostgreSQL
        var labels = MigrationPlan.Build().Tables.Single(t => t.Name == "Boards").Column("FilterLabelIds");
        Assert.Equal(NpgsqlDbType.Array | NpgsqlDbType.Integer, labels.PgType);
        Assert.Equal(new[] { 1, 2, 3 }, labels.Convert("[1,2,3]"));
        Assert.Equal(Array.Empty<int>(), labels.Convert("[]"));
    }

    [Fact]
    public void Checksums_ignore_row_order_and_postgres_precision_but_see_changes()
    {
        static string Sum(params object?[][] rows)
        {
            var c = new ValueConversion.TableChecksum();
            foreach (var r in rows) c.AddRow(r);
            return c.Hex;
        }
        var t = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        Assert.Equal(Sum([1, "a", t], [2, "b", null]), Sum([2, "b", null], [1, "a", t]));
        Assert.Equal(Sum([1, t]), Sum([1, t.AddTicks(7)])); // below a microsecond
        Assert.Equal(Sum([1, 1.10m]), Sum([1, 1.1m]));
        Assert.NotEqual(Sum([1, "a"]), Sum([1, "b"]));
        Assert.NotEqual(Sum([1, "a"]), Sum([1, "a"], [2, "x"]));
        Assert.NotEqual(Sum([1, null]), Sum([1, ""]));
    }

    // ---------------------------------------------------------------- CLI and refusals (no PostgreSQL server needed)

    private const string FakeTarget = "Host=127.0.0.1;Port=1;Database=bytebench;Username=u;Password=s3cret-pw";

    private static async Task<(int Code, string Out, string Err)> Cli(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await MigrateDbCli.RunAsync(["migrate-db", .. args], o, e);
        return (code, o.ToString(), e.ToString());
    }

    [Theory]
    [InlineData]
    [InlineData("--from", "x.db")]
    [InlineData("--to", FakeTarget)]
    [InlineData("--from", "x.db", "--to", FakeTarget, "--batch-size", "0")]
    [InlineData("--from", "x.db", "--to", FakeTarget, "--nope")]
    [InlineData("--from", "x.db", "--to", "Data Source=y.db")]
    public async Task Bad_arguments_exit_with_2(params string[] args)
    {
        var (code, _, err) = await Cli(args);
        Assert.Equal(2, code);
        Assert.Contains("migrate-db", err);
    }

    [Fact]
    public async Task Help_exits_0_and_a_missing_source_exits_1()
    {
        Assert.Equal(0, (await Cli("--help")).Code);
        var (code, _, err) = await Cli("--from", Path.Combine(Path.GetTempPath(), "no-such-bytebench.db"), "--to", FakeTarget);
        Assert.Equal(1, code);
        Assert.Contains("does not exist", err);
    }

    [Fact]
    public async Task A_source_behind_the_latest_migration_is_refused_with_advice()
    {
        var file = Path.Combine(app.DataRoot, "old.db");
        await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={file}").Options))
        {
            var all = db.Database.GetMigrations().ToList();
            await db.GetService<IMigrator>().MigrateAsync(all[^2]);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var (code, _, err) = await Cli("--from", file, "--to", FakeTarget);
        Assert.Equal(1, code);
        Assert.Contains("Start this ByteBench version once against the SQLite file first", err);
        Assert.DoesNotContain("s3cret-pw", err);
    }

    [Fact]
    public async Task Dry_run_reads_and_converts_everything_without_contacting_the_target()
    {
        using (app.Db()) { } // the app has migrated and seeded its SQLite file
        var lines = new List<string>();
        var report = await DatabaseMover.RunAsync(new MigrateDbOptions(app.SqliteFile, FakeTarget, DryRun: true, Verify: true, BatchSize: 7), lines.Add);
        Assert.True(report.Success);
        Assert.True(report.DryRun);
        Assert.True(report.TotalRows > 100);
        Assert.Equal(MigrationPlan.Build().Tables.Count, report.Tables.Count);
        Assert.Contains(lines, l => l.Contains("nothing was written"));
        Assert.DoesNotContain(lines, l => l.Contains("s3cret-pw"));

        using var db = app.Db();
        Assert.Equal(db.Issues.Count(), report.Tables.Single(t => t.Table == "Issues").Rows);
        Assert.Equal(db.Users.Count(), report.Tables.Single(t => t.Table == "AspNetUsers").Rows);

        var (code, output, _) = await Cli("--from", app.SqliteFile, "--to", FakeTarget, "--dry-run");
        Assert.Equal(0, code);
        Assert.DoesNotContain("s3cret-pw", output);
    }

    [Fact]
    public async Task An_unreachable_target_fails_with_exit_1_and_without_the_password()
    {
        using (app.Db()) { }
        var (code, output, err) = await Cli("--from", app.SqliteFile, "--to", FakeTarget + ";Timeout=2;Command Timeout=2");
        Assert.Equal(1, code);
        Assert.DoesNotContain("s3cret-pw", output + err);
        Assert.StartsWith("error:", err);
    }

    [Fact]
    public async Task Rows_that_reference_missing_rows_are_reported_before_anything_is_written()
    {
        using (app.Db()) { }
        var copy = Path.Combine(app.DataRoot, "orphan.db");
        await using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={app.SqliteFile};Pooling=False"))
        {
            await c.OpenAsync();
            await using (var vacuum = c.CreateCommand()) { vacuum.CommandText = $"VACUUM INTO '{copy}'"; await vacuum.ExecuteNonQueryAsync(); } // a consistent copy of the live file
        }
        await using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={copy};Pooling=False"))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_keys = OFF; UPDATE Issues SET ParentId = 999999 WHERE Id = (SELECT MIN(Id) FROM Issues)";
            await cmd.ExecuteNonQueryAsync();
        }
        var (code, _, err) = await Cli("--from", copy, "--to", FakeTarget);
        Assert.Equal(1, code);
        Assert.Contains("Issues", err);
        Assert.Contains("missing row", err);
    }
}

public class MigrateDbPostgresTests(SqliteSourceApp app) : IClassFixture<SqliteSourceApp>
{
    private static string Create(out string name)
    {
        name = "gbt_mig_" + Guid.NewGuid().ToString("N");
        using (var c = new Npgsql.NpgsqlConnection(TestApp.PostgresServer))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{name}\"";
            cmd.ExecuteNonQuery();
        }
        return new Npgsql.NpgsqlConnectionStringBuilder(TestApp.PostgresServer) { Database = name, Pooling = true, MaxPoolSize = 10 }.ConnectionString;
    }

    private static void Drop(string name)
    {
        Npgsql.NpgsqlConnection.ClearAllPools();
        using var c = new Npgsql.NpgsqlConnection(TestApp.PostgresServer);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task A_sqlite_database_moves_into_postgres_verifies_and_the_app_runs_on_it()
    {
        if (!TestApp.PostgresServer?.Contains('=') ?? true) return; // needs BYTEBENCH_TEST_POSTGRES

        // ----- representative data on top of the demo seed
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        var parent = await app.CreateIssueAsync(project.Id, "alice", "Parent é\U0001F600", IssueType.Epic);
        var child = await app.CreateIssueAsync(project.Id, "bob", "Child", parentId: parent.Id, storyPoints: 5);
        var longText = string.Concat(Enumerable.Repeat("long ü中\U0001F600 text\n", 20_000)); // ~400 KB
        await using (var s = app.Scope(app.UserId("alice")))
        {
            await s.Get<IssueService>().AddCommentAsync(child.Id, app.UserId("alice"), longText);
            await s.Get<CiVariableService>().SaveAsync(project.Id, null, null, "DEPLOY_KEY", "super-secret-value", false);
            await s.Get<SavedFilterService>().CreateAsync(project.Id, "Mine", "assignee = me", shared: true);
        }
        Webhook hook;
        await using (var s = app.Scope(app.UserId("alice")))
            hook = await s.Get<WebhookService>().CreateAsync(project.Id, "https://example.com/hook", "whsec-plain-secret", ["issue.created"], true);
        await app.CreateTokenAsync("alice", TokenScopes.ReadApi);
        using (var db = app.Db())
        {
            db.Activities.Add(new Activity { IssueId = child.Id, ActorId = app.UserId("bob"), Text = "migrate-db marker", CreatedAt = new DateTime(2026, 3, 4, 5, 6, 7, 123, DateTimeKind.Utc) });
            await db.SaveChangesAsync();
        }

        var cs = Create(out var dbName);
        try
        {
            var lines = new List<string>();
            var options = new MigrateDbOptions(app.SqliteFile, cs, Verify: true, BatchSize: 50);
            var report = await DatabaseMover.RunAsync(options, lines.Add);
            Assert.True(report.Success, string.Join("\n", report.Problems));
            Assert.True(report.VerifyRan);
            Assert.All(report.Tables, t => { Assert.True(t.Verified, t.Table); Assert.Equal(t.Rows, t.TargetRows); });
            Assert.True(report.Tables.Count(t => t.Rows > 0) >= 25, "the seed and the test data reach many tables");
            Assert.DoesNotContain(lines, l => l.Contains(new Npgsql.NpgsqlConnectionStringBuilder(cs).Password ?? "\u0000none"));

            // a second run into the filled database is refused; --force empties ByteBench's tables and loads again
            var ex = await Assert.ThrowsAsync<MigrationException>(() => DatabaseMover.RunAsync(options, _ => { }));
            Assert.Contains("--force", ex.Message);
            var again = await DatabaseMover.RunAsync(options with { Force = true }, _ => { });
            Assert.True(again.Success, string.Join("\n", again.Problems));
            Assert.Equal(report.TotalRows, again.TotalRows);

            // the CLI agrees, and exits 0 on a verified run
            var o = new StringWriter();
            Assert.Equal(1, await MigrateDbCli.RunAsync(["migrate-db", "--from", app.SqliteFile, "--to", cs], o, new StringWriter()));
            Assert.Equal(0, await MigrateDbCli.RunAsync(["migrate-db", "--from", app.SqliteFile, "--to", cs, "--force", "--verify"], o, new StringWriter()));

            // ----- the app on PostgreSQL, with the same keys
            using var pgApp = new MigratedApp(cs, app.KeysRoot);
            long sourceIssues, sourceMax;
            using (var db = app.Db()) { sourceIssues = db.Issues.Count(); sourceMax = db.Issues.Max(i => i.Id); }
            using (var db = pgApp.Db())
            {
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
                Assert.Equal(sourceIssues, db.Issues.Count());
                var movedChild = db.Issues.AsNoTracking().Single(i => i.Id == child.Id);
                Assert.Equal(parent.Id, movedChild.ParentId);
                Assert.Equal(5, movedChild.StoryPoints);
                Assert.Equal("Parent é\U0001F600", db.Issues.Single(i => i.Id == parent.Id).Title);
                Assert.Equal(longText, db.Comments.Single(c => c.IssueId == child.Id).Body);
                var activity = db.Activities.AsNoTracking().Single(a => a.IssueId == child.Id && a.Text == "migrate-db marker");
                Assert.Equal(DateTimeKind.Utc, activity.CreatedAt.Kind);
                Assert.Equal(new DateTime(2026, 3, 4, 5, 6, 7, 123, DateTimeKind.Utc), activity.CreatedAt);
                Assert.Equal(app.Db().Users.Select(u => u.PasswordHash).ToList().OrderBy(h => h, StringComparer.Ordinal), db.Users.Select(u => u.PasswordHash).ToList().OrderBy(h => h, StringComparer.Ordinal));
                Assert.Equal(1, db.SavedFilters.Count(f => f.Name == "Mine"));
            }
            // encrypted secrets are still readable because the key ring is shared
            await using (var s = pgApp.Scope(pgApp.UserId("alice")))
            {
                string stored;
                using (var db = pgApp.Db()) stored = db.Webhooks.Single(h => h.Id == hook.Id).Secret;
                Assert.Equal("whsec-plain-secret", s.Get<WebhookSecrets>().Reveal(stored));
                Assert.Single(await s.Get<CiVariableService>().ListAsync(project.Id, null), v => v.Name == "DEPLOY_KEY");
            }
            // sign in with a migrated password hash, then create rows: sequences continue after the copied ids
            using (await pgApp.CookieClientAsync("alice")) { }
            var created = await pgApp.CreateIssueAsync(project.Id, "alice", "After the move");
            Assert.True(created.Id > sourceMax, $"new id {created.Id} must be above {sourceMax}");
            using (var api = await pgApp.ApiClientAsync("alice")) { Assert.True((await api.GetAsync("/api/v1/projects")).IsSuccessStatusCode); }
        }
        finally { Drop(dbName); }
    }
}
