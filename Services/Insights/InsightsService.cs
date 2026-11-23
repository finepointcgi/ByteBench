using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Insights;

/// <summary>
/// Repository insights. Singleton. The numbers are cached in <see cref="RepositoryInsight"/> per repository and default-branch
/// head sha: a push moves the head, so the next view finds the cache stale and recomputes it in the background (one computation
/// at a time per repository, never on the caller's thread). Callers check access first (<c>RepositoryService.GetRepositoryAsync</c>
/// only returns repositories the user can view); this service does no permission checks.
/// </summary>
public sealed class InsightsService(IDbContextFactory<AppDbContext> dbFactory, IConfiguration config, RepoStorage storage,
    IOptionsMonitor<InsightsOptions> options, ILogger<InsightsService> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<int, Task> running = new();
    private readonly ConcurrentDictionary<int, (string Sha, string Error, DateTime At)> failures = new();
    private string? gitPath;
    private bool gitLocated;

    public static readonly string[] Ranges = ["7d", "30d", "90d", "1y", "all"];
    public static string NormalizeRange(string? range) => range != null && Ranges.Contains(range.ToLowerInvariant()) ? range.ToLowerInvariant() : "1y";

    /// <summary>The insights for <paramref name="repo"/> (needs <c>Project</c> loaded). Starts a computation if the cache is stale; with
    /// <paramref name="wait"/> it waits up to that long for it, else it returns what exists (maybe an older head) as <see cref="InsightsStatus.Computing"/>.</summary>
    public async Task<InsightsView> GetAsync(Repository repo, string? range, TimeSpan? wait = null, CancellationToken ct = default)
    {
        range = NormalizeRange(range);
        var path = storage.PathFor(repo.Project.Key, repo.Slug);
        var branch = repo.DefaultBranch;
        string? head;
        try { head = await Task.Run(() => GitOps.Exists(path) ? GitOps.ResolveSha(path, branch) : null, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { head = null; log.LogWarning(ex, "Insights: could not read {Repo}", path); }
        if (head == null) return new InsightsView { Status = InsightsStatus.Empty, Range = range, Branch = branch, Message = "This repository has no commits on its default branch yet." };

        var row = await LoadAsync(repo.Id, ct);
        if (row != null && row.HeadSha == head) return await BuildAsync(repo, row, range, InsightsStatus.Ready, false, null, ct);

        if (failures.TryGetValue(repo.Id, out var f) && f.Sha == head && DateTime.UtcNow - f.At < TimeSpan.FromMinutes(1))
            return await FailedAsync(repo, row, range, f.Error, ct);

        var task = Start(repo.Id, path, branch, head);
        if (wait is { } w && w > TimeSpan.Zero)
        {
            try { await task.WaitAsync(w, ct); } catch (TimeoutException) { }
            row = await LoadAsync(repo.Id, ct);
            if (row != null && row.HeadSha == head) return await BuildAsync(repo, row, range, InsightsStatus.Ready, false, null, ct);
            if (failures.TryGetValue(repo.Id, out f) && f.Sha == head) return await FailedAsync(repo, row, range, f.Error, ct);
        }
        if (row != null) return await BuildAsync(repo, row, range, InsightsStatus.Computing, true, "Computing fresh numbers; these are from an earlier head of the branch.", ct);
        return new InsightsView { Status = InsightsStatus.Computing, Range = range, Branch = branch, HeadSha = head, Message = "Computing the insights for the first time. This page refreshes by itself." };
    }

    private async Task<InsightsView> FailedAsync(Repository repo, RepositoryInsight? row, string range, string error, CancellationToken ct)
    {
        if (row != null) return await BuildAsync(repo, row, range, InsightsStatus.Failed, true, error, ct);
        return new InsightsView { Status = InsightsStatus.Failed, Range = range, Branch = repo.DefaultBranch, Message = error };
    }

    private Task Start(int repositoryId, string path, string branch, string head)
    {
        lock (running)
        {
            if (running.TryGetValue(repositoryId, out var existing)) return existing;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            running[repositoryId] = done.Task;
            _ = Task.Run(async () =>
            {
                try { await ComputeAndStoreAsync(repositoryId, path, branch, head); }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Computing insights of repository {Id} failed", repositoryId);
                    failures[repositoryId] = (head, ex is InvalidOperationException ? ex.Message : "The insights could not be computed.", DateTime.UtcNow);
                }
                finally
                {
                    lock (running) running.TryRemove(repositoryId, out Task? _);
                    done.SetResult();
                }
            });
            return done.Task;
        }
    }

    private async Task ComputeAndStoreAsync(int repositoryId, string path, string branch, string head)
    {
        if (!gitLocated) { gitPath = Git.GitHttpBackend.LocateGit(config); gitLocated = true; }
        if (gitPath == null) throw new InvalidOperationException("Insights are unavailable: git was not found on the server.");
        var result = await InsightsComputer.ComputeAsync(gitPath, path, head, options.CurrentValue, CancellationToken.None);
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.RepositoryInsights.FirstOrDefaultAsync(i => i.RepositoryId == repositoryId);
        if (row == null) db.RepositoryInsights.Add(row = new RepositoryInsight { RepositoryId = repositoryId });
        row.Branch = branch; row.HeadSha = head; row.ComputedAt = DateTime.UtcNow;
        row.CommitsScanned = result.Commits; row.Truncated = result.Truncated;
        row.DataJson = JsonSerializer.Serialize(result.Data, Json);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            // Another instance computed the same head at the same time: fine if its row is there, else it's a real failure.
            await using var check = await dbFactory.CreateDbContextAsync();
            if (!await check.RepositoryInsights.AsNoTracking().AnyAsync(i => i.RepositoryId == repositoryId && i.HeadSha == head)) throw;
        }
        failures.TryRemove(repositoryId, out _);
    }

    private async Task<RepositoryInsight?> LoadAsync(int repositoryId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.RepositoryInsights.AsNoTracking().FirstOrDefaultAsync(i => i.RepositoryId == repositoryId, ct);
    }

    private async Task<InsightsView> BuildAsync(Repository repo, RepositoryInsight row, string range, InsightsStatus status, bool stale, string? message, CancellationToken ct)
    {
        var data = JsonSerializer.Deserialize<InsightsData>(row.DataJson, Json) ?? new InsightsData();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var users = await db.Users.AsNoTracking().Where(u => u.Email != null).Select(u => new { u.Email, u.UserName, u.DisplayName }).ToListAsync(ct);
        var byEmail = users.GroupBy(u => u.Email!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => (g.First().UserName!, g.First().DisplayName), StringComparer.OrdinalIgnoreCase);
        var prs = await db.PullRequests.AsNoTracking().Where(p => p.RepositoryId == repo.Id)
            .Select(p => new { p.CreatedAt, p.ClosedAt, p.State }).ToListAsync(ct);
        var view = InsightsViewBuilder.Build(data, range, DateTime.UtcNow, byEmail,
            prs.Select(p => (p.CreatedAt, p.State == PullRequestState.Merged ? p.ClosedAt : null)).ToList());
        view.Status = status; view.Stale = stale; view.Message = message;
        view.Branch = row.Branch; view.HeadSha = row.HeadSha; view.ComputedAt = row.ComputedAt;
        view.CommitsScanned = row.CommitsScanned; view.Truncated = row.Truncated;
        return view;
    }
}

