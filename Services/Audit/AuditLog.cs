using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GitBench.Data;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace GitBench.Services.Audit;

public class AuditOptions
{
    /// <summary>Events older than this many days are deleted once a day. 0 or less keeps events forever.</summary>
    public int RetentionDays { get; set; } = 365;
}

/// <summary>
/// The client's IP address and user agent for a Blazor circuit. Interactive components have no reliable HttpContext,
/// so <see cref="AuditCircuitHandler"/> fills this from the connection's initial request when the circuit opens.
/// </summary>
public sealed class AuditRequestInfo
{
    public bool Captured { get; private set; }
    public string? IpAddress { get; private set; }
    public string? UserAgent { get; private set; }

    public void Capture(HttpContext http)
    {
        (IpAddress, UserAgent) = AuditLog.ClientOf(http);
        Captured = true;
    }
}

/// <summary>Captures IP and user agent into the circuit's <see cref="AuditRequestInfo"/> when the circuit starts.</summary>
public sealed class AuditCircuitHandler(AuditRequestInfo info, IHttpContextAccessor accessor) : CircuitHandler
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (!info.Captured && accessor.HttpContext is { } http) info.Capture(http);
        return Task.CompletedTask;
    }
}

/// <summary>A page of audit events plus the total number matching the filter.</summary>
public sealed record AuditPage(List<AuditEvent> Events, int Total, Dictionary<int, string> ProjectKeys);

