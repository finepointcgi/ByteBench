namespace GitBench.Services.Hosting.Backup;

/// <summary>
/// <c>dotnet GitBench.dll backup [dir] [--keep N]</c> and <c>dotnet GitBench.dll restore &lt;file&gt; [--force]</c>, handled before the
/// web host starts (Program.cs), with the same configuration the server reads: appsettings, <c>GitBench__*</c>-style environment
/// variables, user secrets. Exit code 0 on success, 1 on a backup/restore problem, 2 on bad arguments.
/// </summary>
public static class BackupCli
{
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] is "backup" or "restore";

    private const string Usage = """
        GitBench backup and restore (run with the same configuration/environment as the server).

          dotnet GitBench.dll backup [<dir>] [--keep <n>]
              Writes gitbench-backup-<UTC time>.tar.gz (+ .sha256) into <dir> (default: Backup:Directory, else ./backups).
              Safe while GitBench runs: the database is copied with SQLite's online backup API (or pg_dump on Postgres).
              --keep <n>   afterwards delete all but the newest <n> archives in <dir>.

          dotnet GitBench.dll restore <file> [--force]
              Verifies the archive (manifest, SHA-256 of every file, version, database provider), then restores database,
              repositories, attachments, packages, LFS, pages and keys. Stop GitBench first. Refuses to overwrite a non-empty
              installation unless --force is given (then the contents of each folder and the database are replaced).
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h")) { Console.WriteLine(Usage); return 0; }
        // Same configuration sources as the server (the builder is only used for them; no host is built).
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = Directory.GetCurrentDirectory() });
        var options = builder.Configuration.GetSection("Backup").Get<BackupOptions>() ?? new BackupOptions();
        BackupLayout layout;
        try { layout = BackupLayout.Resolve(builder.Configuration, builder.Environment.ContentRootPath, options); }
        catch (InvalidOperationException ex) { Console.Error.WriteLine("error: " + ex.Message); return 1; }
        void Log(string m) => Console.WriteLine(m);

        try
        {
            if (args[0] == "backup")
            {
                string? dir = null;
                int? keep = null;
                for (var i = 1; i < args.Length; i++)
                {
                    if (args[i] == "--keep" && i + 1 < args.Length && int.TryParse(args[++i], out var n) && n > 0) keep = n;
                    else if (args[i].StartsWith("--")) { Console.Error.WriteLine($"error: unknown or incomplete option {args[i]}\n\n{Usage}"); return 2; }
                    else dir ??= args[i];
                }
                var target = Path.GetFullPath(dir ?? layout.BackupDirectory);
                // The backup folder is never part of an archive (so it may sit inside a data folder), see BackupArchive.
                var result = await BackupArchive.CreateAsync(layout, options, target, Log, CancellationToken.None);
                BackupService.WriteStatus(target, s => { s.LastAttemptUtc = DateTime.UtcNow; s.LastSuccessUtc = s.LastAttemptUtc; s.LastFile = Path.GetFileName(result.Path); s.LastBytes = result.Bytes; s.LastError = null; s.LastTrigger = "cli"; });
                if (keep != null || options.KeepLast > 0 && dir == null)
                    BackupService.Retention.Apply(target, keep ?? options.KeepLast, dir == null ? options.KeepDays : 0, Log);
                Console.WriteLine($"Backup written to {result.Path} ({result.Bytes:N0} bytes, SHA-256 {result.Sha256})");
                return 0;
            }

            var force = args.Contains("--force");
            var file = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
            if (file == null || args.Skip(1).Any(a => a.StartsWith("--") && a != "--force")) { Console.Error.WriteLine(Usage); return 2; }
            var restored = await BackupRestorer.RestoreAsync(Path.GetFullPath(file), layout, options, force, Log, CancellationToken.None);
            Console.WriteLine($"Restored {restored.Files} files ({restored.Bytes:N0} bytes) from a GitBench {restored.Manifest.AppVersion} backup of {restored.Manifest.CreatedAtUtc:u}. Start GitBench to apply any newer migrations.");
            return 0;
        }
        catch (BackupException ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }
}
