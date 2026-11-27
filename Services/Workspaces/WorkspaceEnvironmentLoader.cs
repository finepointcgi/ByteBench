using System.Text;
using System.Text.Json;
using ByteBench.Services.Git;

namespace ByteBench.Services.Workspaces;

/// <summary>What a repository's root looks like to a workspace: its environment file (if any) and, without one, a suggested file.</summary>
public sealed record WorkspaceEnvLoad(WorkspaceEnvResult? Result, WorkspaceEnvironment? Suggestion, string? SuggestedYaml)
{
    /// <summary>The environment to use: the file's, or an empty one (with the problems still reported through <see cref="Result"/>).</summary>
    public WorkspaceEnvironment Environment => Result?.Environment ?? WorkspaceEnvironment.Empty;
    public bool HasFile => Result != null;
}

/// <summary>Finds and parses a repository's workspace environment at a revision, and suggests one for repositories without a file.</summary>
public static class WorkspaceEnvironmentLoader
{
    /// <summary>Reads the first candidate file that exists at <paramref name="rev"/> of the repository at <paramref name="repoPath"/>.</summary>
    public static WorkspaceEnvLoad Load(string repoPath, string rev)
    {
        foreach (var candidate in WorkspaceEnvFile.Candidates)
        {
            var blob = GitOps.GetBlob(repoPath, rev, candidate);
            if (blob == null) continue;
            if (blob.TooLarge || blob.IsBinary || blob.Text == null)
                return new WorkspaceEnvLoad(WorkspaceEnvResult.Failure(candidate, $"{candidate} is too large or isn't a text file."), null, null);
            return new WorkspaceEnvLoad(WorkspaceEnvFile.Parse(candidate, blob.Text), null, null);
        }
        var suggestion = Suggest(repoPath, rev);
        return new WorkspaceEnvLoad(null, suggestion, suggestion == null ? null : ToYaml(suggestion));
    }

    /// <summary>Parses a file read from a checkout (a path from <see cref="WorkspaceEnvFile.Candidates"/> and its text).</summary>
    public static WorkspaceEnvResult ParseCandidate(string path, string text) => WorkspaceEnvFile.Parse(path, text);

    // ---------- detection ----------

    public static WorkspaceEnvironment? Suggest(string repoPath, string rev)
    {
        var root = GitOps.ListTree(repoPath, rev, "", withLastCommit: false);
        if (root == null) return null;
        var names = root.Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
        return Suggest(names, name => names.Contains(name) ? GitOps.GetBlob(repoPath, rev, name)?.Text : null);
    }

