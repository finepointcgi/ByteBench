using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ByteBench.Services.Workspaces;

/// <summary>
/// The tools of one CLI-backed AI run, served to the Claude Code CLI as an MCP server (Streamable HTTP, JSON-RPC over POST) on 127.0.0.1 for the
/// life of the run. The CLI runs on the host in an empty directory with every built-in tool switched off, so the only things its model can do are
/// the tools listed here: the same <see cref="WorkspaceAiTools"/> the API backend uses (files and commands inside the workspace container as the
/// unprivileged <c>bbai</c> user, paths validated, output capped, wrapped as untrusted and masked here before it goes back to the model). There is no
/// tool that commits, pushes or reaches another workspace. Requests need the run's random bearer token (kept in a private file, never in the CLI's
/// environment or command line) and a loopback Host header; the listener is closed when the run ends. Tool calls are serialised and capped.
/// </summary>
public sealed class AiRunToolBridge : IDisposable
{
    public const string ServerName = "bytebench";
    public const string ToolPrefix = "mcp__" + ServerName + "__";
    private const int MaxBodyBytes = 4 * 1024 * 1024;

    private readonly HttpListener listener = new();
    private readonly WorkspaceAiTools tools;
    private readonly Func<string, string> mask;
    private readonly Action touch;
    private readonly int maxToolCalls;
    private readonly byte[] tokenBytes;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource stop;
    private readonly int port;
    private int calls;

    public string Token { get; }
    public string Url => $"http://127.0.0.1:{port}/mcp";
    /// <summary>Set when a call was refused because the run used up its tool calls.</summary>
    public bool ToolLimitHit { get; private set; }
    public int Calls => Volatile.Read(ref calls);

    private AiRunToolBridge(WorkspaceAiTools tools, Func<string, string> mask, Action touch, int maxToolCalls, int port, CancellationToken ct)
    {
        this.tools = tools; this.mask = mask; this.touch = touch; this.maxToolCalls = maxToolCalls; this.port = port;
        tokenBytes = RandomNumberGenerator.GetBytes(32);
        Token = Convert.ToHexString(tokenBytes).ToLowerInvariant();
        stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    /// <summary>Starts listening on a free loopback port.</summary>
    public static AiRunToolBridge Start(WorkspaceAiTools tools, Func<string, string> mask, Action touch, int maxToolCalls, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            int port;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var bridge = new AiRunToolBridge(tools, mask, touch, maxToolCalls, port, ct);
            try
            {
                bridge.listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                bridge.listener.Start();
            }
            catch (HttpListenerException) when (attempt < 8) { bridge.Dispose(); continue; } // the port was taken between probing and listening
            _ = Task.Run(() => bridge.AcceptLoopAsync());
            return bridge;
        }
    }

