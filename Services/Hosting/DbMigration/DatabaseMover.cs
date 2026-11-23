using System.Diagnostics;
using System.Globalization;
using ByteBench.Data;
using ByteBench.Data.Postgres;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ByteBench.Services.Hosting.DbMigration;

public sealed record MigrateDbOptions(string From, string To, bool DryRun = false, bool Verify = false, int BatchSize = 1000, bool Force = false);

public sealed record TableResult(string Table, long Rows, long TargetRows, string? SourceChecksum, string? TargetChecksum, bool? Verified);

public sealed class MoveReport
{
    public bool DryRun { get; init; }
    public List<TableResult> Tables { get; } = [];
    public List<string> Problems { get; } = [];
    public bool VerifyRan { get; set; }
    public long TotalRows => Tables.Sum(t => t.Rows);
    public bool Success => Problems.Count == 0;
}

/// <summary>
/// Copies a ByteBench SQLite database into PostgreSQL. The tables, columns, types and foreign keys all come from the EF model, the
/// data is loaded in one transaction (all or nothing), ids are preserved and every sequence is moved past the highest id.
/// </summary>
public static class DatabaseMover
{
    public static SqliteConnectionStringBuilder ParseSource(string from)
    {
        SqliteConnectionStringBuilder b;
        try { b = from.Contains('=') ? new SqliteConnectionStringBuilder(from) : new SqliteConnectionStringBuilder { DataSource = from }; }
        catch (ArgumentException ex) { throw new MigrationException("--from is neither a SQLite file nor a SQLite connection string: " + ex.Message); }
        if (string.IsNullOrWhiteSpace(b.DataSource) || b.DataSource == ":memory:")
            throw new MigrationException("--from must name a SQLite database file.");
        b.DataSource = Path.GetFullPath(b.DataSource);
        if (!File.Exists(b.DataSource)) throw new MigrationException($"The SQLite file {b.DataSource} does not exist.");
        b.Mode = SqliteOpenMode.ReadOnly; // the source is never written
        b.Pooling = false;
        return b;
    }

    public static NpgsqlConnectionStringBuilder ParseTarget(string to)
    {
        NpgsqlConnectionStringBuilder b;
        try { b = new NpgsqlConnectionStringBuilder(to); }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        { throw new MigrationException("--to must be an Npgsql connection string, e.g. \"Host=db;Database=bytebench;Username=bytebench;Password=...\": " + ex.Message); }
        if (string.IsNullOrWhiteSpace(b.Host) || string.IsNullOrWhiteSpace(b.Database))
            throw new MigrationException("--to needs at least Host and Database, e.g. \"Host=db;Database=bytebench;Username=bytebench;Password=...\".");
        b.CommandTimeout = 0; // loading and verifying a big table may take minutes
        return b;
    }

    /// <summary>Host, port, database and user: never the password.</summary>
    public static string Describe(NpgsqlConnectionStringBuilder b) => $"{b.Host}:{b.Port}/{b.Database}" + (string.IsNullOrEmpty(b.Username) ? "" : " as " + b.Username);