    /// <summary>Suggests a toolchain, setup and tasks from the file names in the repository root. Null when nothing is recognised.</summary>
    public static WorkspaceEnvironment? Suggest(IReadOnlyList<string> rootFiles, Func<string, string?> read)
    {
        bool Has(string n) => rootFiles.Contains(n, StringComparer.Ordinal);
        bool Ext(string e) => rootFiles.Any(f => f.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        var notes = new List<string>();
        var found = new List<string>();
        string? toolchain = null;
        var setup = new List<string>();
        var tasks = new List<WorkspaceTaskDefinition>();

        if (Ext(".sln") || Ext(".slnx") || Ext(".csproj") || Ext(".fsproj"))
        {
            found.Add("a .NET solution/project");
            toolchain = "dotnet@10";
            setup.Add("dotnet restore");
            tasks.Add(new("build", "dotnet build --no-restore", null, false));
            tasks.Add(new("test", "dotnet test --no-restore", null, false));
            var proj = rootFiles.FirstOrDefault(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
            var isWeb = proj != null && read(proj)?.Contains("Microsoft.NET.Sdk.Web", StringComparison.Ordinal) == true;
            if (isWeb) tasks.Insert(0, new("run", "dotnet run --no-restore --urls http://0.0.0.0:5000", 5000, true));
            else if (proj != null) tasks.Insert(0, new("run", $"dotnet run --no-restore --project {WorkspaceEnvFile.ShellQuote(proj)}", null, false));
        }
        if (Has("package.json"))
        {
            found.Add("package.json");
            var (scripts, deps) = ReadPackageJson(read("package.json"));
            if (toolchain == null)
            {
                toolchain = "node@22";
                setup.Add(Has("pnpm-lock.yaml") ? "corepack enable && pnpm install --frozen-lockfile" : Has("yarn.lock") ? "corepack enable && yarn install --immutable" : Has("package-lock.json") ? "npm ci" : "npm install");
                var runner = Has("pnpm-lock.yaml") ? "pnpm" : Has("yarn.lock") ? "yarn" : "npm";
                string Script(string s) => runner == "npm" ? $"npm run {s}" : $"{runner} {s}";
                var dev = scripts.Contains("dev") ? "dev" : scripts.Contains("start") ? "start" : null;
                if (dev != null)
                {
                    var port = deps.Contains("vite") ? 5173 : deps.Contains("next") ? 3000 : deps.Contains("@angular/cli") ? 4200 : 3000;
                    tasks.Add(new("run", dev == "start" && runner == "npm" ? "npm start" : Script(dev), port, true));
                }
                if (scripts.Contains("test")) tasks.Add(new("test", runner == "npm" ? "npm test" : $"{runner} test", null, false));
                if (scripts.Contains("build")) tasks.Add(new("build", Script("build"), null, false));
            }
        }
        if (Has("pyproject.toml") || Has("requirements.txt") || Has("setup.py") || Has("Pipfile"))
        {
            found.Add("a Python project");
            if (toolchain == null)
            {
                toolchain = "python@3.13";
                setup.Add(Has("requirements.txt") ? "pip install -r requirements.txt" : Has("pyproject.toml") || Has("setup.py") ? "pip install -e ." : "pip install pipenv && pipenv install --dev");
                if (Has("manage.py")) tasks.Add(new("run", "python manage.py runserver 0.0.0.0:8000", 8000, true));
                else if ((read("requirements.txt") ?? "").Contains("flask", StringComparison.OrdinalIgnoreCase)) tasks.Add(new("run", "flask run --host 0.0.0.0 --port 5000", 5000, true));
                tasks.Add(new("test", "python -m pytest", null, false));
            }
        }
        if (Has("go.mod"))
        {
            found.Add("go.mod");
            if (toolchain == null)
            {
                toolchain = "go@1.24";
                setup.Add("go mod download");
                tasks.Add(new("run", "go run .", null, false));
                tasks.Add(new("build", "go build ./...", null, false));
                tasks.Add(new("test", "go test ./...", null, false));
            }
        }
        if (Has("Cargo.toml"))
        {
            found.Add("Cargo.toml");
            if (toolchain == null)
            {
                toolchain = "rust@stable";
                setup.Add("cargo fetch");
                tasks.Add(new("run", "cargo run", null, false));
                tasks.Add(new("build", "cargo build", null, false));
                tasks.Add(new("test", "cargo test", null, false));
            }
        }
        if (Has("pom.xml") || Has("build.gradle") || Has("build.gradle.kts"))
        {
            found.Add("a Java project");
            if (toolchain == null)
            {
                toolchain = "java@21";
                if (Has("pom.xml"))
                {
                    setup.Add("mvn -B -ntp dependency:resolve");
                    tasks.Add(new("build", "mvn -B -ntp package -DskipTests", null, false));
                    tasks.Add(new("test", "mvn -B -ntp test", null, false));
                }
                else
                {
                    var g = Has("gradlew") ? "./gradlew" : "gradle";
                    tasks.Add(new("build", $"{g} build -x test", null, false));
                    tasks.Add(new("test", $"{g} test", null, false));
                }
            }
        }
        if (toolchain == null) return null;
        if (found.Count > 1) notes.Add($"Also found {string.Join(", ", found.Skip(1))}; add their setup and tasks if you use them.");
        return new WorkspaceEnvironment("(suggested)", toolchain, null, setup, tasks, [], new Dictionary<string, string>(), true, [], notes);
    }

    private static (HashSet<string> Scripts, HashSet<string> Dependencies) ReadPackageJson(string? text)
    {
        var scripts = new HashSet<string>(StringComparer.Ordinal);
        var deps = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return (scripts, deps);
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 16 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (scripts, deps);
            if (doc.RootElement.TryGetProperty("scripts", out var s) && s.ValueKind == JsonValueKind.Object) foreach (var p in s.EnumerateObject()) scripts.Add(p.Name);
            foreach (var section in new[] { "dependencies", "devDependencies" })
                if (doc.RootElement.TryGetProperty(section, out var d) && d.ValueKind == JsonValueKind.Object) foreach (var p in d.EnumerateObject()) deps.Add(p.Name);
        }
        catch (JsonException) { }
        return (scripts, deps);
    }

    // ---------- YAML text ----------

    /// <summary>Writes an environment as a .bytebench/workspace.yml (the one-click "Add" button commits this text).</summary>
    public static string ToYaml(WorkspaceEnvironment e)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ByteBench workspace environment. Docs: /docs/workspaces");
        sb.AppendLine("# Commands run in the workspace container exactly as written; secrets belong in workspace secrets, not here.");
        foreach (var n in e.Notes) sb.AppendLine("# " + n);
        if (e.Toolchain != null) sb.AppendLine($"toolchain: {e.Toolchain}");
        if (e.Image != null) sb.AppendLine($"image: {e.Image}");
        if (e.Setup.Count > 0)
        {
            sb.AppendLine("setup:");
            foreach (var s in e.Setup) sb.AppendLine("  - " + Quote(s));
        }
        if (e.Tasks.Count > 0)
        {
            sb.AppendLine("tasks:");
            foreach (var t in e.Tasks)
            {
                sb.AppendLine($"  {t.Name}:");
                sb.AppendLine($"    command: {Quote(t.Command)}");
                if (t.Port != null) sb.AppendLine($"    port: {t.Port}");
                if (t.Preview) sb.AppendLine("    preview: true");
            }
        }
        if (e.Ports.Count > 0) sb.AppendLine($"ports: [{string.Join(", ", e.Ports)}]");
        if (e.Env.Count > 0)
        {
            sb.AppendLine("env:");
            foreach (var (k, v) in e.Env) sb.AppendLine($"  {k}: {Quote(v)}");
        }
        if (!e.Submodules) sb.AppendLine("submodules: false");
        if (e.Services.Count > 0)
        {
            sb.AppendLine("services:");
            foreach (var s in e.Services)
            {
                sb.AppendLine($"  {s.Name}:");
                sb.AppendLine($"    image: {s.Image}");
                if (s.Env.Count > 0)
                {
                    sb.AppendLine("    env:");
                    foreach (var (k, v) in s.Env) sb.AppendLine($"      {k}: {Quote(v)}");
                }
                sb.AppendLine($"    ports: [{string.Join(", ", s.Ports)}]");
            }
        }
        return sb.ToString();
    }

    /// <summary>A YAML double-quoted scalar.</summary>
    private static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: if (c < 0x20) sb.Append($"\\x{(int)c:X2}"); else sb.Append(c); break;
            }
        }
        return sb.Append('"').ToString();
    }
}
