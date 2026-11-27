using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Services.Accounts;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Workspaces;

/// <summary>What someone may do in a workspace. Ordered: each role includes the one before.</summary>
public enum WorkspaceRole { None = 0, Viewer = 1, Collaborator = 2, Owner = 3 }

/// <summary>A person seen in a workspace (a proxied request in the last minute and a half, or an open IDE connection).</summary>
public sealed record WorkspacePresence(int UserId, DateTime LastSeen, int OpenConnections);

/// <summary>
/// Who may do what in each running workspace, kept in memory because the proxy asks on every request (held by <see cref="WorkspaceRuntime"/>).
/// Revoking a person is immediate: the role is gone before the next request and the connections they hold open are cut.
/// </summary>
public sealed class WorkspaceAccessRegistry
{
    private sealed class Presence
    {
        public long LastSeenTicks;
        public int Open;
        public CancellationTokenSource Kill = new();
    }

    private readonly ConcurrentDictionary<int, ConcurrentDictionary<int, WorkspaceShareRole>> shares = new();
    private readonly ConcurrentDictionary<(int Workspace, int User), Presence> presence = new();

    public static readonly TimeSpan PresenceWindow = TimeSpan.FromSeconds(90);

    public WorkspaceRole RoleOf(int workspaceId, int ownerId, int userId)
    {
        if (userId == ownerId) return WorkspaceRole.Owner;
        if (shares.TryGetValue(workspaceId, out var m) && m.TryGetValue(userId, out var role))
            return role == WorkspaceShareRole.Collaborator ? WorkspaceRole.Collaborator : WorkspaceRole.Viewer;
        return WorkspaceRole.None;
    }

    public void Grant(int workspaceId, int userId, WorkspaceShareRole role) => shares.GetOrAdd(workspaceId, _ => new()).AddOrUpdate(userId, role, (_, _) => role);

    /// <summary>Removes one person's access and cuts the connections they have open.</summary>
    public void Revoke(int workspaceId, int userId)
    {
        if (shares.TryGetValue(workspaceId, out var m)) m.TryRemove(userId, out _);
        if (presence.TryRemove((workspaceId, userId), out var p)) Cancel(p);
    }

    /// <summary>Removes everyone's shared access to the workspace (stop, failure, sharing turned off).</summary>
    public void RevokeAll(int workspaceId)
    {
        shares.TryRemove(workspaceId, out _);
        foreach (var key in presence.Keys.Where(k => k.Workspace == workspaceId).ToList())
            if (presence.TryRemove(key, out var p)) Cancel(p);
    }

    private static void Cancel(Presence p)
    {
        try { p.Kill.Cancel(); } catch (ObjectDisposedException) { }
    }

    public IReadOnlyList<int> SharedUsers(int workspaceId) => shares.TryGetValue(workspaceId, out var m) ? m.Keys.ToList() : [];

    /// <summary>Notes a request from this person (the "connected" list).</summary>
    public void Seen(int workspaceId, int userId) =>
        Interlocked.Exchange(ref presence.GetOrAdd((workspaceId, userId), _ => new Presence()).LastSeenTicks, DateTime.UtcNow.Ticks);

    /// <summary>An open long-lived connection (the IDE's websocket): counts as connected while it lasts, and its token is cancelled when the person is revoked.</summary>
    public ConnectionHandle OpenConnection(int workspaceId, int userId)
    {
        var p = presence.GetOrAdd((workspaceId, userId), _ => new Presence());
        Interlocked.Increment(ref p.Open);
        Interlocked.Exchange(ref p.LastSeenTicks, DateTime.UtcNow.Ticks);
        return new ConnectionHandle(p.Kill.Token, () =>
        {
            Interlocked.Decrement(ref p.Open);
            Interlocked.Exchange(ref p.LastSeenTicks, DateTime.UtcNow.Ticks);
        });
    }

