using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace GitBench.Services.Ci;

/// <summary>
/// A step's container to run: image, script, workspace and the non-secret <c>GITBENCH_*</c> variables. CI secret
/// variables go through <paramref name="EnvFile"/> (a mode-600 temp file, see <see cref="CiRunner"/>) so their values
/// never appear on a command line.
/// </summary>
public sealed record CiContainerSpec(string Name, string Image, string Script, string Workspace, IReadOnlyDictionary<string, string> Env,
    string? EnvFile = null);

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
    /// only removes this instance's leftovers when several GitBench servers share one Docker daemon.
    /// </summary>
    public static readonly string InstanceKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(AppContext.BaseDirectory)))[..12].ToLowerInvariant();

    private static readonly string InstanceLabel = "gitbench-ci=" + InstanceKey;

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
            "--network", string.IsNullOrWhiteSpace(o.Network) ? "bridge" : o.Network,
            "-v", spec.Workspace + ":/workspace",
            "-w", "/workspace",
        };
        if (!string.IsNullOrWhiteSpace(o.Memory)) args.AddRange(["--memory", o.Memory]);
        if (!string.IsNullOrWhiteSpace(o.Cpus)) args.AddRange(["--cpus", o.Cpus]);
        if (o.PidsLimit > 0) args.AddRange(["--pids-limit", o.PidsLimit.ToString()]);
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

    /// <summary>Removes every leftover container labelled with this instance (e.g. after a crash). Best effort.</summary>
    public async Task RemoveStaleContainersAsync(CancellationToken ct)
    {
        try
        {
            var (code, ids) = await RunCaptureAsync(["ps", "-aq", "--filter", "label=" + InstanceLabel], ct);
            var list = ids.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (code != 0 || list.Length == 0) return;
            await RunCaptureAsync(["rm", "-f", .. list], ct);
            log.LogInformation("Removed {Count} stale CI container(s)", list.Length);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug(ex, "Could not list stale CI containers"); }
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
