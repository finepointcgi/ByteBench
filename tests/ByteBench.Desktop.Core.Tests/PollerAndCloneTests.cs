using System.Net;
using System.Text;

namespace ByteBench.Desktop.Core.Tests;

public class ServerApiTests
{
    private const string Good = """{"name":"ByteBench","version":"1.2.3","apiVersion":1,"capabilities":["notifications"],"workspaceBaseDomain":"example.com"}""";

    [Fact]
    public void Server_info_ok()
    {
        var c = ServerApi.EvaluateServerInfo(200, Good);
        Assert.Equal(CompatLevel.Ok, c.Level);
        Assert.True(c.SupportsNotifications);
        Assert.Equal("example.com", c.Info!.WorkspaceBaseDomain);
        Assert.Equal(["notifications"], c.Info.Capabilities);
    }

    [Fact] public void Newer_api_version_still_supports_notifications() =>
        Assert.Equal(CompatLevel.Newer, ServerApi.EvaluateServerInfo(200, Good.Replace("\"apiVersion\":1", "\"apiVersion\":2")).Level);

    [Fact] public void Unreachable_and_5xx_are_unavailable()
    {
        Assert.Equal(CompatLevel.Unavailable, ServerApi.EvaluateServerInfo(null, null).Level);
        Assert.Equal(CompatLevel.Unavailable, ServerApi.EvaluateServerInfo(503, "").Level);
    }

    [Fact] public void Old_bytebench_404_problem_document_is_legacy() =>
        Assert.Equal(CompatLevel.Legacy, ServerApi.EvaluateServerInfo(404, """{"title":"Not Found","status":404}""").Level);

    [Theory]
    [InlineData(404, "<html>nope</html>")]
    [InlineData(200, "<html>hello</html>")]
    [InlineData(200, """{"name":"Other","version":"1","apiVersion":1}""")]
    [InlineData(200, """{"name":"ByteBench","version":"1"}""")]
    [InlineData(302, "")]
    public void Anything_else_is_not_bytebench(int status, string body) =>
        Assert.Equal(CompatLevel.NotByteBench, ServerApi.EvaluateServerInfo(status, body).Level);

    [Fact]
    public void Notification_list_parsing_is_defensive()
    {
        var list = ServerApi.ParseNotificationList("""
        {"unreadCount":3,"items":[
          {"id":5,"kind":"Mention","title":"Hi","url":"/p/K/issues/1","isRead":false,"createdAt":"2026-01-01T00:00:00Z","actor":"Ann"},
          {"id":"bad","title":"x","url":"/x"},
          {"id":6,"title":7,"url":"/x"},
          "junk",
          {"id":7,"title":"No actor","url":"/y","isRead":true}
        ]}
        """);
        Assert.NotNull(list);
        Assert.Equal(3, list.UnreadCount);
        Assert.Equal([5, 7], list.Items.Select(i => i.Id));
        Assert.Equal("Ann", list.Items[0].Actor);
        Assert.True(list.Items[1].IsRead);
        Assert.Null(ServerApi.ParseNotificationList("[]"));
        Assert.Null(ServerApi.ParseNotificationList("""{"items":[]}"""));
        Assert.Null(ServerApi.ParseNotificationList("not json"));
        Assert.Equal(0, ServerApi.ParseNotificationList("""{"unreadCount":-4,"items":[]}""")!.UnreadCount);
    }

    [Fact]
    public void Long_titles_are_truncated() =>
        Assert.Equal(200, ServerApi.ParseNotificationList($$"""{"unreadCount":1,"items":[{"id":1,"title":"{{new string('t', 500)}}","url":"/x"}]}""")!.Items[0].Title.Length);
}

public class NotificationLogicTests
{
    private static ApiNotification N(int id, bool read = false, string url = "/x") => new(id, "K", "t" + id, url, read, "", null);

