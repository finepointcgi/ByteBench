using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ByteBench.Services.Workspaces;

/// <summary>Opens the outbound connection of an allowed proxy request. Replaceable so tests never touch the network.</summary>
public interface IEgressConnector
{
    /// <summary>Connects to <paramref name="host"/>:<paramref name="port"/>; throws <see cref="EgressDeniedException"/> when the name resolves somewhere the proxy must never go.</summary>
    Task<Stream> ConnectAsync(string host, int port, bool allowPrivate, CancellationToken ct);
}

public sealed class EgressDeniedException(string message) : Exception(message);

/// <summary>
/// The real connector: resolves the name itself and refuses loopback, private, link-local and other non-public addresses (so an allowed
/// host name can't be pointed at ByteBench's own network, a metadata service or another workspace), unless the admin allowed private targets.
/// </summary>
public sealed class DnsEgressConnector : IEgressConnector
{
    public async Task<Stream> ConnectAsync(string host, int port, bool allowPrivate, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        if (addresses.Length == 0) throw new EgressDeniedException($"{host} did not resolve.");
        Exception? last = null;
        foreach (var ip in addresses)
        {
            if (!allowPrivate && !IsPublic(ip)) throw new EgressDeniedException($"{host} resolves to a non-public address ({ip}).");
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(ip, port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException) { socket.Dispose(); last = ex; }
        }
        throw last ?? new SocketException();
    }

    /// <summary>Whether the address is on the public internet (not loopback, private, CGNAT, link-local, multicast, reserved or unique-local).</summary>
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b6 = ip.GetAddressBytes();
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal || ip.Equals(IPAddress.IPv6None) || ip.Equals(IPAddress.IPv6Any) || (b6[0] == 0xFE && (b6[1] & 0xC0) == 0x80));
        }
        var b = ip.GetAddressBytes();
        return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
                 || (b[0] == 100 && b[1] is >= 64 and <= 127) || (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] is >= 16 and <= 31)
                 || (b[0] == 192 && b[1] == 0 && b[2] == 0) || (b[0] == 192 && b[1] == 168) || (b[0] == 198 && b[1] is 18 or 19));
    }
}

/// <summary>A request the policy refused: what a workspace tried to reach.</summary>
public sealed record EgressBlock(DateTime At, string Host, int Port, string Method, string Reason, int Count);

/// <summary>What the proxy knows about one workspace.</summary>
public sealed class EgressEntry(int workspaceId, string publicId, int projectId, int ownerId, EffectiveEgress policy, EgressRule? alwaysAllowed)
{
    public int WorkspaceId { get; } = workspaceId;
    public string PublicId { get; } = publicId;
    public int ProjectId { get; } = projectId;
    public int OwnerId { get; } = ownerId;
    public EffectiveEgress Policy { get; } = policy;
    /// <summary>ByteBench's own git endpoint: always reachable, and the one private address the proxy will connect to.</summary>
    public EgressRule? AlwaysAllowed { get; } = alwaysAllowed;
    public long Allowed;
    public long Blocked;

    public bool Allows(string host, int port) => (AlwaysAllowed?.Matches(host, port) ?? false) || Policy.Allows(host, port);
    public bool IsOwnServer(string host, int port) => AlwaysAllowed?.Matches(host, port) ?? false;
}

