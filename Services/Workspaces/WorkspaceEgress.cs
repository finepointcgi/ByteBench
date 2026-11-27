using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Workspaces;

/// <summary>Configuration section <c>Workspaces:Egress</c> (the proxy that workspaces in <c>allowlist</c> mode reach the internet through).</summary>
public class WorkspaceEgressOptions
{
    /// <summary>How containers reach the proxy: default is the host of the git endpoint (<c>Workspaces:GitBaseUrl</c>), e.g. <c>host.docker.internal</c>.</summary>
    public string? ProxyHost { get; set; }
    /// <summary>The proxy's port (0 picks a free one at first use). Containers must be able to reach it the same way they reach the git endpoint.</summary>
    public int ProxyPort { get; set; }
    /// <summary>The interface the proxy listens on. It requires a per-workspace secret, but on a shared host bind it to the Docker bridge address if you can.</summary>
    public string BindAddress { get; set; } = "0.0.0.0";
    /// <summary>Let the proxy connect to private addresses an allow-list names (an internal registry). Off: only public addresses, plus ByteBench's own git endpoint.</summary>
    public bool AllowPrivateTargets { get; set; }
    /// <summary>Blocked requests kept in memory per workspace for the UI (distinct host:port, with counts).</summary>
    public int BlockLogSize { get; set; } = 200;
}

