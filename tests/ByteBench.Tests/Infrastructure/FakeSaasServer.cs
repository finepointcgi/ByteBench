using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace ByteBench.Tests.Infrastructure;

/// <summary>Base of the Trello, Linear and Asana fakes: a local HttpListener that records every request and lets the subclass answer it. Never talks to a real service.</summary>
public abstract class FakeSaasServer : IDisposable
{
    public sealed record Request(string Method, string Path, string Query, string? Authorization, string Body)
    {
        public string? Param(string name) => System.Web.HttpUtility.ParseQueryString(Query)[name];
    }

    public sealed record Reply(int Status, string? Body, Dictionary<string, string>? Headers = null);

    private readonly HttpListener listener = new();
    public string Origin { get; }
    public List<Request> Requests { get; } = [];
    public bool RateLimitOnce { get; set; }

    public int Hits(string pathContains) { lock (Requests) return Requests.Count(r => r.Path.Contains(pathContains, StringComparison.Ordinal)); }

    protected FakeSaasServer()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        Origin = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(Origin + "/");
    }

    /// <summary>Called by the subclass constructor once its data is ready.</summary>
    protected void Start()
    {
        listener.Start();
        _ = Task.Run(Loop);
    }

    protected abstract Reply Route(Request request);

    protected static Reply Json(JsonNode node, int status = 200, Dictionary<string, string>? headers = null) => new(status, node.ToJsonString(), headers);

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
            var raw = ctx.Request.RawUrl ?? "/";
            var q = raw.IndexOf('?');
            var body = "";
            if (ctx.Request.HasEntityBody) using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = sr.ReadToEnd();
            var request = new Request(ctx.Request.HttpMethod, Uri.UnescapeDataString(q < 0 ? raw : raw[..q]), q < 0 ? "" : raw[(q + 1)..], ctx.Request.Headers["Authorization"], body);
            lock (Requests) Requests.Add(request);
            var reply = Route(request);
            ctx.Response.StatusCode = reply.Status;
            foreach (var (k, v) in reply.Headers ?? []) ctx.Response.AddHeader(k, v);
            ctx.Response.ContentType = "application/json";
            ctx.Response.OutputStream.Write(Encoding.UTF8.GetBytes(reply.Body ?? ""));
        }
        catch { /* the client gave up */ }
        finally { try { ctx.Response.Close(); } catch { } }
    }

    public void Dispose() { try { listener.Stop(); listener.Close(); } catch { } }
}
