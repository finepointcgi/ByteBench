using GitBench.Services.Hosting.Backup;

namespace GitBench.Services.Hosting;

public enum CheckSeverity { Info, Warning, Problem }

public sealed record HostingCheck(CheckSeverity Severity, string Key, string Message);

/// <summary>
/// Reviews the hosting configuration the way an operator would before going live. Shown on <c>/admin/system</c> and logged once
/// at startup outside Development (warnings and problems only). Nothing here changes behaviour.
/// </summary>
public static class ProductionChecks
{
    public static IReadOnlyList<HostingCheck> Evaluate(IConfiguration c, IHostEnvironment env)
    {
        var list = new List<HostingCheck>();
        void Add(CheckSeverity s, string key, string message) => list.Add(new(s, key, message));
        var prod = !env.IsDevelopment();

        if (!prod) Add(CheckSeverity.Info, "Environment", $"Running as {env.EnvironmentName}: cookies are not Secure-only and HSTS/HTTPS redirection are off.");

        var publicUrl = c["PublicBaseUrl"];
        if (prod && string.IsNullOrWhiteSpace(publicUrl))
            Add(CheckSeverity.Warning, "PublicBaseUrl", "Not set: links in API responses, webhooks and e-mail are learned from the first request. Set it to the public https:// URL.");
        else if (prod && publicUrl!.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !IsLocal(publicUrl))
            Add(CheckSeverity.Warning, "PublicBaseUrl", "Is plain http://. Production sign-in cookies are Secure-only, so serve GitBench over https.");

        var proxy = c.GetValue("ReverseProxy:Enabled", false);
        if (prod && !proxy && publicUrl?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true)
            Add(CheckSeverity.Warning, "ReverseProxy:Enabled", "PublicBaseUrl is https but ReverseProxy:Enabled is false: behind a TLS proxy GitBench then sees http, sends no HSTS header and logs the proxy's address as the client. Enable it and list the proxy in ReverseProxy:KnownProxies / KnownNetworks.");
        if (proxy && c.GetSection("ReverseProxy:KnownProxies").GetChildren().Count() + c.GetSection("ReverseProxy:KnownNetworks").GetChildren().Count() == 0)
            Add(CheckSeverity.Info, "ReverseProxy:KnownProxies", "Only loopback proxies are trusted. Add your proxy's address or network (KnownProxies / KnownNetworks) if it runs elsewhere, e.g. another container.");

        if (prod && !PublicSecureCookies(c, env))
            Add(CheckSeverity.Warning, "Security:SecureCookies", "Off: sign-in cookies are also sent over plain HTTP. Only for a trial without TLS.");
        if (prod && !c.GetValue("Security:ContentSecurityPolicy", true))
            Add(CheckSeverity.Warning, "Security:ContentSecurityPolicy", "The Content-Security-Policy is off.");
        if (prod && (c.GetValue<bool?>("Seed:DemoData") ?? false))
            Add(CheckSeverity.Problem, "Seed:DemoData", "On: the demo users share a published password. Turn it off in production.");
        if (prod && c["AllowedHosts"] is null or "*")
            Add(CheckSeverity.Info, "AllowedHosts", "Is '*': any Host header is accepted. Set it to your host name when the proxy doesn't filter it.");
        if (!c.GetValue("RateLimiting:Enabled", true))
            Add(CheckSeverity.Warning, "RateLimiting:Enabled", "Rate limiting is off.");

        // Data Protection key ring: sign-in cookies, antiforgery and stored secrets depend on it surviving restarts.
        var keys = c["DataProtection:KeysPath"];
        if (string.IsNullOrWhiteSpace(keys))
            Add(prod ? CheckSeverity.Warning : CheckSeverity.Info, "DataProtection:KeysPath",
                $"Not set: the key ring is kept in {Path.Combine(env.ContentRootPath, "keys")}. Point it at a persistent, backed-up folder (a volume in Docker), or every deployment signs everyone out and loses encrypted webhook/CI secrets.");
        else if (KeysProblem(keys) is { } problem)
            Add(CheckSeverity.Problem, "DataProtection:KeysPath", problem);
        if (prod && string.IsNullOrWhiteSpace(c["DataProtection:CertificatePath"]) && !OperatingSystem.IsWindows())
            Add(CheckSeverity.Info, "DataProtection:CertificatePath", "Keys are stored unencrypted on disk (file mode 700). Set DataProtection:CertificatePath to a .pfx to encrypt them at rest.");

        var provider = DatabaseSetup.GetProvider(c);
        if (prod && provider == DatabaseProvider.Sqlite)
            Add(CheckSeverity.Info, "Database:Provider", "SQLite: one server process only. Use Database:Provider=Postgres for a managed or replicated database.");

        var backup = c.GetSection("Backup").Get<BackupOptions>() ?? new BackupOptions();
        if (prod && !backup.Enabled)
            Add(CheckSeverity.Warning, "Backup:Enabled", "Scheduled backups are off. Turn them on (Backup:Enabled=true), or run `dotnet GitBench.dll backup <dir>` from cron, and test a restore.");
        if (backup.Enabled && backup.KeepLast == 0 && backup.KeepDays == 0)
            Add(CheckSeverity.Info, "Backup:KeepLast", "No retention: archives pile up until the disk is full. Set Backup:KeepLast and/or Backup:KeepDays.");
        if (backup.Enabled && !backup.IncludeKeys)
            Add(CheckSeverity.Info, "Backup:IncludeKeys", "Keys are not in the archives: back up DataProtection:KeysPath separately, or a restore can't decrypt stored secrets.");

        var level = c["Logging:LogLevel:Default"];
        if (prod && level is "Debug" or "Trace")
            Add(CheckSeverity.Info, "Logging:LogLevel:Default", $"Is {level}: expect very large logs. Use Information in production, and a log driver with rotation (docker: max-size / max-file).");
        if (c.GetValue("Audit:RetentionDays", 365) == 0)
            Add(CheckSeverity.Info, "Audit:RetentionDays", "0 keeps audit events forever; the table grows without bound.");
        if (c.GetValue("Retention:WebhookDeliveryDays", 30) == 0)
            Add(CheckSeverity.Info, "Retention:WebhookDeliveryDays", "0 keeps webhook delivery payloads forever; the table grows without bound.");
        if (c.GetValue("Ci:Enabled", true) && prod)
            Add(CheckSeverity.Info, "Ci:Enabled", "CI pipelines run containers through the Docker daemon: anyone who can push can run code there. See the CI security notes.");
        return list;
    }

    private static bool PublicSecureCookies(IConfiguration c, IHostEnvironment env) => ProductionSetup.SecureCookies(c, env);

    private static bool IsLocal(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase));

    /// <summary>Why the key ring folder can't be used, or null when it is writable.</summary>
    public static string? KeysProblem(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(full);
            var probe = Path.Combine(full, ".write-test-" + Guid.NewGuid().ToString("N")[..6]);
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"DataProtection:KeysPath ({path}) is not writable ({ex.Message}). Sign-in cookies and stored secrets can't survive a restart; fix the folder's permissions (the container user is uid 1654).";
        }
    }

    /// <summary>Logs warnings and problems once at startup (Production-like environments).</summary>
    public static void LogAtStartup(IConfiguration c, IHostEnvironment env, ILogger log)
    {
        if (env.IsDevelopment()) return;
        foreach (var check in Evaluate(c, env).Where(x => x.Severity != CheckSeverity.Info))
            log.Log(check.Severity == CheckSeverity.Problem ? LogLevel.Error : LogLevel.Warning, "Hosting check {Key}: {Message}", check.Key, check.Message);
    }
}
