using ByteBench.Services.Workspaces;

namespace ByteBench.Tests;

/// <summary>The environment file: strict validation, the devcontainer subset, detection for repositories without a file.</summary>
public class WorkspaceEnvironmentTests
{
    private const string Full = """
        toolchain: node@22
        setup:
          - npm ci
          - "npx playwright install"
        tasks:
          run:
            command: npm run dev
            port: 5173
            preview: true
          test: npm test
        ports: [3000, 9229]
        env:
          NODE_ENV: development
          FEATURE: true
        submodules: false
        services:
          db:
            image: postgres:16
            env:
              POSTGRES_PASSWORD: dev
          cache: redis:7
        """;

    [Fact]
    public void A_complete_file_parses()
    {
        var r = WorkspaceEnvFile.ParseYaml(Full);
        Assert.True(r.Ok, string.Join("\n", r.Errors));
        var e = r.Environment!;
        Assert.Equal("node@22", e.Toolchain);
        Assert.Equal(["npm ci", "npx playwright install"], e.Setup);
        Assert.Equal(2, e.Tasks.Count);
        Assert.Equal(new WorkspaceTaskDefinition("run", "npm run dev", 5173, true), e.Tasks[0]);
        Assert.Equal(new WorkspaceTaskDefinition("test", "npm test", null, false), e.Tasks[1]);
        Assert.Equal([3000, 9229], e.Ports);
        Assert.Equal("true", e.Env["FEATURE"]);
        Assert.False(e.Submodules);
        Assert.Equal(["db", "cache"], e.Services.Select(s => s.Name));
        Assert.Equal([5432], e.Services[0].Ports); // the usual port of an official image
        Assert.Equal([6379], e.Services[1].Ports);
        Assert.Equal([5173, 3000, 9229], e.AllPorts);
    }

    [Fact]
    public void Submodules_default_to_on_and_the_setup_hash_follows_the_commands()
    {
        var a = WorkspaceEnvFile.ParseYaml("setup: npm ci\n").Environment!;
        var b = WorkspaceEnvFile.ParseYaml("setup: [npm ci]\n").Environment!;
        var c = WorkspaceEnvFile.ParseYaml("setup: npm install\n").Environment!;
        var d = WorkspaceEnvFile.ParseYaml("toolchain: node\nsetup: npm ci\n").Environment!;
        Assert.True(a.Submodules);
        Assert.Equal(a.SetupHash, b.SetupHash);
        Assert.NotEqual(a.SetupHash, c.SetupHash);
        Assert.NotEqual(a.SetupHash, d.SetupHash); // another toolchain: dependencies must be installed again
        Assert.Equal("", WorkspaceEnvFile.ParseYaml("ports: [3000]\n").Environment!.SetupHash);
    }

