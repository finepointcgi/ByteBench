using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GitBench.Services.Git;
using GitBench.Services.Hosting;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.IO;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.Extensions.Options;
using SshBuffer = Microsoft.DevTunnels.Ssh.Buffer;

namespace GitBench.Services.Ssh;

/// <summary>
/// The embedded SSH server for git: <c>git clone ssh://git@host:2222/WEB/web-app.git</c>. Built on Microsoft.DevTunnels.Ssh
/// (Microsoft's maintained, async SSH library with server sessions, public-key auth and exec channels), with ed25519 client
/// keys verified through <see cref="Ed25519Algorithm"/>.
/// <list type="bullet">
/// <item>Public-key authentication only (<see cref="SshKeyService.AuthenticateAsync"/>); any user name is accepted (use <c>git</c>).</item>
/// <item>Only the exec commands <c>git-upload-pack '&lt;KEY&gt;/&lt;slug&gt;.git'</c> and <c>git-receive-pack '…'</c> run
/// (<see cref="SshGitCommand"/>). Shells get a friendly message; PTYs, port forwarding, agent/X11 forwarding and subsystems
/// are refused.</item>
/// </list>
/// </summary>
public sealed partial class SshGitServer(IOptions<SshOptions> options, IServiceScopeFactory scopes, IConfiguration config, IHostEnvironment env,
    AppRateLimits rateLimits, RepoStorage storage, ILoggerFactory loggerFactory) : BackgroundService
{
    private readonly ILogger log = loggerFactory.CreateLogger("GitBench.Ssh");
    private readonly ConcurrentDictionary<SshServerSession, byte> sessions = new();
    private TcpListener? listener;
    private SshSessionConfiguration? sessionConfig;
    private SshServerCredentials? credentials;
    private string? gitPath;

    /// <summary>The port actually bound (useful with <c>Ssh:Port=0</c>), or null when the server isn't running.</summary>
    public int? BoundPort { get; private set; }

    /// <summary>The host key fingerprint (<c>SHA256:…</c>), or null when the server isn't running.</summary>
    public string? HostKeyFingerprint { get; private set; }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (!o.Enabled) return Task.CompletedTask;

        gitPath = GitHttpBackend.LocateGit(config);
        if (gitPath == null) log.LogError("git executable not found: git over SSH will refuse every command until git is installed.");

        var keyPath = SshHostKey.DefaultPath(config, env);
        RSA hostKey;
        try { hostKey = SshHostKey.LoadOrCreate(keyPath, log); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            log.LogError(ex, "Could not load or create the SSH host key at {Path}; the SSH server is not started", keyPath);
            return Task.CompletedTask;
        }
        HostKeyFingerprint = SshHostKey.Fingerprint(hostKey);
        credentials = new SshServerCredentials(new Rsa.KeyPair(hostKey));
        sessionConfig = CreateConfiguration();

        try
        {
            if (o.ListenAddress is { Length: > 0 } addr)
                listener = new TcpListener(IPAddress.Parse(addr), o.Port);
            else
            {
                listener = new TcpListener(IPAddress.IPv6Any, o.Port);
                listener.Server.DualMode = true;
            }
            listener.Start();
        }
        catch (Exception ex) when (ex is SocketException or FormatException)
        {
            log.LogError(ex, "The SSH server could not listen on port {Port}; git over SSH is unavailable", o.Port);
            listener = null;
            return Task.CompletedTask;
        }
        BoundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        log.LogInformation("Git over SSH listening on port {Port} (host key {Fingerprint})", BoundPort, HostKeyFingerprint);
        return base.StartAsync(cancellationToken);
    }

    /// <summary>Public-key auth only; RSA/ECDSA from the library plus ed25519; the library's ciphers, MACs and key exchanges.</summary>
    private static SshSessionConfiguration CreateConfiguration()
    {
        var c = new SshSessionConfiguration();
        c.AuthenticationMethods.Clear();
        c.AuthenticationMethods.Add("publickey");
        c.PublicKeyAlgorithms.Clear();
        c.PublicKeyAlgorithms.Add(new Ed25519Algorithm());
        c.PublicKeyAlgorithms.Add(SshAlgorithms.PublicKey.ECDsaSha2Nistp256);
        c.PublicKeyAlgorithms.Add(SshAlgorithms.PublicKey.ECDsaSha2Nistp384);
        c.PublicKeyAlgorithms.Add(SshAlgorithms.PublicKey.ECDsaSha2Nistp521);
        c.PublicKeyAlgorithms.Add(SshAlgorithms.PublicKey.RsaWithSha512);
        c.PublicKeyAlgorithms.Add(SshAlgorithms.PublicKey.RsaWithSha256);
        c.ProtocolExtensions.Remove(SshProtocolExtensionNames.OpenChannelRequest);
        c.MaxClientAuthenticationAttempts = 6;
        return c;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var stop = stoppingToken.Register(() => listener?.Stop());
        while (!stoppingToken.IsCancellationRequested)
        {
            Socket socket;
            try { socket = await listener!.AcceptSocketAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                if (stoppingToken.IsCancellationRequested) break;
                log.LogWarning(ex, "SSH accept failed");
                continue;
            }
            if (sessions.Count >= options.Value.MaxConnections)
            {
                log.LogWarning("Too many SSH connections ({Count}); dropping {Remote}", sessions.Count, socket.RemoteEndPoint);
                socket.Dispose();
                continue;
            }
            _ = Task.Run(() => HandleConnectionAsync(socket, stoppingToken), CancellationToken.None);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        listener?.Stop();
        foreach (var s in sessions.Keys)
        {
            try { await s.CloseAsync(SshDisconnectReason.ByApplication, "GitBench is shutting down."); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { log.LogDebug(ex, "Closing SSH session"); }
        }
        await base.StopAsync(cancellationToken);
    }

    /// <summary>Per-connection state: the client address and, once a signature is verified, who the key belongs to.</summary>
    private sealed class Connection(string remote)
    {
        public string Remote { get; } = remote;
        public SshIdentity? Identity { get; set; }
    }

    private async Task HandleConnectionAsync(Socket socket, CancellationToken stoppingToken)
    {
        var conn = new Connection(socket.RemoteEndPoint is IPEndPoint ep ? ep.Address.ToString() : "unknown");
        socket.NoDelay = true;
        var stream = new NetworkStream(socket, ownsSocket: true);
        var session = new SshServerSession(sessionConfig!, new TraceSource("GitBench.Ssh.Library", SourceLevels.Off)) { Credentials = credentials! };
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sessions[session] = 0;
        session.Closed += (_, e) =>
        {
            if (e.Exception != null && e.Reason != SshDisconnectReason.ByApplication)
                log.LogDebug(e.Exception, "SSH session from {Remote} closed: {Reason}", conn.Remote, e.Reason);
            closed.TrySetResult();
        };
        session.Authenticating += (_, e) => e.AuthenticationTask = AuthenticateAsync(conn, e);
        session.ChannelOpening += (_, e) => OnChannelOpening(conn, e, stoppingToken);
        // Global requests (tcpip-forward for "ssh -R", …) have no handler, so they are refused.
        try
        {
            await session.ConnectAsync(stream, stoppingToken);
            var timeout = Task.Delay(TimeSpan.FromSeconds(Math.Max(5, options.Value.AuthTimeoutSeconds)), stoppingToken);
            if (await Task.WhenAny(closed.Task, timeout) == timeout && conn.Identity == null && !closed.Task.IsCompleted)
            {
                log.LogInformation("SSH connection from {Remote} didn't authenticate in time", conn.Remote);
                await session.CloseAsync(SshDisconnectReason.NoMoreAuthMethodsAvailable, "Authentication timed out.");
            }
            await closed.Task.WaitAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is SshConnectionException or IOException or SocketException or ObjectDisposedException)
        {
            log.LogDebug(ex, "SSH connection from {Remote} failed", conn.Remote);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "SSH connection from {Remote} failed", conn.Remote);
        }
        finally
        {
            sessions.TryRemove(session, out _);
            session.Dispose();
            await stream.DisposeAsync();
        }
    }

    private async Task<ClaimsPrincipal?> AuthenticateAsync(Connection conn, SshAuthenticatingEventArgs e)
    {
        if (e.AuthenticationType is not (SshAuthenticationType.ClientPublicKey or SshAuthenticationType.ClientPublicKeyQuery) || e.PublicKey == null)
            return null;
        try
        {
            var fingerprint = SshPublicKey.FingerprintOf(e.PublicKey.GetPublicKeyBytes().ToArray());
            var verified = e.AuthenticationType == SshAuthenticationType.ClientPublicKey; // signature checked by the library
            await using var scope = scopes.CreateAsyncScope();
            var identity = await scope.ServiceProvider.GetRequiredService<SshKeyService>().AuthenticateAsync(fingerprint, verified, e.Cancellation);
            if (identity == null)
            {
                if (verified) log.LogInformation("SSH auth failed: unknown, expired or disabled key {Fingerprint} from {Remote}", fingerprint, conn.Remote);
                return null;
            }
            if (verified)
            {
                conn.Identity = identity;
                log.LogInformation("SSH auth: {Name} with key {Fingerprint} from {Remote}", identity.Name, fingerprint, conn.Remote);
            }
            return new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, identity.Name)], "ssh-publickey"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "SSH authentication error for {Remote}", conn.Remote);
            return null;
        }
    }

    private void OnChannelOpening(Connection conn, SshChannelOpeningEventArgs e, CancellationToken stoppingToken)
    {
        if (!e.IsRemoteRequest) return;
        if (conn.Identity == null || e.Request.ChannelType != SshChannel.SessionChannelType)
        {
            e.FailureReason = SshChannelOpenFailureReason.AdministrativelyProhibited;
            e.FailureDescription = "GitBench only serves git over SSH; port forwarding is disabled.";
            return;
        }
        var channel = new ChannelState(e.Channel, new ChannelStream(new SshStream(e.Channel)));
        e.Channel.Request += (_, r) => OnChannelRequest(conn, channel, r, stoppingToken);
    }

    /// <summary>One session channel: its stream (created at open so no data is missed) and the environment it was given.</summary>
    private sealed class ChannelState(SshChannel channel, ChannelStream stream)
    {
        public SshChannel Channel { get; } = channel;
        public ChannelStream Stream { get; } = stream;
        public string? GitProtocol { get; set; }
        public bool Started { get; set; }
    }

    [GeneratedRegex("^[A-Za-z0-9=:._-]{1,64}$")]
    private static partial Regex GitProtocolPattern();

    private void OnChannelRequest(Connection conn, ChannelState state, SshRequestEventArgs<ChannelRequestMessage> e, CancellationToken stoppingToken)
    {
        switch (e.RequestType)
        {
            case "env":
            {
                var env = e.Request.ConvertTo<EnvRequestMessage>();
                // git sends GIT_PROTOCOL (SendEnv) to ask for protocol v2; everything else is ignored.
                e.IsAuthorized = env.Name == "GIT_PROTOCOL" && GitProtocolPattern().IsMatch(env.Value ?? "");
                if (e.IsAuthorized) state.GitProtocol = env.Value;
                return;
            }
            case ChannelRequestTypes.Command when !state.Started:
            {
                state.Started = true;
                var command = e.Request.ConvertTo<CommandRequestMessage>().Command ?? "";
                e.IsAuthorized = true;
                e.ResponseContinuation = response =>
                {
                    // Run outside the session's message loop, which must keep delivering the client's data.
                    _ = Task.Run(() => RunCommandAsync(conn, state, command, stoppingToken), CancellationToken.None);
                    return Task.CompletedTask;
                };
                return;
            }
            case ChannelRequestTypes.Shell when !state.Started:
                state.Started = true;
                e.IsAuthorized = true;
                e.ResponseContinuation = response =>
                {
                    _ = Task.Run(() => FinishAsync(state, 1, Encoding.UTF8.GetBytes(ShellMessage(conn.Identity!))), CancellationToken.None);
                    return Task.CompletedTask;
                };
                return;
            default:
                // pty-req, subsystem (sftp), x11-req, auth-agent-req, a second exec, …
                e.IsAuthorized = false;
                return;
        }
    }

    private string ShellMessage(SshIdentity id)
    {
        var who = id.IsDeployKey ? $"deploy key \"{id.KeyTitle}\"" : id.UserName;
        return $"Hi {who}! You've successfully authenticated, but GitBench does not provide shell access.\r\n" +
               $"Use git instead, e.g.: git clone ssh://git@{options.Value.PublicHost ?? "<host>"}:{options.Value.EffectivePublicPort}/KEY/repository.git\r\n";
    }

    private async Task RunCommandAsync(Connection conn, ChannelState state, string command, CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        EventHandler<SshChannelClosedEventArgs> onClosed = (_, _) => cts.Cancel();
        state.Channel.Closed += onClosed;
        try
        {
            var runner = new SshGitCommand(scopes, rateLimits, storage, gitPath, log);
            await runner.RunAsync(conn.Identity!, conn.Remote, command, state.GitProtocol, state.Stream,
                (exit, message) => FinishAsync(state, exit, message), cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            log.LogDebug("SSH command from {Remote} cancelled (client went away)", conn.Remote);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SshChannelException or SshConnectionException)
        {
            log.LogDebug(ex, "SSH command from {Remote} ended early", conn.Remote);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "SSH command '{Command}' from {Remote} failed", command, conn.Remote);
            try { await FinishAsync(state, 1, SshGitCommand.Error("internal error; the details are in the server log.").Item2); }
            catch (Exception inner) when (inner is not OutOfMemoryException) { log.LogDebug(inner, "Closing the SSH channel"); }
        }
        finally
        {
            state.Channel.Closed -= onClosed;
        }
    }

    /// <summary>Writes <paramref name="output"/> (if any), then EOF, exit-status and close.</summary>
    private static async Task FinishAsync(ChannelState state, int exitStatus, byte[]? output)
    {
        if (state.Channel.IsClosed) return;
        if (output is { Length: > 0 }) await state.Stream.WriteAsync(output);
        await state.Channel.SendAsync(SshBuffer.Empty, CancellationToken.None); // EOF
        await state.Channel.CloseAsync(unchecked((uint)exitStatus));
    }

    /// <summary>
    /// The channel as a stream. SshStream returns 0 once at the client's EOF but blocks on any further read, so the EOF is
    /// remembered here: every later read returns 0 too (receive-pack's command reader and the copy loop both read to EOF).
    /// </summary>
    internal sealed class ChannelStream(SshStream inner) : Stream
    {
        private volatile bool eof;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (eof || buffer.Length == 0) return 0;
            var array = new byte[buffer.Length];
            var n = await inner.ReadAsync(array, 0, array.Length, ct);
            if (n == 0) eof = true;
            else array.AsMemory(0, n).CopyTo(buffer);
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            new(inner.WriteAsync(buffer.ToArray(), 0, buffer.Length, ct));

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.WriteAsync(buffer, offset, count, ct);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>The <c>env</c> channel request (RFC 4254 6.4): name and value.</summary>
    private sealed class EnvRequestMessage : ChannelRequestMessage
    {
        public string? Name { get; private set; }
        public string? Value { get; private set; }

        protected override void OnRead(ref SshDataReader reader)
        {
            base.OnRead(ref reader);
            Name = reader.ReadString(Encoding.UTF8);
            Value = reader.ReadString(Encoding.UTF8);
        }
    }
}
