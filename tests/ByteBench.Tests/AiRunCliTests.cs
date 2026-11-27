using System.Diagnostics;
using System.Net;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Ai;
using ByteBench.Services.Editor.Ai;
using ByteBench.Services.Workspaces;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ByteBench.Tests;

/// <summary>
/// AI runs on the Claude Code CLI backend, with a scripted fake <c>claude</c> (a shell script that emits stream-json and calls the run's MCP bridge with
/// curl, like the real CLI would) and the fake Docker backend. The real CLI, the model and Docker are never used.
/// </summary>
public class AiRunCliApp : AiRunApp
{
    // The host starts in the base constructor (KestrelApp), so the fake is written the first time it is needed.
    private string? fakeDir;
    public string FakeDir => fakeDir ??= Prepare();
    public string Script => Path.Combine(FakeDir, "claude");

    private string Prepare()
    {
        var dir = Path.Combine(DataRoot, "fake-claude");
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "claude");
        File.WriteAllText(script, ScriptText.Replace("@DIR@", dir).Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(dir, "mode"), "normal");
        return dir;
    }

    public string Mode { set => File.WriteAllText(Path.Combine(FakeDir, "mode"), value); }
    public string Read(string name) => File.ReadAllText(Path.Combine(FakeDir, name));
    public bool Has(string name) => File.Exists(Path.Combine(FakeDir, name));

    protected override void ExtraSettings(IDictionary<string, string?> settings)
    {
        settings["Workspaces:AiRuns:Backend"] = "claude-cli";
        settings["Ai:ClaudeCode:Path"] = Script;
        settings["Workspaces:AiRuns:CommandTimeoutSeconds"] = "30";
        ExtraCliSettings(settings);
    }

    protected virtual void ExtraCliSettings(IDictionary<string, string?> settings) { }

    public static async Task<bool> ExitedAsync(int pid)
    {
        for (var i = 0; i < 80; i++)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited) return true;
            }
            catch (ArgumentException) { return true; }
            await Task.Delay(100);
        }
        return false;
    }

    private const string ScriptText = """
        #!/bin/sh
        D='@DIR@'
        case "$1" in
          --version) echo "9.9.9 (Claude Code)"; exit 0;;
          --help)
            echo "Usage: claude [options] [command] [prompt]"
            echo "  -p, --print  --output-format <format>  --verbose  --mcp-config <configs...>  --strict-mcp-config"
            echo "  --tools <tools...>  --allowedTools, --allowed-tools <tools...>  --disallowedTools, --disallowed-tools <tools...>"
            echo "  --permission-mode <mode>  --no-session-persistence  --disable-slash-commands  --max-turns <n>  --max-budget-usd <amount>"
            echo "  --append-system-prompt <prompt>  --model <model>"
            exit 0;;
        esac
        : > "$D/args"
        for a in "$@"; do printf '%s\n' "$a" >> "$D/args"; done
        env > "$D/env"
        pwd > "$D/cwd"
        ls -A > "$D/files"
        cat > "$D/stdin"
        echo $$ > "$D/pid"
        MCP=""; prev=""
        for a in "$@"; do [ "$prev" = "--mcp-config" ] && MCP="$a"; prev="$a"; done
        cp "$MCP" "$D/mcp.json"
        URL=$(sed -n 's/.*"url":"\([^"]*\)".*/\1/p' "$MCP")
        TOKEN=$(sed -n 's/.*Bearer \([0-9a-f]*\)".*/\1/p' "$MCP")
        rpc() { curl -s -X POST "$URL" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d "$1"; }
        mode=$(cat "$D/mode")
        if [ "$mode" = "sleep" ]; then
          printf '%s\n' '{"type":"assistant","message":{"id":"m1","content":[{"type":"text","text":"Thinking."}],"usage":{"input_tokens":10,"output_tokens":2}}}'
          sleep 30 &
          echo $! > "$D/childpid"
          wait
          exit 0
        fi
        if [ "$mode" = "steps" ]; then
          for i in 1 2 3 4 5 6; do
            printf '%s\n' '{"type":"assistant","message":{"id":"m'$i'","content":[{"type":"text","text":"step"}],"usage":{"input_tokens":10,"output_tokens":2}}}'
          done
          sleep 30 &
          echo $! > "$D/childpid"
          wait
          exit 0
        fi
        if [ "$mode" = "toolflood" ]; then
          printf '%s\n' '{"type":"assistant","message":{"id":"m1","content":[{"type":"tool_use","id":"a","name":"mcp__bytebench__list_files","input":{}},{"type":"tool_use","id":"b","name":"mcp__bytebench__list_files","input":{}},{"type":"tool_use","id":"c","name":"mcp__bytebench__list_files","input":{}},{"type":"tool_use","id":"d","name":"mcp__bytebench__list_files","input":{}}],"usage":{"input_tokens":10,"output_tokens":2}}}'
          sleep 30 &
          echo $! > "$D/childpid"
          wait
          exit 0
        fi
        if [ "$mode" = "loginerror" ]; then
          printf '%s\n' '{"type":"result","subtype":"success","is_error":true,"result":"Invalid API key · Please run /login","usage":{"input_tokens":0,"output_tokens":0}}'
          exit 1
        fi
        rpc '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"claude","version":"9"}}}' > "$D/rpc-init"
        rpc '{"jsonrpc":"2.0","method":"notifications/initialized"}' > "$D/rpc-initialized"
        rpc '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' > "$D/rpc-list"
        curl -s -o /dev/null -w '%{http_code}' -X POST "$URL" -d '{}' > "$D/noauth"
        curl -s -o /dev/null -w '%{http_code}' -X POST "$URL" -H 'Authorization: Bearer 0000' -d '{}' > "$D/badauth"
        printf '%s\n' '{"type":"system","subtype":"init","tools":["mcp__bytebench__write_file"]}'
        printf '%s\n' '{"type":"assistant","message":{"id":"m1","content":[{"type":"text","text":"I will fix it."},{"type":"tool_use","id":"u1","name":"mcp__bytebench__write_file","input":{"path":"src/a.txt","content":"fixed\n"}}],"usage":{"input_tokens":100,"output_tokens":20}}}'
        rpc '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"write_file","arguments":{"path":"src/a.txt","content":"fixed\n"}}}' > "$D/rpc-write"
        printf '%s\n' '{"type":"assistant","message":{"id":"m2","content":[{"type":"tool_use","id":"u2","name":"mcp__bytebench__run_command","input":{"command":"dotnet test"}}],"usage":{"input_tokens":150,"output_tokens":30}}}'
        rpc '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"run_command","arguments":{"command":"dotnet test"}}}' > "$D/rpc-command"
        rpc '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"git_push","arguments":{}}}' > "$D/rpc-unknown"
        rpc '{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"read_file","arguments":{"path":"../../etc/passwd"}}}' > "$D/rpc-escape"
        printf '%s\n' '{"type":"assistant","message":{"id":"m3","content":[{"type":"text","text":"Done."}],"usage":{"input_tokens":50,"output_tokens":10}}}'
        printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"Fixed the bug in `src/a.txt`; `dotnet test` passes.","total_cost_usd":0.0123,"usage":{"input_tokens":300,"output_tokens":60,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}'
        """;
}