    [Theory]
    [InlineData(0, 30)]
    [InlineData(1, 60)]
    [InlineData(2, 120)]
    [InlineData(3, 240)]
    [InlineData(4, 300)]
    [InlineData(50, 300)]
    public void Backoff_doubles_from_30_seconds_to_5_minutes(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), NotificationLogic.NextPollDelay(failures));

    [Fact]
    public void First_poll_announces_nothing_but_records_the_baseline()
    {
        var (show, more, newest) = NotificationLogic.SelectToAnnounce([N(9), N(4)], null);
        Assert.Empty(show);
        Assert.Equal(0, more);
        Assert.Equal(9, newest);
        Assert.Equal(0, NotificationLogic.SelectToAnnounce([], null).Newest); // empty inbox: baseline 0
    }

    [Fact]
    public void Announces_only_unread_items_newer_than_the_last_one_oldest_first()
    {
        var (show, more, newest) = NotificationLogic.SelectToAnnounce([N(12), N(11, read: true), N(10), N(5)], 9);
        Assert.Equal([10, 12], show.Select(n => n.Id));
        Assert.Equal(0, more);
        Assert.Equal(12, newest);
    }

    [Fact]
    public void Caps_the_burst_and_counts_the_rest()
    {
        var items = Enumerable.Range(1, 8).Select(i => N(i)).Reverse().ToList();
        var (show, more, _) = NotificationLogic.SelectToAnnounce(items, 0, max: 5);
        Assert.Equal([4, 5, 6, 7, 8], show.Select(n => n.Id));
        Assert.Equal(3, more);
    }

    [Fact]
    public void Nothing_new_records_nothing()
    {
        var (show, _, newest) = NotificationLogic.SelectToAnnounce([N(3)], 3);
        Assert.Empty(show);
        Assert.Null(newest);
    }

    [Theory]
    [InlineData("/p/K/issues/1", "/p/K/issues/1")]
    [InlineData("//evil.com", "/notifications")]
    [InlineData("https://evil.com/x", "/notifications")]
    [InlineData("javascript:1", "/notifications")]
    [InlineData("", "/notifications")]
    public void Notification_paths_must_be_server_relative(string url, string expected) =>
        Assert.Equal(expected, NotificationLogic.PathForNotification(N(1, url: url), "https://bb.example.com"));
}

internal sealed class FakeTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

internal sealed class FakeServer : HttpMessageHandler
{
    public Func<HttpRequestMessage, (HttpStatusCode, string)> Handler { get; set; } = _ => (HttpStatusCode.NotFound, "");
    public List<(Uri Url, string? Auth)> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request.RequestUri!, request.Headers.Authorization?.ToString()));
        var (status, body) = Handler(request);
        if (status == 0) throw new HttpRequestException("down");
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}

public class NotificationPollerTests : IDisposable
{
    private const string Info = """{"name":"ByteBench","version":"1","apiVersion":1,"capabilities":["notifications"],"workspaceBaseDomain":null}""";
    private readonly TempDir _dir = new();
    private readonly FakeServer _server = new();
    private readonly FakeTime _time = new();
    private readonly InMemoryTokenStore _tokens = new();
    private readonly ProfileStore _store;
    private readonly NotificationPoller _poller;
    private readonly ServerProfile _profile;
    private readonly List<(ApiNotification N, string Path)> _announced = [];
    private readonly List<(string Id, int Unread, int Total)> _unread = [];

    public NotificationPollerTests()
    {
        _store = new ProfileStore(_dir.File("p.json"));
        _profile = _store.Add("S", "https://bb.example.com").Value!;
        _poller = new NotificationPoller(_store, _tokens, new HttpClient(_server), _time);
        _poller.NotificationAnnounced += (_, n, path) => _announced.Add((n, path));
        _poller.UnreadChanged += (id, u, t) => _unread.Add((id, u, t));
        _tokens.SetAsync(_profile.Id, "secret-token").GetAwaiter().GetResult();
    }

    public void Dispose() => _dir.Dispose();

    private static string List(int unread, params (int Id, bool Read, string Url)[] items) =>
        "{\"unreadCount\":" + unread + ",\"items\":[" + string.Join(",", items.Select(i => $"{{\"id\":{i.Id},\"kind\":\"K\",\"title\":\"T{i.Id}\",\"url\":\"{i.Url}\",\"isRead\":{(i.Read ? "true" : "false")},\"createdAt\":\"x\"}}")) + "]}";

    private void Serve(Func<string> notifications) =>
        _server.Handler = r => r.RequestUri!.AbsolutePath switch
        {
            "/api/v1/server-info" => (HttpStatusCode.OK, Info),
            "/api/v1/notifications" => (HttpStatusCode.OK, notifications()),
            _ => (HttpStatusCode.NotFound, ""),
        };

