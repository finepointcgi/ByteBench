using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace ByteBench.Services.Mirroring;

/// <summary>
/// Encrypts mirror passwords and tokens at rest with ASP.NET Core Data Protection (like <see cref="Webhooks.WebhookSecrets"/>).
/// Losing the key ring makes them unreadable: syncs then fail with a clear error until someone enters the token again.
/// </summary>
public sealed class MirrorCredentials(IDataProtectionProvider provider)
{
    // Stored identifier: keep "GitBench" (the product was renamed to ByteBench). Changing it would make existing encrypted data/tokens unreadable.
    public const string Purpose = "GitBench.Mirroring.Credentials.v1";
    private readonly IDataProtector protector = provider.CreateProtector(Purpose);

    public string Protect(string secret) => protector.Protect(secret);

    /// <summary>The plaintext secret; throws <see cref="MirrorException"/> if it can't be decrypted.</summary>
    public string Reveal(string stored)
    {
        try { return protector.Unprotect(stored); }
        catch (CryptographicException) { throw new MirrorException("The saved password or token can't be decrypted (the server's keys changed). Enter it again."); }
    }
}

/// <summary>
/// Runs git for a mirror: <c>ArgumentList</c> (no shell), a timeout that kills the process tree, and an environment that
/// ignores the server's system and global git config. Credentials go through a temporary credential helper whose answer
/// file is mode 600 in a mode 700 directory, never through the URL or the command line, and are deleted afterwards.
/// </summary>
internal sealed class MirrorGit(string gitPath, MirrorOptions options, string contentRoot, ILogger log)
{
    public sealed record Output(int ExitCode, string StdOut, string StdErr);

    /// <summary>The transports git may use (<c>GIT_ALLOW_PROTOCOL</c>). Only tests widen this (to read a local repository).</summary>
    public string AllowedProtocols { get; init; } = "http:https:ssh";

    /// <summary>A prepared remote: the URL plus the environment that pins its address and supplies credentials.</summary>
    public sealed class Remote : IDisposable
    {
        public required string Url { get; init; }
        public Dictionary<string, string> Environment { get; } = new();
        public List<(string Key, string Value)> Config { get; } = [];
        public string? TempDir { get; set; }

        public void Dispose()
        {
            if (TempDir == null) return;
            try { Directory.Delete(TempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Builds the remote for <paramref name="uri"/>: pinned to <paramref name="pin"/> (if any), with credentials (if any).</summary>
    public Remote Prepare(Uri uri, IPAddress? pin, string? username, string? secret)
    {
        var remote = new Remote { Url = uri.OriginalString };
        try
        {
            if (uri.Scheme is "http" or "https")
            {
                if (pin != null)
                {
                    var port = uri.IsDefaultPort ? (uri.Scheme == "https" ? 443 : 80) : uri.Port;
                    var address = pin.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{pin}]" : pin.ToString();
                    remote.Config.Add(("http.curloptResolve", $"{uri.IdnHost}:{port}:{address}"));
                }
                if (!string.IsNullOrEmpty(secret))
                {
                    var user = string.IsNullOrWhiteSpace(username) ? "git" : username.Trim();
                    if (user.Any(char.IsControl) || secret.Any(char.IsControl)) throw new MirrorException("The username or token contains invalid characters.");
                    remote.TempDir = CreatePrivateDirectory();
                    var file = Path.Combine(remote.TempDir, "credentials");
                    WritePrivateFile(file, $"username={user}\npassword={secret}\n");
                    remote.Environment["BYTEBENCH_MIRROR_CREDENTIALS"] = file;
                    // Answers "get" from the file; "store"/"erase" do nothing. The script holds no secret, only the variable name.
                    remote.Config.Add(("credential.helper", "!f() { test \"$1\" = get && cat \"$BYTEBENCH_MIRROR_CREDENTIALS\"; }; f"));
                }
            }
            else // ssh
            {
                var ssh = new List<string>
                {
                    "ssh", "-F", "/dev/null", "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes", "-o", "IdentityAgent=none",
                    "-o", "StrictHostKeyChecking=accept-new", "-o", "ConnectTimeout=30",
                    "-o", "UserKnownHostsFile=" + KnownHostsFile(),
                };
                ssh.AddRange(!string.IsNullOrWhiteSpace(options.SshKeyFile) && File.Exists(options.SshKeyFile)
                    ? ["-o", "IdentityFile=" + options.SshKeyFile]
                    : ["-o", "IdentityFile=none"]);
                if (pin != null) ssh.AddRange(["-o", "HostName=" + pin]); // known_hosts still checks the name in the URL
                remote.Environment["GIT_SSH_COMMAND"] = string.Join(' ', ssh.Select(Quote));
                remote.Environment["GIT_SSH_VARIANT"] = "ssh";
            }
            return remote;
        }
        catch
        {
            remote.Dispose();
            throw;
        }
    }

    /// <summary>Runs <c>git --git-dir=&lt;repoPath&gt; args…</c> against <paramref name="remote"/>.</summary>
    public async Task<Output> RunAsync(string repoPath, Remote? remote, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(gitPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = repoPath,
        };
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase) || k == "SSH_AUTH_SOCK").ToList())
            psi.Environment.Remove(key);
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_ALLOW_PROTOCOL"] = AllowedProtocols;
        psi.Environment["GIT_ASKPASS"] = "";
        psi.Environment["SSH_ASKPASS"] = "";
        psi.Environment["LC_ALL"] = "C";

