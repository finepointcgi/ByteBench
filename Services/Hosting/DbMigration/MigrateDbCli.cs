namespace ByteBench.Services.Hosting.DbMigration;

/// <summary>
/// <c>dotnet ByteBench.dll migrate-db --from &lt;sqlite&gt; --to &lt;npgsql&gt; [--dry-run] [--verify] [--batch-size N] [--force]</c>, handled
/// before the web host starts (Program.cs). Exit code 0 on success, 1 when the move is refused, fails or verification finds a
/// difference, 2 on bad arguments. Connection-string passwords are never printed.
/// </summary>
public static class MigrateDbCli
{
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == "migrate-db";

    public const string Usage = """
        ByteBench: move an existing SQLite database into PostgreSQL.

          dotnet ByteBench.dll migrate-db --from "<sqlite file or connection string>" --to "<Npgsql connection string>"
                                          [--dry-run] [--verify] [--batch-size N] [--force]

          --from        the SQLite file (bytebench.db) or a connection string ("Data Source=/data/db/bytebench.db"). Opened read-only.
                        It must already be at this version's latest migration: start the new ByteBench once against it first.
          --to          e.g. "Host=db;Database=bytebench;Username=bytebench;Password=..." The schema is created or upgraded with the
                        PostgreSQL migrations (the role needs CREATE DATABASE if the database does not exist yet).
          --dry-run     read the source and check every value converts; write nothing and do not contact the target.
          --verify      afterwards compare row counts and a checksum of every column per table; exit 1 on any difference.
          --batch-size  rows per bulk insert (default 1000).
          --force       the target may already hold ByteBench data: empty ByteBench's tables first (other tables are not touched).

        Stop ByteBench before running it. Only the database moves; repositories, attachments, packages, LFS, pages and the
        Data Protection keys stay on disk and are used as they are. Exit code: 0 ok, 1 refused or failed, 2 bad arguments.
        """;

    public static Task<int> RunAsync(string[] args) => RunAsync(args, Console.Out, Console.Error);

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Contains("--help") || args.Contains("-h")) { output.WriteLine(Usage); return 0; }
        var (options, problem) = Parse(args);
        if (options == null) { error.WriteLine($"error: {problem}\n\n{Usage}"); return 2; }

        string Safe(string s) => Redact(s, options.To);
        try
        {
            var report = await DatabaseMover.RunAsync(options, m => output.WriteLine(Safe(m)));
            if (report.Success) return 0;
            foreach (var p in report.Problems) error.WriteLine("error: " + Safe(p));
            return 1;
        }
        catch (MigrationException ex) { error.WriteLine("error: " + Safe(ex.Message)); return 1; }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException)
        {
            error.WriteLine("error: " + Safe(ex.GetBaseException().Message));
            return 1;
        }
    }

    public static (MigrateDbOptions? Options, string? Problem) Parse(string[] args)
    {
        string? from = null, to = null;
        bool dry = false, verify = false, force = false;
        var batch = 1000;
        for (var i = 1; i < args.Length; i++)
        {
            string Value(string name) => i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : throw new ArgumentException($"{name} needs a value");
            try
            {
                switch (args[i])
                {
                    case "--from": from = Value("--from"); break;
                    case "--to": to = Value("--to"); break;
                    case "--dry-run": dry = true; break;
                    case "--verify": verify = true; break;
                    case "--force": force = true; break;
                    case "--batch-size":
                        if (!int.TryParse(Value("--batch-size"), out batch) || batch < 1) return (null, "--batch-size must be a positive number");
                        break;
                    default: return (null, $"unknown option {args[i]}");
                }
            }
            catch (ArgumentException ex) { return (null, ex.Message); }
        }
        if (string.IsNullOrWhiteSpace(from)) return (null, "--from is required");
        if (string.IsNullOrWhiteSpace(to)) return (null, "--to is required");
        if (to.TrimStart().StartsWith("Data Source", StringComparison.OrdinalIgnoreCase)) return (null, "--to must be an Npgsql connection string (Host=...;Database=...), not a SQLite one");
        if (!from.Contains('=') && from.Contains("Host", StringComparison.OrdinalIgnoreCase) && from.Contains(';')) return (null, "--from must be SQLite; to read PostgreSQL there is nothing to migrate");
        return (new MigrateDbOptions(from, to, dry, verify, batch, force), null);
    }

    /// <summary>Removes the target's password from a message (a driver message could echo the connection string).</summary>
    public static string Redact(string message, string connectionString)
    {
        try
        {
            var password = new Npgsql.NpgsqlConnectionStringBuilder(connectionString).Password;
            return string.IsNullOrEmpty(password) ? message : message.Replace(password, "***");
        }
        catch (ArgumentException) { return message; }
    }
}
