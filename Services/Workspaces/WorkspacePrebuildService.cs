using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using ByteBench.Data;
using ByteBench.Services.Audit;
using ByteBench.Services.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Workspaces;

/// <summary>
/// Prebuilds (singleton, no permission checks: <see cref="WorkspacePrebuildManager"/> and the push listener call it after theirs). A prebuild
/// is a Docker image committed from a container that cloned the repository, fetched its submodules and ran the setup commands of the
/// repository's own <c>.bytebench/workspace.yml</c>. New workspaces on that branch start from the newest usable prebuild
/// (<see cref="FindForStartAsync"/>); with none, or a stale or missing one, they start as before. A failing prebuild never blocks a start.
///
/// What keeps secrets out of the image: the build container gets no environment beyond <c>BB_*</c> and no workspace secrets; its read-only git token
/// is written for the clone only and the credential files are deleted before the setup commands run and again before the snapshot is taken.
/// Builds run one at a time on a background worker, newest push first within a branch (an older queued build for the same branch is superseded).
/// </summary>
public sealed class WorkspacePrebuildService(IDbContextFactory<AppDbContext> dbFactory, IWorkspaceBackend backend, WorkspaceRuntime runtime, WorkspaceLifecycle lifecycle,
    WorkspaceImage image, IOptionsMonitor<WorkspaceOptions> options, RepoStorage storage, IServiceScopeFactory scopes, ILogger<WorkspacePrebuildService> log) : BackgroundService
{
    private readonly Channel<int> queue = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim enqueueGate = new(1, 1);
    private int pending;
    private int cleaning;

    private WorkspaceOptions Opts => options.CurrentValue;

    // ---------- enqueue ----------

    /// <summary>
    /// Queues a prebuild of <paramref name="branch"/>. Null when the commit has no workspace environment file, or (unless <paramref name="force"/>) this very commit
    /// with this very configuration is already built or in the queue. A broken environment file gives a Failed row that says why.
    /// </summary>
    public async Task<WorkspacePrebuild?> EnqueueAsync(int repositoryId, string branch, string? sha, string trigger, int? userId, bool force = false)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var repo = await db.Repositories.AsNoTracking().Include(r => r.Project).FirstOrDefaultAsync(r => r.Id == repositoryId);
        if (repo == null) return null;
        var path = storage.PathFor(repo.Project.Key, repo.Slug);
        sha ??= await Task.Run(() => GitOps.ResolveSha(path, branch));
        if (sha == null) return null;
        var blob = await Task.Run(() => GitOps.GetBlob(path, sha, PrebuildConfigReader.FileName));
        if (blob == null) return null;

        var parsed = blob.Text == null || blob.TooLarge || blob.IsBinary
            ? new PrebuildConfigResult(null, PrebuildConfigReader.FileName + " is too large or isn't text.")
            : PrebuildConfigReader.Parse(blob.Text);

        await enqueueGate.WaitAsync();
        try
        {
            var live = await db.WorkspacePrebuilds.Where(p => p.RepositoryId == repositoryId && p.Ref == branch && (p.State == PrebuildState.Queued || p.State == PrebuildState.Building || p.State == PrebuildState.Ready)).ToListAsync();
            if (!force && parsed.Config != null && live.Any(p => p.CommitSha == sha && p.ConfigHash == parsed.Config.Hash)) return null;

            var pb = new WorkspacePrebuild
            {
                RepositoryId = repositoryId, ProjectId = repo.ProjectId, Ref = branch, CommitSha = sha, Trigger = trigger, TriggeredById = userId,
                BaseImage = Opts.Image, ConfigHash = parsed.Config?.Hash ?? "",
            };
            if (parsed.Config == null)
            {
                pb.State = PrebuildState.Failed;
                pb.Error = parsed.Error;
                pb.FinishedAt = DateTime.UtcNow;
                db.WorkspacePrebuilds.Add(pb);
                await db.SaveChangesAsync();
                return pb;
            }
            foreach (var older in live.Where(p => p.State == PrebuildState.Queued))
            {
                older.State = PrebuildState.Superseded; // a newer commit of the same branch replaces it before it ever started
                older.FinishedAt = DateTime.UtcNow;
                older.Error = "Replaced by a newer prebuild before it started.";
            }
            db.WorkspacePrebuilds.Add(pb);
            await db.SaveChangesAsync();
            Interlocked.Increment(ref pending);
            await queue.Writer.WriteAsync(pb.Id);
            return pb;
        }
        finally { enqueueGate.Release(); }
    }

    /// <summary>Completes when the queue is empty and nothing is being built (tests and the UI's "wait for it").</summary>
    public async Task WhenIdleAsync(TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while ((Volatile.Read(ref pending) > 0 || Volatile.Read(ref cleaning) > 0) && DateTime.UtcNow < until) await Task.Delay(20);
    }

    // ---------- worker ----------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RecoverAsync(stoppingToken);
            _ = Task.Run(() => CleanupLoopAsync(stoppingToken), CancellationToken.None);
            await foreach (var id in queue.Reader.ReadAllAsync(stoppingToken))
            {
                try { await BuildAsync(id, stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException) { log.LogWarning(ex, "Prebuild {Id} crashed", id); }
                finally { Interlocked.Decrement(ref pending); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>After a restart: builds that were running are failed (their containers are removed by the workspace cleanup), queued ones are queued again.</summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            foreach (var pb in await db.WorkspacePrebuilds.Where(p => p.State == PrebuildState.Building).ToListAsync(ct))
            {
                pb.State = PrebuildState.Failed;
                pb.Error = "ByteBench restarted while this prebuild was building.";
                pb.GitTokenHash = null;
                pb.FinishedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ct);
            foreach (var id in await db.WorkspacePrebuilds.Where(p => p.State == PrebuildState.Queued).OrderBy(p => p.Id).Select(p => p.Id).ToListAsync(ct))
            {
                Interlocked.Increment(ref pending);
                await queue.Writer.WriteAsync(id, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException) { log.LogWarning(ex, "Prebuild recovery failed"); }
    }

    private async Task CleanupLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, Opts.Prebuilds.CleanupIntervalMinutes)), ct);
                try { await CleanupAsync(null, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException) { log.LogWarning(ex, "Prebuild cleanup failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ---------- build ----------

    private sealed class PrebuildException(string message) : Exception(message);

    /// <summary>Builds one queued prebuild (public so tests can run it without the worker). Never throws for a failed build: the row says what happened.</summary>
    public async Task BuildAsync(int prebuildId, CancellationToken stop = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        var pb = await db.WorkspacePrebuilds.Include(p => p.Repository).ThenInclude(r => r.Project).FirstOrDefaultAsync(p => p.Id == prebuildId, CancellationToken.None);
        if (pb == null || pb.State != PrebuildState.Queued) return; // superseded or deleted while it waited
        var o = Opts;
        var text = new BuildLog(Math.Max(4096, o.Prebuilds.MaxLogBytes));
        pb.State = PrebuildState.Building;
        pb.StartedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
        runtime.SetPrebuilding(pb.Id, true);
        string? containerId = null, tag = null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        cts.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, o.Prebuilds.BuildTimeoutMinutes)));
        var ct = cts.Token;
        try
        {
            if (!o.Prebuilds.Enabled) throw new PrebuildException("Prebuilds are turned off on this server.");
            if (!await backend.IsAvailableAsync(ct)) throw new PrebuildException("Docker isn't available on this server.");
            if (image.State != WorkspaceImageState.Ready) await image.EnsureAsync(ct);
            if (image.State != WorkspaceImageState.Ready) throw new PrebuildException(image.Problem ?? "The workspace image isn't ready yet.");
            var user = await db.Users.AsNoTracking().Where(u => u.Id == pb.TriggeredById).Select(u => new { u.UserName, u.DisplayName }).FirstOrDefaultAsync(ct)
                ?? throw new PrebuildException("The user this prebuild runs for no longer exists.");

            var gitToken = WorkspaceTokens.NewGitToken();
            pb.GitTokenHash = WorkspaceTokens.Hash(gitToken);
            await db.SaveChangesAsync(CancellationToken.None);

            var slug = pb.Repository.Slug;
            var workDir = WorkspaceLifecycle.WorkDir(slug);
            var name = $"bb-pb-{Ci.CiDocker.InstanceKey}-{pb.Id}";
            var env = new Dictionary<string, string> { ["BB_WORKDIR"] = workDir, ["BB_IDE_PORT"] = WorkspaceLifecycle.IdePort.ToString() }; // (what a container is started with is baked into the snapshot's image config, so only what a workspace sets again anyway)
            text.Line($"Starting a build container from {o.Image}");
            var container = await backend.StartAsync(new WorkspaceContainerSpec(-pb.Id, name, o.Image, o.Network, "host.docker.internal", WorkspaceLifecycle.IdePort, env,
                new WorkspaceLimits(o.Memory, o.Cpus, o.PidsLimit, o.DiskLimit)) { PublishIdePort = false }, ct);
            containerId = container.ContainerId;
            await backend.WaitUntilWaitingAsync(containerId, ct);
            await lifecycle.ApplyEgressAsync(containerId, o.Image, o.EgressMode is "none" or "open" ? o.EgressMode : "public-only", ct);

            // Clone with a read-only token that exists only for this build.
            var git = lifecycle.GitBase();
            var credential = $"{git.Scheme}://{Uri.EscapeDataString(user.UserName!)}:{gitToken}@{git.Authority}\n";
            await RunAsync(containerId, new ExecSpec(["sh", "-c", "umask 077; cat > \"$HOME/.git-credentials\""]) { Stdin = credential }, "storing the git credential", text, ct);
            foreach (var args in new[]
            {
                new[] { "git", "config", "--global", "credential.helper", "store" },
                ["git", "config", "--global", "user.name", "ByteBench prebuild"],
                ["git", "config", "--global", "user.email", "prebuild@users.noreply.bytebench"],
            })
                await RunAsync(containerId, new ExecSpec(args), "configuring git", text, ct);

            var repoUrl = new Uri(git, $"git/{pb.Repository.Project.Key}/{slug}.git").ToString();
            text.Line($"== Cloning {pb.Ref}");
            await RunAsync(containerId, new ExecSpec(["sh", "-c", WorkspaceLifecycle.CloneScript, "sh", repoUrl, workDir, pb.Ref])
            { WorkDir = WorkspaceLifecycle.WorkspaceRoot, Timeout = TimeSpan.FromMinutes(10), MaxOutputBytes = 16 * 1024, Env = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" } },
                "checking out the repository", text, ct);
            var head = await backend.ExecAsync(containerId, new ExecSpec(["git", "-C", workDir, "rev-parse", "HEAD"]), ct);
            if (head.Ok && head.Output.Trim().Length == 40) pb.CommitSha = head.Output.Trim();

            // The configuration of the commit that was actually checked out is the one that counts.
            var path = storage.PathFor(pb.Repository.Project.Key, slug);
            var blob = pb.CommitSha == null ? null : await Task.Run(() => GitOps.GetBlob(path, pb.CommitSha, PrebuildConfigReader.FileName), ct);
            if (blob?.Text == null) throw new PrebuildException($"{PrebuildConfigReader.FileName} is no longer in {pb.Ref}.");
            var parsed = PrebuildConfigReader.Parse(blob.Text);
            if (parsed.Config == null) throw new PrebuildException(parsed.Error ?? "The environment file is invalid.");
            var config = parsed.Config;
            pb.ConfigHash = config.Hash;

            if (config.Submodules)
            {
                text.Line("== Submodules");
                await RunAsync(containerId, new ExecSpec(["sh", "-c", "if [ -f .gitmodules ]; then git submodule update --init --recursive; else echo 'no submodules'; fi"])
                { WorkDir = workDir, Timeout = TimeSpan.FromMinutes(10), Env = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" } }, "fetching submodules", text, ct);
            }

            await ScrubAsync(containerId, workDir, text, ct); // no credential of any kind while the repository's own commands run
            var number = 0;
            foreach (var command in config.Setup)
            {
                number++;
                text.Line($"== Setup {number}/{config.Setup.Count}");
                text.Line("$ " + command);
                var r = await backend.ExecAsync(containerId, new ExecSpec(["sh", "-c", command]) { WorkDir = workDir, Timeout = TimeSpan.FromMinutes(Math.Max(1, o.Prebuilds.BuildTimeoutMinutes)), MaxOutputBytes = 256 * 1024 }, ct);
                text.Block(r.Output);
                if (r.TimedOut) throw new PrebuildException($"Setup command {number} ran too long and was stopped.");
                if (!r.Ok) throw new PrebuildException($"Setup command {number} failed (exit code {r.ExitCode}): {Short(command)}");
            }
            await ScrubAsync(containerId, workDir, text, ct);

            text.Line("== Saving the snapshot");
            tag = $"bytebench-prebuild:{Ci.CiDocker.InstanceKey[..Math.Min(8, Ci.CiDocker.InstanceKey.Length)].ToLowerInvariant()}-r{pb.RepositoryId}-p{pb.Id}";
            if (!await backend.CommitAsync(containerId, tag, ct)) throw new PrebuildException("Docker couldn't save the snapshot (see the server log).");

            pb.State = PrebuildState.Ready;
            pb.ImageTag = tag;
            pb.BaseImage = o.Image;
            pb.Error = null;
            text.Line("Done.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var timedOut = ex is OperationCanceledException && !stop.IsCancellationRequested;
            pb.State = PrebuildState.Failed;
            pb.Error = Clip(timedOut ? $"The prebuild took longer than {o.Prebuilds.BuildTimeoutMinutes} minutes." : ex is OperationCanceledException ? "ByteBench is shutting down." : WorkspaceLifecycle.Safe(ex.Message), 500);
            text.Line("FAILED: " + pb.Error);
            if (ex is not PrebuildException && ex is not OperationCanceledException) log.LogWarning(ex, "Prebuild {Id} failed", pb.Id);
            if (tag != null) await TryRemoveImageAsync(tag);
            pb.ImageTag = null;
        }
        finally
        {
            if (containerId != null)
            {
                try { using var rm = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await backend.RemoveAsync(containerId, rm.Token); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { log.LogWarning(ex, "Could not remove prebuild container {Id}", containerId); }
            }
            runtime.SetPrebuilding(pb.Id, false);
            pb.GitTokenHash = null;
            pb.FinishedAt = DateTime.UtcNow;
            pb.Log = text.ToString();
            try { await db.SaveChangesAsync(CancellationToken.None); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { log.LogWarning(ex, "Could not save prebuild {Id}", pb.Id); }
        }
        await AuditAsync(pb.State == PrebuildState.Ready ? "workspace.prebuild_built" : "workspace.prebuild_failed", pb, new { @ref = pb.Ref, sha = pb.CommitSha, trigger = pb.Trigger, error = pb.Error });
        if (pb.State == PrebuildState.Ready) await CleanupAsync(pb.RepositoryId, CancellationToken.None);
    }

    /// <summary>Deletes every file that could hold a credential (the git credential store and config, the IDE token) from the build container.</summary>
    private async Task ScrubAsync(string containerId, string workDir, BuildLog text, CancellationToken ct)
    {
        var script = "rm -f \"$HOME/.git-credentials\" \"$HOME/.gitconfig\" /tmp/.bb-token; git -C \"$1\" config --local --unset-all credential.helper 2>/dev/null; " +
                     "test ! -e \"$HOME/.git-credentials\" && test ! -e \"$HOME/.gitconfig\"";
        await RunAsync(containerId, new ExecSpec(["sh", "-c", script, "sh", workDir]), "removing the git credential", text, ct);
    }

    private async Task RunAsync(string containerId, ExecSpec spec, string what, BuildLog text, CancellationToken ct)
    {
        var r = await backend.ExecAsync(containerId, spec, ct);
        if (spec.Stdin == null) text.Block(r.Output); // never echo what was fed in on stdin
        if (r.TimedOut) throw new PrebuildException($"Timed out while {what}.");
        if (!r.Ok) throw new PrebuildException($"Failed while {what}: {Short(WorkspaceLifecycle.Safe(r.Output))}");
    }

    private async Task TryRemoveImageAsync(string tag)
    {
        try { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)); await backend.RemoveImageAsync(tag, cts.Token); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { log.LogWarning(ex, "Could not remove prebuild image {Tag}", tag); }
    }

    private async Task AuditAsync(string action, WorkspacePrebuild pb, object details)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AuditLog>().RecordAsync(action, "repository", pb.RepositoryId, pb.ProjectId, details, actorId: pb.TriggeredById);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { log.LogDebug(ex, "Could not audit {Action}", action); }
    }

    private static string Short(string s) => Clip(s.ReplaceLineEndings(" ").Trim(), 160);
    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;

    /// <summary>Build output, bounded (the end is kept) and with anything that looks like a workspace token blanked out.</summary>
    private sealed class BuildLog(int maxChars)
    {
        private readonly StringBuilder sb = new();
        public void Line(string s) => sb.Append(Redact(s)).Append('\n');
        public void Block(string s) { if (!string.IsNullOrEmpty(s)) sb.Append(Redact(s.TrimEnd())).Append('\n'); }
        private static string Redact(string s) => Regex.Replace(Regex.Replace(s, @"gbw_[A-Za-z0-9_-]+", "gbw_…"), @"://[^/@\s]+:[^/@\s]+@", "://…@");
        public override string ToString() => sb.Length <= maxChars ? sb.ToString() : "… (earlier output left out)\n" + sb.ToString(sb.Length - maxChars, maxChars);
    }

    // ---------- using a prebuild ----------

    /// <summary>
    /// The prebuild a new workspace on <paramref name="refName"/> should start from: the newest Ready one for that branch, built on the current workspace image,
    /// from the same setup configuration as the branch has now, not past its retention, and whose image still exists. Null (a normal start) otherwise,
    /// and for any problem finding one.
    /// </summary>
    public async Task<WorkspacePrebuild?> FindForStartAsync(Repository repo, string refName)
    {
        try
        {
            var o = Opts;
            if (!o.Prebuilds.Enabled) return null;
            await using var db = await dbFactory.CreateDbContextAsync();
            var settings = await db.RepositoryPrebuildSettings.AsNoTracking().FirstOrDefaultAsync(s => s.RepositoryId == repo.Id);
            if (settings is not { Enabled: true }) return null;
            var path = storage.PathFor(repo.Project.Key, repo.Slug);
            var sha = await Task.Run(() => GitOps.ResolveSha(path, refName));
            if (sha == null) return null;
            var blob = await Task.Run(() => GitOps.GetBlob(path, sha, PrebuildConfigReader.FileName));
            if (blob?.Text == null || PrebuildConfigReader.Parse(blob.Text).Config is not { } config) return null;
            var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, settings.RetentionDays));
            var candidates = await db.WorkspacePrebuilds.Where(p => p.RepositoryId == repo.Id && p.Ref == refName && p.State == PrebuildState.Ready && p.ImageTag != null
                    && p.BaseImage == o.Image && p.ConfigHash == config.Hash && p.FinishedAt > cutoff)
                .OrderByDescending(p => p.FinishedAt).Take(5).ToListAsync();
            foreach (var c in candidates)
            {
                if (await backend.ImageExistsAsync(c.ImageTag!, CancellationToken.None)) return c;
                c.State = PrebuildState.Expired; // the image was removed behind our back
                c.ImageTag = null;
                await db.SaveChangesAsync();
            }
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogWarning(ex, "Looking for a prebuild of {Repo}@{Ref} failed; starting normally", repo.Slug, refName);
            return null;
        }
    }

    // ---------- cleanup ----------

    /// <summary>
    /// Removes prebuilds beyond each branch's keep-last count and older than the retention (all of them when the repository's prebuilds are off), retries
    /// images that couldn't be removed before (a workspace was still running from them), drops images no row knows about, and trims old failed rows.
    /// One repository, or all of them.
    /// </summary>
    public async Task CleanupAsync(int? repositoryId, CancellationToken ct)
    {
        Interlocked.Increment(ref cleaning);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var repoIds = repositoryId is { } one ? [one]
                : await db.WorkspacePrebuilds.Select(p => p.RepositoryId).Distinct().ToListAsync(ct);
            foreach (var id in repoIds) await CleanupRepositoryAsync(db, id, ct);
            if (repositoryId == null && await backend.IsAvailableAsync(ct))
            {
                var known = (await db.WorkspacePrebuilds.Where(p => p.ImageTag != null).Select(p => p.ImageTag!).ToListAsync(ct)).ToHashSet();
                foreach (var tag in await backend.ListPrebuildImagesAsync(ct))
                    if (!known.Contains(tag)) await TryRemoveImageAsync(tag); // an orphan: its row is gone
            }
        }
        finally { Interlocked.Decrement(ref cleaning); }
    }

    private async Task CleanupRepositoryAsync(AppDbContext db, int repositoryId, CancellationToken ct)
    {
        var settings = await db.RepositoryPrebuildSettings.AsNoTracking().FirstOrDefaultAsync(s => s.RepositoryId == repositoryId, ct);
        var keep = Math.Clamp(settings?.KeepLast ?? 3, 1, 50);
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, settings?.RetentionDays ?? 14));
        var rows = await db.WorkspacePrebuilds.Where(p => p.RepositoryId == repositoryId && p.State != PrebuildState.Queued && p.State != PrebuildState.Building).OrderByDescending(p => p.FinishedAt).ToListAsync(ct);

        var readyByRef = rows.Where(p => p.State == PrebuildState.Ready).GroupBy(p => p.Ref);
        foreach (var group in readyByRef)
        {
            var index = 0;
            foreach (var p in group)
            {
                index++;
                if (settings is not { Enabled: true } || index > keep || p.FinishedAt < cutoff) await ExpireAsync(p);
            }
        }
        foreach (var p in rows.Where(p => p.State == PrebuildState.Expired && p.ImageTag != null)) await ExpireAsync(p); // an earlier removal was refused

        var history = Math.Max(5, Opts.Prebuilds.KeepHistory);
        var old = rows.Where(p => p.State is PrebuildState.Failed or PrebuildState.Superseded || (p.State == PrebuildState.Expired && p.ImageTag == null)).Skip(history).ToList();
        db.WorkspacePrebuilds.RemoveRange(old);
        await db.SaveChangesAsync(ct);
    }

    private async Task ExpireAsync(WorkspacePrebuild p)
    {
        if (p.ImageTag != null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            bool removed;
            try { removed = await backend.RemoveImageAsync(p.ImageTag, cts.Token); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { log.LogWarning(ex, "Could not remove prebuild image {Tag}", p.ImageTag); removed = false; }
            if (removed) p.ImageTag = null; // otherwise kept so the next cleanup tries again
        }
        p.State = PrebuildState.Expired;
    }

    /// <summary>Removes one prebuild's image now and deletes its row (not while it is building).</summary>
    public async Task<bool> DeleteAsync(int prebuildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var p = await db.WorkspacePrebuilds.FirstOrDefaultAsync(x => x.Id == prebuildId);
        if (p == null || p.State == PrebuildState.Building) return false;
        if (p.State == PrebuildState.Queued) { p.State = PrebuildState.Superseded; p.Error = "Cancelled."; p.FinishedAt = DateTime.UtcNow; await db.SaveChangesAsync(); return true; }
        if (p.ImageTag != null && !await backend.RemoveImageAsync(p.ImageTag, CancellationToken.None))
        {
            p.State = PrebuildState.Expired; // in use by a running workspace: gone from the list of usable ones now, removed by a later cleanup
            await db.SaveChangesAsync();
            return true;
        }
        db.WorkspacePrebuilds.Remove(p);
        await db.SaveChangesAsync();
        return true;
    }
}
