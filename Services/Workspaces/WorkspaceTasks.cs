using System.Text;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Workspaces;

/// <summary>
/// Runs a workspace's setup commands and tasks as processes inside its container (<c>docker exec</c>, as the developer's user, the same exec path as
/// everything else). Commands are handed to the container's shell exactly as the repository author wrote them: they are arguments to <c>bash -c</c>
/// inside the container, never part of a host command line. Output goes to a capped, secret-masked log; stopping kills the whole process tree.
/// </summary>
public sealed class WorkspaceTasks(IWorkspaceBackend backend, IOptionsMonitor<WorkspaceOptions> options, ILogger<WorkspaceTasks> log)
{
    public const int MaxRunningTasks = 5;
    public const string SetupMarker = "/workspace/.bb/setup.hash";

    /// <summary>
    /// Sources the environment file (caches, the repository's variables, secrets), records the pid for <see cref="StopScript"/> and replaces itself
    /// with the command. <c>$1</c>: pid file, <c>$2</c>: the command.
    /// </summary>
    private const string RunScript = """
        [ -f "$HOME/.bb-env" ] && . "$HOME/.bb-env"
        echo $$ > "$1"
        exec bash -c "$2"
        """;

    /// <summary>Ends the process tree of the pid in <c>$1</c>: TERM, a moment to exit, then KILL.</summary>
    private const string StopScript = """
        pid=$(cat "$1" 2>/dev/null) || exit 0
        [ -n "$pid" ] || exit 0
        kill_tree() { for c in $(pgrep -P "$1" 2>/dev/null); do kill_tree "$c" "$2"; done; kill "-$2" "$1" 2>/dev/null || true; }
        kill_tree "$pid" TERM
        sleep 2
        kill_tree "$pid" KILL
        rm -f "$1"
        exit 0
        """;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> running = new();

    /// <summary>Completes when no task or setup run started through this object is still going (tests).</summary>
    public Task WhenIdleAsync() => Task.WhenAll(running.Keys.ToList());

    private ExecSpec Spec(string workDir, string pidFile, string command, TimeSpan timeout) => new(["sh", "-c", RunScript, "sh", pidFile, command])
    {
        WorkDir = workDir, Timeout = timeout, MaxOutputBytes = 16 * 1024,
    };

    // ---------- setup ----------

    /// <summary>
    /// Runs the setup block once: skipped when the hash recorded in the workspace's volume matches (dependencies are still there),
    /// stops at the first failing command and keeps the output and the failure on the session for the page.
    /// </summary>
    public async Task RunSetupAsync(LiveWorkspace live, WorkspaceSession session, string workDir, CancellationToken ct)
    {
        var env = session.Env;
        if (env.Setup.Count == 0) { session.Setup = SetupState.None; return; }
        session.Setup = SetupState.Running;
        session.SetupMessage = null;
        session.SetupLog.Clear();
        try
        {
            var hash = env.SetupHash;
            var existing = await backend.ExecAsync(live.ContainerId, new ExecSpec(["sh", "-c", "cat \"$1\" 2>/dev/null || true", "sh", SetupMarker]) { Timeout = TimeSpan.FromSeconds(10) }, ct);
            if (existing.Ok && existing.Output.Trim() == hash)
            {
                session.Setup = SetupState.UpToDate;
                session.SetupMessage = "Setup is up to date: its commands didn't change since they last succeeded, so the installed dependencies were kept.";
                session.SetupLog.Append(session.SetupMessage);
                return;
            }
            var timeout = TimeSpan.FromMinutes(Math.Max(1, options.CurrentValue.SetupTimeoutMinutes));
            for (var i = 0; i < env.Setup.Count; i++)
            {
                var command = env.Setup[i];
                session.SetupLog.Append($"$ {command}");
                var pidFile = $"/tmp/.bb-setup-{i}.pid";
                var result = await backend.ExecStreamAsync(live.ContainerId, Spec(workDir, pidFile, command, timeout), session.SetupLog.Append, ct);
                if (!result.Ok)
                {
                    session.Setup = SetupState.Failed;
                    session.SetupMessage = result.TimedOut
                        ? $"Setup command {i + 1} of {env.Setup.Count} took longer than {timeout.TotalMinutes:0} minutes and was stopped: {First(command)}"
                        : $"Setup command {i + 1} of {env.Setup.Count} failed (exit code {result.ExitCode}): {First(command)}";
                    session.SetupLog.Append(session.SetupMessage);
                    return;
                }
            }
            var write = await backend.ExecAsync(live.ContainerId, new ExecSpec(["sh", "-c", "mkdir -p \"$(dirname \"$1\")\" && printf '%s' \"$2\" > \"$1\"", "sh", SetupMarker, hash]) { Timeout = TimeSpan.FromSeconds(10) }, ct);
            if (!write.Ok) log.LogWarning("Could not record the setup hash for workspace {Id}: {Output}", live.PublicId, write.Output);
            session.Setup = SetupState.Succeeded;
            session.SetupMessage = "Setup finished.";
            session.SetupLog.Append("Setup finished.");
        }
        catch (OperationCanceledException)
        {
            session.Setup = SetupState.Failed;
            session.SetupMessage = "Setup was interrupted because the workspace stopped.";
            session.SetupLog.Append(session.SetupMessage);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogWarning(ex, "Setup of workspace {Id} failed unexpectedly", live.PublicId);
            session.Setup = SetupState.Failed;
            session.SetupMessage = "Setup could not run: " + WorkspaceLifecycle.Safe(ex.Message);
            session.SetupLog.Append(session.SetupMessage);
        }
    }

