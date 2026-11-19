using System.Diagnostics;
using ByteBench.Data;
using ByteBench.Services.Attachments;
using ByteBench.Services.Git;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.Net.Http.Headers;

namespace ByteBench.Services.Releases;

/// <summary>Configuration section <c>Archives</c>: limits for source archive downloads.</summary>
public sealed class ArchiveOptions
{
    /// <summary>An archive larger than this is cut off (the connection is aborted, so the client sees a failed download).</summary>
    public long MaxBytes { get; set; } = 1024L * 1024 * 1024;
    /// <summary>An archive that takes longer than this is cut off.</summary>
    public int TimeoutSeconds { get; set; } = 300;
    /// <summary>Archives generated at once; more get 503 with Retry-After.</summary>
    public int MaxConcurrent { get; set; } = 4;
}

/// <summary>
/// Browser downloads that live outside the API:
/// <list type="bullet">
/// <item><c>GET /p/{KEY}/repos/{slug}/archive/{ref}.zip</c> and <c>.tar.gz</c>: a source archive of a branch, tag or SHA, made
/// by <c>git archive</c> (argument list, never a shell; the ref is resolved to a SHA first) and streamed with size and
/// time limits.</item>
/// <item><c>GET /release-assets/{id}/{fileName}</c>: a release asset, served with the same safe headers as issue attachments.</item>
/// </list>
/// Both need ViewProject (assets of draft releases need PushCode). Unknown or invisible things are 404. A signed-in browser
/// session or <c>Authorization: Bearer gbp_…</c> (any read scope) works.
/// </summary>
public static class ReleaseDownloads
{
    public static IEndpointRouteBuilder MapReleaseDownloads(this IEndpointRouteBuilder app)
    {
        var services = app.ServiceProvider;
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("ByteBench.Archives");
        var gitPath = GitHttpBackend.LocateGit(services.GetRequiredService<IConfiguration>());
        var options = services.GetRequiredService<IConfiguration>().GetSection("Archives").Get<ArchiveOptions>() ?? new ArchiveOptions();
        var gate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrent));

        app.MapGet("/p/{key}/repos/{slug}/archive/{**path}", async (HttpContext http, string key, string slug, string path,
            ProjectAccess access, RepositoryService repos) =>
        {
            NoStatusPages(http);
            if (await ResolveUserAsync(http) is not { } userId) return Results.Unauthorized();
            access.ActAs(userId);
            if (ArchiveFormats.Parse(path) is not { } parsed || string.IsNullOrEmpty(parsed.Ref)) return Results.NotFound();
            var (rev, format) = parsed;
            var repo = await repos.GetRepositoryAsync(key, slug);
            if (repo == null) return Results.NotFound();
            var sha = await repos.Git(repo, p => GitOps.Exists(p) ? GitOps.ResolveSha(p, rev) : null);
            if (sha == null) return Results.NotFound();
            if (gitPath == null) return Results.Problem("Git is not available on this server.", statusCode: StatusCodes.Status503ServiceUnavailable);

            if (!await gate.WaitAsync(0))
            {
                http.Response.Headers.RetryAfter = "10";
                return Results.Problem("Too many archives are being generated; try again shortly.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            try
            {
                var name = $"{repo.Slug}-{SafeRefName(rev)}";
                await StreamArchiveAsync(http, gitPath, repos.PathOf(repo), sha, name, format, options, log);
                return Results.Empty;
            }
            finally { gate.Release(); }
        }).ExcludeFromDescription();

        app.MapGet("/release-assets/{id:int}/{fileName}", async (HttpContext http, int id, ReleaseService releases) =>
        {
            NoStatusPages(http);
            if (await ResolveUserAsync(http) is not { } userId) return Results.Unauthorized();
            if (await releases.OpenAssetAsync(id, userId) is not { } found) return Results.NotFound();
            return ServeAsset(http, found.Asset, found.Path);
        }).ExcludeFromDescription();
        return app;
    }

    /// <summary>Cookie session, else a Bearer personal access token with any read scope. Null if neither.</summary>
    private static async Task<int?> ResolveUserAsync(HttpContext http)
    {
        var users = http.RequestServices.GetRequiredService<UserManager<User>>();
        var header = http.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = await http.RequestServices.GetRequiredService<AccessTokenService>().FindActiveAsync(header[7..].Trim());
            if (token == null || await users.IsLockedOutAsync(token.User)) return null;
            return token.Scopes == TokenScopes.None ? null : token.UserId;
        }
        if (http.User.Identity?.IsAuthenticated == true && await users.GetUserAsync(http.User) is { } u && !await users.IsLockedOutAsync(u))
            return u.Id;
        return null;
    }

    private static void NoStatusPages(HttpContext http)
    {
        if (http.Features.Get<IStatusCodePagesFeature>() is { } pages) pages.Enabled = false;
    }

    /// <summary><c>release/1.x</c> → <c>release-1.x</c>; anything outside [A-Za-z0-9._-] becomes '-'.</summary>
    public static string SafeRefName(string rev)
    {
        var chars = rev.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-').ToArray();
        var s = new string(chars).Trim('-', '.');
        return s.Length == 0 ? "archive" : s.Length > 100 ? s[..100] : s;
    }

    private static async Task StreamArchiveAsync(HttpContext http, string gitPath, string repoPath, string sha, string name,
        ArchiveFormat format, ArchiveOptions options, ILogger log)
    {
        var psi = new ProcessStartInfo(gitPath)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var k in psi.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(k);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.ArgumentList.Add("--git-dir=" + repoPath);
        psi.ArgumentList.Add("archive");
        psi.ArgumentList.Add(format == ArchiveFormat.Zip ? "--format=zip" : "--format=tar.gz");
        psi.ArgumentList.Add("--prefix=" + name + "/");
        psi.ArgumentList.Add(sha); // a resolved 40-hex SHA: never read as an option

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, options.TimeoutSeconds)));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, http.RequestAborted);
        using var process = new Process { StartInfo = psi };
        try { process.Start(); }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not start git archive");
            http.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }
        using var kill = cts.Token.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        var stdout = process.StandardOutput.BaseStream;
        var buffer = new byte[81920];
        long total = 0;
        var started = false;
        try
        {
            int n;
            while ((n = await stdout.ReadAsync(buffer, cts.Token)) > 0)
            {
                total += n;
                if (total > options.MaxBytes)
                {
                    log.LogWarning("Archive {Name} exceeded {Max} bytes; aborted", name, options.MaxBytes);
                    process.Kill(entireProcessTree: true);
                    http.Abort();
                    return;
                }
                if (!started)
                {
                    started = true;
                    var h = http.Response.Headers;
                    http.Response.StatusCode = StatusCodes.Status200OK;
                    http.Response.ContentType = format == ArchiveFormat.Zip ? "application/zip" : "application/gzip";
                    var disposition = new ContentDispositionHeaderValue("attachment");
                    disposition.SetHttpFileName(name + ArchiveFormats.Extension(format));
                    h.ContentDisposition = disposition.ToString();
                    h.XContentTypeOptions = "nosniff";
                    h.CacheControl = "private, max-age=300";
                    h.ETag = $"\"{sha}{(format == ArchiveFormat.Zip ? "z" : "t")}\"";
                }
                await http.Response.Body.WriteAsync(buffer.AsMemory(0, n), cts.Token);
            }
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (timeout.IsCancellationRequested) log.LogWarning("Archive {Name} took longer than {Seconds}s; aborted", name, options.TimeoutSeconds);
            http.Abort();
            return;
        }

        if (process.ExitCode != 0)
        {
            log.LogWarning("git archive exited with {Code} for {Name}: {Error}", process.ExitCode, name, (await stderr).Trim());
            if (!started) http.Response.StatusCode = StatusCodes.Status500InternalServerError;
            else http.Abort(); // a truncated archive must not look complete
        }
    }

    /// <summary>Same safety rules as issue attachments (<see cref="AttachmentDownloads.Serve"/>).</summary>
    private static IResult ServeAsset(HttpContext http, ReleaseAsset a, string path)
    {
        if (!File.Exists(path)) return Results.NotFound();
        var h = http.Response.Headers;
        h.XContentTypeOptions = "nosniff";
        h.ContentSecurityPolicy = "sandbox; default-src 'none'; img-src 'self'; style-src 'unsafe-inline'";
        h["Cross-Origin-Resource-Policy"] = "same-origin";
        h.CacheControl = "private, no-cache";
        var inline = AttachmentStorage.IsInlineImage(a.ContentType);
        var disposition = new ContentDispositionHeaderValue(inline ? "inline" : "attachment");
        disposition.SetHttpFileName(a.FileName);
        h.ContentDisposition = disposition.ToString();
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Results.File(stream, inline ? a.ContentType : "application/octet-stream",
            lastModified: new DateTimeOffset(DateTime.SpecifyKind(a.CreatedAt, DateTimeKind.Utc)),
            entityTag: new EntityTagHeaderValue($"\"{a.Sha256}\""), enableRangeProcessing: true);
    }
}
