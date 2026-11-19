using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ByteBench.Services.Git;

/// <summary>
/// Runs <c>git http-backend</c> as a CGI process for one request: streams the request body to its
/// stdin, parses the CGI response headers from stdout, then streams the rest of stdout to the client.
/// Nothing is buffered beyond a small header buffer, so packs of any size stream through.
/// </summary>
internal sealed partial class GitHttpBackend(string? gitPath, RepoStorage storage, ILogger logger)
{
    /// <summary>Absolute path of the git executable, or null when git could not be found.</summary>
    public string? GitPath { get; } = gitPath;

    /// <summary>
    /// Finds git: the "Git:Executable" setting if present, otherwise the first "git" on PATH.
    /// Returns null (and the caller logs) when nothing usable is found.
    /// </summary>
    public static string? LocateGit(IConfiguration config)
    {
        var configured = config["Git:Executable"];
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;

        var names = OperatingSystem.IsWindows() ? new[] { "git.exe", "git.cmd" } : new[] { "git" };
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim('"'), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { /* malformed PATH entry */ }
            }
        }
        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9=:._-]{1,64}$")]
    private static partial Regex GitProtocolPattern();

    /// <summary>Outcome of one CGI run. <see cref="ExitCode"/> is -1 if git never ran to completion
    /// (client disconnect, spawn failure). <see cref="RequestBytes"/> counts the (possibly gzipped)
    /// request body bytes forwarded to git's stdin.</summary>
    public readonly record struct Result(int ExitCode, long RequestBytes);

    /// <summary>
    /// Executes http-backend. <paramref name="pathInfo"/> must already be validated
    /// (e.g. "/WEB/web-app.git/info/refs"). <paramref name="body"/> replaces the request body with an already
    /// decoded stream (CONTENT_LENGTH and Content-Encoding are then dropped); <paramref name="extraEnv"/> adds CGI
    /// environment variables. Both are used by <see cref="ProtectedPushGuard"/>.
    /// </summary>
    public async Task<Result> RunAsync(HttpContext ctx, string pathInfo, string remoteUser,
        Stream? body = null, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var request = ctx.Request;
        var response = ctx.Response;
        var aborted = ctx.RequestAborted;

        var psi = new ProcessStartInfo(GitPath!)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = storage.Root,
        };
        psi.ArgumentList.Add("http-backend");

        // Don't let the server's own git environment (GIT_DIR, GIT_CONFIG_*, ...) leak into the CGI.
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(key);

        var env = psi.Environment;
        env["GIT_PROJECT_ROOT"] = storage.Root;
        env["GIT_HTTP_EXPORT_ALL"] = "1";
        env["GATEWAY_INTERFACE"] = "CGI/1.1";
        env["SERVER_PROTOCOL"] = request.Protocol;
        env["REQUEST_METHOD"] = request.Method;
        env["PATH_INFO"] = pathInfo;
        env["QUERY_STRING"] = request.QueryString.HasValue ? request.QueryString.Value![1..] : "";
        env["CONTENT_TYPE"] = request.ContentType ?? "";
        env["REMOTE_USER"] = remoteUser;
        env["REMOTE_ADDR"] = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
        if (request.ContentLength is { } len) env["CONTENT_LENGTH"] = len.ToString(System.Globalization.CultureInfo.InvariantCulture);
        else env.Remove("CONTENT_LENGTH"); // http-backend reads to EOF when unset (chunked requests)
        // git clients gzip large request bodies; http-backend inflates them itself when told.
        var encoding = request.Headers.ContentEncoding.ToString();
        if (!string.IsNullOrEmpty(encoding)) env["HTTP_CONTENT_ENCODING"] = encoding;
        var gitProtocol = request.Headers["Git-Protocol"].ToString();
        if (!string.IsNullOrEmpty(gitProtocol) && GitProtocolPattern().IsMatch(gitProtocol))
            env["GIT_PROTOCOL"] = gitProtocol;
        if (body != null)
        {
            env.Remove("CONTENT_LENGTH");
            env.Remove("HTTP_CONTENT_ENCODING");
        }
        if (extraEnv != null)
            foreach (var (k, v) in extraEnv) env[k] = v;

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Process.Start returned false");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start '{Git} http-backend'", GitPath);
            response.StatusCode = StatusCodes.Status500InternalServerError;
            return new Result(-1, 0);
        }

        using var killOnAbort = aborted.Register(() => TryKill(process));

        var stderrTask = DrainStderrAsync(process.StandardError, pathInfo);
        var stdinTask = PumpStdinAsync(body ?? request.Body, process.StandardInput.BaseStream, aborted);

        try
        {
            await CopyCgiResponseAsync(process.StandardOutput.BaseStream, response, aborted);
            var requestBytes = await stdinTask;
            await process.WaitForExitAsync(aborted);
            await stderrTask;
            return new Result(process.ExitCode, requestBytes);
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            logger.LogInformation("Client disconnected during git {PathInfo}", pathInfo);
            TryKill(process);
            return new Result(-1, 0);
        }
        catch (IOException ex) when (aborted.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Client disconnected during git {PathInfo}", pathInfo);
            TryKill(process);
            return new Result(-1, 0);
        }
    }

    private async Task<long> PumpStdinAsync(Stream body, Stream stdin, CancellationToken ct)
    {
        long total = 0;
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int n;
            while ((n = await body.ReadAsync(buffer, ct)) > 0)
            {
                total += n;
                await stdin.WriteAsync(buffer.AsMemory(0, n), ct);
            }
        }
        catch (IOException ex)
        {
            // git closed stdin early (e.g. it rejected the request); its stdout says why.
            logger.LogDebug(ex, "git http-backend stdin closed early");
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            try { await stdin.DisposeAsync(); } catch (IOException) { }
        }
        return total;
    }

    private async Task DrainStderrAsync(StreamReader stderr, string pathInfo)
    {
        string? line;
        while ((line = await stderr.ReadLineAsync()) is not null)
        {
            if (line.Length == 0) continue;
            // Real problems at Warning; progress chatter ("Counting objects: 11%...") at Debug.
            if (line.StartsWith("fatal:", StringComparison.Ordinal) || line.StartsWith("error:", StringComparison.Ordinal))
                logger.LogWarning("git http-backend [{PathInfo}]: {Line}", pathInfo, line);
            else
                logger.LogDebug("git http-backend [{PathInfo}]: {Line}", pathInfo, line);
        }
    }

    private const int MaxHeaderBytes = 64 * 1024;

    /// <summary>Reads CGI headers ("Status:", "Content-Type:", ...) then streams the body.</summary>
    private static async Task CopyCgiResponseAsync(Stream stdout, HttpResponse response, CancellationToken ct)
    {
        var buffer = new byte[MaxHeaderBytes];
        int filled = 0, headerEnd = -1, bodyStart = -1;
        while (headerEnd < 0)
        {
            if (filled == buffer.Length) throw new InvalidDataException("CGI headers too large");
            var n = await stdout.ReadAsync(buffer.AsMemory(filled), ct);
            if (n == 0) break;
            filled += n;
            (headerEnd, bodyStart) = FindHeaderEnd(buffer.AsSpan(0, filled));
        }

        if (headerEnd < 0)
        {
            // git produced no (complete) headers: treat as a server error.
            response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        var headerText = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        var status = StatusCodes.Status200OK;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in headerText.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Status", StringComparison.OrdinalIgnoreCase))
            {
                var space = value.IndexOf(' ');
                if (int.TryParse(space < 0 ? value : value[..space], out var code)) status = code;
            }
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(value, out var cl)) response.ContentLength = cl;
            }
            else
            {
                // First occurrence replaces anything we preset (e.g. Cache-Control); repeats append.
                if (seen.Add(name)) response.Headers[name] = value;
                else response.Headers.Append(name, value);
            }
        }
        response.StatusCode = status;

        if (filled > bodyStart)
            await response.Body.WriteAsync(buffer.AsMemory(bodyStart, filled - bodyStart), ct);
        await response.Body.FlushAsync(ct);
        await stdout.CopyToAsync(response.Body, 81920, ct);
    }

    /// <summary>Finds the blank line ending the CGI headers ("\r\n\r\n" or "\n\n").</summary>
    private static (int headerEnd, int bodyStart) FindHeaderEnd(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] != '\n') continue;
            if (i + 1 < data.Length && data[i + 1] == '\n') return (i, i + 2);
            if (i + 2 < data.Length && data[i + 1] == '\r' && data[i + 2] == '\n') return (i, i + 3);
        }
        return (-1, -1);
    }

    private void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) { logger.LogDebug(ex, "Failed to kill git http-backend"); }
    }
}
