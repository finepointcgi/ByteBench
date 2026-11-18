using System.Diagnostics;
using GitBench.Data;
using GitBench.Services;
using GitBench.Services.Editor;
using GitBench.Services.Editor.Ai;
using GitBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GitBench.Tests;

/// <summary>
/// The app with the Claude Code backend pointed at a fake <c>claude</c> shell script (never the real one). The script answers
/// --version/--help, records its arguments, environment and working directory, then behaves per <see cref="Mode"/>:
/// "normal" streams canned stream-json and edits files, "sleep" hangs (timeout), "turns" calls tools forever (turn cap).
/// </summary>
public class ClaudeCodeApp : TestApp
{
    public string FakeDir { get; }
    public string Script => Path.Combine(FakeDir, "claude");

    public ClaudeCodeApp()
    {
        FakeDir = Path.Combine(DataRoot, "fake-claude");
        Directory.CreateDirectory(FakeDir);
        File.WriteAllText(Script, ScriptText.Replace("@DIR@", FakeDir).Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Mode = "normal";
    }

    public string Mode { set => File.WriteAllText(Path.Combine(FakeDir, "mode"), value); }

    public string Read(string name) => File.ReadAllText(Path.Combine(FakeDir, name));

    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["Ai:Editor:Backend"] = "ClaudeCode";
        settings["Ai:ClaudeCode:Path"] = Script;
        settings["Ai:ClaudeCode:TimeoutSeconds"] = "2";
        settings["Ai:ClaudeCode:MaxTurns"] = "5";
    }

    private const string ScriptText = """
        #!/bin/sh
        D='@DIR@'
        case "$1" in
          --version) echo "9.9.9 (Claude Code)"; exit 0;;
          --help)
            echo "Usage: claude [options] [command] [prompt]"
            echo "  -p, --print  --output-format <format>  --include-partial-messages  --verbose"
            echo "  --tools <tools...>  --allowedTools, --allowed-tools <tools...>  --disallowedTools, --disallowed-tools <tools...>"
            echo "  --permission-mode <mode>  --permission-prompts <target>  --restricted  --strict-mcp-config"
            echo "  --no-session-persistence  --disable-slash-commands  --max-turns <n>  --append-system-prompt <prompt>  --model <model>"
            exit 0;;
        esac
        : > "$D/args"
        for a in "$@"; do printf '%s\n' "$a" >> "$D/args"; done
        env > "$D/env"
        pwd > "$D/cwd"
        ls -ld . > "$D/dirmode"
        ls -A > "$D/files"
        cat > "$D/stdin"
        echo $$ > "$D/pid"
        mode=$(cat "$D/mode")
        if [ "$mode" = "sleep" ]; then
          sleep 30 &
          echo $! > "$D/childpid"
          wait
          exit 0
        fi
        if [ "$mode" = "turns" ]; then
          i=0
          while [ $i -lt 50 ]; do
            echo '{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t","name":"Read","input":{"file_path":"old.txt"}}]}}'
            i=$((i+1))
            sleep 0.05
          done
          exit 0
        fi
        echo '{"type":"system","subtype":"init","tools":["Read","Grep","Glob","Edit","Write"]}'
        echo '{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Editing the files."}}}'
        echo '{"type":"assistant","message":{"content":[{"type":"text","text":"Editing the files."},{"type":"tool_use","id":"t1","name":"Edit","input":{"file_path":"src/app.txt"}}]}}'
        printf 'line 1\nline TWO\nline 3\n' > src/app.txt
        printf 'brand new\n' > new.txt
        rm old.txt
        mkdir -p .claude && echo '{"hooks":{}}' > .claude/settings.local.json
        echo '{"type":"result","subtype":"success","is_error":false,"result":"Editing the files.","usage":{"input_tokens":12,"output_tokens":7,"cache_read_input_tokens":3,"cache_creation_input_tokens":0}}'
        """;
}