    public sealed class ConnectionHandle(CancellationToken token, Action onClose) : IDisposable
    {
        public CancellationToken Token { get; } = token;
        public void Dispose() => onClose();
    }

    public IReadOnlyList<WorkspacePresence> Connected(int workspaceId)
    {
        var now = DateTime.UtcNow;
        return presence.Where(kv => kv.Key.Workspace == workspaceId)
            .Select(kv => new WorkspacePresence(kv.Key.User, new DateTime(Interlocked.Read(ref kv.Value.LastSeenTicks), DateTimeKind.Utc), Volatile.Read(ref kv.Value.Open)))
            .Where(p => p.OpenConnections > 0 || now - p.LastSeen < PresenceWindow).ToList();
    }
}

/// <summary>
/// Database side of sharing that needs no signed-in user (singleton, used by the lifecycle, the maintenance sweep and
/// <see cref="WorkspaceSharingService"/>): loading shares into the live registry and ending them, with an audit record each.
/// </summary>
public sealed class WorkspaceShareStore(IDbContextFactory<AppDbContext> dbFactory, WorkspaceRuntime runtime, WorkspaceAudit audit)
{
    /// <summary>Ends one share (it stays in the table as history), cuts the person's sessions and connections and records it.</summary>
    public async Task EndAsync(WorkspaceShare share, string workspacePublicId, int projectId, string reason, int? actorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.WorkspaceShares.FirstOrDefaultAsync(s => s.Id == share.Id);
        if (row == null || row.EndedAt != null) return;
        row.EndedAt = DateTime.UtcNow;
        row.EndReason = reason.Length > 100 ? reason[..100] : reason;
        await db.SaveChangesAsync();
        runtime.Access.Revoke(row.WorkspaceId, row.UserId);
        runtime.RevokeUser(row.WorkspaceId, row.UserId);
        await audit.RecordAsync("workspace.share_ended", workspacePublicId, projectId, actorId, new { userId = row.UserId, role = row.Role.ToString(), reason });
    }

    /// <summary>Ends every active share of a workspace (it stopped, failed or sharing was switched off). Idempotent.</summary>
    public async Task EndAllAsync(int workspaceId, string reason, int? actorId)
    {
        runtime.Access.RevokeAll(workspaceId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var active = await db.WorkspaceShares.Where(s => s.WorkspaceId == workspaceId && s.EndedAt == null).ToListAsync();
        if (active.Count == 0) return;
        var ws = await db.Workspaces.AsNoTracking().Where(w => w.Id == workspaceId).Select(w => new { w.PublicId, w.ProjectId }).FirstAsync();
        foreach (var s in active)
        {
            s.EndedAt = DateTime.UtcNow;
            s.EndReason = reason.Length > 100 ? reason[..100] : reason;
            runtime.RevokeUser(workspaceId, s.UserId);
        }
        await db.SaveChangesAsync();
        foreach (var s in active)
            await audit.RecordAsync("workspace.share_ended", ws.PublicId, ws.ProjectId, actorId, new { userId = s.UserId, role = s.Role.ToString(), reason });
    }

    /// <summary>Every active share of a workspace that is no longer live (a restart, a crash): ended with the reason "workspace not running".</summary>
    public async Task EndOrphansAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var ids = await db.WorkspaceShares.Where(s => s.EndedAt == null && s.Workspace.State != WorkspaceState.Running && s.Workspace.State != WorkspaceState.Starting)
            .Select(s => s.WorkspaceId).Distinct().ToListAsync();
        foreach (var id in ids) await EndAllAsync(id, "workspace not running", null);
    }
}

/// <summary>The admin switch for sharing: off, project members only (default) or anyone who can view the repository.</summary>
public static class WorkspaceSharingMode
{
    public const string SettingKey = "workspaces.sharing";
    public const string Off = "off", Members = "members", Viewers = "viewers";
    public static string Normalize(string? value) => value is Off or Members or Viewers ? value : Members;
}

