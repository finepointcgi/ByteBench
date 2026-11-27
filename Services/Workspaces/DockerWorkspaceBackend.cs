using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ByteBench.Services.Ci;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Workspaces;

/// <summary>
/// Workspaces on the docker CLI, the way the CI runner talks to Docker (<see cref="CiDocker"/>): every argument goes through
/// <see cref="ProcessStartInfo.ArgumentList"/>, never a shell. Containers are labelled with this instance's key (so cleanup only touches
/// this server's own), run as uid 1000 with <c>--cap-drop ALL</c> and <c>no-new-privileges</c>, are never privileged and never
/// get the docker socket, a host mount or the server's environment. The only thing published is the IDE port, on 127.0.0.1.
/// </summary>
public sealed class DockerWorkspaceBackend(IOptionsMonitor<WorkspaceOptions> options, ILogger<DockerWorkspaceBackend> log) : IWorkspaceBackend
{
    public const string IdLabel = "bytebench-workspace-id";
    private static readonly string InstanceLabel = "bytebench-workspaces=" + CiDocker.InstanceKey;

    private readonly SemaphoreSlim gate = new(1, 1);
    private (bool Ok, DateTime At) lastCheck;
    private readonly HashSet<string> networks = [];

    private WorkspaceOptions Options => options.CurrentValue;

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - lastCheck.At < TimeSpan.FromSeconds(30)) return lastCheck.Ok;
            var ok = false;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var (code, output) = await RunAsync(["version", "--format", "{{.Server.Version}}"], null, timeout.Token);
                ok = code == 0 && output.Trim().Length > 0;
                if (!ok) log.LogWarning("Docker is not available for workspaces (exit {Code}): {Output}", code, output.Trim());
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.LogWarning("Docker is not available for workspaces: {Message}", ex.Message);
            }
            lastCheck = (ok, DateTime.UtcNow);
            return ok;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> ImageExistsAsync(string image, CancellationToken ct)
    {
        var (code, _) = await RunAsync(["image", "inspect", "--format", "{{.Id}}", image], null, ct);
        return code == 0;
    }

    public Task<int> BuildImageAsync(string contextDir, string image, Action<string> onLine, CancellationToken ct) =>
        RunStreamingAsync(["build", "--progress=plain", "-t", image, contextDir], onLine, ct);

    public Task<int> PullImageAsync(string image, Action<string> onLine, CancellationToken ct) => RunStreamingAsync(["pull", image], onLine, ct);

    private async Task<int> RunStreamingAsync(IEnumerable<string> args, Action<string> onLine, CancellationToken ct)
    {
        using var p = new Process { StartInfo = StartInfo(args), EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        p.WaitForExit();
        return p.ExitCode;
    }

    /// <summary>The <c>docker run</c> arguments for a workspace (public for tests and the README).</summary>
    public IReadOnlyList<string> RunArguments(WorkspaceContainerSpec spec, bool withDiskLimit = true)
    {
        var l = spec.Limits;
        var args = new List<string>
        {
            "run", "-d", "--name", spec.Name,
            "--label", InstanceLabel, "--label", $"{IdLabel}={spec.WorkspaceId}",
            "--hostname", "workspace",
            "--init",
            "--user", "1000:1000",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--network", spec.Network,
            "--add-host", spec.HostAlias + ":host-gateway",
            "--sysctl", "net.ipv6.conf.all.disable_ipv6=1",
            "--ulimit", "nofile=65536:65536", "--ulimit", "core=0",
            "--pids-limit", l.PidsLimit > 0 ? l.PidsLimit.ToString() : "-1",
        };
        if (spec.PublishIdePort) args.AddRange(["-p", $"127.0.0.1::{spec.IdePort}"]); // loopback only: never on the host's public interface
        if (!string.IsNullOrWhiteSpace(l.Memory)) args.AddRange(["--memory", l.Memory, "--memory-swap", l.Memory]);
        if (!string.IsNullOrWhiteSpace(l.Cpus)) args.AddRange(["--cpus", l.Cpus]);
        if (withDiskLimit && !string.IsNullOrWhiteSpace(l.DiskLimit)) args.AddRange(["--storage-opt", "size=" + l.DiskLimit]);
        foreach (var (k, v) in spec.Env) args.AddRange(["-e", $"{k}={v}"]);
        args.Add(spec.Image);
        return args;
    }

    public async Task<WorkspaceContainer> StartAsync(WorkspaceContainerSpec spec, CancellationToken ct)
    {
        await EnsureNetworkAsync(spec.Network, ct);
        var applied = true;
        var (code, output) = await RunAsync(RunArguments(spec), null, ct);
        if (code != 0 && output.Contains("storage-opt", StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning("Docker refused the workspace disk limit ({Output}); starting workspace {Name} without one", output.Trim(), spec.Name);
            await RunAsync(["rm", "-f", spec.Name], null, ct);
            applied = false;
            (code, output) = await RunAsync(RunArguments(spec, withDiskLimit: false), null, ct);
        }
        if (code != 0) throw new InvalidOperationException("Docker could not start the workspace container: " + Tail(output));
        var id = output.Trim().Split('\n')[^1].Trim();
        try { return new WorkspaceContainer(id, spec.PublishIdePort ? await PublishedPortAsync(id, spec.IdePort, ct) : 0, applied); }
        catch { await RemoveAsync(id, CancellationToken.None); throw; }
    }

    public async Task<WorkspaceContainer> RestartAsync(string containerId, int idePort, CancellationToken ct)
    {
        var (code, output) = await RunAsync(["restart", "-t", "3", containerId], null, ct);
        if (code != 0) throw new InvalidOperationException("Docker could not restart the workspace container: " + Tail(output));
        var published = await RunAsync(["port", containerId, idePort + "/tcp"], null, ct);
        return new WorkspaceContainer(containerId, published.Code == 0 && published.Output.Contains("127.0.0.1") ? await PublishedPortAsync(containerId, idePort, ct) : 0);
    }

    private async Task<int> PublishedPortAsync(string id, int containerPort, CancellationToken ct)
    {
        var (code, output) = await RunAsync(["port", id, containerPort + "/tcp"], null, ct);
        // "127.0.0.1:49153" (and possibly an IPv6 line)
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = line.LastIndexOf(':');
            if (code == 0 && colon > 0 && int.TryParse(line[(colon + 1)..], out var port) && line.StartsWith("127.0.0.1", StringComparison.Ordinal)) return port;
        }
        throw new InvalidOperationException("Docker did not publish the IDE port: " + Tail(output));
    }

    private async Task EnsureNetworkAsync(string name, CancellationToken ct)
    {
        lock (networks) { if (networks.Contains(name)) return; }
        var (code, _) = await RunAsync(["network", "inspect", name], null, ct);
        if (code != 0)
        {
            // icc=false: workspaces on this network can't talk to each other.
            var (c2, o2) = await RunAsync(["network", "create", "--driver", "bridge", "--opt", "com.docker.network.bridge.enable_icc=false",
                "--label", InstanceLabel, name], null, ct);
            if (c2 != 0 && !o2.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Docker could not create the workspace network: " + Tail(o2));
        }
        lock (networks) networks.Add(name);
    }

    public async Task WaitUntilWaitingAsync(string containerId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var (code, _) = await RunAsync(["exec", "-u", "1000:1000", containerId, "test", "-f", "/tmp/.bb-waiting"], null, ct);
            if (code == 0) return;
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException("The workspace container did not come up (its entrypoint never started).");
            await Task.Delay(200, ct);
        }
    }

    public async Task ApplyEgressAsync(string containerId, string image, EgressPolicy policy, CancellationToken ct)
    {
        var (code, output) = await RunAsync(["run", "--rm", "--network", "container:" + containerId, "--user", "0",
            "--cap-drop", "ALL", "--cap-add", "NET_ADMIN", "--security-opt", "no-new-privileges", "--label", InstanceLabel,
            "-e", "BB_EGRESS_MODE=" + policy.Mode, "-e", "BB_ALLOW=" + string.Join(',', policy.Allow),
            "--entrypoint", "/usr/local/bin/bb-apply-egress", image], null, ct);
        if (code != 0) throw new InvalidOperationException("Could not apply the workspace network policy: " + Tail(output));
    }

    public async Task<ExecResult> ExecAsync(string containerId, ExecSpec spec, CancellationToken ct)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(spec.Timeout.TotalSeconds));
        var args = new List<string> { "exec" };
        if (spec.Stdin != null) args.Add("-i");
        args.AddRange(["-u", spec.User == "bbai" ? "1001:1000" : "1000:1000"]);
        if (spec.WorkDir != null) args.AddRange(["-w", spec.WorkDir]);
        if (spec.Env != null) foreach (var (k, v) in spec.Env) args.AddRange(["-e", $"{k}={v}"]);
        // timeout(1) inside the container is what actually ends a runaway command; killing the docker CLI alone would leave it running.
        args.AddRange([containerId, "timeout", "-k", "3", seconds.ToString(), .. spec.Command]);

        using var p = new Process { StartInfo = StartInfo(args, redirectInput: spec.Stdin != null) };
        p.Start();
        var buffer = new CappedBuffer(spec.MaxOutputBytes);
        var pumps = Task.WhenAll(Pump(p.StandardOutput.BaseStream, buffer), Pump(p.StandardError.BaseStream, buffer));
        if (spec.Stdin != null)
        {
            try
            {
                await p.StandardInput.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(spec.Stdin), ct);
                p.StandardInput.Close();
            }
            catch (IOException) { /* the command exited without reading its input */ }
        }
        using var hard = CancellationTokenSource.CreateLinkedTokenSource(ct);
        hard.CancelAfter(spec.Timeout + TimeSpan.FromSeconds(15));
        try { await p.WaitForExitAsync(hard.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            await pumps;
            return new ExecResult(124, buffer.Text, TimedOut: true, buffer.Truncated);
        }
        await pumps;
        // timeout(1) exits 124 when it had to stop the command, 137 if it had to kill it.
        return new ExecResult(p.ExitCode, buffer.Text, p.ExitCode is 124 or 137, buffer.Truncated);
    }

    public async Task MarkReadyAsync(string containerId, CancellationToken ct)
    {
        var (code, output) = await RunAsync(["exec", "-u", "1000:1000", containerId, "touch", "/tmp/.bb-ready"], null, ct);
        if (code != 0) throw new InvalidOperationException("Could not start the IDE: " + Tail(output));
    }

    public async Task<bool> IsHealthyAsync(string containerId, CancellationToken ct)
    {
        var (code, output) = await RunAsync(["inspect", "--format", "{{.State.Running}} {{if .State.Health}}{{.State.Health.Status}}{{end}}", containerId], null, ct);
        return code == 0 && output.Trim() == "true healthy";
    }

    public async Task<Stream> ConnectAsync(WorkspaceEndpoint endpoint, int port, CancellationToken ct)
    {
        if (port == endpoint.IdeContainerPort && endpoint.IdeHostPort > 0)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, endpoint.IdeHostPort, ct);
                client.NoDelay = true;
                return new OwnedNetworkStream(client);
            }
            catch { client.Dispose(); throw; }
        }
        // Any other port: a relay inside the container, so servers bound to 127.0.0.1 work and nothing else is published.
        var p = new Process { StartInfo = StartInfo(["exec", "-i", "-u", "1000:1000", endpoint.ContainerId, "socat", "-", $"TCP:127.0.0.1:{port}"], redirectInput: true) };
        p.Start();
        _ = p.StandardError.BaseStream.CopyToAsync(Stream.Null);
        return new ProcessStream(p);
    }

    public async Task RemoveAsync(string containerId, CancellationToken ct)
    {
        var (code, output) = await RunAsync(["rm", "-f", "-v", containerId], null, ct);
        if (code != 0 && !output.Contains("No such container", StringComparison.OrdinalIgnoreCase))
            log.LogWarning("docker rm {Id} failed: {Output}", containerId, Tail(output));
    }

    public async Task<IReadOnlyList<ManagedContainer>> ListManagedAsync(CancellationToken ct)
    {
        var (code, output) = await RunAsync(["ps", "-a", "--no-trunc", "--filter", "label=" + InstanceLabel, "--format", "{{.ID}} {{.Label \"" + IdLabel + "\"}}"], null, ct);
        var list = new List<ManagedContainer>();
        if (code != 0) return list;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
            list.Add(new ManagedContainer(parts[0], parts.Length > 1 && int.TryParse(parts[1], out var id) ? id : null));
        }
        return list;
    }

    public const string PrebuildLabel = "bytebench-workspace-prebuild";

    public async Task<bool> CommitAsync(string containerId, string imageTag, CancellationToken ct)
    {
        var (code, output) = await RunAsync(["commit", "--pause=true", "--change", $"LABEL {PrebuildLabel}={CiDocker.InstanceKey}", containerId, imageTag], null, ct);
        if (code != 0) log.LogWarning("docker commit {Id} failed: {Output}", containerId, Tail(output));
        return code == 0;
    }

    public async Task<bool> RemoveImageAsync(string imageTag, CancellationToken ct)
    {
        var (code, output) = await RunAsync(["image", "rm", imageTag], null, ct);
        if (code != 0 && output.Contains("No such image", StringComparison.OrdinalIgnoreCase)) return true;
        if (code != 0) log.LogInformation("docker image rm {Tag} failed: {Output}", imageTag, Tail(output));
        return code == 0;
    }

    public async Task<IReadOnlyList<string>> ListPrebuildImagesAsync(CancellationToken ct)
    {
        var (code, output) = await RunAsync(["images", "--filter", $"label={PrebuildLabel}={CiDocker.InstanceKey}", "--format", "{{.Repository}}:{{.Tag}}"], null, ct);
        return code != 0 ? [] : output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(t => !t.Contains("<none>")).ToList();
    }

    // ---------- plumbing ----------

    private async Task<(int Code, string Output)> RunAsync(IEnumerable<string> args, string? stdin, CancellationToken ct)
    {
        using var p = new Process { StartInfo = StartInfo(args, redirectInput: stdin != null) };
        p.Start();
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        if (stdin != null) { await p.StandardInput.WriteAsync(stdin); p.StandardInput.Close(); }
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        return (p.ExitCode, await stdout + await stderr);
    }

    private ProcessStartInfo StartInfo(IEnumerable<string> args, bool redirectInput = false)
    {
        var psi = new ProcessStartInfo(Options.DockerExecutable)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = redirectInput,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private static async Task Pump(Stream from, CappedBuffer to)
    {
        var buf = new byte[8192];
        int n;
        while ((n = await from.ReadAsync(buf)) > 0) to.Write(buf.AsSpan(0, n));
    }

    private static string Tail(string s) => s.Length <= 600 ? s.Trim() : "…" + s[^600..].Trim();

    /// <summary>Collects output up to a limit; the rest is dropped.</summary>
    private sealed class CappedBuffer(int max)
    {
        private readonly MemoryStream ms = new();
        private readonly object sync = new();
        public bool Truncated { get; private set; }
        public void Write(ReadOnlySpan<byte> data)
        {
            lock (sync)
            {
                var room = max - (int)ms.Length;
                if (room <= 0) { Truncated = true; return; }
                if (data.Length > room) { Truncated = true; data = data[..room]; }
                ms.Write(data);
            }
        }
        public string Text { get { lock (sync) return Encoding.UTF8.GetString(ms.ToArray()); } }
    }

    private sealed class OwnedNetworkStream(TcpClient client) : Stream
    {
        private readonly NetworkStream inner = client.GetStream();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) client.Dispose(); base.Dispose(disposing); }
    }

    /// <summary>A bidirectional stream over a child process's stdout/stdin (the in-container relay).</summary>
    private sealed class ProcessStream(Process p) : Stream
    {
        private readonly Stream stdout = p.StandardOutput.BaseStream;
        private readonly Stream stdin = p.StandardInput.BaseStream;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => stdin.Flush();
        public override int Read(byte[] buffer, int offset, int count) => stdout.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => stdout.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) { stdin.Write(buffer, offset, count); stdin.Flush(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await stdin.WriteAsync(buffer, ct);
            await stdin.FlushAsync(ct);
        }
        public override Task FlushAsync(CancellationToken ct) => stdin.FlushAsync(ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                p.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
