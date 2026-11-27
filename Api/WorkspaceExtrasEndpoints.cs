using System.Net.WebSockets;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Workspaces;

namespace ByteBench.Api;

public record EditorConnectRequest(string? PublicKey = null);

/// <param name="Listening">Something is listening now; false for a port the environment file or a task declares that hasn't started yet.</param>
/// <param name="Task">The environment task that serves this port (its preview), if any.</param>
/// <param name="Origin">Where the port is served: <c>http://p3000-{id}.localhost:5108</c> (its own origin), or a path on ByteBench's origin in shared-origin mode. Opening it needs a ticket: see the open endpoint.</param>
public record WorkspacePortDto(int Port, string? Origin, bool Listening = true, string? Task = null);

/// <param name="Url">Single-use (60 s) entry URL on the port's origin: open it right away, never store or share it.</param>
public record WorkspacePortOpenDto(int Port, string Url, string? Origin, int ExpiresInSeconds);

public record GitCommitRequest(string? Message = null, bool Push = false, string? Branch = null);

public record GitPullRequestRequest(string? Title = null, string? Description = null, string? Target = null, bool CloseIssue = true, bool Draft = false);

public record StartFromWorkRequest(string? Repo = null);

public record WorkspaceLaunchDto(WorkspaceDto Workspace, string Branch, bool BranchCreated, bool Reused);

public record GitResultDto(bool Ok, string Output);

/// <summary>
/// The workspace extras: connect a local editor over an SSH tunnel, list and open a workspace's ports (for the desktop apps), commit, push and open a pull
/// request from the workspace, and start a workspace from an issue or a pull request. Every <c>/workspaces/{id}</c> route answers 404 to anyone but the
/// owner; routes that start from an issue or a pull request answer 404 to someone who can't see it.
/// </summary>
public static class WorkspaceExtrasEndpoints
{
    public static void MapWorkspaceExtrasEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/workspaces").WithTags("Workspaces");

        // Local editors.
        g.MapGet("/{id}/editor", EditorInfo).WithName("GetWorkspaceEditorInfo").WithSummary("Connect a local editor: status and addresses")
            .WithDescription("Whether connecting VS Code (Remote-SSH) or JetBrains Gateway is on (an administrator switch, off by default) and the tunnel address. No secrets.");
        g.MapPost("/{id}/editor/connect", EditorConnect).WithName("ConnectWorkspaceEditor").WithSummary("Authorize your SSH public key for this workspace")
            .WithDescription("Installs the public key (never a private key) in the running workspace for at most 8 hours (and never past the workspace's own end) and returns the SSH configuration and editor instructions. 422 when the switch is off or the workspace isn't running. Audited.")
            .WithJsonBody<EditorConnectRequest>();
        g.MapGet("/{id}/editor/tunnel", EditorTunnel).WithName("OpenWorkspaceEditorTunnel").WithSummary("WebSocket tunnel to the workspace's SSH server")
            .WithDescription("Binary WebSocket carrying the SSH protocol, used as the editor's ProxyCommand by `bytebench workspace tunnel`. Needs a personal access token with write_api (not a browser session).");

        // Ports (desktop apps).
        g.MapGet("/{id}/ports", ListPorts).WithName("ListWorkspacePorts").WithSummary("Ports something in the workspace is listening on")
            .WithDescription("422 if the workspace isn't running.");
        g.MapPost("/{id}/ports/{port}/open", OpenPort).WithName("OpenWorkspacePort").WithSummary("A single-use address to open a workspace port")
            .WithDescription("Returns a 60-second, single-use entry URL on the port's own origin. 422 if the workspace isn't running.");