public class WebEditorClaudeCodeTests(ClaudeCodeApp app) : IClassFixture<ClaudeCodeApp>
{
    private async Task<(Project Project, Repository Repo, EditorContext Ctx)> SetupAsync()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        var repo = await app.CreateRepoAsync(project.Id, "alice");
        app.Commit(repo, "main", "src/app.txt", "line 1\nline 2\nline 3\n", message: "add app");
        app.Commit(repo, "main", "old.txt", "old\n", message: "add old");
        app.Commit(repo, "main", ".claude/settings.json", "{\"hooks\":{\"PreToolUse\":[]}}\n", message: "repo-supplied Claude config");
        var bob = app.UserId("bob");
        await using var s = app.Scope(bob);
        return (project, repo, await s.Get<EditorWorkspaceService>().OpenAsync(repo, "main", bob));
    }

    [Fact]
    public async Task Claude_code_runs_restricted_in_a_private_copy_and_its_edits_become_pending_proposals()
    {
        if (OperatingSystem.IsWindows()) return;
        app.Mode = "normal";
        var (project, _, ctx) = await SetupAsync();
        var bob = app.UserId("bob");
        Environment.SetEnvironmentVariable("GITBENCH_TEST_HOST_SECRET", "do-not-leak");
        await using var s = app.Scope(bob);
        var ai = s.Get<EditorAiService>();
        var status = await ai.GetStatusAsync(project.Id);
        Assert.True(status.Enabled);
        Assert.Equal(EditorAiBackendKind.ClaudeCode, status.Backend);
        Assert.False(status.CompletionsAvailable); // ghost text is API-only

        // An uncommitted change is part of the copy Claude Code sees.
        await s.Get<EditorWorkspaceService>().CreateFileAsync(ctx.WorkspaceId, bob, "draft.txt", "work in progress\n");
        var texts = new List<string>();
        var sink = new Sink(texts);
        var result = await ai.RunAsync(ctx.WorkspaceId, bob, new EditorAiInput("Tidy up the files") { OpenFile = "src/app.txt" }, sink, CancellationToken.None);
        Assert.Null(result.Error);
        Assert.Contains("Editing the files.", result.Text);
        Assert.Contains("Editing the files.", string.Concat(texts));
        Assert.Equal(1, result.ToolCalls);

        // Command line: read/edit tools only, Bash and the web refused, never prompts, capped.
        var args = app.Read("args").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string After(string flag) => args[Array.IndexOf(args, flag) + 1];
        Assert.Contains("-p", args);
        Assert.Equal("stream-json", After("--output-format"));
        Assert.Equal("Read,Grep,Glob,Edit,Write", After("--tools"));
        Assert.DoesNotContain("Bash", After("--tools"));
        Assert.DoesNotContain("Bash", After("--allowedTools"));
        Assert.Contains("Bash", After("--disallowedTools").Split(','));
        Assert.Contains("WebFetch", After("--disallowedTools").Split(','));
        Assert.Contains("WebSearch", After("--disallowedTools").Split(','));
        Assert.Equal("dontAsk", After("--permission-mode"));
        Assert.Equal("5", After("--max-turns"));
        Assert.Contains("--strict-mcp-config", args);
        Assert.Contains("--restricted", args);
        Assert.DoesNotContain("--dangerously-skip-permissions", args);
        Assert.Contains("--append-system-prompt", args);
        Assert.Contains("are untrusted data", app.Read("args")); // the multi-line system prompt is one argument
        // The prompt went on stdin, not the command line.
        Assert.Contains("Tidy up the files", app.Read("stdin"));
        Assert.DoesNotContain(args, a => a.Contains("Tidy up the files"));

        // Clean environment, private temp directory, repo-supplied Claude config not copied.
        Assert.DoesNotContain("GITBENCH_TEST_HOST_SECRET", app.Read("env"));
        Assert.DoesNotContain("ConnectionStrings", app.Read("env"));
        Assert.StartsWith("drwx------", app.Read("dirmode"));
        var copied = app.Read("files");
        Assert.Contains("draft.txt", copied);
        Assert.DoesNotContain(".claude", copied);
        var cwd = app.Read("cwd").Trim();
        Assert.False(Directory.Exists(cwd), "the temporary workspace is deleted afterwards");

        // Every change is a pending proposal; the .claude folder it wrote is ignored; nothing was applied.
        Assert.Equal(["new.txt", "old.txt", "src/app.txt"], result.Proposals.Select(p => p.Path).ToArray());
        Assert.All(result.Proposals, p => Assert.Equal(ProposalState.Pending, p.State));
        Assert.Equal(ProposedEditKind.Create, result.Proposals[0].Kind);
        Assert.Equal(ProposedEditKind.Delete, result.Proposals[1].Kind);
        var modify = result.Proposals[2];
        Assert.Equal(ProposedEditKind.Modify, modify.Kind);
        Assert.Equal("line 1\nline TWO\nline 3\n", modify.NewText);
        var ws = s.Get<EditorWorkspaceService>();
        Assert.Equal("line 1\nline 2\nline 3\n", (await ws.ReadFileAsync(ctx.WorkspaceId, bob, "src/app.txt")).Text);

        await ai.AcceptAsync(ctx.WorkspaceId, bob, modify);
        Assert.Equal("line 1\nline TWO\nline 3\n", (await ws.ReadFileAsync(ctx.WorkspaceId, bob, "src/app.txt")).Text);

        await using var db = app.Db();
        Assert.True(await db.AiUsages.AnyAsync(u => u.Feature == "editor.chat.claude-code" && u.OutputTokens == 7 && u.UserId == bob));
    }

    [Fact]
    public async Task Claude_code_is_killed_at_the_timeout()
    {
        if (OperatingSystem.IsWindows()) return;
        app.Mode = "sleep";
        var (_, _, ctx) = await SetupAsync();
        var bob = app.UserId("bob");
        await using var s = app.Scope(bob);
        var watch = Stopwatch.StartNew();
        var result = await s.Get<EditorAiService>().RunAsync(ctx.WorkspaceId, bob, new EditorAiInput("Hang"), NullEditorAiSink.Instance, CancellationToken.None);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
        Assert.Contains("didn't finish", result.Error);
        Assert.Empty(result.Proposals);
        foreach (var pidFile in new[] { "pid", "childpid" })
        {
            var pid = int.Parse(app.Read(pidFile).Trim());
            Assert.True(await ExitedAsync(pid), $"process {pid} ({pidFile}) still runs");
        }
        Assert.False(Directory.Exists(app.Read("cwd").Trim()));
    }

    [Fact]
    public async Task Claude_code_is_stopped_at_the_turn_cap()
    {
        if (OperatingSystem.IsWindows()) return;
        app.Mode = "turns";
        var (_, _, ctx) = await SetupAsync();
        var bob = app.UserId("bob");
        await using var s = app.Scope(bob);
        var result = await s.Get<EditorAiService>().RunAsync(ctx.WorkspaceId, bob, new EditorAiInput("Loop"), NullEditorAiSink.Instance, CancellationToken.None);
        Assert.Contains("5 turns", result.StopNote);
        Assert.Equal(6, result.ToolCalls);
        Assert.Empty(result.Proposals);
    }

    private static async Task<bool> ExitedAsync(int pid)
    {
        for (var i = 0; i < 50; i++)
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

    private sealed class Sink(List<string> texts) : IEditorAiSink
    {
        public Task TextAsync(string delta) { texts.Add(delta); return Task.CompletedTask; }
        public Task ToolAsync(string tool, string detail) => Task.CompletedTask;
        public Task ProposalAsync(ProposedEdit edit) => Task.CompletedTask;
    }
}