public sealed class AuditQuery
{
    /// <summary>Username (exact, case-insensitive), or "system" for events without an actor.</summary>
    public string? Actor { get; set; }
    /// <summary>Matches actions starting with this, e.g. <c>member.</c> or <c>auth.sign_in</c>.</summary>
    public string? ActionPrefix { get; set; }
    public int? ProjectId { get; set; }
    /// <summary>Inclusive UTC lower bound.</summary>
    public DateTime? FromUtc { get; set; }
    /// <summary>Exclusive UTC upper bound.</summary>
    public DateTime? ToUtc { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

/// <summary>
/// Records security-relevant events (<see cref="AuditEvent"/>) and reads them back for the audit pages.
/// <para>
/// The actor is the explicit <c>actorId</c>, else the acting user (<see cref="ProjectAccess"/>), else the signed-in
/// user of the current HTTP request. IP and user agent come from the circuit (<see cref="AuditRequestInfo"/>) or the
/// current HTTP request. Recording never throws: a failure is logged and the caller's operation goes on.
/// </para>
/// <para>
/// <b>Never pass secrets</b> (passwords, tokens or any part of them, webhook secrets) in <c>details</c>; as a second
/// line of defence, keys that look like secrets are dropped before storing.
/// </para>
/// </summary>
public class AuditLog(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, AuditRequestInfo circuit,
    IHttpContextAccessor httpAccessor, ILogger<AuditLog> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task RecordAsync(string action, string? targetType = null, object? targetId = null, int? projectId = null,
        object? details = null, int? actorId = null, bool anonymous = false)
    {
        try
        {
            var http = circuit.Captured ? null : httpAccessor.HttpContext;
            var uid = anonymous ? null : actorId ?? await access.ResolveUserIdAsync() ?? UserIdOf(http?.User);
            var (ip, ua) = circuit.Captured ? (circuit.IpAddress, circuit.UserAgent) : http != null ? ClientOf(http) : (null, null);

            await using var db = await dbFactory.CreateDbContextAsync();
            var name = uid == null ? null : await db.Users.Where(u => u.Id == uid).Select(u => u.UserName).FirstOrDefaultAsync();
            db.AuditEvents.Add(new AuditEvent
            {
                ActorId = uid, ActorName = name, IpAddress = ip, UserAgent = ua, Action = action,
                TargetType = targetType, TargetId = targetId?.ToString(), ProjectId = projectId, Details = Serialize(details),
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Could not record audit event {Action}", action);
        }
    }

    /// <summary>Records the outcome of a password sign-in attempt (never the password).</summary>
    public async Task SignInAttemptAsync(string login, SignInResult result)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var norm = login.ToUpperInvariant();
        var userId = await db.Users.Where(u => u.NormalizedUserName == norm || u.NormalizedEmail == norm).Select(u => (int?)u.Id).FirstOrDefaultAsync();
        if (result.Succeeded) await RecordAsync("auth.sign_in", "user", userId, actorId: userId);
        else if (result.IsLockedOut) await RecordAsync("auth.lockout", "user", userId, details: new { login }, anonymous: true);
        else await RecordAsync("auth.sign_in_failed", userId == null ? null : "user", userId,
            details: new { login, reason = result.IsNotAllowed ? "not_allowed" : "invalid_credentials" }, anonymous: true);
    }

    // ---------- Reading ----------

    /// <summary>
    /// Events matching the query, newest first. Without a project: site admins only. With a project: anyone with
    /// <see cref="Permission.ManageProjectSettings"/> there (Owners and admins).
    /// </summary>
    public async Task<AuditPage> SearchAsync(AuditQuery q, bool all = false)
    {
        await RequireReadAsync(q.ProjectId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var query = db.AuditEvents.AsNoTracking().AsQueryable();
        if (q.ProjectId is { } pid) query = query.Where(e => e.ProjectId == pid);
        if (!string.IsNullOrWhiteSpace(q.Actor))
        {
            var actor = q.Actor.Trim();
            if (actor.Equals("system", StringComparison.OrdinalIgnoreCase)) query = query.Where(e => e.ActorId == null);
            else
            {
                var norm = actor.ToUpperInvariant();
                var ids = await db.Users.Where(u => u.NormalizedUserName == norm).Select(u => u.Id).ToListAsync();
                query = query.Where(e => (e.ActorId != null && ids.Contains(e.ActorId.Value)) || e.ActorName == actor);
            }
        }
        if (!string.IsNullOrWhiteSpace(q.ActionPrefix))
        {
            var prefix = q.ActionPrefix.Trim().ToLowerInvariant();
            query = query.Where(e => e.Action.StartsWith(prefix));
        }
        if (q.FromUtc is { } from) query = query.Where(e => e.CreatedAt >= from);
        if (q.ToUtc is { } to) query = query.Where(e => e.CreatedAt < to);

        var total = await query.CountAsync();
        query = query.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id);
        if (!all)
        {
            var size = Math.Clamp(q.PageSize, 1, 500);
            query = query.Skip((Math.Max(q.Page, 1) - 1) * size).Take(size);
        }
        else query = query.Take(100_000);
        var events = await query.ToListAsync();
        var projectIds = events.Where(e => e.ProjectId != null).Select(e => e.ProjectId!.Value).Distinct().ToList();
        var keys = await db.Projects.Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Key);
        return new AuditPage(events, total, keys);
    }

    /// <summary>Every action name recorded so far (for the filter's suggestions).</summary>
    public async Task<List<string>> ActionNamesAsync(int? projectId = null)
    {
        await RequireReadAsync(projectId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.AuditEvents.AsQueryable();
        if (projectId is { } pid) q = q.Where(e => e.ProjectId == pid);
        return await q.Select(e => e.Action).Distinct().OrderBy(a => a).ToListAsync();
    }

    private async Task RequireReadAsync(int? projectId)
    {
        if (projectId is { } pid) await access.RequireAsync(pid, Permission.ManageProjectSettings);
        else if (!await access.IsAdminAsync()) throw new ForbiddenException("Only site administrators can view the audit log.");
    }

    /// <summary>CSV of the events (RFC 4180; cells that a spreadsheet would treat as formulas are prefixed with ').</summary>
    public static string ToCsv(AuditPage page)
    {
        var sb = new StringBuilder("time_utc,actor_id,actor,ip,user_agent,action,target_type,target_id,project,details\r\n");
        foreach (var e in page.Events)
        {
            string?[] cells =
            [
                DateTime.SpecifyKind(e.CreatedAt, DateTimeKind.Utc).ToString("O"), e.ActorId?.ToString(), e.ActorName, e.IpAddress, e.UserAgent,
                e.Action, e.TargetType, e.TargetId, e.ProjectId is { } p ? page.ProjectKeys.GetValueOrDefault(p, p.ToString()) : null, e.Details,
            ];
            sb.AppendJoin(',', cells.Select(Cell)).Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Cell(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        if (v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
        return v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    // ---------- Helpers ----------

    internal static (string? Ip, string? UserAgent) ClientOf(HttpContext http)
    {
        var ip = http.Connection.RemoteIpAddress;
        if (ip is { IsIPv4MappedToIPv6: true }) ip = ip.MapToIPv4();
        var ua = http.Request.Headers.UserAgent.ToString();
        return (ip?.ToString(), string.IsNullOrEmpty(ua) ? null : ua.Length > 512 ? ua[..512] : ua);
    }

    private static int? UserIdOf(ClaimsPrincipal? user) =>
        int.TryParse(user?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static string? Serialize(object? details)
    {
        if (details == null) return null;
        var node = JsonSerializer.SerializeToNode(details, Json);
        Scrub(node);
        return node?.ToJsonString(Json);
    }

    /// <summary>Drops properties whose names suggest secrets, recursively.</summary>
    private static void Scrub(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToList())
                {
                    var k = key.ToLowerInvariant();
                    if (k.Contains("password") || k.Contains("secret") || k.Contains("plaintext") || k.Contains("hash")
                        || k is "token" or "prefix" or "apikey")
                        o.Remove(key);
                    else Scrub(o[key]);
                }
                break;
            case JsonArray a:
                foreach (var item in a) Scrub(item);
                break;
        }
    }

    /// <summary>"https://hooks.example.com": a webhook URL without user info, path or query (which can carry credentials).</summary>
    public static string SafeUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Host}{(u.IsDefaultPort ? "" : ":" + u.Port)}" : "(invalid url)";
}

/// <summary>Deletes audit events older than <see cref="AuditOptions.RetentionDays"/>, at startup and then daily.</summary>
public sealed class AuditRetentionService(IDbContextFactory<AppDbContext> dbFactory, IOptions<AuditOptions> options, ILogger<AuditRetentionService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var days = options.Value.RetentionDays;
                if (days > 0)
                {
                    var cutoff = DateTime.UtcNow.AddDays(-days);
                    await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                    var n = await db.AuditEvents.Where(e => e.CreatedAt < cutoff).ExecuteDeleteAsync(stoppingToken);
                    if (n > 0) log.LogInformation("Audit retention: deleted {Count} events older than {Days} days", n, days);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Audit retention cleanup failed");
            }
            try { await Task.Delay(TimeSpan.FromDays(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}

public static class AuditServiceCollectionExtensions
{
    public static IServiceCollection AddAuditLog(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AuditOptions>(config.GetSection("Audit"));
        services.AddHttpContextAccessor();
        services.AddScoped<AuditRequestInfo>();
        services.AddScoped<AuditLog>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<CircuitHandler, AuditCircuitHandler>());
        services.AddHostedService<AuditRetentionService>();
        return services;
    }

    /// <summary>
    /// GET /admin/audit/export.csv (site admins; all events) and ?project=KEY (that project's Owners). Takes the same
    /// filters as the audit pages: actor, action, from, to (yyyy-MM-dd, UTC days, inclusive).
    /// </summary>
    public static void MapAuditExport(this IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/audit/export.csv", async (HttpContext http, AuditLog audit, ProjectAccess access, IDbContextFactory<AppDbContext> dbFactory,
            string? project, string? actor, string? action, DateOnly? from, DateOnly? to) =>
        {
            if (UserIdOf(http.User) is not { } uid) return Results.Unauthorized();
            access.ActAs(uid);
            int? projectId = null;
            if (!string.IsNullOrEmpty(project))
            {
                await using var db = await dbFactory.CreateDbContextAsync();
                var key = project.ToUpperInvariant();
                projectId = await db.Projects.Where(p => p.Key == key).Select(p => (int?)p.Id).FirstOrDefaultAsync() ?? 0;
            }
            var q = new AuditQuery
            {
                ProjectId = projectId, Actor = actor, ActionPrefix = action,
                FromUtc = from?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), ToUtc = to?.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            };
            AuditPage page;
            try { page = await audit.SearchAsync(q, all: true); }
            catch (ForbiddenException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            await audit.RecordAsync("audit.exported", projectId: projectId, details: new { project, actor, action, from, to, rows = page.Events.Count });
            var name = $"gitbench-audit{(project != null ? "-" + project.ToLowerInvariant() : "")}-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv";
            return Results.File(Encoding.UTF8.GetBytes(AuditLog.ToCsv(page)), "text/csv; charset=utf-8", name);
        }).RequireAuthorization();
    }

    private static int? UserIdOf(ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
