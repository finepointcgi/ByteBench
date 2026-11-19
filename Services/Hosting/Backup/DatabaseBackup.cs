using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace GitBench.Services.Hosting.Backup;

/// <summary>
/// Dumping and restoring the database itself. SQLite: the online backup API (a transactionally consistent copy while the server
/// runs, no -wal/-shm files). Postgres: <c>pg_dump</c> / <c>pg_restore</c> custom format through pipes, credentials only in the
/// child's environment (never on a command line).
/// </summary>
public static class DatabaseBackup
{
    public const string SqliteEntry = "db/gitbench.db";
    public const string PostgresEntry = "db/postgres.dump";

    public static string EntryFor(DatabaseProvider p) => p == DatabaseProvider.Sqlite ? SqliteEntry : PostgresEntry;

    /// <summary>Writes a consistent copy of the database into <paramref name="targetFile"/> and returns the newest migration id in it.</summary>
    public static async Task<string> DumpAsync(BackupLayout layout, BackupOptions options, string targetFile, CancellationToken ct)
    {
        return layout.Provider == DatabaseProvider.Sqlite
            ? DumpSqlite(layout, targetFile)
            : await DumpPostgresAsync(layout, options, targetFile, ct);
    }

    // ---------------------------------------------------------------- SQLite