    /// <summary>The MCP configuration file's content for the CLI's <c>--mcp-config</c>.</summary>
    public string McpConfigJson() => new JsonObject
    {
        ["mcpServers"] = new JsonObject
        {
            [ServerName] = new JsonObject { ["type"] = "http", ["url"] = Url, ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + Token } },
        },
    }.ToJsonString();

    private async Task AcceptLoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private bool Authorized(HttpListenerRequest req)
    {
        if (req.Headers["Origin"] != null) return false; // a browser page, never the CLI
        var host = req.Headers["Host"] ?? "";
        if (host != $"127.0.0.1:{port}" && host != $"localhost:{port}") return false; // DNS rebinding
        var auth = req.Headers["Authorization"] ?? "";
        if (!auth.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        var given = Encoding.UTF8.GetBytes(auth["Bearer ".Length..].Trim());
        var expected = Encoding.UTF8.GetBytes(Token);
        return given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected);
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            if (!Authorized(req)) { await Respond(ctx, 401, null); return; }
            if (req.Url?.AbsolutePath.TrimEnd('/') != "/mcp") { await Respond(ctx, 404, null); return; }
            if (req.HttpMethod == "DELETE") { await Respond(ctx, 200, null); return; }
            if (req.HttpMethod != "POST") { await Respond(ctx, 405, null); return; } // no server-initiated stream
            if (req.ContentLength64 > MaxBodyBytes) { await Respond(ctx, 413, null); return; }

            string body;
            using (var reader = new StreamReader(req.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync(stop.Token);
            if (body.Length > MaxBodyBytes) { await Respond(ctx, 413, null); return; }
            JsonNode? root;
            try { root = JsonNode.Parse(body); }
            catch (JsonException) { await Respond(ctx, 400, Error(null, -32700, "Parse error")); return; }

            if (root is JsonArray batch)
            {
                var replies = new JsonArray();
                foreach (var item in batch)
                    if (await DispatchAsync(item) is { } r) replies.Add(r);
                await Respond(ctx, replies.Count == 0 ? 202 : 200, replies.Count == 0 ? null : replies);
            }
            else
            {
                var reply = await DispatchAsync(root);
                await Respond(ctx, reply == null ? 202 : 200, reply);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try { await Respond(ctx, 500, null); } catch (Exception) { /* the client is gone */ }
        }
    }

    private static async Task Respond(HttpListenerContext ctx, int status, JsonNode? body)
    {
        ctx.Response.StatusCode = status;
        if (body != null)
        {
            var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
        }
        ctx.Response.Close();
    }

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private async Task<JsonNode?> DispatchAsync(JsonNode? message)
    {
        if (message is not JsonObject obj || obj["method"]?.GetValue<string>() is not { } method) return null; // responses to us, or noise
        var id = obj["id"];
        if (id == null) return null; // a notification (notifications/initialized, notifications/cancelled)
        var ps = obj["params"] as JsonObject;
        switch (method)
        {
            case "initialize":
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = ps?["protocolVersion"]?.GetValue<string>() ?? "2025-03-26",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "ByteBench workspace", ["version"] = "1" },
                });
            case "ping": return Result(id, new JsonObject());
            case "tools/list": return Result(id, new JsonObject { ["tools"] = ToolList() });
            case "tools/call": return Result(id, await CallAsync(ps));
            default: return Error(id, -32601, "Method not found");
        }
    }

    private static JsonObject Result(JsonNode id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };

    private static JsonArray ToolList()
    {
        var list = new JsonArray();
        foreach (var d in WorkspaceAiTools.Definitions)
        {
            var props = new JsonObject();
            foreach (var (name, schema) in d.Properties) props[name] = JsonNode.Parse(schema.GetRawText());
            list.Add(new JsonObject
            {
                ["name"] = d.Name,
                ["description"] = d.Description,
                ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = new JsonArray(d.Required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()) },
            });
        }
        return list;
    }

    private async Task<JsonObject> CallAsync(JsonObject? ps)
    {
        static JsonObject Text(string text, bool isError) =>
            new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["isError"] = isError };

        var name = ps?["name"]?.GetValue<string>() ?? "";
        if (WorkspaceAiTools.Definitions.All(d => d.Name != name)) return Text($"Unknown tool '{name}'.", true);
        var input = ps?["arguments"] is JsonObject args ? JsonSerializer.SerializeToElement(args) : JsonSerializer.SerializeToElement(new { });
        await serial.WaitAsync(stop.Token);
        try
        {
            if (Interlocked.Increment(ref calls) > maxToolCalls)
            {
                ToolLimitHit = true;
                return Text($"The run's tool-call limit ({maxToolCalls}) is used up. Stop now and write your final summary.", true);
            }
            touch();
            var (content, isError) = await tools.ExecuteAsync(name, input, stop.Token);
            touch();
            return Text(mask(content), isError);
        }
        finally { serial.Release(); }
    }

    public void Dispose()
    {
        try { stop.Cancel(); } catch (ObjectDisposedException) { }
        try { listener.Close(); } catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException) { }
    }
}
