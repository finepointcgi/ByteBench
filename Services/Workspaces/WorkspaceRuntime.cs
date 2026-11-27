using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ByteBench.Data;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Workspaces;

/// <summary>Random secrets for workspaces, and their hashes.</summary>
public static class WorkspaceTokens
{
    /// <summary>Prefix of the git token a workspace clones and pushes with (not a personal access token: it only works for one repository).</summary>
    public const string GitPrefix = "gbw_";

    public static string NewGitToken() => GitPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
    public static string NewConnectionToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private const string Base32 = "abcdefghijklmnopqrstuvwxyz234567";

    /// <summary>100 random bits as 20 characters of [a-z2-7]: safe in a DNS label and a URL path.</summary>
    public static string NewPublicId()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        var sb = new StringBuilder(20);
        foreach (var b in bytes) sb.Append(Base32[b & 31]);
        return sb.ToString();
    }

    public static readonly Regex PublicIdPattern = new("^[a-z2-7]{20}$", RegexOptions.Compiled);
}

/// <summary>A workspace that is running: what the proxy needs, held in memory (the connection token's plaintext never touches the database).</summary>
public sealed class LiveWorkspace(int id, string publicId, int ownerId, string containerId, int ideHostPort, int idePort, string connectionToken)
{
    public int Id { get; } = id;
    public string PublicId { get; } = publicId;
    public int OwnerId { get; } = ownerId;
    public string ContainerId { get; } = containerId;
    public int IdeHostPort { get; } = ideHostPort;
    public int IdePort { get; } = idePort;
    public string ConnectionToken { get; } = connectionToken;
    private long lastActivityTicks = DateTime.UtcNow.Ticks;
    public DateTime LastActivity => new(Interlocked.Read(ref lastActivityTicks), DateTimeKind.Utc);
    public void Touch() => Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);
    /// <summary>Sets the last activity explicitly (tests).</summary>
    public void SetActivity(DateTime utc) => Interlocked.Exchange(ref lastActivityTicks, utc.Ticks);
    public WorkspaceEndpoint Endpoint => new(ContainerId, IdeHostPort, IdePort);
}

/// <summary>
/// In-memory state shared by the proxy, the service and the reaper (singleton): the running workspaces, one-time launch tickets and the
/// sessions they are exchanged for, and the background provisioning tasks. Everything here is dropped when ByteBench restarts, which
/// matches the reaper: workspaces don't survive a restart.
/// </summary>
public sealed class WorkspaceRuntime
{
    private readonly ConcurrentDictionary<string, LiveWorkspace> byPublicId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, LiveWorkspace> byId = new();
    private readonly ConcurrentDictionary<string, Grant> tickets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Grant> sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> background = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> locks = new();

    /// <summary>When the reaper last finished a pass (admin health).</summary>
    public DateTime? ReaperLastRun { get; set; }

    /// <summary>Serialises "check the caps, then create the row" so two starts can't both slip under a limit.</summary>
    public SemaphoreSlim StartGate { get; } = new(1, 1);

