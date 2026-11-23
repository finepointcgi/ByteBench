using System.Globalization;
using System.Net;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Insights;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

public sealed class InsightsSmallApp : TestApp
{
    protected override void AddSettings(IDictionary<string, string?> settings) { settings["Insights:MaxCommits"] = "3"; }
}

/// <summary>A throw-away history with known authors, dates, lockfiles, vendored files and languages, pushed into a ByteBench repository.</summary>
public sealed class InsightsHistory : IDisposable
{
    public GitCli Git { get; } = new();
    public Project Project { get; private set; } = null!;
    public Repository Repo { get; private set; } = null!;
    public string BarePath { get; private set; } = "";
    public string Work { get; private set; } = "";
    public DateTime Today { get; } = DateTime.UtcNow.Date;
    public string BobDay => Today.AddDays(-3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public void Dispose() => Git.Dispose();

    private static string Iso(DateTime d) => d.ToString("yyyy-MM-dd'T'HH:mm:ss'+0000'", CultureInfo.InvariantCulture);

    public async Task CommitAsync(string name, string email, DateTime when, string message)
    {
        await Git.OkAsync(Work, "add", "-A");
        await Git.OkAsync(Work, "-c", "user.name=" + name, "-c", "user.email=" + email, "commit", "-m", message, "--date=" + Iso(when));
    }

    private static string Lines(string prefix, int n) => string.Concat(Enumerable.Range(1, n).Select(i => $"{prefix}{i}\n"));

    public static async Task<InsightsHistory> BuildAsync(TestApp app, string owner = "carol", params (string, ProjectRole)[] members)
    {
        var h = new InsightsHistory();
        h.Project = await app.CreateProjectAsync(owner, ProjectVisibility.Private, members);
        h.Repo = await app.CreateRepoAsync(h.Project.Id, owner);
        h.BarePath = app.Services.GetRequiredService<RepoStorage>().PathFor(h.Project.Key, h.Repo.Slug);
        h.Work = h.Git.Dir("work");
        Directory.CreateDirectory(h.Work);
        await h.Git.OkAsync(h.Work, "init", "-b", "main");
        var t = h.Today;

        // c1: Alice, 40 days ago 09:00. A.cs 10 lines + README 2 lines count; the 100-line lockfile doesn't.
        Directory.CreateDirectory(Path.Combine(h.Work, "src"));
        await File.WriteAllTextAsync(Path.Combine(h.Work, "src", "A.cs"), Lines("line", 10));
        await File.WriteAllTextAsync(Path.Combine(h.Work, "README.md"), "# Title\ntext\n");
        await File.WriteAllTextAsync(Path.Combine(h.Work, "package-lock.json"), Lines("lock", 100));
        await h.CommitAsync("Alice", "alice@example.com", t.AddDays(-40).AddHours(9), "Add A");
        // c2: the same person under another address, 10 days ago 14:00: +5
        await File.AppendAllTextAsync(Path.Combine(h.Work, "src", "A.cs"), Lines("more", 5));
        await h.CommitAsync("Alice", "alicia@elsewhere.example", t.AddDays(-10).AddHours(14), "Grow A");
        // c3: Bob, 3 days ago 14:30: +4 python, -3 in A.cs
        await File.WriteAllTextAsync(Path.Combine(h.Work, "app.py"), Lines("py", 4));
        await File.WriteAllTextAsync(Path.Combine(h.Work, "src", "A.cs"), Lines("line", 10) + Lines("more", 2));
        await h.CommitAsync("Bob", "bob@example.com", t.AddDays(-3).AddHours(14).AddMinutes(30), "Add app");
        // c4: Bob, 3 days ago 15:10: a vendored file (not counted), an unknown extension (counted as lines, not as a language)
        Directory.CreateDirectory(Path.Combine(h.Work, "node_modules", "x"));
        await File.WriteAllTextAsync(Path.Combine(h.Work, "node_modules", "x", "index.js"), Lines("v", 50));
        await File.WriteAllTextAsync(Path.Combine(h.Work, "notes.xyz"), "a\nb\n");
        await h.CommitAsync("Bob", "bob@example.com", t.AddDays(-3).AddHours(15).AddMinutes(10), "Vendor and notes");

        await h.Git.OkAsync(h.Work, "push", "--force", h.BarePath, "main:main");
        return h;
    }

    public async Task PushMoreAsync()
    {
        await File.AppendAllTextAsync(Path.Combine(Work, "app.py"), "py5\n");
        await CommitAsync("Carla", "carla@example.com", DateTime.UtcNow.AddMinutes(-5), "One more");
        await Git.OkAsync(Work, "push", "--force", BarePath, "main:main");
    }
}

public class InsightsTests(TestApp app) : IClassFixture<TestApp>
{
    private InsightsService Service => app.Services.GetRequiredService<InsightsService>();
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private async Task<Repository> RepoAsync(InsightsHistory h, string user)
    {
        await using var s = app.Scope(app.UserId(user));
        return (await s.Get<RepositoryService>().GetRepositoryAsync(h.Project.Key, h.Repo.Slug))!;
    }

