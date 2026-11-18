using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace GitBench.Services.Editor.Ai;

/// <summary>What <c>claude --version</c> and <c>claude --help</c> said, and when.</summary>
public sealed record ClaudeCodeStatus(bool Installed, string? Version, string? ExecutablePath, string? Error, DateTime CheckedAt,
    IReadOnlySet<string> Flags)
{
    public bool Supports(string flag) => Flags.Contains(flag);
}

/// <summary>
/// Finds Claude Code on the server (<c>Ai:ClaudeCode:Path</c>, default <c>claude</c> on PATH) by running
/// <c>claude --version</c>, and reads <c>claude --help</c> to learn which flags this version has (so GitBench never passes one
/// it doesn't list). Runs at startup (<see cref="ClaudeCodeStartupCheck"/>) and on demand (the AI status pages). Singleton.
/// </summary>
public sealed partial class ClaudeCodeDetector(IOptions<ClaudeCodeOptions> options, ILogger<ClaudeCodeDetector> log)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);
    private readonly SemaphoreSlim gate = new(1, 1);
    private volatile ClaudeCodeStatus? last;

    public ClaudeCodeStatus? Last => last;

    public async Task<ClaudeCodeStatus> GetAsync(bool refresh = false, CancellationToken ct = default)
    {
        if (!refresh && last is { } cached) return cached;
        await gate.WaitAsync(ct);
        try
        {
            if (!refresh && last is { } again) return again;
            last = await DetectAsync(ct);
            return last;
        }
        finally { gate.Release(); }
    }

    private async Task<ClaudeCodeStatus> DetectAsync(CancellationToken ct)
    {
        var exe = ResolveExecutable(options.Value.Path);
        if (exe == null)
            return new ClaudeCodeStatus(false, null, null, $"'{options.Value.Path}' was not found on the server's PATH.", DateTime.UtcNow, new HashSet<string>());
        try
        {
            var (code, stdout, stderr) = await RunAsync(exe, ["--version"], ct);
            if (code != 0) return new ClaudeCodeStatus(false, null, exe, $"'claude --version' exited with {code}: {Trim(stderr)}", DateTime.UtcNow, new HashSet<string>());
            var version = stdout.Trim().Split('\n')[0].Trim();
            var (_, help, _) = await RunAsync(exe, ["--help"], ct);
            var flags = FlagPattern().Matches(help).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
            log.LogInformation("Claude Code found at {Path}: {Version}", exe, version);
            return new ClaudeCodeStatus(true, version, exe, null, DateTime.UtcNow, flags);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or TimeoutException or InvalidOperationException)
        {
            log.LogWarning(ex, "Claude Code check failed for {Path}", exe);
            return new ClaudeCodeStatus(false, null, exe, ex.Message, DateTime.UtcNow, new HashSet<string>());
        }
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunAsync(string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        ClaudeCodeEnvironment.Apply(psi);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Claude Code.");
        p.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token);
            return (p.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"'{Path.GetFileName(exe)} {string.Join(' ', args)}' did not finish in {CommandTimeout.TotalSeconds:0} seconds.");
        }
    }

    /// <summary>The full path of the executable: <paramref name="configured"/> itself if it is a path, else the first match on PATH.</summary>
    public static string? ResolveExecutable(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        if (configured.Contains('/') || configured.Contains('\\'))
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;
        var exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';').Prepend("").ToArray()
            : [""];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, configured + ext);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s.Trim();

    [GeneratedRegex(@"(?<![\w-])--[a-z][a-z0-9-]+")]
    private static partial Regex FlagPattern();
}

/// <summary>
/// The environment Claude Code runs with: a clean one, without the server's secrets (no ANTHROPIC_API_KEY, connection
/// strings, cloud credentials…). Only what it needs to find its own install and login: HOME/USER (its config and the
/// account's keychain on macOS), PATH, locale and temp, and Claude Code's own config/token variables when set.
/// </summary>
public static class ClaudeCodeEnvironment
{
    private static readonly string[] PassThrough =
    [
        "HOME", "USER", "LOGNAME", "PATH", "LANG", "LC_ALL", "LC_CTYPE", "TMPDIR", "TZ", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME",
        "CLAUDE_CONFIG_DIR", "CLAUDE_CODE_OAUTH_TOKEN",
        // Windows
        "USERPROFILE", "APPDATA", "LOCALAPPDATA", "SystemRoot", "SystemDrive", "ComSpec", "PATHEXT", "TEMP", "TMP", "USERNAME",
    ];

    public static void Apply(System.Diagnostics.ProcessStartInfo psi)
    {
        var keep = PassThrough.Select(k => (k, Environment.GetEnvironmentVariable(k))).Where(x => x.Item2 != null).ToList();
        psi.Environment.Clear();
        foreach (var (k, v) in keep) psi.Environment[k] = v;
        psi.Environment["DISABLE_AUTOUPDATER"] = "1";
        psi.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
    }
}

/// <summary>Checks for Claude Code once at startup (in the background) when the editor may use it.</summary>
public sealed class ClaudeCodeStartupCheck(ClaudeCodeDetector detector, IOptions<EditorAiOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.Backend == EditorAiBackendKind.Api) return;
        try { await detector.GetAsync(refresh: true, stoppingToken); }
        catch (OperationCanceledException) { }
    }
}
