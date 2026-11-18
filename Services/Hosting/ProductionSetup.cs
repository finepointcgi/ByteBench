using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using GitBench.Data;
using GitBench.Services.Ci;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace GitBench.Services.Hosting;

/// <summary>
/// Everything needed to run GitBench in production, wired from Program.cs with a few calls:
/// <list type="bullet">
/// <item>Data Protection keys persisted to <c>DataProtection:KeysPath</c> (optionally encrypted with a certificate), so
/// sign-in cookies, antiforgery tokens and encrypted secrets survive restarts.</item>
/// <item>Forwarded headers behind a reverse proxy (<c>ReverseProxy:*</c>).</item>
/// <item>Secure cookies, security headers and a Content-Security-Policy for HTML pages (<c>Security:*</c>).</item>
/// <item>Rate limiting of sign-in, the API, git over HTTP and webhook test pings (<c>RateLimiting:*</c>).</item>
/// <item>Health checks at <c>/health/live</c> and <c>/health/ready</c>.</item>
/// <item>Demo data only where wanted (<c>Seed:DemoData</c>) and site admins from <c>Bootstrap:AdminUsers</c>.</item>
/// </list>
/// </summary>
public static class ProductionSetup
{
    public const string CspNonceKey = "gitbench.csp-nonce";

    // ------------------------------------------------------------------ services

    public static WebApplicationBuilder AddGitBenchHosting(this WebApplicationBuilder builder)
    {
        var config = builder.Configuration;
        var env = builder.Environment;
        var services = builder.Services;

        // ----- Data Protection -----
        var keysPath = Path.GetFullPath(config["DataProtection:KeysPath"] is { Length: > 0 } kp ? kp : Path.Combine(env.ContentRootPath, "keys"));
        Directory.CreateDirectory(keysPath);
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(keysPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not ours to chmod: leave it */ }
        }
        var dp = services.AddDataProtection()
            .SetApplicationName(config["DataProtection:ApplicationName"] is { Length: > 0 } app ? app : "GitBench")
            .PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        if (config["DataProtection:CertificatePath"] is { Length: > 0 } certPath)
        {
            // PKCS#12 (.pfx) with the private key. Old certificates can be listed to keep reading keys they encrypted.
            var cert = X509CertificateLoader.LoadPkcs12FromFile(certPath, config["DataProtection:CertificatePassword"]);
            dp.ProtectKeysWithCertificate(cert);
            var older = config.GetSection("DataProtection:DecryptionCertificatePaths").Get<string[]>() ?? [];
            if (older.Length > 0)
                dp.UnprotectKeysWithAnyCertificate(older.Select(p => X509CertificateLoader.LoadPkcs12FromFile(p, config["DataProtection:CertificatePassword"])).Prepend(cert).ToArray());
        }