    [Fact]
    public async Task Computes_contributors_languages_activity_and_busy_times_from_a_real_history()
    {
        using var h = await InsightsHistory.BuildAsync(app);
        var view = await Service.GetAsync(await RepoAsync(h, "carol"), "all", Wait);

        Assert.Equal(InsightsStatus.Ready, view.Status);
        Assert.Equal(4, view.CommitsScanned);
        Assert.False(view.Truncated);
        Assert.Equal("main", view.Branch);

        // Alice's two addresses merge (same e-mail would too); the e-mail that matches a ByteBench user links to it.
        Assert.Equal(["Alice", "Bob"], view.Contributors.Select(c => c.Name).OrderBy(n => n));
        var alice = view.Contributors.Single(c => c.Name == "Alice");
        Assert.Equal(2, alice.Commits);
        Assert.Equal(17, alice.Additions); // 10 + 2 + 5; the lockfile's 100 lines are left out
        Assert.Equal(0, alice.Deletions);
        Assert.Equal("alice", alice.UserName);
        var bob = view.Contributors.Single(c => c.Name == "Bob");
        Assert.Equal((2, 6, 3), (bob.Commits, bob.Additions, bob.Deletions)); // +4 python, +2 notes.xyz, -3 in A.cs; node_modules isn't counted
        Assert.Equal("bob", bob.UserName);
        Assert.Equal(["Alice", "Bob"], view.Contributors.Select(c => c.Name).OrderBy(n => n));
        Assert.True(view.Contributors[0].Commits >= view.Contributors[1].Commits);

        // The range limits contributors: 30 days leaves one of Alice's commits.
        var month = await Service.GetAsync(await RepoAsync(h, "carol"), "30d", Wait);
        Assert.Equal(1, month.Contributors.Single(c => c.Name == "Alice").Commits);
        Assert.Equal(5, month.Contributors.Single(c => c.Name == "Alice").Additions);
        var week = await Service.GetAsync(await RepoAsync(h, "carol"), "7d", Wait);
        Assert.Equal(["Bob"], week.Contributors.Select(c => c.Name));

        // Languages: C#, Markdown and Python; not the vendored JS, the lockfile's JSON or the unknown extension.
        Assert.Equal(["C#", "Markdown", "Python"], view.Languages.Select(l => l.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(1, view.Languages.Single(l => l.Name == "C#").Files);
        Assert.Equal(100, Math.Round(view.Languages.Sum(l => l.Percent)), 0);
        Assert.All(view.Languages, l => Assert.True(l.Bytes > 0));

        // Weeks and days: 52 weeks, every commit inside them; the heatmap runs to today.
        Assert.Equal(52, view.CommitActivity.Count);
        Assert.Equal(4, view.CommitActivity.Sum(w => w.Commits));
        Assert.Equal(52, view.CodeFrequency.Count);
        Assert.Equal(17 + 6, view.CodeFrequency.Sum(w => w.Additions));
        Assert.Equal(3, view.CodeFrequency.Sum(w => w.Deletions));
        Assert.Equal(h.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), view.Heatmap[^1].Date);
        Assert.Equal(2, view.Heatmap.Single(d => d.Date == h.BobDay).Commits);
        Assert.Equal(DayOfWeek.Monday, DateTime.ParseExact(view.Heatmap[0].Date, "yyyy-MM-dd", CultureInfo.InvariantCulture).DayOfWeek);

        // Busiest hours and weekdays (UTC).
        Assert.Equal((1, 2, 1), (view.Hours[9], view.Hours[14], view.Hours[15]));
        Assert.Equal(4, view.Hours.Sum());
        Assert.Equal(4, view.Weekdays.Sum());
        Assert.True(view.Weekdays[(int)h.Today.AddDays(-3).DayOfWeek] >= 2); // both of Bob's commits
    }

    [Fact]
    public async Task Pull_request_stats_count_opened_merged_and_the_median_time_to_merge()
    {
        using var h = await InsightsHistory.BuildAsync(app);
        var now = DateTime.UtcNow;
        await using (var db = app.Db())
        {
            var alice = app.UserId("alice");
            PullRequest Pr(int n, PullRequestState state, double hoursOpen) => new()
            {
                RepositoryId = h.Repo.Id, Number = n, Title = "PR " + n, SourceBranch = "f" + n, TargetBranch = "main", AuthorId = alice, State = state,
                CreatedAt = now.AddHours(-hoursOpen), ClosedAt = state == PullRequestState.Open ? null : now,
            };
            db.PullRequests.AddRange(Pr(1, PullRequestState.Merged, 10), Pr(2, PullRequestState.Merged, 20), Pr(3, PullRequestState.Open, 1), Pr(4, PullRequestState.Declined, 5));
            await db.SaveChangesAsync();
        }
        var view = await Service.GetAsync(await RepoAsync(h, "carol"), "1y", Wait);
        Assert.Equal(12, view.PullRequests.Count);
        Assert.Equal(4, view.PullRequests.Sum(m => m.Opened));
        var thisMonth = view.PullRequests.Single(m => m.Month == now.ToString("yyyy-MM", CultureInfo.InvariantCulture));
        Assert.Equal(2, thisMonth.Merged); // declined and open ones don't count
        Assert.Equal(15.0, thisMonth.MedianHoursToMerge);
    }

    [Fact]
    public async Task Is_cached_per_head_and_recomputed_after_a_push_one_computation_at_a_time()
    {
        using var h = await InsightsHistory.BuildAsync(app);
        var repo = await RepoAsync(h, "carol");

        // Many viewers at once share one computation (the unique row per repository would throw otherwise).
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Service.GetAsync(repo, "all", Wait)));
        Assert.All(results, r => Assert.Equal(InsightsStatus.Ready, r.Status));
        Assert.Single(results.Select(r => r.ComputedAt).Distinct());
        await using (var db = app.Db()) Assert.Equal(1, await db.RepositoryInsights.CountAsync(i => i.RepositoryId == h.Repo.Id));

