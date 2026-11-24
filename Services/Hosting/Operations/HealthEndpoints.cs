using System.Text.Json;
using ByteBench.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Hosting.Operations;

public sealed record ReadinessCheck(string Name, bool Ok);

/// <summary>
/// Readiness for load balancers and orchestrators: database reachable with a trivial query, migrations applied, repository
/// root present and writable, enough free disk (<c>Health:MinFreeDiskMb</c>, default 200) and data-protection keys usable.
/// Never throws; a failing check is reported by name only (no exception text, paths or connection strings).
/// </summary>
public sealed class ReadinessProbe(IDbContextFactory<AppDbContext> dbFactory, RepoStorage repos, IDataProtectionProvider protection, IConfiguration config, ILogger<ReadinessProbe> log)
{
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);

    public async Task<IReadOnlyList<ReadinessCheck>> RunAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Budget);
        var (database, migrations) = await DatabaseAsync(cts.Token);
        return
        [
            new("database", database),
            new("migrations", migrations),
            new("storage", Guard("storage", StorageWritable)),
            new("disk", Guard("disk", DiskOk)),
            new("keys", Guard("keys", KeysLoadable)),
        ];
    }

    private async Task<(bool Database, bool Migrations)> DatabaseAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            if (!await db.Database.CanConnectAsync(ct)) return (false, false);
            await db.Database.ExecuteSqlRawAsync("SELECT 1", ct);
            var pending = await db.Database.GetPendingMigrationsAsync(ct);
            return (true, !pending.Any());
        }
        catch (Exception ex)
        {
            log.LogWarning("Readiness: database check failed ({Type})", ex.GetType().Name);
            return (false, false);
        }
    }

    private bool Guard(string name, Func<bool> check)
    {
        try { return check(); }
        catch (Exception ex) { log.LogWarning("Readiness: {Check} check failed ({Type})", name, ex.GetType().Name); return false; }
    }

    private bool StorageWritable()
    {
        Directory.CreateDirectory(repos.Root);
        var probe = Path.Combine(repos.Root, $".ready-{Guid.NewGuid():N}");
        File.WriteAllText(probe, "ok");
        File.Delete(probe);
        return true;
    }

    private bool DiskOk()
    {
        var minMb = config.GetValue("Health:MinFreeDiskMb", 200L);
        if (minMb <= 0) return true;
        DriveInfo drive;
        try { drive = new DriveInfo(Path.GetFullPath(repos.Root)); }
        catch (Exception) { return true; } // volume not measurable on this platform: do not fail readiness for it
        return drive.AvailableFreeSpace >= minMb * 1024 * 1024;
    }

    private bool KeysLoadable()
    {
        var protector = protection.CreateProtector("ByteBench.Readiness");
        return protector.Unprotect(protector.Protect("ok")) == "ok";
    }
}

public static class HealthEndpoints
{
    /// <summary>The paths probes call: exempt from HTTPS redirection and the plain-HTTP cookie warning.</summary>
    public static bool IsProbePath(PathString path) =>
        path.StartsWithSegments("/healthz") || path.StartsWithSegments("/readyz") || path.StartsWithSegments("/health");

    public static IEndpointRouteBuilder MapByteBenchProbes(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/healthz", ["GET", "HEAD"], (HttpContext ctx) =>
        {
            Prepare(ctx, "text/plain; charset=utf-8");
            return ctx.Request.Method == "HEAD" ? Task.CompletedTask : ctx.Response.WriteAsync("ok");
        }).AllowAnonymous().DisableRateLimiting().ExcludeFromDescription();

        app.MapMethods("/readyz", ["GET", "HEAD"], async (HttpContext ctx, ReadinessProbe probe) =>
        {
            IReadOnlyList<ReadinessCheck> checks;
            try { checks = await probe.RunAsync(ctx.RequestAborted); }
            catch (Exception) { checks = [new("probe", false)]; }
            var failed = checks.Where(c => !c.Ok).Select(c => c.Name).ToArray();
            Prepare(ctx, "application/json; charset=utf-8");
            ctx.Response.StatusCode = failed.Length == 0 ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;
            if (ctx.Request.Method == "HEAD") return;
            var body = new
            {
                status = failed.Length == 0 ? "ok" : "unavailable",
                checks = checks.Select(c => new { name = c.Name, status = c.Ok ? "ok" : "failed" }),
            };
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }).AllowAnonymous().DisableRateLimiting().ExcludeFromDescription();
        return app;
    }

    private static void Prepare(HttpContext ctx, string contentType)
    {
        if (ctx.Features.Get<IStatusCodePagesFeature>() is { } pages) pages.Enabled = false; // a 503 stays a bare 503
        ctx.Response.ContentType = contentType;
        ctx.Response.Headers.CacheControl = "no-store";
    }
}