        // ----- Forwarded headers (reverse proxy) -----
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            var section = config.GetSection("ReverseProxy");
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            if (section.GetValue("ForwardHost", false)) o.ForwardedHeaders |= ForwardedHeaders.XForwardedHost;
            o.ForwardLimit = section.GetValue<int?>("ForwardLimit") ?? 1;
            var proxies = section.GetSection("KnownProxies").Get<string[]>() ?? [];
            var networks = section.GetSection("KnownNetworks").Get<string[]>() ?? [];
            if (proxies.Length > 0 || networks.Length > 0)
            {
                o.KnownProxies.Clear();
                o.KnownIPNetworks.Clear();
                foreach (var p in proxies) o.KnownProxies.Add(IPAddress.Parse(p));
                foreach (var n in networks) o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(n));
            }
            // Otherwise only loopback proxies are trusted (the framework default).
            var hosts = section.GetSection("AllowedHosts").Get<string[]>() ?? [];
            foreach (var h in hosts) o.AllowedHosts.Add(h);
        });

        // ----- Cookies & HSTS -----
        var secure = SecureCookies(config, env);
        services.PostConfigureAll<CookieAuthenticationOptions>(o =>
        {
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax; // Lax, not Strict: links from e-mail and chat must arrive signed in
            o.Cookie.SecurePolicy = secure ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        });
        services.Configure<AntiforgeryOptions>(o =>
        {
            o.Cookie.SameSite = SameSiteMode.Strict;
            // SameAsRequest (Secure on HTTPS): with Always, antiforgery throws on plain-HTTP requests and every form page is a 500.
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });
        services.AddHsts(o =>
        {
            o.MaxAge = TimeSpan.FromDays(config.GetValue("Security:HstsMaxAgeDays", 365));
            o.IncludeSubDomains = config.GetValue("Security:HstsIncludeSubDomains", false);
        });

        // ----- Rate limiting -----
        services.Configure<RateLimitSettings>(config.GetSection("RateLimiting"));
        services.AddSingleton<AppRateLimits>();
        services.AddRateLimiter(o =>
        {
            var settings = config.GetSection("RateLimiting").Get<RateLimitSettings>() ?? new RateLimitSettings();
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => AppRateLimits.Partition(ctx, settings));
            o.OnRejected = AppRateLimits.OnRejectedAsync;
        });

        // ----- Health checks -----
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
            .AddCheck<StorageHealthCheck>("storage", tags: ["ready"])
            .AddCheck<DockerHealthCheck>("docker", failureStatus: HealthStatus.Degraded, tags: ["ready"]);

        return builder;
    }

    /// <summary><c>Security:SecureCookies</c>; defaults to true outside Development (cookies only over HTTPS).</summary>
    public static bool SecureCookies(IConfiguration config, IHostEnvironment env) =>
        config.GetValue<bool?>("Security:SecureCookies") ?? !env.IsDevelopment();

    // ------------------------------------------------------------------ middleware

    /// <summary>First in the pipeline: when <c>ReverseProxy:Enabled</c>, trusts X-Forwarded-For/-Proto from known proxies.</summary>
    public static WebApplication UseGitBenchForwardedHeaders(this WebApplication app)
    {
        if (app.Configuration.GetValue("ReverseProxy:Enabled", false)) app.UseForwardedHeaders();
        if (SecureCookies(app.Configuration, app.Environment))
        {
            // Sign-in cookies are Secure-only: over plain HTTP the browser drops them and sign-in appears to do nothing.
            var warned = 0;
            app.Use((ctx, next) =>
            {
                if (!ctx.Request.IsHttps && !ctx.Request.Path.StartsWithSegments("/health") && Interlocked.Exchange(ref warned, 1) == 0)
                    app.Logger.LogWarning("Plain-HTTP request to {Host} while Security:SecureCookies is on: sign-in only works over HTTPS. " +
                        "Put GitBench behind a TLS proxy (with ReverseProxy:Enabled), or set Security:SecureCookies=false for a trial.", ctx.Request.Host);
                return next(ctx);
            });
        }
        return app;
    }

    /// <summary>HSTS and HTTPS redirection (outside Development, unless <c>Security:HttpsRedirection</c> is false).</summary>
    public static WebApplication UseGitBenchHttps(this WebApplication app)
    {
        if (app.Environment.IsDevelopment()) return app;
        app.UseHsts();
        if (app.Configuration.GetValue("Security:HttpsRedirection", true)) app.UseHttpsRedirection();
        return app;
    }

    /// <summary>
    /// Security headers on every response, plus a Content-Security-Policy on HTML responses that don't set their own.
    /// Scripts: same origin, the per-request nonce (Blazor's import map) and hashes of the few inline handlers the app
    /// uses (<c>onfocus="this.select()"</c>, copy buttons). Styles allow <c>'unsafe-inline'</c> because Blazor components
    /// set <c>style=""</c> attributes (label colours, chart sizes). Blazor's circuit needs <c>connect-src</c> to this
    /// host's ws/wss.
    /// </summary>
    public static WebApplication UseGitBenchSecurityHeaders(this WebApplication app)
    {
        var config = app.Configuration;
        var cspEnabled = config.GetValue("Security:ContentSecurityPolicy", true);
        var reportOnly = config.GetValue("Security:ContentSecurityPolicyReportOnly", false);
        var hashes = string.Join(' ', InlineHandlers.Concat(config.GetSection("Security:CspInlineHandlers").Get<string[]>() ?? [])
            .Select(h => $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(h)))}'"));

        app.Use((ctx, next) =>
        {
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            ctx.Items[CspNonceKey] = nonce;
            ctx.Response.OnStarting(() =>
            {
                var h = ctx.Response.Headers;
                if (!h.ContainsKey("X-Content-Type-Options")) h.XContentTypeOptions = "nosniff";
                h.XFrameOptions = "DENY";
                h["Referrer-Policy"] = "strict-origin-when-cross-origin";
                h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
                h["Cross-Origin-Opener-Policy"] = "same-origin";
                // Blazor's own "frame-ancestors 'self'" is replaced (ours is stricter); a response's own full policy (attachments) is kept.
                var existing = h.ContentSecurityPolicy.ToString();
                if (cspEnabled && (existing.Length == 0 || existing.StartsWith("frame-ancestors", StringComparison.Ordinal))
                    && !h.ContainsKey("Content-Security-Policy-Report-Only")
                    && ctx.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var host = ctx.Request.Host.Value;
                    var csp = "default-src 'self'; " +
                              $"script-src 'self' 'nonce-{nonce}' 'unsafe-hashes' {hashes}; " +
                              "style-src 'self' 'unsafe-inline'; " +
                              "img-src 'self' data: blob:; font-src 'self' data:; " +
                              $"connect-src 'self' ws://{host} wss://{host}; " +
                              "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
                    if (reportOnly) h["Content-Security-Policy-Report-Only"] = csp;
                    else h.ContentSecurityPolicy = csp;
                }
                return Task.CompletedTask;
            });
            return next(ctx);
        });
        return app;
    }

    /// <summary>The per-request CSP nonce for inline <c>&lt;script&gt;</c> elements (App.razor's import map).</summary>
    public static string? CspNonce(HttpContext? ctx) => ctx?.Items[CspNonceKey] as string;

    /// <summary>Inline event-handler attributes in the app's markup (allowed by hash with 'unsafe-hashes').</summary>
    private static readonly string[] InlineHandlers =
    [
        "this.select()",
        "navigator.clipboard.writeText(document.getElementById('new-token').value); this.textContent='Copied';",
        "navigator.clipboard.writeText(document.getElementById('hook-secret').value); this.textContent='Copied';",
        // Account/Pages/TwoFactorSettings.razor (static SSR): copy recovery codes, confirm regenerating them.
        "navigator.clipboard.writeText(document.getElementById('recovery-codes').textContent); this.textContent='Copied';",
        "return confirm('Generate new recovery codes? Your current codes will stop working.');",
    ];

    /// <summary><c>/health/live</c> (process up) and <c>/health/ready</c> (database, storage, Docker as "degraded").</summary>
    public static WebApplication MapGitBenchHealth(this WebApplication app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteJsonAsync })
            .DisableRateLimiting().AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready"), ResponseWriter = WriteJsonAsync })
            .DisableRateLimiting().AllowAnonymous();
        return app;
    }

    private static Task WriteJsonAsync(HttpContext ctx, HealthReport report)
    {
        if (ctx.Features.Get<IStatusCodePagesFeature>() is { } pages) pages.Enabled = false;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        var body = new
        {
            status = report.Status.ToString(),
            durationMs = (int)report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.ToDictionary(e => e.Key, e => new { status = e.Value.Status.ToString(), description = e.Value.Description }),
        };
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    // ------------------------------------------------------------------ startup data

    /// <summary><c>Seed:DemoData</c>; defaults to true in Development only. Demo users share a published password.</summary>
    public static bool SeedDemoData(WebApplication app) =>
        app.Configuration.GetValue<bool?>("Seed:DemoData") ?? app.Environment.IsDevelopment();

    /// <summary>
    /// Makes the users named in <c>Bootstrap:AdminUsers</c> (usernames, comma-separated or an array) site admins. Used to get
    /// the first admin on a fresh production install: register, set the variable, restart.
    /// </summary>
    public static void PromoteBootstrapAdmins(AppDbContext db, IConfiguration config, ILogger log)
    {
        var section = config.GetSection("Bootstrap:AdminUsers");
        var names = (section.Get<string[]>() ?? (section.Value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(n => n.ToUpperInvariant()).ToList();
        if (names.Count == 0) return;
        foreach (var user in db.Users.Where(u => names.Contains(u.NormalizedUserName!) && !u.IsAdmin).ToList())
        {
            user.IsAdmin = true;
            log.LogWarning("Bootstrap:AdminUsers: {User} is now a site admin", user.UserName);
        }
        db.SaveChanges();
    }
}

// ---------------------------------------------------------------------- rate limiting

/// <summary>A fixed-window limit: <see cref="PermitLimit"/> requests per <see cref="WindowSeconds"/>.</summary>
public sealed class WindowLimit
{
    public int PermitLimit { get; set; }
    public int WindowSeconds { get; set; } = 60;
}

/// <summary>Configuration section <c>RateLimiting</c>.</summary>
public sealed class RateLimitSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>POSTs to <c>/account/*</c> (sign-in, registration, two-factor), per client IP.</summary>
    public WindowLimit Auth { get; set; } = new() { PermitLimit = 10, WindowSeconds = 60 };
    /// <summary><c>/api/*</c>, per access token (else per signed-in user, else per IP).</summary>
    public WindowLimit Api { get; set; } = new() { PermitLimit = 300, WindowSeconds = 60 };
    /// <summary><c>/git/*</c>, per user named in the Basic credentials (else per IP).</summary>
    public WindowLimit Git { get; set; } = new() { PermitLimit = 120, WindowSeconds = 60 };
    /// <summary>Webhook test pings, per user.</summary>
    public WindowLimit WebhookPing { get; set; } = new() { PermitLimit = 5, WindowSeconds = 60 };
    /// <summary>Git commands over SSH (each clone, fetch or push), per user or deploy key.</summary>
    public WindowLimit Ssh { get; set; } = new() { PermitLimit = 120, WindowSeconds = 60 };
}

/// <summary>Partitioning for the global HTTP rate limiter, the 429 response, and the in-app limiter for webhook test pings.</summary>
public sealed class AppRateLimits(IOptions<RateLimitSettings> options)
{
    private readonly PartitionedRateLimiter<string> pings = PartitionedRateLimiter.Create<string, string>(key =>
        options.Value.Enabled && options.Value.WebhookPing.PermitLimit > 0
            ? RateLimitPartition.GetFixedWindowLimiter(key, _ => Window(options.Value.WebhookPing))
            : RateLimitPartition.GetNoLimiter(key));

    private readonly PartitionedRateLimiter<string> ssh = PartitionedRateLimiter.Create<string, string>(key =>
        options.Value.Enabled && options.Value.Ssh.PermitLimit > 0
            ? RateLimitPartition.GetFixedWindowLimiter(key, _ => Window(options.Value.Ssh))
            : RateLimitPartition.GetNoLimiter(key));

    /// <summary>
    /// Git over SSH (<c>RateLimiting:Ssh</c>), keyed per user or deploy key like <c>/git/*</c> is per user. Returns null when
    /// the command may run, else the seconds to wait.
    /// </summary>
    public int? TryAcquireSsh(string key)
    {
        using var lease = ssh.AttemptAcquire("ssh:" + key);
        if (lease.IsAcquired) return null;
        return lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)) : options.Value.Ssh.WindowSeconds;
    }

    /// <summary>Throws (with a "try again in N seconds" message) when the user sent too many test pings.</summary>
    public void CheckWebhookPing(int? userId)
    {
        using var lease = pings.AttemptAcquire("ping:" + (userId?.ToString() ?? "anon"));
        if (lease.IsAcquired) return;
        var wait = lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? (int)Math.Ceiling(retry.TotalSeconds) : options.Value.WebhookPing.WindowSeconds;
        throw new InvalidOperationException($"Too many test pings. Try again in {wait} seconds.");
    }

    internal static RateLimitPartition<string> Partition(HttpContext ctx, RateLimitSettings s)
    {
        if (!s.Enabled) return RateLimitPartition.GetNoLimiter("off");
        var path = ctx.Request.Path;
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (HttpMethods.IsPost(ctx.Request.Method) && path.StartsWithSegments("/account") && !path.StartsWithSegments("/account/logout"))
            return Limit("auth:" + ip, s.Auth);

        if (path.StartsWithSegments("/api"))
        {
            var auth = ctx.Request.Headers.Authorization.ToString();
            var key = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? "t:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(auth[7..].Trim())))[..16]
                : ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } uid ? "u:" + uid : "ip:" + ip;
            return Limit("api:" + key, s.Api);
        }

        if (path.StartsWithSegments("/git"))
            return Limit("git:" + (BasicUser(ctx) is { } user ? "u:" + user : "ip:" + ip), s.Git);

        return RateLimitPartition.GetNoLimiter("none");
    }

    private static RateLimitPartition<string> Limit(string key, WindowLimit limit) =>
        limit.PermitLimit <= 0 ? RateLimitPartition.GetNoLimiter(key) : RateLimitPartition.GetFixedWindowLimiter(key, _ => Window(limit));

    private static FixedWindowRateLimiterOptions Window(WindowLimit limit) => new()
    {
        PermitLimit = limit.PermitLimit, Window = TimeSpan.FromSeconds(Math.Max(1, limit.WindowSeconds)), QueueLimit = 0, AutoReplenishment = true,
    };

    /// <summary>The (lower-cased) login from HTTP Basic credentials, without checking them.</summary>
    private static string? BasicUser(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
            var sep = decoded.IndexOf(':');
            return sep > 0 ? decoded[..sep].ToLowerInvariant() : null;
        }
        catch (FormatException) { return null; }
    }

    /// <summary>429 with Retry-After; problem+json for the API, plain text otherwise (never the HTML status page).</summary>
    internal static async ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken ct)
    {
        var http = context.HttpContext;
        if (http.Features.Get<IStatusCodePagesFeature>() is { } pages) pages.Enabled = false;
        var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)) : 60;
        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        http.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.Response.Headers.CacheControl = "no-store";
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GitBench.RateLimiting")
            .LogWarning("Rate limit hit: {Method} {Path} from {Ip}", http.Request.Method, http.Request.Path, http.Connection.RemoteIpAddress);
        if (http.Request.Path.StartsWithSegments("/api"))
        {
            http.Response.ContentType = "application/problem+json";
            await http.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                type = "https://tools.ietf.org/html/rfc6585#section-4", title = "Too Many Requests", status = 429,
                detail = $"Rate limit exceeded. Try again in {seconds} seconds.",
            }), ct);
            return;
        }
        http.Response.ContentType = "text/plain; charset=utf-8";
        await http.Response.WriteAsync($"Too many requests. Try again in {seconds} seconds.\n", ct);
    }
}

// ---------------------------------------------------------------------- health checks

public sealed class DatabaseHealthCheck(IDbContextFactory<AppDbContext> dbFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            if (!await db.Database.CanConnectAsync(ct)) return HealthCheckResult.Unhealthy("Cannot connect to the database.");
            await db.Projects.AnyAsync(ct);
            return HealthCheckResult.Healthy("Database reachable.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Database query failed.", ex);
        }
    }
}

/// <summary>The repository root and the attachments folder exist and are writable.</summary>
public sealed class StorageHealthCheck(RepoStorage repos, Attachments.AttachmentStorage attachments) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        foreach (var (name, dir) in new[] { ("repositories", repos.Root), ("attachments", attachments.Root) })
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, $".health-{Guid.NewGuid():N}");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy($"The {name} folder is not writable.", ex));
            }
        }
        return Task.FromResult(HealthCheckResult.Healthy("Repositories and attachments folders are writable."));
    }
}

/// <summary>Docker is only needed for CI pipelines: a missing daemon is "Degraded", never "Unhealthy".</summary>
public sealed class DockerHealthCheck(IOptionsMonitor<CiOptions> options, CiDocker docker) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        if (!options.CurrentValue.Enabled) return HealthCheckResult.Healthy("CI pipelines are disabled.");
        return await docker.IsAvailableAsync(ct)
            ? HealthCheckResult.Healthy("Docker is available for CI pipelines.")
            : HealthCheckResult.Degraded("Docker is not available: CI pipelines will fail.");
    }
}
