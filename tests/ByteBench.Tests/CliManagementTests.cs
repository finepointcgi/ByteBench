using System.Text.Json.Nodes;
using ByteBench.Cli;
using ByteBench.Data;
using ByteBench.Tests.Infrastructure;

namespace ByteBench.Tests;

/// <summary>The bytebench commands that create and configure project structure, run in-process against the TestApp.</summary>
public class CliManagementTests(TestApp app) : IClassFixture<TestApp>, IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "bytebench-cli-mgmt-tests", Guid.NewGuid().ToString("N"));
    private static int counter;

    public void Dispose()
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { }
    }

    private sealed record Result(int Code, string Out, string Err)
    {
        public JsonNode? Json => JsonNode.Parse(Out);
    }

    private async Task<Result> Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var env = new CliEnvironment
        {
            Out = o, Err = e, In = new StringReader(""), InputRedirected = false, OutputIsTerminal = false,
            ConfigPath = Path.Combine(dir, "bytebench", "config.json"),
            GetVariable = _ => null,
            CreateHandler = () => app.Server.CreateHandler(),
        };
        var code = await new CliApp(env).RunAsync(args);
        return new Result(code, o.ToString(), e.ToString());
    }

    private async Task LoginAsync(string user = "alice", TokenScopes scopes = TokenScopes.ReadApi | TokenScopes.WriteApi)
    {
        var token = await app.CreateTokenAsync(user, scopes);
        var o = new StringWriter();
        var env = new CliEnvironment
        {
            Out = o, Err = new StringWriter(), In = new StringReader(token + "\n"), InputRedirected = true, OutputIsTerminal = false,
            ConfigPath = Path.Combine(dir, "bytebench", "config.json"), GetVariable = _ => null, CreateHandler = () => app.Server.CreateHandler(),
        };
        Assert.Equal(0, await new CliApp(env).RunAsync(["login", "--server", "http://localhost"]));
    }

    [Fact]
    public async Task A_project_can_be_built_up_and_torn_down_from_the_command_line()
    {
        await LoginAsync();
        var key = "CL" + Interlocked.Increment(ref counter).ToString("D3");
        var created = await Run("projects", "create", "--key", key, "--name", "CLI project", "--visibility", "internal", "--json");
        Assert.Equal(0, created.Code);
        Assert.Equal(key, created.Json!["key"]!.GetValue<string>());
        Assert.Equal("internal", created.Json!["visibility"]!.GetValue<string>());

        var dupe = await Run("projects", "create", "--key", key, "--name", "Again");
        Assert.Equal(ExitCodes.Error, dupe.Code);
        Assert.Contains("Conflict", dupe.Err);

        Assert.Equal(0, (await Run("projects", "update", key, "--name", "Renamed by CLI", "--description", "hello")).Code);
        Assert.Equal(ExitCodes.Usage, (await Run("projects", "update", key)).Code); // nothing to change

        var add = await Run("members", "add", key, "bob", "--role", "developer");
        Assert.Equal(0, add.Code);
        Assert.Contains("developer", add.Out);
        Assert.Equal(ExitCodes.Error, (await Run("members", "add", key, "bob", "--role", "viewer")).Code); // already a member
        Assert.Equal(ExitCodes.Usage, (await Run("members", "add", key, "bob", "--role", "boss")).Code);
        Assert.Equal(0, (await Run("members", "role", key, "bob", "--role", "maintainer")).Code);
        var members = await Run("members", "list", key);
        Assert.Contains("bob", members.Out);
        Assert.Contains("maintainer", members.Out);
        Assert.Equal(0, (await Run("members", "remove", key, "bob")).Code);

        var label = await Run("labels", "create", key, "--name", "cli-label", "--color", "#00ff00", "--json");
        Assert.Equal(0, label.Code);
        var labelId = label.Json!["id"]!.GetValue<int>();
        Assert.Contains("cli-label", (await Run("labels", "list", key)).Out);
        Assert.Equal(0, (await Run("labels", "delete", key, labelId.ToString())).Code);
        Assert.Equal(ExitCodes.Error, (await Run("labels", "delete", key, labelId.ToString())).Code);

        var version = await Run("versions", "create", key, "--name", "1.0", "--end", "2031-01-01", "--json");
        var versionId = version.Json!["id"]!.GetValue<int>();
        Assert.Contains("released", (await Run("versions", "release", key, versionId.ToString())).Out);
        Assert.Contains("yes", (await Run("versions", "list", key)).Out);

        var sprint = await Run("sprints", "create", key, "--goal", "Learn", "--json");
        var sprintId = sprint.Json!["id"]!.GetValue<int>().ToString();
        Assert.Equal(ExitCodes.Usage, (await Run("sprints", "start", key, sprintId, "--start", "2031-01-01")).Code); // no --end
        Assert.Equal(0, (await Run("sprints", "start", key, sprintId, "--start", "2031-01-01", "--end", "2031-01-14")).Code);
        Assert.Contains("active", (await Run("sprints", "list", key)).Out);
        var done = await Run("sprints", "complete", key, sprintId);
        Assert.Equal(0, done.Code);
        Assert.Contains("backlog", done.Out);

        var repo = await Run("repos", "create", "--project", key, "--name", "CLI Repo", "--json");
        Assert.Equal(0, repo.Code);
        Assert.Equal("cli-repo", repo.Json!["slug"]!.GetValue<string>());
        Assert.Equal(0, (await Run("repos", "update", $"{key}/cli-repo", "--description", "d")).Code);
        Assert.Equal(ExitCodes.Error, (await Run("repos", "update", $"{key}/cli-repo", "--branch", "ghost")).Code);
        Assert.Equal(0, (await Run("protection", "set", $"{key}/cli-repo", "--pattern", "main", "--approvals", "2", "--type", "block-pushes")).Code);
        var updated = await Run("protection", "set", $"{key}/cli-repo", "--pattern", "main", "--approvals", "1");
        Assert.Contains("Updated", updated.Out);
        var rules = await Run("protection", "list", $"{key}/cli-repo", "--json");
        Assert.Single(rules.Json!.AsArray());
        Assert.Equal(0, (await Run("protection", "delete", $"{key}/cli-repo", rules.Json![0]!["id"]!.GetValue<int>().ToString())).Code);

        var hook = await Run("webhooks", "create", key, "--url", "https://example.com/hook", "--type", "push", "--type", "issue.created");
        Assert.Equal(0, hook.Code);
        Assert.Contains("Signing secret", hook.Out);
        Assert.Equal(ExitCodes.Usage, (await Run("webhooks", "create", key, "--url", "https://example.com/hook")).Code);
        var hooks = await Run("webhooks", "list", key, "--json");
        Assert.DoesNotContain("Signing", hooks.Out);
        Assert.Equal(0, (await Run("webhooks", "delete", key, hooks.Json![0]!["id"]!.GetValue<int>().ToString())).Code);

        Assert.Equal(0, (await Run("repos", "archive", $"{key}/cli-repo")).Code);
        Assert.Equal(0, (await Run("repos", "unarchive", $"{key}/cli-repo")).Code);
        Assert.Equal(ExitCodes.Usage, (await Run("repos", "delete", $"{key}/cli-repo")).Code); // needs --confirm
        Assert.Equal(ExitCodes.Error, (await Run("repos", "delete", $"{key}/cli-repo", "--confirm", "nope")).Code);
        Assert.Equal(0, (await Run("repos", "delete", $"{key}/cli-repo", "--confirm", "CLI Repo")).Code);

        Assert.Equal(ExitCodes.Usage, (await Run("projects", "delete", key)).Code);
        Assert.Equal(ExitCodes.Usage, (await Run("projects", "delete", key, "--confirm", "OTHER")).Code);
        Assert.Equal(0, (await Run("projects", "delete", key, "--confirm", key)).Code);
        Assert.Equal(ExitCodes.Error, (await Run("projects", "update", key, "--name", "gone")).Code);
    }

    [Fact]
    public async Task Refusals_become_exit_codes_and_messages()
    {
        await LoginAsync("bob");
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        var denied = await Run("members", "add", project.Key, "carol", "--role", "viewer");
        Assert.Equal(ExitCodes.Error, denied.Code);
        Assert.Contains("Not allowed", denied.Err);
        Assert.Equal(ExitCodes.Usage, (await Run("members", "wave", project.Key)).Code);
        Assert.Equal(ExitCodes.Usage, (await Run("sprints", "create", project.Key, "--nope", "x")).Code);
        Assert.Equal(ExitCodes.Usage, (await Run("labels", "create", "not a key", "--name", "x")).Code);
        Assert.Equal(ExitCodes.Usage, (await Run("sprints")).Code);

        await LoginAsync("alice", TokenScopes.ReadApi);
        var readOnly = await Run("labels", "create", project.Key, "--name", "nope");
        Assert.Equal(ExitCodes.Auth, readOnly.Code);
        Assert.Contains("scope", readOnly.Err);
    }

    [Fact]
    public async Task Completion_knows_the_new_commands()
    {
        var script = await Run("completion", "bash");
        Assert.Equal(0, script.Code);
        foreach (var word in new[] { "members", "sprints", "protection", "webhooks", "labels", "versions", "--confirm", "--visibility" })
            Assert.Contains(word, script.Out);
    }
}