    public static async Task<MoveReport> RunAsync(MigrateDbOptions o, Action<string> log, CancellationToken ct = default)
    {
        if (o.BatchSize < 1) throw new MigrationException("--batch-size must be at least 1.");
        var sourceCs = ParseSource(o.From);
        var target = ParseTarget(o.To);
        var report = new MoveReport { DryRun = o.DryRun };
        var clock = Stopwatch.StartNew();

        var plan = MigrationPlan.Build();
        log($"Source: {sourceCs.DataSource} (SQLite, read-only)");
        log(o.DryRun ? "Target: not contacted (--dry-run)" : $"Target: {Describe(target)}");
        log($"Plan: {plan.Tables.Count} tables, {plan.Tables.Sum(t => t.Columns.Length)} columns, batches of {o.BatchSize:N0} rows" +
            (plan.Tables.Any(t => t.Deferred.Any()) ? $"; {plan.Tables.Sum(t => t.Deferred.Count())} foreign keys loaded in a second pass (" +
                string.Join(", ", plan.Tables.Where(t => t.Deferred.Any()).Select(t => t.Name)) + ")" : ""));

        await using var src = new SqliteConnection(sourceCs.ConnectionString);
        try { await src.OpenAsync(ct); }
        catch (SqliteException ex) { throw new MigrationException($"Cannot open {sourceCs.DataSource}: {ex.Message}", ex); }
        await using var srcTx = (SqliteTransaction)await src.BeginTransactionAsync(ct); // one snapshot for the whole run

        CheckSourceMigrations(src, srcTx, log);
        PreflightOrphans(src, srcTx, plan, report);
        if (!report.Success) return report;

        if (o.DryRun)
        {
            foreach (var t in plan.Tables)
            {
                var sw = Stopwatch.StartNew();
                long n = 0;
                foreach (var raw in ReadRows(src, srcTx, t.SelectAll(orderBy: OrderBy(t))))
                { ConvertRow(t, raw); n++; }
                report.Tables.Add(new TableResult(t.Name, n, 0, null, null, null));
                log($"  {t.Display,-44} {n,12:N0} rows  checked ({sw.Elapsed.TotalSeconds:0.0}s)");
            }
            log($"Dry run: {report.TotalRows:N0} rows in {plan.Tables.Count} tables convert cleanly; nothing was written. ({clock.Elapsed.TotalSeconds:0.0}s)");
            if (o.Verify) log("--verify is ignored with --dry-run (there is no target to compare with).");
            return report;
        }

        // ---------------------------------------------------------------- target schema
        log("Creating or upgrading the PostgreSQL schema with the Postgres migrations...");
        string[] targetMigrations;
        try
        {
            await using var ctx = new PostgresAppDbContext(new DbContextOptionsBuilder<PostgresAppDbContext>().UseNpgsql(target.ConnectionString).Options);
            await ctx.Database.MigrateAsync(ct);
            targetMigrations = (await ctx.Database.GetAppliedMigrationsAsync(ct)).ToArray();
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        { throw new MigrationException($"Cannot prepare the target database {Describe(target)}: {Scrub(ex.GetBaseException().Message, target)}", ex); }

        await using var pg = new NpgsqlConnection(target.ConnectionString);
        await pg.OpenAsync(ct);
        var nonEmpty = new List<string>();
        foreach (var t in plan.Tables)
        {
            await using var cmd = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {t.Quoted})", pg);
            if ((bool)(await cmd.ExecuteScalarAsync(ct))!) nonEmpty.Add(t.Display);
        }
        if (nonEmpty.Count > 0 && !o.Force)
            throw new MigrationException($"The target database already contains ByteBench data ({nonEmpty.Count} tables with rows, e.g. {string.Join(", ", nonEmpty.Take(4))}). " +
                "Point --to at an empty database, or pass --force to empty ByteBench's tables first (other tables are left alone).");

        // ---------------------------------------------------------------- copy (one transaction)
        await using (var tx = await pg.BeginTransactionAsync(ct))
        {
            if (nonEmpty.Count > 0)
            {
                log($"--force: emptying {plan.Tables.Count} ByteBench tables in the target...");
                await using var truncate = new NpgsqlCommand($"TRUNCATE TABLE {string.Join(", ", plan.Tables.Select(t => t.Quoted))} RESTART IDENTITY", pg, tx);
                await truncate.ExecuteNonQueryAsync(ct);
            }

            var pending = new Dictionary<string, List<object?[]>>();
            foreach (var t in plan.Tables)
            {
                var sw = Stopwatch.StartNew();
                var (rows, deferredRows) = await CopyTableAsync(t, src, srcTx, pg, tx, o.BatchSize, ct);
                if (deferredRows.Count > 0) pending[t.Name] = deferredRows;
                report.Tables.Add(new TableResult(t.Name, rows, 0, null, null, null));
                log($"  {t.Display,-44} {rows,12:N0} rows  ({sw.Elapsed.TotalSeconds:0.0}s)");
            }

            foreach (var t in plan.Tables.Where(t => pending.ContainsKey(t.Name)))
            {
                await FillDeferredAsync(t, pending[t.Name], pg, tx, o.BatchSize, ct);
                log($"  {t.Display,-44} {pending[t.Name].Count,12:N0} rows  second pass (circular foreign keys)");
            }

            log("Moving identity sequences past the highest id...");
            foreach (var t in plan.Tables)
                foreach (var c in t.Columns.Where(c => c.IsGenerated))
                {
                    await using var find = new NpgsqlCommand("SELECT pg_get_serial_sequence(@t, @c)", pg, tx);
                    find.Parameters.AddWithValue("t", t.Quoted);
                    find.Parameters.AddWithValue("c", c.Name);
                    if (await find.ExecuteScalarAsync(ct) is not string seq) continue;
                    await using var set = new NpgsqlCommand($"SELECT setval(@s::regclass, COALESCE((SELECT MAX({TablePlan.Q(c.Name)}) FROM {t.Quoted}), 0) + 1, false)", pg, tx);
                    set.Parameters.AddWithValue("s", seq);
                    await set.ExecuteScalarAsync(ct);
                }
            await tx.CommitAsync(ct);
        }
        log($"Copied {report.TotalRows:N0} rows in {plan.Tables.Count} tables. Target migration history: {targetMigrations.Length} entries, latest {targetMigrations.LastOrDefault()}. ({clock.Elapsed.TotalSeconds:0.0}s)");

        if (o.Verify)
        {
            log("Verifying (row counts and a per-table checksum of every column)...");
            await VerifyAsync(plan, src, srcTx, target, report, log, ct);
            log(report.Success ? "Verification passed: every table has the same row count and checksum in both databases." : $"Verification FAILED: {report.Problems.Count} problem(s).");
        }
        return report;
    }

