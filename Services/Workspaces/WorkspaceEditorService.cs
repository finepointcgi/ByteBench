using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Services.Accounts;
using ByteBench.Services.Audit;

namespace ByteBench.Services.Workspaces;

/// <summary>What the "Connect from your editor" panel and the REST endpoint show. Nothing here is a secret: the user's own public key is what authorizes the SSH login.</summary>
public sealed record EditorConnection(string Alias, string User, string Directory, string TunnelUrl, string TunnelCommand, string SshConfig,
    string KeyFingerprint, DateTimeOffset KeyExpiresAt, string? HostKeyFingerprint, IReadOnlyList<string> VsCodeSteps, IReadOnlyList<string> JetBrainsSteps,
    IReadOnlyList<string> Notes);

public sealed record EditorInfo(bool Enabled, string Alias, string User, string Directory, string TunnelUrl, string TunnelCommand, string? Problem);

/// <summary>A validated SSH public key, rewritten in a canonical form (no options, a fixed comment).</summary>
public sealed record SshPublicKey(string Type, string Base64, string Fingerprint)
{
    public string Canonical => $"{Type} {Base64}";
}

/// <summary>
/// Local editors (VS Code Remote-SSH, JetBrains Gateway) for a running workspace.
/// <para>
/// <b>Approach.</b> Both editors speak plain SSH, and the workspace container publishes nothing but the IDE port. So an OpenSSH server
/// runs <i>inside</i> the container as the unprivileged <c>coder</c> user, on <c>127.0.0.1:2222</c> (key authentication only, no
/// password, no root), and ByteBench reaches it the same way the preview proxy reaches any port: through the container. The editor never connects to
/// the container directly. It runs <c>bytebench workspace tunnel &lt;id&gt;</c> as its SSH <c>ProxyCommand</c>, which opens a WebSocket
/// to <c>/api/v1/workspaces/{id}/editor/tunnel</c> on ByteBench (so it works through the same reverse proxy and TLS as the web UI, with no
/// extra port) and pipes the SSH bytes through it. The tunnel needs a personal access token with <c>write_api</c> that belongs to the workspace's
/// owner; browser sessions can't open it (no cross-site WebSocket hijacking).
/// </para>
/// <para>
/// <b>Credentials.</b> The user's own public key is installed in the container's <c>authorized_keys</c> with an <c>expiry-time</c> of at most
/// <see cref="KeyLifetime"/> (and never beyond the workspace's own expiry). The key is rewritten into canonical form (no options, no comment),
/// so a crafted key can't smuggle <c>command=</c> or other options into the file. Nothing long-lived is shown in the UI.
/// </para>
/// Off by default: an administrator switches it on (<see cref="EnabledKey"/>). Only the owner can use any of it.
/// </summary>
public sealed partial class WorkspaceEditorService(WorkspaceService workspaces, IWorkspaceBackend backend, WorkspaceRuntime runtime, SiteSettings siteSettings,
    AuditLog audit, WorkspaceEditorTunnels tunnels, ProjectAccess access)
{
    public const string EnabledKey = "workspaces.editor.enabled";
    /// <summary>The port sshd listens on, on the container's loopback.</summary>
    public const int SshPort = 2222;
    public const string SshUser = "coder";
    public static readonly TimeSpan KeyLifetime = TimeSpan.FromHours(8);
    public const int MaxKeys = 5;

    /// <summary>The admin switch. Off unless an administrator turned it on: it opens a way into the container that the web IDE alone doesn't.</summary>
    public async Task<bool> IsEnabledAsync() => await siteSettings.GetAsync(EnabledKey) == "true";

    public async Task SetEnabledAsync(bool on, int adminId)
    {
        if (!await access.IsAdminAsync(adminId)) throw new ForbiddenException("Only site administrators can do this.");
        await siteSettings.SetAsync(EnabledKey, on ? "true" : "false", adminId);
        await audit.RecordAsync("workspace.admin_editor_switch", "workspaces", null, details: new { enabled = on }, actorId: adminId);
    }

    public static string AliasFor(string publicId) => "bytebench-ws-" + publicId[..8];

    public static string TunnelPath(string publicId) => $"/api/v1/workspaces/{publicId}/editor/tunnel";

    /// <summary>What the panel shows before any key is registered.</summary>
    public async Task<EditorInfo> GetInfoAsync(string publicId, int userId, string baseUrl)
    {
        var info = await workspaces.GetMineAsync(publicId, userId) ?? throw new InvalidOperationException("Workspace not found.");
        var enabled = await IsEnabledAsync();
        var problem = !enabled ? "Connecting a local editor is turned off on this server. A site administrator can turn it on under Admin → Workspaces."
            : info.Workspace.State != WorkspaceState.Running ? "The workspace isn't running."
            : null;
        return new EditorInfo(enabled && problem == null, AliasFor(publicId), SshUser, WorkspaceLifecycle.WorkDir(info.RepoSlug), TunnelUrl(baseUrl, publicId),
            TunnelCommand(publicId), problem);
    }

    /// <summary>
    /// Installs the user's public key in the container (expiring), makes sure the SSH server is running and returns the instructions.
    /// Refused when the switch is off, for anyone but the owner, and for anything but a running workspace.
    /// </summary>
    public async Task<EditorConnection> ConnectAsync(string publicId, int userId, string publicKey, string baseUrl)
    {
        if (!await IsEnabledAsync()) throw new InvalidOperationException("Connecting a local editor is turned off on this server (Admin → Workspaces).");
        var key = ParsePublicKey(publicKey);
        var (live, ws) = await workspaces.GetRunningAsync(publicId, userId);
        live.Touch();
        var expires = DateTimeOffset.UtcNow + KeyLifetime;
        var wsEnd = DateTime.SpecifyKind(ws.ExpiresAt, DateTimeKind.Utc);
        if (wsEnd < expires.UtcDateTime) expires = new DateTimeOffset(wsEnd);
        if (expires <= DateTimeOffset.UtcNow.AddMinutes(1)) throw new InvalidOperationException("This workspace is about to expire.");

        var line = $"expiry-time=\"{expires.UtcDateTime:yyyyMMddHHmmss}Z\" {key.Canonical} bytebench-workspace";
        var install = await backend.ExecAsync(live.ContainerId, new ExecSpec(["sh", "-c", InstallKeyScript, "sh", key.Base64, MaxKeys.ToString()])
            { Stdin = line + "\n", Timeout = TimeSpan.FromSeconds(30) }, CancellationToken.None);
        if (!install.Ok) throw new InvalidOperationException("Couldn't set up SSH in the workspace: " + WorkspaceLifecycle.Safe(install.Output));
        var start = await backend.ExecAsync(live.ContainerId, new ExecSpec(["sh", "-c", StartSshdScript, "sh", SshPort.ToString()]) { Timeout = TimeSpan.FromSeconds(30) }, CancellationToken.None);
        if (!start.Ok)
            throw new InvalidOperationException("The SSH server in the workspace didn't start" + (start.Output.Contains("No such file", StringComparison.OrdinalIgnoreCase)
                ? ": the workspace image has no OpenSSH server. Rebuild it from docker/workspace." : ": " + WorkspaceLifecycle.Safe(start.Output)));
        var hostKey = HostKeyLine().Match(start.Output) is { Success: true } m ? m.Groups[1].Value : null;

        await audit.RecordAsync("workspace.editor_key_added", "workspace", ws.PublicId, ws.ProjectId,
            new { fingerprint = key.Fingerprint, expires = expires.UtcDateTime }, userId);

        var dir = WorkspaceLifecycle.WorkDir(ws.Repository.Slug);
        var alias = AliasFor(publicId);
        var config = SshConfigFor(alias, publicId);
        return new EditorConnection(alias, SshUser, dir, TunnelUrl(baseUrl, publicId), TunnelCommand(publicId), config, key.Fingerprint, expires, hostKey,
            VsCodeStepsFor(alias, dir), JetBrainsStepsFor(alias, dir),
            [
                "Your public key works for this workspace only, and stops working at " + expires.UtcDateTime.ToString("u") + " or when the workspace stops.",
                "The first connection downloads the editor's remote components (VS Code Server or the JetBrains backend) inside the workspace, so it needs internet access: it fails when the server's workspace network mode is 'none'.",
                "Everything you open or change is inside the workspace. Push to keep it.",
            ]);
    }

    public static string TunnelCommand(string publicId) => $"bytebench workspace tunnel {publicId}";

    private static string TunnelUrl(string baseUrl, string publicId)
    {
        var b = baseUrl.TrimEnd('/');
        b = b.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss://" + b[8..] : b.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "ws://" + b[7..] : b;
        return b + TunnelPath(publicId);
    }

    public static string SshConfigFor(string alias, string publicId) => string.Join('\n',
    [
        $"Host {alias}",
        "  HostName workspace.invalid",
        $"  User {SshUser}",
        "  IdentityFile ~/.ssh/id_ed25519",
        "  IdentitiesOnly yes",
        $"  ProxyCommand {TunnelCommand(publicId)}",
        "  StrictHostKeyChecking accept-new",
        "  ServerAliveInterval 30",
    ]);

    private static IReadOnlyList<string> VsCodeStepsFor(string alias, string dir) =>
    [
        "Install VS Code and its \"Remote - SSH\" extension (ms-vscode-remote.remote-ssh).",
        "Install the bytebench command line and sign in once: bytebench login --server <this server>.",
        $"Add the SSH configuration above to ~/.ssh/config (use your own private key path for IdentityFile).",
        $"In VS Code run \"Remote-SSH: Connect to Host…\" and choose {alias}, then open the folder {dir}.",
    ];

    private static IReadOnlyList<string> JetBrainsStepsFor(string alias, string dir) =>
    [
        "Install JetBrains Gateway (or use the Remote Development entry of your JetBrains IDE).",
        "Install the bytebench command line and sign in once: bytebench login --server <this server>.",
        "Add the SSH configuration above to ~/.ssh/config (use your own private key path for IdentityFile).",
        $"In Gateway choose SSH → New Connection, pick \"Specify connection details\" → \"OpenSSH config\" and select {alias} (user {SshUser}).",
        $"Choose your IDE and the project directory {dir}, then Download IDE and Connect.",
    ];

    /// <summary>
    /// Opens the byte stream to the container's SSH server for the tunnel endpoint. Same checks as <see cref="ConnectAsync"/> (switch, owner, running),
    /// plus a cap on simultaneous tunnels per workspace. Dispose the returned lease when the connection ends.
    /// </summary>
    public async Task<(Stream Stream, IDisposable Lease)> OpenTunnelAsync(string publicId, int userId, CancellationToken ct)
    {
        if (!await IsEnabledAsync()) throw new InvalidOperationException("Connecting a local editor is turned off on this server (Admin → Workspaces).");
        var (live, _) = await workspaces.GetRunningAsync(publicId, userId);
        var lease = tunnels.TryAcquire(live.Id) ?? throw new InvalidOperationException("Too many editor connections to this workspace.");
        try
        {
            live.Touch();
            var stream = await backend.ConnectAsync(live.Endpoint, SshPort, ct);
            return (stream, lease);
        }
        catch { lease.Dispose(); throw; }
    }

    /// <summary>Marks the workspace as in use (an open editor session counts as activity, so the idle reaper leaves it alone).</summary>
    public void Touch(string publicId) => runtime.Find(publicId)?.Touch();

    // ---------- public key validation ----------

    private static readonly HashSet<string> KeyTypes = new(StringComparer.Ordinal)
    {
        "ssh-ed25519", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521", "sk-ssh-ed25519@openssh.com", "sk-ecdsa-sha2-nistp256@openssh.com", "ssh-rsa",
    };

    /// <summary>
    /// Accepts one OpenSSH public key line (<c>type base64 [comment]</c>) and nothing else: no authorized_keys options, no second line, no private key,
    /// a base64 blob whose embedded type matches, and for RSA at least 2048 bits. The result is rebuilt from the parts, never the raw text.
    /// </summary>
    public static SshPublicKey ParsePublicKey(string? text)
    {
        var raw = (text ?? "").Trim();
        if (raw.Length is 0 or > 2000) throw new InvalidOperationException("Paste your SSH public key (one line starting with ssh-ed25519, ecdsa-sha2-… or ssh-rsa).");
        if (raw.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("That is a private key. Never paste it anywhere: paste the public key (the .pub file) instead.");
        if (raw.Any(char.IsControl)) throw new InvalidOperationException("The public key must be a single line.");
        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !KeyTypes.Contains(parts[0]))
            throw new InvalidOperationException("That doesn't look like an SSH public key. It should start with ssh-ed25519, ecdsa-sha2-… or ssh-rsa (key options aren't accepted).");
        byte[] blob;
        try { blob = Convert.FromBase64String(parts[1]); }
        catch (FormatException) { throw new InvalidOperationException("The public key isn't valid base64."); }
        if (blob.Length is < 16 or > 1500 || Convert.ToBase64String(blob) != parts[1]) throw new InvalidOperationException("The public key isn't valid.");
        // The blob starts with its own type name (uint32 length + bytes); it must agree with the declared type.
        var typeLength = (blob[0] << 24) | (blob[1] << 16) | (blob[2] << 8) | blob[3];
        if (typeLength is < 1 or > 64 || 4 + typeLength > blob.Length || Encoding.ASCII.GetString(blob, 4, typeLength) != parts[0])
            throw new InvalidOperationException("The public key's type doesn't match its contents.");
        if (parts[0] == "ssh-rsa" && RsaBits(blob, typeLength) < 2048) throw new InvalidOperationException("RSA keys must be at least 2048 bits. Use an ed25519 key: ssh-keygen -t ed25519.");
        var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
        return new SshPublicKey(parts[0], parts[1], fingerprint);
    }

    private static int RsaBits(byte[] blob, int typeLength)
    {
        // ssh-rsa: string "ssh-rsa", mpint e, mpint n.
        var o = 4 + typeLength;
        if (o + 4 > blob.Length) return 0;
        var eLen = (blob[o] << 24) | (blob[o + 1] << 16) | (blob[o + 2] << 8) | blob[o + 3];
        o += 4 + eLen;
        if (eLen < 0 || o + 4 > blob.Length) return 0;
        var nLen = (blob[o] << 24) | (blob[o + 1] << 16) | (blob[o + 2] << 8) | blob[o + 3];
        o += 4;
        if (nLen < 0 || o + nLen > blob.Length) return 0;
        while (nLen > 0 && blob[o] == 0) { o++; nLen--; } // the leading zero byte of a positive mpint
        return nLen * 8;
    }

    [GeneratedRegex(@"^BB_HOSTKEY (SHA256:[A-Za-z0-9+/]+)\s*$", RegexOptions.Multiline)]
    private static partial Regex HostKeyLine();

    // Reads the (already validated, canonical) authorized_keys line from stdin; $1 is its base64 blob, $2 how many keys to keep.
    private const string InstallKeyScript = """
        set -e
        umask 077
        d="$HOME/.ssh"
        mkdir -p "$d"
        chmod 700 "$d"
        f="$d/authorized_keys"
        new=$(cat)
        touch "$f"
        { tail -n "$(( $2 - 1 ))" "$f" | grep -vF -- "$1" || true; printf '%s\n' "$new"; } > "$f.new"
        mv "$f.new" "$f"
        """;

    // Idempotent: writes a locked-down config under ~/.bb-ssh and starts sshd (as coder, on loopback) unless it already runs. $1 is the port.
    private const string StartSshdScript = """
        set -e
        umask 077
        d="$HOME/.bb-ssh"
        mkdir -p "$d"
        [ -f "$d/host_ed25519" ] || ssh-keygen -q -t ed25519 -N '' -C '' -f "$d/host_ed25519"
        printf '%s\n' \
          "Port $1" "ListenAddress 127.0.0.1" "HostKey $d/host_ed25519" "PidFile $d/sshd.pid" \
          "AuthorizedKeysFile $HOME/.ssh/authorized_keys" "AuthenticationMethods publickey" \
          "PasswordAuthentication no" "KbdInteractiveAuthentication no" "PubkeyAuthentication yes" \
          "PermitRootLogin no" "AllowUsers coder" "UsePAM no" "PermitUserEnvironment no" "PermitEmptyPasswords no" \
          "X11Forwarding no" "AllowAgentForwarding no" "AllowTcpForwarding yes" "GatewayPorts no" "PrintMotd no" \
          "MaxAuthTries 3" "LoginGraceTime 20" "MaxStartups 10:50:20" "StrictModes yes" "LogLevel ERROR" \
          "Subsystem sftp internal-sftp" > "$d/sshd_config"
        if [ -f "$d/sshd.pid" ] && kill -0 "$(cat "$d/sshd.pid")" 2>/dev/null; then :; else /usr/sbin/sshd -f "$d/sshd_config"; fi
        echo "BB_HOSTKEY $(ssh-keygen -lf "$d/host_ed25519.pub" | cut -d' ' -f2)"
        """;
}

/// <summary>Counts the editor tunnels open per workspace (a cap, so one workspace can't be used to open unbounded relays).</summary>
public sealed class WorkspaceEditorTunnels
{
    public const int MaxPerWorkspace = 8;
    private readonly ConcurrentDictionary<int, int> open = new();

    public IDisposable? TryAcquire(int workspaceId)
    {
        var count = open.AddOrUpdate(workspaceId, 1, (_, c) => c + 1);
        if (count > MaxPerWorkspace) { Release(workspaceId); return null; }
        return new Lease(this, workspaceId);
    }

    public int Count(int workspaceId) => open.TryGetValue(workspaceId, out var c) ? c : 0;

    private void Release(int workspaceId) => open.AddOrUpdate(workspaceId, 0, (_, c) => Math.Max(0, c - 1));

    private sealed class Lease(WorkspaceEditorTunnels owner, int id) : IDisposable
    {
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) owner.Release(id);
        }
    }
}
