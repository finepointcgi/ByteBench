using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ByteBench.Services.Workspaces;

namespace ByteBench.Tests.Infrastructure;

/// <summary>
/// A Docker stand-in for workspaces: records every call, answers exec through a script and "connects" every port to a local
/// upstream server (<see cref="Upstream"/>) that echoes what it received, so the proxy can be tested without Docker.
/// </summary>
public sealed class FakeWorkspaceBackend : IWorkspaceBackend, IDisposable
{
    public record ExecCall(string ContainerId, ExecSpec Spec);

    public bool DockerAvailable { get; set; } = true;
    public bool ImageExists { get; set; } = true;
    public List<WorkspaceContainerSpec> Started { get; } = [];
    public ConcurrentQueue<ExecCall> Execs { get; } = new();
    public List<EgressPolicy> EgressApplied { get; } = [];
    public List<string> Removed { get; } = [];
    public List<string> Ready { get; } = [];
    public ConcurrentDictionary<string, WorkspaceContainer> Containers { get; } = new();
    public List<(WorkspaceEndpoint Endpoint, int Port)> Connections { get; } = [];
    public bool FailClone { get; set; }
    /// <summary>Overrides exec output (first match wins): predicate on the spec → result.</summary>
    public List<(Func<ExecSpec, bool> When, Func<ExecSpec, ExecResult> Result)> ExecScripts { get; } = [];
    public bool HealthyAfterStart { get; set; } = true;
    private int counter;
    private readonly Upstream upstream = new();

    public int UpstreamPort => upstream.Port;
    public Upstream Server => upstream;

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(DockerAvailable);
    /// <summary>Images that don't exist until built (toolchain images).</summary>
    public HashSet<string> Missing { get; } = [];
    public Task<bool> ImageExistsAsync(string image, CancellationToken ct) { lock (Missing) return Task.FromResult(ImageExists && !Missing.Contains(image)); }

    public List<(string Image, IReadOnlyDictionary<string, string>? Args)> Built { get; } = [];

    public Task<int> BuildImageAsync(string contextDir, string image, Action<string> onLine, CancellationToken ct, IReadOnlyDictionary<string, string>? buildArgs = null)
    {
        onLine("fake build of " + image);
        lock (Built) Built.Add((image, buildArgs));
        lock (Missing) Missing.Remove(image);
        ImageExists = true;
        return Task.FromResult(0);
    }

    public Task<int> PullImageAsync(string image, Action<string> onLine, CancellationToken ct) => BuildImageAsync("", image, onLine, ct);

    public List<string> Volumes { get; } = [];
    public List<string> VolumesRemoved { get; } = [];
    public List<(string Network, int WorkspaceId)> Networks { get; } = [];
    public List<(string Container, string Network)> Connected { get; } = [];
    public List<WorkspaceSidecarSpec> Sidecars { get; } = [];
    public List<int> AuxiliaryRemoved { get; } = [];
    public Dictionary<string, long> VolumeSizes { get; } = [];
    public ConcurrentQueue<ExecCall> Streams { get; } = new();
    private readonly ConcurrentDictionary<string, int> volumeOwner = new();

    public Task CreateVolumeAsync(string name, int workspaceId, CancellationToken ct)
    {
        lock (Volumes) { if (!Volumes.Contains(name)) Volumes.Add(name); }
        volumeOwner[name] = workspaceId;
        return Task.CompletedTask;
    }