    private static SqliteConnection OpenSqlite(string file, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = mode, Pooling = false }.ConnectionString);
        c.Open();
        return c;
    }

    private static string DumpSqlite(BackupLayout layout, string targetFile)
    {
        if (layout.SqliteFile is not { } source) throw new BackupException("Backups need a SQLite database file; this configuration uses an in-memory database.");
        if (!File.Exists(source)) throw new BackupException($"The database file {source} does not exist.");
        using (var src = OpenSqlite(source, SqliteOpenMode.ReadOnly))
        using (var dst = OpenSqlite(targetFile))
        {
            src.BackupDatabase(dst);
            using var cmd = dst.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=DELETE;"; // one self-contained file: the copy keeps the source's WAL mode otherwise
            cmd.ExecuteNonQuery();
        }
        using var check = OpenSqlite(targetFile, SqliteOpenMode.ReadOnly);
        using (var cmd = check.CreateCommand())
        {
            cmd.CommandText = "PRAGMA integrity_check;";
            var result = cmd.ExecuteScalar()?.ToString();
            if (result != "ok") throw new BackupException("The database copy failed its integrity check: " + result);
        }
        return LatestMigration(check) ?? "";
    }

    private static string? LatestMigration(System.Data.Common.DbConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT MAX(\"MigrationId\") FROM \"__EFMigrationsHistory\"";
        try { return cmd.ExecuteScalar() as string; }
        catch (Exception ex) when (ex is SqliteException or PostgresException) { return null; } // never migrated
    }

    // ---------------------------------------------------------------- Postgres

    private static ProcessStartInfo PgStart(string exe, BackupLayout layout, IEnumerable<string> args, bool stdin, bool stdout)
    {
        var b = new NpgsqlConnectionStringBuilder(layout.ConnectionString);
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = stdin, RedirectStandardOutput = stdout, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (!string.IsNullOrEmpty(b.Host)) psi.Environment["PGHOST"] = b.Host.Split(',')[0];
        if (b.Port != 0) psi.Environment["PGPORT"] = b.Port.ToString();
        if (!string.IsNullOrEmpty(b.Username)) psi.Environment["PGUSER"] = b.Username;
        if (!string.IsNullOrEmpty(b.Password)) psi.Environment["PGPASSWORD"] = b.Password;
        if (!string.IsNullOrEmpty(b.Database)) psi.Environment["PGDATABASE"] = b.Database;
        psi.Environment["PGSSLMODE"] = b.SslMode.ToString().ToLowerInvariant() switch { "verifyca" => "verify-ca", "verifyfull" => "verify-full", var m => m };
        return psi;
    }

    private static Process StartPg(ProcessStartInfo psi)
    {
        try { return Process.Start(psi)!; }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new BackupException($"Could not run {psi.FileName}: {ex.Message}. Install the PostgreSQL client tools (the Docker image has them) " +
                                      "or set Backup:PgDumpPath / Backup:PgRestorePath.", ex);
        }
    }

    private static async Task<string> DumpPostgresAsync(BackupLayout layout, BackupOptions options, string targetFile, CancellationToken ct)
    {
        var psi = PgStart(options.PgDumpPath, layout, ["--format=custom", "--no-owner", "--no-privileges"], stdin: false, stdout: true);
        using var p = StartPg(psi);
        var err = p.StandardError.ReadToEndAsync(ct);
        await using (var file = File.Create(targetFile)) await p.StandardOutput.BaseStream.CopyToAsync(file, ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new BackupException($"pg_dump failed (exit {p.ExitCode}): {(await err).Trim()}");

        await using var conn = new NpgsqlConnection(layout.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(\"MigrationId\") FROM \"__EFMigrationsHistory\"";
        try { return await cmd.ExecuteScalarAsync(ct) as string ?? ""; }
        catch (PostgresException) { return ""; }
    }

    // ---------------------------------------------------------------- Restore

    /// <summary>True when the target database already holds users or projects (a fresh, just-migrated database does not count).</summary>
    public static async Task<bool> HasDataAsync(BackupLayout layout, CancellationToken ct)
    {
        if (layout.Provider == DatabaseProvider.Sqlite)
        {
            if (layout.SqliteFile is not { } file || !File.Exists(file) || new FileInfo(file).Length == 0) return false;
            using var c = OpenSqlite(file, SqliteOpenMode.ReadOnly);
            return AnyRow(c, "AspNetUsers") || AnyRow(c, "Projects");
        }
        try
        {
            await using var c = new NpgsqlConnection(layout.ConnectionString);
            await c.OpenAsync(ct);
            return AnyRow(c, "AspNetUsers") || AnyRow(c, "Projects");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidCatalogName) { return false; } // database doesn't exist yet
    }

    private static bool AnyRow(System.Data.Common.DbConnection c, string table)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT EXISTS (SELECT 1 FROM \"{table}\")";
        try { return Convert.ToBoolean(cmd.ExecuteScalar()); }
        catch (Exception ex) when (ex is SqliteException or PostgresException) { return false; } // table missing: not migrated yet
    }

    /// <summary>The newest migration id this build of GitBench knows for the provider.</summary>
    public static string KnownLatestMigration(DatabaseProvider provider)
    {
        var contextType = provider == DatabaseProvider.Sqlite ? typeof(GitBench.Data.AppDbContext) : typeof(GitBench.Data.Postgres.PostgresAppDbContext);
        return typeof(DatabaseBackup).Assembly.GetTypes()
            .Select(t => (Type: t, Migration: t.GetCustomAttributes(typeof(Microsoft.EntityFrameworkCore.Migrations.MigrationAttribute), false)
                .OfType<Microsoft.EntityFrameworkCore.Migrations.MigrationAttribute>().FirstOrDefault(),
                Context: t.GetCustomAttributes(typeof(Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute), false)
                    .OfType<Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute>().FirstOrDefault()))
            .Where(x => x.Migration != null && x.Context?.ContextType == contextType)
            .Select(x => x.Migration!.Id).OrderBy(id => id, StringComparer.Ordinal).LastOrDefault() ?? "";
    }

    /// <summary>Puts the dumped database in place. The caller has already checked emptiness / --force.</summary>
    public static async Task RestoreAsync(BackupLayout layout, BackupOptions options, string dumpFile, CancellationToken ct)
    {
        if (layout.Provider == DatabaseProvider.Sqlite)
        {
            var target = layout.SqliteFile ?? throw new BackupException("Restoring needs a SQLite database file; this configuration uses an in-memory database.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { target, target + "-wal", target + "-shm", target + "-journal" }) if (File.Exists(f)) File.Delete(f);
            File.Copy(dumpFile, target);
            return;
        }

        await EnsurePostgresDatabaseAsync(layout, ct);
        var psi = PgStart(options.PgRestorePath, layout, ["--clean", "--if-exists", "--no-owner", "--no-privileges", "--exit-on-error", "--dbname=" + new NpgsqlConnectionStringBuilder(layout.ConnectionString).Database], stdin: true, stdout: false);
        using var p = StartPg(psi);
        var err = p.StandardError.ReadToEndAsync(ct);
        await using (var file = File.OpenRead(dumpFile))
        {
            try { await file.CopyToAsync(p.StandardInput.BaseStream, ct); } catch (IOException) { /* pg_restore exited early: its error follows */ }
            try { p.StandardInput.Close(); } catch (IOException) { }
        }
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new BackupException($"pg_restore failed (exit {p.ExitCode}): {(await err).Trim()}");
    }

    private static async Task EnsurePostgresDatabaseAsync(BackupLayout layout, CancellationToken ct)
    {
        var b = new NpgsqlConnectionStringBuilder(layout.ConnectionString);
        try
        {
            await using var c = new NpgsqlConnection(b.ConnectionString);
            await c.OpenAsync(ct);
            return;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidCatalogName) { }
        var name = b.Database!;
        b.Database = "postgres";
        await using var admin = new NpgsqlConnection(b.ConnectionString);
        await admin.OpenAsync(ct);
        await using var cmd = admin.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{name.Replace("\"", "\"\"")}\"";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