public sealed record ShareInfo(int UserId, string UserName, string? DisplayName, WorkspaceShareRole Role, DateTime CreatedAt, bool Connected, DateTime? LastSeen);

public sealed record SharedWorkspaceInfo(Workspace Workspace, string ProjectKey, string RepoSlug, string OwnerName, WorkspaceShareRole Role);

public sealed record ViewerEntry(string Name, bool IsDirectory, long Size);

public sealed record ViewerFile(string Path, string? Text, bool Binary, bool Truncated);

/// <summary>
/// Sharing a running workspace with other ByteBench users. The owner invites people who can already view the repository:
/// <para>
/// <b>Collaborator</b>: the same OpenVSCode server as the owner (it has no multi-user mode; each person opens their own browser window on it),
/// reached through the proxy with their own single-use ticket and their own session. They see the same files, and share terminals through tmux.
/// They act as the container's user, so they can also push with the owner's git token: invite people you trust like you.
/// </para>
/// <para>
/// <b>Viewer</b>: never gets an IDE session (the proxy refuses their ticket and session; none is issued). They read files and terminal output
/// through ByteBench, which runs fixed read-only commands inside the container for them, as the assistant's unprivileged user for files. No input
/// of theirs ever reaches a shell, an editor or the IDE; this is enforced here and in the proxy, not by hiding buttons.
/// </para>
/// </summary>
public sealed partial class WorkspaceSharingService(IDbContextFactory<AppDbContext> dbFactory, WorkspaceRuntime runtime, IWorkspaceBackend backend, ProjectAccess access,
    WorkspaceAudit audit, ByteBench.Services.Audit.AuditLog auditLog, WorkspaceShareStore store, NotificationService notifications, SiteSettings siteSettings)
{
    public const int MaxSharesPerWorkspace = 10;
    public const int MaxFileBytes = 256 * 1024;

    // ---------- admin switch ----------

    public async Task<string> GetModeAsync() => WorkspaceSharingMode.Normalize(await siteSettings.GetAsync(WorkspaceSharingMode.SettingKey));

    public async Task SetModeAsync(string mode, int adminId)
    {
        if (!await access.IsAdminAsync(adminId)) throw new ForbiddenException("Only site administrators can do this.");
        if (mode is not (WorkspaceSharingMode.Off or WorkspaceSharingMode.Members or WorkspaceSharingMode.Viewers)) throw new InvalidOperationException("Unknown sharing mode.");
        await siteSettings.SetAsync(WorkspaceSharingMode.SettingKey, mode, adminId);
        await auditLog.RecordAsync(WorkspaceAuditActions.AdminSharing, "workspaces", null, details: new { mode }, actorId: adminId);
        if (mode == WorkspaceSharingMode.Off)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            foreach (var id in await db.WorkspaceShares.Where(s => s.EndedAt == null).Select(s => s.WorkspaceId).Distinct().ToListAsync())
                await store.EndAllAsync(id, "sharing turned off", adminId);
        }
    }

    // ---------- invite & revoke ----------

    /// <summary>Invites a user (by username) to the owner's running workspace, or changes their role. They must be able to view the repository (and, by default, be a project member).</summary>
    public async Task<ShareInfo> InviteAsync(string publicId, int ownerId, string userName, WorkspaceShareRole role)
    {
        var mode = await GetModeAsync();
        if (mode == WorkspaceSharingMode.Off) throw new InvalidOperationException("Sharing workspaces is turned off on this server.");
        if (!Enum.IsDefined(role)) throw new InvalidOperationException("Unknown role.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var ws = await db.Workspaces.Include(w => w.Repository).ThenInclude(r => r.Project).FirstOrDefaultAsync(w => w.PublicId == publicId && w.OwnerId == ownerId)
                 ?? throw new InvalidOperationException("Workspace not found.");
        if (ws.State != WorkspaceState.Running || runtime.Find(ws.Id) == null) throw new InvalidOperationException("Only a running workspace can be shared.");

        var norm = (userName ?? "").Trim().TrimStart('@').ToUpperInvariant();
        var invitee = norm.Length == 0 ? null : await db.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == norm);
        if (invitee == null || invitee.DeactivatedAt != null) throw new InvalidOperationException($"No active user '{userName}'.");
        if (invitee.Id == ownerId) throw new InvalidOperationException("You own this workspace already.");
        if (!await access.CanAsync(ws.ProjectId, Permission.ViewProject, invitee.Id))
            throw new InvalidOperationException($"{invitee.UserName} can't view this repository, so they can't be invited.");
        if (mode == WorkspaceSharingMode.Members && !await IsMemberAsync(db, ws.ProjectId, invitee))
            throw new InvalidOperationException($"{invitee.UserName} isn't a member of this project. An administrator can allow sharing with anyone who can view the repository.");

        var active = await db.WorkspaceShares.Where(s => s.WorkspaceId == ws.Id && s.EndedAt == null).ToListAsync();
        var existing = active.FirstOrDefault(s => s.UserId == invitee.Id);
        if (existing == null && active.Count >= MaxSharesPerWorkspace) throw new InvalidOperationException($"At most {MaxSharesPerWorkspace} people can be invited to one workspace.");

        var changed = existing == null || existing.Role != role;
        if (existing == null)
        {
            existing = new WorkspaceShare { WorkspaceId = ws.Id, UserId = invitee.Id, Role = role, InvitedById = ownerId };
            db.WorkspaceShares.Add(existing);
        }
        else existing.Role = role;
        await db.SaveChangesAsync();
        if (role == WorkspaceShareRole.Viewer)
        {
            // A downgrade drops any IDE session and connection they had.
            runtime.Access.Revoke(ws.Id, invitee.Id);
            runtime.RevokeUser(ws.Id, invitee.Id);
        }
        runtime.Access.Grant(ws.Id, invitee.Id, role);

        if (changed)
        {
            await audit.RecordAsync("workspace.shared", ws.PublicId, ws.ProjectId, ownerId, new { userId = invitee.Id, user = invitee.UserName, role = role.ToString() });
            try
            {
                var owner = await db.Users.Where(u => u.Id == ownerId).Select(u => u.UserName).FirstAsync();
                await notifications.OnWorkspaceSharedAsync(ws.ProjectId, invitee.Id, ownerId, $"{ws.Repository.Project.Key}/{ws.Repository.Slug} ({ws.Ref})",
                    $"/workspaces/shared/{ws.PublicId}", role == WorkspaceShareRole.Collaborator ? $"{owner} invited you to work in their workspace (full access)." : $"{owner} invited you to watch their workspace (read-only).");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* a notification must never undo an invitation */ }
        }
        return new ShareInfo(invitee.Id, invitee.UserName!, invitee.DisplayName, existing.Role, existing.CreatedAt, false, null);
    }

    private static async Task<bool> IsMemberAsync(AppDbContext db, int projectId, User user)
    {
        if (user.IsAdmin) return true;
        if (await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == user.Id)) return true;
        var p = await db.Projects.Where(x => x.Id == projectId).Select(x => new { x.GroupId, P1 = x.Group!.ParentId, P2 = x.Group!.Parent!.ParentId, P3 = x.Group!.Parent!.Parent!.ParentId }).FirstOrDefaultAsync();
        if (p?.GroupId == null) return false;
        var groups = new List<int?> { p.GroupId, p.P1, p.P2, p.P3 }.Where(g => g != null).Select(g => g!.Value).ToList();
        return await db.GroupMembers.AnyAsync(g => g.UserId == user.Id && groups.Contains(g.GroupId));
    }

    /// <summary>The owner removes one person.</summary>
    public async Task RevokeAsync(string publicId, int ownerId, int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var ws = await OwnedAsync(db, publicId, ownerId);
        var share = await db.WorkspaceShares.FirstOrDefaultAsync(s => s.WorkspaceId == ws.Id && s.UserId == userId && s.EndedAt == null)
                    ?? throw new InvalidOperationException("That person isn't invited.");
        await store.EndAsync(share, ws.PublicId, ws.ProjectId, "revoked by the owner", ownerId);
    }

    /// <summary>The owner ends all sharing of the workspace.</summary>
    public async Task EndSharingAsync(string publicId, int ownerId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var ws = await OwnedAsync(db, publicId, ownerId);
        await store.EndAllAsync(ws.Id, "sharing ended by the owner", ownerId);
    }

    /// <summary>An invitee leaves a workspace they were invited to.</summary>
    public async Task LeaveAsync(string publicId, int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var ws = await db.Workspaces.FirstOrDefaultAsync(w => w.PublicId == publicId) ?? throw new InvalidOperationException("Workspace not found.");
        var share = await db.WorkspaceShares.FirstOrDefaultAsync(s => s.WorkspaceId == ws.Id && s.UserId == userId && s.EndedAt == null) ?? throw new InvalidOperationException("Workspace not found.");
        await store.EndAsync(share, ws.PublicId, ws.ProjectId, "left by the invitee", userId);
    }

    private static async Task<Workspace> OwnedAsync(AppDbContext db, string publicId, int ownerId) =>
        await db.Workspaces.FirstOrDefaultAsync(w => w.PublicId == publicId && w.OwnerId == ownerId) ?? throw new InvalidOperationException("Workspace not found.");

    // ---------- lists ----------

    /// <summary>The people invited to the owner's workspace and whether they are connected now.</summary>
    public async Task<List<ShareInfo>> ListAsync(string publicId, int ownerId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var ws = await OwnedAsync(db, publicId, ownerId);
        var shares = await db.WorkspaceShares.AsNoTracking().Include(s => s.User).Where(s => s.WorkspaceId == ws.Id && s.EndedAt == null).OrderBy(s => s.CreatedAt).ToListAsync();
        var present = runtime.Access.Connected(ws.Id).ToDictionary(p => p.UserId);
        return shares.Select(s => new ShareInfo(s.UserId, s.User.UserName ?? "", s.User.DisplayName, s.Role, s.CreatedAt, present.ContainsKey(s.UserId),
            present.TryGetValue(s.UserId, out var p) ? p.LastSeen : null)).ToList();
    }

    /// <summary>Everyone seen in the workspace right now, the owner included: name and role.</summary>
    public async Task<List<(string UserName, WorkspaceRole Role, DateTime LastSeen)>> ConnectedAsync(string publicId, int ownerId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var ws = await OwnedAsync(db, publicId, ownerId);
        var present = runtime.Access.Connected(ws.Id);
        var names = await db.Users.AsNoTracking().Where(u => present.Select(p => p.UserId).Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.UserName ?? "");
        return present.Select(p => (names.GetValueOrDefault(p.UserId, "?"), runtime.Access.RoleOf(ws.Id, ws.OwnerId, p.UserId), p.LastSeen)).OrderBy(p => p.Item1).ToList();
    }

    /// <summary>Running workspaces other people invited the user to.</summary>
    public async Task<List<SharedWorkspaceInfo>> SharedWithMeAsync(int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.WorkspaceShares.AsNoTracking()
            .Where(s => s.UserId == userId && s.EndedAt == null && s.Workspace.State == WorkspaceState.Running)
            .Select(s => new { s.Workspace, s.Role, Key = s.Workspace.Repository.Project.Key, Slug = s.Workspace.Repository.Slug, Owner = s.Workspace.Owner.UserName })
            .OrderByDescending(s => s.Workspace.CreatedAt).ToListAsync();
        return rows.Select(r => new SharedWorkspaceInfo(r.Workspace, r.Key, r.Slug, r.Owner ?? "", r.Role)).ToList();
    }

    /// <summary>A workspace the user was invited to (null for anyone else, and once the share has ended).</summary>
    public async Task<SharedWorkspaceInfo?> GetSharedAsync(string publicId, int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var r = await db.WorkspaceShares.AsNoTracking()
            .Where(s => s.UserId == userId && s.EndedAt == null && s.Workspace.PublicId == publicId)
            .Select(s => new { s.Workspace, s.Role, Key = s.Workspace.Repository.Project.Key, Slug = s.Workspace.Repository.Slug, Owner = s.Workspace.Owner.UserName })
            .FirstOrDefaultAsync();
        return r == null ? null : new SharedWorkspaceInfo(r.Workspace, r.Key, r.Slug, r.Owner ?? "", r.Role);
    }

    /// <summary>The URL that opens the IDE for a collaborator (a single-use ticket on the workspace origin); null for viewers and everyone else.</summary>
    public async Task<string?> OpenUrlAsync(string publicId, int userId, Uri mainBase, WorkspaceService workspaces) =>
        await workspaces.EntryUrlAsync(publicId, userId, mainBase);

    // ---------- the viewer's read-only window ----------

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,40}$")]
    private static partial Regex SessionNamePattern();

    private const string ListScript = """
        set -e
        root="$1"; rel="$2"
        p=$(realpath -e -- "$root/$rel") || { echo "No such file or directory." >&2; exit 3; }
        case "$p" in "$root"|"$root"/*) ;; *) echo "Outside the workspace." >&2; exit 4 ;; esac
        case "$p" in */.git|*/.git/*) echo "Not available." >&2; exit 4 ;; esac
        [ -d "$p" ] || { echo "Not a directory." >&2; exit 5; }
        find "$p" -mindepth 1 -maxdepth 1 -not -name .git -printf '%y\t%s\t%f\n' | sort -t "$(printf '\t')" -k1,1 -k3,3 | head -n 2000
        """;

    private const string ReadScript = """
        set -e
        root="$1"; rel="$2"; max="$3"
        p=$(realpath -e -- "$root/$rel") || { echo "No such file or directory." >&2; exit 3; }
        case "$p" in "$root"/*) ;; *) echo "Outside the workspace." >&2; exit 4 ;; esac
        case "$p" in */.git|*/.git/*) echo "Not available." >&2; exit 4 ;; esac
        [ -f "$p" ] || { echo "Not a file." >&2; exit 5; }
        if [ "$(head -c 8192 -- "$p" | tr -cd '\000' | wc -c)" -gt 0 ]; then echo "BB-BINARY"; exit 0; fi
        head -c "$max" -- "$p"
        """;

    private async Task<(LiveWorkspace Live, Workspace Ws)> RequireViewAsync(string publicId, int userId)
    {
        var live = WorkspaceTokens.PublicIdPattern.IsMatch(publicId) ? runtime.Find(publicId) : null;
        if (live == null || runtime.Access.RoleOf(live.Id, live.OwnerId, userId) == WorkspaceRole.None) throw new InvalidOperationException("Workspace not found, or it isn't running.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var ws = await db.Workspaces.AsNoTracking().Include(w => w.Repository).FirstOrDefaultAsync(w => w.Id == live.Id && w.State == WorkspaceState.Running)
                 ?? throw new InvalidOperationException("Workspace not found, or it isn't running.");
        runtime.Access.Seen(live.Id, userId);
        return (live, ws);
    }

    private static string CleanPath(string? rel)
    {
        rel = (rel ?? "").Trim().Trim('/');
        if (rel.Length > 500 || rel.Contains('\0') || rel.Contains('\\') || rel.Split('/').Any(s => s == "..")) throw new InvalidOperationException("That path isn't valid.");
        return rel;
    }

    /// <summary>Directory listing of the checkout (read-only; <c>.git</c> is never shown).</summary>
    public async Task<List<ViewerEntry>> ListDirectoryAsync(string publicId, int userId, string? relativePath)
    {
        var (live, ws) = await RequireViewAsync(publicId, userId);
        var rel = CleanPath(relativePath);
        var r = await backend.ExecAsync(live.ContainerId, new ExecSpec(["sh", "-c", ListScript, "sh", WorkspaceLifecycle.WorkDir(ws.Repository.Slug), rel])
        { User = "bbai", Timeout = TimeSpan.FromSeconds(15), MaxOutputBytes = 256 * 1024 }, CancellationToken.None);
        if (!r.Ok) throw new InvalidOperationException(r.Output.Trim().Length > 0 ? r.Output.Trim() : "Could not list that folder.");
        var list = new List<ViewerEntry>();
        foreach (var line in r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t', 3);
            if (parts.Length == 3 && long.TryParse(parts[1], out var size)) list.Add(new ViewerEntry(parts[2], parts[0] == "d", size));
        }
        return list.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A file's text (the first 256 KB), read as the assistant's unprivileged user: files the developer keeps private (git credentials, mode 600 files) aren't readable.</summary>
    public async Task<ViewerFile> ReadFileAsync(string publicId, int userId, string relativePath)
    {
        var (live, ws) = await RequireViewAsync(publicId, userId);
        var rel = CleanPath(relativePath);
        if (rel.Length == 0) throw new InvalidOperationException("Choose a file.");
        var r = await backend.ExecAsync(live.ContainerId, new ExecSpec(["sh", "-c", ReadScript, "sh", WorkspaceLifecycle.WorkDir(ws.Repository.Slug), rel, (MaxFileBytes + 1).ToString()])
        { User = "bbai", Timeout = TimeSpan.FromSeconds(15), MaxOutputBytes = MaxFileBytes + 1024 }, CancellationToken.None);
        if (!r.Ok) throw new InvalidOperationException(r.Output.Trim().Length > 0 ? r.Output.Trim() : "Could not read that file.");
        if (r.Output.TrimEnd() == "BB-BINARY") return new ViewerFile(rel, null, true, false);
        var truncated = r.Truncated || r.Output.Length > MaxFileBytes;
        return new ViewerFile(rel, truncated && r.Output.Length > MaxFileBytes ? r.Output[..MaxFileBytes] : r.Output, false, truncated);
    }

    /// <summary>Names of the tmux sessions in the workspace (terminals the owner chose to share: <c>tmux new -s name</c>).</summary>
    public async Task<List<string>> TerminalSessionsAsync(string publicId, int userId)
    {
        var (live, _) = await RequireViewAsync(publicId, userId);
        var r = await backend.ExecAsync(live.ContainerId, new ExecSpec(["tmux", "list-sessions", "-F", "#{session_name}"]) { Timeout = TimeSpan.FromSeconds(10), MaxOutputBytes = 8192 }, CancellationToken.None);
        return r.Ok ? r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(n => SessionNamePattern().IsMatch(n)).ToList() : [];
    }

    /// <summary>The visible text and recent scrollback of a shared tmux session (<c>tmux capture-pane</c>: reads, never types).</summary>
    public async Task<string> TerminalOutputAsync(string publicId, int userId, string session)
    {
        var (live, _) = await RequireViewAsync(publicId, userId);
        if (!SessionNamePattern().IsMatch(session ?? "")) throw new InvalidOperationException("That terminal name isn't valid.");
        var r = await backend.ExecAsync(live.ContainerId, new ExecSpec(["tmux", "capture-pane", "-p", "-J", "-S", "-500", "-t", "=" + session + ":"]) { Timeout = TimeSpan.FromSeconds(10), MaxOutputBytes = 128 * 1024 }, CancellationToken.None);
        if (!r.Ok) throw new InvalidOperationException("That terminal isn't there any more.");
        return r.Output;
    }
}