    // ---------------------------------------------------------------- source

    private static void CheckSourceMigrations(SqliteConnection src, SqliteTransaction tx, Action<string> log)
    {
        using var sqlite = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=:memory:").Options);
        var expected = sqlite.Database.GetMigrations().ToList();
        var applied = new List<string>();
        try
        {
            using var cmd = src.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\"";
            using var r = cmd.ExecuteReader();
            while (r.Read()) applied.Add(r.GetString(0));
        }
        catch (SqliteException)
        {
            throw new MigrationException("The source has no migration history, so it is not a ByteBench database (or ByteBench never started on it). " +
                "Start the current ByteBench version once against the SQLite file first, so it applies its migrations, then stop it and run migrate-db.");
        }
        var missing = expected.Except(applied).ToList();
        var unknown = applied.Except(expected).ToList();
        if (missing.Count > 0)
            throw new MigrationException($"The source database is at migration {applied.LastOrDefault() ?? "(none)"}, but this ByteBench version is at {expected.Last()} ({missing.Count} newer migration(s)). " +
                "Start this ByteBench version once against the SQLite file first (it applies the migrations on start), stop it, then run migrate-db again.");
        if (unknown.Count > 0)
            throw new MigrationException($"The source database has migrations this ByteBench version does not know ({string.Join(", ", unknown.Take(3))}). It comes from a newer ByteBench: run migrate-db from the same version that last used it.");
        log($"Source migration history: {applied.Count} entries, latest {applied.Last()} (current).");
    }

    private static void PreflightOrphans(SqliteConnection src, SqliteTransaction tx, MigrationPlan plan, MoveReport report)
    {
        foreach (var t in plan.Tables)
            foreach (var fk in t.ForeignKeys)
            {
                var p = plan.Tables.First(x => x.Name == fk.PrincipalTable);
                var childCols = string.Join(" AND ", fk.Columns.Select(c => $"c.{TablePlan.Q(c)} IS NOT NULL"));
                var join = string.Join(" AND ", fk.Columns.Select((c, i) => $"p.{TablePlan.Q(fk.PrincipalColumns[i])} = c.{TablePlan.Q(c)}"));
                using var cmd = src.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"SELECT COUNT(*) FROM {t.Quoted} c WHERE {childCols} AND NOT EXISTS (SELECT 1 FROM {p.Quoted} p WHERE {join})";
                var n = (long)cmd.ExecuteScalar()!;
                if (n > 0)
                    report.Problems.Add($"{t.Display}: {n:N0} row(s) reference a missing row in {p.Display} through {string.Join(", ", fk.Columns)}. PostgreSQL enforces this foreign key; fix or delete those rows in the SQLite file (on a copy), then run again.");
            }
    }

    private static string OrderBy(TablePlan t) => string.Join(", ", t.Key.Select(TablePlan.Q));

    private static IEnumerable<object?[]> ReadRows(SqliteConnection c, SqliteTransaction tx, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var row = new object?[r.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            yield return row;
        }
    }

    /// <summary>Converts one source row to Npgsql-ready values; a failure names the table, column and row.</summary>
    public static object?[] ConvertRow(TablePlan t, object?[] raw)
    {
        var values = new object?[t.Columns.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var col = t.Columns[i];
            try
            {
                values[i] = col.Convert(raw[i]);
                if (values[i] == null && !col.Nullable) throw new MigrationException("the value is NULL but the column does not allow NULL");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var key = string.Join(", ", t.Key.Select(k => $"{k}={Preview(raw[t.Index(k)])}"));
                throw new MigrationException($"{t.Display}.{col.Name} (row {key}): {(ex is MigrationException ? ex.Message : ex.GetType().Name + ": " + ex.Message)}", ex);
            }
        }
        return values;
    }

