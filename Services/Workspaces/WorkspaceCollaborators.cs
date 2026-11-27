using System.Collections.Concurrent;
using ByteBench.Data;

namespace ByteBench.Services.Workspaces;

/// <summary>
/// Gives each collaborator of a running workspace their own unprivileged OS user inside the container (<c>bbc1</c>…<c>bbc10</c>, created in the image),
/// with its own home, its own OpenVSCode server on a loopback port, and, only if the owner ticked "may push", its own git credential.
/// <para>
/// Why: a collaborator used to share the owner's OS user, so their IDE terminal could read the owner's <c>~/.bb-env</c> (injected secrets) and
/// <c>~/.git-credentials</c>. Now the owner's home is mode 700 and the owner's connection token file is mode 600, so none of it is readable by
/// a collaborator; the project folder is shared through group permissions (<c>coder</c> is every collaborator user's primary group, the folder is
/// setgid and group-writable). Collaborators don't see each other's homes either (each is mode 700), because each has a separate slot.
/// </para>
/// The proxy sends a collaborator's ticketed session to their own server with their own connection token (see <see cref="WorkspaceProxyMiddleware"/>).
/// </summary>
public sealed class WorkspaceCollaborators(IWorkspaceBackend backend, WorkspaceRuntime runtime, ILogger<WorkspaceCollaborators> log)
{
    /// <summary>The starter script in the image (<c>docker/workspace/bb-collab-ide.sh</c>): <c>$1</c> port, <c>$2</c> folder; the connection token on stdin.</summary>
    public const string StartCommand = "/usr/local/bin/bb-collab-ide";

    private readonly ConcurrentDictionary<int, SemaphoreSlim> locks = new();

    /// <summary>Git identity and (when <c>$3</c> is 1) the credential read from stdin, written mode 600 into the collaborator's own home.</summary>
    public const string PrepareScript = """
        set -e
        umask 077
        git config --global user.name "$1"
        git config --global user.email "$2"
        git config --global pull.rebase false
        if [ "$3" = 1 ]; then
            cat > "$HOME/.git-credentials"
            git config --global credential.helper store
        else
            rm -f "$HOME/.git-credentials"
            git config --global --unset-all credential.helper 2>/dev/null || true
        fi
        """;

    /// <summary>Ends every process of the collaborator's user (kill -1 reaches all of the caller's own processes, this shell too).</summary>
    public const string KillScript = "kill -KILL -1";

    /// <summary>Empties the slot's home and its files in /tmp so the next person to get the slot starts clean (no credential, history or token).</summary>
    public const string WipeScript = """
        find "$HOME" -mindepth 1 -delete 2>/dev/null || true
        find /tmp -mindepth 1 -maxdepth 1 -user "$(id -u)" -exec rm -rf {} + 2>/dev/null || true
        exit 0
        """;

