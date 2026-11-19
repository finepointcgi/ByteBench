using System.Diagnostics;
using LibGit2Sharp;
using LgRepository = LibGit2Sharp.Repository;

namespace ByteBench.Services.Git;

/// <summary>The outcome of "Sync fork" for the fork's default branch.</summary>
public enum ForkSyncState { UpToDate, FastForwarded, Ahead, Diverged, NoUpstreamBranch }

/// <param name="Before">The fork's default branch tip before the sync (null if it had none).</param>
/// <param name="After">Its tip afterwards.</param>
/// <param name="Ahead">Commits on the fork's branch that upstream doesn't have.</param>
/// <param name="Behind">Commits on upstream's branch that the fork didn't have (before a fast-forward).</param>
/// <param name="Branches">Upstream branches fetched into <c>refs/upstream/*</c>.</param>
public sealed record ForkSyncResult(ForkSyncState State, string? Before, string? After, int Ahead, int Behind, int Branches);

/// <summary>Forks: copying a bare repository and moving commits between the repositories of a fork network.</summary>
public static partial class GitOps
{
    /// <summary>Where "Sync fork" keeps the upstream's branches inside a fork (not branches of the fork itself).</summary>
    public const string UpstreamRefPrefix = "refs/upstream/";

    /// <summary>A branch head or, for names starting with <c>refs/</c> (a fork PR's <c>refs/pull/{n}/head</c>), any ref.</summary>
    public sealed record SourceHead(Commit? Tip);

    /// <summary>
    /// Resolves a PR's source: a branch name, or a full ref name such as <c>refs/pull/{n}/head</c> (which isn't a branch,
    /// so <see cref="BranchCollection"/> doesn't see it). Null when it doesn't exist.
    /// </summary>
    internal static SourceHead? SourceTip(LgRepository r, string name)
    {
        if (name.StartsWith("refs/", StringComparison.Ordinal) && !name.StartsWith("refs/heads/", StringComparison.Ordinal))
            return r.Refs[name]?.ResolveToDirectReference()?.Target is Commit c ? new SourceHead(c) : null;
        return r.Branches[name] is { } b ? new SourceHead(b.Tip) : null;
    }

