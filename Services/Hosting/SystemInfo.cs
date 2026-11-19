using System.Diagnostics;
using System.Runtime.InteropServices;
using GitBench.Data;
using GitBench.Services.Hosting.Backup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace GitBench.Services.Hosting;

public sealed record StorageEntry(string Name, string Path, bool Exists, long Files, long Bytes, bool Truncated);

public sealed record QueueState(string Name, string Description, long Waiting, long Active, long Failed24h);

public sealed record DatabaseInfo(string Provider, string Target, string? ServerVersion, long? SizeBytes, int Applied, int Pending, string? LatestMigration);

public sealed record HealthLine(string Name, string Status, string? Description);

public sealed record SystemSnapshot(
    string Version, string Runtime, string Os, string Environment, DateTime StartedUtc, long WorkingSetBytes,
    DatabaseInfo Database, IReadOnlyList<StorageEntry> Storage, IReadOnlyList<QueueState> Queues,
    IReadOnlyList<HealthLine> Health, IReadOnlyList<HostingCheck> Checks, BackupSummary Backups);

public sealed record BackupSummary(bool ScheduleEnabled, string Directory, int Count, long TotalBytes, BackupStatusFile Last, BackupFileInfo? Newest);

/// <summary>
/// Disk usage of the storage folders, measured on demand and cached for a minute (a big repository folder takes a while to walk).
/// </summary>
public sealed class StorageUsage(IConfiguration config, IWebHostEnvironment env, Microsoft.Extensions.Options.IOptionsMonitor<BackupOptions> options)
{
    private (DateTime At, IReadOnlyList<StorageEntry> Entries)? cache;
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<IReadOnlyList<StorageEntry>> GetAsync(bool refresh = false, CancellationToken ct = default)
    {
        if (!refresh && cache is { } c && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(1)) return c.Entries;
        await gate.WaitAsync(ct);
        try
        {
            if (!refresh && cache is { } c2 && DateTime.UtcNow - c2.At < TimeSpan.FromMinutes(1)) return c2.Entries;
            var layout = BackupLayout.Resolve(config, env.ContentRootPath, new BackupOptions { IncludeKeys = true, IncludeCiArtifacts = true, Directory = options.CurrentValue.Directory });
            var entries = await Task.Run(() =>
            {
                var list = layout.Sections.Select(s => Measure(s.Name, s.Path, ct)).ToList();
                if (layout.SqliteFile is { } db)
                {
                    long bytes = 0;
                    foreach (var f in new[] { db, db + "-wal", db + "-shm" }) if (File.Exists(f)) bytes += new FileInfo(f).Length;
                    list.Insert(0, new StorageEntry("database", db, File.Exists(db), File.Exists(db) ? 1 : 0, bytes, false));
                }
                list.Add(Measure("backups", layout.BackupDirectory, ct));
                return (IReadOnlyList<StorageEntry>)list;
            }, ct);
            cache = (DateTime.UtcNow, entries);
            return entries;
        }
        finally { gate.Release(); }
    }

    private static StorageEntry Measure(string name, string path, CancellationToken ct)
    {
        if (!Directory.Exists(path)) return new(name, path, false, 0, 0, false);
        long files = 0, bytes = 0;
        var deadline = Stopwatch.StartNew();
        var truncated = false;
        foreach (var f in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            ct.ThrowIfCancellationRequested();
            try { bytes += new FileInfo(f).Length; files++; } catch (IOException) { }
            if (deadline.Elapsed > TimeSpan.FromSeconds(20)) { truncated = true; break; }
        }
        return new(name, path, true, files, bytes, truncated);
    }
}

