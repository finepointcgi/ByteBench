using System.Diagnostics;
using ByteBench.Services.Hosting.Backup;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Hosting.Operations;

/// <summary>build_info, uptime, memory, GC and thread numbers of this process.</summary>
public sealed class ProcessMetricCollector : IMetricCollector
{
    private static readonly DateTime Started = Process.GetCurrentProcess().StartTime.ToUniversalTime();
    public string Name => "process";

    public Task CollectAsync(PrometheusWriter w, CancellationToken ct)
    {
        w.Declare("bytebench_build_info", "gauge", "ByteBench version (always 1).");
        w.Sample("bytebench_build_info", 1, ("version", BackupManifest.CurrentAppVersion()));
        w.Declare("bytebench_process_start_time_seconds", "gauge", "Start time of the process, Unix seconds.");
        w.Sample("bytebench_process_start_time_seconds", new DateTimeOffset(Started, TimeSpan.Zero).ToUnixTimeSeconds());
        w.Declare("bytebench_process_uptime_seconds", "gauge", "Seconds since the process started.");
        w.Sample("bytebench_process_uptime_seconds", (DateTime.UtcNow - Started).TotalSeconds);
        using var p = Process.GetCurrentProcess();
        w.Declare("bytebench_process_working_set_bytes", "gauge", "Working set memory of the process.");
        w.Sample("bytebench_process_working_set_bytes", p.WorkingSet64);
        w.Declare("bytebench_process_gc_heap_bytes", "gauge", "Managed heap size as of the last GC.");
        w.Sample("bytebench_process_gc_heap_bytes", GC.GetTotalMemory(false));
        w.Declare("bytebench_process_gc_collections_total", "counter", "Garbage collections by generation.");
        for (var g = 0; g <= 2; g++) w.Sample("bytebench_process_gc_collections_total", GC.CollectionCount(g), ("generation", g.ToString()));
        w.Declare("bytebench_process_threads", "gauge", "Thread pool threads in the process.");
        w.Sample("bytebench_process_threads", ThreadPool.ThreadCount);
        return Task.CompletedTask;
    }
}

public sealed class HttpMetricCollector(HttpMetrics http) : IMetricCollector
{
    public string Name => "http";
    public Task CollectAsync(PrometheusWriter w, CancellationToken ct) { http.Write(w); return Task.CompletedTask; }
}

public sealed class DatabaseMetricCollector(OperationsStats stats) : IMetricCollector
{
    public string Name => "database";

    public async Task CollectAsync(PrometheusWriter w, CancellationToken ct)
    {
        var db = await stats.DatabaseAsync(ct);
        w.Declare("bytebench_db_up", "gauge", "1 when the database answers a trivial query.");
        w.Sample("bytebench_db_up", db.Up ? 1 : 0);
        if (db.SizeBytes is { } size)
        {
            w.Declare("bytebench_db_size_bytes", "gauge", "Database size (SQLite file with WAL, or pg_database_size).");
            w.Sample("bytebench_db_size_bytes", size);
        }
        w.Declare("bytebench_db_pending_migrations", "gauge", "Migrations not yet applied.");
        w.Sample("bytebench_db_pending_migrations", db.PendingMigrations);
        w.Declare("bytebench_db_query_failures_total", "counter", "Failures of the queries behind these metrics.");
        w.Sample("bytebench_db_query_failures_total", stats.DbQueryFailures);
    }
}

public sealed class QueueMetricCollector(OperationsStats stats) : IMetricCollector
{
    public string Name => "queues";

    public async Task CollectAsync(PrometheusWriter w, CancellationToken ct)
    {
        var q = await stats.QueuesAsync(ct);
        w.Declare("bytebench_queue_depth", "gauge", "Jobs waiting (state=queued) or in progress (state=running) per queue; mirrors_due, webhook_failed_24h and the like use state=due/failed.");
        void S(string queue, string state, long v) => w.Sample("bytebench_queue_depth", v, ("queue", queue), ("state", state));
        S("ci_pipelines", "queued", q.CiQueued); S("ci_pipelines", "running", q.CiRunning);
        S("tracker_imports", "queued", q.TrackerImportQueued); S("tracker_imports", "running", q.TrackerImportRunning);
        S("repository_imports", "queued", q.RepoImportQueued); S("repository_imports", "running", q.RepoImportRunning);
        S("mirror_syncs", "due", q.MirrorsDue);
        S("webhook_deliveries", "queued", q.WebhookPending); S("webhook_deliveries", "failed", q.WebhookFailed24h);
        S("merge_queue", "queued", q.MergeQueueQueued); S("merge_queue", "running", q.MergeQueueActive);
        S("auto_merge", "armed", q.AutoMergeArmed);
        S("notification_email", "queued", q.EmailPending); S("notification_chat", "queued", q.ChatPending);
        S("dependency_bot", "due", q.DependencyBotDue);
        S("ai_reviews", "queued", q.AiReviewPending); S("ai_reviews", "running", q.AiReviewRunning);
        S("ai_triage", "queued", q.TriagePending);
    }
}

