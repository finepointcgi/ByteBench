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
    /// <summary>
    /// Records an event about a workspace by id (the proxy, the egress proxy and sharing know ids, not entities). Action names are listed in
    /// <see cref="WorkspaceAuditActions"/>. <c>details</c> never holds secret values: secrets are recorded by name only.
    /// </summary>
    public async Task RecordAsync(string action, string publicId, int projectId, int? actorId, object? details = null)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AuditLog>().RecordAsync(action, "workspace", publicId, projectId, WithoutValues(details), actorId: actorId, anonymous: actorId == null);
    }

    /// <summary>
    /// Workspace events name secrets (<c>names</c>), never carry their values: properties called value(s), content, data or env are dropped on top of
    /// the audit log's own scrubbing of password, token, secret and hash properties.
    /// </summary>
    internal static System.Text.Json.Nodes.JsonNode? WithoutValues(object? details)
    {
        if (details == null) return null;
        var node = System.Text.Json.JsonSerializer.SerializeToNode(details);
        Strip(node);
        return node;

        static void Strip(System.Text.Json.Nodes.JsonNode? n)
        {
            switch (n)
            {
                case System.Text.Json.Nodes.JsonObject o:
                    foreach (var key in o.Select(p => p.Key).ToList())
                    {
                        if (key.ToLowerInvariant() is "value" or "values" or "content" or "data" or "env" or "plaintext") o.Remove(key);
                        else Strip(o[key]);
                    }
                    break;
                case System.Text.Json.Nodes.JsonArray a:
                    foreach (var item in a) Strip(item);
                    break;
            }
        }
    }

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
    IOptionsMonitor<WorkspaceOptions> options, WorkspaceAudit audit, WorkspaceUrls urls, IServer server, WorkspaceEgress egress, WorkspaceShareStore shares, WorkspaceImage image, WorkspaceSecretStore secretStore,
    SubmoduleResolver submodules, WorkspaceTasks tasks, WorkspaceCollaborators collaborators, Api.PublicBaseUrl publicBase, ILogger<WorkspaceLifecycle> log)
{
    /// <summary>The IDE's port inside the container. Deliberately unusual: 3000, 5000 and 8080 are what the apps people run there want.</summary>
    public const int IdePort = 13370;
    public const string WorkspaceRoot = "/workspace";
    public const string CacheRoot = "/home/coder/.bbcache";

    /// <summary>The names of a workspace's two volumes: the project (<c>/workspace</c>) and the dependency caches.</summary>
    public static (string Work, string Cache) VolumeNames(string publicId) =>
        ($"bb-wsv-{ByteBench.Services.Ci.CiDocker.InstanceKey}-{publicId}-work", $"bb-wsv-{ByteBench.Services.Ci.CiDocker.InstanceKey}-{publicId}-cache");

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
    public async Task ProvisionAsync(int workspaceId, string gitToken, string connectionToken, WorkspaceProvisionPlan plan, bool resume = false)
    {
        var gate = runtime.LockFor(workspaceId);
        await gate.WaitAsync();
        string? containerId = null;
        WorkspaceSession? session = null;
        var phase = "starting";
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var ws = await db.Workspaces.Include(w => w.Owner).Include(w => w.Repository).ThenInclude(r => r.Project).FirstOrDefaultAsync(w => w.Id == workspaceId);
            if (ws == null || ws.State != WorkspaceState.Starting) return;
            var o = Opts;
            session = runtime.ResetSession(workspaceId);
            session.Env = plan.Env;
            session.EnvErrors = plan.EnvErrors;
            foreach (var w in plan.Warnings) session.Warn(w);
            var plog = session.ProvisionLog;
            plog.Append(resume ? "Resuming the workspace with its saved files and dependencies…" : "Starting the workspace…");

            // One cancellation scope for the whole start (a stop request cancels it); the startup time limit begins after the image exists,
            // because building a toolchain image the first time can take many minutes.
            using var timeout = runtime.BeginProvisioning(workspaceId, TimeSpan.FromMinutes(60));
            phase = "preparing the image";
            if (plan.BaseImage != null) await image.EnsureDerivedAsync(plan.Image, plan.BaseImage, plog.Append, timeout.Token);
            else if (!await backend.ImageExistsAsync(plan.Image, timeout.Token)) await image.EnsureAsync(timeout.Token);
            // Each step has its own time limit inside the overall (cancellable) scope: a first checkout with submodules is allowed far longer than the container start.
            var startupSeconds = Math.Max(30, o.StartupTimeoutSeconds);
            CancellationTokenSource Step(int seconds) { var c = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token); c.CancelAfter(TimeSpan.FromSeconds(seconds)); return c; }
            using var startup = Step(startupSeconds);
            var ct = startup.Token;

            var env = new Dictionary<string, string>
            {
                ["BB_WORKDIR"] = WorkDir(ws.Repository.Slug),
                ["BB_IDE_PORT"] = IdePort.ToString(),
            };
            if (urls.Mode == WorkspaceOriginMode.SharedPath) env["BB_IDE_BASE_PATH"] = $"/_ws/{ws.PublicId}/ide";
            // The network policy: the admin's, narrowed by the repository's egress: (never widened). An allow-list points the tools at the filtering proxy.
            var policy = await egress.ResolveAsync(db, ws, plan.Env.Egress);
            foreach (var (k, v) in egress.Prepare(ws, policy, GitBase(), plan.Env.Services.Select(x => x.Name))) env[k] = v;
            var name = $"bb-ws-{ByteBench.Services.Ci.CiDocker.InstanceKey}-{ws.PublicId[..10]}";
            WorkspaceAllocation? alloc = null;
            var fromPrebuild = false;
            var (workVolume, cacheVolume) = VolumeNames(ws.PublicId);
            phase = "creating the volumes";
            await backend.CreateVolumeAsync(workVolume, ws.Id, ct);
            await backend.CreateVolumeAsync(cacheVolume, ws.Id, ct);
            ws.VolumesKept = true;
            // A new workspace on a branch with a usable prebuild starts from its image (the volumes are then seeded from it); a resume never does: its own volumes win.
            alloc = await db.WorkspaceAllocations.FirstOrDefaultAsync(a => a.WorkspaceId == ws.Id, ct);
            if (!resume && alloc?.PrebuildId != null && ws.Image != plan.Image)
            {
                if (await backend.ImageExistsAsync(ws.Image, ct)) { fromPrebuild = true; plog.Append("Starting from a prebuild: the repository is already cloned and its setup has run."); }
                else
                {
                    // The prebuilt image went away between the start request and now: a normal start, never a failed one.
                    log.LogInformation("Workspace {Id}: prebuild image {Image} is gone, starting from the base image", ws.PublicId, ws.Image);
                    ws.Image = plan.Image;
                    alloc.PrebuildId = null;
                }
            }
            else ws.Image = plan.Image;
            ws.EnvironmentSummary = plan.Summary;
            await db.SaveChangesAsync(CancellationToken.None);

            phase = "starting the container";
            plog.Append($"Starting the container from {plan.Image}…");
            var container = await backend.StartAsync(new WorkspaceContainerSpec(ws.Id, name, ws.Image, o.Network, "host.docker.internal", IdePort, env,
                alloc is { CpuMillis: > 0 } ? new WorkspaceResources(alloc.CpuMillis, alloc.MemoryMb, alloc.DiskMb).ToLimits(o.PidsLimit) : new WorkspaceLimits(o.Memory, o.Cpus, o.PidsLimit, o.DiskLimit))
            { PublishIdePort = !UseRelay(), Mounts = [new(workVolume, WorkspaceRoot), new(cacheVolume, CacheRoot)] }, ct);
            containerId = container.ContainerId;
            ws.ContainerId = containerId;
            ws.ContainerName = name;
            ws.PortMapJson = JsonSerializer.Serialize(new Dictionary<string, int> { [IdePort.ToString()] = container.IdeHostPort });
            await db.SaveChangesAsync(CancellationToken.None);
            if (!container.DiskLimitApplied) log.LogWarning("Workspace {Id} runs without a disk limit (Docker refused --storage-opt)", ws.PublicId);

            await backend.WaitUntilWaitingAsync(containerId, ct);
            phase = "starting the services";
            List<SidecarAddress> allow;
            using (var services = Step(startupSeconds + 600))
            {
                allow = await StartServicesAsync(ws, containerId, plan.Env, session, services.Token);
                session.EgressAllow = allow.Select(a => $"{a.Ip}:{a.Port}").ToList();
                phase = "applying the network policy";
                await ApplyEgressAsync(containerId, ws.Image, ws.EgressMode, services.Token, allow.Select(a => $"{a.Ip}:{a.Port}").ToList());
                phase = "waiting for the services";
                await WaitForServicesAsync(containerId, allow, session, services.Token);
            }

            phase = "checking out the repository";
            using (var checkout = Step(20 * 60)) await CheckOutAsync(ws, containerId, gitToken, plan, session, fromPrebuild, checkout.Token);
            phase = "preparing the environment";
            using (var prep = Step(120)) await WriteEnvironmentAsync(ws, containerId, plan, session, prep.Token);
            phase = "starting the IDE";
            plog.Append("Starting the IDE…");
            using var ide = Step(startupSeconds);
            await WriteTokenAndStartAsync(containerId, connectionToken, ide.Token);
            await WaitHealthyAsync(containerId, ide.Token);

            if (!await StillStartingAsync(db, ws)) { await RemoveQuietlyAsync(containerId); return; } // stopped while starting

            // The setup state is set before the workspace shows as Running: a task started the moment it opens must see "setup pending" and be refused.
            session.Setup = plan.Env.Setup.Count > 0 ? SetupState.Pending : SetupState.None;
            ws.State = WorkspaceState.Running;
            ws.StartedAt = DateTime.UtcNow;
            ws.LastActivityAt = DateTime.UtcNow;
            ws.ExpiresAt = DateTime.UtcNow.AddHours(Math.Max(1, o.MaxLifetimeHours));
            await db.SaveChangesAsync(CancellationToken.None);
            var live = new LiveWorkspace(ws.Id, ws.PublicId, ws.OwnerId, containerId, container.IdeHostPort, IdePort, connectionToken);
            runtime.Register(live);
            plog.Append("The workspace is ready.");
            if (plan.Env.Image != null && plan.BaseImage != null)
                await audit.RecordAsync(WorkspaceAuditActions.ImageOverride, ws, ws.OwnerId, new { image = plan.Env.Image, derived = plan.Image });
            await audit.RecordAsync(resume ? "workspace.resumed" : "workspace.started", ws, ws.OwnerId, new
            {
                repository = $"{ws.Repository.Project.Key}/{ws.Repository.Slug}", @ref = ws.Ref, canPush = ws.CanPush, egress = ws.EgressMode, image = ws.Image, source = ws.Source,
                environment = plan.Summary, services = plan.Env.Services.Count, tasks = plan.Env.Tasks.Count,
            });

            // The repository's setup commands run now, in the background: the IDE is already usable and the output is on the page.
            if (plan.Env.Setup.Count > 0)
                runtime.TrackSetup(ws.Id, Task.Run(() => tasks.RunSetupAsync(live, session, WorkDir(ws.Repository.Slug), session.Lifetime.Token)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogWarning(ex, "Workspace {Id} failed to start", workspaceId);
            var message = ex is OperationCanceledException ? $"The workspace did not become ready in time (while {phase})." : Safe(ex.Message);
            session?.ProvisionLog.Append("Failed: " + message);
            await FailAsync(workspaceId, containerId, message, keepVolumes: resume);
        }
        finally { runtime.EndProvisioning(workspaceId); gate.Release(); }
    }

    /// <summary>A sidecar's address as the workspace's network policy needs it.</summary>
    private sealed record SidecarAddress(string Name, string Ip, int Port);

    /// <summary>
    /// Starts the repository's <c>services:</c> on a private internal network (no way out), attaches the workspace to it, and returns the addresses
    /// the network policy must keep reachable. The workspace reaches each service by its name.
    /// </summary>
    private async Task<List<SidecarAddress>> StartServicesAsync(Workspace ws, string containerId, WorkspaceEnvironment env, WorkspaceSession session, CancellationToken ct)
    {
        var addresses = new List<SidecarAddress>();
        if (env.Services.Count == 0) return addresses;
        var o = Opts;
        var network = $"bb-wsn-{ByteBench.Services.Ci.CiDocker.InstanceKey}-{ws.PublicId[..10]}";
        session.ProvisionLog.Append("Creating the private network for the services…");
        await backend.CreateInternalNetworkAsync(network, ws.Id, ct);
        await backend.ConnectNetworkAsync(containerId, network, ct);
        var index = 0;
        foreach (var s in env.Services)
        {
            session.ProvisionLog.Append($"Starting service '{s.Name}' ({s.Image})…");
            var sidecar = await backend.StartSidecarAsync(new WorkspaceSidecarSpec(ws.Id, $"bb-wss-{ByteBench.Services.Ci.CiDocker.InstanceKey}-{ws.PublicId[..10]}-{index++}", s.Image, network, s.Name, s.Env,
                new WorkspaceLimits("1g", o.Cpus, 512, null)), session.ProvisionLog.Append, ct);
            foreach (var port in s.Ports) addresses.Add(new SidecarAddress(s.Name, sidecar.IpAddress, port));
        }
        return addresses;
    }

    /// <summary>Waits (from inside the workspace, through its own network policy) until every service port accepts connections; a service that never does fails the start with its last output.</summary>
    private async Task WaitForServicesAsync(string containerId, List<SidecarAddress> services, WorkspaceSession session, CancellationToken ct)
    {
        var limit = Math.Max(5, Opts.ServiceStartTimeoutSeconds);
        foreach (var s in services)
        {
            session.ProvisionLog.Append($"Waiting for {s.Name}:{s.Port}…");
            var r = await backend.ExecAsync(containerId, new ExecSpec(["bash", "-c",
                "for i in $(seq 1 \"$3\"); do if (exec 3<>\"/dev/tcp/$1/$2\") 2>/dev/null; then exit 0; fi; sleep 1; done; exit 1", "bash", s.Name, s.Port.ToString(), limit.ToString()])
            { Timeout = TimeSpan.FromSeconds(limit + 10) }, ct);
            if (!r.Ok) throw new InvalidOperationException($"Service '{s.Name}' did not accept connections on port {s.Port} within {limit} seconds.");
        }
    }

    /// <summary>
    /// Writes what the container needs before the IDE starts: the environment file (dependency cache locations, the repository's plain
    /// variables, secrets; mode 600 in the developer's home, unreadable to the AI assistant's user), the shell hook, and the user's own
    /// VS Code settings and shell snippet. Secret values only travel through stdin.
    /// </summary>
    private async Task WriteEnvironmentAsync(Workspace ws, string containerId, WorkspaceProvisionPlan plan, WorkspaceSession session, CancellationToken ct)
    {
        // Repository secrets reach only people who can push: a read-only visitor opening a workspace must not get them.
        var secrets = await secretStore.ResolveAsync(ws.OwnerId, ws.RepositoryId, includeRepository: ws.CanPush, ct);
        session.Redactor.SetValues(secrets.Values);
        var file = WorkspaceTasks.BuildEnvFile(plan.Env.Env, secrets);
        // The developer's home is private to the developer: collaborators have their own OS user (in the same group) and must not read ~/.bb-env, the git credential or the IDE token.
        await RunOrThrowAsync(containerId, new ExecSpec(["chmod", "700", "/home/coder"]), "securing the home directory", ct);
        await RunOrThrowAsync(containerId, new ExecSpec(["sh", "-c", "umask 077; cat > \"$HOME/.bb-env\""]) { Stdin = file }, "storing the environment", ct);
        if (secrets.Count > 0)
            await audit.RecordAsync(WorkspaceAuditActions.SecretInjected, ws, ws.OwnerId, new { names = secrets.Keys.Order(StringComparer.Ordinal).ToList(), count = secrets.Count });
        if (secrets.Count > 0) session.ProvisionLog.Append($"Injected {secrets.Count} secret(s) as environment variables: {string.Join(", ", secrets.Keys.Order(StringComparer.Ordinal))}");
        await RunOrThrowAsync(containerId, new ExecSpec(["sh", "-c",
            "touch \"$HOME/.bashrc\"; grep -q bb-env \"$HOME/.bashrc\" || printf '%s\\n' \"$1\" >> \"$HOME/.bashrc\"", "sh",
            "[ -f ~/.bb-env ] && . ~/.bb-env; [ -f ~/.bb-user-rc ] && . ~/.bb-user-rc  # ByteBench"]), "configuring the shell", ct);

        UserWorkspaceSettings? prefs;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            prefs = await db.UserWorkspaceSettings.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == ws.OwnerId, ct);
        if (!string.IsNullOrWhiteSpace(prefs?.ShellSnippet))
            await RunOrThrowAsync(containerId, new ExecSpec(["sh", "-c", "umask 077; cat > \"$HOME/.bb-user-rc\""]) { Stdin = prefs!.ShellSnippet }, "applying your shell snippet", ct);
        else await backend.ExecAsync(containerId, new ExecSpec(["sh", "-c", "rm -f \"$HOME/.bb-user-rc\""]), ct);
        if (!string.IsNullOrWhiteSpace(prefs?.SettingsJson))
            await RunOrThrowAsync(containerId, new ExecSpec(["sh", "-c", "d=\"$HOME/.openvscode-server/data/Machine\"; mkdir -p \"$d\" && cat > \"$d/settings.json\""]) { Stdin = prefs!.SettingsJson }, "applying your VS Code settings", ct);
    }

    private static async Task<bool> StillStartingAsync(AppDbContext db, Workspace ws)
    {
        await db.Entry(ws).ReloadAsync();
        return ws.State == WorkspaceState.Starting;
    }

    internal async Task ApplyEgressAsync(string containerId, string image, string mode, CancellationToken ct, IReadOnlyList<string>? extraAllow = null)
    {
        var git = GitBase();
        var allow = new List<string>(extraAllow ?? []);
        var ip = git.HostNameType == UriHostNameType.Dns ? await ResolveInContainerAsync(containerId, git.Host, ct) : git.Host;
        if (ip != null) allow.Add($"{ip}:{git.Port}");
        else if (mode != "open") throw new InvalidOperationException($"The container can't resolve ByteBench's git host '{git.Host}' (Workspaces:GitBaseUrl).");
        if (mode == WorkspaceEgressPolicy.Allowlist)
        {
            // Only the git endpoint and the filtering proxy stay reachable: no direct connections and no DNS (names are resolved by the proxy).
            var (proxyHost, proxyPort) = egress.ProxyAddress(git);
            var proxyIp = proxyHost.Equals(git.Host, StringComparison.OrdinalIgnoreCase) ? ip
                : System.Net.IPAddress.TryParse(proxyHost, out _) ? proxyHost : await ResolveInContainerAsync(containerId, proxyHost, ct);
            if (proxyIp == null) throw new InvalidOperationException($"The container can't resolve the egress proxy host '{proxyHost}' (Workspaces:Egress:ProxyHost).");
            allow.Add($"{proxyIp}:{proxyPort}");
            mode = WorkspaceEgressPolicy.None;
        }
        await backend.ApplyEgressAsync(containerId, image, new EgressPolicy(mode, allow), ct);
    }

    private async Task<string?> ResolveInContainerAsync(string containerId, string host, CancellationToken ct)
    {
        var r = await backend.ExecAsync(containerId, new ExecSpec(["getent", "ahostsv4", host]) { Timeout = TimeSpan.FromSeconds(10) }, ct);
        var first = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return r.Ok && System.Net.IPAddress.TryParse(first, out _) ? first : null;
    }

    /// <summary>
    /// Clones (or, when the workspace's volume already holds the checkout, fetches) and fetches submodules one by one, so a submodule that can't be
    /// fetched is reported by name with the reason (<c>BB-WARN:</c> lines) while the rest and the workspace carry on. Only http(s) transports are
    /// allowed (no file:// or ext:: submodules), every command is time-limited and never prompts. <c>$1</c> repository URL, <c>$2</c> directory,
    /// <c>$3</c> ref, <c>$4</c> 1 to fetch submodules, <c>$5</c>/<c>$6</c> an optional URL prefix rewrite (the server's public address → the
    /// address containers use), <c>$7</c> seconds per submodule. The credential is the git credential store's, which only answers for the server's own host.
    /// </summary>
    public const string CloneScript = """
        set -e
        umask 002
        export GIT_ALLOW_PROTOCOL=http:https GIT_TERMINAL_PROMPT=0
        if [ -n "$5" ]; then git config --global "url.$6.insteadOf" "$5"; fi
        if [ -d "$2/.git" ]; then
            cd "$2"
            git fetch --quiet origin || echo "BB-WARN: could not fetch from the server; the workspace keeps its previous checkout."
        else
            git clone --no-checkout -- "$1" "$2"
            cd "$2"
            if git show-ref --verify --quiet "refs/remotes/origin/$3"; then git checkout -q -B "$3" --track "origin/$3"; else git checkout -q --detach "$3"; fi
        fi
        if [ "$4" = 1 ] && [ -f .gitmodules ]; then
            git config -f .gitmodules --get-regexp '^submodule\..*\.path$' | while read -r key path; do
                [ -n "$path" ] || continue
                echo "Fetching submodule $path…"
                err=$(timeout "$7" git -c protocol.file.allow=never submodule update --init --recursive --force -- "$path" 2>&1) || {
                    reason=$(printf '%s' "$err" | grep -v '^[[:space:]]*$' | tail -n 3 | tr '\n' ' ' | cut -c1-400)
                    echo "BB-WARN: submodule '$path' could not be fetched: $reason"
                }
            done
        fi
        """;

    /// <summary>
    /// After <see cref="CloneScript"/> fetched into a volume that a prebuild seeded: puts the checkout on the requested ref (<c>$1</c> directory, <c>$2</c> ref,
    /// <c>$3</c> 1 to update submodules). Only for a brand-new workspace, so nothing of the user's can be lost; untracked files (installed dependencies) stay.
    /// </summary>
    private const string CheckoutPrebuiltScript = """
        set -e
        umask 002
        export GIT_ALLOW_PROTOCOL=http:https GIT_TERMINAL_PROMPT=0
        cd "$1"
        git reset -q --hard
        if git show-ref --verify --quiet "refs/remotes/origin/$2"; then git checkout -q -B "$2" --track "origin/$2"; git reset -q --hard "origin/$2"; else git checkout -q --detach "$2"; fi
        if [ "$3" = 1 ] && [ -f .gitmodules ]; then git submodule update -q --init --recursive --force || echo "BB-WARN: submodules could not be updated"; fi
        """;

    /// <summary>Turns git's raw message about a failed submodule into something that says what to do.</summary>
    internal static string FriendlySubmoduleWarning(string line)
    {
        // The start of the line names the submodule: keep it, drop the end if it is long (Safe keeps the end, which suits other messages).
        var s = Regex.Replace(Regex.Replace(line.Replace("BB-WARN:", "").Trim(), @"gbw_[A-Za-z0-9_-]+", "gbw_…"), @"://[^/@\s]+:[^/@\s]+@", "://…@");
        if (s.Length > 600) s = s[..600] + "…";
        if (s.Contains("terminal prompts disabled", StringComparison.OrdinalIgnoreCase) || s.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
            || s.Contains("could not read Username", StringComparison.OrdinalIgnoreCase) || s.Contains("could not read Password", StringComparison.OrdinalIgnoreCase))
            s += " (it needs credentials: this workspace's token only works for this repository and the submodules on this server you can view; a private submodule on another host can't be fetched)";
        else if (s.Contains("timed out", StringComparison.OrdinalIgnoreCase) || s.Contains("exit status 124", StringComparison.OrdinalIgnoreCase)) s += " (it took too long)";
        return s;
    }

    private async Task CheckOutAsync(Workspace ws, string containerId, string gitToken, WorkspaceProvisionPlan plan, WorkspaceSession session, bool fromPrebuild, CancellationToken ct)
    {
        var git = GitBase();
        var fetchSubmodules = plan.Env.Submodules;
        if (fetchSubmodules && ws.CommitSha is { Length: > 0 } sha)
        {
            // The token is limited to this repository; submodules on this server need the same grant, read-only, and only if the owner can view them.
            var resolution = await submodules.ResolveAsync(ws.Repository.Project.Key, ws.Repository.Slug, sha, ws.OwnerId, git, ct);
            ws.SubmoduleRepoIdsJson = JsonSerializer.Serialize(resolution.RepositoryIds);
            foreach (var n in resolution.Notes) session.Warn(n);
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            await db.Workspaces.Where(w => w.Id == ws.Id).ExecuteUpdateAsync(s => s.SetProperty(w => w.SubmoduleRepoIdsJson, ws.SubmoduleRepoIdsJson), CancellationToken.None);
        }
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
        // URLs that name this server by its public address are rewritten to the address containers reach it on (a prefix rewrite: no credential involved).
        string rewriteFrom = "", rewriteTo = "";
        if (publicBase.Value is { Length: > 0 } pub && Uri.TryCreate(pub, UriKind.Absolute, out var pubUri) && !string.Equals(pubUri.Authority, git.Authority, StringComparison.OrdinalIgnoreCase))
        {
            rewriteFrom = pub.TrimEnd('/') + "/git/";
            rewriteTo = new Uri(git, "git/").ToString();
        }
        var perSubmodule = Math.Max(10, Opts.SubmoduleTimeoutSeconds);
        session.ProvisionLog.Append($"Checking out {ws.Ref}{(fetchSubmodules ? " (with submodules)" : "")}…");
        async Task<ExecResult> RunCloneAsync() => await backend.ExecStreamAsync(containerId, new ExecSpec(["sh", "-c", CloneScript, "sh", repoUrl, WorkDir(ws.Repository.Slug), ws.Ref, fetchSubmodules ? "1" : "0",
            rewriteFrom, rewriteTo, perSubmodule.ToString()])
        {
            WorkDir = WorkspaceRoot, Timeout = TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(perSubmodule * 8), MaxOutputBytes = 16 * 1024,
            Env = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" },
        }, line =>
        {
            if (line.StartsWith("BB-WARN:", StringComparison.Ordinal)) { var w = FriendlySubmoduleWarning(line); session.Warn(w); session.ProvisionLog.Append("Warning: " + w); }
            else session.ProvisionLog.Append(Safe(line));
        }, ct);
        var clone = await RunCloneAsync();
        if (clone.Ok && fromPrebuild)
        {
            // The volume was seeded from a prebuild (a clone with its setup done): move it to the requested ref. If that fails, throw it away and clone afresh.
            var move = await backend.ExecAsync(containerId, new ExecSpec(["sh", "-c", CheckoutPrebuiltScript, "sh", WorkDir(ws.Repository.Slug), ws.Ref, fetchSubmodules ? "1" : "0"])
            { WorkDir = WorkspaceRoot, Timeout = TimeSpan.FromMinutes(5), MaxOutputBytes = 16 * 1024, Env = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" } }, ct);
            if (!move.Ok)
            {
                log.LogWarning("Workspace {Id}: moving the prebuilt checkout to {Ref} failed ({Output}); cloning afresh", ws.PublicId, ws.Ref, Safe(move.Output));
                session.ProvisionLog.Append("The prebuilt checkout could not be brought up to date; cloning afresh…");
                await backend.ExecAsync(containerId, new ExecSpec(["sh", "-c", "rm -rf \"$1\" /workspace/.bb", "sh", WorkDir(ws.Repository.Slug)]) { Timeout = TimeSpan.FromMinutes(2) }, ct);
                clone = await RunCloneAsync();
            }
        }
        if (!clone.Ok) throw new InvalidOperationException("Checking out the repository failed: " + Safe(clone.Output.Replace("BB-WARN:", "")));
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
                var session = runtime.Session(workspaceId);
                session.CancelAllTasks(); // the container's processes end with the restart
                var container = await backend.RestartAsync(containerId, IdePort, ct);
                await backend.WaitUntilWaitingAsync(containerId, ct);
                await ApplyEgressAsync(containerId, ws.Image, ws.EgressMode, ct, session.EgressAllow);
                await WriteTokenAndStartAsync(containerId, live.ConnectionToken, ct);
                await WaitHealthyAsync(containerId, ct);
                var restarted = new LiveWorkspace(ws.Id, ws.PublicId, ws.OwnerId, containerId, container.IdeHostPort, IdePort, live.ConnectionToken);
                await collaborators.RestoreAsync(restarted, ct); // the restart ended their IDE servers; their homes and credentials survived
                ws.PortMapJson = JsonSerializer.Serialize(new Dictionary<string, int> { [IdePort.ToString()] = container.IdeHostPort });
                ws.State = WorkspaceState.Running;
                ws.LastActivityAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                runtime.Register(restarted);
                await audit.RecordAsync("workspace.restarted", ws, actorId);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log.LogWarning(ex, "Workspace {Id} failed to restart", ws.PublicId);
                await FailAsync(workspaceId, containerId, ex is OperationCanceledException ? "The workspace did not come back in time." : Safe(ex.Message), keepVolumes: true);
            }
        }
        finally { gate.Release(); }
    }

    // ---------- stop & fail ----------

    /// <summary>Stops a workspace: access is cut first, then the container is removed and the git token revoked. Idempotent.</summary>
    public async Task StopAsync(int workspaceId, string reason, int? actorId)
    {
        runtime.Forget(workspaceId); // no more proxying, whatever happens next
        egress.Forget(workspaceId);
        await shares.EndAllAsync(workspaceId, "workspace stopped", actorId); // sharing never outlives the workspace
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
            runtime.FindSession(workspaceId)?.Shutdown();
            if (ws.ContainerId != null) await RemoveQuietlyAsync(ws.ContainerId);
            await RemoveAuxiliaryQuietlyAsync(workspaceId);
            ws.State = WorkspaceState.Stopped; // the volumes stay (Resume, or Delete / the retention period removes them)
            ws.StoppedAt = DateTime.UtcNow;
            ws.StopReason = reason.Length > 100 ? reason[..100] : reason;
            await db.SaveChangesAsync(CancellationToken.None);
            await audit.RecordAsync("workspace.stopped", ws, actorId, new { reason, by = actorId == null ? "system" : actorId == ws.OwnerId ? "owner" : "admin" });
        }
        finally { gate.Release(); runtime.DropLock(workspaceId); }
    }

    private async Task FailAsync(int workspaceId, string? containerId, string error, bool keepVolumes = false)
    {
        runtime.Forget(workspaceId);
        egress.Forget(workspaceId);
        await shares.EndAllAsync(workspaceId, "workspace failed", null);
        runtime.FindSession(workspaceId)?.Shutdown();
        if (containerId != null) await RemoveQuietlyAsync(containerId);
        await RemoveAuxiliaryQuietlyAsync(workspaceId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var ws = await db.Workspaces.Include(w => w.Repository).ThenInclude(r => r.Project).FirstOrDefaultAsync(w => w.Id == workspaceId);
        if (ws == null || ws.State is WorkspaceState.Stopped or WorkspaceState.Failed or WorkspaceState.Stopping) return; // a stop in progress finishes the job
        ws.State = WorkspaceState.Failed;
        ws.GitTokenHash = null;
        ws.StoppedAt = DateTime.UtcNow;
        ws.Error = error.Length > 500 ? error[..500] : error;
        // A start that never got going leaves nothing worth keeping; a workspace that had been used keeps its files so it can be resumed.
        if (!keepVolumes && ws.VolumesKept) { await RemoveVolumesQuietlyAsync(ws.PublicId); ws.VolumesKept = false; }
        await db.SaveChangesAsync(CancellationToken.None);
        await audit.RecordAsync("workspace.failed", ws, null, new { error = ws.Error, repository = $"{ws.Repository.Project.Key}/{ws.Repository.Slug}" });
    }

    private async Task RemoveAuxiliaryQuietlyAsync(int workspaceId)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await backend.RemoveAuxiliaryAsync(workspaceId, cts.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { log.LogWarning(ex, "Could not remove the services of workspace {Id}", workspaceId); }
    }

    public async Task RemoveVolumesQuietlyAsync(string publicId)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var (work, cache) = VolumeNames(publicId);
            await backend.RemoveVolumesAsync([work, cache], cts.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { log.LogWarning(ex, "Could not remove the volumes of workspace {Id}", publicId); }
    }

    /// <summary>
    /// Deletes a workspace for good: stops it if it runs, removes its project and cache volumes and its row (the audit trail stays).
    /// Idempotent.
    /// </summary>
    public async Task DeleteAsync(int workspaceId, int? actorId)
    {
        await StopAsync(workspaceId, "deleted", actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var ws = await db.Workspaces.Include(w => w.Repository).ThenInclude(r => r.Project).FirstOrDefaultAsync(w => w.Id == workspaceId);
        if (ws == null) return;
        if (ws.IsLive) throw new InvalidOperationException("The workspace is still stopping; try again in a moment.");
        await RemoveVolumesQuietlyAsync(ws.PublicId);
        db.Workspaces.Remove(ws);
        await db.SaveChangesAsync(CancellationToken.None);
        runtime.DropSession(workspaceId);
        await audit.RecordAsync("workspace.deleted", ws, actorId, new { repository = $"{ws.Repository.Project.Key}/{ws.Repository.Slug}" });
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