/// <summary>Turns the cached aggregate into the views for one range (pure: easy to test).</summary>
public static class InsightsViewBuilder
{
    public static DateTime StartOfWeek(DateTime day)
    {
        var d = day.Date;
        return d.AddDays(-(((int)d.DayOfWeek + 6) % 7)); // Monday
    }

    private static string Fmt(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static InsightsView Build(InsightsData data, string range, DateTime nowUtc, IReadOnlyDictionary<string, (string UserName, string DisplayName)> usersByEmail,
        IReadOnlyList<(DateTime Created, DateTime? Merged)> pullRequests)
    {
        var today = nowUtc.Date;
        var view = new InsightsView { Range = range, Languages = data.Languages, Hours = data.Hours, Weekdays = data.Weekdays };

        // Contributors in the range (days are UTC).
        DateTime? from = range switch { "7d" => today.AddDays(-6), "30d" => today.AddDays(-29), "90d" => today.AddDays(-89), "1y" => today.AddDays(-364), _ => null };
        var fromKey = from is { } f ? Fmt(f) : null;
        foreach (var c in data.Contributors)
        {
            int commits = 0, add = 0, del = 0;
            foreach (var (day, v) in c.Days)
            {
                if (fromKey != null && string.CompareOrdinal(day, fromKey) < 0) continue;
                commits += v[0]; add += v[1]; del += v[2];
            }
            if (commits == 0) continue;
            var user = c.Email.Length > 0 && usersByEmail.TryGetValue(c.Email, out var u) ? u : default;
            view.Contributors.Add(new ContributorView(c.Name, c.Email, commits, add, del, user.UserName, user.DisplayName));
        }
        view.Contributors = view.Contributors.OrderByDescending(c => c.Commits).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

        // Weeks and days: the last 52 weeks (the current one included).
        var firstWeek = StartOfWeek(today).AddDays(-7 * 51);
        var weeks = Enumerable.Range(0, 52).Select(i => firstWeek.AddDays(7 * i)).ToList();
        var commitsPerWeek = new Dictionary<string, int>(); var linesPerWeek = new Dictionary<string, (int, int)>();
        foreach (var (day, v) in data.Days)
        {
            if (!DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) || d < firstWeek) continue;
            var key = Fmt(StartOfWeek(d));
            commitsPerWeek[key] = commitsPerWeek.GetValueOrDefault(key) + v[0];
            var (a, r) = linesPerWeek.GetValueOrDefault(key);
            linesPerWeek[key] = (a + v[1], r + v[2]);
        }
        view.CommitActivity = weeks.Select(w => new WeekCommits(Fmt(w), commitsPerWeek.GetValueOrDefault(Fmt(w)))).ToList();
        view.CodeFrequency = weeks.Select(w => { var (a, r) = linesPerWeek.GetValueOrDefault(Fmt(w)); return new WeekLines(Fmt(w), a, r); }).ToList();
        for (var d = firstWeek; d <= today; d = d.AddDays(1))
            view.Heatmap.Add(new DayCommits(Fmt(d), data.Days.TryGetValue(Fmt(d), out var dv) ? dv[0] : 0));

        // Pull requests by month, last 12 months.
        var firstMonth = new DateTime(today.Year, today.Month, 1).AddMonths(-11);
        for (var m = firstMonth; m <= today; m = m.AddMonths(1))
        {
            var next = m.AddMonths(1);
            var opened = pullRequests.Count(p => p.Created >= m && p.Created < next);
            var mergedHours = pullRequests.Where(p => p.Merged is { } t && t >= m && t < next).Select(p => (p.Merged!.Value - p.Created).TotalHours).OrderBy(h => h).ToList();
            double? median = mergedHours.Count == 0 ? null
                : Math.Round(mergedHours.Count % 2 == 1 ? mergedHours[mergedHours.Count / 2] : (mergedHours[mergedHours.Count / 2 - 1] + mergedHours[mergedHours.Count / 2]) / 2, 1);
            view.PullRequests.Add(new MonthPullRequests(m.ToString("yyyy-MM", CultureInfo.InvariantCulture), opened, mergedHours.Count, median));
        }
        return view;
    }
}

public static class InsightsServiceCollectionExtensions
{
    /// <summary>Repository insights: the cache-backed computation service (config section <c>Insights</c>).</summary>
    public static IServiceCollection AddInsights(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<InsightsOptions>(config.GetSection("Insights"));
        services.AddSingleton<InsightsService>();
        return services;
    }
}