public sealed class WorkerMetricCollector(WorkerRegistry registry) : IMetricCollector
{
    public string Name => "workers";

    public Task CollectAsync(PrometheusWriter w, CancellationToken ct)
    {
        var all = registry.Snapshot();
        w.Declare("bytebench_worker_enabled", "gauge", "1 when the worker is enabled by configuration.");
        foreach (var s in all) w.Sample("bytebench_worker_enabled", s.Enabled ? 1 : 0, ("worker", s.Name));
        w.Declare("bytebench_worker_last_run_timestamp_seconds", "gauge", "Unix time of the worker's last loop iteration.");
        foreach (var s in all.Where(s => s.LastTickUtc != null))
            w.Sample("bytebench_worker_last_run_timestamp_seconds", new DateTimeOffset(s.LastTickUtc!.Value, TimeSpan.Zero).ToUnixTimeSeconds(), ("worker", s.Name));
        w.Declare("bytebench_worker_errors_total", "counter", "Failed iterations of the worker since start.");
        foreach (var s in all) w.Sample("bytebench_worker_errors_total", s.Errors, ("worker", s.Name));
        return Task.CompletedTask;
    }
}

public sealed class EntityMetricCollector(OperationsStats stats) : IMetricCollector
{
    public string Name => "entities";

    public async Task CollectAsync(PrometheusWriter w, CancellationToken ct)
    {
        var e = await stats.EntitiesAsync(ct);
        w.Declare("bytebench_users", "gauge", "Registered users.");
        w.Sample("bytebench_users", e.Users);
        w.Declare("bytebench_projects", "gauge", "Projects.");
        w.Sample("bytebench_projects", e.Projects);
        w.Declare("bytebench_issues", "gauge", "Issues.");
        w.Sample("bytebench_issues", e.Issues);
        w.Declare("bytebench_repositories", "gauge", "Repositories.");
        w.Sample("bytebench_repositories", e.Repositories);
    }
}

public sealed class StorageMetricCollector(OperationsStats stats) : IMetricCollector
{
    public string Name => "storage";

    public Task CollectAsync(PrometheusWriter w, CancellationToken ct)
    {
        if (stats.RepositoryBytes() is { } bytes)
        {
            w.Declare("bytebench_repository_storage_bytes", "gauge", "Size of the repository folder (measured in the background, at most every 5 minutes).");
            w.Sample("bytebench_repository_storage_bytes", bytes);
        }
        if (stats.DiskFreeBytes() is { } free)
        {
            w.Declare("bytebench_disk_free_bytes", "gauge", "Free space on the volume holding the repositories.");
            w.Sample("bytebench_disk_free_bytes", free);
        }
        return Task.CompletedTask;
    }
}

public static class OperationsServiceCollectionExtensions
{
    /// <summary>Health probes, <c>/metrics</c> (<c>Metrics:*</c>, off by default) and the worker registry.</summary>
    public static IServiceCollection AddByteBenchOperations(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<MetricsOptions>(config.GetSection("Metrics"));
        services.AddSingleton(WorkerRegistry.Instance);
        services.AddSingleton<OperationsStats>();
        services.AddSingleton<ReadinessProbe>();
        services.AddSingleton<HttpMetrics>();
        services.AddSingleton<MetricsScrape>();
        services.AddSingleton<IMetricCollector, ProcessMetricCollector>();
        services.AddSingleton<IMetricCollector, HttpMetricCollector>();
        services.AddSingleton<IMetricCollector, DatabaseMetricCollector>();
        services.AddSingleton<IMetricCollector, QueueMetricCollector>();
        services.AddSingleton<IMetricCollector, WorkerMetricCollector>();
        services.AddSingleton<IMetricCollector, EntityMetricCollector>();
        services.AddSingleton<IMetricCollector, StorageMetricCollector>();
        return services;
    }

    /// <summary>Counts requests by route group; place right after the forwarded-headers middleware.</summary>
    public static WebApplication UseByteBenchMetrics(this WebApplication app)
    {
        app.UseMiddleware<HttpMetricsMiddleware>();
        return app;
    }

    public static WebApplication MapByteBenchOperations(this WebApplication app)
    {
        app.MapByteBenchProbes();
        app.MapByteBenchMetrics();
        return app;
    }
}