        // Git from the workspace bar.
        g.MapGet("/{id}/git", GitStatus).WithName("GetWorkspaceGitStatus").WithSummary("Branch, changed files and sync state of the workspace's checkout");
        g.MapPost("/{id}/git/commit", GitCommit).WithName("CommitWorkspace").WithSummary("Commit everything in the workspace (optionally push)")
            .WithDescription("Commits all changes as you. With push=true it also pushes to the workspace's branch (or `branch`) with the workspace's git token; branch protection still applies. Audited.")
            .WithJsonBody<GitCommitRequest>();
        g.MapPost("/{id}/git/push", GitPush).WithName("PushWorkspace").WithSummary("Push the workspace's current branch");
        g.MapPost("/{id}/git/pull-request", GitPullRequest).WithName("OpenWorkspacePullRequest").WithSummary("Open a pull request from the workspace's branch")
            .WithDescription("Pushes the branch, then opens a pull request into `target` (default: the repository's default branch). When the workspace was opened for an issue the description names it, which links the pull request to the issue (`closeIssue` chooses `Fixes` over `Related to`). An open pull request for the branch is returned instead of a duplicate. 422 with uncommitted changes.")
            .WithJsonBody<GitPullRequestRequest>(optional: true);

        // Issue / PR links.
        g.MapGet("/{id}/links", Links).WithName("GetWorkspaceLinks").WithSummary("The issue and pull request a workspace was opened for");
        api.MapPost("/projects/{key}/issues/{number:int}/workspace", StartFromIssue).WithTags("Workspaces").WithName("StartWorkspaceFromIssue")
            .WithSummary("Start a workspace on the issue's branch")
            .WithDescription("Opens (or returns your live) workspace on a branch named from the issue, e.g. KEY-12-fix-login, creating it from the default branch when it doesn't exist (needs push permission). `repo` is only needed when the project has several repositories. 202. Audited.")
            .WithJsonBody<StartFromWorkRequest>(optional: true);
        api.MapPost("/projects/{key}/repos/{slug}/pull-requests/{number:int}/workspace", StartFromPullRequest).WithTags("Workspaces").WithName("StartWorkspaceFromPullRequest")
            .WithSummary("Start a workspace on the pull request's source branch")
            .WithDescription("Opens (or returns your live) workspace on the source branch. Pull requests from forks aren't supported. 202. Audited.");
    }

    private static async Task<WorkspaceInfo> Mine(WorkspaceService workspaces, ApiCaller caller, string id) =>
        await workspaces.GetMineAsync(id, caller.Id) ?? throw ApiException.NotFound("Workspace not found.");

    private static string Base(HttpContext http) => http.RequestServices.GetRequiredService<PublicBaseUrl>().For(http.Request);

    // ---------- editor ----------

    private static async Task<EditorInfo> EditorInfo(HttpContext http, ApiCaller caller, WorkspaceService workspaces, WorkspaceEditorService editor, string id)
    {
        await Mine(workspaces, caller, id);
        return await editor.GetInfoAsync(id, caller.Id, Base(http));
    }

    private static async Task<EditorConnection> EditorConnect(HttpContext http, ApiCaller caller, WorkspaceService workspaces, WorkspaceEditorService editor, string id)
    {
        await Mine(workspaces, caller, id);
        var body = await ApiResults.ReadBodyAsync<EditorConnectRequest>(http.Request);
        return await editor.ConnectAsync(id, caller.Id, body.PublicKey ?? "", Base(http));
    }

    private static async Task<IResult> EditorTunnel(HttpContext http, ApiCaller caller, WorkspaceService workspaces, WorkspaceEditorService editor, string id)
    {
        await Mine(workspaces, caller, id);
        // A browser session can't open this: a cookie would let any web page that the user visits open a shell into their workspace (WebSockets
        // aren't subject to CORS). The token must be an explicit credential with write access.
        if (caller.Token == null || caller.Scopes is not { } scopes || !scopes.HasFlag(TokenScopes.WriteApi))
            throw new ApiException(StatusCodes.Status403Forbidden, "Forbidden", "The editor tunnel needs a personal access token with the write_api scope (a browser session can't open it).");
        if (!http.WebSockets.IsWebSocketRequest) throw ApiException.BadRequest("This endpoint speaks WebSocket. Use `bytebench workspace tunnel` as the SSH ProxyCommand.");
        var (stream, lease) = await editor.OpenTunnelAsync(id, caller.Id, http.RequestAborted);
        using (lease)
        await using (stream)
        {
            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            await PumpAsync(socket, stream, () => editor.Touch(id), http.RequestAborted);
        }
        return Results.Empty;
    }

    private static async Task PumpAsync(WebSocket socket, Stream stream, Action touch, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var up = Task.Run(async () =>
        {
            var buffer = new byte[32 * 1024];
            while (!cts.Token.IsCancellationRequested)
            {
                var r = await socket.ReceiveAsync(buffer, cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) return;
                if (r.MessageType != WebSocketMessageType.Binary) { await socket.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "binary frames only", CancellationToken.None); return; }
                touch();
                await stream.WriteAsync(buffer.AsMemory(0, r.Count), cts.Token);
                await stream.FlushAsync(cts.Token);
            }
        }, cts.Token);
        var down = Task.Run(async () =>
        {
            var buffer = new byte[32 * 1024];
            while (!cts.Token.IsCancellationRequested)
            {
                var n = await stream.ReadAsync(buffer, cts.Token);
                if (n == 0) return;
                await socket.SendAsync(buffer.AsMemory(0, n), WebSocketMessageType.Binary, true, cts.Token);
            }
        }, cts.Token);
        await Task.WhenAny(up, down);
        cts.Cancel();
        try { await Task.WhenAll(up, down); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or WebSocketException or ObjectDisposedException or InvalidOperationException) { }
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None); }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException) { }
        }
    }

    // ---------- ports ----------

    private static string? PortOrigin(WorkspaceUrls urls, Uri mainBase, string publicId, int port) => urls.Mode switch
    {
        WorkspaceOriginMode.Subdomain => urls.Origin(mainBase, WorkspaceUrls.PreviewAudience(port), publicId),
        WorkspaceOriginMode.SharedPath => $"{mainBase.GetLeftPart(UriPartial.Authority)}/_ws/{publicId}/{WorkspaceUrls.PreviewAudience(port)}/",
        _ => null,
    };

    private static async Task<List<WorkspacePortDto>> ListPorts(HttpContext http, ApiCaller caller, WorkspaceService workspaces, WorkspaceUrls urls, string id)
    {
        await Mine(workspaces, caller, id);
        var mainBase = new Uri(Base(http) + "/");
        // The same data the workspace page's Preview menu uses: sockets that are listening, plus the ports the environment file and its tasks declare.
        var listening = await workspaces.ListeningPortsAsync(id, caller.Id);
        var env = await workspaces.GetEnvironmentAsync(id, caller.Id);
        var tasks = env.Tasks.Where(t => t.Port is not null).GroupBy(t => t.Port!.Value).ToDictionary(g => g.Key, g => g.First().Name);
        return listening.Concat(env.Ports).Concat(tasks.Keys).Where(p => p is >= 1 and <= 65535).Distinct().Order()
            .Select(p => new WorkspacePortDto(p, PortOrigin(urls, mainBase, id, p), listening.Contains(p), tasks.GetValueOrDefault(p))).ToList();
    }

    private static async Task<WorkspacePortOpenDto> OpenPort(HttpContext http, ApiCaller caller, WorkspaceService workspaces, WorkspaceUrls urls, string id, string port)
    {
        await Mine(workspaces, caller, id);
        // Parsed here (not by the route) so a bad value is a 400 after authentication, never a route miss.
        if (!int.TryParse(port, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) || number is < 1 or > 65535)
            throw ApiException.BadRequest("Port must be a number between 1 and 65535.");
        var mainBase = new Uri(Base(http) + "/");
        var url = await workspaces.EntryUrlAsync(id, caller.Id, mainBase, number) ?? throw ApiException.Unprocessable("The workspace isn't running, or workspaces can't be opened on this server.");
        return new WorkspacePortOpenDto(number, url, PortOrigin(urls, mainBase, id, number), (int)WorkspaceRuntime.TicketLifetime.TotalSeconds);
    }

    // ---------- git ----------

    private static async Task<WorkspaceGitStatus> GitStatus(ApiCaller caller, WorkspaceService workspaces, WorkspaceGitService git, string id)
    {
        await Mine(workspaces, caller, id);
        return await git.GetStatusAsync(id, caller.Id);
    }

    private static async Task<GitResultDto> GitCommit(HttpContext http, ApiCaller caller, WorkspaceService workspaces, WorkspaceGitService git, string id)
    {
        await Mine(workspaces, caller, id);
        var body = await ApiResults.ReadBodyAsync<GitCommitRequest>(http.Request);
        var r = await git.CommitAsync(id, caller.Id, body.Message ?? "", body.Push, body.Branch);
        if (!r.Ok) throw ApiException.Unprocessable(r.ExitCode == 3 ? "Nothing to commit." : r.Output);
        return new GitResultDto(true, r.Output);
    }

    private static async Task<GitResultDto> GitPush(ApiCaller caller, WorkspaceService workspaces, WorkspaceGitService git, string id)
    {
        await Mine(workspaces, caller, id);
        var r = await git.PushAsync(id, caller.Id);
        if (!r.Ok) throw ApiException.Unprocessable(r.Output);
        return new GitResultDto(true, r.Output);
    }

    private static async Task<WorkspacePullRequestResult> GitPullRequest(HttpContext http, ApiCaller caller, WorkspaceService workspaces, WorkspaceGitService git, string id)
    {
        await Mine(workspaces, caller, id);
        var body = http.Request.ContentLength is > 0 ? await ApiResults.ReadBodyAsync<GitPullRequestRequest>(http.Request) : new GitPullRequestRequest();
        return await git.OpenPullRequestAsync(id, caller.Id, body.Title, body.Description, body.Target, body.CloseIssue, body.Draft);
    }

    // ---------- links, issue and PR launch ----------

    private static async Task<WorkspaceLinkView> Links(ApiCaller caller, WorkspaceService workspaces, WorkspaceLaunchService launch, string id)
    {
        await Mine(workspaces, caller, id);
        return await launch.GetLinksAsync(id, caller.Id) ?? throw ApiException.NotFound("Workspace not found.");
    }

    private static async Task<IResult> StartFromIssue(HttpContext http, ApiCaller caller, WorkspaceService workspaces, WorkspaceLaunchService launch, IssueService issues, string key, int number)
    {
        _ = await issues.GetIssueAsync(key, number) ?? throw ApiException.NotFound($"Issue {key.ToUpperInvariant()}-{number} not found.");
        var body = http.Request.ContentLength is > 0 ? await ApiResults.ReadBodyAsync<StartFromWorkRequest>(http.Request) : new StartFromWorkRequest();
        var r = await launch.StartForIssueAsync(key, number, body.Repo, caller.Id);
        return await LaunchResponse(workspaces, caller, r);
    }

    private static async Task<IResult> StartFromPullRequest(ApiCaller caller, WorkspaceService workspaces, WorkspaceLaunchService launch, RepositoryService repos, string key, string slug, int number)
    {
        _ = await repos.GetRepositoryAsync(key, slug) ?? throw ApiException.NotFound($"Repository '{key.ToUpperInvariant()}/{slug}' not found.");
        var r = await launch.StartForPullRequestAsync(key, slug, number, caller.Id);
        return await LaunchResponse(workspaces, caller, r);
    }

    private static async Task<IResult> LaunchResponse(WorkspaceService workspaces, ApiCaller caller, LaunchResult r)
    {
        var view = await workspaces.GetMineAsync(r.Workspace.PublicId, caller.Id) ?? throw ApiException.NotFound("Workspace not found.");
        return TypedResults.Json(new WorkspaceLaunchDto(WorkspaceEndpoints.ToDto(view), r.Branch, r.BranchCreated, r.Reused), statusCode: StatusCodes.Status202Accepted);
    }
}
