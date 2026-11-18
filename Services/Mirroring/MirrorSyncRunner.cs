using System.Collections.Concurrent;
using GitBench.Data;
using GitBench.Services.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GitBench.Services.Mirroring;

/// <summary>What one sync did.</summary>
public sealed record MirrorSyncResult(bool Success, string Message, int ChangedRefs);

/// <summary>
/// Runs mirror syncs (singleton, no permission checks: callers check). One sync per mirror at a time. The outcome is
/// stored on the <see cref="RepositoryMirror"/> row (status, time, message) for the settings page.
/// <list type="bullet">
/// <item><b>Pull</b>: <c>git fetch --prune</c> of <c>refs/heads/*</c> and <c>refs/tags/*</c> straight into the bare repository
/// (with "only protected branches": just the upstream branches matching a branch protection rule, and local matching
/// branches that disappeared upstream are deleted). Afterwards the repository's push mirrors are queued if anything changed.
/// Push listeners (webhooks, CI, smart commits) don't run for mirrored commits.</item>
/// <item><b>Push</b>: <c>git push --prune</c> of the same refs (forced, so the remote follows this repository; with
/// "only protected branches" just the matching branches plus tags, and nothing is deleted).</item>
/// </list>
/// </summary>
public sealed class MirrorSyncRunner(IDbContextFactory<AppDbContext> dbFactory, RepoStorage storage, MirrorUrlGuard guard,
    MirrorCredentials credentials, IOptionsMonitor<MirrorOptions> options, IConfiguration config, IWebHostEnvironment env,
    MirrorQueue queue, ILogger<MirrorSyncRunner> log)
{
    private readonly ConcurrentDictionary<int, SemaphoreSlim> locks = new();
    private readonly string? gitPath = GitHttpBackend.LocateGit(config);

    public bool IsRunning(int mirrorId) => locks.TryGetValue(mirrorId, out var l) && l.CurrentCount == 0;

    /// <summary>
    /// Syncs the mirror now. When another sync of it is running, waits for it if <paramref name="wait"/>, else returns
    /// null right away. Errors are recorded on the mirror and returned, not thrown.
    /// </summary>
    public async Task<MirrorSyncResult?> SyncAsync(int mirrorId, bool wait, CancellationToken ct = default)
    {
        var gate = locks.GetOrAdd(mirrorId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(wait ? Timeout.Infinite : 0, ct)) return null;
        try { return await SyncCoreAsync(mirrorId, ct); }
        finally { gate.Release(); }
    }

    private async Task<MirrorSyncResult?> SyncCoreAsync(int mirrorId, CancellationToken ct)
    {
        RepositoryMirror? mirror;
        string? key;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            mirror = await db.RepositoryMirrors.AsNoTracking().Include(m => m.Repository).ThenInclude(r => r.Project)
                .FirstOrDefaultAsync(m => m.Id == mirrorId, ct);
            if (mirror == null) return null;
            key = mirror.Repository.Project.Key;
            await db.RepositoryMirrors.Where(m => m.Id == mirrorId).ExecuteUpdateAsync(s => s
                .SetProperty(m => m.LastStatus, MirrorSyncStatus.Running).SetProperty(m => m.LastSyncAt, DateTime.UtcNow), ct);
        }

        MirrorSyncResult result;
        try
        {
            if (gitPath == null) throw new MirrorException("git is not installed on this server.");
            var repoPath = storage.PathFor(key, mirror.Repository.Slug);
            if (!Directory.Exists(repoPath)) throw new MirrorException("The repository is missing on disk.");
            var uri = new Uri(mirror.Url);
            var pin = await guard.ResolveAsync(uri, ct);
            var secret = mirror.ProtectedSecret == null ? null : credentials.Reveal(mirror.ProtectedSecret);
            var git = new MirrorGit(gitPath, options.CurrentValue, env.ContentRootPath, log);
            using var remote = git.Prepare(uri, pin, mirror.Username, secret);
            result = mirror.Direction == MirrorDirection.Pull
                ? await PullAsync(git, remote, repoPath, mirror, ct)
                : await PushAsync(git, remote, repoPath, mirror, ct);
        }
        catch (MirrorException ex) { result = new MirrorSyncResult(false, ex.Message, 0); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { result = new MirrorSyncResult(false, "Cancelled (the server is shutting down).", 0); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or UriFormatException)
        {
            log.LogError(ex, "Mirror {Id} sync failed", mirrorId);
            result = new MirrorSyncResult(false, "Sync failed: " + ex.Message, 0);
        }

        var now = DateTime.UtcNow;
        var message = result.Message.Length > 2000 ? result.Message[..2000] : result.Message;
        await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
        {
            DateTime? next = mirror.Direction == MirrorDirection.Pull ? now.AddMinutes(Math.Max(1, mirror.IntervalMinutes)) : null;
            await db.RepositoryMirrors.Where(m => m.Id == mirrorId).ExecuteUpdateAsync(s => s
                .SetProperty(m => m.LastStatus, result.Success ? MirrorSyncStatus.Succeeded : MirrorSyncStatus.Failed)
                .SetProperty(m => m.LastSuccessAt, m => result.Success ? now : m.LastSuccessAt)
                .SetProperty(m => m.LastMessage, message)
                .SetProperty(m => m.NextSyncAt, next), CancellationToken.None);

            // A pull that changed something is passed on to the repository's push mirrors.
            if (result.Success && result.ChangedRefs > 0 && mirror.Direction == MirrorDirection.Pull)
                foreach (var id in await db.RepositoryMirrors.Where(m => m.RepositoryId == mirror.RepositoryId && m.Direction == MirrorDirection.Push && m.Enabled)
                             .Select(m => m.Id).ToListAsync(CancellationToken.None))
                    queue.Enqueue(id);
        }
        if (result.Success) log.LogInformation("Mirror {Id} ({Direction}) of repository {RepoId} synced: {Message}", mirrorId, mirror.Direction, mirror.RepositoryId, message);
        else log.LogWarning("Mirror {Id} ({Direction}) of repository {RepoId} failed: {Message}", mirrorId, mirror.Direction, mirror.RepositoryId, message);
        return result with { Message = message };
    }

    private async Task<MirrorSyncResult> PullAsync(MirrorGit git, MirrorGit.Remote remote, string repoPath, RepositoryMirror mirror, CancellationToken ct)
    {
        var before = await RefsAsync(git, repoPath, ct);
        var refspecs = new List<string>();
        List<string>? protectedPatterns = null;
        if (mirror.OnlyProtectedBranches)
        {
            protectedPatterns = await ProtectedPatternsAsync(mirror.RepositoryId, ct);
            var ls = await git.RunAsync(repoPath, remote, ["ls-remote", "--heads", "--", remote.Url], ct);
            if (ls.ExitCode != 0) throw new MirrorException(GitError("git ls-remote", ls));
            foreach (var line in ls.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length != 2 || !parts[1].StartsWith("refs/heads/", StringComparison.Ordinal)) continue;
                var branch = parts[1]["refs/heads/".Length..];
                if (protectedPatterns.Any(p => BranchPatterns.Matches(p, branch))) refspecs.Add($"+refs/heads/{branch}:refs/heads/{branch}");
            }
        }
        else refspecs.Add("+refs/heads/*:refs/heads/*");
        refspecs.Add("+refs/tags/*:refs/tags/*");

        var fetch = await git.RunAsync(repoPath, remote, ["fetch", "--prune", "--no-tags", "--no-write-fetch-head", "--quiet", "--", remote.Url, .. refspecs], ct);
        if (fetch.ExitCode != 0) throw new MirrorException(GitError("git fetch", fetch));

        if (protectedPatterns != null)
        {
            // Matching branches that are gone upstream go here too ("--prune" only covers the refspecs that were fetched).
            var fetched = refspecs.Select(r => r[(r.IndexOf(':') + 1)..]).ToHashSet(StringComparer.Ordinal);
            var stale = (await RefsAsync(git, repoPath, ct)).Keys
                .Where(r => r.StartsWith("refs/heads/", StringComparison.Ordinal) && !fetched.Contains(r)
                            && protectedPatterns.Any(p => BranchPatterns.Matches(p, r["refs/heads/".Length..])))
                .ToList();
            foreach (var r in stale)
            {
                var del = await git.RunAsync(repoPath, null, ["update-ref", "-d", r], ct);
                if (del.ExitCode != 0) throw new MirrorException(GitError("git update-ref", del));
            }
        }

        var after = await RefsAsync(git, repoPath, ct);
        var changed = after.Count(a => !before.TryGetValue(a.Key, out var sha) || sha != a.Value) + before.Keys.Count(k => !after.ContainsKey(k));
        return new MirrorSyncResult(true, changed == 0 ? "Up to date." : $"{changed} ref{(changed == 1 ? "" : "s")} updated.", changed);
    }

    private async Task<MirrorSyncResult> PushAsync(MirrorGit git, MirrorGit.Remote remote, string repoPath, RepositoryMirror mirror, CancellationToken ct)
    {
        var args = new List<string> { "push", "--porcelain" };
        var refspecs = new List<string>();
        if (mirror.OnlyProtectedBranches)
        {
            var patterns = await ProtectedPatternsAsync(mirror.RepositoryId, ct);
            foreach (var r in (await RefsAsync(git, repoPath, ct)).Keys.Where(r => r.StartsWith("refs/heads/", StringComparison.Ordinal)))
                if (patterns.Any(p => BranchPatterns.Matches(p, r["refs/heads/".Length..]))) refspecs.Add($"+{r}:{r}");
        }
        else
        {
            args.Add("--prune");
            refspecs.Add("+refs/heads/*:refs/heads/*");
        }
        refspecs.Add("+refs/tags/*:refs/tags/*");
        var push = await git.RunAsync(repoPath, remote, [.. args, "--", remote.Url, .. refspecs], ct);
        if (push.ExitCode != 0) throw new MirrorException(GitError("git push", push));
        // --porcelain: "<flag>\t<from>:<to>\t<summary>"; '=' means up to date.
        var changed = push.StdOut.Split('\n').Count(l => l.Length > 1 && l[1] == '\t' && l[0] is not '=' and not '!');
        return new MirrorSyncResult(true, changed == 0 ? "Up to date." : $"{changed} ref{(changed == 1 ? "" : "s")} pushed.", changed);
    }

    private static async Task<Dictionary<string, string>> RefsAsync(MirrorGit git, string repoPath, CancellationToken ct)
    {
        var o = await git.RunAsync(repoPath, null, ["for-each-ref", "--format=%(objectname) %(refname)", "refs/heads", "refs/tags"], ct);
        if (o.ExitCode != 0) throw new MirrorException(GitError("git for-each-ref", o));
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in o.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var sp = line.IndexOf(' ');
            if (sp > 0) refs[line[(sp + 1)..]] = line[..sp];
        }
        return refs;
    }

    private async Task<List<string>> ProtectedPatternsAsync(int repositoryId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.BranchProtectionRules.Where(r => r.RepositoryId == repositoryId).Select(r => r.Pattern).ToListAsync(ct);
    }

    /// <summary>git's own error lines (fatal:/error:/remote:), else the last lines of stderr.</summary>
    private static string GitError(string what, MirrorGit.Output o)
    {
        var lines = o.StdErr.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var useful = lines.Where(l => l.StartsWith("fatal:", StringComparison.Ordinal) || l.StartsWith("error:", StringComparison.Ordinal)
                                      || l.StartsWith("remote:", StringComparison.Ordinal) || l.StartsWith("!", StringComparison.Ordinal)).ToList();
        if (useful.Count == 0) useful = lines.TakeLast(5).ToList();
        if (useful.Count == 0) useful = o.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith('!')).TakeLast(5).ToList();
        var text = string.Join("\n", useful);
        return $"{what} failed (exit {o.ExitCode})" + (text.Length > 0 ? ": " + text : ".");
    }
}

/// <summary>Mirror ids waiting for the background service (push mirrors after a push, or after a pull mirror changed refs).</summary>
public sealed class MirrorQueue
{
    private readonly System.Threading.Channels.Channel<int> channel = System.Threading.Channels.Channel.CreateUnbounded<int>();
    private readonly ConcurrentDictionary<int, byte> pending = new();

    /// <summary>Queues a sync; a mirror already waiting isn't queued twice.</summary>
    public void Enqueue(int mirrorId)
    {
        if (pending.TryAdd(mirrorId, 0)) channel.Writer.TryWrite(mirrorId);
    }

    public async IAsyncEnumerable<int> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var id in channel.Reader.ReadAllAsync(ct))
        {
            pending.TryRemove(id, out _);
            yield return id;
        }
    }
}