/// <summary>
/// Network egress for workspaces. Resolves the policy a workspace starts with (admin mode and allow-list, narrowed by the repository's
/// <c>egress:</c>), holds the per-workspace proxy credentials and the blocked-request log, and runs the filtering proxy (lazily, on the first
/// <c>allowlist</c> workspace).
/// <para>
/// <b>Enforcement.</b> The container's firewall (applied from a helper that shares its network namespace, so the workspace never holds NET_ADMIN)
/// allows ByteBench's git endpoint and the proxy, and nothing else: no direct connections and no DNS. <c>HTTP_PROXY</c>/<c>HTTPS_PROXY</c> point
/// tools at the proxy, which names the workspace by its secret, checks host and port against that workspace's rules, resolves the name itself and
/// refuses non-public addresses. Denied requests are counted, kept in a short log for the UI and audited (first time per host and port).
/// </para>
/// </summary>
public sealed class WorkspaceEgress(IDbContextFactory<AppDbContext> dbFactory, IOptionsMonitor<WorkspaceOptions> options,
    IEgressConnector connector, WorkspaceAudit audit, ILogger<WorkspaceEgress> log) : IDisposable
{
    public const string ModeKey = "workspaces.egress.mode";
    public const string AllowKey = "workspaces.egress.allow";
    private const int MaxAuditedPerWorkspace = 25;

    private readonly ConcurrentDictionary<string, EgressEntry> byToken = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, string> tokenOf = new();
    private readonly ConcurrentDictionary<string, BlockLog> blockLogs = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private EgressProxyServer? proxy;

    private WorkspaceOptions Opts => options.CurrentValue;

    // ---------- admin policy ----------

    /// <summary>The admin's policy: the mode and allow-list from the site settings; unset means <c>Workspaces:EgressMode</c> and the default list.</summary>
    public async Task<EffectiveEgress> GetAdminPolicyAsync()
    {
        var settings = new SiteSettings(dbFactory);
        return WorkspaceEgressPolicy.Admin(await settings.GetAsync(ModeKey), await settings.GetAsync(AllowKey), Opts.EgressMode);
    }

    // ---------- start ----------

    /// <summary>
    /// Combines the admin policy with the repository's <c>egress:</c> (already parsed and validated with the rest of <c>.bytebench/workspace.yml</c>; it can
    /// only narrow), sets <see cref="Workspace.EgressMode"/> and stores the record in <paramref name="db"/> (the caller saves).
    /// </summary>
    public async Task<EffectiveEgress> ResolveAsync(AppDbContext db, Workspace ws, RepoEgressSpec? repo)
    {
        var admin = await GetAdminPolicyAsync();
        var effective = WorkspaceEgressPolicy.Narrow(admin, repo);
        ws.EgressMode = effective.Mode;
        var record = await db.WorkspaceEgressRecords.FirstOrDefaultAsync(r => r.WorkspaceId == ws.Id);
        if (record == null) db.WorkspaceEgressRecords.Add(record = new WorkspaceEgressRecord { WorkspaceId = ws.Id });
        record.Mode = effective.Mode;
        record.AdminMode = admin.Mode;
        record.RepoNarrowed = effective.RepoNarrowed;
        record.RulesJson = WorkspaceEgressPolicy.RulesJson(effective.Rules);
        record.WarningsJson = JsonSerializer.Serialize(effective.Warnings);
        return effective;
    }

    /// <summary>
    /// For an <c>allowlist</c> workspace: starts the proxy if needed, registers the workspace and returns the environment that points its tools at the
    /// proxy. Empty for every other mode.
    /// </summary>
    public IReadOnlyDictionary<string, string> Prepare(Workspace ws, EffectiveEgress policy, Uri gitBase, IEnumerable<string>? directHosts = null)
    {
        if (!policy.UsesProxy) return new Dictionary<string, string>();
        var port = EnsureProxy();
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));
        var own = new EgressRule(gitBase.Host.ToLowerInvariant(), gitBase.Port);
        Forget(ws.Id);
        var entry = new EgressEntry(ws.Id, ws.PublicId, ws.ProjectId, ws.OwnerId, policy, own);
        byToken[Hash(token)] = entry;
        tokenOf[ws.Id] = Hash(token);
        var url = $"http://bytebench:{token}@{ProxyHost(gitBase)}:{port}";
        // Reached directly, not through the proxy: ByteBench's git endpoint and the repository's own services (private, no way out).
        var noProxy = string.Join(",", new[] { "localhost", "127.0.0.1", gitBase.Host }.Concat(directHosts ?? []));
        return new Dictionary<string, string>
        {
            ["HTTP_PROXY"] = url, ["HTTPS_PROXY"] = url, ["http_proxy"] = url, ["https_proxy"] = url,
            ["NO_PROXY"] = noProxy, ["no_proxy"] = noProxy,
        };
    }

    /// <summary>Host and port a container reaches the proxy on (for its firewall rule).</summary>
    public (string Host, int Port) ProxyAddress(Uri gitBase) => (ProxyHost(gitBase), EnsureProxy());

    private string ProxyHost(Uri gitBase) => Opts.Egress.ProxyHost is { Length: > 0 } h ? h : gitBase.Host;

    private int EnsureProxy()
    {
        lock (gate)
        {
            proxy ??= new EgressProxyServer(connector, FindByToken, OnBlocked, () => Opts.Egress.AllowPrivateTargets, log);
            return proxy.Start(Opts.Egress.BindAddress, Opts.Egress.ProxyPort);
        }
    }

    private EgressEntry? FindByToken(string token) => byToken.TryGetValue(Hash(token), out var e) ? e : null;
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    /// <summary>Drops a workspace's proxy credential (stop, failure): the proxy refuses it from now on.</summary>
    public void Forget(int workspaceId)
    {
        if (tokenOf.TryRemove(workspaceId, out var hash)) byToken.TryRemove(hash, out _);
    }

    // ---------- the blocked-request log ----------

    private sealed class BlockLog
    {
        public readonly object Sync = new();
        public readonly Dictionary<string, EgressBlock> Items = new();
        public int Audited;
    }

    private void OnBlocked(EgressEntry entry, string host, int port, string method, string reason)
    {
        var l = blockLogs.GetOrAdd(entry.PublicId, _ => new BlockLog());
        var key = $"{host}:{port}";
        bool first;
        lock (l.Sync)
        {
            first = !l.Items.TryGetValue(key, out var existing);
            l.Items[key] = new EgressBlock(DateTime.UtcNow, host, port, method, reason, (existing?.Count ?? 0) + 1);
            while (l.Items.Count > Math.Max(10, Opts.Egress.BlockLogSize))
                l.Items.Remove(l.Items.OrderBy(i => i.Value.At).First().Key);
            if (first && l.Audited >= MaxAuditedPerWorkspace) first = false;
            else if (first) l.Audited++;
        }
        if (!first) return;
        _ = Task.Run(async () =>
        {
            try { await audit.RecordAsync("workspace.egress_blocked", entry.PublicId, entry.ProjectId, entry.OwnerId, new { host, port, method, reason, mode = entry.Policy.Mode }); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { log.LogDebug(ex, "Could not audit a blocked request"); }
        });
    }

    /// <summary>Blocked requests of a running workspace, most recent first (distinct host:port with counts).</summary>
    public IReadOnlyList<EgressBlock> Blocked(string publicId)
    {
        if (!blockLogs.TryGetValue(publicId, out var l)) return [];
        lock (l.Sync) return l.Items.Values.OrderByDescending(b => b.At).ToList();
    }

    /// <summary>Requests the proxy let through and refused for a workspace since it started.</summary>
    public (long Allowed, long Blocked) Counts(int workspaceId) =>
        tokenOf.TryGetValue(workspaceId, out var hash) && byToken.TryGetValue(hash, out var e) ? (Interlocked.Read(ref e.Allowed), Interlocked.Read(ref e.Blocked)) : (0, 0);

    /// <summary>Forgets the log of a finished workspace (its entries stay in the audit log).</summary>
    public void DropLog(string publicId) => blockLogs.TryRemove(publicId, out _);

    public void Dispose() => proxy?.Dispose();
}