    [Fact]
    public async Task First_poll_sets_a_baseline_without_announcing_then_new_items_are_announced()
    {
        Serve(() => List(2, (8, false, "/p/K/issues/8"), (7, false, "/p/K/issues/7")));
        var first = await _poller.PollOnceAsync(_profile);
        Assert.Equal(PollStatus.Ok, first.Status);
        Assert.Equal(2, first.Unread);
        Assert.Empty(_announced);
        Assert.Equal(8, _store.Get(_profile.Id)!.LastNotifiedId);
        Assert.Equal(2, _poller.TotalUnread);

        Serve(() => List(3, (9, false, "/p/K/issues/9")));
        var second = await _poller.PollOnceAsync(_store.Get(_profile.Id)!);
        Assert.Equal(3, second.Unread);
        var (n, path) = Assert.Single(_announced);
        Assert.Equal(9, n.Id);
        Assert.Equal("/p/K/issues/9", path);
        Assert.Equal(9, _store.Get(_profile.Id)!.LastNotifiedId);
        Assert.Contains(_unread, u => u.Id == _profile.Id && u.Unread == 3 && u.Total == 3);
    }

    [Fact]
    public async Task Requests_carry_the_bearer_token_and_ask_only_for_newer_unread_items()
    {
        Serve(() => List(1, (4, false, "/x")));
        await _poller.PollOnceAsync(_profile);
        await _poller.PollOnceAsync(_store.Get(_profile.Id)!);
        var notifRequests = _server.Requests.Where(r => r.Url.AbsolutePath == "/api/v1/notifications").ToList();
        Assert.All(notifRequests, r => Assert.Equal("Bearer secret-token", r.Auth));
        Assert.DoesNotContain("afterId", notifRequests[0].Url.Query);
        Assert.Contains("afterId=4", notifRequests[1].Url.Query);
        Assert.Contains("unread=true", notifRequests[1].Url.Query);
        Assert.All(_server.Requests.Where(r => r.Url.AbsolutePath == "/api/v1/server-info"), r => Assert.Null(r.Auth)); // anonymous: no token leaves for it
        Assert.Single(_server.Requests, r => r.Url.AbsolutePath == "/api/v1/server-info"); // support is remembered
    }

    [Fact]
    public async Task Unsafe_notification_urls_open_the_notifications_page()
    {
        Serve(() => List(1, (1, false, "/seed")));
        await _poller.PollOnceAsync(_profile);
        Serve(() => List(1, (2, false, "//evil.com/x")));
        await _poller.PollOnceAsync(_store.Get(_profile.Id)!);
        Assert.Equal("/notifications", Assert.Single(_announced).Path);
    }