        // Same head: served from the cache.
        var again = await Service.GetAsync(repo, "all");
        Assert.Equal(InsightsStatus.Ready, again.Status);
        Assert.Equal(results[0].ComputedAt, again.ComputedAt);

        // A push moves the head: the old numbers are replaced.
        await h.PushMoreAsync();
        var fresh = await Service.GetAsync(repo, "all", Wait);
        Assert.Equal(InsightsStatus.Ready, fresh.Status);
        Assert.Equal(5, fresh.CommitsScanned);
        Assert.NotEqual(results[0].HeadSha, fresh.HeadSha);
        Assert.Contains(fresh.Contributors, c => c.Name == "Carla");
        await using (var db = app.Db()) Assert.Equal(1, await db.RepositoryInsights.CountAsync(i => i.RepositoryId == h.Repo.Id));
    }

    [Fact]
    public async Task Without_waiting_the_first_view_says_computing_and_never_blocks()
    {
        using var h = await InsightsHistory.BuildAsync(app);
        var repo = await RepoAsync(h, "carol");
        var first = await Service.GetAsync(repo, "1y"); // no wait: returns at once
        Assert.True(first.Status is InsightsStatus.Computing or InsightsStatus.Ready);
        if (first.Status == InsightsStatus.Computing) Assert.Empty(first.Contributors);
        var done = await Service.GetAsync(repo, "1y", Wait);
        Assert.Equal(InsightsStatus.Ready, done.Status);
    }