    [Theory]
    [InlineData("toolchain: cobol", "not a toolchain ByteBench knows")]
    [InlineData("toolchain: node@18", "only node@22")]
    [InlineData("toolchain: node\nimage: node:22", "either 'toolchain' or 'image'")]
    [InlineData("image: --privileged", "not a valid image name")]
    [InlineData("image: \"node:22; rm -rf /\"", "not a valid image name")]
    [InlineData("image: \"$(curl evil)\"", "not a valid image name")]
    [InlineData("image: -v/:/host", "not a valid image name")]
    [InlineData("frobnicate: 1", "Unknown key 'frobnicate'")]
    [InlineData("ports: [80]", "not a port from 1024")]
    [InlineData("ports: [13370]", "used by the IDE")]
    [InlineData("ports: [70000]", "not a port")]
    [InlineData("ports: [abc]", "not a port")]
    [InlineData("env:\n  LD_PRELOAD: /x.so", "reserved")]
    [InlineData("env:\n  PATH: /x", "reserved")]
    [InlineData("env:\n  GIT_ASKPASS: /x", "reserved")]
    [InlineData("env:\n  BB_WORKDIR: /x", "reserved")]
    [InlineData("env:\n  \"A B\": x", "not a valid variable name")]
    [InlineData("env:\n  A: \"line1\\nline2\"", "single line")]
    [InlineData("tasks:\n  \"bad name\": echo", "not a valid task name")]
    [InlineData("tasks:\n  run:\n    port: 3000", "'command' is required")]
    [InlineData("tasks:\n  run:\n    command: x\n    preview: true", "needs a 'port'")]
    [InlineData("tasks:\n  a:\n    command: x\n    port: 3000\n    preview: true\n  b:\n    command: y\n    port: 3001\n    preview: true", "only one task")]
    [InlineData("tasks:\n  run:\n    command: x\n    cmd: y", "unknown key 'cmd'")]
    [InlineData("setup: 5\nsetup2: x", "Unknown key 'setup2'")]
    [InlineData("services:\n  Bad_Name: redis", "not a valid service name")]
    [InlineData("services:\n  x: \"re dis\"", "not a valid image name")]
    [InlineData("services:\n  x: someapp:1", "give the port")]
    [InlineData("submodules: maybe", "true or false")]
    [InlineData("- just\n- a list", "must be a mapping")]
    [InlineData("a: [unclosed", "invalid YAML")]
    public void Invalid_files_are_refused_with_a_clear_message(string yaml, string expected)
    {
        var r = WorkspaceEnvFile.ParseYaml(yaml);
        Assert.False(r.Ok);
        Assert.Null(r.Environment);
        Assert.Contains(r.Errors, e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_problem_is_reported_not_only_the_first()
    {
        var r = WorkspaceEnvFile.ParseYaml("toolchain: cobol\nports: [80]\nenv:\n  PATH: x\n");
        Assert.Equal(3, r.Errors.Count);
    }

    [Fact]
    public void Size_and_count_limits_hold()
    {
        Assert.Contains("larger than 64 KB", WorkspaceEnvFile.ParseYaml("setup: " + new string('x', 70_000)).Errors[0]);
        Assert.Contains("too long", WorkspaceEnvFile.ParseYaml("setup: " + new string('x', 5000)).Errors[0]);
        var many = "setup:\n" + string.Concat(Enumerable.Range(0, 21).Select(i => $"  - cmd{i}\n"));
        Assert.Contains(WorkspaceEnvFile.ParseYaml(many).Errors, e => e.Contains("too many commands"));
        var tasks = "tasks:\n" + string.Concat(Enumerable.Range(0, 21).Select(i => $"  t{i}: echo\n"));
        Assert.Contains(WorkspaceEnvFile.ParseYaml(tasks).Errors, e => e.Contains("too many tasks"));
        Assert.Contains(WorkspaceEnvFile.ParseYaml("").Errors, e => e.Contains("empty"));
    }

    [Fact]
    public void Commands_are_kept_exactly_as_written()
    {
        // Nothing is interpolated or escaped: the container's shell sees what the author wrote.
        var cmd = "echo \"$HOME\" && cat <<'EOF'\n$(whoami) `id` ; rm -rf /\nEOF";
        var r = WorkspaceEnvFile.ParseYaml("tasks:\n  t:\n    command: |\n      " + cmd.Replace("\n", "\n      ") + "\n");
        Assert.True(r.Ok, string.Join("\n", r.Errors));
        Assert.Equal(cmd, r.Environment!.Tasks[0].Command);
    }

    [Fact]
    public void Toolchains_come_from_the_curated_list_only()
    {
        Assert.Equal("mcr.microsoft.com/dotnet/sdk:10.0", WorkspaceToolchains.Resolve("dotnet@10", out _)!.BaseImage);
        Assert.Equal("node:22-bookworm", WorkspaceToolchains.Resolve("Node", out _)!.BaseImage);
        Assert.NotNull(WorkspaceToolchains.Resolve("python@3.13", out _));
        Assert.NotNull(WorkspaceToolchains.Resolve("go", out _));
        Assert.NotNull(WorkspaceToolchains.Resolve("java@21", out _));
        Assert.NotNull(WorkspaceToolchains.Resolve("rust", out _));
        Assert.Null(WorkspaceToolchains.Resolve("node@22; evil", out var versionError));
        Assert.Contains("only node@22", versionError);
        Assert.Null(WorkspaceToolchains.Resolve("node;evil", out var error));
        Assert.Contains("not a toolchain", error);
        Assert.All(WorkspaceToolchains.All, t => Assert.True(WorkspaceEnvFile.IsValidImage(t.BaseImage), t.BaseImage));
    }

    [Theory]
    [InlineData("node:22-bookworm", true)]
    [InlineData("ghcr.io/org/dev-env:1.2.3", true)]
    [InlineData("registry.local:5000/team/img:tag", true)]
    [InlineData("mcr.microsoft.com/dotnet/sdk@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("UPPER/case", false)]
    [InlineData("a b", false)]
    [InlineData("-x", false)]
    [InlineData("img;ls", false)]
    [InlineData("img$(x)", false)]
    [InlineData("img\nx", false)]
    [InlineData("", false)]
    public void Image_names_are_validated(string image, bool ok) => Assert.Equal(ok, WorkspaceEnvFile.IsValidImage(image));

    // ---------- devcontainer.json ----------

    [Fact]
    public void A_devcontainer_json_contributes_its_useful_subset_and_says_what_it_ignores()
    {
        const string json = """
            {
              // comments and trailing commas are fine in devcontainer.json
              "name": "x",
              "image": "mcr.microsoft.com/devcontainers/typescript-node:22",
              "forwardPorts": [3000, "5173", "db:5432"],
              "postCreateCommand": "npm install",
              "containerEnv": { "NODE_ENV": "development", "FROM_HOST": "${localEnv:HOME}" },
              "features": { "ghcr.io/devcontainers/features/docker-in-docker:2": {} },
              "runArgs": ["--privileged"],
              "customizations": { "vscode": { "extensions": ["a.b"] } },
            }
            """;
        var r = WorkspaceEnvFile.ParseDevcontainer(json, ".devcontainer/devcontainer.json");
        Assert.True(r.Ok, string.Join("\n", r.Errors));
        var e = r.Environment!;
        Assert.Equal("mcr.microsoft.com/devcontainers/typescript-node:22", e.Image);
        Assert.Equal([3000, 5173], e.Ports);
        Assert.Equal(["npm install"], e.Setup);
        Assert.Equal("development", e.Env["NODE_ENV"]);
        Assert.False(e.Env.ContainsKey("FROM_HOST"));
        Assert.Contains(r.Notes, n => n.Contains("features") && n.Contains("doesn't install"));
        Assert.Contains(r.Notes, n => n.Contains("runArgs") && n.Contains("ignored"));
        Assert.Contains(r.Notes, n => n.Contains("db:5432"));
        Assert.Contains(r.Notes, n => n.Contains("FROM_HOST"));
    }

    [Fact]
    public void Devcontainer_commands_can_be_lists_or_objects_and_are_quoted_safely()
    {
        var r = WorkspaceEnvFile.ParseDevcontainer("{ \"postCreateCommand\": { \"a\": \"make\", \"b\": [\"echo\", \"it's; fine\"] } }", "devcontainer.json");
        Assert.True(r.Ok);
        Assert.Equal(["make", "echo 'it'\\''s; fine'"], r.Environment!.Setup);
    }

    [Fact]
    public void A_devcontainer_that_builds_a_dockerfile_is_not_built()
    {
        var r = WorkspaceEnvFile.ParseDevcontainer("{ \"build\": { \"dockerfile\": \"Dockerfile\" } }", ".devcontainer.json");
        Assert.True(r.Ok);
        Assert.Null(r.Environment!.Image);
        Assert.Contains(r.Notes, n => n.Contains("never builds a repository's Dockerfile"));
    }

    [Theory]
    [InlineData("{ \"image\": \"--rm\" }", "not a valid image name")]
    [InlineData("{ \"forwardPorts\": [80] }", "not a port")]
    [InlineData("{ \"containerEnv\": { \"LD_PRELOAD\": \"x\" } }", "reserved")]
    [InlineData("[1]", "must be a JSON object")]
    [InlineData("{ nope", "invalid JSON")]
    public void Invalid_devcontainers_are_refused(string json, string expected)
    {
        var r = WorkspaceEnvFile.ParseDevcontainer(json, "devcontainer.json");
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    // ---------- detection ----------

    private static WorkspaceEnvironment? Suggest(string[] files, Dictionary<string, string>? contents = null) =>
        WorkspaceEnvironmentLoader.Suggest(files, name => contents != null && contents.TryGetValue(name, out var c) ? c : null);

    [Fact]
    public void A_node_project_gets_a_toolchain_setup_and_a_previewed_run_task()
    {
        var s = Suggest(["package.json", "package-lock.json", "README.md"],
            new() { ["package.json"] = "{ \"scripts\": { \"dev\": \"vite\", \"test\": \"vitest\", \"build\": \"vite build\" }, \"devDependencies\": { \"vite\": \"^5\" } }" })!;
        Assert.Equal("node@22", s.Toolchain);
        Assert.Equal(["npm ci"], s.Setup);
        var run = s.Tasks.Single(t => t.Name == "run");
        Assert.Equal("npm run dev", run.Command);
        Assert.Equal(5173, run.Port);
        Assert.True(run.Preview);
        Assert.Contains(s.Tasks, t => t is { Name: "test", Command: "npm test" });
        Assert.Contains(s.Tasks, t => t is { Name: "build" });
    }

    [Fact]
    public void A_dotnet_web_project_runs_on_a_port_and_a_library_does_not()
    {
        var web = Suggest(["App.sln", "App.csproj"], new() { ["App.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>" })!;
        Assert.Equal("dotnet@10", web.Toolchain);
        Assert.Equal(["dotnet restore"], web.Setup);
        Assert.Equal(5000, web.Tasks[0].Port);
        Assert.True(web.Tasks[0].Preview);
        var lib = Suggest(["Lib.csproj"], new() { ["Lib.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>" })!;
        Assert.Null(lib.Tasks[0].Port);
        Assert.False(lib.Tasks[0].Preview);
    }

    [Theory]
    [InlineData("requirements.txt", "python@3.13", "pip install -r requirements.txt")]
    [InlineData("pyproject.toml", "python@3.13", "pip install -e .")]
    [InlineData("go.mod", "go@1.24", "go mod download")]
    [InlineData("Cargo.toml", "rust@stable", "cargo fetch")]
    [InlineData("pom.xml", "java@21", "mvn -B -ntp dependency:resolve")]
    public void Other_ecosystems_are_recognised(string file, string toolchain, string setup)
    {
        var s = Suggest([file])!;
        Assert.Equal(toolchain, s.Toolchain);
        Assert.Contains(setup, s.Setup);
        Assert.NotEmpty(s.Tasks);
    }

    [Fact]
    public void Nothing_recognisable_suggests_nothing_and_mixed_repositories_say_so()
    {
        Assert.Null(Suggest(["README.md", "notes.txt"]));
        var mixed = Suggest(["App.sln", "package.json"])!;
        Assert.Equal("dotnet@10", mixed.Toolchain);
        Assert.Contains(mixed.Notes, n => n.Contains("package.json"));
    }

    [Fact]
    public void A_suggested_file_is_valid_and_round_trips()
    {
        foreach (var files in new[] { new[] { "package.json" }, ["App.csproj"], ["go.mod"], ["requirements.txt"], ["Cargo.toml"], ["pom.xml"], ["build.gradle", "gradlew"] })
        {
            var s = Suggest(files, new() { ["package.json"] = "{ \"scripts\": { \"start\": \"node x.js\" } }" })!;
            var yaml = WorkspaceEnvironmentLoader.ToYaml(s);
            var parsed = WorkspaceEnvFile.ParseYaml(yaml);
            Assert.True(parsed.Ok, $"{string.Join(',', files)}:\n{yaml}\n{string.Join('\n', parsed.Errors)}");
            Assert.Equal(s.Toolchain, parsed.Environment!.Toolchain);
            Assert.Equal(s.Setup, parsed.Environment.Setup);
            Assert.Equal(s.Tasks, parsed.Environment.Tasks);
        }
    }

    [Fact]
    public void Quoting_keeps_odd_commands_intact_in_the_generated_file()
    {
        var env = new WorkspaceEnvironment("(suggested)", "node@22", null, ["echo \"hi\" \\ done\ttab"], [new("t", "a: b # c\nd", null, false)], [], new Dictionary<string, string>(), true, [], []);
        var parsed = WorkspaceEnvFile.ParseYaml(WorkspaceEnvironmentLoader.ToYaml(env));
        Assert.True(parsed.Ok, string.Join('\n', parsed.Errors));
        Assert.Equal(env.Setup, parsed.Environment!.Setup);
        Assert.Equal(env.Tasks, parsed.Environment.Tasks);
    }
}
