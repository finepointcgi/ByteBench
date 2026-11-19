using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Hosting.Backup;

/// <summary>
/// Backups from inside the running server: "back up now" on <c>/admin/backups</c> and the schedule (<c>Backup:Enabled</c>).
/// One backup at a time. The last attempt is remembered in <c>backup-status.json</c> in the backup folder, so
/// <c>/admin/system</c> can show it after a restart (and after a CLI backup).
/// </summary>
public sealed class BackupService(IConfiguration config, IWebHostEnvironment env, IOptionsMonitor<BackupOptions> options, ILogger<BackupService> log)
{
    private const string StatusFileName = "backup-status.json";
    private readonly SemaphoreSlim gate = new(1, 1);

    public BackupOptions Options => options.CurrentValue;
    public BackupLayout Layout => BackupLayout.Resolve(config, env.ContentRootPath, Options);
    public bool IsRunning => gate.CurrentCount == 0;
    public string Directory => Layout.BackupDirectory;

    public async Task<BackupResult> RunAsync(string trigger, CancellationToken ct = default)
    {
        if (!await gate.WaitAsync(0, ct)) throw new BackupException("A backup is already running.");
        var layout = Layout;
        try
        {
            var result = await BackupArchive.CreateAsync(layout, Options, null, m => log.LogInformation("Backup: {Message}", m), ct);
            WriteStatus(layout.BackupDirectory, s => { s.LastAttemptUtc = DateTime.UtcNow; s.LastSuccessUtc = s.LastAttemptUtc; s.LastFile = Path.GetFileName(result.Path); s.LastBytes = result.Bytes; s.LastError = null; s.LastTrigger = trigger; });
            Retention.Apply(layout.BackupDirectory, Options.KeepLast, Options.KeepDays, m => log.LogInformation("Backup retention: {Message}", m));
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Backup failed");
            WriteStatus(layout.BackupDirectory, s => { s.LastAttemptUtc = DateTime.UtcNow; s.LastError = ex.Message; s.LastTrigger = trigger; });
            throw;
        }
        finally { gate.Release(); }
    }

    public IReadOnlyList<BackupFileInfo> List() => Retention.List(Directory);

    public BackupStatusFile Status() => ReadStatus(Directory);

    public string? PathOf(string name) => BackupArchive.IsBackupName(name) && File.Exists(Path.Combine(Directory, name)) ? Path.Combine(Directory, name) : null;

    public bool Delete(string name)
    {
        if (PathOf(name) is not { } path) return false;
        File.Delete(path);
        if (File.Exists(path + ".sha256")) File.Delete(path + ".sha256");
        return true;
    }

    public static BackupStatusFile ReadStatus(string dir)
    {
        try { return JsonSerializer.Deserialize<BackupStatusFile>(File.ReadAllText(Path.Combine(dir, StatusFileName))) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public static void WriteStatus(string dir, Action<BackupStatusFile> update)
    {
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            var s = ReadStatus(dir);
            update(s);
            File.WriteAllText(Path.Combine(dir, StatusFileName), JsonSerializer.Serialize(s));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* status is a courtesy */ }
    }

    /// <summary>Old archives out: keep the newest <c>keepLast</c>, and drop the ones older than <c>keepDays</c>. The newest always stays.</summary>
    public static class Retention
    {
        public static IReadOnlyList<BackupFileInfo> List(string dir)
        {
            if (!System.IO.Directory.Exists(dir)) return [];
            return System.IO.Directory.EnumerateFiles(dir).Select(p => new FileInfo(p)).Where(f => BackupArchive.IsBackupName(f.Name))
                .Select(f => new BackupFileInfo(f.Name, f.Length, f.LastWriteTimeUtc)).OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList();
        }

        public static int Apply(string dir, int keepLast, int keepDays, Action<string>? log = null)
        {
            var files = List(dir);
            var cutoff = keepDays > 0 ? DateTime.UtcNow.AddDays(-keepDays) : (DateTime?)null;
            var deleted = 0;
            for (var i = 1; i < files.Count; i++) // index 0 = newest: never deleted
            {
                var f = files[i];
                if ((keepLast > 0 && i >= keepLast) || (cutoff != null && f.CreatedUtc < cutoff))
                {
                    var path = Path.Combine(dir, f.Name);
                    try { File.Delete(path); if (File.Exists(path + ".sha256")) File.Delete(path + ".sha256"); deleted++; log?.Invoke($"deleted {f.Name}"); }
                    catch (IOException) { }
                }
            }
            return deleted;
        }
    }
}

/// <summary>Runs <see cref="BackupService"/> on the <c>Backup:*</c> schedule while the server is up.</summary>
public sealed class BackupScheduler(BackupService backups, IOptionsMonitor<BackupOptions> options, ILogger<BackupScheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.CurrentValue.Enabled) return;
        log.LogInformation("Scheduled backups every {Hours} h from {Start:HH:mm} UTC into {Dir}; keeping the newest {Keep}",
            options.CurrentValue.IntervalHours, options.CurrentValue.StartTime, backups.Directory, options.CurrentValue.KeepLast);
        while (!ct.IsCancellationRequested)
        {
            var wait = NextRun(DateTime.UtcNow, options.CurrentValue) - DateTime.UtcNow;
            try
            {
                while (wait > TimeSpan.Zero) { var step = wait > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : wait; await Task.Delay(step, ct); wait -= step; }
                await backups.RunAsync("scheduled", ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log.LogError(ex, "Scheduled backup failed; trying again at the next slot"); }
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ContinueWith(_ => { }); // never twice in one slot
        }
    }

    /// <summary>The next slot after <paramref name="now"/>: <c>StartTimeUtc</c> plus whole multiples of <c>IntervalHours</c>.</summary>
    public static DateTime NextRun(DateTime now, BackupOptions o)
    {
        var interval = TimeSpan.FromHours(Math.Max(o.IntervalHours, 0.1));
        var anchor = now.Date + o.StartTime.ToTimeSpan();
        var k = Math.Ceiling((now - anchor) / interval);
        var next = anchor + k * interval;
        return next <= now ? next + interval : next;
    }
}