public class AiRunCliLimitsApp : AiRunCliApp
{
    protected override void ExtraCliSettings(IDictionary<string, string?> s) { s["Workspaces:AiRuns:MaxSteps"] = "3"; s["Workspaces:AiRuns:MaxToolCalls"] = "2"; }
}

public class AiRunCliTimeApp : AiRunCliApp
{
    protected override void ExtraCliSettings(IDictionary<string, string?> s) => s["Workspaces:AiRuns:MaxDurationMinutes"] = "0.04";
}

/// <summary>Neither backend: no key (the model reports unconfigured) and no CLI on the path.</summary>
public class AiRunNeitherApp : AiRunApp
{
    protected override void ExtraSettings(IDictionary<string, string?> settings) => settings["Ai:ClaudeCode:Path"] = "definitely-not-installed-claude";
    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton<IEditorChatModel>(new UnconfiguredModel());
    }
}

public sealed class UnconfiguredModel : IEditorChatModel
{
    public bool IsConfigured { get; set; }
    public IEditorChatConversation Begin(EditorChatSetup setup) => throw new InvalidOperationException("no model");
}

public class AiRunCliTests(AiRunCliApp app) : IClassFixture<AiRunCliApp>
{
    private static string[] Args(AiRunCliApp a) => a.Read("args").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public async Task A_cli_run_edits_through_the_bridge_inside_the_container_and_opens_the_pull_request()
    {
        if (OperatingSystem.IsWindows()) return;
        app.Mode = "normal";
        Environment.SetEnvironmentVariable("BYTEBENCH_TEST_HOST_SECRET", "do-not-leak");
        try
        {
            var (project, repo, issue) = await app.SetupAsync();
            var pushed = app.ScriptGit(repo);
            app.Backend.ExecScripts.Add((s => s.User == "bbai" && s.Command.Any(c => c.Contains("dotnet test")), _ => new ExecResult(0, "build ok, token gbw_secrettoken1234 printed")));
            var run = await app.WaitAsync((await app.RequestAsync(issue)).PublicId);

            Assert.Equal((AiRunStatus.Succeeded, null), (run.Status, run.StopReason));
            Assert.Equal([run.Branch!], pushed);
            Assert.Equal(3, run.Steps); // model messages, read off the stream
            Assert.Equal(2, run.ToolCalls); // tool_use blocks
            Assert.Equal(0.0123m, run.CostUsd); // the CLI's own estimate
            Assert.Equal(300 + 0, run.InputTokens);
            Assert.Equal(60, run.OutputTokens);
            Assert.Contains("dotnet test", run.Summary);
            Assert.Empty(app.Model.Setups); // the API was not used

            await using var db = app.Db();
            var pr = await db.PullRequests.AsNoTracking().SingleAsync(p => p.Id == run.PullRequestId);
            Assert.Equal((run.Branch, app.BotId(), false), (pr.SourceBranch, pr.AuthorId, pr.IsDraft));
            Assert.Contains($"Fixes {project.Key}-{issue.Number}", pr.Description);
            Assert.Contains($"pull request #{pr.Number}", Assert.Single(await app.CommentsAsync(issue.Id)));

            // The edit and the command ran in the container as bbai (never as the developer), through the same tools as the API backend.
            await app.AssertWorkspaceDeletedAsync(run);
            var execs = app.Backend.Execs.Select(e => e.Spec).ToList();
            Assert.Contains(execs, e => e.User == "bbai" && e.Stdin == "fixed\n");
            Assert.DoesNotContain(execs.Where(e => e.User == "bbai"), e => e.Command.Contains("push") || e.Command.Any(c => c.Contains("git commit")));
            Assert.Contains(execs, e => e.User == "coder" && e.Command.Count >= 3 && e.Command[0] == "git" && e.Command[1] == "push");

            // The CLI: restricted command line, no built-in tools, clean environment, empty private directory that is deleted afterwards.
            var args = Args(app);
            string After(string flag) => args[Array.IndexOf(args, flag) + 1];
            Assert.Contains("-p", args);
            Assert.Equal("stream-json", After("--output-format"));
            Assert.Contains("--strict-mcp-config", args);
            var raw = app.Read("args").Split('\n');
            Assert.Equal(["--tools", "", "--allowedTools"], raw.Skip(Array.IndexOf(raw, "--tools")).Take(3).ToArray()); // no built-in tools at all
            Assert.Equal("mcp__bytebench", After("--allowedTools"));
            Assert.Contains("Bash", After("--disallowedTools").Split(','));
            Assert.Contains("WebFetch", After("--disallowedTools").Split(','));
            Assert.Equal("dontAsk", After("--permission-mode"));
            Assert.Equal("40", After("--max-turns"));
            Assert.Equal("5", After("--max-budget-usd"));
            Assert.DoesNotContain("--dangerously-skip-permissions", args);
            Assert.Contains("untrusted", app.Read("args"));
            var token = System.Text.RegularExpressions.Regex.Match(app.Read("mcp.json"), "Bearer ([0-9a-f]+)").Groups[1].Value;
            Assert.Equal(64, token.Length);
            Assert.DoesNotContain(token, app.Read("args")); // the config file holds the token, never the command line, the environment or the prompt
            Assert.DoesNotContain(token, app.Read("env"));
            Assert.DoesNotContain(token, app.Read("stdin"));
            Assert.DoesNotContain("BYTEBENCH_TEST_HOST_SECRET", app.Read("env"));
            Assert.DoesNotContain("ConnectionStrings", app.Read("env"));
            Assert.Equal(["mcp.json"], app.Read("files").Split('\n', StringSplitOptions.RemoveEmptyEntries));
            Assert.False(Directory.Exists(app.Read("cwd").Trim()), "the run directory is deleted");
            Assert.Contains("UNTRUSTED ISSUE", app.Read("stdin"));
            Assert.Contains("Fix the login bug", app.Read("stdin"));

            // The bridge: MCP handshake, exactly the workspace tools, bearer token required, output masked, nothing outside the workspace.
            Assert.Contains("\"protocolVersion\":\"2025-03-26\"", app.Read("rpc-init"));
            Assert.Equal("", app.Read("rpc-initialized"));
            var names = JsonDocument.Parse(app.Read("rpc-list")).RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
            Assert.Equal(WorkspaceAiTools.Definitions.Select(d => d.Name).ToArray(), names);
            Assert.DoesNotContain(names, n => n!.Contains("commit") || n.Contains("push"));
            Assert.Equal("401", app.Read("noauth"));
            Assert.Equal("401", app.Read("badauth"));
            Assert.DoesNotContain("isError\":true", app.Read("rpc-write"));
            var command = app.Read("rpc-command");
            Assert.DoesNotContain("gbw_secrettoken1234", command);
            Assert.Contains("***", command);
            Assert.Contains("UNTRUSTED COMMAND OUTPUT", command);
            Assert.Contains("Unknown tool", app.Read("rpc-unknown"));
            Assert.Contains("isError\":true", app.Read("rpc-escape"));
            Assert.DoesNotContain("root:", app.Read("rpc-escape"));
            var url = System.Text.RegularExpressions.Regex.Match(app.Read("mcp.json"), "\"url\":\"([^\"]+)\"").Groups[1].Value;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.PostAsync(url, new StringContent("{}"))); // the bridge is closed after the run

            // The log shows the streamed events; usage and audit were recorded.
            var events = await app.EventsAsync(run.Id);
            Assert.Contains(events, e => e.StartsWith("status: Backend: Claude Code CLI"));
            Assert.Contains("tool: Writing src/a.txt", events);
            Assert.Contains(events, e => e.StartsWith("tool: Running: dotnet test"));
            Assert.Contains(events, e => e == "info: AI: I will fix it.");
            Assert.Contains(events, e => e.StartsWith("step: Step 3:"));
            Assert.Contains($"info: Opened pull request #{pr.Number}", events);
            Assert.NotEmpty(await db.AiUsages.Where(u => u.Feature == "workspace.run.claude-cli").ToListAsync());
            Assert.Contains("ai_run.finished", await db.AuditEvents.Where(a => a.TargetId == run.PublicId).Select(a => a.Action).ToListAsync());
        }
        finally { Environment.SetEnvironmentVariable("BYTEBENCH_TEST_HOST_SECRET", null); }
    }

    [Fact]
    public async Task The_backend_choice_is_visible_to_the_admin_and_overridable_site_wide()
    {
        var admin = app.UserId("alice");
        await using var s = app.Scope(admin);
        var runs = s.Get<AiRunService>();
        var info = await runs.GetBackendAsync();
        Assert.Equal((AiRunBackendKind.ClaudeCli, "claude-cli", false), (info.Active, info.Setting, info.FromSite));
        Assert.Contains("Claude Code CLI", info.Explanation);
        Assert.True(info.CliInstalled);

        await runs.SetBackendSiteAsync("api", admin);
        info = await runs.GetBackendAsync();
        Assert.Equal((AiRunBackendKind.Api, "api", true), (info.Active, info.Setting, info.FromSite)); // the fake model is a configured API
        await runs.SetBackendSiteAsync(null, admin); // back to the configuration
        Assert.Equal(AiRunBackendKind.ClaudeCli, (await runs.GetBackendAsync()).Active);

        var other = await app.NewUserAsync();
        await using var s2 = app.Scope(app.UserId(other));
        await Assert.ThrowsAsync<ForbiddenException>(() => s2.Get<AiRunService>().SetBackendSiteAsync("api", app.UserId(other)));
    }

    [Fact]
    public async Task Auto_prefers_the_api_key_else_the_cli_and_explains_when_neither_exists()
    {
        var options = app.Services.GetRequiredService<IOptionsMonitor<WorkspaceOptions>>();
        var detector = app.Services.GetRequiredService<ClaudeCodeDetector>();
        await using var s = app.Scope(app.UserId("alice"));
        var site = s.Get<ByteBench.Services.Accounts.SiteSettings>();
        await site.SetAsync(AiRunBackendResolver.SettingKey, "auto", null);
        try
        {
            var model = new UnconfiguredModel();
            var resolver = new AiRunBackendResolver(options, model, detector, site);
            var cli = await resolver.ResolveAsync();
            Assert.Equal(AiRunBackendKind.ClaudeCli, cli.Active);
            Assert.Contains("no Claude API key", cli.Explanation);

            model.IsConfigured = true;
            Assert.Equal(AiRunBackendKind.Api, (await resolver.ResolveAsync()).Active);

            await site.SetAsync(AiRunBackendResolver.SettingKey, "api", null); // explicit: never silently moved to the CLI
            model.IsConfigured = false;
            var api = await resolver.ResolveAsync();
            Assert.Equal(AiRunBackendKind.None, api.Active);
            Assert.Contains("no Claude API key", api.Explanation);
        }
        finally { await site.SetAsync(AiRunBackendResolver.SettingKey, "", null); }
    }

    [Fact]
    public void The_command_line_always_restricts_the_cli_even_when_help_lists_nothing()
    {
        var bare = new ClaudeCodeStatus(true, "1.0", "/x/claude", null, DateTime.UtcNow, new HashSet<string>());
        var args = AiRunExecutor.BuildCliArguments(bare, "/tmp/mcp.json", "sonnet", new AiRunOptions(), "SYSTEM");
        Assert.Contains("--strict-mcp-config", args);
        Assert.Contains("--mcp-config", args);
        Assert.DoesNotContain("--tools", args); // only passed when the CLI lists it
        Assert.Contains("--disallowedTools", args);
        Assert.Contains("dontAsk", args);
        Assert.Contains("sonnet", args);
        Assert.DoesNotContain("--max-turns", args);
    }

    [Fact]
    public async Task Cancelling_a_cli_run_kills_the_process_tree_and_removes_the_workspace()
    {
        if (OperatingSystem.IsWindows()) return;
        app.Mode = "sleep";
        var (_, repo, issue) = await app.SetupAsync();
        app.ScriptGit(repo);
        var run = await app.RequestAsync(issue);
        await app.WaitForStatusAsync(run.PublicId, AiRunStatus.Running);
        await WaitForFileAsync("childpid");
        var pid = int.Parse(app.Read("pid").Trim());
        var child = int.Parse(app.Read("childpid").Trim());
        await using (var s = app.Scope(app.UserId("alice"))) await s.Get<AiRunService>().CancelAsync(run.PublicId, app.UserId("alice"));
        var done = await app.WaitAsync(run.PublicId);
        Assert.Equal((AiRunStatus.Cancelled, "cancelled"), (done.Status, done.StopReason));
        Assert.Null(done.PullRequestId);
        Assert.True(await AiRunCliApp.ExitedAsync(pid), "the CLI is still running");
        Assert.True(await AiRunCliApp.ExitedAsync(child), "a child of the CLI is still running");
        await app.AssertWorkspaceDeletedAsync(done);
        Assert.False(Directory.Exists(app.Read("cwd").Trim()));
    }

    [Fact]
    public async Task A_login_problem_fails_the_run_with_a_hint_and_nothing_is_pushed()
    {
        if (OperatingSystem.IsWindows()) return;
        app.Mode = "loginerror";
        var (_, repo, issue) = await app.SetupAsync();
        var pushed = app.ScriptGit(repo, status: "");
        var run = await app.WaitAsync((await app.RequestAsync(issue)).PublicId);
        Assert.Equal((AiRunStatus.Failed, "error"), (run.Status, run.StopReason));
        Assert.Contains("Sign in again", run.Error);
        Assert.Empty(pushed);
        await app.AssertWorkspaceDeletedAsync(run);
        app.Mode = "normal";
    }

    private async Task WaitForFileAsync(string name)
    {
        for (var i = 0; i < 200 && !app.Has(name); i++) await Task.Delay(50);
        Assert.True(app.Has(name));
    }
}

