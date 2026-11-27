using System.Diagnostics;
using System.Text.Json;
using ByteBench.Services.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ByteBench.Tests;

/// <summary>
/// The real Docker backend and the real workspace image (docker/workspace; build it with <c>docker build -t bytebench-workspace:1 docker/workspace</c>
/// or let ByteBench build it, or set BYTEBENCH_WORKSPACE_IMAGE). Skipped without Docker or the image; excluded with <c>Category!=Docker</c>.
/// </summary>
[Trait("Category", "Docker")]
public sealed class WorkspaceDockerTests : IAsyncLifetime
{
    private static readonly string Image = Environment.GetEnvironmentVariable("BYTEBENCH_WORKSPACE_IMAGE") is { Length: > 0 } e ? e : "bytebench-workspace:2";

    private static readonly Lazy<bool> Ready = new(() =>
    {
        if (!DockerFactAttribute.Available.Value) return false;
        try
        {
            var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "image", "inspect", Image }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            return p.WaitForExit(20_000) && p.ExitCode == 0;
        }
        catch (Exception) { return false; }
    });

    public sealed class WorkspaceDockerFactAttribute : FactAttribute
    {
        public WorkspaceDockerFactAttribute()
        {
            if (!Ready.Value) Skip = "Docker or the workspace image isn't available.";
        }
    }

    private sealed class StaticOptions(WorkspaceOptions value) : IOptionsMonitor<WorkspaceOptions>
    {
        public WorkspaceOptions CurrentValue => value;
        public WorkspaceOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<WorkspaceOptions, string?> listener) => null;
    }

    private readonly DockerWorkspaceBackend backend = new(new StaticOptions(new WorkspaceOptions { Image = Image }), NullLogger<DockerWorkspaceBackend>.Instance);
    private WorkspaceContainer? container;
    private const string Root = "/workspace/code";

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (container != null) await backend.RemoveAsync(container.ContainerId, CancellationToken.None);
    }

    private async Task<string> StartAsync()
    {
        container = await backend.StartAsync(new WorkspaceContainerSpec(990_001, "bb-ws-test-" + Guid.NewGuid().ToString("N")[..10], Image, "bytebench-workspaces-test", "host.docker.internal", 3000,
            new Dictionary<string, string> { ["BB_WORKDIR"] = Root }, new WorkspaceLimits("1g", "1", 512, null)), CancellationToken.None);
        await backend.WaitUntilWaitingAsync(container.ContainerId, CancellationToken.None);
        await backend.ApplyEgressAsync(container.ContainerId, Image, new EgressPolicy("public-only", []), CancellationToken.None);
        return container.ContainerId;
    }

    private async Task<ExecResult> Sh(string id, string script, string user = "coder") =>
        await backend.ExecAsync(id, new ExecSpec(["sh", "-c", script]) { User = user }, CancellationToken.None);

    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    [WorkspaceDockerFact]
    public async Task The_container_is_unprivileged_and_the_network_policy_blocks_private_ranges()
    {
        var id = await StartAsync();
        var who = await Sh(id, "id -u; grep -E 'Cap(Eff|Prm|Bnd)' /proc/self/status");
        Assert.StartsWith("1000", who.Output);
        Assert.All(who.Output.Split('\n').Where(l => l.StartsWith("Cap")), l => Assert.EndsWith("0000000000000000", l.Trim()));
        // A rejected connection (policy) answers at once with "connection refused"; an unrouted one would time out (28).
        var priv = await Sh(id, "curl -s -m 4 -o /dev/null http://10.1.2.3:80; echo $?");
        Assert.Equal("7", priv.Output.Trim());
        var gateway = await Sh(id, "curl -s -m 4 -o /dev/null http://172.17.0.1:80; echo $?");
        Assert.Equal("7", gateway.Output.Trim());
        // The workspace user can't rewrite the rules (no NET_ADMIN, not root).
        var tamper = await Sh(id, "iptables -F OUTPUT 2>&1; echo $?");
        Assert.DoesNotContain("\n0", "\n" + tamper.Output.Trim());
    }

    [WorkspaceDockerFact]
    public async Task The_assistants_tools_stay_inside_the_checkout_inside_a_real_container()
    {
        var id = await StartAsync();
        var setup = await Sh(id, $"umask 002; mkdir -p {Root}/src && cd {Root} && git init -q . && echo hello > README.md && echo code > src/a.txt && ln -s /etc escape && ln -s /etc/hostname linkfile " +
                                 "&& git add README.md src && git -c user.name=t -c user.email=t@x commit -qm init && umask 077 && echo 'http://u:gbw_secret@h' > ~/.git-credentials");
        Assert.True(setup.Ok, setup.Output);
        var tools = new WorkspaceAiTools(backend, id, "code", new WorkspaceAiOptions { MaxOutputBytes = 2000, CommandTimeoutSeconds = 3 });
        var ct = CancellationToken.None;

        var (read, err) = await tools.ExecuteAsync("read_file", J(new { path = "README.md" }), ct);
        Assert.False(err, read);
        Assert.Contains("1\thello", read);

        // Symlinks that leave the checkout are refused, for reads and writes.
        foreach (var path in new[] { "escape/passwd", "linkfile", "escape" })
        {
            var (msg, e) = await tools.ExecuteAsync("read_file", J(new { path }), ct);
            Assert.True(e, $"{path}: {msg}");
            Assert.DoesNotContain("root:", msg);
        }
        var (w1, werr1) = await tools.ExecuteAsync("write_file", J(new { path = "escape/pwned", content = "x" }), ct);
        Assert.True(werr1, w1);
        var (w2, werr2) = await tools.ExecuteAsync("write_file", J(new { path = "linkfile", content = "x" }), ct);
        Assert.True(werr2, w2);
        Assert.DoesNotContain("pwned", (await Sh(id, "ls /etc")).Output);

        // A write works, lands in the checkout, and the developer (another user, same group) can edit the file.
        var (w3, werr3) = await tools.ExecuteAsync("write_file", J(new { path = "docs/new file.md", content = "line1\nline2\n" }), ct);
        Assert.False(werr3, w3);
        var edit = await Sh(id, $"echo more >> '{Root}/docs/new file.md' && cat '{Root}/docs/new file.md'");
        Assert.True(edit.Ok, edit.Output);
        var (ed, eerr) = await tools.ExecuteAsync("edit_file", J(new { path = "docs/new file.md", find = "line2", replace = "LINE2" }), ct);
        Assert.False(eerr, ed);
        Assert.Contains("LINE2", (await Sh(id, $"cat '{Root}/docs/new file.md'")).Output);
        var (gw, gerr) = await tools.ExecuteAsync("write_file", J(new { path = ".git/hooks/pre-commit", content = "#!/bin/sh\ncurl evil" }), ct);
        Assert.True(gerr, gw);

        var (list, lerr) = await tools.ExecuteAsync("list_files", J(new { }), ct);
        Assert.False(lerr, list);
        Assert.Contains("README.md", list);
        var (search, serr) = await tools.ExecuteAsync("search_code", J(new { query = "hello" }), ct);
        Assert.False(serr, search);
        Assert.Contains("README.md:1", search);
    }

    [WorkspaceDockerFact]
    public async Task Commands_run_as_the_assistant_user_without_git_credentials_with_capped_output_and_a_real_timeout()
    {
        var id = await StartAsync();
        await Sh(id, $"mkdir -p {Root} && cd {Root} && git init -q . && umask 077 && echo 'http://u:gbw_secret@h' > ~/.git-credentials && git config --global user.name dev");
        var tools = new WorkspaceAiTools(backend, id, "code", new WorkspaceAiOptions { MaxOutputBytes = 2000, CommandTimeoutSeconds = 3 });
        var ct = CancellationToken.None;

        var (who, _) = await tools.ExecuteAsync("run_command", J(new { command = "id -un; pwd" }), ct);
        Assert.Contains("bbai", who);
        Assert.Contains(Root, who);

        var (creds, cerr) = await tools.ExecuteAsync("run_command", J(new { command = "cat /home/coder/.git-credentials; cat ~coder/.gitconfig" }), ct);
        Assert.True(cerr);
        Assert.DoesNotContain("gbw_secret", creds);

        var (push, perr) = await tools.ExecuteAsync("run_command", J(new { command = "git push" }), ct);
        Assert.True(perr);
        Assert.Contains("not available to you", push);

        var (big, _) = await tools.ExecuteAsync("run_command", J(new { command = "yes | head -c 500000" }), ct);
        Assert.True(big.Length < 3000, $"output was {big.Length} chars");
        Assert.Contains("truncated", big);

        var started = DateTime.UtcNow;
        var (slow, serr) = await tools.ExecuteAsync("run_command", J(new { command = "sleep 60; echo never" }), ct);
        Assert.True(serr);
        Assert.Contains("Timed out", slow);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
        // The process inside the container was really stopped, not just abandoned by the docker CLI.
        await Task.Delay(500);
        Assert.DoesNotContain("sleep 60", (await Sh(id, "ps -eo args")).Output);
    }

    [Fact]
    public void Docker_sizes_are_parsed()
    {
        Assert.Equal(0, DockerWorkspaceBackend.ParseSize("0B"));
        Assert.Equal(1_500_000_000, DockerWorkspaceBackend.ParseSize("1.5GB"));
        Assert.Equal(300_000, DockerWorkspaceBackend.ParseSize("300kB".ToUpperInvariant().Replace("KB", "kB")));
        Assert.Null(DockerWorkspaceBackend.ParseSize("n/a"));
    }

    [WorkspaceDockerFact]
    public async Task Volumes_are_writable_by_the_workspace_user_survive_the_container_and_are_removed_on_request()
    {
        var work = "bb-wsv-test-" + Guid.NewGuid().ToString("N")[..8] + "-work";
        var cache = work.Replace("-work", "-cache");
        await backend.CreateVolumeAsync(work, 990_002, CancellationToken.None);
        await backend.CreateVolumeAsync(cache, 990_002, CancellationToken.None);
        try
        {
            WorkspaceContainerSpec Spec() => new(990_002, "bb-ws-test-" + Guid.NewGuid().ToString("N")[..10], Image, "bytebench-workspaces-test", "host.docker.internal", 3000,
                new Dictionary<string, string>(), new WorkspaceLimits("1g", "1", 512, null)) { Mounts = [new(work, "/workspace"), new(cache, "/home/coder/.bbcache")] };
            container = await backend.StartAsync(Spec(), CancellationToken.None);
            var id = container.ContainerId;
            await backend.WaitUntilWaitingAsync(id, CancellationToken.None);
            var write = await Sh(id, "mkdir -p /workspace/app/node_modules/x && echo kept > /workspace/app/node_modules/x/f && echo cached > /home/coder/.bbcache/c && id -u");
            Assert.True(write.Ok, write.Output);
            await backend.RemoveAsync(id, CancellationToken.None);

            container = await backend.StartAsync(Spec(), CancellationToken.None);
            await backend.WaitUntilWaitingAsync(container.ContainerId, CancellationToken.None);
            var read = await Sh(container.ContainerId, "cat /workspace/app/node_modules/x/f /home/coder/.bbcache/c");
            Assert.Equal("kept\ncached", read.Output.Trim());

            var listed = await backend.ListVolumesAsync(CancellationToken.None);
            Assert.Contains(listed, v => v.Name == work && v.WorkspaceId == 990_002);
            await backend.RemoveAsync(container.ContainerId, CancellationToken.None);
            container = null;
            await backend.RemoveVolumesAsync([work, cache], CancellationToken.None);
            Assert.DoesNotContain(await backend.ListVolumesAsync(CancellationToken.None), v => v.Name == work || v.Name == cache);
        }
        finally { await backend.RemoveVolumesAsync([work, cache], CancellationToken.None); }
    }

    [WorkspaceDockerFact]
    public async Task A_streamed_task_is_ended_with_its_whole_process_tree_by_the_stop_script()
    {
        var id = await StartAsync();
        var tasks = new WorkspaceTasks(backend, Microsoft.Extensions.Options.Options.Create(new WorkspaceOptions()) is var o ? new StaticOptions(o.Value) : null!, NullLogger<WorkspaceTasks>.Instance);
        var live = new LiveWorkspace(1, "abcdefghijklmnopqrst", 1, id, 0, 3000, "t");
        var session = new WorkspaceSession(1) { Env = WorkspaceEnvFile.ParseYaml("tasks:\n  loop: \"(sleep 300 &) ; echo go-$((6*7)); sleep 300\"\n").Environment! };
        var run = tasks.Start(live, session, "/workspace", "loop", out _);
        for (var i = 0; i < 50 && !run.Log.Text().Contains("go-42"); i++) await Task.Delay(100);
        Assert.Contains("go-42", run.Log.Text());
        Assert.Contains("sleep 300", (await Sh(id, "ps -eo args")).Output);
        Assert.True(await tasks.StopAsync(live, session, "loop"));
        Assert.Equal(TaskState.Stopped, run.State);
        await Task.Delay(500);
        Assert.DoesNotContain("sleep 300", (await Sh(id, "ps -eo args")).Output); // the child and the grandchild too
    }

    [WorkspaceDockerFact]
    public async Task Ports_in_the_container_are_reachable_through_the_relay_even_on_loopback_only_servers()
    {
        var id = await StartAsync();
        await Sh(id, "(python3 -m http.server 8123 --bind 127.0.0.1 >/dev/null 2>&1 &)");
        await Task.Delay(1500);
        await using var stream = await backend.ConnectAsync(new WorkspaceEndpoint(id, container!.IdeHostPort, 3000), 8123, CancellationToken.None);
        await stream.WriteAsync("GET / HTTP/1.0\r\n\r\n"u8.ToArray());
        var buf = new byte[256];
        var n = await stream.ReadAsync(buf);
        Assert.StartsWith("HTTP/1.0 200", System.Text.Encoding.ASCII.GetString(buf, 0, n));
    }
}
