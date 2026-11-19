using System.Security.Cryptography;
using Microsoft.DevTunnels.Ssh.Algorithms;

namespace ByteBench.Services.Ssh;

/// <summary>Configuration section <c>Ssh</c>: the embedded SSH server for git (<see cref="SshGitServer"/>).</summary>
public sealed class SshOptions
{
    public const string Section = "Ssh";

    /// <summary>Run the SSH server. Off by default; on in <c>appsettings.Development.json</c> and the Docker image.</summary>
    public bool Enabled { get; set; }
    /// <summary>TCP port to listen on (0 picks a free port, for tests).</summary>
    public int Port { get; set; } = 2222;
    /// <summary>Address to listen on; default all interfaces (IPv6 and IPv4).</summary>
    public string? ListenAddress { get; set; }
    /// <summary>The host key (PEM). Generated on first run; default <c>{DataProtection:KeysPath}/ssh_host_rsa_key</c>.</summary>
    public string? HostKeyPath { get; set; }
    /// <summary>Host name shown in SSH clone URLs; default the host of the web request.</summary>
    public string? PublicHost { get; set; }
    /// <summary>Port shown in SSH clone URLs (e.g. 22 when a load balancer or Docker maps 22 to <see cref="Port"/>); default <see cref="Port"/>.</summary>
    public int? PublicPort { get; set; }
    /// <summary>Simultaneous SSH connections; more are dropped.</summary>
    public int MaxConnections { get; set; } = 200;
    /// <summary>A connection that hasn't authenticated after this many seconds is closed.</summary>
    public int AuthTimeoutSeconds { get; set; } = 30;

    public int EffectivePublicPort => PublicPort ?? Port;
}

/// <summary>The SSH host key: RSA 3072, generated on first run and kept as PKCS#8 PEM (mode 600) at <see cref="SshOptions.HostKeyPath"/>.</summary>
internal static class SshHostKey
{
    public static string DefaultPath(IConfiguration config, IHostEnvironment env) =>
        Path.GetFullPath(config["Ssh:HostKeyPath"] is { Length: > 0 } p ? p
            : Path.Combine(config["DataProtection:KeysPath"] is { Length: > 0 } kp ? kp : Path.Combine(env.ContentRootPath, "keys"), "ssh_host_rsa_key"));

    public static RSA LoadOrCreate(string path, ILogger logger)
    {
        var rsa = RSA.Create();
        if (File.Exists(path))
        {
            rsa.ImportFromPem(File.ReadAllText(path));
            return rsa;
        }

        rsa.Dispose();
        rsa = RSA.Create(3072);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, rsa.ExportPkcs8PrivateKeyPem() + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, overwrite: false);
        logger.LogInformation("Generated a new SSH host key at {Path}", path);
        return rsa;
    }

    /// <summary>The <c>SHA256:…</c> fingerprint clients show on first connect.</summary>
    public static string Fingerprint(RSA rsa)
    {
        using var pair = new Rsa.KeyPair(RSA.Create(rsa.ExportParameters(false)));
        return SshPublicKey.FingerprintOf(pair.GetPublicKeyBytes().ToArray());
    }
}