    public static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);

    /// <summary>What a ticket or session allows: one workspace, for one user, on one origin (<c>ide</c> or <c>p3000</c>).</summary>
    public sealed record Grant(int WorkspaceId, int UserId, string Audience, DateTime ExpiresAt, string MainOrigin);

    public void Register(LiveWorkspace ws)
    {
        byPublicId[ws.PublicId] = ws;
        byId[ws.Id] = ws;
    }

    private readonly ConcurrentDictionary<int, int> runningTasks = new();
    private readonly ConcurrentDictionary<int, byte> activePrebuilds = new();

    /// <summary>Marks a task (a run of a workspace task, a long command) as running in a workspace until the returned handle is disposed: such a workspace is never stopped for being idle.</summary>
    public IDisposable BeginTask(int workspaceId)
    {
        runningTasks.AddOrUpdate(workspaceId, 1, (_, n) => n + 1);
        Find(workspaceId)?.Touch();
        return new TaskHandle(this, workspaceId);
    }

    public bool HasRunningTask(int workspaceId) => runningTasks.TryGetValue(workspaceId, out var n) && n > 0;

    private sealed class TaskHandle(WorkspaceRuntime owner, int workspaceId) : IDisposable
    {
        private int done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref done, 1) != 0) return;
            owner.runningTasks.AddOrUpdate(workspaceId, 0, (_, n) => Math.Max(0, n - 1));
            owner.Find(workspaceId)?.Touch(); // finishing a task is activity too: the idle period starts now
        }
    }

    /// <summary>Prebuilds being built right now (their build containers are not orphans for the reaper).</summary>
    public bool IsPrebuilding(int prebuildId) => activePrebuilds.ContainsKey(prebuildId);
    public void SetPrebuilding(int prebuildId, bool on) { if (on) activePrebuilds[prebuildId] = 0; else activePrebuilds.TryRemove(prebuildId, out _); }

    public LiveWorkspace? Find(string publicId) => byPublicId.TryGetValue(publicId, out var w) ? w : null;
    public LiveWorkspace? Find(int id) => byId.TryGetValue(id, out var w) ? w : null;
    public IReadOnlyCollection<LiveWorkspace> All => byId.Values.ToList();

    /// <summary>Forgets a workspace and every ticket and session for it (stop, failure, expiry).</summary>
    public void Forget(int workspaceId)
    {
        if (byId.TryRemove(workspaceId, out var w)) byPublicId.TryRemove(w.PublicId, out _);
        foreach (var (k, g) in tickets) if (g.WorkspaceId == workspaceId) tickets.TryRemove(k, out _);
        foreach (var (k, g) in sessions) if (g.WorkspaceId == workspaceId) sessions.TryRemove(k, out _);
    }

    private readonly ConcurrentDictionary<int, CancellationTokenSource> provisioning = new();

    /// <summary>Registers a cancellable provisioning run; <see cref="CancelProvisioning"/> (a stop request) cancels it.</summary>
    public CancellationTokenSource BeginProvisioning(int workspaceId, TimeSpan timeout)
    {
        var cts = new CancellationTokenSource(timeout);
        provisioning[workspaceId] = cts;
        return cts;
    }

    public void EndProvisioning(int workspaceId)
    {
        if (provisioning.TryRemove(workspaceId, out var cts)) cts.Dispose();
    }

    public void CancelProvisioning(int workspaceId)
    {
        if (provisioning.TryGetValue(workspaceId, out var cts))
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Drops the lock of a workspace that reached a final state.</summary>
    public void DropLock(int workspaceId) => locks.TryRemove(workspaceId, out _);

    /// <summary>Per-workspace lock for operations that must not overlap (provisioning, stop, restart).</summary>
    public SemaphoreSlim LockFor(int workspaceId) => locks.GetOrAdd(workspaceId, _ => new SemaphoreSlim(1, 1));

    // ---------- tickets & sessions ----------

    /// <summary>A single-use token that a signed-in ByteBench user hands to the workspace origin to get a session there.</summary>
    public string IssueTicket(int workspaceId, int userId, string audience, string mainOrigin)
    {
        Sweep();
        var ticket = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));
        tickets[Key(ticket)] = new Grant(workspaceId, userId, audience, DateTime.UtcNow + TicketLifetime, mainOrigin);
        return ticket;
    }

    /// <summary>Consumes a ticket (it works once) and returns a session token for the same workspace and audience, or null.</summary>
    public string? RedeemTicket(string ticket, int workspaceId, string audience, out int userId)
    {
        userId = 0;
        if (!tickets.TryRemove(Key(ticket), out var g)) return null;
        if (g.ExpiresAt < DateTime.UtcNow || g.WorkspaceId != workspaceId || !string.Equals(g.Audience, audience, StringComparison.Ordinal)) return null;
        var session = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        sessions[Key(session)] = g with { ExpiresAt = DateTime.UtcNow + SessionLifetime };
        userId = g.UserId;
        return session;
    }

    /// <summary>The grant behind a session cookie, if it is valid for exactly this workspace and audience.</summary>
    public Grant? FindSession(string session, int workspaceId, string audience)
    {
        if (!sessions.TryGetValue(Key(session), out var g)) return null;
        if (g.ExpiresAt < DateTime.UtcNow || g.WorkspaceId != workspaceId || !string.Equals(g.Audience, audience, StringComparison.Ordinal)) return null;
        return g;
    }

    private static string Key(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private void Sweep()
    {
        var now = DateTime.UtcNow;
        foreach (var (k, g) in tickets) if (g.ExpiresAt < now) tickets.TryRemove(k, out _);
        foreach (var (k, g) in sessions) if (g.ExpiresAt < now) sessions.TryRemove(k, out _);
    }

    // ---------- background provisioning ----------

    /// <summary>Tracks a background task (starting a workspace) so tests and shutdown can wait for it.</summary>
    public void Track(Task task)
    {
        background[task] = 0;
        _ = task.ContinueWith(t => background.TryRemove(t, out _), TaskScheduler.Default);
    }

    public Task WhenIdleAsync() => Task.WhenAll(background.Keys.ToArray());
}