    private static string First(string command)
    {
        var line = command.Split('\n')[0].Trim();
        return line.Length > 120 ? line[..120] + "…" : line;
    }

    // ---------- tasks ----------

    /// <summary>
    /// Starts a task. A task that is already running is left alone (its current state is returned). Needs a finished or failed setup:
    /// running <c>npm start</c> while <c>npm install</c> is still going would only fail confusingly.
    /// </summary>
    public WorkspaceTaskRun Start(LiveWorkspace live, WorkspaceSession session, string workDir, string taskName, out bool alreadyRunning)
    {
        var def = session.Env.Tasks.FirstOrDefault(t => t.Name == taskName)
                  ?? throw new InvalidOperationException($"There is no task named '{taskName}'. " + (session.Env.Tasks.Count == 0 ? "This workspace has no tasks (add them to .bytebench/workspace.yml)." : "Tasks: " + string.Join(", ", session.Env.Tasks.Select(t => t.Name)) + "."));
        alreadyRunning = false;
        lock (session.Tasks)
        {
            if (session.Tasks.TryGetValue(def.Name, out var current) && current.State == TaskState.Running) { alreadyRunning = true; return current; }
            if (session.Setup == SetupState.Running) throw new InvalidOperationException("Setup is still running; start the task when it has finished (see the setup log).");
            if (session.Tasks.Values.Count(t => t.State == TaskState.Running) >= MaxRunningTasks) throw new InvalidOperationException($"{MaxRunningTasks} tasks are already running; stop one first.");
            var run = new WorkspaceTaskRun(def.Name, def.Command, def.Port, def.Preview, session.Redactor);
            session.Tasks[def.Name] = run;
            run.Log.Append($"$ {def.Command}");
            var task = Task.Run(() => RunAsync(live, run, workDir));
            running[task] = 0;
            _ = task.ContinueWith(t => running.TryRemove(t, out _), TaskScheduler.Default);
            return run;
        }
    }

