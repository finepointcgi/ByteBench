using System.Net;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ByteBench.Cli;

/// <summary>
/// <c>bytebench workspace connect</c> (allow your SSH public key in a workspace, print the SSH config) and <c>bytebench workspace tunnel</c> (the SSH
/// <c>ProxyCommand</c> that VS Code Remote-SSH and JetBrains Gateway end up running).
/// </summary>
public sealed partial class CliApp
{
    [GeneratedRegex("^[a-z2-7]{20}$")]
    private static partial Regex WorkspaceIdRx();

    private string WorkspaceId()
    {
        if (args.Words.Count != 3) throw CliException.Usage($"Usage: bytebench workspace {args.Words[1]} <workspace-id>   (the id in the workspace's address).");
        var id = args.Words[2].Trim();
        return WorkspaceIdRx().IsMatch(id) ? id : throw CliException.Usage("That doesn't look like a workspace id (20 characters, as in /workspaces/<id>).");
    }

    private async Task<int> WorkspaceConnectAsync(CancellationToken ct)
    {
        args.Allow("key");
        var id = WorkspaceId();
        var keyPath = args.Opt("key") ?? DefaultPublicKeyPath();
        string publicKey;
        try { publicKey = (await File.ReadAllTextAsync(keyPath, ct)).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw CliException.Usage($"Can't read the public key '{Text.Line(keyPath)}'. Create one with: ssh-keygen -t ed25519   (or pass --key FILE.pub).");
        }
        if (publicKey.Contains("PRIVATE KEY", StringComparison.Ordinal)) throw CliException.Usage("That file holds a private key. Pass the matching .pub file instead.");
        using var api = Connect();
        var r = (await api.PostAsync($"api/v1/workspaces/{id}/editor/connect", new { publicKey }, ct)).Body;
        if (AsJson) { Print(r); return ExitCodes.Ok; }
        env.Out.WriteLine($"Your key ({Text.Line(r.S("keyFingerprint"))}) is allowed in the workspace until {Text.Line(r.S("keyExpiresAt"))}.");
        env.Out.WriteLine();
        env.Out.WriteLine("Add this to ~/.ssh/config (and set IdentityFile to your private key):");
        env.Out.WriteLine();
        env.Out.WriteLine(Text.Safe(r.S("sshConfig")));
        env.Out.WriteLine();
        env.Out.WriteLine($"VS Code: install \"Remote - SSH\", run \"Remote-SSH: Connect to Host…\" and choose {Text.Line(r.S("alias"))}, then open {Text.Line(r.S("directory"))}.");
        env.Out.WriteLine($"JetBrains Gateway: SSH → New Connection → OpenSSH config → {Text.Line(r.S("alias"))}, then pick your IDE and {Text.Line(r.S("directory"))}.");
        return ExitCodes.Ok;
    }

    private string DefaultPublicKeyPath()
    {
        var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        foreach (var name in new[] { "id_ed25519.pub", "id_ecdsa.pub", "id_rsa.pub" })
            if (File.Exists(Path.Combine(ssh, name))) return Path.Combine(ssh, name);
        return Path.Combine(ssh, "id_ed25519.pub");
    }

    /// <summary>The tunnel's WebSocket address: the server's own, with ws/wss in place of http/https.</summary>
    public static Uri TunnelUri(Uri server, string workspaceId)
    {
        var http = new Uri(server, $"api/v1/workspaces/{workspaceId}/editor/tunnel");
        return new UriBuilder(http) { Scheme = http.Scheme == Uri.UriSchemeHttps ? "wss" : "ws", Port = http.IsDefaultPort ? -1 : http.Port }.Uri;
    }

    private async Task<int> WorkspaceTunnelAsync(CancellationToken ct)
    {
        args.Allow();
        var id = WorkspaceId();
        var (server, token, _) = Resolve();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        socket.Options.CollectHttpResponseDetails = true; // so a refusal can be explained (401, 403, 404, 422)
        try
        {
            using var invoker = new HttpMessageInvoker(env.CreateHandler());
            await socket.ConnectAsync(TunnelUri(server, id), invoker, ct);
        }
        catch (WebSocketException ex)
        {
            throw socket.HttpStatusCode switch
            {
                HttpStatusCode.Unauthorized => CliException.Auth("The server rejected the access token. Run: bytebench login --server <url>"),
                HttpStatusCode.Forbidden => CliException.Failure("Not allowed: the tunnel needs a token with the write_api scope."),
                HttpStatusCode.NotFound => CliException.Failure("Workspace not found (is it yours, and signed in to the right server?)."),
                HttpStatusCode.UnprocessableEntity => CliException.Failure("The workspace can't be reached: it isn't running, or connecting local editors is turned off on this server."),
                _ => CliException.Failure("Can't open the tunnel: " + Text.Line(ex.Message)),
            };
        }
        catch (HttpRequestException ex)
        {
            throw CliException.Failure($"Can't reach {ServerUrl.Display(server)}: {Text.Line(ex.InnerException?.Message ?? ex.Message)}");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var stdin = env.OpenRawInput();
        await using var stdout = env.OpenRawOutput();
        var up = Task.Run(async () =>
        {
            var buffer = new byte[32 * 1024];
            while (!cts.IsCancellationRequested)
            {
                var n = await stdin.ReadAsync(buffer, cts.Token);
                if (n == 0) break; // ssh closed its side
                await socket.SendAsync(buffer.AsMemory(0, n), WebSocketMessageType.Binary, true, cts.Token);
            }
        }, cts.Token);
        var down = Task.Run(async () =>
        {
            var buffer = new byte[32 * 1024];
            while (!cts.IsCancellationRequested)
            {
                var r = await socket.ReceiveAsync(buffer, cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
                await stdout.WriteAsync(buffer.AsMemory(0, r.Count), cts.Token);
                await stdout.FlushAsync(cts.Token);
            }
        }, cts.Token);
        await Task.WhenAny(up, down);
        cts.Cancel();
        try { await Task.WhenAll(up, down); }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException) { }
        if (socket.State == WebSocketState.Open)
        {
            try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
            catch (Exception ex) when (ex is WebSocketException or IOException) { }
        }
        return ExitCodes.Ok;
    }
}