/// <summary>Where a workspace is reachable: its own origin (<c>ide-{id}.{BaseDomain}</c>), or a path on ByteBench's origin when explicitly allowed.</summary>
public enum WorkspaceOriginMode { Unavailable, Subdomain, SharedPath }

public sealed partial class WorkspaceUrls(IOptions<WorkspaceOptions> options, IHostEnvironment env)
{
    private readonly WorkspaceOptions opts = options.Value;

    public string? BaseDomain => opts.BaseDomain is { Length: > 0 } d ? d.Trim().Trim('.').ToLowerInvariant()
        : env.IsDevelopment() ? "localhost" : null;

    public WorkspaceOriginMode Mode => BaseDomain != null ? WorkspaceOriginMode.Subdomain
        : opts.AllowSharedOrigin ? WorkspaceOriginMode.SharedPath : WorkspaceOriginMode.Unavailable;

    public const string IdeAudience = "ide";
    public static string PreviewAudience(int port) => "p" + port;

    public static string Label(string audience, string publicId) => audience == IdeAudience ? $"ide-{publicId}" : $"{audience}-{publicId}";

    [GeneratedRegex("^(?<aud>ide|p(?<port>[0-9]{2,5}))-(?<id>[a-z2-7]{20})$")]
    private static partial Regex LabelPattern();

    /// <summary>Parses the first label of a workspace host (<c>ide-{id}</c> or <c>p3000-{id}</c>).</summary>
    public static bool TryParseLabel(string label, out string audience, out string publicId, out int? port)
    {
        audience = publicId = "";
        port = null;
        var m = LabelPattern().Match(label);
        if (!m.Success) return false;
        audience = m.Groups["aud"].Value;
        publicId = m.Groups["id"].Value;
        if (m.Groups["port"].Success)
        {
            if (!int.TryParse(m.Groups["port"].Value, out var p) || p is < 1 or > 65535) return false;
            port = p;
        }
        return true;
    }

    /// <summary>If <paramref name="host"/> is <c>{label}.{BaseDomain}</c>, the label.</summary>
    public bool TryGetLabel(string host, out string label)
    {
        label = "";
        var domain = BaseDomain;
        if (domain == null) return false;
        var h = host.ToLowerInvariant();
        var suffix = "." + domain;
        if (!h.EndsWith(suffix, StringComparison.Ordinal)) return false;
        label = h[..^suffix.Length];
        return label.Length > 0 && !label.Contains('.');
    }

    /// <summary>The base address of a workspace origin, with ByteBench's scheme and port: <c>https://ide-xyz.example.com</c>.</summary>
    public string Origin(Uri mainBase, string audience, string publicId)
    {
        var port = mainBase.IsDefaultPort ? "" : ":" + mainBase.Port;
        return $"{mainBase.Scheme}://{Label(audience, publicId)}.{BaseDomain}{port}";
    }

    /// <summary>Where a browser enters a workspace origin: the one-time ticket URL (subdomain mode), or the path (shared-origin mode).</summary>
    public string EntryUrl(Uri mainBase, string audience, string publicId, string? ticket, string next = "/")
    {
        if (Mode == WorkspaceOriginMode.SharedPath)
            return audience == IdeAudience ? $"{mainBase.GetLeftPart(UriPartial.Authority)}/_ws/{publicId}/ide/" : $"{mainBase.GetLeftPart(UriPartial.Authority)}/_ws/{publicId}/{audience}/";
        return $"{Origin(mainBase, audience, publicId)}{EnterPath}?ticket={Uri.EscapeDataString(ticket ?? "")}&next={Uri.EscapeDataString(next)}";
    }

    public const string EnterPath = "/_bb/enter";
}