/// <summary>Everything <c>/admin/system</c> shows.</summary>
public sealed class SystemInfoService(IDbContextFactory<AppDbContext> dbFactory, IConfiguration config, IWebHostEnvironment env, HealthCheckService health,
    BackupService backups, StorageUsage storage)
{
    private static readonly DateTime Started = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    public async Task<SystemSnapshot> GetAsync(bool refreshStorage = false, CancellationToken ct = default)
    {
        var report = await health.CheckHealthAsync(r => r.Tags.Contains("ready"), ct);
        var healthLines = report.Entries.Select(e => new HealthLine(e.Key, e.Value.Status.ToString(), e.Value.Description)).OrderBy(h => h.Name).ToList();

        var list = backups.List();
        var summary = new BackupSummary(backups.Options.Enabled, backups.Directory, list.Count, list.Sum(f => f.Bytes), backups.Status(), list.FirstOrDefault());
        return new SystemSnapshot(
            BackupManifest.CurrentAppVersion(), RuntimeInformation.FrameworkDescription, $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
            env.EnvironmentName, Started, Process.GetCurrentProcess().WorkingSet64,
            await DatabaseAsync(ct), await storage.GetAsync(refreshStorage, ct), await QueuesAsync(ct), healthLines,
            ProductionChecks.Evaluate(config, env), summary);
    }

    private async Task<DatabaseInfo> DatabaseAsync(CancellationToken ct)
    {
        var provider = DatabaseSetup.GetProvider(config);
        var cs = DatabaseSetup.GetConnectionString(config);
        string target;
        if (provider == DatabaseProvider.Postgres)
        {
            var b = new NpgsqlConnectionStringBuilder(cs);
            target = $"{b.Host}:{b.Port}/{b.Database} as {b.Username}"; // never the password
        }
        else target = DatabaseSetup.SqliteFile(config) ?? "(in memory)";

        string? version = null, latest = null;
        long? size = null;
        int applied = 0, pending = 0;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var appliedList = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
            applied = appliedList.Count;
            pending = (await db.Database.GetPendingMigrationsAsync(ct)).Count();
            latest = appliedList.LastOrDefault();
            if (provider == DatabaseProvider.Sqlite)
            {
                version = await db.Database.SqlQueryRaw<string>("SELECT sqlite_version() AS \"Value\"").FirstOrDefaultAsync(ct);
                if (DatabaseSetup.SqliteFile(config) is { } f && File.Exists(f)) size = new FileInfo(f).Length;
            }
            else
            {
                version = await db.Database.SqlQueryRaw<string>("SELECT current_setting('server_version') AS \"Value\"").FirstOrDefaultAsync(ct);
                size = await db.Database.SqlQueryRaw<long>("SELECT pg_database_size(current_database()) AS \"Value\"").FirstOrDefaultAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { version = "unavailable: " + ex.Message; }
        return new(provider.ToString(), target, version, size, applied, pending, latest);
    }

    private async Task<IReadOnlyList<QueueState>> QueuesAsync(CancellationToken ct)
    {
        var day = DateTime.UtcNow.AddHours(-24);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return
        [
            new("Webhook deliveries", "Sent to your endpoints; failed ones can be redelivered from the webhook's page.",
                await db.WebhookDeliveries.LongCountAsync(d => d.Success == null, ct), 0,
                await db.WebhookDeliveries.LongCountAsync(d => d.Success == false && d.CreatedAt > day, ct)),
            new("CI pipelines", "Queued waits for a free runner slot (Ci:MaxConcurrentJobs).",
                await db.Pipelines.LongCountAsync(p => p.Status == PipelineStatus.Queued, ct),
                await db.Pipelines.LongCountAsync(p => p.Status == PipelineStatus.Running, ct),
                await db.Pipelines.LongCountAsync(p => (p.Status == PipelineStatus.Failed || p.Status == PipelineStatus.Error) && p.CreatedAt > day, ct)),
            new("Merge queue", "Pull requests waiting for or in their merge test.",
                await db.MergeQueueEntries.LongCountAsync(e => e.State == MergeQueueEntryState.Queued, ct),
                await db.MergeQueueEntries.LongCountAsync(e => e.State == MergeQueueEntryState.Testing || e.State == MergeQueueEntryState.Passed, ct),
                await db.MergeQueueEntries.LongCountAsync(e => e.State == MergeQueueEntryState.Failed && e.UpdatedAt > day, ct)),
            new("E-mail notifications", "Waiting to be sent or batched into a digest.",
                await db.Notifications.LongCountAsync(n => n.EmailPending, ct), 0, 0),
            new("Chat notifications", "Slack and Teams messages waiting to be sent.",
                await db.Notifications.LongCountAsync(n => n.ChatPending, ct), 0, 0),
        ];
    }
}
