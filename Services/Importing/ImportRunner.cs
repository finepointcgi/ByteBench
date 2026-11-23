using System.Collections.Concurrent;
using ByteBench.Data;
using ByteBench.Services.Git;
using ByteBench.Services.Mirroring;
using ByteBench.Services.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Importing;

/// <summary>What a look at the source URL found (wizard step 1 → 2).</summary>
public sealed record ImportProbe(string Url, string? DefaultBranch, IReadOnlyList<string> Branches, int TagCount, string SuggestedName);

/// <summary>
/// Clones remote repositories for the import wizard (singleton; callers check permissions). The URL goes through the mirror
/// SSRF guard (<see cref="MirrorUrlGuard"/>, including its private-network switch) and git runs through <see cref="MirrorGit"/>
/// (no shell, no global git config, credentials only through a temporary 0600 credential file, DNS pinned to the checked
/// address). Only <c>refs/heads/*</c> and <c>refs/tags/*</c> are fetched, so <c>refs/pull</c>, <c>refs/merge-queue</c> and other
/// reserved namespaces never come over. Credentials are held in memory for the one run and never written anywhere.
/// </summary>
public sealed class ImportRunner(IDbContextFactory<AppDbContext> dbFactory, RepoStorage storage, MirrorUrlGuard guard,
    IOptionsMonitor<MirrorOptions> mirrorOptions, IOptionsMonitor<ImportOptions> options, ImportTestHooks hooks, IConfiguration config, IWebHostEnvironment env,
    IServiceScopeFactory scopes, ILogger<ImportRunner> log)
{
    public const string InterruptedMessage = "The server restarted before this import finished, so it was stopped and its partial data removed. Start the import again.";

    private readonly string? gitPath = GitHttpBackend.LocateGit(config);
    private readonly System.Threading.Channels.Channel<int> queue = System.Threading.Channels.Channel.CreateUnbounded<int>();
    /// <summary>Imports this process knows about (queued or running), with the credentials they were started with.</summary>
    private readonly ConcurrentDictionary<int, (string? Username, string? Secret)> live = new();
    private readonly ConcurrentDictionary<int, RunState> running = new();

    private sealed class RunState
    {
        public CancellationTokenSource Cts { get; } = new();
        public volatile bool UserCancelled;
        public volatile bool TooLarge;
    }

    public string TempRoot => Path.Combine(Path.TrimEndingDirectorySeparator(storage.Root), ".imports");

    // ---------------------------------------------------------------- validation & probing

    /// <summary>Null if <paramref name="url"/> is an acceptable import source, else why not (same rules as mirror URLs).</summary>
    public string? Validate(string? url)
    {
        if (hooks.AllowFileUrls && Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var f) && f.IsFile && !f.IsUnc) return null;
        return guard.Validate(url);
    }

    private MirrorGit NewGit(int timeoutSeconds)
    {
        if (gitPath == null) throw new MirrorException("git is not installed on this server.");
        var m = mirrorOptions.CurrentValue;
        var o = new MirrorOptions { SshKeyFile = m.SshKeyFile, KnownHostsFile = m.KnownHostsFile, TimeoutSeconds = timeoutSeconds };
        return new MirrorGit(gitPath, o, env.ContentRootPath, log)
        {
            AllowedProtocols = hooks.AllowFileUrls ? "http:https:ssh:file" : "http:https:ssh",
        };
    }

    private async Task<MirrorGit.Remote> PrepareAsync(MirrorGit git, string url, string? username, string? secret, CancellationToken ct)
    {
        if (Validate(url) is { } invalid) throw new MirrorException(invalid);
        var uri = new Uri(url.Trim());
        if (uri.IsFile) return new MirrorGit.Remote { Url = uri.OriginalString };
        var pin = await guard.ResolveAsync(uri, ct);
        return git.Prepare(uri, pin, username, secret);
    }

    /// <summary>Asks the remote for its default branch, branches and tags (<c>git ls-remote</c>), to fill in wizard step 2.</summary>
    public async Task<ImportProbe> ProbeAsync(string url, string? username, string? secret, CancellationToken ct)
    {
        url = (url ?? "").Trim();
        var git = NewGit(60);
        using var remote = await PrepareAsync(git, url, username, secret, ct);
        var tmp = Path.Combine(TempRoot, "probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Task.Run(() => GitOps.InitBare(tmp, "main"), ct);
            var o = await git.RunAsync(tmp, remote, ["ls-remote", "--symref", "--", remote.Url, "HEAD", "refs/heads/*", "refs/tags/*"], ct);
            if (o.ExitCode != 0) throw new MirrorException(GitError("Couldn't read the repository", o));
            string? head = null;
            var branches = new List<string>();
            var tags = 0;
            foreach (var line in o.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length != 2) continue;
                if (parts[0].StartsWith("ref: refs/heads/", StringComparison.Ordinal) && parts[1] == "HEAD") head = parts[0]["ref: refs/heads/".Length..];
                else if (parts[1].StartsWith("refs/heads/", StringComparison.Ordinal)) branches.Add(parts[1]["refs/heads/".Length..]);
                else if (parts[1].StartsWith("refs/tags/", StringComparison.Ordinal) && !parts[1].EndsWith("^{}", StringComparison.Ordinal)) tags++;
            }
            if (branches.Count == 0) throw new MirrorException("The repository has no branches, so there is nothing to import.");
            branches.Sort(StringComparer.Ordinal);
            var def = PickDefaultBranch(head, branches);
            return new ImportProbe(url, def, branches.Take(200).ToList(), tags, SuggestName(url));
        }
        finally { DeleteDirectory(tmp); }
    }

    private static string PickDefaultBranch(string? head, IReadOnlyCollection<string> branches) =>
        head != null && branches.Contains(head) ? head
        : branches.Contains("main") ? "main" : branches.Contains("master") ? "master" : branches.First();

    /// <summary>"https://host/group/My-Repo.git" → "My-Repo".</summary>
    public static string SuggestName(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url;
        var last = path.TrimEnd('/').Split('/').LastOrDefault() ?? "";
        if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) last = last[..^4];
        return Uri.UnescapeDataString(last);
    }

    // ---------------------------------------------------------------- queue

    /// <summary>Remembers the credentials for the run and queues the import.</summary>
    public void Enqueue(int importId, string? username, string? secret)
    {
        live[importId] = (username, secret);
        queue.Writer.TryWrite(importId);
    }

    public IAsyncEnumerable<int> ReadQueueAsync(CancellationToken ct) => queue.Reader.ReadAllAsync(ct);

    public bool IsLive(int importId) => live.ContainsKey(importId);

    /// <summary>
    /// Cancels an import: a queued one is marked cancelled right away, a running one is stopped (git is killed, the partial
    /// data removed). Returns false if it was already finished.
    /// </summary>
    public async Task<bool> CancelAsync(int importId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var queued = await db.RepositoryImports.Where(i => i.Id == importId && i.Status == ImportStatus.Queued).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.Status, ImportStatus.Cancelled).SetProperty(i => i.Error, "Cancelled.").SetProperty(i => i.FinishedAt, DateTime.UtcNow));
        if (queued > 0) { Forget(importId); return true; }
        if (running.TryGetValue(importId, out var state))
        {
            state.UserCancelled = true;
            try { state.Cts.Cancel(); } catch (ObjectDisposedException) { }
            return true;
        }
        return false;
    }

    private void Forget(int importId) => live.TryRemove(importId, out _);

    // ---------------------------------------------------------------- restart safety

    /// <summary>
    /// Marks imports that were queued or running in a previous process (nothing in this one knows them) as failed and removes
    /// their temporary data. Called when the worker starts.
    /// </summary>
    public async Task RecoverInterruptedAsync()
    {
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var stuck = await db.RepositoryImports.Where(i => i.Status == ImportStatus.Queued || i.Status == ImportStatus.Cloning).Select(i => i.Id).ToListAsync();
            var orphans = stuck.Where(id => !live.ContainsKey(id)).ToList();
            if (orphans.Count > 0)
            {
                await db.RepositoryImports.Where(i => orphans.Contains(i.Id)).ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.Status, ImportStatus.Failed).SetProperty(i => i.Error, InterruptedMessage).SetProperty(i => i.FinishedAt, DateTime.UtcNow));
                log.LogWarning("Marked {Count} interrupted repository import(s) as failed", orphans.Count);
            }
        }
        try
        {
            if (!Directory.Exists(TempRoot)) return;
            foreach (var dir in Directory.EnumerateDirectories(TempRoot))
            {
                var name = Path.GetFileName(dir);
                var dash = name.IndexOf('-');
                if (dash > 0 && int.TryParse(name[..dash], out var id) && live.ContainsKey(id)) continue;
                if (name.StartsWith("probe-", StringComparison.Ordinal) && Directory.GetCreationTimeUtc(dir) > DateTime.UtcNow.AddHours(-1)) continue;
                DeleteDirectory(dir);
            }
        }
        catch (IOException ex) { log.LogWarning(ex, "Couldn't clean up {Dir}", TempRoot); }
    }

    // ---------------------------------------------------------------- running an import

    /// <summary>
    /// Runs one queued import to the end (clone, create the repository, notify listeners once). Claims it first, so an import
    /// never runs twice; returns false if it wasn't queued. Failures are recorded on the import, not thrown.
    /// </summary>
    public async Task<bool> RunAsync(int importId, CancellationToken stopping = default)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
        {
            var claimed = await db.RepositoryImports.Where(i => i.Id == importId && i.Status == ImportStatus.Queued).ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, ImportStatus.Cloning).SetProperty(i => i.StartedAt, DateTime.UtcNow), CancellationToken.None);
            if (claimed == 0) return false;
        }
        var state = new RunState();
        running[importId] = state;
        live.TryGetValue(importId, out var credentials);
        string? tmp = null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(state.Cts.Token, stopping);
            tmp = await CloneAsync(importId, credentials, state, linked.Token);
            await FinishAsync(importId, tmp, credentials, state);
            tmp = null; // moved into place
        }
        catch (Exception ex)
        {
            var message = ex switch
            {
                OperationCanceledException when state.UserCancelled => "Cancelled.",
                OperationCanceledException when state.TooLarge => $"The repository is larger than the limit of {FormatSize(options.CurrentValue.MaxRepoBytes)} (Import:MaxRepoBytes).",
                OperationCanceledException => "Stopped because the server is shutting down.",
                MirrorException or InvalidOperationException => ex.Message,
                IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception => "Import failed: " + ex.Message,
                _ => "Import failed unexpectedly (see the server log).",
            };
            if (ex is not (OperationCanceledException or MirrorException or InvalidOperationException)) log.LogError(ex, "Import {Id} failed", importId);
            var status = ex is OperationCanceledException && state.UserCancelled ? ImportStatus.Cancelled : ImportStatus.Failed;
            await SetFinalAsync(importId, status, message);
            await AuditAsync(importId, status == ImportStatus.Cancelled ? "repository.import_cancelled" : "repository.import_failed", message);
        }
        finally
        {
            if (tmp != null) DeleteDirectory(tmp);
            running.TryRemove(importId, out _);
            state.Cts.Dispose();
            Forget(importId);
        }
        return true;
    }

    /// <summary>Fetches every branch and tag into a fresh bare repository under the temp root and returns its path.</summary>
    private async Task<string> CloneAsync(int importId, (string? Username, string? Secret) credentials, RunState state, CancellationToken ct)
    {
        RepositoryImport job;
        await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
            job = await db.RepositoryImports.AsNoTracking().FirstAsync(i => i.Id == importId, CancellationToken.None);
        var o = options.CurrentValue;
        var git = NewGit(Math.Max(10, o.TimeoutMinutes * 60));
        using var remote = await PrepareAsync(git, job.SourceUrl, credentials.Username, credentials.Secret, ct);

        var tmp = Path.Combine(TempRoot, $"{importId}-{Guid.NewGuid():N}");
        try
        {
            await Task.Run(() => GitOps.InitBare(tmp, "main"), ct);
            using var monitorStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var monitor = MonitorSizeAsync(importId, tmp, o.MaxRepoBytes, state, monitorStop.Token);
            MirrorGit.Output fetch;
            try
            {
                fetch = await git.RunAsync(tmp, remote, ["fetch", "--no-tags", "--no-write-fetch-head", "--quiet", "--", remote.Url,
                    "+refs/heads/*:refs/heads/*", "+refs/tags/*:refs/tags/*"], ct);
            }
            finally
            {
                monitorStop.Cancel();
                await monitor;
            }
            ct.ThrowIfCancellationRequested();
            if (fetch.ExitCode != 0) throw new MirrorException(GitError("git fetch", fetch));

            if (job.DefaultBranch == null)
            {
                // No branch was chosen (API-style use): follow the source's HEAD.
                var head = await git.RunAsync(tmp, remote, ["ls-remote", "--symref", "--", remote.Url, "HEAD"], ct);
                var line = head.ExitCode == 0 ? head.StdOut.Split('\n').FirstOrDefault(l => l.StartsWith("ref: refs/heads/", StringComparison.Ordinal)) : null;
                if (line != null)
                {
                    var branch = line["ref: refs/heads/".Length..].Split('\t')[0];
                    await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
                    await db.RepositoryImports.Where(i => i.Id == importId).ExecuteUpdateAsync(s => s.SetProperty(i => i.DefaultBranch, branch), CancellationToken.None);
                }
            }

            var size = DirectorySize(tmp);
            await SetBytesAsync(importId, size);
            if (size > o.MaxRepoBytes) { state.TooLarge = true; throw new OperationCanceledException(); }
            return tmp;
        }
        catch
        {
            DeleteDirectory(tmp);
            throw;
        }
    }

    /// <summary>Records the size on disk while git runs, and stops the clone when it passes the limit.</summary>
    private async Task MonitorSizeAsync(int importId, string dir, long max, RunState state, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct);
                var size = DirectorySize(dir);
                await SetBytesAsync(importId, size);
                if (size > max)
                {
                    state.TooLarge = true;
                    try { state.Cts.Cancel(); } catch (ObjectDisposedException) { }
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Creates the repository from the fetched data through <see cref="RepositoryService"/>, then notifies listeners once.</summary>
    private async Task FinishAsync(int importId, string tmp, (string? Username, string? Secret) credentials, RunState state)
    {
        RepositoryImport job;
        User author;
        await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
        {
            job = await db.RepositoryImports.AsNoTracking().FirstAsync(i => i.Id == importId, CancellationToken.None);
            author = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == job.RequestedById, CancellationToken.None)
                     ?? throw new InvalidOperationException("The user who started this import no longer exists.");
        }
        if (state.UserCancelled) throw new OperationCanceledException();

        var (branches, tags, defaultBranch, tip) = await Task.Run(() =>
        {
            var b = GitOps.ListBranches(tmp, "");
            if (b.Count == 0) throw new MirrorException("The repository has no branches, so there is nothing to import.");
            var names = b.Select(x => x.Name).ToList();
            var def = job.DefaultBranch != null && names.Contains(job.DefaultBranch) ? job.DefaultBranch
                : PickDefaultBranch(null, names.Order(StringComparer.Ordinal).ToList());
            return (b.Count, GitOps.ListTags(tmp).Count, def, b.First(x => x.Name == def).Tip!.Sha);
        });

        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ProjectAccess>().ActAs(author.Id);
        var repos = sp.GetRequiredService<RepositoryService>();
        var repo = await repos.CreateImportedAsync(job.ProjectId, job.Name, job.Slug, job.Description, defaultBranch, tmp, job.SourceUrl, author);

        string? note = null;
        if (job.KeepAsMirror)
        {
            try
            {
                await sp.GetRequiredService<MirrorService>().SavePullAsync(repo.Id, new MirrorInput
                {
                    Url = job.SourceUrl, Username = credentials.Username, Secret = credentials.Secret, IntervalMinutes = 60,
                }, author.Id);
            }
            catch (InvalidOperationException ex)
            {
                note = "Imported, but it couldn't be kept as a pull mirror: " + ex.Message;
            }
        }

        var bytes = DirectorySize(storage.PathFor(repo.Project.Key, repo.Slug));
        await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
            await db.RepositoryImports.Where(i => i.Id == importId).ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, ImportStatus.Done).SetProperty(i => i.RepositoryId, repo.Id).SetProperty(i => i.Error, note)
                .SetProperty(i => i.BranchCount, branches).SetProperty(i => i.TagCount, tags).SetProperty(i => i.BytesReceived, bytes)
                .SetProperty(i => i.DefaultBranch, defaultBranch).SetProperty(i => i.FinishedAt, DateTime.UtcNow), CancellationToken.None);
        await sp.GetRequiredService<Audit.AuditLog>().RecordAsync("repository.imported", "repository", repo.Id, job.ProjectId,
            new { repo.Slug, source = job.SourceUrl, branches, tags, keptAsMirror = job.KeepAsMirror && note == null }, author.Id);

        // One summary event instead of one per imported branch and tag: the default branch was "pushed" (created) with its history.
        await using var notify = scopes.CreateAsyncScope();
        notify.ServiceProvider.GetRequiredService<ProjectAccess>().ActAs(author.Id);
        notify.ServiceProvider.GetRequiredService<PushCapture>().SetRefs([WebhookPushEvents.Branch(defaultBranch, null, tip)]);
        await GitRepoLookup.NotifyPushListenersAsync(notify.ServiceProvider, log, repo.Id, author.Id);
        log.LogInformation("Imported {Url} as {Key}/{Slug}: {Branches} branches, {Tags} tags", job.SourceUrl, repo.Project.Key, repo.Slug, branches, tags);
    }

    // ---------------------------------------------------------------- helpers

    private async Task SetFinalAsync(int importId, ImportStatus status, string message)
    {
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        var text = message.Length > 2000 ? message[..2000] : message;
        await db.RepositoryImports.Where(i => i.Id == importId).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.Status, status).SetProperty(i => i.Error, text).SetProperty(i => i.FinishedAt, DateTime.UtcNow), CancellationToken.None);
    }

    private async Task SetBytesAsync(int importId, long bytes)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            await db.RepositoryImports.Where(i => i.Id == importId && i.Status == ImportStatus.Cloning)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.BytesReceived, bytes), CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException) { log.LogDebug(ex, "Couldn't record import progress"); }
    }

    private async Task AuditAsync(int importId, string action, string message)
    {
        try
        {
            RepositoryImport? job;
            await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
                job = await db.RepositoryImports.AsNoTracking().FirstOrDefaultAsync(i => i.Id == importId, CancellationToken.None);
            if (job == null) return;
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<Audit.AuditLog>().RecordAsync(action, "repository_import", importId, job.ProjectId,
                new { slug = job.Slug, source = job.SourceUrl, error = message }, job.RequestedById);
        }
        catch (Exception ex) { log.LogWarning(ex, "Couldn't audit import {Id}", importId); }
    }

    public static long DirectorySize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try { total += f.Length; } catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return total;
    }

    internal static void DeleteDirectory(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); // git marks packs read-only
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
    };

    /// <summary>git's own error lines (fatal:/error:/remote:), else the last lines of stderr.</summary>
    private static string GitError(string what, MirrorGit.Output o)
    {
        var lines = o.StdErr.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var useful = lines.Where(l => l.StartsWith("fatal:", StringComparison.Ordinal) || l.StartsWith("error:", StringComparison.Ordinal)
                                      || l.StartsWith("remote:", StringComparison.Ordinal)).ToList();
        if (useful.Count == 0) useful = lines.TakeLast(5).ToList();
        var text = string.Join("\n", useful);
        return $"{what} failed (exit {o.ExitCode})" + (text.Length > 0 ? ": " + text : ".");
    }
}