/// <summary>
/// A small forward proxy (HTTP <c>CONNECT</c> for https and anything tunnelled, absolute-form for plain http) that the workspaces in
/// <c>allowlist</c> mode reach through. The container's firewall allows nothing else, so a request is made only if this policy says so.
/// Each workspace authenticates with its own secret (the proxy password in <c>HTTP_PROXY</c>), which names the workspace and so its policy.
/// </summary>
public sealed class EgressProxyServer(IEgressConnector connector, Func<string, EgressEntry?> findByToken, Action<EgressEntry, string, int, string, string> onBlocked,
    Func<bool> allowPrivate, ILogger log) : IDisposable
{
    private TcpListener? listener;
    private readonly CancellationTokenSource cts = new();
    private readonly SemaphoreSlim connections = new(512);
    private readonly object gate = new();

    public int Port { get; private set; }

    /// <summary>Starts listening (once) and returns the port.</summary>
    public int Start(string bindAddress, int port)
    {
        lock (gate)
        {
            if (listener != null) return Port;
            var ip = IPAddress.TryParse(bindAddress, out var a) ? a : IPAddress.Any;
            listener = new TcpListener(ip, port);
            listener.Start(128);
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoop);
            log.LogInformation("Workspace egress proxy listening on {Address}:{Port}", ip, Port);
            return Port;
        }
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var client = await listener!.AcceptTcpClientAsync(cts.Token);
                if (!await connections.WaitAsync(0)) { client.Dispose(); continue; }
                _ = Task.Run(async () =>
                {
                    try { await HandleAsync(client); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { log.LogDebug(ex, "Egress proxy connection failed"); }
                    finally { connections.Release(); }
                });
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { /* stopped */ }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        client.NoDelay = true;
        var stream = client.GetStream();
        using var headTimeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        headTimeout.CancelAfter(TimeSpan.FromSeconds(20));
        var buffer = new byte[16 * 1024];
        var filled = 0;
        int end;
        while ((end = IndexOfHeaderEnd(buffer, filled)) < 0)
        {
            if (filled == buffer.Length) { await Reply(stream, 431, "Request header too large"); return; }
            var n = await stream.ReadAsync(buffer.AsMemory(filled), headTimeout.Token);
            if (n == 0) return;
            filled += n;
        }
        var lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
        var request = lines[0].Split(' ');
        if (request.Length != 3 || !request[2].StartsWith("HTTP/", StringComparison.Ordinal)) { await Reply(stream, 400, "Bad request"); return; }
        var method = request[0].ToUpperInvariant();
        var target = request[1];

        string? auth = null;
        foreach (var line in lines.Skip(1))
            if (line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase)) auth = line[(line.IndexOf(':') + 1)..].Trim();
        var entry = AuthenticatedEntry(auth);
        if (entry == null)
        {
            await Reply(stream, 407, "Proxy authentication required", "Proxy-Authenticate: Basic realm=\"ByteBench workspace\"\r\n");
            return;
        }

        string host;
        int port;
        string? forwardTarget = null;
        if (method == "CONNECT")
        {
            if (!TrySplitHostPort(target, 443, out host, out port)) { await Reply(stream, 400, "Bad CONNECT target"); return; }
        }
        else
        {
            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || uri.UserInfo.Length > 0)
            { await Reply(stream, 400, "Only absolute http:// requests (or CONNECT) are proxied"); return; }
            host = uri.IdnHost;
            port = uri.Port;
            forwardTarget = uri.PathAndQuery;
        }
        host = host.ToLowerInvariant().TrimEnd('.');

        if (!entry.Allows(host, port))
        {
            Interlocked.Increment(ref entry.Blocked);
            onBlocked(entry, host, port, method, "not in the allow-list");
            await Reply(stream, 403, $"Blocked by the ByteBench workspace network policy: {host}:{port} is not allowed.");
            return;
        }

        Stream upstream;
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            connect.CancelAfter(TimeSpan.FromSeconds(15));
            upstream = await connector.ConnectAsync(host, port, allowPrivate() || entry.IsOwnServer(host, port), connect.Token);
        }
        catch (EgressDeniedException ex)
        {
            Interlocked.Increment(ref entry.Blocked);
            onBlocked(entry, host, port, method, ex.Message);
            await Reply(stream, 403, "Blocked by the ByteBench workspace network policy: " + ex.Message);
            return;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
        {
            await Reply(stream, 502, $"Could not reach {host}:{port}.");
            return;
        }

        await using var _up = upstream;
        Interlocked.Increment(ref entry.Allowed);
        var leftover = buffer.AsMemory(end + 4, filled - end - 4);
        if (method == "CONNECT")
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), cts.Token);
        }
        else
        {
            var head = new StringBuilder($"{request[0]} {forwardTarget} HTTP/1.1\r\n");
            foreach (var line in lines.Skip(1))
            {
                if (line.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("Keep-Alive:", StringComparison.OrdinalIgnoreCase)) continue;
                head.Append(line).Append("\r\n");
            }
            head.Append("Connection: close\r\n\r\n");
            await upstream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cts.Token);
        }
        if (leftover.Length > 0) await upstream.WriteAsync(leftover, cts.Token);
        await upstream.FlushAsync(cts.Token);

        using var pipe = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var up = Copy(stream, upstream, pipe.Token);
        var down = Copy(upstream, stream, pipe.Token);
        await Task.WhenAny(up, down);
        pipe.Cancel();
        try { await Task.WhenAll(up, down); } catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or SocketException) { }
    }

    private EgressEntry? AuthenticatedEntry(string? header)
    {
        if (header == null || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
            var colon = decoded.IndexOf(':');
            return colon < 0 ? null : findByToken(decoded[(colon + 1)..]);
        }
        catch (FormatException) { return null; }
    }

    private static bool TrySplitHostPort(string target, int defaultPort, out string host, out int port)
    {
        host = "";
        port = defaultPort;
        if (target.StartsWith('['))
        {
            var close = target.IndexOf(']');
            if (close < 0) return false;
            host = target[1..close];
            var rest = target[(close + 1)..];
            return rest.Length == 0 || (rest[0] == ':' && int.TryParse(rest[1..], out port) && port is >= 1 and <= 65535);
        }
        var colon = target.LastIndexOf(':');
        if (colon < 0) { host = target; return host.Length > 0; }
        host = target[..colon];
        return host.Length > 0 && int.TryParse(target[(colon + 1)..], out port) && port is >= 1 and <= 65535;
    }

    private static async Task Copy(Stream from, Stream to, CancellationToken ct)
    {
        var buf = new byte[32 * 1024];
        while (true)
        {
            var n = await from.ReadAsync(buf, ct);
            if (n == 0) return;
            await to.WriteAsync(buf.AsMemory(0, n), ct);
            await to.FlushAsync(ct);
        }
    }

    private static async Task Reply(Stream stream, int status, string message, string extraHeaders = "")
    {
        var reason = status switch { 400 => "Bad Request", 403 => "Forbidden", 407 => "Proxy Authentication Required", 431 => "Request Header Fields Too Large", 502 => "Bad Gateway", _ => "Error" };
        var body = Encoding.UTF8.GetBytes(message + "\n");
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n{extraHeaders}\r\n";
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            await stream.WriteAsync(body);
            await stream.FlushAsync();
        }
        catch (IOException) { }
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
        try { listener?.Stop(); } catch (SocketException) { }
    }
}