public class AiRunCliLimitTests(AiRunCliLimitsApp app) : IClassFixture<AiRunCliLimitsApp>
{
    [Fact]
    public async Task The_step_limit_is_read_off_the_stream_kills_the_cli_and_keeps_the_work_as_a_draft_pull_request()
    {
        if (OperatingSystem.IsWindows()) return;
        app.Mode = "steps";
        var (_, repo, issue) = await app.SetupAsync();
        app.ScriptGit(repo);
        var run = await app.WaitAsync((await app.RequestAsync(issue)).PublicId);
        Assert.Equal((AiRunStatus.Failed, "step limit"), (run.Status, run.StopReason));
        Assert.NotNull(run.PullRequestId);
        await using var db = app.Db();
        Assert.True((await db.PullRequests.AsNoTracking().SingleAsync(p => p.Id == run.PullRequestId)).IsDraft);
        Assert.Equal(3, run.Steps);
        Assert.True(await AiRunCliApp.ExitedAsync(int.Parse(app.Read("childpid").Trim())));
    }

    [Fact]
    public async Task The_tool_call_limit_stops_the_cli_too()
    {
        if (OperatingSystem.IsWindows()) return;
        app.Mode = "toolflood";
        var (_, repo, issue) = await app.SetupAsync();
        app.ScriptGit(repo, status: "");
        var run = await app.WaitAsync((await app.RequestAsync(issue)).PublicId);
        Assert.Equal((AiRunStatus.Failed, "tool call limit"), (run.Status, run.StopReason));
        Assert.Equal(3, run.ToolCalls); // the third call crossed the limit of 2
        Assert.True(await AiRunCliApp.ExitedAsync(int.Parse(app.Read("childpid").Trim())));
    }
}

