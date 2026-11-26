using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Ci;

/// <summary>
/// A step's container to run: image, script, workspace and the non-secret <c>BYTEBENCH_*</c> variables. CI secret
/// variables go through <paramref name="EnvFile"/> (a mode-600 temp file, see <see cref="CiRunner"/>) so their values
/// never appear on a command line.
/// </summary>
public sealed record CiContainerSpec(string Name, string Image, string Script, string Workspace, IReadOnlyDictionary<string, string> Env,
    string? EnvFile = null, string? Network = null);

/// <summary>A service could not be started or never became ready; the message says why (and includes the service's last log lines).</summary>
public sealed class CiServiceException(string message) : Exception(message);

/// <summary>
/// A job's running <c>services:</c>: a private Docker network and one container per service, each reachable by its alias.
/// Dispose to stop the containers and remove the network.
/// </summary>
public sealed class CiServiceSet(CiDocker docker, string network, IReadOnlyList<string> containers) : IAsyncDisposable
{
    public string Network { get; } = network;
    public IReadOnlyList<string> Containers { get; } = containers;
    public async ValueTask DisposeAsync() => await docker.TeardownServicesAsync(Network, Containers);
}

/// <summary>
/// Thin wrapper over the docker CLI (singleton). Every argument goes through <see cref="ProcessStartInfo.ArgumentList"/>,
/// never a concatenated command line. Containers never get the docker socket or host environment variables; the only
/// secrets they get are the pipeline's CI variables, via <c>--env-file</c>. They always run with
/// <c>--security-opt no-new-privileges</c>.
/// </summary>
public class CiDocker(IOptionsMonitor<CiOptions> options, ILogger<CiDocker> log)
{
    /// <summary>
    /// Identifies this server instance (from its install path). Containers are labelled with it, so startup cleanup
    /// only removes this instance's leftovers when several ByteBench servers share one Docker daemon.
    /// </summary>
    public static readonly string InstanceKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(AppContext.BaseDirectory)))[..12].ToLowerInvariant();

    private static readonly string InstanceLabel = "bytebench-ci=" + InstanceKey;

    private readonly SemaphoreSlim checkGate = new(1, 1);
    private (bool Ok, DateTime At) lastCheck;

    private CiOptions Options => options.CurrentValue;

    /// <summary>True if the docker CLI can reach a daemon. Cached for 30 seconds.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        await checkGate.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - lastCheck.At < TimeSpan.FromSeconds(30)) return lastCheck.Ok;
            var ok = false;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var (code, output) = await RunCaptureAsync(["version", "--format", "{{.Server.Version}}"], timeout.Token);
                ok = code == 0 && output.Trim().Length > 0;
                if (!ok) log.LogWarning("Docker is not available (exit {Code}): {Output}", code, output.Trim());
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.LogWarning("Docker is not available: {Message}", ex.Message);
            }
            lastCheck = (ok, DateTime.UtcNow);
            return ok;
        }
        finally { checkGate.Release(); }
    }

    /// <summary>The <c>docker run</c> arguments for a step (exposed for tests and the README).</summary>
    public IReadOnlyList<string> RunArguments(CiContainerSpec spec)
    {
        var o = Options;
        var args = new List<string>
        {
            "run", "--rm", "--name", spec.Name,
            "--label", InstanceLabel,
            "--security-opt", "no-new-privileges",
            "--network", spec.Network ?? (string.IsNullOrWhiteSpace(o.Network) ? "bridge" : o.Network),
            "-v", spec.Workspace + ":/workspace",
            "-w", "/workspace",
        };
        if (!string.IsNullOrWhiteSpace(o.Memory)) args.AddRange(["--memory", o.Memory]);
        if (!string.IsNullOrWhiteSpace(o.Cpus)) args.AddRange(["--cpus", o.Cpus]);
        if (o.PidsLimit > 0) args.AddRange(["--pids-limit", o.PidsLimit.ToString()]);
        if (!string.IsNullOrWhiteSpace(o.User)) args.AddRange(["--user", o.User.Trim()]);
        if (o.DropCapabilities) args.AddRange(["--cap-drop", "ALL"]);
        if (spec.EnvFile != null) args.AddRange(["--env-file", spec.EnvFile]); // CI secret variables; -e below wins on clashes
        foreach (var (k, v) in spec.Env) args.AddRange(["-e", $"{k}={v}"]);
        // sh -e: a multi-line step fails at its first failing command.
        args.AddRange(["--entrypoint", "sh", spec.Image, "-e", "-c", spec.Script]);
        return args;
    }

    /// <summary>
    /// Runs one step container, sending each output line (stdout and stderr, as they arrive) to <paramref name="onLine"/>.
    /// When <paramref name="ct"/> is cancelled the container is killed with <c>docker kill</c>. Returns the exit code.
    /// </summary>
    public async Task<int> RunStepAsync(CiContainerSpec spec, Action<string> onLine, CancellationToken ct)
    {
        using var p = new Process { StartInfo = StartInfo(RunArguments(spec)), EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        await using var reg = ct.Register(() => _ = KillAsync(spec.Name, p));
        await p.WaitForExitAsync(CancellationToken.None);
        p.WaitForExit(); // flush the async output readers
        return p.ExitCode;
    }

    /// <summary>Kills a step container by name (and the local docker CLI process, in case the container never started).</summary>
    public async Task KillAsync(string containerName, Process? cli = null)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await RunCaptureAsync(["kill", containerName], cts.Token);
        }
        catch (Exception ex) { log.LogWarning(ex, "docker kill {Name} failed", containerName); }
        try
        {
            if (cli is { HasExited: false })
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                if (!cli.HasExited) cli.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { /* already exited */ }
    }

    /// <summary>Removes every leftover container and service network labelled with this instance (e.g. after a crash). Best effort.</summary>
    public async Task RemoveStaleContainersAsync(CancellationToken ct)
    {
        try
        {
            var (code, ids) = await RunCaptureAsync(["ps", "-aq", "--filter", "label=" + InstanceLabel], ct);
            var list = ids.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (code == 0 && list.Length > 0)
            {
                await RunCaptureAsync(["rm", "-f", .. list], ct);
                log.LogInformation("Removed {Count} stale CI container(s)", list.Length);
            }
            var (ncode, nets) = await RunCaptureAsync(["network", "ls", "-q", "--filter", "label=" + InstanceLabel], ct);
            var networks = nets.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (ncode == 0 && networks.Length > 0) await RunCaptureAsync(["network", "rm", .. networks], ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug(ex, "Could not list stale CI containers"); }
    }

    // ----------------------------------------------------------------- services:

    /// <summary>The flags every sidecar container gets: the job containers' isolation, minus <c>--user</c> (a database image needs its own user).</summary>
    public IReadOnlyList<string> ServiceRunArguments(string name, string network, CiServiceDefinition service)
    {
        var o = Options;
        var args = new List<string>
        {
            "run", "-d", "--name", name,
            "--label", InstanceLabel,
            "--security-opt", "no-new-privileges",
            "--network", network, "--network-alias", service.Alias,
        };
        if (!string.IsNullOrWhiteSpace(o.Memory)) args.AddRange(["--memory", o.Memory]);
        if (!string.IsNullOrWhiteSpace(o.Cpus)) args.AddRange(["--cpus", o.Cpus]);
        if (o.PidsLimit > 0) args.AddRange(["--pids-limit", o.PidsLimit.ToString()]);
        if (o.DropCapabilities) args.AddRange(["--cap-drop", "ALL"]);
        foreach (var (k, v) in service.Variables ?? new Dictionary<string, string>()) args.AddRange(["-e", $"{k}={v}"]);
        args.Add(service.Image);
        if (service.Command != null) args.AddRange(service.Command);
        return args;
    }

    /// <summary>
    /// Starts a job's services on a new private network (<c>--internal</c>, so no outside access, when <c>Ci:Network</c> is
    /// <c>none</c>) and waits until each is ready: its image's HEALTHCHECK says healthy, else its <c>port</c> accepts a connection,
    /// else it is simply still running. A failure removes everything again and throws <see cref="CiServiceException"/>.
    /// </summary>
    public async Task<CiServiceSet> StartServicesAsync(string jobKey, IReadOnlyList<CiServiceDefinition> services, Action<string> onLine, CancellationToken ct)
    {
        var network = $"bytebench-ci-net-{jobKey}-{Guid.NewGuid().ToString("N")[..6]}";
        var netArgs = new List<string> { "network", "create", "--label", InstanceLabel };
        if (Options.Network == "none") netArgs.Add("--internal");
        netArgs.Add(network);
        var (ncode, nout) = await RunCaptureAsync(netArgs, ct);
        if (ncode != 0) throw new CiServiceException($"Could not create the services network: {nout.Trim()}");

        var started = new List<string>();
        try
        {
            var timeout = TimeSpan.FromSeconds(Math.Clamp(Options.ServiceStartTimeoutSeconds, 5, 600));
            var names = new List<(CiServiceDefinition Def, string Name)>();
            for (var i = 0; i < services.Count; i++)
            {
                var name = $"bytebench-ci-svc-{jobKey}-{i}-{Guid.NewGuid().ToString("N")[..6]}";
                onLine($"[bytebench] Starting service '{services[i].Alias}' ({services[i].Image})...");
                var (code, output) = await RunCaptureAsync(ServiceRunArguments(name, network, services[i]), ct);
                if (code != 0) throw new CiServiceException($"Service '{services[i].Alias}' ({services[i].Image}) could not be started: {Tail(output)}");
                started.Add(name);
                names.Add((services[i], name));
            }
            foreach (var (def, name) in names)
            {
                await WaitReadyAsync(def, name, network, timeout, ct);
                onLine($"[bytebench] Service '{def.Alias}' is ready.");
            }
            return new CiServiceSet(this, network, started);
        }
        catch
        {
            await TeardownServicesAsync(network, started);
            throw;
        }
    }

    private async Task WaitReadyAsync(CiServiceDefinition def, string container, string network, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var stableSince = DateTime.UtcNow;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var (code, state) = await RunCaptureAsync(["inspect", "-f", "{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{end}}", container], ct);
            var parts = state.Trim().Split('|');
            if (code != 0 || parts[0] != "running")
                throw new CiServiceException($"Service '{def.Alias}' ({def.Image}) stopped before it was ready ({(code == 0 ? parts[0] : "gone")}). Its last output:\n{await LogsAsync(container)}");
            var health = parts.Length > 1 ? parts[1] : "";
            if (health == "healthy") return;
            if (health == "unhealthy") throw new CiServiceException($"Service '{def.Alias}' ({def.Image}) reports unhealthy. Its last output:\n{await LogsAsync(container)}");
            if (health == "")
            {
                if (def.Port is { } port)
                {
                    // alpine's busybox nc: the port accepts a connection from inside the services network.
                    var (pcode, _) = await RunCaptureAsync(["run", "--rm", "--label", InstanceLabel, "--security-opt", "no-new-privileges", "--network", network,
                        "--memory", "64m", "--entrypoint", "sh", CiFile.DefaultImage, "-c", $"nc -z -w 2 {def.Alias} {port}"], ct);
                    if (pcode == 0) return;
                }
                else if (DateTime.UtcNow - stableSince >= TimeSpan.FromSeconds(2)) return; // no healthcheck, no port: running is all we can know
            }
            if (DateTime.UtcNow > deadline)
                throw new CiServiceException($"Service '{def.Alias}' ({def.Image}) was not ready after {timeout.TotalSeconds:0} seconds" +
                    (def.Port == null && health == "" ? "" : health != "" ? $" (health: {health})" : $" (port {def.Port} not open)") + $". Its last output:\n{await LogsAsync(container)}");
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }

    /// <summary>The last lines of a service container's output (for failure messages).</summary>
    public async Task<string> LogsAsync(string container)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var (_, output) = await RunCaptureAsync(["logs", "--tail", "30", container], cts.Token);
            return Tail(output);
        }
        catch (Exception) { return "(no output)"; }
    }

    private static string Tail(string text)
    {
        var t = text.Trim();
        return t.Length == 0 ? "(no output)" : t.Length > 2000 ? "…" + t[^2000..] : t;
    }

    /// <summary>Stops and removes a job's service containers and its network. Best effort.</summary>
    public async Task TeardownServicesAsync(string network, IEnumerable<string> containers)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var list = containers.ToList();
            if (list.Count > 0) await RunCaptureAsync(["rm", "-f", .. list], cts.Token);
            await RunCaptureAsync(["network", "rm", network], cts.Token);
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not remove the services of a CI job ({Network})", network); }
    }

    /// <summary>
    /// Deletes files a container created as root that the server user can't delete itself, by running
    /// <paramref name="image"/> (already pulled) once more with the workspace mounted. Best effort.
    /// </summary>
    public async Task CleanWorkspaceAsync(string workspace, string image, CancellationToken ct)
    {
        try
        {
            await RunCaptureAsync(["run", "--rm", "--label", InstanceLabel, "--security-opt", "no-new-privileges",
                "--network", "none", "-v", workspace + ":/workspace", "--entrypoint", "sh", image,
                "-c", "find /workspace -mindepth 1 -delete"], ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug(ex, "Workspace cleanup container failed"); }
    }

    private async Task<(int Code, string Output)> RunCaptureAsync(IEnumerable<string> args, CancellationToken ct)
    {
        using var p = new Process { StartInfo = StartInfo(args) };
        p.Start();
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        return (p.ExitCode, await stdout + await stderr);
    }

    private ProcessStartInfo StartInfo(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(Options.DockerExecutable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }
}
