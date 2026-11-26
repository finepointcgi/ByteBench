using System.Text.Json;
using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Services.Audit;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Workspaces;

/// <summary>Records workspace audit events from singletons and background tasks (a scope per call; details never contain secrets).</summary>
public sealed class WorkspaceAudit(IServiceScopeFactory scopes)
{
    public async Task RecordAsync(string action, Workspace ws, int? actorId, object? details = null)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AuditLog>().RecordAsync(action, "workspace", ws.PublicId, ws.ProjectId,
            details, actorId: actorId ?? ws.OwnerId, anonymous: false);
    }
}

/// <summary>
/// Starting, restarting and stopping workspace containers (singleton, used by the scoped <see cref="WorkspaceService"/>, the reaper and
/// the background provisioning task). Order matters for security: the container starts idle, its network policy is applied from a
/// separate helper, only then are credentials and the repository put in, and the IDE starts last.
/// </summary>
public sealed partial class WorkspaceLifecycle(IDbContextFactory<AppDbContext> dbFactory, IWorkspaceBackend backend, WorkspaceRuntime runtime,
    IOptionsMonitor<WorkspaceOptions> options, WorkspaceAudit audit, WorkspaceUrls urls, IServer server, ILogger<WorkspaceLifecycle> log)
{
    public const int IdePort = 3000;
    public const string WorkspaceRoot = "/workspace";

    private WorkspaceOptions Opts => options.CurrentValue;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/@+-]{0,199}$")]
    private static partial Regex SafeRefPattern();

    /// <summary>A branch, tag or commit name that is safe to pass to git as a plain argument (no option-like or odd names).</summary>
    public static bool IsSafeRef(string r) => SafeRefPattern().IsMatch(r) && !r.Contains("..") && !r.EndsWith('/') && !r.EndsWith(".lock") && !r.Contains("//");

    /// <summary>The directory the repository is checked out in.</summary>
    public static string WorkDir(string slug) => $"{WorkspaceRoot}/{slug}";

    /// <summary>
    /// How containers reach ByteBench's git endpoint: <c>Workspaces:GitBaseUrl</c>, else <c>http://host.docker.internal:{port}</c> with the
    /// port of ByteBench's first listen address.
    /// </summary>
    public Uri GitBase()
    {
        if (Opts.GitBaseUrl is { Length: > 0 } configured) return new Uri(configured.TrimEnd('/') + "/");
        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        var port = address != null && Uri.TryCreate(address.Replace("*", "localhost").Replace("+", "localhost").Replace("[::]", "localhost").Replace("0.0.0.0", "localhost"), UriKind.Absolute, out var u) ? u.Port : 8080;
        return new Uri($"http://host.docker.internal:{port}/");
    }

    /// <summary>Whether the IDE is reached through the in-container relay instead of a published loopback port (see <see cref="WorkspaceOptions.IdeTransport"/>).</summary>
    public bool UseRelay() => Opts.IdeTransport.ToLowerInvariant() switch
    {
        "relay" => true,
        "published" => false,
        _ => File.Exists("/.dockerenv") || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOCKER_HOST")),
    };

    // ---------- start ----------

    /// <summary>Provisions a workspace row created by <see cref="WorkspaceService"/>: container, network policy, checkout, IDE. Never throws; failures are recorded.</summary>
    public async Task ProvisionAsync(int workspaceId, string gitToken, string connectionToken)
    {
        var gate = runtime.LockFor(workspaceId);
        await gate.WaitAsync();
        string? containerId = null;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var ws = await db.Workspaces.Include(w => w.Owner).Include(w => w.Repository).ThenInclude(r => r.Project).FirstOrDefaultAsync(w => w.Id == workspaceId);
            if (ws == null || ws.State != WorkspaceState.Starting) return;
            var o = Opts;
            using var timeout = runtime.BeginProvisioning(workspaceId, TimeSpan.FromSeconds(Math.Max(30, o.StartupTimeoutSeconds)));
            var ct = timeout.Token;

            var env = new Dictionary<string, string>
            {
                ["BB_WORKDIR"] = WorkDir(ws.Repository.Slug),
                ["BB_IDE_PORT"] = IdePort.ToString(),
            };
            if (urls.Mode == WorkspaceOriginMode.SharedPath) env["BB_IDE_BASE_PATH"] = $"/_ws/{ws.PublicId}/ide";
            var name = $"bb-ws-{ByteBench.Services.Ci.CiDocker.InstanceKey}-{ws.PublicId[..10]}";
            var container = await backend.StartAsync(new WorkspaceContainerSpec(ws.Id, name, ws.Image, o.Network, "host.docker.internal", IdePort, env,
                new WorkspaceLimits(o.Memory, o.Cpus, o.PidsLimit, o.DiskLimit)) { PublishIdePort = !UseRelay() }, ct);
            containerId = container.ContainerId;
            ws.ContainerId = containerId;
            ws.ContainerName = name;
            ws.PortMapJson = JsonSerializer.Serialize(new Dictionary<string, int> { [IdePort.ToString()] = container.IdeHostPort });
            await db.SaveChangesAsync(CancellationToken.None);
            if (!container.DiskLimitApplied) log.LogWarning("Workspace {Id} runs without a disk limit (Docker refused --storage-opt)", ws.PublicId);

            await backend.WaitUntilWaitingAsync(containerId, ct);
            await ApplyEgressAsync(containerId, ws.Image, ws.EgressMode, ct);
            await CheckOutAsync(ws, containerId, gitToken, ct);
            await WriteTokenAndStartAsync(containerId, connectionToken, ct);
            await WaitHealthyAsync(containerId, ct);

            if (!await StillStartingAsync(db, ws)) { await RemoveQuietlyAsync(containerId); return; } // stopped while starting

            ws.State = WorkspaceState.Running;
            ws.StartedAt = DateTime.UtcNow;
            ws.LastActivityAt = DateTime.UtcNow;
            ws.ExpiresAt = DateTime.UtcNow.AddHours(Math.Max(1, o.MaxLifetimeHours));
            await db.SaveChangesAsync(CancellationToken.None);
            runtime.Register(new LiveWorkspace(ws.Id, ws.PublicId, ws.OwnerId, containerId, container.IdeHostPort, IdePort, connectionToken));
            await audit.RecordAsync("workspace.started", ws, ws.OwnerId, new { repository = $"{ws.Repository.Project.Key}/{ws.Repository.Slug}", @ref = ws.Ref, canPush = ws.CanPush, egress = ws.EgressMode, image = ws.Image, source = ws.Source });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogWarning(ex, "Workspace {Id} failed to start", workspaceId);
            await FailAsync(workspaceId, containerId, ex is OperationCanceledException ? "The workspace did not become ready in time." : Safe(ex.Message));
        }
        finally { runtime.EndProvisioning(workspaceId); gate.Release(); }
    }

    private static async Task<bool> StillStartingAsync(AppDbContext db, Workspace ws)
    {
        await db.Entry(ws).ReloadAsync();
        return ws.State == WorkspaceState.Starting;
    }

    private async Task ApplyEgressAsync(string containerId, string image, string mode, CancellationToken ct)
    {
        var git = GitBase();
        var allow = new List<string>();
        var ip = git.HostNameType == UriHostNameType.Dns ? await ResolveInContainerAsync(containerId, git.Host, ct) : git.Host;
        if (ip != null) allow.Add($"{ip}:{git.Port}");
        else if (mode != "open") throw new InvalidOperationException($"The container can't resolve ByteBench's git host '{git.Host}' (Workspaces:GitBaseUrl).");
        await backend.ApplyEgressAsync(containerId, image, new EgressPolicy(mode, allow), ct);
    }

    private async Task<string?> ResolveInContainerAsync(string containerId, string host, CancellationToken ct)
    {
        var r = await backend.ExecAsync(containerId, new ExecSpec(["getent", "ahostsv4", host]) { Timeout = TimeSpan.FromSeconds(10) }, ct);
        var first = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return r.Ok && System.Net.IPAddress.TryParse(first, out _) ? first : null;
    }

    private const string CloneScript = """
        set -e
        umask 002
        git clone --no-checkout -- "$1" "$2"
        cd "$2"
        if git show-ref --verify --quiet "refs/remotes/origin/$3"; then git checkout -q -B "$3" --track "origin/$3"; else git checkout -q --detach "$3"; fi
        """;

    private async Task CheckOutAsync(Workspace ws, string containerId, string gitToken, CancellationToken ct)
    {
        var git = GitBase();
        var credential = $"{git.Scheme}://{Uri.EscapeDataString(ws.Owner.UserName!)}:{gitToken}@{git.Authority}\n";
        // The token goes through stdin into a mode-600 file the developer's user owns; the AI assistant's user can't read it.
        await RunOrThrowAsync(containerId, new ExecSpec(["sh", "-c", "umask 077; cat > \"$HOME/.git-credentials\""]) { Stdin = credential }, "storing the git credential", ct);
        foreach (var args in new[]
        {
            new[] { "git", "config", "--global", "credential.helper", "store" },
            ["git", "config", "--global", "user.name", string.IsNullOrWhiteSpace(ws.Owner.DisplayName) ? ws.Owner.UserName! : ws.Owner.DisplayName],
            ["git", "config", "--global", "user.email", ws.Owner.Email ?? $"{ws.Owner.UserName}@users.noreply.bytebench"],
            ["git", "config", "--global", "pull.rebase", "false"],
        })
            await RunOrThrowAsync(containerId, new ExecSpec(args), "configuring git", ct);

        var repoUrl = new Uri(git, $"git/{ws.Repository.Project.Key}/{ws.Repository.Slug}.git").ToString();
        var clone = await backend.ExecAsync(containerId, new ExecSpec(["sh", "-c", CloneScript, "sh", repoUrl, WorkDir(ws.Repository.Slug), ws.Ref])
        {
            WorkDir = WorkspaceRoot, Timeout = TimeSpan.FromMinutes(5), MaxOutputBytes = 16 * 1024,
            Env = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" },
        }, ct);
        if (!clone.Ok) throw new InvalidOperationException("Checking out the repository failed: " + Safe(clone.Output));
        ws.CommitSha = null;
        var head = await backend.ExecAsync(containerId, new ExecSpec(["git", "-C", WorkDir(ws.Repository.Slug), "rev-parse", "HEAD"]), ct);
        if (head.Ok && head.Output.Trim().Length == 40) ws.CommitSha = head.Output.Trim();
    }

    private async Task WriteTokenAndStartAsync(string containerId, string connectionToken, CancellationToken ct)
    {
        await RunOrThrowAsync(containerId, new ExecSpec(["sh", "-c", "umask 077; cat > /tmp/.bb-token"]) { Stdin = connectionToken }, "storing the connection token", ct);
        await backend.MarkReadyAsync(containerId, ct);
    }

    private async Task WaitHealthyAsync(string containerId, CancellationToken ct)
    {
        while (!await backend.IsHealthyAsync(containerId, ct)) await Task.Delay(500, ct);
    }

    private async Task RunOrThrowAsync(string containerId, ExecSpec spec, string what, CancellationToken ct)
    {
        var r = await backend.ExecAsync(containerId, spec, ct);
        if (!r.Ok) throw new InvalidOperationException($"Setting up the workspace failed while {what}: {Safe(r.Output)}");
    }

    // ---------- restart ----------

    /// <summary>Restarts the container (files stay; running processes end). The network policy is applied again before the IDE comes back.</summary>
    public async Task RestartAsync(int workspaceId, int actorId)
    {
        var gate = runtime.LockFor(workspaceId);
        await gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var ws = await db.Workspaces.Include(w => w.Repository).ThenInclude(r => r.Project).FirstOrDefaultAsync(w => w.Id == workspaceId);
            if (ws is not { State: WorkspaceState.Running, ContainerId: { } containerId } || runtime.Find(workspaceId) is not { } live)
                throw new InvalidOperationException("Only a running workspace can be restarted.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(30, Opts.StartupTimeoutSeconds)));
            var ct = timeout.Token;
            ws.State = WorkspaceState.Starting;
            await db.SaveChangesAsync(CancellationToken.None);
            runtime.Forget(workspaceId); // sessions are dropped, the IDE reconnects through a fresh ticket
            try
            {
                var container = await backend.RestartAsync(containerId, IdePort, ct);
                await backend.WaitUntilWaitingAsync(containerId, ct);
                await ApplyEgressAsync(containerId, ws.Image, ws.EgressMode, ct);
                await WriteTokenAndStartAsync(containerId, live.ConnectionToken, ct);
                await WaitHealthyAsync(containerId, ct);
                ws.PortMapJson = JsonSerializer.Serialize(new Dictionary<string, int> { [IdePort.ToString()] = container.IdeHostPort });
                ws.State = WorkspaceState.Running;
                ws.LastActivityAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                runtime.Register(new LiveWorkspace(ws.Id, ws.PublicId, ws.OwnerId, containerId, container.IdeHostPort, IdePort, live.ConnectionToken));
                await audit.RecordAsync("workspace.restarted", ws, actorId);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log.LogWarning(ex, "Workspace {Id} failed to restart", ws.PublicId);
                await FailAsync(workspaceId, containerId, ex is OperationCanceledException ? "The workspace did not come back in time." : Safe(ex.Message));
            }
        }
        finally { gate.Release(); }
    }

    // ---------- stop & fail ----------

    /// <summary>Stops a workspace: access is cut first, then the container is removed and the git token revoked. Idempotent.</summary>
    public async Task StopAsync(int workspaceId, string reason, int? actorId)
    {
        runtime.Forget(workspaceId); // no more proxying, whatever happens next
        await using var db = await dbFactory.CreateDbContextAsync();
        var ws = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == workspaceId);
        if (ws == null || ws.State is WorkspaceState.Stopped or WorkspaceState.Failed) return;
        ws.State = WorkspaceState.Stopping;
        ws.GitTokenHash = null; // revoked before the container is even touched
        await db.SaveChangesAsync(CancellationToken.None);
        runtime.CancelProvisioning(workspaceId); // a start in progress notices and cleans up after itself
        var gate = runtime.LockFor(workspaceId);
        await gate.WaitAsync();
        try
        {
            await db.Entry(ws).ReloadAsync();
            if (ws.State is WorkspaceState.Stopped or WorkspaceState.Failed) return;
            if (ws.ContainerId != null) await RemoveQuietlyAsync(ws.ContainerId);
            ws.State = WorkspaceState.Stopped;
            ws.StoppedAt = DateTime.UtcNow;
            ws.StopReason = reason.Length > 100 ? reason[..100] : reason;
            await db.SaveChangesAsync(CancellationToken.None);
            await audit.RecordAsync("workspace.stopped", ws, actorId, new { reason, by = actorId == null ? "system" : actorId == ws.OwnerId ? "owner" : "admin" });
        }
        finally { gate.Release(); runtime.DropLock(workspaceId); }
    }

    private async Task FailAsync(int workspaceId, string? containerId, string error)
    {
        runtime.Forget(workspaceId);
        if (containerId != null) await RemoveQuietlyAsync(containerId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var ws = await db.Workspaces.Include(w => w.Repository).ThenInclude(r => r.Project).FirstOrDefaultAsync(w => w.Id == workspaceId);
        if (ws == null || ws.State is WorkspaceState.Stopped or WorkspaceState.Failed or WorkspaceState.Stopping) return; // a stop in progress finishes the job
        ws.State = WorkspaceState.Failed;
        ws.GitTokenHash = null;
        ws.StoppedAt = DateTime.UtcNow;
        ws.Error = error.Length > 500 ? error[..500] : error;
        await db.SaveChangesAsync(CancellationToken.None);
        await audit.RecordAsync("workspace.failed", ws, null, new { error = ws.Error, repository = $"{ws.Repository.Project.Key}/{ws.Repository.Slug}" });
    }

    private async Task RemoveQuietlyAsync(string containerId)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await backend.RemoveAsync(containerId, cts.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { log.LogWarning(ex, "Could not remove workspace container {Id}", containerId); }
    }

    /// <summary>An error message for the UI: single line, bounded, and with anything that looks like a workspace token blanked out.</summary>
    internal static string Safe(string message)
    {
        var s = Regex.Replace(message ?? "", @"gbw_[A-Za-z0-9_-]+", "gbw_…");
        s = Regex.Replace(s, @"://[^/@\s]+:[^/@\s]+@", "://…@");
        s = s.Trim();
        return s.Length > 400 ? "…" + s[^400..] : s;
    }
}