public class AiRunCliTimeTests(AiRunCliTimeApp app) : IClassFixture<AiRunCliTimeApp>
{
    [Fact]
    public async Task The_time_limit_kills_the_cli_process_tree()
    {
        if (OperatingSystem.IsWindows()) return;
        app.Mode = "sleep";
        var (_, repo, issue) = await app.SetupAsync();
        app.ScriptGit(repo);
        var run = await app.WaitAsync((await app.RequestAsync(issue)).PublicId, seconds: 30);
        Assert.Equal((AiRunStatus.Failed, "time limit"), (run.Status, run.StopReason));
        Assert.True(await AiRunCliApp.ExitedAsync(int.Parse(app.Read("pid").Trim())));
        Assert.True(await AiRunCliApp.ExitedAsync(int.Parse(app.Read("childpid").Trim())));
        await app.AssertWorkspaceDeletedAsync(run);
    }
}

public class AiRunNeitherTests(AiRunNeitherApp app) : IClassFixture<AiRunNeitherApp>
{
    [Fact]
    public async Task With_no_key_and_no_cli_runs_are_unavailable_and_say_why()
    {
        var (project, _, issue) = await app.SetupAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => app.RequestAsync(issue));
        Assert.Contains("isn't set up", ex.Message);
        Assert.Contains("Claude Code CLI", ex.Message);
        await using var s = app.Scope(app.UserId("alice"));
        var availability = await s.Get<AiRunService>().GetAvailabilityAsync(project.Id);
        Assert.False(availability.Available);
        Assert.Contains("was not found", availability.Reason);
        var info = await s.Get<AiRunService>().GetBackendAsync();
        Assert.Equal(AiRunBackendKind.None, info.Active);
        Assert.False(info.CliInstalled);
    }
}
