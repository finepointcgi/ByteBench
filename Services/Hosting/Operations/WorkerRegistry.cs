using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace ByteBench.Services.Hosting.Operations;

/// <summary>What the registry knows about one hosted worker. Immutable: every update replaces the record.</summary>
public sealed record WorkerState(
    string Name, bool Enabled, DateTime? LastTickUtc, DateTime? LastSuccessUtc, DateTime? LastErrorUtc,
    string? LastError, long Ticks, long Errors);

/// <summary>
/// Where every background worker reports that it is alive: <c>Report</c> once at start (enabled or not), <c>Tick</c> at each
/// loop iteration, <c>Success</c> / <c>Failure</c> when an iteration ends. Read by <c>/admin/system/workers</c> and
/// <c>/metrics</c>. One shared instance (<see cref="Instance"/>) is what the app registers; tests can create their own.
/// Errors keep only the exception type and a short, scrubbed first line of its message.
/// </summary>
public sealed class WorkerRegistry(TimeProvider? clock = null)
{
    public static WorkerRegistry Instance { get; } = new();

    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, WorkerState> workers = new(StringComparer.Ordinal);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private void Update(string name, Func<WorkerState, WorkerState> change, bool enabledDefault = true) =>
        workers.AddOrUpdate(name, _ => change(new WorkerState(name, enabledDefault, null, null, null, null, 0, 0)), (_, old) => change(old));

    /// <summary>Declares the worker (at start) and whether its configuration turns it on.</summary>
    public void Report(string name, bool enabled) => Update(name, s => s with { Enabled = enabled }, enabled);

    /// <summary>A loop iteration started (or the worker picked up an item).</summary>
    public void Tick(string name) { var n = Now; Update(name, s => s with { LastTickUtc = n, Ticks = s.Ticks + 1 }); }

    /// <summary>An iteration finished without error.</summary>
    public void Success(string name) { var n = Now; Update(name, s => s with { LastTickUtc = n, LastSuccessUtc = n }); }

    /// <summary>An iteration failed. Only the exception type and a scrubbed, truncated message are kept.</summary>
    public void Failure(string name, Exception ex)
    {
        var n = Now;
        var text = Describe(ex);
        Update(name, s => s with { LastTickUtc = n, LastErrorUtc = n, LastError = text, Errors = s.Errors + 1 });
    }

    public IReadOnlyList<WorkerState> Snapshot() => [.. workers.Values.OrderBy(w => w.Name, StringComparer.Ordinal)];

    public WorkerState? Get(string name) => workers.TryGetValue(name, out var s) ? s : null;

    // ---------------------------------------------------------------- error text

    private static readonly (Regex Pattern, string Replacement)[] Secrets =
    [
        (new(@"(?i)\b(bearer|basic)\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.Compiled), "$1 ***"),
        (new(@"(?i)\b(password|passwd|pwd|secret|token|api[_-]?key|apikey|access[_-]?key|authorization|signature)\s*[=:]\s*[^\s;,&""']+", RegexOptions.Compiled), "$1=***"),
        (new(@"(?<=://)[^/\s:@]+:[^/\s@]+(?=@)", RegexOptions.Compiled), "***"),                       // user:password@host
        (new(@"(?i)([?&](?:token|key|sig|signature|access_token|secret)=)[^&\s]+", RegexOptions.Compiled), "$1***"),
        (new(@"\b(?:gh[pousr]_|glpat-|xox[abprs]-|sk-|AKIA)[A-Za-z0-9_-]{8,}", RegexOptions.Compiled), "***"),
        (new(@"\b[A-Za-z0-9+/_-]{32,}={0,2}", RegexOptions.Compiled), "***"),                            // long opaque strings
    ];

    private const int MaxMessage = 160;

    /// <summary>"TypeName: first line of the message", secrets masked, at most about 200 characters. Never a stack trace.</summary>
    public static string Describe(Exception ex)
    {
        var message = Scrub((ex.Message ?? "").Split('\n', 2)[0].Trim());
        if (message.Length > MaxMessage) message = message[..MaxMessage] + "...";
        return message.Length == 0 ? ex.GetType().Name : $"{ex.GetType().Name}: {message}";
    }

    public static string Scrub(string text)
    {
        foreach (var (pattern, replacement) in Secrets) text = pattern.Replace(text, replacement);
        return text;
    }
}
