using GitBench.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GitBench.Services.Hosting.Backup;

public static class BackupServiceCollectionExtensions
{
    /// <summary>Backup service (admin page, schedule), storage usage and retention jobs.</summary>
    public static IServiceCollection AddBackups(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<BackupOptions>(config.GetSection("Backup"));
        services.AddSingleton<BackupService>();
        services.AddHostedService<BackupScheduler>();
        services.AddSingleton<StorageUsage>();
        services.AddSingleton<SystemInfoService>();
        services.Configure<RetentionOptions>(config.GetSection("Retention"));
        services.AddHostedService<WebhookDeliveryRetentionService>();
        return services;
    }

    /// <summary><c>/admin/backups/download/{name}</c>: the archive (or its .sha256), for site admins.</summary>
    public static void MapBackupDownloads(this IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/backups/download/{name}", async (string name, HttpContext http, ProjectAccess access, BackupService backups) =>
        {
            if (!int.TryParse(http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var uid)) return Results.Unauthorized();
            if (!await access.IsAdminAsync(uid)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var baseName = name.EndsWith(".sha256", StringComparison.Ordinal) ? name[..^7] : name;
            if (backups.PathOf(baseName) is not { } path) return Results.NotFound();
            var file = name == baseName ? path : path + ".sha256";
            if (!File.Exists(file)) return Results.NotFound();
            return Results.File(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan),
                name == baseName ? "application/gzip" : "text/plain", name);
        }).RequireAuthorization();
    }
}

/// <summary><c>Retention:*</c> for data that would otherwise grow without bound.</summary>
public sealed class RetentionOptions
{
    /// <summary>Webhook delivery history (payloads and responses) is deleted after this many days (0 = keep forever). Default 30.</summary>
    public int WebhookDeliveryDays { get; set; } = 30;
}

/// <summary>Deletes webhook deliveries older than <see cref="RetentionOptions.WebhookDeliveryDays"/>, at startup and then every 6 hours.</summary>
public sealed class WebhookDeliveryRetentionService(IDbContextFactory<AppDbContext> dbFactory, IOptions<RetentionOptions> options,
    ILogger<WebhookDeliveryRetentionService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var days = options.Value.WebhookDeliveryDays;
                if (days > 0)
                {
                    var cutoff = DateTime.UtcNow.AddDays(-days);
                    await using var db = await dbFactory.CreateDbContextAsync(ct);
                    var n = await db.WebhookDeliveries.Where(d => d.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
                    if (n > 0) log.LogInformation("Retention: deleted {Count} webhook deliveries older than {Days} days", n, days);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Webhook delivery retention failed"); }
            try { await Task.Delay(TimeSpan.FromHours(6), ct); }
            catch (OperationCanceledException) { break; }
        }
    }
}
