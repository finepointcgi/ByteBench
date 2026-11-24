using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ByteBench.Services.Push;

namespace ByteBench.Tests.Infrastructure;

/// <summary>One request the fake push service received.</summary>
public sealed record PushRequest(string Path, IReadOnlyDictionary<string, string> Headers, byte[] Body);

/// <summary>
/// A stand-in for a browser vendor's push service (RFC 8030) on 127.0.0.1 (HttpListener): records every POST and answers
/// with <see cref="Status"/> (201 by default), optionally after <see cref="Delay"/>. <see cref="NewBrowser"/> plays the browser
/// side: a fresh P-256 key pair and auth secret, and the matching subscription endpoint.
/// </summary>
public sealed class FakePushService : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource stop = new();
    public string BaseUrl { get; }
    public ConcurrentQueue<PushRequest> Requests { get; } = new();
    public int Status { get; set; } = 201;
    public string? RetryAfter { get; set; }
    public TimeSpan Delay { get; set; }

    public FakePushService()
    {
        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(BaseUrl + "/");
        listener.Start();
        _ = Task.Run(LoopAsync);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task LoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch (Exception) { return; }
            _ = Task.Run(async () =>
            {
                using var ms = new MemoryStream();
                await ctx.Request.InputStream.CopyToAsync(ms);
                Requests.Enqueue(new PushRequest(ctx.Request.Url!.AbsolutePath,
                    ctx.Request.Headers.AllKeys.Where(k => k != null).ToDictionary(k => k!, k => ctx.Request.Headers[k]!, StringComparer.OrdinalIgnoreCase), ms.ToArray()));
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay);
                ctx.Response.StatusCode = Status;
                if (RetryAfter != null) ctx.Response.Headers["Retry-After"] = RetryAfter;
                ctx.Response.Close();
            });
        }
    }

    /// <summary>A browser: its key pair and auth secret, and the endpoint its subscription would point to.</summary>
    public FakeBrowser NewBrowser() => new(BaseUrl + "/push/" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        stop.Cancel();
        // The managed HttpListener on macOS can throw while tearing down sockets of pending requests; nothing to clean up then.
        try { listener.Stop(); listener.Close(); } catch (ObjectDisposedException) { } catch (HttpListenerException) { }
    }
}

public sealed class FakeBrowser(string endpoint) : IDisposable
{
    public string Endpoint { get; } = endpoint;
    public ECDiffieHellman Key { get; } = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    public byte[] Auth { get; } = RandomNumberGenerator.GetBytes(16);
    public string P256dh => WebPushCrypto.Base64Url(WebPushCrypto.PublicKeyBytes(Key));
    public string AuthText => WebPushCrypto.Base64Url(Auth);

    /// <summary>What the browser does with a push: decrypt the body with its private key.</summary>
    public byte[] Decrypt(byte[] body) => WebPushCrypto.Decrypt(body, Key, Auth);

    public void Dispose() => Key.Dispose();
}
