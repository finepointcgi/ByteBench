using System.Net;
using System.Text.Json.Nodes;
using ByteBench.Cli;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Tests.Infrastructure;

namespace ByteBench.Tests;

/// <summary>Runs the bytebench CLI in-process against the TestApp over (in-memory) HTTP.</summary>
public class CliTests(TestApp app) : IClassFixture<TestApp>, IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "bytebench-cli-tests", Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(dir, "bytebench", "config.json");

    public void Dispose()
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { }
    }

    private sealed record Result(int Code, string Out, string Err)
    {
        public JsonNode? Json => JsonNode.Parse(Out);
    }

    private async Task<Result> Run(string[] args, string? stdin = null, Dictionary<string, string?>? vars = null, bool tty = false,
        Func<HttpMessageHandler>? handler = null)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var env = new CliEnvironment
        {
            Out = o, Err = e, In = new StringReader(stdin ?? ""), InputRedirected = stdin != null,
            OutputIsTerminal = tty, ConfigPath = ConfigPath,
            GetVariable = name => vars != null && vars.TryGetValue(name, out var v) ? v : null,
            CreateHandler = handler ?? (() => app.Server.CreateHandler()),
        };
        var code = await new CliApp(env).RunAsync(args);
        return new Result(code, o.ToString(), e.ToString());
    }

    private async Task<(string Token, Result Login)> LoginAsync(string user = "alice", TokenScopes scopes = TokenScopes.ReadApi | TokenScopes.WriteApi)
    {
        var token = await app.CreateTokenAsync(user, scopes);
        return (token, await Run(["login", "--server", "http://localhost"], token + "\n"));
    }

    // ---------- login, logout, whoami, config file ----------

    [Fact]
    public async Task Login_stores_the_token_privately_and_whoami_and_logout_work()
    {
        var (token, login) = await LoginAsync();
        Assert.Equal(0, login.Code);
        Assert.Contains("as alice", login.Out);
        Assert.DoesNotContain(token, login.Out + login.Err);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(ConfigPath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(ConfigPath)!));
        }
        Assert.Contains(token, File.ReadAllText(ConfigPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(ConfigPath)!, "*.tmp"));

        var who = await Run(["whoami"]);
        Assert.Equal(0, who.Code);
        Assert.StartsWith("alice", who.Out);
        Assert.Contains("read_api", who.Out);
        Assert.DoesNotContain(token, who.Out + who.Err);
        var whoJson = (await Run(["whoami", "--json"])).Json;
        Assert.Equal("alice", whoJson!["username"]!.GetValue<string>());

        var out1 = await Run(["logout"]);
        Assert.Equal(0, out1.Code);
        Assert.False(File.Exists(ConfigPath));
        Assert.Equal(ExitCodes.Auth, (await Run(["whoami"])).Code);
    }

    [Fact]
    public async Task Login_takes_the_token_from_the_environment_and_rejects_bad_ones()
    {
        var token = await app.CreateTokenAsync("bob", TokenScopes.ReadApi);
        var viaEnv = await Run(["login", "--server", "http://localhost"], vars: new() { ["BYTEBENCH_TOKEN"] = token });
        Assert.Equal(0, viaEnv.Code);
        Assert.DoesNotContain(token, viaEnv.Out + viaEnv.Err);
        File.Delete(ConfigPath);

        var wrong = await Run(["login", "--server", "http://localhost"], "gbp_definitely-not-valid\n");
        Assert.Equal(ExitCodes.Auth, wrong.Code);
        Assert.False(File.Exists(ConfigPath));

        Assert.Equal(ExitCodes.Usage, (await Run(["login", "--server", "http://localhost"], "not-a-token\n")).Code);
        Assert.Equal(ExitCodes.Usage, (await Run(["login", "--server", "http://localhost"], "")).Code); // empty stdin
        var tty = await Run(["login", "--server", "http://localhost"]); // interactive terminal, nothing to read
        Assert.Equal(ExitCodes.Usage, tty.Code);
        Assert.Contains("stdin", tty.Err);
        Assert.Equal(ExitCodes.Usage, (await Run(["login"], token)).Code); // no --server
    }

    [Fact]
    public async Task Plain_http_is_refused_except_for_localhost_unless_insecure()
    {
        var token = await app.CreateTokenAsync("alice", TokenScopes.ReadApi);
        var refused = await Run(["login", "--server", "http://bytebench.example.com"], token);
        Assert.Equal(ExitCodes.Usage, refused.Code);
        Assert.Contains("plain HTTP", refused.Err);
        Assert.False(File.Exists(ConfigPath));
        Assert.Equal(ExitCodes.Usage, (await Run(["login", "--server", "ftp://x"], token)).Code);
        Assert.Equal(ExitCodes.Usage, (await Run(["login", "--server", "https://user:pw@host.example"], token)).Code);

        foreach (var local in new[] { "http://localhost", "http://127.0.0.1:5108", "http://[::1]:8080", "http://dev.localhost" })
            Assert.True(ServerUrl.IsLocal(new Uri(local)), local);
        Assert.False(ServerUrl.IsLocal(new Uri("http://localhost.evil.example")));
        Assert.False(ServerUrl.IsLocal(new Uri("http://10.0.0.5")));

        // --insecure is explicit and warns; https needs nothing (this test server answers any host name).
        var ok = await Run(["login", "--server", "http://bytebench.example.com", "--insecure"], token);
        Assert.Equal(0, ok.Code);
        Assert.Contains("plain HTTP", ok.Err);
        Assert.Equal(0, (await Run(["whoami"])).Code); // remembered
        Assert.Equal(0, (await Run(["login", "--server", "https://bytebench.example.com"], token)).Code);
        Assert.Equal(0, (await Run(["whoami"])).Code);
        Assert.False(JsonNode.Parse(File.ReadAllText(ConfigPath))!["insecure"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_config_file_is_never_written_through_a_symlink_and_must_be_private()
    {
        if (OperatingSystem.IsWindows()) return;
        var token = await app.CreateTokenAsync("alice", TokenScopes.ReadApi);
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var target = Path.Combine(dir, "victim.txt");
        File.WriteAllText(target, "precious");
        File.CreateSymbolicLink(ConfigPath, target);
        var r = await Run(["login", "--server", "http://localhost"], token);
        Assert.Equal(ExitCodes.Error, r.Code);
        Assert.Contains("symbolic link", r.Err);
        Assert.Equal("precious", File.ReadAllText(target));
        Assert.Equal(ExitCodes.Error, (await Run(["whoami"])).Code);
        Assert.Equal(ExitCodes.Error, (await Run(["logout"])).Code);
        Assert.True(File.Exists(target));

        // A linked directory is refused too.
        File.Delete(ConfigPath);
        Directory.Delete(Path.GetDirectoryName(ConfigPath)!);
        var realDir = Path.Combine(dir, "real");
        Directory.CreateDirectory(realDir);
        Directory.CreateSymbolicLink(Path.GetDirectoryName(ConfigPath)!, realDir);
        Assert.Equal(ExitCodes.Error, (await Run(["login", "--server", "http://localhost"], token)).Code);
        Assert.Empty(Directory.GetFiles(realDir));
        Directory.Delete(Path.GetDirectoryName(ConfigPath)!);

        // A config other users could read isn't trusted.
        Assert.Equal(0, (await Run(["login", "--server", "http://localhost"], token)).Code);
        File.SetUnixFileMode(ConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var open = await Run(["whoami"]);
        Assert.Equal(ExitCodes.Error, open.Code);
        Assert.Contains("chmod 600", open.Err);
        Assert.DoesNotContain(token, open.Err);
    }

    [Fact]
    public async Task Environment_variables_work_without_a_config_file_and_nothing_prints_the_token()
    {
        var token = await app.CreateTokenAsync("alice", TokenScopes.ReadApi | TokenScopes.WriteApi);
        var vars = new Dictionary<string, string?> { ["BYTEBENCH_TOKEN"] = token, ["BYTEBENCH_SERVER"] = "http://localhost" };
        var all = new List<Result>
        {
            await Run(["whoami"], vars: vars), await Run(["projects"], vars: vars), await Run(["issues", "list", "--project", "WEB"], vars: vars),
            await Run(["issues", "show", "WEB-1"], vars: vars), await Run(["open", "WEB-1"], vars: vars),
            await Run(["issues", "show", "WEB-99999"], vars: vars), await Run(["--bogus"], vars: vars),
        };
        Assert.Equal([0, 0, 0, 0, 0, 1, 2], all.Select(r => r.Code));
        Assert.All(all, r => Assert.DoesNotContain(token, r.Out + r.Err));
        Assert.False(File.Exists(ConfigPath));
        Assert.Contains("Not found", all[5].Err);

        // A wrong token is an authentication failure (3), not a generic error.
        var bad = await Run(["whoami"], vars: new() { ["BYTEBENCH_TOKEN"] = "gbp_wrong", ["BYTEBENCH_SERVER"] = "http://localhost" });
        Assert.Equal(ExitCodes.Auth, bad.Code);
    }

    [Fact]
    public async Task A_redirect_is_not_followed_so_the_token_stays_put()
    {
        var token = await app.CreateTokenAsync("alice", TokenScopes.ReadApi);
        var seen = new List<Uri>();
        var vars = new Dictionary<string, string?> { ["BYTEBENCH_TOKEN"] = token, ["BYTEBENCH_SERVER"] = "http://localhost" };
        var r = await Run(["whoami"], vars: vars, handler: () => new RedirectingHandler(seen));
        Assert.Equal(ExitCodes.Error, r.Code);
        Assert.Contains("redirected", r.Err);
        Assert.Single(seen);
    }

    private sealed class RedirectingHandler(List<Uri> seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            seen.Add(request.RequestUri!);
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("https://evil.example/steal");
            return Task.FromResult(r);
        }
    }

    // ---------- usage ----------

    [Fact]
    public async Task Usage_errors_exit_2_and_help_exits_0()
    {
        Assert.Equal(ExitCodes.Usage, (await Run([])).Code);
        var help = await Run(["--help"]);
        Assert.Equal(0, help.Code);
        Assert.Contains("issues create", help.Out);
        Assert.Equal(0, (await Run(["issues", "--help"])).Code);
        Assert.Equal(ExitCodes.Usage, (await Run(["frobnicate"])).Code);
        Assert.Equal(ExitCodes.Usage, (await Run(["issues"])).Code);
        Assert.Equal(ExitCodes.Usage, (await Run(["issues", "list", "--nope"])).Code);
        Assert.Equal(ExitCodes.Usage, (await Run(["issues", "list", "--limit"])).Code);
        Assert.Equal(ExitCodes.Usage, (await Run(["issues", "list", "--json=yes"])).Code);
        Assert.Equal(ExitCodes.Usage, (await Run(["whoami", "--project", "WEB"])).Code);
        Assert.Equal(0, (await Run(["--version"])).Code);

        await LoginAsync();
        Assert.Equal(ExitCodes.Usage, (await Run(["issues", "show"])).Code);
        Assert.Equal(ExitCodes.Usage, (await Run(["issues", "show", "not-a-key"])).Code);
        Assert.Equal(ExitCodes.Usage, (await Run(["issues", "show", "WEB-1", "extra"])).Code);
        Assert.Equal(ExitCodes.Usage, (await Run(["issues", "create", "--project", "WEB"])).Code); // no title
        Assert.Equal(ExitCodes.Usage, (await Run(["issues", "list", "--limit", "5000"])).Code);
        Assert.Equal(ExitCodes.Usage, (await Run(["issues", "list", "--project", "WEB", "--status", "weird"])).Code);
        Assert.Equal(ExitCodes.Usage, (await Run(["prs", "list", "--state", "weird"])).Code);
        Assert.Equal(ExitCodes.Usage, (await Run(["pipelines", "list", "nonsense"])).Code);
        Assert.Equal(ExitCodes.Usage, (await Run(["issues", "comment", "WEB-1"])).Code); // no body
    }

    [Fact]
    public async Task Version_prints_the_package_version_and_completion_scripts_cover_every_command()
    {
        var version = await Run(["--version"]);
        Assert.Equal(0, version.Code);
        Assert.Matches(@"^bytebench \d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?\s*$", version.Out);
        Assert.DoesNotContain("+", version.Out); // no source-revision suffix
        Assert.Equal("bytebench " + CliApp.VersionText, version.Out.Trim());

        foreach (var shell in new[] { "bash", "zsh", "fish" })
        {
            var r = await Run(["completion", shell]);
            Assert.Equal(0, r.Code);
            foreach (var command in CliApp.CompletionCommands.Keys) Assert.Contains(command, r.Out);
            Assert.Contains("pipelines", r.Out);
            Assert.Contains("logs", r.Out);
        }
        Assert.Contains("complete -F _bytebench bytebench", (await Run(["completion", "bash"])).Out);
        Assert.Equal(ExitCodes.Usage, (await Run(["completion"])).Code);
        Assert.Equal(ExitCodes.Usage, (await Run(["completion", "powershell"])).Code);
        Assert.Equal(ExitCodes.Usage, (await Run(["completion", "bash", "--project", "X"])).Code);
    }

    // ---------- commands ----------

    [Fact]
    public async Task Issue_commands_round_trip()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        await LoginAsync("bob");
        var key = project.Key;

        var projects = await Run(["projects"]);
        Assert.Contains(key, projects.Out);
        Assert.Contains(key, (await Run(["projects", "--json"])).Json!.AsArray().Select(p => p!["key"]!.GetValue<string>()));

        var created = await Run(["issues", "create", "--project", key, "--title", "Crash on save", "--description", "It crashes.", "--type", "bug", "--priority", "high", "--assignee", "alice"]);
        Assert.Equal(0, created.Code);
        Assert.Contains($"Created {key}-1", created.Out);
        var createdJson = await Run(["issues", "create", "--project", key, "--title", "Second", "--json", "--description-file", "-"], "from stdin\n");
        Assert.Equal($"{key}-2", createdJson.Json!["key"]!.GetValue<string>());
        Assert.Equal("from stdin", createdJson.Json["description"]!.GetValue<string>().Trim());

        var list = await Run(["issues", "list", "--project", key]);
        Assert.Contains("Crash on save", list.Out);
        Assert.Contains("alice", list.Out);
        var gql = await Run(["issues", "list", "--project", key, "--query", "text ~ \"crash\""]);
        Assert.Contains($"{key}-1", gql.Out);
        Assert.DoesNotContain($"{key}-2", gql.Out);
        Assert.Single((await Run(["issues", "list", "--project", key, "--query", "text ~ \"crash\"", "--json"])).Json!.AsArray());
        Assert.Equal(ExitCodes.Error, (await Run(["issues", "list", "--query", "AND AND"])).Code); // 400 from the server

        var comment = await Run(["issues", "comment", $"{key}-1", "--body", "Seen it too."]);
        Assert.Equal(0, comment.Code);
        var show = await Run(["issues", "show", $"{key}-1"]);
        Assert.Contains("Crash on save", show.Out);
        Assert.Contains("It crashes.", show.Out);
        Assert.Contains("Seen it too.", show.Out);
        Assert.Equal("Seen it too.", (await Run(["issues", "show", $"{key}-1", "--json"])).Json!["comments"]![0]!["body"]!.GetValue<string>());

        var moved = await Run(["issues", "move", $"{key}-1", "--to", "In Progress"]);
        Assert.Equal(0, moved.Code);
        Assert.Contains("In Progress", moved.Out);
        Assert.Equal(0, (await Run(["issues", "move", $"{key}-1", "--to", "done"])).Code); // category name
        var bad = await Run(["issues", "move", $"{key}-1", "--to", "Nowhere"]);
        Assert.Equal(ExitCodes.Error, bad.Code);
        Assert.Contains("statuses", bad.Err);

        Assert.Contains("assigned to bob", (await Run(["issues", "assign", $"{key}-1", "bob"])).Out);
        Assert.Contains("unassigned", (await Run(["issues", "assign", $"{key}-1", "--none"])).Out);
        Assert.Equal(ExitCodes.Error, (await Run(["issues", "assign", $"{key}-1", "no-such-user"])).Code);

        Assert.Equal($"http://localhost/p/{key}/issues/1\n", (await Run(["open", $"{key}-1"])).Out.Replace("\r\n", "\n"));
        Assert.Equal($"http://localhost/p/{key}/issues/1", (await Run(["open", $"{key}-1", "--json"])).Json!["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_read_only_token_cannot_write_and_a_private_project_is_invisible()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private);
        var issue = await app.CreateIssueAsync(project.Id, "alice", "Hidden");
        await LoginAsync("alice", TokenScopes.ReadApi);
        var denied = await Run(["issues", "create", "--project", project.Key, "--title", "nope"]);
        Assert.Equal(ExitCodes.Auth, denied.Code); // missing scope
        Assert.Contains("scope", denied.Err);
        Assert.Contains("Hidden", (await Run(["issues", "list", "--project", project.Key])).Out);

        File.Delete(ConfigPath);
        await LoginAsync("carol");
        var r = await Run(["issues", "show", $"{project.Key}-{issue.Number}"]);
        Assert.Equal(ExitCodes.Error, r.Code);
        Assert.Contains("Not found", r.Err);
        Assert.DoesNotContain(project.Key, (await Run(["projects"])).Out);
    }

    [Fact]
    public async Task Repository_pull_request_and_pipeline_commands()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        var repo = await app.CreateRepoAsync(project.Id, "alice", "svc");
        app.Commit(repo, "feature/z", "z.txt", "z");
        await using (var s = app.Scope(app.UserId("bob")))
            await s.Get<PullRequestService>().CreateAsync(repo, "Add z", "Adds z.", "feature/z", "main", app.UserId("bob"));
        int pipelineId;
        await using (var db = app.Db())
        {
            var p = new Pipeline { RepositoryId = repo.Id, Ref = "main", CommitSha = new string('b', 40), CommitMessage = "ci", Status = PipelineStatus.Failed };
            var job = new PipelineJob { Name = "build", Image = "alpine", Status = PipelineStatus.Failed };
            var step = new PipelineStep { Name = "compile", Run = "false", Status = PipelineStatus.Failed, ExitCode = 2 };
            job.Steps.Add(step);
            p.Jobs.Add(job);
            db.Pipelines.Add(p);
            await db.SaveChangesAsync();
            db.PipelineStepLogs.Add(new PipelineStepLog { StepId = step.Id, Text = "line 1\nerror: boom \u001b[2Jhidden\n" });
            await db.SaveChangesAsync();
            pipelineId = p.Id;
        }
        await LoginAsync("bob");
        var name = $"{project.Key}/svc";

        var repos = await Run(["repos", "list", "--project", project.Key]);
        Assert.Contains(name, repos.Out);
        Assert.Contains("main", repos.Out);
        Assert.Contains(name, (await Run(["repos", "list"])).Out);
        Assert.Equal("svc", (await Run(["repos", "list", "--project", project.Key, "--json"])).Json![0]!["slug"]!.GetValue<string>());

        var prs = await Run(["prs", "list", "--project", project.Key]);
        Assert.Contains("Add z", prs.Out);
        Assert.Contains($"{name}#1", prs.Out);
        Assert.Contains("Add z", (await Run(["prs", "list"])).Out);
        Assert.DoesNotContain("Add z", (await Run(["prs", "list", "--project", project.Key, "--state", "merged"])).Out);
        var show = await Run(["prs", "show", name, "1"]);
        Assert.Contains("feature/z -> main", show.Out);
        Assert.Contains("Adds z.", show.Out);
        Assert.Contains("Add z", (await Run(["prs", "show", name + "#1"])).Out);
        Assert.Equal(ExitCodes.Error, (await Run(["prs", "show", name, "99"])).Code);
        Assert.Equal("Add z", (await Run(["prs", "show", name, "1", "--json"])).Json!["pullRequest"]!["title"]!.GetValue<string>());

        var pipelines = await Run(["pipelines", "list", name]);
        Assert.Contains(pipelineId.ToString(), pipelines.Out);
        Assert.Contains("failed", pipelines.Out);
        Assert.DoesNotContain(pipelineId + " ", (await Run(["pipelines", "list", name, "--status", "passed"])).Out);

        var logs = await Run(["pipelines", "logs", name, pipelineId.ToString()]);
        Assert.Equal(0, logs.Code);
        Assert.Contains("error: boom", logs.Out);
        Assert.DoesNotContain('\u001b', logs.Out); // terminal escapes from a log are neutralised
        Assert.Contains("compile", logs.Err);
        Assert.Equal(ExitCodes.Error, (await Run(["pipelines", "logs", name, "999999"])).Code);
        Assert.Contains("boom", (await Run(["pipelines", "logs", name, pipelineId.ToString(), "--json"])).Json!["log"]!.GetValue<string>());
    }

    // ---------- output ----------

    [Fact]
    public async Task Colour_only_on_a_terminal_without_no_color_and_server_text_cannot_inject_escapes()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private);
        await app.CreateIssueAsync(project.Id, "alice", "Evil \u001b[31mred\u001b]0;pwned\u0007 title");
        await using (var db = app.Db())
        {
            var i = db.Issues.First(x => x.ProjectId == project.Id);
            i.Status = IssueStatus.Done;
            await db.SaveChangesAsync();
        }
        await LoginAsync();
        var plain = await Run(["issues", "list", "--project", project.Key, "--status", "all"]);
        Assert.Contains("Evil", plain.Out);
        Assert.DoesNotContain('\u001b', plain.Out);
        Assert.DoesNotContain('\u0007', plain.Out);

        var colour = await Run(["issues", "list", "--project", project.Key, "--status", "all"], tty: true);
        Assert.Contains("\u001b[32m", colour.Out, StringComparison.Ordinal); // Done is green on a terminal…
        Assert.DoesNotContain("\u001b[31mred", colour.Out, StringComparison.Ordinal); // …but the title's own escape is gone

        var noColor = await Run(["issues", "list", "--project", project.Key, "--status", "all"], tty: true, vars: new() { ["NO_COLOR"] = "1" });
        Assert.DoesNotContain('\u001b', noColor.Out);
        Assert.Equal(plain.Out, noColor.Out);

        // --json is raw data, byte for byte what the server sent (JSON-escaped).
        var json = await Run(["issues", "list", "--project", project.Key, "--status", "all", "--json"], tty: true);
        Assert.DoesNotContain('\u001b', json.Out);
        Assert.Contains("Evil", json.Json![0]!["title"]!.GetValue<string>());
    }
}
