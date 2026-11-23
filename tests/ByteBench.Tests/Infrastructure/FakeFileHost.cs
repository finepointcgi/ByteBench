using System.Net;
using System.Net.Sockets;

namespace ByteBench.Tests.Infrastructure;

/// <summary>What a fake serves at a file path: bytes, a redirect, or an error status.</summary>
public sealed record FakeFile(byte[]? Bytes = null, int Status = 200, string? RedirectTo = null, bool Chunked = false);

public static class FakeFiles
{
    /// <summary>A few real PNG bytes (the signature is what the storage sniffs).</summary>
    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 1, 0, 0, 0, 1];
    public static readonly byte[] Pdf = "%PDF-1.4 fake pdf bytes"u8.ToArray();
    public static byte[] Filler(int length, byte value = 0x41) => Enumerable.Repeat(value, length).ToArray();

    public static void Write(HttpListenerResponse res, FakeFile file)
    {
        if (file.RedirectTo != null)
        {
            res.StatusCode = 302;
            res.RedirectLocation = file.RedirectTo;
            return;
        }
        res.StatusCode = file.Status;
        res.ContentType = "application/octet-stream";
        var bytes = file.Bytes ?? [];
        if (file.Status != 200) return;
        if (file.Chunked) res.SendChunked = true; else res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes);
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
}

/// <summary>
/// A second web host on a local port that serves files (an attachment CDN, or GitHub's upload host). It records the credentials every
/// request carried, so tests can prove a token never reaches it. Never talks to a real service.
/// </summary>
public sealed class FakeFileHost : IDisposable
{
    public sealed record Seen(string PathAndQuery, string? Authorization, string? PrivateToken);

    private readonly HttpListener listener = new();
    public string Url { get; }
    public Dictionary<string, FakeFile> Files { get; } = [];
    public List<Seen> Requests { get; } = [];
    /// <summary>Called with the path of every request before it is answered.</summary>
    public Action<string>? OnRequest { get; set; }

    public FakeFileHost()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        Url = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(Url + "/");
        listener.Start();
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            lock (Requests) Requests.Add(new Seen(req.Url!.PathAndQuery, req.Headers["Authorization"], req.Headers["PRIVATE-TOKEN"]));
            OnRequest?.Invoke(req.Url!.AbsolutePath);
            FakeFile? file;
            lock (Files) Files.TryGetValue(req.Url!.AbsolutePath, out file);
            if (file == null) ctx.Response.StatusCode = 404; else FakeFiles.Write(ctx.Response, file);
        }
        catch { /* client gave up */ }
        finally { try { ctx.Response.Close(); } catch { } }
    }

    public void Dispose() { try { listener.Stop(); listener.Close(); } catch { } }
}