    private async Task RunAsync(LiveWorkspace live, WorkspaceTaskRun run, string workDir)
    {
        try
        {
            var lifetime = TimeSpan.FromHours(Math.Max(1, options.CurrentValue.MaxLifetimeHours));
            var result = await backend.ExecStreamAsync(live.ContainerId, Spec(workDir, run.PidFile, run.Command, lifetime), run.Log.Append, run.Cts.Token);
            run.ExitCode = result.ExitCode;
            run.State = run.StopRequested ? TaskState.Stopped : result.Ok ? TaskState.Succeeded : TaskState.Failed;
            if (result.TimedOut && !run.StopRequested) run.Log.Append("The task ran into the workspace's maximum lifetime and was stopped.");
        }
        catch (OperationCanceledException)
        {
            run.State = TaskState.Stopped;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogWarning(ex, "Task {Task} of workspace {Id} could not run", run.Name, live.PublicId);
            run.State = TaskState.Failed;
            run.Log.Append("The task could not be started: " + WorkspaceLifecycle.Safe(ex.Message));
        }
        finally
        {
            run.EndedAt = DateTime.UtcNow;
            if (run.State == TaskState.Running) run.State = TaskState.Failed;
            run.Log.Append(run.State switch
            {
                TaskState.Succeeded => "[task finished: exit code 0]",
                TaskState.Stopped => "[task stopped]",
                _ => $"[task failed: exit code {run.ExitCode?.ToString() ?? "unknown"}]",
            });
        }
    }

    /// <summary>Stops a running task: the process tree in the container is ended, then the docker client is released. A task that isn't running is left as it is.</summary>
    public async Task<bool> StopAsync(LiveWorkspace live, WorkspaceSession session, string taskName)
    {
        if (!session.Tasks.TryGetValue(taskName, out var run) || run.State != TaskState.Running) return false;
        run.StopRequested = true;
        try
        {
            await backend.ExecAsync(live.ContainerId, new ExecSpec(["sh", "-c", StopScript, "sh", run.PidFile]) { Timeout = TimeSpan.FromSeconds(15) }, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { log.LogDebug(ex, "Stopping task {Task} in the container failed", taskName); }
        try { run.Cts.Cancel(); } catch (ObjectDisposedException) { }
        for (var i = 0; i < 50 && run.State == TaskState.Running; i++) await Task.Delay(100);
        return true;
    }

    /// <summary>The environment file the container sources: cache locations, the repository's variables and the secrets (single-quoted, no expansion).</summary>
    public static string BuildEnvFile(IReadOnlyDictionary<string, string> repoEnv, IReadOnlyDictionary<string, string> secrets)
    {
        var sb = new StringBuilder();
        sb.Append("# Written by ByteBench when the workspace started (mode 600). Not part of the repository.\n");
        // Dependency caches live in a named volume, so they survive the container.
        foreach (var (k, v) in CacheEnvironment) sb.Append("export ").Append(k).Append('=').Append(Quote(v)).Append('\n');
        sb.Append("export PATH=\"$PATH:/home/coder/.bbcache/cargo/bin:/home/coder/.bbcache/go/bin\"\n");
        foreach (var (k, v) in repoEnv) sb.Append("export ").Append(k).Append('=').Append(Quote(v)).Append('\n');
        foreach (var (k, v) in secrets) sb.Append("export ").Append(k).Append('=').Append(Quote(v)).Append('\n');
        return sb.ToString();
    }

    /// <summary>Where each toolchain keeps its downloads, redirected into the cache volume.</summary>
    public static readonly IReadOnlyDictionary<string, string> CacheEnvironment = new Dictionary<string, string>
    {
        ["NUGET_PACKAGES"] = "/home/coder/.bbcache/nuget",
        ["npm_config_cache"] = "/home/coder/.bbcache/npm",
        ["PIP_CACHE_DIR"] = "/home/coder/.bbcache/pip",
        ["CARGO_HOME"] = "/home/coder/.bbcache/cargo",
        ["GOMODCACHE"] = "/home/coder/.bbcache/go/mod",
        ["GOCACHE"] = "/home/coder/.bbcache/go/build",
        ["GOBIN"] = "/home/coder/.bbcache/go/bin",
        ["GRADLE_USER_HOME"] = "/home/coder/.bbcache/gradle",
        ["MAVEN_OPTS"] = "-Dmaven.repo.local=/home/coder/.bbcache/m2",
        ["YARN_CACHE_FOLDER"] = "/home/coder/.bbcache/yarn",
        ["COREPACK_HOME"] = "/home/coder/.bbcache/corepack",
    };

    /// <summary>A POSIX single-quoted word: nothing inside is expanded by the shell.</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