    [Fact]
    public async Task No_token_means_no_polling_and_no_request()
    {
        await _tokens.DeleteAsync(_profile.Id);
        var r = await _poller.PollOnceAsync(_profile);
        Assert.Equal(PollStatus.NoToken, r.Status);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task Disabled_notifications_make_no_requests()
    {
        _store.SetNotificationsEnabled(false);
        Assert.Equal(PollStatus.Disabled, (await _poller.PollOnceAsync(_profile)).Status);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task A_legacy_server_is_skipped_and_never_sent_the_token()
    {
        _server.Handler = _ => (HttpStatusCode.NotFound, """{"title":"Not Found","status":404}""");
        var r = await _poller.PollOnceAsync(_profile);
        Assert.Equal(PollStatus.Unsupported, r.Status);
        Assert.All(_server.Requests, q => Assert.Null(q.Auth));
        Assert.DoesNotContain(_server.Requests, q => q.Url.AbsolutePath == "/api/v1/notifications");
    }

    [Fact]
    public async Task Failures_back_off_30s_60s_120s_240s_300s_and_success_resets()
    {
        _server.Handler = _ => (0, ""); // connection fails
        var expected = new[] { 60, 120, 240, 300, 300 };
        foreach (var seconds in expected)
        {
            var r = await _poller.PollOnceAsync(_profile);
            Assert.Equal(PollStatus.Failed, r.Status);
            var due = await _poller.PollDueAsync();
            Assert.Equal(_time.GetUtcNow() + TimeSpan.FromSeconds(seconds), due);
            _time.Advance(TimeSpan.FromSeconds(seconds));
        }
        Serve(() => List(0));
        Assert.Equal(PollStatus.Ok, (await _poller.PollOnceAsync(_profile)).Status);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromSeconds(30), await _poller.PollDueAsync());
    }

    [Fact]
    public async Task PollDue_skips_profiles_that_are_not_due_yet()
    {
        Serve(() => List(0));
        await _poller.PollDueAsync();
        var count = _server.Requests.Count;
        _time.Advance(TimeSpan.FromSeconds(10));
        await _poller.PollDueAsync();
        Assert.Equal(count, _server.Requests.Count);
        _time.Advance(TimeSpan.FromSeconds(21));
        await _poller.PollDueAsync();
        Assert.True(_server.Requests.Count > count);
    }

    [Fact]
    public async Task Rejected_token_counts_as_a_failure_and_a_redirect_is_never_followed()
    {
        _server.Handler = r => r.RequestUri!.AbsolutePath == "/api/v1/server-info" ? (HttpStatusCode.OK, Info) : (HttpStatusCode.Unauthorized, "");
        Assert.Equal(PollStatus.Unauthorized, (await _poller.PollOnceAsync(_profile)).Status);
        _server.Handler = r => r.RequestUri!.AbsolutePath == "/api/v1/server-info" ? (HttpStatusCode.OK, Info) : (HttpStatusCode.Redirect, "");
        Assert.Equal(PollStatus.Failed, (await _poller.PollOnceAsync(_profile)).Status);
    }

    [Fact]
    public async Task Garbage_answers_are_a_failure_not_a_crash()
    {
        Serve(() => "<html>captive portal</html>");
        Assert.Equal(PollStatus.Failed, (await _poller.PollOnceAsync(_profile)).Status);
        Assert.Null(_store.Get(_profile.Id)!.LastNotifiedId);
    }

    [Fact]
    public async Task Burst_is_capped_and_summarised()
    {
        Serve(() => List(0));
        await _poller.PollOnceAsync(_profile);
        var more = 0;
        _poller.MoreAnnounced += (_, m) => more = m;
        Serve(() => List(8, Enumerable.Range(1, 8).Reverse().Select(i => (i, false, "/x")).ToArray()));
        await _poller.PollOnceAsync(_store.Get(_profile.Id)!);
        Assert.Equal(5, _announced.Count);
        Assert.Equal(3, more);
    }

    [Fact]
    public async Task Forget_clears_the_unread_count()
    {
        Serve(() => List(4, (1, false, "/x")));
        await _poller.PollOnceAsync(_profile);
        Assert.Equal(4, _poller.UnreadFor(_profile.Id));
        _poller.Forget(_profile.Id);
        Assert.Equal(0, _poller.TotalUnread);
    }

    [Fact]
    public async Task RunAsync_stops_when_cancelled()
    {
        Serve(() => List(0));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var poller = new NotificationPoller(_store, _tokens, new HttpClient(_server)); // real clock
        await poller.RunAsync(cts.Token);
        Assert.NotEmpty(_server.Requests);
    }
}

public class GitCloneTests
{
    private static readonly RepoRef Repo = new("WEB", "api");

    [Theory]
    [InlineData("/p/WEB/repos/api", "WEB", "api")]
    [InlineData("/p/WEB/repos/api/blob/main/x.cs", "WEB", "api")]
    [InlineData("/git/WEB/api.git", "WEB", "api")]
    [InlineData("/git/WEB/api.git/info/refs", "WEB", "api")]
    [InlineData("/p/WEB/repos/my.repo-1", "WEB", "my.repo-1")]
    public void RepoFromPath_accepts(string path, string key, string slug) => Assert.Equal(new RepoRef(key, slug), GitClone.RepoFromPath(path));

    [Theory]
    [InlineData("/p/WEB/issues/1")]
    [InlineData("/")]
    [InlineData("/p/-bad/repos/api")]
    [InlineData("/p/WEB/repos/--upload-pack=x")]
    [InlineData("/p/WEB/repos/..")]
    [InlineData("/p/WEB/repos/a%2Fb")]
    [InlineData("/git/WEB/x.git.git")]
    public void RepoFromPath_rejects(string path) => Assert.Null(GitClone.RepoFromPath(path));

    [Fact]
    public void Clone_url_and_arguments()
    {
        var url = GitClone.CloneUrl("https://bb.example.com/", Repo);
        Assert.Equal("https://bb.example.com/git/WEB/api.git", url);
        Assert.Equal(["clone", "--", url, "/tmp/x/api"], GitClone.CloneArgs(url, "/tmp/x/api"));
    }