        // Config through the environment: never on the command line. An empty credential.helper first drops any helper
        // (e.g. a keychain) git would otherwise use, so the token is never stored anywhere.
        var config = new List<(string, string)> { ("credential.helper", ""), ("http.followRedirects", "false"), ("core.askPass", "") };
        if (remote != null)
        {
            config.AddRange(remote.Config);
            foreach (var (k, v) in remote.Environment) psi.Environment[k] = v;
        }
        psi.Environment["GIT_CONFIG_COUNT"] = config.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < config.Count; i++)
        {
            psi.Environment[$"GIT_CONFIG_KEY_{i}"] = config[i].Item1;
            psi.Environment[$"GIT_CONFIG_VALUE_{i}"] = config[i].Item2;
        }

        psi.ArgumentList.Add("--git-dir=" + repoPath);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, options.TimeoutSeconds)));
        await using var kill = timeout.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { log.LogDebug(ex, "Could not kill git"); }
        });
        var stdout = ReadLimitedAsync(process.StandardOutput);
        var stderr = ReadLimitedAsync(process.StandardError);
        await process.WaitForExitAsync(CancellationToken.None);
        var output = new Output(process.ExitCode, await stdout, await stderr);
        if (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
            throw new MirrorException($"git {args[0]} timed out after {options.TimeoutSeconds} seconds.");
        ct.ThrowIfCancellationRequested();
        return output;
    }

    /// <summary>Reads a stream to the end, keeping at most 1 MB.</summary>
    private static async Task<string> ReadLimitedAsync(StreamReader reader)
    {
        var sb = new StringBuilder();
        var buffer = new char[8192];
        int n;
        while ((n = await reader.ReadAsync(buffer)) > 0)
            if (sb.Length < 1024 * 1024) sb.Append(buffer, 0, n);
        return sb.ToString();
    }

    private string KnownHostsFile()
    {
        var file = options.KnownHostsFile;
        if (string.IsNullOrWhiteSpace(file)) file = Path.Combine(contentRoot, "keys", "mirror_known_hosts");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        return Path.GetFullPath(file);
    }

    private static string Quote(string s) => s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or '=' or ':' or ',' or '@')
        ? s : "'" + s.Replace("'", "'\\''") + "'";

    private static string CreatePrivateDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bytebench-mirror-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return dir;
    }

    private static void WritePrivateFile(string path, string content)
    {
        var o = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) o.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; // 600
        using var f = new FileStream(path, o);
        f.Write(Encoding.UTF8.GetBytes(content));
    }
}