    [Fact]
    public async Task Rest_endpoint_returns_the_same_data_for_viewers_and_hides_it_from_everyone_else()
    {
        using var h = await InsightsHistory.BuildAsync(app, "carol", ("bob", ProjectRole.Viewer));
        var url = $"/api/v1/projects/{h.Project.Key}/repos/{h.Repo.Slug}/insights";

        var bob = await app.ApiClientAsync("bob", TokenScopes.ReadApi);
        var ok = await bob.GetAsync(url + "?range=all");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using var doc = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("ready", root.GetProperty("status").GetString());
        Assert.Equal("all", root.GetProperty("range").GetString());
        Assert.Equal(4, root.GetProperty("commitsScanned").GetInt32());
        var contributors = root.GetProperty("contributors").EnumerateArray().ToList();
        Assert.Equal(2, contributors.Count);
        var alice = contributors.Single(c => c.GetProperty("name").GetString() == "Alice");
        Assert.Equal("alice", alice.GetProperty("userName").GetString());
        Assert.Equal(17, alice.GetProperty("additions").GetInt32());
        Assert.Equal(52, root.GetProperty("commitActivity").GetArrayLength());
        Assert.Equal(52, root.GetProperty("codeFrequency").GetArrayLength());
        Assert.Equal(24, root.GetProperty("hours").GetArrayLength());
        Assert.Equal(7, root.GetProperty("weekdays").GetArrayLength());
        Assert.Equal(12, root.GetProperty("pullRequests").GetArrayLength());
        Assert.Contains(root.GetProperty("languages").EnumerateArray(), l => l.GetProperty("name").GetString() == "Python");

        Assert.Equal(HttpStatusCode.BadRequest, (await bob.GetAsync(url + "?range=forever")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/v1/projects/{h.Project.Key}/repos/nope/insights")).StatusCode);

        // A user who can't see the project gets 404, not the numbers; no token at all gets 401.
        var dan = await app.ApiClientAsync("dan", TokenScopes.ReadApi);
        Assert.Equal(HttpStatusCode.NotFound, (await dan.GetAsync(url)).StatusCode);
        var anonymous = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Page_renders_server_side_svg_charts_for_a_viewer_and_not_for_an_outsider()
    {
        using var h = await InsightsHistory.BuildAsync(app, "carol", ("bob", ProjectRole.Viewer));
        await Service.GetAsync(await RepoAsync(h, "carol"), "all", Wait); // warm the cache so the first render has data
        var url = $"/p/{h.Project.Key}/repos/{h.Repo.Slug}/insights";

        var bob = await app.CookieClientAsync("bob");
        var page = await bob.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Insights", html);
        Assert.Contains("<svg", html);
        Assert.Contains("Contributors", html);
        Assert.Contains("/u/alice", html);
        Assert.Contains("Commit activity", html);
        Assert.DoesNotContain("<script>", html.Replace("<script src", ""), StringComparison.Ordinal); // charts need no inline script

        var dan = await app.CookieClientAsync("dan");
        var hidden = await (await dan.GetAsync(url)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Commit activity", hidden);
        Assert.DoesNotContain("/u/alice", hidden);
    }

    [Theory]
    [InlineData("package-lock.json", true)]
    [InlineData("web/yarn.lock", true)]
    [InlineData("Cargo.lock", true)]
    [InlineData("node_modules/a/b.js", true)]
    [InlineData("src/vendor/x.go", true)]
    [InlineData("app.min.js", true)]
    [InlineData("Foo.Designer.cs", true)]
    [InlineData("api.pb.go", true)]
    [InlineData("obj/Debug/x.cs", true)]
    [InlineData("src/App.cs", false)]
    [InlineData("README.md", false)]
    [InlineData("lib/vendorish.py", false)]
    public void Excludes_lockfiles_vendored_and_generated_paths(string path, bool excluded) => Assert.Equal(excluded, LanguageTable.IsExcluded(path));

    [Theory]
    [InlineData("a/B.cs", "C#")]
    [InlineData("x.TSX", "TypeScript")]
    [InlineData("Dockerfile", "Dockerfile")]
    [InlineData("deploy/Dockerfile", "Dockerfile")]
    [InlineData("notes.xyz", null)]
    [InlineData("LICENSE", null)]
    public void Maps_extensions_and_a_few_file_names_to_languages(string path, string? language) => Assert.Equal(language, LanguageTable.Of(path)?.Name);
}

public class InsightsLimitTests(InsightsSmallApp app) : IClassFixture<InsightsSmallApp>
{
    [Fact]
    public async Task A_history_longer_than_the_commit_limit_is_cut_and_says_so()
    {
        using var h = await InsightsHistory.BuildAsync(app);
        await using var s = app.Scope(app.UserId("carol"));
        var repo = (await s.Get<RepositoryService>().GetRepositoryAsync(h.Project.Key, h.Repo.Slug))!;
        var view = await app.Services.GetRequiredService<InsightsService>().GetAsync(repo, "all", TimeSpan.FromSeconds(60));
        Assert.Equal(InsightsStatus.Ready, view.Status);
        Assert.True(view.Truncated);
        Assert.Equal(3, view.CommitsScanned); // the newest three of four
        Assert.DoesNotContain(view.Contributors, c => c.Name == "Alice" && c.Commits == 2);
    }
}
