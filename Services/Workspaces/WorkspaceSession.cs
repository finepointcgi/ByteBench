using System.Collections.Concurrent;
using System.Text;

namespace ByteBench.Services.Workspaces;

/// <summary>Replaces the values of a workspace's secrets in anything that is shown, logged or returned to the AI assistant.</summary>
public sealed class SecretRedactor
{
    public const string Mask = "***";
    private volatile string[] values = [];

    /// <summary>Values shorter than 4 characters are not masked (masking "1" would garble every log).</summary>
    public void SetValues(IEnumerable<string> secrets) =>
        values = secrets.SelectMany(v => new[] { v }.Concat(v.Split('\n', '\r'))).Select(v => v.Trim()).Where(v => v.Length >= 4).Distinct(StringComparer.Ordinal)
            .OrderByDescending(v => v.Length).ToArray();

    public string Redact(string text)
    {
        var vs = values;
        if (vs.Length == 0 || text.Length == 0) return text;
        foreach (var v in vs) text = text.Replace(v, Mask, StringComparison.Ordinal);
        return text;
    }
}

/// <summary>A capped, thread-safe log with sequence numbers (pollers ask for "everything after n"). The oldest lines are dropped first.</summary>
public sealed class LogBuffer(SecretRedactor? redactor = null, int maxBytes = 256 * 1024, int maxLineChars = 4000)
{
    private readonly object sync = new();
    private readonly LinkedList<(long Seq, string Line)> lines = new();
    private long next = 1;
    private int bytes;

    public void Append(string line)
    {
        line = line.Replace("\r", "").Replace("\0", "");
        if (redactor != null) line = redactor.Redact(line);
        if (line.Length > maxLineChars) line = line[..maxLineChars] + "…";
        lock (sync)
        {
            lines.AddLast((next++, line));
            bytes += line.Length + 1;
            while (bytes > maxBytes && lines.Count > 1)
            {
                bytes -= lines.First!.Value.Line.Length + 1;
                lines.RemoveFirst();
            }
        }
    }

    public void Clear() { lock (sync) { lines.Clear(); bytes = 0; } }

    /// <summary>The lines after <paramref name="afterSeq"/> (0: from the start of what is kept), at most <paramref name="max"/> of the newest, and the sequence to ask for next.</summary>
    public (IReadOnlyList<string> Lines, long Next) Read(long afterSeq = 0, int max = 500)
    {
        lock (sync)
        {
            var list = lines.Where(l => l.Seq > afterSeq).Select(l => l.Line).ToList();
            if (list.Count > max) list = list.GetRange(list.Count - max, max);
            return (list, next - 1);
        }
    }

    public IReadOnlyList<string> Tail(int n) => Read(0, n).Lines;

    public string Text(int tail = 200) => string.Join('\n', Tail(tail));
}

public enum SetupState { None, Pending, Running, Succeeded, Failed, UpToDate }

public enum TaskState { Running, Succeeded, Failed, Stopped }

/// <summary>One run of a task: a process in the workspace container whose output is kept in a capped log.</summary>
public sealed class WorkspaceTaskRun
{
    public WorkspaceTaskRun(string name, string command, int? port, bool preview, SecretRedactor redactor)
    {
        Name = name; Command = command; Port = port; Preview = preview;
        Log = new LogBuffer(redactor);
        Id = Guid.NewGuid().ToString("N")[..12];
    }

    public string Id { get; }
    public string Name { get; }
    public string Command { get; }
    public int? Port { get; }
    public bool Preview { get; }
    public LogBuffer Log { get; }
    public TaskState State { get; set; } = TaskState.Running;
    public int? ExitCode { get; set; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
    public bool StopRequested { get; set; }
    public CancellationTokenSource Cts { get; } = new();
    public string PidFile => $"/tmp/.bb-task-{Id}.pid";
}

/// <summary>What a task looks like to the UI, the API and the assistant (never the raw process).</summary>
public sealed record TaskInfo(string Name, string Command, int? Port, bool Preview, string State, int? ExitCode, DateTime? StartedAt, DateTime? EndedAt);

/// <summary>
/// In-memory state of one workspace's environment: what the repository asked for, the provisioning and setup logs, the tasks that ran.
/// (Logs are not stored: they hold output of the developer's own commands.)
/// </summary>
public sealed class WorkspaceSession
{
    private readonly object sync = new();
    private readonly List<string> warnings = [];

    public WorkspaceSession(int workspaceId)
    {
        WorkspaceId = workspaceId;
        Redactor = new SecretRedactor();
        ProvisionLog = new LogBuffer(Redactor);
        SetupLog = new LogBuffer(Redactor);
    }

    public int WorkspaceId { get; }
    public SecretRedactor Redactor { get; }
    public LogBuffer ProvisionLog { get; }
    public LogBuffer SetupLog { get; }
    public WorkspaceEnvironment Env { get; set; } = WorkspaceEnvironment.Empty;
    /// <summary>Why the environment file was refused (shown on the page; the workspace still opens with the default setup).</summary>
    public IReadOnlyList<string> EnvErrors { get; set; } = [];
    public SetupState Setup { get; set; } = SetupState.None;
    public string? SetupMessage { get; set; }
    public ConcurrentDictionary<string, WorkspaceTaskRun> Tasks { get; } = new(StringComparer.Ordinal);
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    public IReadOnlyList<string> Warnings { get { lock (sync) return warnings.ToList(); } }

    public void Warn(string message)
    {
        message = Redactor.Redact(message);
        lock (sync) { if (!warnings.Contains(message) && warnings.Count < 50) warnings.Add(message); }
    }

    public void ClearWarnings() { lock (sync) warnings.Clear(); }

    /// <summary>Cancelled when the workspace stops or fails: ends the setup run and the docker clients of tasks still streaming.</summary>
    public CancellationTokenSource Lifetime { get; } = new();
    /// <summary><c>ip:port</c> pairs of the sidecar services, kept reachable by the network policy (applied again after a restart).</summary>
    public IReadOnlyList<string> EgressAllow { get; set; } = [];

    public void Shutdown()
    {
        CancelAllTasks();
        try { Lifetime.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void CancelAllTasks()
    {
        foreach (var t in Tasks.Values)
        {
            t.StopRequested = true;
            try { t.Cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    public TaskInfo Describe(WorkspaceTaskDefinition def)
    {
        Tasks.TryGetValue(def.Name, out var run);
        return new TaskInfo(def.Name, def.Command, def.Port, def.Preview, run == null ? "Idle" : run.State.ToString(), run?.ExitCode, run?.StartedAt, run?.EndedAt);
    }
}