    [Fact]
    public void Start_info_uses_an_argument_list_without_a_shell_and_keeps_the_token_off_the_command_line()
    {
        var psi = GitClone.BuildStartInfo("/usr/bin/git", "https://bb.example.com/git/WEB/api.git", "/tmp/a b/api; rm -rf x", "ann", "tok");
        Assert.False(psi.UseShellExecute);
        Assert.Equal("", psi.Arguments);
        Assert.Equal(["clone", "--", "https://bb.example.com/git/WEB/api.git", "/tmp/a b/api; rm -rf x"], psi.ArgumentList);
        Assert.DoesNotContain(psi.ArgumentList, a => a.Contains("tok"));
        Assert.Equal("0", psi.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ann:tok")), psi.Environment["GIT_CONFIG_VALUE_0"]);
    }

    [Fact]
    public void Without_a_token_no_credentials_are_set() => Assert.DoesNotContain("GIT_CONFIG_COUNT", GitClone.CloneEnv(null, null).Keys);

    [Fact]
    public void Target_directory_must_be_new_or_empty()
    {
        using var dir = new TempDir();
        var ok = GitClone.TargetDirectory(dir.Path, Repo);
        Assert.True(ok.Ok);
        Assert.Equal(Path.Combine(dir.Path, "api"), ok.Dir);

        Directory.CreateDirectory(ok.Dir);
        Assert.True(GitClone.TargetDirectory(dir.Path, Repo).Ok); // existing but empty
        File.WriteAllText(Path.Combine(ok.Dir, "f"), "x");
        var busy = GitClone.TargetDirectory(dir.Path, Repo);
        Assert.False(busy.Ok);
        Assert.Contains("not empty", busy.Error);
    }

    [Fact]
    public void FindOnPath_walks_PATH_without_a_shell()
    {
        using var dir = new TempDir();
        var exe = OperatingSystem.IsWindows() ? "git.exe" : "git";
        File.WriteAllText(dir.File(exe), "");
        var sep = OperatingSystem.IsWindows() ? ";" : ":";
        Assert.Equal(dir.File(exe), GitClone.FindOnPath("git", "/nonexistent" + sep + dir.Path, OperatingSystem.IsWindows()));
        Assert.Null(GitClone.FindOnPath("git", "/nonexistent", OperatingSystem.IsWindows()));
    }

    [Fact]
    public async Task RunAsync_really_clones_a_local_repository_through_the_argument_list()
    {
        var git = GitClone.FindOnPath("git", Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows());
        if (git is null) return; // git is not installed on this machine: nothing to run
        using var dir = new TempDir();
        var origin = Path.Combine(dir.Path, "srv");
        // A "server": <origin>/git/WEB/api.git is a bare repository reachable as a file:// URL.
        var bare = Path.Combine(origin, "git", "WEB", "api.git");
        Directory.CreateDirectory(bare);
        Assert.Equal(0, await Run(git, "init", "--bare", "--quiet", "--initial-branch=main", bare));
        var seed = Path.Combine(dir.Path, "seed");
        Assert.Equal(0, await Run(git, "init", "--quiet", "--initial-branch=main", seed));
        File.WriteAllText(Path.Combine(seed, "README.md"), "hello");
        Assert.Equal(0, await Run(git, "-C", seed, "add", "."));
        Assert.Equal(0, await Run(git, "-C", seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "--quiet", "-m", "init"));
        Assert.Equal(0, await Run(git, "-C", seed, "push", "--quiet", bare, "HEAD:refs/heads/main"));

        var parent = Path.Combine(dir.Path, "work");
        Directory.CreateDirectory(parent);
        var fileOrigin = new Uri(origin).AbsoluteUri.TrimEnd('/'); // file:///.../srv
        var result = await GitClone.RunAsync(fileOrigin, Repo, parent, null, null, git);
        Assert.True(result.Ok, result.Message + " / " + fileOrigin);
        Assert.True(File.Exists(Path.Combine(parent, "api", "README.md")));

        // Cloning again into the now non-empty folder is refused before git is started.
        var again = await GitClone.RunAsync(fileOrigin, Repo, parent, null, null, git);
        Assert.False(again.Ok);
    }

    [Fact]
    public async Task RunAsync_reports_git_failures()
    {
        var git = GitClone.FindOnPath("git", Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows());
        if (git is null) return;
        using var dir = new TempDir();
        var result = await GitClone.RunAsync(new Uri(dir.Path).AbsoluteUri.TrimEnd('/'), Repo, dir.Path, null, null, git);
        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task RunAsync_without_git_says_so()
    {
        using var dir = new TempDir();
        var r = await GitClone.RunAsync("https://x.example.com", Repo, dir.Path, null, null, gitExecutable: Path.Combine(dir.Path, "no-such-git"));
        Assert.False(r.Ok);
        Assert.Contains("Could not start git", r.Message);
    }

    private static async Task<int> Run(string exe, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        await p.StandardOutput.ReadToEndAsync();
        await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return p.ExitCode;
    }
}
