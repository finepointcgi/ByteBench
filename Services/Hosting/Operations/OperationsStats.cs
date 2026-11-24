using System.Diagnostics;
using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Hosting.Operations;

/// <summary>Queue and backlog sizes, as of <see cref="At"/>. Names double as the worker page's queue column.</summary>
public sealed record QueueDepths(
    long CiQueued, long CiRunning, long TrackerImportQueued, long TrackerImportRunning, long RepoImportQueued, long RepoImportRunning,
    long MirrorsDue, long WebhookPending, long WebhookFailed24h, long MergeQueueQueued, long MergeQueueActive, long AutoMergeArmed,
    long EmailPending, long ChatPending, long DependencyBotDue, long AiReviewPending, long AiReviewRunning, long TriagePending)
{
    public static readonly QueueDepths Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

public sealed record DatabaseStatus(bool Up, long? SizeBytes, int PendingMigrations);

public sealed record EntityCounts(long Users, long Projects, long Issues, long Repositories);

/// <summary>A value computed at most once per <c>ttl</c>; concurrent callers share one computation. Failures are not cached.</summary>
public sealed class TtlCache<T>(TimeSpan ttl, Func<CancellationToken, Task<T>> load, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private (DateTime At, T Value)? cached;

    public async Task<T> GetAsync(CancellationToken ct = default)
    {
        if (cached is { } c && time.GetUtcNow().UtcDateTime - c.At < ttl) return c.Value;
        await gate.WaitAsync(ct);
        try
        {
            if (cached is { } c2 && time.GetUtcNow().UtcDateTime - c2.At < ttl) return c2.Value;
            var value = await load(ct);
            cached = (time.GetUtcNow().UtcDateTime, value);
            return value;
        }
        finally { gate.Release(); }
    }
}

/// <summary>
/// The numbers behind <c>/metrics</c> and the workers page. Every query is cached (15 s for queues and the database, 60 s for
/// entity counts, 5 min for repository storage) so a scrape storm or a busy admin page cannot hammer the database.
/// </summary>
public sealed class OperationsStats
{
    private readonly IDbContextFactory<AppDbContext> dbFactory;
    private readonly IConfiguration config;
    private readonly RepoStorage repos;
    private readonly TtlCache<QueueDepths> queues;
    private readonly TtlCache<DatabaseStatus> database;
    private readonly TtlCache<EntityCounts> entities;
    private long dbQueryFailures;

    // Repository storage is walked in the background; scrapes only ever read the last result.
    private long repoBytes = -1;
    private DateTime repoBytesAt = DateTime.MinValue;
    private int repoWalking;

    public OperationsStats(IDbContextFactory<AppDbContext> dbFactory, IConfiguration config, RepoStorage repos)
    {
        this.dbFactory = dbFactory;
        this.config = config;
        this.repos = repos;
        queues = new(TimeSpan.FromSeconds(15), LoadQueuesAsync);
        database = new(TimeSpan.FromSeconds(15), LoadDatabaseAsync);
        entities = new(TimeSpan.FromSeconds(60), LoadEntitiesAsync);
    }

    /// <summary>Failures of the queries this class runs (a cheap stand-in for a driver-level counter).</summary>
    public long DbQueryFailures => Interlocked.Read(ref dbQueryFailures);

    public Task<QueueDepths> QueuesAsync(CancellationToken ct = default) => queues.GetAsync(ct);
    public Task<DatabaseStatus> DatabaseAsync(CancellationToken ct = default) => database.GetAsync(ct);
    public Task<EntityCounts> EntitiesAsync(CancellationToken ct = default) => entities.GetAsync(ct);

    private async Task<QueueDepths> LoadQueuesAsync(CancellationToken ct)
    {
        try
        {
            var now = DateTime.UtcNow;
            var day = now.AddHours(-24);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return new QueueDepths(
                await db.Pipelines.LongCountAsync(p => p.Status == PipelineStatus.Queued, ct),
                await db.Pipelines.LongCountAsync(p => p.Status == PipelineStatus.Running, ct),
                await db.TrackerImportJobs.LongCountAsync(j => j.Status == TrackerImportStatus.Queued, ct),
                await db.TrackerImportJobs.LongCountAsync(j => j.Status == TrackerImportStatus.Running, ct),
                await db.RepositoryImports.LongCountAsync(i => i.Status == ImportStatus.Queued, ct),
                await db.RepositoryImports.LongCountAsync(i => i.Status == ImportStatus.Cloning, ct),
                await db.RepositoryMirrors.LongCountAsync(m => m.Direction == MirrorDirection.Pull && m.Enabled && (m.NextSyncAt == null || m.NextSyncAt <= now), ct),
                await db.WebhookDeliveries.LongCountAsync(d => d.Success == null, ct),
                await db.WebhookDeliveries.LongCountAsync(d => d.Success == false && d.CreatedAt > day, ct),
                await db.MergeQueueEntries.LongCountAsync(e => e.State == MergeQueueEntryState.Queued, ct),
                await db.MergeQueueEntries.LongCountAsync(e => e.State == MergeQueueEntryState.Testing || e.State == MergeQueueEntryState.Passed, ct),
                await db.PullRequestAutoMerges.LongCountAsync(ct),
                await db.Notifications.LongCountAsync(n => n.EmailPending, ct),
                await db.Notifications.LongCountAsync(n => n.ChatPending, ct),
                await db.DependencyBotSettings.LongCountAsync(s => s.Enabled && s.NextRunAt != null && s.NextRunAt <= now, ct),
                await db.AiReviews.LongCountAsync(r => r.Status == AiReviewStatus.Queued, ct),
                await db.AiReviews.LongCountAsync(r => r.Status == AiReviewStatus.Running, ct),
                await db.IssueTriageRuns.LongCountAsync(r => r.Status == TriageRunStatus.Queued, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Interlocked.Increment(ref dbQueryFailures); throw; }
    }

    private async Task<DatabaseStatus> LoadDatabaseAsync(CancellationToken ct)
    {
        long? size = null;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            if (!await db.Database.CanConnectAsync(ct)) { Interlocked.Increment(ref dbQueryFailures); return new(false, null, 0); }
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).Count();
            if (DatabaseSetup.GetProvider(config) == DatabaseProvider.Postgres)
                size = await db.Database.SqlQueryRaw<long>("SELECT pg_database_size(current_database()) AS \"Value\"").FirstOrDefaultAsync(ct);
            else if (DatabaseSetup.SqliteFile(config) is { } file)
            {
                long bytes = 0;
                foreach (var f in new[] { file, file + "-wal" }) if (File.Exists(f)) bytes += new FileInfo(f).Length;
                size = bytes;
            }
            return new(true, size, pending);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Interlocked.Increment(ref dbQueryFailures); return new(false, size, 0); }
    }

    private async Task<EntityCounts> LoadEntitiesAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return new(await db.Users.LongCountAsync(ct), await db.Projects.LongCountAsync(ct), await db.Issues.LongCountAsync(ct), await db.Repositories.LongCountAsync(ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Interlocked.Increment(ref dbQueryFailures); throw; }
    }

    /// <summary>
    /// Total size of the repository folder, or null until the first walk has finished. A stale (older than 5 min) value starts one
    /// background walk (bounded to 20 s) and the old value is returned meanwhile, so callers never wait for the disk.
    /// </summary>
    public long? RepositoryBytes()
    {
        if (DateTime.UtcNow - repoBytesAt > TimeSpan.FromMinutes(5) && Interlocked.Exchange(ref repoWalking, 1) == 0)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    long total = 0;
                    var sw = Stopwatch.StartNew();
                    var root = repos.Root;
                    if (Directory.Exists(root))
                        foreach (var f in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                        {
                            try { total += new FileInfo(f).Length; } catch (IOException) { }
                            if (sw.Elapsed > TimeSpan.FromSeconds(20)) break;
                        }
                    Interlocked.Exchange(ref repoBytes, total);
                }
                catch (Exception) { /* keep the previous value */ }
                finally { repoBytesAt = DateTime.UtcNow; Interlocked.Exchange(ref repoWalking, 0); }
            });
        }
        var v = Interlocked.Read(ref repoBytes);
        return v < 0 ? null : v;
    }

    /// <summary>Free bytes on the volume holding the repositories, or null when it cannot be read.</summary>
    public long? DiskFreeBytes()
    {
        try { return new DriveInfo(Path.GetFullPath(repos.Root)).AvailableFreeSpace; }
        catch (Exception) { return null; }
    }
}