    public Task RemoveVolumesAsync(IEnumerable<string> names, CancellationToken ct)
    {
        foreach (var n in names)
        {
            lock (Volumes) Volumes.Remove(n);
            lock (VolumesRemoved) VolumesRemoved.Add(n);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ManagedVolume>> ListVolumesAsync(CancellationToken ct)
    {
        lock (Volumes) return Task.FromResult<IReadOnlyList<ManagedVolume>>(Volumes.Select(v => new ManagedVolume(v, volumeOwner.TryGetValue(v, out var id) ? id : null, VolumeSizes.GetValueOrDefault(v, 1024))).ToList());
    }

    public Task CreateInternalNetworkAsync(string name, int workspaceId, CancellationToken ct)
    {
        lock (Networks) Networks.Add((name, workspaceId));
        return Task.CompletedTask;
    }

    public Task ConnectNetworkAsync(string containerId, string network, CancellationToken ct)
    {
        lock (Connected) Connected.Add((containerId, network));
        return Task.CompletedTask;
    }

    public Task<WorkspaceSidecarContainer> StartSidecarAsync(WorkspaceSidecarSpec spec, Action<string> onLine, CancellationToken ct)
    {
        onLine("fake pull of " + spec.Image);
        lock (Sidecars) Sidecars.Add(spec);
        return Task.FromResult(new WorkspaceSidecarContainer("s" + Interlocked.Increment(ref counter).ToString("D4") + new string('0', 59), "10.99.0." + (10 + Sidecars.Count)));
    }

    public Task RemoveAuxiliaryAsync(int workspaceId, CancellationToken ct)
    {
        lock (AuxiliaryRemoved) AuxiliaryRemoved.Add(workspaceId);
        return Task.CompletedTask;
    }

    public Task<string> SidecarLogsAsync(string containerId, CancellationToken ct) => Task.FromResult("(fake sidecar log)");

    /// <summary>Streaming exec: the script's output (split into lines) is delivered to the callback; otherwise like <see cref="ExecAsync"/>.</summary>
    public List<(Func<ExecSpec, bool> When, Func<ExecSpec, Action<string>, CancellationToken, Task<ExecResult>> Run)> StreamScripts { get; } = [];

    public async Task<ExecResult> ExecStreamAsync(string containerId, ExecSpec spec, Action<string> onLine, CancellationToken ct)
    {
        Streams.Enqueue(new ExecCall(containerId, spec));
        foreach (var (when, run) in StreamScripts.ToList())
            if (when(spec)) return await run(spec, onLine, ct);
        var result = await ExecAsync(containerId, spec, ct);
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)) onLine(line);
        return result;
    }

    public Task<WorkspaceContainer> StartAsync(WorkspaceContainerSpec spec, CancellationToken ct)
    {
        Started.Add(spec);
        var c = new WorkspaceContainer("c" + Interlocked.Increment(ref counter).ToString("D4") + new string('0', 59), 40000 + counter);
        Containers[c.ContainerId] = c;
        workspaceOf[c.ContainerId] = spec.WorkspaceId;
        return Task.FromResult(c);
    }

    public Task<WorkspaceContainer> RestartAsync(string containerId, int idePort, CancellationToken ct) => Task.FromResult(Containers[containerId]);
    public Task WaitUntilWaitingAsync(string containerId, CancellationToken ct) => Task.CompletedTask;

    public Task ApplyEgressAsync(string containerId, string image, EgressPolicy policy, CancellationToken ct)
    {
        EgressApplied.Add(policy);
        return Task.CompletedTask;
    }

    public Task<ExecResult> ExecAsync(string containerId, ExecSpec spec, CancellationToken ct)
    {
        Execs.Enqueue(new ExecCall(containerId, spec));
        foreach (var (when, result) in ExecScripts)
            if (when(spec)) return Task.FromResult(result(spec));
        if (FailClone && spec.Command.Contains("sh") && spec.Command.Any(a => a.Contains("git clone")))
            return Task.FromResult(new ExecResult(128, "fatal: could not read Username"));
        return Task.FromResult(new ExecResult(0, ""));
    }

    public Task MarkReadyAsync(string containerId, CancellationToken ct)
    {
        Ready.Add(containerId);
        return Task.CompletedTask;
    }

    public Task<bool> IsHealthyAsync(string containerId, CancellationToken ct) => Task.FromResult(HealthyAfterStart);