    /// <summary>
    /// Makes <paramref name="to"/> a copy of the bare repository at <paramref name="from"/>: every branch and tag, the
    /// same HEAD, no remote. With a git executable this is <c>git clone --bare --local</c>, which hard-links the object
    /// files (nearly free on disk, and still independent: deleting either repository never affects the other, unlike
    /// <c>--shared</c>/alternates). Without one, LibGit2Sharp fetches everything into a new repository (a full copy).
    /// </summary>
    public static void CloneBare(string from, string to, string? gitPath)
    {
        if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any())
            throw new InvalidOperationException("A repository already exists at that location on disk.");
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        try
        {
            if (gitPath != null)
            {
                RunGit(gitPath, null, "clone", "--bare", "--local", "--quiet", "--", from, to);
                RunGit(gitPath, to, "remote", "remove", "origin");
            }
            else
            {
                string head;
                using (var src = new LgRepository(from)) head = src.Head.CanonicalName;
                LgRepository.Init(to, isBare: true);
                using var r = new LgRepository(to);
                Dictionary<string, string> refs;
                using (var src = new LgRepository(from))
                    refs = src.Refs.Where(x => x.CanonicalName.StartsWith("refs/heads/", StringComparison.Ordinal) || x.CanonicalName.StartsWith("refs/tags/", StringComparison.Ordinal))
                        .Select(x => (x.CanonicalName, Sha: x.ResolveToDirectReference()?.TargetIdentifier)).Where(x => x.Sha != null)
                        .ToDictionary(x => x.CanonicalName, x => x.Sha!);
                CopyRefs(r, from, refs, "fork");
                r.Refs.UpdateTarget("HEAD", head);
            }
        }
        catch
        {
            try { if (Directory.Exists(to)) Directory.Delete(to, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// Fetches <paramref name="branch"/> of the repository at <paramref name="fromPath"/> into <paramref name="toRef"/> of
    /// <paramref name="intoPath"/> (forced: a fork PR's ref follows force pushes too). Returns the new tip, or null if
    /// the branch doesn't exist (the ref is then left as it was).
    /// </summary>
    public static string? FetchBranchInto(string intoPath, string fromPath, string branch, string toRef)
    {
        string? tip;
        using (var src = new LgRepository(fromPath)) tip = src.Branches[branch]?.Tip?.Sha;
        if (tip == null) return null;
        lock (LockFor(intoPath))
        {
            using var r = new LgRepository(intoPath);
            if (r.Refs[toRef]?.ResolveToDirectReference()?.TargetIdentifier == tip) return tip;
            CopyRefs(r, fromPath, new Dictionary<string, string> { [toRef] = tip }, "fork pull request");
            return r.Refs[toRef]?.ResolveToDirectReference()?.TargetIdentifier;
        }
    }

    /// <summary>Deletes a ref if it exists (e.g. a temporary preview ref).</summary>
    public static void DeleteRef(string path, string refName)
    {
        lock (LockFor(path))
        {
            using var r = new LgRepository(path);
            if (r.Refs[refName] != null) r.Refs.Remove(refName);
        }
    }

    /// <summary>
    /// "Sync fork": fetches every branch of the upstream into <c>refs/upstream/*</c> of the fork, then fast-forwards the
    /// fork's <paramref name="branch"/> to upstream's <paramref name="upstreamBranch"/> when the fork has nothing upstream
    /// lacks. A diverged or ahead branch is left alone. Returns what happened.
    /// </summary>
    public static ForkSyncResult SyncFork(string forkPath, string upstreamPath, string branch, string upstreamBranch)
    {
        lock (LockFor(forkPath))
        {
            using var r = new LgRepository(forkPath);
            Dictionary<string, string> wanted;
            using (var src = new LgRepository(upstreamPath))
                wanted = src.Branches.Where(b => !b.IsRemote && b.Tip != null)
                    .ToDictionary(b => UpstreamRefPrefix + b.FriendlyName, b => b.Tip.Sha);
            CopyRefs(r, upstreamPath, wanted, "sync fork");
            foreach (var stale in r.Refs.Where(x => x.CanonicalName.StartsWith(UpstreamRefPrefix, StringComparison.Ordinal) && !wanted.ContainsKey(x.CanonicalName)).ToList())
                r.Refs.Remove(stale); // branches deleted upstream
            var fetched = wanted.Count;
            var up = r.Refs[UpstreamRefPrefix + upstreamBranch]?.ResolveToDirectReference()?.Target as Commit;
            var mine = r.Branches[branch];
            if (up == null) return new ForkSyncResult(ForkSyncState.NoUpstreamBranch, mine?.Tip?.Sha, mine?.Tip?.Sha, 0, 0, fetched);
            if (mine?.Tip == null)
            {
                r.Branches.Add(branch, up);
                return new ForkSyncResult(ForkSyncState.FastForwarded, null, up.Sha, 0, 0, fetched);
            }
            var div = r.ObjectDatabase.CalculateHistoryDivergence(mine.Tip, up);
            int ahead = div.AheadBy ?? 0, behind = div.BehindBy ?? 0;
            var before = mine.Tip.Sha;
            if (behind == 0) return new ForkSyncResult(ahead == 0 ? ForkSyncState.UpToDate : ForkSyncState.Ahead, before, before, ahead, 0, fetched);
            if (ahead > 0) return new ForkSyncResult(ForkSyncState.Diverged, before, before, ahead, behind, fetched);
            r.Refs.UpdateTarget(r.Refs[mine.CanonicalName], up.Id, $"sync fork: fast-forward to upstream {upstreamBranch}");
            return new ForkSyncResult(ForkSyncState.FastForwarded, before, up.Sha, 0, behind, fetched);
        }
    }

    /// <summary>
    /// Points each ref of <paramref name="wanted"/> (ref name → commit SHA in the repository at <paramref name="path"/>)
    /// at its commit in <paramref name="r"/>, first fetching the commits <paramref name="r"/> doesn't have yet. The refs
    /// are written here rather than by the fetch, so they're set even when no objects needed fetching. Callers hold the lock.
    /// </summary>
    private static void CopyRefs(LgRepository r, string path, IReadOnlyDictionary<string, string> wanted, string logMessage)
    {
        var missing = wanted.Values.Distinct().Where(sha => r.ObjectDatabase.Contains(new ObjectId(sha)) == false).ToList();
        if (missing.Count > 0)
        {
            // Fetch the source's branches into a scratch namespace (LibGit2Sharp fetches from named remotes only),
            // then drop the scratch refs and the remote again.
            var name = "bytebench-" + Guid.NewGuid().ToString("N")[..12];
            r.Network.Remotes.Add(name, path, $"+refs/heads/*:refs/{name}/*");
            try { Commands.Fetch(r, name, [$"+refs/heads/*:refs/{name}/*"], new FetchOptions { TagFetchMode = TagFetchMode.None }, logMessage); }
            finally
            {
                foreach (var x in r.Refs.Where(x => x.CanonicalName.StartsWith($"refs/{name}/", StringComparison.Ordinal)).ToList()) r.Refs.Remove(x);
                r.Network.Remotes.Remove(name);
            }
            if (wanted.Values.FirstOrDefault(sha => !r.ObjectDatabase.Contains(new ObjectId(sha))) is { } lost)
                throw new InvalidOperationException($"Commit {lost[..7]} could not be fetched.");
        }
        foreach (var (name, sha) in wanted) r.Refs.Add(name, new ObjectId(sha), logMessage, allowOverwrite: true);
    }

    /// <summary>Runs git with an argument list (never a shell), without the caller's GIT_* environment; throws with stderr on failure.</summary>
    private static void RunGit(string gitPath, string? gitDir, params string[] args)
    {
        var psi = new ProcessStartInfo(gitPath)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var k in psi.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(k);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (gitDir != null) psi.ArgumentList.Add("--git-dir=" + gitDir);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git.");
        var stderr = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(TimeSpan.FromMinutes(10)))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new InvalidOperationException($"git {args[0]} timed out.");
        }
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {args[0]} failed: {stderr.Result.Trim()}");
    }
}
