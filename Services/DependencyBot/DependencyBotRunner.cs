using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Services.Audit;
using ByteBench.Services.Git;
using ByteBench.Services.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.DependencyBot;

/// <summary>
/// Finds outdated dependencies and opens update pull requests. Singleton; no permission checks (callers are the scheduler and
/// <see cref="DependencyBotService"/>). Package managers are never run: manifests are read from the bare repository and parsed,
/// registries are asked over HTTP through the SSRF guard, and the update commit is written with Git object plumbing
/// (<see cref="GitOps.CommitFileBytesOnNewBranch"/>) touching only the manifest files. One check or run per repository at a time.
/// </summary>
public sealed partial class DependencyBotRunner(IDbContextFactory<AppDbContext> dbFactory, IServiceScopeFactory scopes, RepoStorage storage,
    RegistryClient registries, IOptionsMonitor<DependencyBotOptions> options, ILogger<DependencyBotRunner> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", "vendor", ".git", "bin", "obj", "third_party", "bower_components", ".venv", "venv", "site-packages" };
    private readonly ConcurrentDictionary<int, SemaphoreSlim> locks = new();
    private readonly SemaphoreSlim scheduled = new(1, 1);

    public static DependencyReport? ReadReport(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<DependencyReport>(json, Json); }
        catch (JsonException) { return null; }
    }

    private static string DateKey(DateTime d) => d.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ scheduling

    /// <summary>Runs the enabled repositories whose time has come, one at a time. Returns how many ran.</summary>
    public async Task<int> RunDueAsync(DateTime nowUtc, CancellationToken ct)
    {
        if (!await scheduled.WaitAsync(0, ct)) return 0;
        try
        {
            List<int> due;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
                due = await db.DependencyBotSettings.Where(s => s.Enabled && s.NextRunAt != null && s.NextRunAt <= nowUtc)
                    .OrderBy(s => s.NextRunAt).Select(s => s.RepositoryId).Take(20).ToListAsync(ct);
            var ran = 0;
            foreach (var repositoryId in due)
            {
                if (ct.IsCancellationRequested) break;
                try { if (await RunScheduledAsync(repositoryId, ct)) ran++; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { log.LogError(ex, "Dependency bot run for repository {Id} failed", repositoryId); }
            }
            return ran;
        }
        finally { scheduled.Release(); }
    }

    private static DateTime NextRun(DependencySchedule schedule, DateTime from) => schedule switch
    {
        DependencySchedule.Daily => from.AddDays(1),
        DependencySchedule.Monthly => from.AddMonths(1),
        _ => from.AddDays(7),
    };

    /// <summary>One scheduled run: check, then open the pull requests the settings allow. False if the repository was busy.</summary>
    public async Task<bool> RunScheduledAsync(int repositoryId, CancellationToken ct)
    {
        var gate = locks.GetOrAdd(repositoryId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) return false;
        try
        {
            string status; string message;
            try
            {
                var report = await CheckCoreAsync(repositoryId, ct);
                var result = await ApplyCoreAsync(repositoryId, report, null, null, ct);
                status = result.Warnings.Count > 0 || report.Warnings.Count > 0 ? "warnings" : "ok";
                message = $"{report.Items.Count(i => i.Status == ItemStatus.Update)} update(s) found, {result.PullRequests.Count} pull request(s) opened.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Dependency check of repository {Id} failed", repositoryId);
                status = "failed"; message = ex is InvalidOperationException ? ex.Message : "The check failed unexpectedly (see the server log).";
            }
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            if (await db.DependencyBotSettings.FirstOrDefaultAsync(s => s.RepositoryId == repositoryId, ct) is { } settings)
            {
                settings.LastRunAt = DateTime.UtcNow; settings.LastStatus = status; settings.LastMessage = Trim(message, 1000);
                settings.NextRunAt = NextRun(settings.Schedule, DateTime.UtcNow);
                await db.SaveChangesAsync(ct);
            }
            return true;
        }
        finally { gate.Release(); }
    }

    // ------------------------------------------------------------------ check

    /// <summary>Checks the repository now and stores the table. Throws <see cref="InvalidOperationException"/> if a check is already running.</summary>
    public async Task<DependencyReport> CheckAsync(int repositoryId, CancellationToken ct)
    {
        var gate = locks.GetOrAdd(repositoryId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) throw new InvalidOperationException("A dependency check is already running for this repository.");
        try { return await CheckCoreAsync(repositoryId, ct); }
        finally { gate.Release(); }
    }

    private async Task<DependencyReport> CheckCoreAsync(int repositoryId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var repo = await db.Repositories.Include(r => r.Project).FirstOrDefaultAsync(r => r.Id == repositoryId, ct) ?? throw new InvalidOperationException("The repository no longer exists.");
        var settings = await db.DependencyBotSettings.FirstOrDefaultAsync(s => s.RepositoryId == repositoryId, ct);
        if (settings == null)
        {
            db.DependencyBotSettings.Add(settings = new DependencyBotSettings { RepositoryId = repositoryId });
            await db.SaveChangesAsync(ct);
        }
        var o = options.CurrentValue;
        var path = storage.PathFor(repo.Project.Key, repo.Slug);
        var branch = string.IsNullOrWhiteSpace(settings.TargetBranch) ? repo.DefaultBranch : settings.TargetBranch!;
        var report = new DependencyReport { Branch = branch };

        var sha = await Task.Run(() => GitOps.Exists(path) ? GitOps.ResolveSha(path, branch) : null, ct);
        if (sha == null)
        {
            report.Warnings.Add($"Branch '{branch}' doesn't exist (yet), so there is nothing to check.");
            await StoreAsync(db, settings, report, ct);
            return report;
        }
        report.HeadSha = sha;

        var enabled = settings.Ecosystems.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ignore = IgnoreRule.ParseAll(settings.IgnoreList);
        var (dependencies, manifests) = await Task.Run(() => ReadDependencies(path, sha, enabled, o, report.Warnings), ct);
        report.ManifestsRead = manifests;

        // Registry lookups: one per distinct package, sequential, throttled; a registry that fails hard is skipped for the rest of the run.
        var infos = new Dictionary<string, PackageInfo>(StringComparer.Ordinal);
        var down = new HashSet<string>();
        var distinct = dependencies.Select(d => (d.Ecosystem, d.Name)).Distinct().ToList();
        if (distinct.Count > o.MaxPackagesPerRun)
        {
            report.Warnings.Add($"{distinct.Count - o.MaxPackagesPerRun} package(s) were not checked: a run looks at {o.MaxPackagesPerRun} packages (DependencyBot:MaxPackagesPerRun).");
            distinct = distinct.Take(o.MaxPackagesPerRun).ToList();
        }
        foreach (var (eco, name) in distinct)
        {
            if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
            if (down.Contains(eco)) continue;
            try { infos[Key(eco, name)] = await registries.GetAsync(eco, name, ct); report.PackagesChecked++; }
            catch (RegistryException ex)
            {
                report.Warnings.Add($"{Ecosystems.Label(eco)} {name}: {ex.Message}");
                if (ex.FatalForRegistry && down.Add(eco)) report.Warnings.Add($"The {Ecosystems.Label(eco)} registry isn't answering; its remaining packages were skipped.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Registry lookup of {Eco} {Name} failed", eco, name);
                report.Warnings.Add($"{Ecosystems.Label(eco)} {name}: the lookup failed unexpectedly.");
            }
        }

        // Pull requests the bot already opened.
        var botPrs = await db.DependencyBotPullRequests.AsNoTracking().Where(p => p.RepositoryId == repositoryId).ToListAsync(ct);
        var prIds = botPrs.Select(p => p.PullRequestId).Distinct().ToList();
        var prStates = await db.PullRequests.AsNoTracking().Where(p => prIds.Contains(p.Id)).Select(p => new { p.Id, p.Number, p.State }).ToDictionaryAsync(p => p.Id, ct);

        var security = await SecurityNotesAsync(db, repo, ct);

        foreach (var dep in dependencies)
        {
            if (!infos.TryGetValue(Key(dep.Ecosystem, dep.Name), out var info) || DepVersion.Parse(dep.Current) is not { } current) continue;
            var latest = UpdateChooser.Latest(info.Versions, current.IsPrerelease);
            if (latest == null || latest <= current) continue;
            var proposed = UpdateChooser.Choose(current, info.Versions, settings.Strategy, dep.Name, ignore);
            var item = new OutdatedItem
            {
                Ecosystem = dep.Ecosystem, Name = dep.Name, Manifest = dep.Manifest, Group = dep.Group, Current = dep.Current,
                Latest = latest.Text, Proposed = proposed?.Text, Kind = (proposed ?? latest).BumpFrom(current),
                ReleaseUrl = info.ReleaseUrl(proposed ?? latest), Status = ItemStatus.Update,
            };
            if (proposed == null)
            {
                item.Status = ItemStatus.HeldBack;
                item.Note = $"The {settings.Strategy.ToString().ToLowerInvariant()} strategy or the ignore list holds back {latest.Text}.";
            }
            else
            {
                var mine = botPrs.Where(p => p.Ecosystem == dep.Ecosystem && string.Equals(p.Package, dep.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                var open = mine.FirstOrDefault(p => prStates.TryGetValue(p.PullRequestId, out var s) && s.State == PullRequestState.Open);
                var closed = mine.FirstOrDefault(p => p.ToVersion == proposed.Text && prStates.TryGetValue(p.PullRequestId, out var s) && s.State == PullRequestState.Declined);
                if (open != null) { item.Status = ItemStatus.PullRequestOpen; item.PullRequestNumber = prStates[open.PullRequestId].Number; item.Note = $"Pull request #{item.PullRequestNumber} is open."; }
                else if (closed != null) { item.Status = ItemStatus.Closed; item.PullRequestNumber = prStates[closed.PullRequestId].Number; item.Note = $"Pull request #{item.PullRequestNumber} for this version was closed without merging."; }
            }
            if (security.TryGetValue(dep.Name.ToLowerInvariant(), out var findings))
                item.SecurityNote = SecurityNote(findings, dep.Current, proposed);
            report.Items.Add(item);
        }
        report.Items = report.Items.OrderBy(i => i.Status == ItemStatus.Update ? 0 : 1).ThenBy(i => i.Ecosystem).ThenBy(i => i.Manifest, StringComparer.Ordinal)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        await StoreAsync(db, settings, report, ct);
        return report;
    }

    private static string Key(string ecosystem, string name) => ecosystem + "\n" + name.ToLowerInvariant();

    private static async Task StoreAsync(AppDbContext db, DependencyBotSettings settings, DependencyReport report, CancellationToken ct)
    {
        report.CheckedAt = DateTime.UtcNow;
        settings.LastReportJson = JsonSerializer.Serialize(report, Json);
        await db.SaveChangesAsync(ct);
    }

    private (List<Dependency> Dependencies, int Manifests) ReadDependencies(string path, string sha, HashSet<string> enabled, DependencyBotOptions o, List<string> warnings)
    {
        var files = GitOps.ListFiles(path, sha) ?? [];
        var result = new List<Dependency>();
        var read = 0;
        foreach (var (file, size) in files)
        {
            if (Ecosystems.OfManifest(file) is not { } eco || !enabled.Contains(eco)) continue;
            if (file.Split('/').SkipLast(1).Any(SkippedDirectories.Contains)) continue;
            if (read >= o.MaxManifests) { warnings.Add($"Only the first {o.MaxManifests} manifests were read (DependencyBot:MaxManifests)."); break; }
            if (size > o.MaxManifestBytes) { warnings.Add($"{file} is larger than {o.MaxManifestBytes} bytes and was skipped."); continue; }
            var bytes = GitOps.ReadFileBytes(path, sha, file, o.MaxManifestBytes);
            if (bytes == null) continue;
            read++;
            result.AddRange(ManifestParser.Parse(eco, file, ManifestParser.Decode(bytes)));
        }
        return (result, read);
    }

    // ------------------------------------------------------------------ security (Night Owl)

    private static async Task<Dictionary<string, List<SecurityFinding>>> SecurityNotesAsync(AppDbContext db, Repository repo, CancellationToken ct)
    {
        var result = new Dictionary<string, List<SecurityFinding>>(StringComparer.Ordinal);
        try
        {
            var snapshot = await SecuritySnapshots.ForBranchAsync(db, repo.Id, repo.DefaultBranch, ct: ct);
            if (snapshot == null) return result;
            foreach (var f in snapshot.Open.Where(v => v.Finding.Category == SecurityCategory.Dependency && v.Finding.PackageName != null).Select(v => v.Finding))
            {
                var key = f.PackageName!.ToLowerInvariant();
                if (!result.TryGetValue(key, out var list)) result[key] = list = [];
                list.Add(f);
            }
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException) { /* the flag is a nicety */ }
        return result;
    }

    private static string? SecurityNote(List<SecurityFinding> findings, string current, DepVersion? proposed)
    {
        var relevant = findings.Where(f => string.IsNullOrEmpty(f.PackageVersion) || f.PackageVersion == current).ToList();
        if (relevant.Count == 0) return null;
        var ids = string.Join(", ", relevant.Select(f => f.RuleId).Distinct().Take(5));
        var fixedBy = proposed != null && relevant.All(f => (f.FixedVersions ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(DepVersion.Parse).Any(v => v != null && v <= proposed));
        return $"Night Owl reports {relevant.Count} open advisory(ies) for {current} ({ids})" + (proposed == null ? "." : fixedBy ? $"; {proposed.Text} fixes them." : "; check whether the new version fixes them.");
    }

    // ------------------------------------------------------------------ open pull requests

    /// <summary>
    /// Opens pull requests for the items of <paramref name="report"/> with status <c>update</c> (only those whose <see cref="OutdatedItem.Key"/>
    /// is in <paramref name="keys"/>, when given), within the open-pull-request limit. <paramref name="actorUserId"/> is the person who clicked
    /// (null: the schedule); the pull request author is always the bot.
    /// </summary>
    public async Task<ApplyResult> ApplyAsync(int repositoryId, IReadOnlyCollection<string>? keys, int? actorUserId, CancellationToken ct)
    {
        var gate = locks.GetOrAdd(repositoryId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) throw new InvalidOperationException("A dependency check is running for this repository; try again in a moment.");
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var settings = await db.DependencyBotSettings.AsNoTracking().FirstOrDefaultAsync(s => s.RepositoryId == repositoryId, ct);
            var report = ReadReport(settings?.LastReportJson) ?? throw new InvalidOperationException("Run \"Check now\" first.");
            return await ApplyCoreAsync(repositoryId, report, keys, actorUserId, ct);
        }
        finally { gate.Release(); }
    }

    private async Task<ApplyResult> ApplyCoreAsync(int repositoryId, DependencyReport report, IReadOnlyCollection<string>? keys, int? actorUserId, CancellationToken ct)
    {
        var result = new ApplyResult();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var repo = await db.Repositories.Include(r => r.Project).FirstOrDefaultAsync(r => r.Id == repositoryId, ct) ?? throw new InvalidOperationException("The repository no longer exists.");
        var settings = await db.DependencyBotSettings.AsNoTracking().FirstOrDefaultAsync(s => s.RepositoryId == repositoryId, ct) ?? throw new InvalidOperationException("The dependency bot isn't set up for this repository.");
        if (repo.IsArchived) { result.Warnings.Add("The repository is archived."); return result; }
        var botId = await DependencyBotAccount.EnsureAsync(db, ct);

        var wanted = report.Items.Where(i => i.Status == ItemStatus.Update && i.Proposed != null && (keys == null || keys.Contains(i.Key))).ToList();
        if (wanted.Count == 0) return result;

        // The open-pull-request limit counts the bot's open pull requests.
        var botPrIds = await db.DependencyBotPullRequests.Where(p => p.RepositoryId == repositoryId).Select(p => p.PullRequestId).Distinct().ToListAsync(ct);
        var openCount = await db.PullRequests.CountAsync(p => botPrIds.Contains(p.Id) && p.State == PullRequestState.Open, ct);
        var room = Math.Max(0, settings.MaxOpenPullRequests - openCount);
        if (room == 0) { result.Warnings.Add($"{openCount} bot pull request(s) are open already (the limit is {settings.MaxOpenPullRequests})."); return result; }

        var branch = string.IsNullOrWhiteSpace(settings.TargetBranch) ? repo.DefaultBranch : settings.TargetBranch!;
        var path = storage.PathFor(repo.Project.Key, repo.Slug);
        var tip = await Task.Run(() => GitOps.ResolveSha(path, branch), ct) ?? throw new InvalidOperationException($"Branch '{branch}' doesn't exist.");
        var o = options.CurrentValue;

        // Fresh manifest state at the tip: the bot only edits what is still there.
        var allPaths = new HashSet<string>(((await Task.Run(() => GitOps.ListFiles(path, tip), ct)) ?? []).Select(f => f.Path), StringComparer.Ordinal);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var parsed = new Dictionary<string, List<Dependency>>(StringComparer.Ordinal);
        async Task<List<Dependency>> ParsedAsync(string manifest, string eco)
        {
            if (parsed.TryGetValue(manifest, out var cached)) return cached;
            var bytes = await Task.Run(() => GitOps.ReadFileBytes(path, tip, manifest, o.MaxManifestBytes), ct);
            if (bytes == null) return parsed[manifest] = [];
            texts[manifest] = ManifestParser.Decode(bytes);
            return parsed[manifest] = ManifestParser.Parse(eco, manifest, texts[manifest]);
        }

        var reviewerIds = await ResolveReviewersAsync(db, repo, settings.Reviewers, result.Warnings, ct);
        var groups = settings.Grouped
            ? new List<List<OutdatedItem>> { wanted }
            : wanted.GroupBy(i => (i.Ecosystem, i.Name, i.Proposed)).Select(g => g.ToList()).ToList();

        foreach (var group in groups)
        {
            if (room <= 0) { result.Warnings.Add("The open pull request limit was reached; the rest wait for the next run."); break; }
            ct.ThrowIfCancellationRequested();
            try
            {
                var edits = new List<(OutdatedItem Item, Dependency Dep)>();
                foreach (var item in group)
                {
                    var dep = (await ParsedAsync(item.Manifest, item.Ecosystem)).FirstOrDefault(d => d.Name == item.Name && d.Current == item.Current);
                    if (dep == null) result.Warnings.Add($"{item.Manifest}: {item.Name} {item.Current} is no longer there (the manifest changed); run \"Check now\" again.");
                    else edits.Add((item, dep));
                }
                if (edits.Count == 0) continue;

                var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var byManifest in edits.GroupBy(e => e.Dep.Manifest))
                {
                    var text = texts[byManifest.Key];
                    foreach (var e in byManifest.OrderByDescending(e => e.Dep.Start)) text = ManifestParser.Replace(text, e.Dep, e.Item.Proposed!);
                    files[byManifest.Key] = ManifestParser.Encode(text);
                }

                var title = group.Count == 1 || group.Select(i => (i.Ecosystem, i.Name)).Distinct().Count() == 1
                    ? $"Bump {group[0].Name} from {group[0].Current} to {group[0].Proposed}"
                    : $"Update {group.Select(i => (i.Ecosystem, i.Name)).Distinct().Count()} dependencies";
                var branchName = settings.Grouped ? GroupBranch(path, tip) : SingleBranch(group[0].Ecosystem, group[0].Name, group[0].Proposed!);
                var body = Body(group, settings, allPaths, branch);
                var commitMessage = title + "\n\n" + string.Join("\n", edits.Select(e => $"- {e.Item.Name} {e.Item.Current} -> {e.Item.Proposed} ({e.Dep.Manifest})"));
                var author = new GitIdentity(DependencyBotAccount.DisplayName, DependencyBotAccount.Email);
                try { await Task.Run(() => GitOps.CommitFileBytesOnNewBranch(path, branchName, tip, files, commitMessage, author), ct); }
                catch (InvalidOperationException ex) { result.Warnings.Add($"{title}: {ex.Message}"); continue; }

                int prId, prNumber;
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    scope.ServiceProvider.GetRequiredService<ProjectAccess>().ActAsSystem(botId);
                    var pr = await scope.ServiceProvider.GetRequiredService<PullRequestService>().CreateAsync(repo, title, body, branchName, branch, botId, reviewerIds);
                    (prId, prNumber) = (pr.Id, pr.Number);
                    await scope.ServiceProvider.GetRequiredService<AuditLog>().RecordAsync("repository.dependency_pr_opened", "pull_request", pr.Id, repo.ProjectId,
                        new
                        {
                            repository = repo.Slug, pullRequest = pr.Number, branch = branchName, target = branch,
                            packages = group.Select(i => new { i.Ecosystem, i.Name, from = i.Current, to = i.Proposed }).Distinct().ToList(),
                            trigger = actorUserId == null ? "schedule" : "manual",
                        }, actorUserId ?? botId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // No pull request, so don't leave the branch behind.
                    try { await Task.Run(() => GitOps.DeleteBranch(path, branchName, repo.DefaultBranch), CancellationToken.None); } catch (Exception) { /* best effort */ }
                    result.Warnings.Add($"{title}: couldn't open the pull request ({(ex is InvalidOperationException ? ex.Message : "unexpected error, see the server log")}).");
                    if (ex is not InvalidOperationException) log.LogWarning(ex, "Opening the dependency pull request failed");
                    continue;
                }

                foreach (var e in edits.Select(e => e.Item).DistinctBy(i => (i.Ecosystem, i.Name)))
                    db.DependencyBotPullRequests.Add(new DependencyBotPullRequest
                    {
                        RepositoryId = repositoryId, Ecosystem = e.Ecosystem, Package = e.Name, FromVersion = Trim(e.Current, 100), ToVersion = Trim(e.Proposed!, 100),
                        Branch = branchName, PullRequestId = prId,
                    });
                await db.SaveChangesAsync(ct);
                result.PullRequests.Add(prNumber);
                room--;
            }
            catch (Exception ex) when (ex is InvalidOperationException)
            {
                result.Warnings.Add(ex.Message);
            }
        }
        return result;
    }

    private static async Task<List<int>> ResolveReviewersAsync(AppDbContext db, Repository repo, string? reviewers, List<string> warnings, CancellationToken ct)
    {
        var ids = new List<int>();
        foreach (var name in (reviewers ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = name.TrimStart('@').ToUpperInvariant();
            var id = await db.Users.Where(u => u.NormalizedUserName == normalized
                    && db.ProjectMembers.Any(m => m.ProjectId == repo.ProjectId && m.UserId == u.Id && m.Role >= ProjectRole.Developer))
                .Select(u => (int?)u.Id).FirstOrDefaultAsync(ct);
            if (id is { } uid) ids.Add(uid); else warnings.Add($"Reviewer '{name}' isn't a project member who can review, so wasn't added.");
        }
        return ids;
    }

    // ------------------------------------------------------------------ names and text

    public static string SingleBranch(string ecosystem, string name, string version) =>
        $"bytebench/deps/{ecosystem}-{Safe(name)}-{Safe(version)}";

    private string GroupBranch(string repoPath, string tip)
    {
        var baseName = $"bytebench/deps/group-{DateTime.UtcNow:yyyyMMdd}-{tip[..7]}";
        var name = baseName;
        for (var i = 2; GitOps.BranchExists(repoPath, name) && i < 20; i++) name = $"{baseName}-{i}";
        return name;
    }

    private static string Safe(string text)
    {
        var s = Regex.Replace(text.Replace("@", "").Replace('/', '-'), @"[^A-Za-z0-9._-]+", "-").Replace("..", ".").Trim('-', '.');
        if (s.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) s += "-";
        return s.Length == 0 ? "pkg" : s.Length > 80 ? s[..80] : s;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];

    private static string Body(List<OutdatedItem> group, DependencyBotSettings settings, ISet<string> allPaths, string target)
    {
        var sb = new StringBuilder();
        var names = group.Select(i => (i.Ecosystem, i.Name)).Distinct().ToList();
        if (names.Count == 1)
            sb.Append($"Bumps **{group[0].Name}** from `{group[0].Current}` to `{group[0].Proposed}` ({group[0].Kind} update).\n\n");
        else sb.Append($"Updates {names.Count} dependencies.\n\n");
        sb.Append("| Package | Ecosystem | Manifest | From | To | Update |\n|---|---|---|---|---|---|\n");
        foreach (var i in group)
            sb.Append($"| {Cell(i.Name)} | {Ecosystems.Label(i.Ecosystem)} | `{i.Manifest}` | `{i.Current}` | `{i.Proposed}` | {i.Kind} |\n");
        var links = group.Where(i => i.ReleaseUrl != null).DistinctBy(i => (i.Name, i.Proposed)).ToList();
        if (links.Count > 0)
        {
            sb.Append("\n**Release notes**\n\n");
            foreach (var i in links) sb.Append($"- [{Cell(i.Name)} {i.Proposed}]({i.ReleaseUrl})\n");
        }
        var advisories = group.Where(i => i.SecurityNote != null).DistinctBy(i => (i.Name, i.Current)).ToList();
        if (advisories.Count > 0)
        {
            sb.Append("\n**Security**\n\n");
            foreach (var i in advisories) sb.Append($"- {Cell(i.Name)}: {i.SecurityNote}\n");
        }
        var notes = group.Select(i => Ecosystems.LockFileNote(i.Ecosystem, i.Manifest, allPaths)).Where(n => n != null).Distinct().ToList();
        if (notes.Count > 0)
        {
            sb.Append("\n**Lockfiles are not updated**\n\nThe bot only edits the manifest text; it never runs a package manager. Regenerate: ");
            sb.Append(string.Join(", ", notes.Select(n => $"`{n}`")));
            sb.Append(" (for example `npm install`, `go mod tidy`) on this branch before merging.\n");
        }
        if (!string.IsNullOrWhiteSpace(settings.Labels)) sb.Append($"\nLabels: {string.Join(", ", settings.Labels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => $"`{l}`"))}\n");
        sb.Append($"\n---\nOpened by the ByteBench dependency bot against `{target}`. Close this pull request without merging to skip this version; the bot won't propose it again.");
        return sb.ToString();
    }

    private static string Cell(string s) => s.Replace("|", "\\|").Replace("\n", " ");
}