    public async Task<Stream> ConnectAsync(WorkspaceEndpoint endpoint, int port, CancellationToken ct)
    {
        lock (Connections) Connections.Add((endpoint, port));
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, RawPorts.TryGetValue(port, out var local) ? local.Port : upstream.Port, ct);
        return client.GetStream();
    }

    /// <summary>Container ports that answer with a raw TCP banner and then echo (a stand-in for the SSH server): port → local listener.</summary>
    public ConcurrentDictionary<int, RawEcho> RawPorts { get; } = new();

    /// <summary>Makes <paramref name="containerPort"/> a raw echo server that first sends <paramref name="banner"/>.</summary>
    public RawEcho ServeRaw(int containerPort, string banner) => RawPorts[containerPort] = new RawEcho(banner);

    /// <summary>A TCP server: sends a banner on connect, then echoes every byte.</summary>
    public sealed class RawEcho : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly string banner;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public RawEcho(string banner)
        {
            this.banner = banner;
            listener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var c = await listener.AcceptTcpClientAsync();
                        _ = Task.Run(async () =>
                        {
                            using var _ = c;
                            var s = c.GetStream();
                            await s.WriteAsync(Encoding.ASCII.GetBytes(this.banner));
                            var buf = new byte[4096];
                            int n;
                            while ((n = await s.ReadAsync(buf)) > 0) await s.WriteAsync(buf.AsMemory(0, n));
                        });
                    }
                }
                catch (Exception) { /* stopped */ }
            });
        }

        public void Dispose() => listener.Stop();
    }

    public Task RemoveAsync(string containerId, CancellationToken ct)
    {
        Removed.Add(containerId);
        Containers.TryRemove(containerId, out _);
        return Task.CompletedTask;
    }

    public List<ManagedContainer> Extra { get; } = [];

    public Task<IReadOnlyList<ManagedContainer>> ListManagedAsync(CancellationToken ct)
    {
        var list = Containers.Keys.Select(id => new ManagedContainer(id, WorkspaceIdOf(id))).Concat(Extra).ToList();
        return Task.FromResult<IReadOnlyList<ManagedContainer>>(list);
    }

    private readonly ConcurrentDictionary<string, int> workspaceOf = new();
    private int? WorkspaceIdOf(string containerId) => workspaceOf.TryGetValue(containerId, out var id) ? id : null;

    /// <summary>Drops a container behind ByteBench's back (it "disappeared").</summary>
    public void Vanish(string containerId) => Containers.TryRemove(containerId, out _);

    /// <summary>The credential line written into the container (user:token@host), for tests that need the plaintext token.</summary>
    public string? GitCredential() => Execs.Select(e => e.Spec.Stdin).FirstOrDefault(s => s != null && s.Contains("gbw_"))?.Trim();

    public string? GitToken()
    {
        var line = GitCredential();
        if (line == null) return null;
        var afterScheme = line[(line.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var userAndToken = afterScheme[..afterScheme.IndexOf('@')];
        return userAndToken[(userAndToken.IndexOf(':') + 1)..];
    }

    public string? ConnectionToken(string? containerId = null) => Execs.Where(e => containerId == null || e.ContainerId == containerId).Select(e => e.Spec).FirstOrDefault(s => s.Command.Any(a => a.Contains("/tmp/.bb-token")))?.Stdin;

    public void Dispose()
    {
        upstream.Dispose();
        foreach (var r in RawPorts.Values) r.Dispose();
    }

    /// <summary>A tiny HTTP/1.1 server: /echo returns the request head as text, /ws upgrades and echoes bytes, anything else 404.</summary>
    public sealed class Upstream : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cts = new();
        public List<string> Requests { get; } = [];
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public Upstream()
        {
            listener.Start();
            _ = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(cts.Token);
                    _ = Task.Run(() => Handle(client));
                }
            }
            catch (Exception) { /* stopped */ }
        }

        private async Task Handle(TcpClient client)
        {
            using var _ = client;
            var stream = client.GetStream();
            var buf = new byte[16384];
            var filled = 0;
            while (true)
            {
                int end;
                while ((end = IndexOfHeaderEnd(buf, filled)) < 0)
                {
                    var n = await stream.ReadAsync(buf.AsMemory(filled));
                    if (n == 0) return;
                    filled += n;
                }
                var head = Encoding.ASCII.GetString(buf, 0, end);
                lock (Requests) Requests.Add(head);
                var rest = filled - (end + 4);
                var first = head.Split("\r\n")[0];
                if (first.Contains(" /ws") && head.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase))
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: test\r\n\r\n"));
                    if (rest > 0) await stream.WriteAsync(buf.AsMemory(end + 4, rest));
                    int n;
                    var echo = new byte[4096];
                    while ((n = await stream.ReadAsync(echo)) > 0) await stream.WriteAsync(echo.AsMemory(0, n));
                    return;
                }
                string body;
                var status = "200 OK";
                var extra = "";
                if (first.Contains("/echo")) { body = head; extra = "Set-Cookie: vscode-tkn=abc; Path=/; SameSite=Lax\r\nSet-Cookie: bb_ws=evil; Path=/\r\nX-Frame-Options: DENY\r\nContent-Security-Policy: default-src 'none'\r\n"; }
                else if (first.Contains(" /big")) body = new string('x', 100_000);
                else { status = "404 Not Found"; body = "nope"; }
                var bytes = Encoding.UTF8.GetBytes(body);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/plain\r\nContent-Length: {bytes.Length}\r\n{extra}\r\n"));
                await stream.WriteAsync(bytes);
                // keep-alive: shift any pipelined bytes (none in these tests)
                filled = 0;
            }
        }

        private static int IndexOfHeaderEnd(byte[] b, int count)
        {
            for (var i = 0; i + 3 < count; i++)
                if (b[i] == '\r' && b[i + 1] == '\n' && b[i + 2] == '\r' && b[i + 3] == '\n') return i;
            return -1;
        }

        public void Dispose()
        {
            cts.Cancel();
            listener.Stop();
        }
    }
}