    private SemaphoreSlim LockFor(int workspaceId) => locks.GetOrAdd(workspaceId, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Prepares (or updates) the collaborator's user and IDE server and returns what the proxy needs. Idempotent: it is called again when the owner changes
    /// "may push". With <paramref name="allowPush"/> the person gets their own credential (their user name, a token that only works for this workspace's
    /// repository while they are invited); without it, no credential is stored anywhere they can reach.
    /// </summary>
    public async Task<CollaboratorSession> EnsureAsync(LiveWorkspace live, Workspace ws, User invitee, bool allowPush, Uri gitBase, CancellationToken ct = default)
    {
        var gate = LockFor(live.Id);
        await gate.WaitAsync(ct);
        try
        {
            var session = runtime.Access.AssignCollaborator(live.Id, invitee.Id, live.ContainerId,
                              slot => new CollaboratorSession(live.Id, slot, invitee.Id, invitee.UserName ?? "", live.ContainerId, WorkspaceLifecycle.WorkDir(ws.Repository.Slug)))
                          ?? throw new InvalidOperationException("All collaborator slots of this workspace are in use.");
            session.DisplayName = string.IsNullOrWhiteSpace(invitee.DisplayName) ? invitee.UserName : invitee.DisplayName;
            session.Email = invitee.Email ?? $"{invitee.UserName}@users.noreply.bytebench";
            session.AllowPush = allowPush;
            if (allowPush)
            {
                // A new token every time the permission is (re)granted: the old one stops working at once.
                var token = WorkspaceTokens.NewGitToken();
                session.GitCredential = $"{gitBase.Scheme}://{Uri.EscapeDataString(invitee.UserName!)}:{token}@{gitBase.Authority}\n";
                session.GitTokenHash = WorkspaceTokens.Hash(token);
            }
            else { session.GitCredential = null; session.GitTokenHash = null; }
            await ApplyAsync(live.ContainerId, session, ct);
            return session;
        }
        finally { gate.Release(); }
    }

    /// <summary>Applies the collaborator's home and IDE server in the container (also after a container restart, which ends their IDE process).</summary>
    private async Task ApplyAsync(string containerId, CollaboratorSession s, CancellationToken ct)
    {
        var user = ExecSpec.CollaboratorUser(s.Slot);
        var prepare = await backend.ExecAsync(containerId, new ExecSpec(["sh", "-c", PrepareScript, "sh", s.DisplayName ?? s.UserName, s.Email ?? "", s.GitCredential != null ? "1" : "0"])
        { User = user, Stdin = s.GitCredential, Timeout = TimeSpan.FromSeconds(30) }, ct);
        if (!prepare.Ok) throw new InvalidOperationException("Could not prepare the collaborator's home: " + WorkspaceLifecycle.Safe(prepare.Output));
        var start = await backend.ExecAsync(containerId, new ExecSpec([StartCommand, s.Port.ToString(), s.WorkDir])
        { User = user, Stdin = s.ConnectionToken, Timeout = TimeSpan.FromSeconds(60) }, ct);
        if (!start.Ok) throw new InvalidOperationException("Could not start the collaborator's IDE: " + WorkspaceLifecycle.Safe(start.Output));
    }

    /// <summary>After the container restarted: starts every collaborator's IDE server again (their homes and credentials survive a restart).</summary>
    public async Task RestoreAsync(LiveWorkspace live, CancellationToken ct = default)
    {
        foreach (var s in runtime.Access.CollaboratorsOf(live.Id))
        {
            try { await ApplyAsync(live.ContainerId, s, ct); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                log.LogWarning(ex, "Workspace {Id}: could not bring back the IDE of collaborator slot {Slot}", live.PublicId, s.Slot);
            }
        }
    }

    /// <summary>
    /// Ends a collaborator's processes and empties their home (the slot is reused), then forgets them. When the workspace isn't live any more
    /// (it stopped: the container goes away with everything in it) only the bookkeeping is dropped.
    /// </summary>
    public async Task ReleaseAsync(int workspaceId, int userId)
    {
        var s = runtime.Access.CollaboratorOf(workspaceId, userId);
        if (s == null) return;
        try { await CleanAsync(workspaceId, s); }
        finally { runtime.Access.RemoveCollaborator(workspaceId, userId); }
    }

    public async Task ReleaseAllAsync(int workspaceId)
    {
        foreach (var s in runtime.Access.CollaboratorsOf(workspaceId)) await ReleaseAsync(workspaceId, s.UserId);
    }

    private async Task CleanAsync(int workspaceId, CollaboratorSession s)
    {
        if (runtime.Find(workspaceId) == null) return;
        var gate = LockFor(workspaceId);
        await gate.WaitAsync();
        try
        {
            var user = ExecSpec.CollaboratorUser(s.Slot);
            // The kill ends the exec's own shell too (exit 137): that is the expected result.
            await backend.ExecAsync(s.ContainerId, new ExecSpec(["sh", "-c", KillScript]) { User = user, Timeout = TimeSpan.FromSeconds(15) }, CancellationToken.None);
            var wipe = await backend.ExecAsync(s.ContainerId, new ExecSpec(["sh", "-c", WipeScript]) { User = user, Timeout = TimeSpan.FromSeconds(30) }, CancellationToken.None);
            if (!wipe.Ok) log.LogWarning("Workspace {Id}: could not empty the home of collaborator slot {Slot}: {Output}", workspaceId, s.Slot, WorkspaceLifecycle.Safe(wipe.Output));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogWarning(ex, "Workspace {Id}: cleaning up collaborator slot {Slot} failed", workspaceId, s.Slot);
        }
        finally { gate.Release(); }
    }
}