    private static string Preview(object? v) => v switch { null => "NULL", byte[] b => $"<{b.Length} bytes>", string s when s.Length > 40 => s[..40] + "...", _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" };

    // ---------------------------------------------------------------- copy

    private static async Task<(long Rows, List<object?[]> Deferred)> CopyTableAsync(TablePlan t, SqliteConnection src, SqliteTransaction srcTx,
        NpgsqlConnection pg, NpgsqlTransaction tx, int batchSize, CancellationToken ct)
    {
        var deferredFks = t.Deferred.ToList();
        var deferredCols = deferredFks.SelectMany(f => f.Columns).Distinct().ToArray();
        var deferredIdx = deferredCols.Select(t.Index).ToArray();
        var keyIdx = t.Key.Select(t.Index).ToArray();
        // A self-reference only needs the second pass for a row whose parent has not been written yet (usually few rows).
        var seen = deferredFks.Where(f => f.PrincipalTable == t.Name).ToDictionary(f => f, _ => new HashSet<string>());
        var pending = new List<object?[]>();
        var batch = new List<object?[]>(Math.Min(batchSize, 4096));
        long rows = 0;

        foreach (var raw in ReadRows(src, srcTx, t.SelectAll(orderBy: OrderBy(t))))
        {
            ct.ThrowIfCancellationRequested();
            var values = ConvertRow(t, raw);
            if (deferredFks.Count > 0)
            {
                var original = deferredIdx.Select(i => values[i]).ToArray();
                var ownKeys = seen.Keys.Select(f => KeyText(f.PrincipalColumns.Select(c => values[t.Index(c)]))).ToArray();
                var deferThisRow = false;
                foreach (var f in deferredFks)
                {
                    var idx = f.Columns.Select(t.Index).ToArray();
                    if (idx.All(i => values[i] == null)) continue;
                    if (idx.Any(i => values[i] == null)) continue; // MATCH SIMPLE: a partly NULL key is not enforced
                    if (seen.TryGetValue(f, out var parents) && parents.Contains(KeyText(idx.Select(i => values[i]))))
                        continue; // parent already written
                    deferThisRow = true;
                    foreach (var i in idx) values[i] = null;
                }
                if (deferThisRow)
                    pending.Add([.. keyIdx.Select(i => values[i]), .. original]);
                var k = 0;
                foreach (var parents in seen.Values) parents.Add(ownKeys[k++]);
            }
            batch.Add(values);
            rows++;
            if (batch.Count >= batchSize) { await FlushAsync(t, batch, pg, tx, ct); batch.Clear(); }
        }
        if (batch.Count > 0) await FlushAsync(t, batch, pg, tx, ct);
        return (rows, pending);
    }

    private static string KeyText(IEnumerable<object?> values) =>
        string.Join("\u001f", values.Select(v => v is byte[] b ? Convert.ToBase64String(b) : Convert.ToString(v, CultureInfo.InvariantCulture)));

    private static async Task FlushAsync(TablePlan t, List<object?[]> batch, NpgsqlConnection pg, NpgsqlTransaction tx, CancellationToken ct)
    {
        var sql = $"COPY {t.Quoted} ({string.Join(", ", t.Columns.Select(c => TablePlan.Q(c.Name)))}) FROM STDIN (FORMAT BINARY)";
        await using var w = await pg.BeginBinaryImportAsync(sql, ct);
        foreach (var row in batch)
        {
            await w.StartRowAsync(ct);
            for (var i = 0; i < row.Length; i++)
            {
                if (row[i] == null) await w.WriteNullAsync(ct);
                else await w.WriteAsync(row[i], t.Columns[i].PgType, ct);
            }
        }
        await w.CompleteAsync(ct);
    }

    private static async Task FillDeferredAsync(TablePlan t, List<object?[]> rows, NpgsqlConnection pg, NpgsqlTransaction tx, int batchSize, CancellationToken ct)
    {
        var deferredCols = t.Deferred.SelectMany(f => f.Columns).Distinct().ToArray();
        var set = string.Join(", ", deferredCols.Select((c, i) => $"{TablePlan.Q(c)} = @v{i}"));
        var where = string.Join(" AND ", t.Key.Select((c, i) => $"{TablePlan.Q(c)} = @k{i}"));
        var sql = $"UPDATE {t.Quoted} SET {set} WHERE {where}";
        foreach (var chunk in rows.Chunk(Math.Min(batchSize, 500)))
        {
            await using var batch = new NpgsqlBatch(pg, tx);
            foreach (var row in chunk)
            {
                var cmd = new NpgsqlBatchCommand(sql);
                for (var i = 0; i < t.Key.Length; i++)
                    cmd.Parameters.Add(new NpgsqlParameter($"k{i}", t.Column(t.Key[i]).PgType) { Value = row[i] ?? DBNull.Value });
                for (var i = 0; i < deferredCols.Length; i++)
                    cmd.Parameters.Add(new NpgsqlParameter($"v{i}", t.Column(deferredCols[i]).PgType) { Value = row[t.Key.Length + i] ?? DBNull.Value });
                batch.BatchCommands.Add(cmd);
            }
            await batch.ExecuteNonQueryAsync(ct);
        }
    }

    // ---------------------------------------------------------------- verify

    private static async Task VerifyAsync(MigrationPlan plan, SqliteConnection src, SqliteTransaction srcTx, NpgsqlConnectionStringBuilder target,
        MoveReport report, Action<string> log, CancellationToken ct)
    {
        report.VerifyRan = true;
        await using var pg = new NpgsqlConnection(target.ConnectionString);
        await pg.OpenAsync(ct);
        for (var n = 0; n < plan.Tables.Count; n++)
        {
            var t = plan.Tables[n];
            var s = new ValueConversion.TableChecksum();
            foreach (var raw in ReadRows(src, srcTx, t.SelectAll())) s.AddRow(ConvertRow(t, raw));

            var d = new ValueConversion.TableChecksum();
            await using (var cmd = new NpgsqlCommand(t.SelectAll(), pg))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                {
                    var row = new object?[t.Columns.Length];
                    for (var i = 0; i < row.Length; i++) row[i] = t.Columns[i].FromPostgres(r.IsDBNull(i) ? null : r.GetValue(i));
                    d.AddRow(row);
                }
            await using (var count = new NpgsqlCommand($"SELECT COUNT(*) FROM {t.Quoted}", pg))
                if ((long)(await count.ExecuteScalarAsync(ct))! != d.Rows)
                    report.Problems.Add($"{t.Display}: the table changed while it was being verified.");

            var ok = s.Rows == d.Rows && s.Hex == d.Hex;
            if (s.Rows != d.Rows) report.Problems.Add($"{t.Display}: {s.Rows:N0} rows in the source, {d.Rows:N0} in the target.");
            else if (s.Hex != d.Hex) report.Problems.Add($"{t.Display}: the row counts match ({s.Rows:N0}) but the checksums differ (source {s.Hex[..16]}..., target {d.Hex[..16]}...): some column values changed.");
            report.Tables[n] = report.Tables[n] with { TargetRows = d.Rows, SourceChecksum = s.Hex, TargetChecksum = d.Hex, Verified = ok };
            if (s.Rows > 0 || !ok) log($"  {t.Display,-44} {s.Rows,12:N0} / {d.Rows,-12:N0} {(ok ? "ok" : "MISMATCH")}");
        }

        log($"  ({plan.Tables.Count(t => report.Tables.First(r => r.Table == t.Name).Rows == 0)} empty tables also match)");

        // Sequences: the next generated id must be above every copied id.
        foreach (var t in plan.Tables)
            foreach (var c in t.Columns.Where(c => c.IsGenerated))
            {
                await using var find = new NpgsqlCommand("SELECT pg_get_serial_sequence(@t, @c)", pg);
                find.Parameters.AddWithValue("t", t.Quoted);
                find.Parameters.AddWithValue("c", c.Name);
                if (await find.ExecuteScalarAsync(ct) is not string seq) continue;
                long max;
                await using (var m = new NpgsqlCommand($"SELECT COALESCE(MAX({TablePlan.Q(c.Name)}), 0) FROM {t.Quoted}", pg)) max = Convert.ToInt64(await m.ExecuteScalarAsync(ct));
                await using var q = new NpgsqlCommand($"SELECT last_value, is_called FROM {seq}", pg);
                await using var r = await q.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                var next = r.GetInt64(0) + (r.GetBoolean(1) ? 1 : 0);
                if (next <= max) report.Problems.Add($"{t.Display}.{c.Name}: the sequence would hand out {next}, but ids up to {max} exist.");
            }

        await using (var ctx = new PostgresAppDbContext(new DbContextOptionsBuilder<PostgresAppDbContext>().UseNpgsql(target.ConnectionString).Options))
        {
            var pendingMigrations = (await ctx.Database.GetPendingMigrationsAsync(ct)).ToList();
            if (pendingMigrations.Count > 0) report.Problems.Add($"The target has migrations that are not applied: {string.Join(", ", pendingMigrations)}.");
        }
    }

    /// <summary>Removes the password (and anything that looks like it) from a message before it is printed.</summary>
    public static string Scrub(string message, NpgsqlConnectionStringBuilder b) =>
        string.IsNullOrEmpty(b.Password) ? message : message.Replace(b.Password, "***");
}
