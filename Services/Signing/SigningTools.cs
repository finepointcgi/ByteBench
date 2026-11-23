using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Signing;

/// <summary>Configuration section <c>Signing</c>: signed commit verification.</summary>
public class SigningOptions
{
    /// <summary>The <c>gpg</c> executable (a name looked up on PATH, or a full path). Without it OpenPGP signatures are "unverifiable".</summary>
    public string GpgPath { get; set; } = "gpg";
    /// <summary>The <c>ssh-keygen</c> executable (OpenSSH 8.0+). Without it SSH signatures are "unverifiable".</summary>
    public string SshKeygenPath { get; set; } = "ssh-keygen";
    /// <summary>Cached verification results older than this are checked again (key changes also invalidate them at once).</summary>
    public int CacheHours { get; set; } = 12;
    /// <summary>Wall-clock limit of one gpg / ssh-keygen run.</summary>
    public int ToolTimeoutSeconds { get; set; } = 15;
    /// <summary>Most commits whose signature is checked when a page or a push asks for many (the rest are reported unchecked).</summary>
    public int MaxCommitsPerCheck { get; set; } = 300;
}

/// <summary>The outcome of one external tool run.</summary>
public sealed record ToolResult(int ExitCode, string Stdout, string Stderr, bool TimedOut)
{
    public bool Ok => ExitCode == 0 && !TimedOut;
}

/// <summary>
/// Finds and runs <c>gpg</c> and <c>ssh-keygen</c>. Every run gets a throw-away home directory (GNUPGHOME / HOME): the machine's
/// real keyring, <c>~/.ssh</c> and agents are never read, so only the keys ByteBench writes for that run can verify anything.
/// </summary>
public class SigningTools(IOptions<SigningOptions> options)
{
    private readonly SigningOptions opts = options.Value;
    private string? gpg, sshKeygen;
    private bool gpgLooked, sshLooked;

    public string? GpgPath => gpgLooked ? gpg : (gpg = Locate(opts.GpgPath, "gpg", ref gpgLooked));
    public string? SshKeygenPath => sshLooked ? sshKeygen : (sshKeygen = Locate(opts.SshKeygenPath, "ssh-keygen", ref sshLooked));
    public bool GpgAvailable => GpgPath != null;
    public bool SshAvailable => SshKeygenPath != null;

    private static string? Locate(string configured, string fallbackName, ref bool flag)
    {
        flag = true;
        var name = string.IsNullOrWhiteSpace(configured) ? fallbackName : configured.Trim();
        if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
        var names = OperatingSystem.IsWindows() ? new[] { name + ".exe", name } : new[] { name };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var n in names)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim('"'), n);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { /* malformed PATH entry */ }
            }
        }
        return null;
    }

    /// <summary>A new private (0700) temporary directory; delete it with <see cref="Delete"/>.</summary>
    public static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bytebench-sign-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return dir;
    }

    public static void Delete(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a leftover temp dir is harmless */ }
    }

    /// <summary>Runs gpg with <paramref name="home"/> as its (empty or throw-away) home and never any agent, dirmngr or network.</summary>
    public Task<ToolResult> RunGpgAsync(string home, IEnumerable<string> args, byte[]? stdin, CancellationToken ct)
    {
        var gnupg = Path.Combine(home, "gnupg");
        Directory.CreateDirectory(gnupg);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(gnupg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string[] common = ["--homedir", gnupg, "--batch", "--no-tty", "--no-autostart", "--disable-dirmngr", "--no-auto-key-retrieve",
            "--no-auto-check-trustdb", "--no-options", "--trust-model", "always"];
        return RunAsync(GpgPath ?? "gpg", common.Concat(args), stdin, home, new Dictionary<string, string> { ["GNUPGHOME"] = gnupg, ["LANG"] = "C", ["LC_ALL"] = "C" }, ct);
    }

    public Task<ToolResult> RunSshKeygenAsync(string home, IEnumerable<string> args, byte[]? stdin, CancellationToken ct) =>
        RunAsync(SshKeygenPath ?? "ssh-keygen", args, stdin, home, new Dictionary<string, string> { ["LANG"] = "C", ["LC_ALL"] = "C" }, ct);

    private async Task<ToolResult> RunAsync(string exe, IEnumerable<string> args, byte[]? stdin, string home, IDictionary<string, string> env, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = home,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Start from a nearly empty environment: no SSH_AUTH_SOCK, no GPG_AGENT_INFO, no real HOME.
        psi.Environment.Clear();
        if (Environment.GetEnvironmentVariable("PATH") is { } path) psi.Environment["PATH"] = path;
        psi.Environment["HOME"] = home;
        foreach (var (k, v) in env) psi.Environment[k] = v;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, opts.ToolTimeoutSeconds)));
        Process? process = null;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                if (stdin is { Length: > 0 }) await process.StandardInput.BaseStream.WriteAsync(stdin, timeout.Token);
                process.StandardInput.Close();
            }
            catch (IOException) { /* the tool exited before reading its input */ }
            await process.WaitForExitAsync(timeout.Token);
            return new ToolResult(process.ExitCode, await stdout, await stderr, false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process?.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return new ToolResult(-1, "", "timed out", true);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new ToolResult(-1, "", ex.Message, false);
        }
        finally { process?.Dispose(); }
    }
}
