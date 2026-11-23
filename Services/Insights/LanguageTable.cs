namespace ByteBench.Services.Insights;

/// <summary>
/// The language breakdown's rules. A file counts for the language of its extension (below; the file name for a few like
/// <c>Dockerfile</c>); files with another extension are not counted at all. Dependency directories, lockfiles and
/// generated files are left out of both the language sizes and the additions/deletions of contributors and code frequency.
/// </summary>
public static class LanguageTable
{
    public sealed record Language(string Name, string Color);

    private static Language L(string n, string c) => new(n, c);

    /// <summary>Extension (lower case, with the dot) → language.</summary>
    public static readonly IReadOnlyDictionary<string, Language> ByExtension = new Dictionary<string, Language>(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = L("C#", "#2f8f4e"), [".csx"] = L("C#", "#2f8f4e"), [".razor"] = L("Razor", "#5b6ee1"), [".cshtml"] = L("Razor", "#5b6ee1"),
        [".fs"] = L("F#", "#5d8aa8"), [".vb"] = L("Visual Basic", "#7a5ea8"),
        [".js"] = L("JavaScript", "#d4a72c"), [".mjs"] = L("JavaScript", "#d4a72c"), [".cjs"] = L("JavaScript", "#d4a72c"), [".jsx"] = L("JavaScript", "#d4a72c"),
        [".ts"] = L("TypeScript", "#3178c6"), [".tsx"] = L("TypeScript", "#3178c6"),
        [".py"] = L("Python", "#3572a5"), [".pyi"] = L("Python", "#3572a5"),
        [".go"] = L("Go", "#00a7c4"), [".rs"] = L("Rust", "#b7623a"), [".rb"] = L("Ruby", "#c13b3b"), [".php"] = L("PHP", "#7a86b8"),
        [".java"] = L("Java", "#b07219"), [".kt"] = L("Kotlin", "#a97bff"), [".kts"] = L("Kotlin", "#a97bff"), [".scala"] = L("Scala", "#c22d40"),
        [".swift"] = L("Swift", "#f05138"), [".m"] = L("Objective-C", "#438eff"), [".mm"] = L("Objective-C", "#438eff"),
        [".c"] = L("C", "#6b6b6b"), [".h"] = L("C", "#6b6b6b"), [".cpp"] = L("C++", "#f34b7d"), [".cc"] = L("C++", "#f34b7d"),
        [".cxx"] = L("C++", "#f34b7d"), [".hpp"] = L("C++", "#f34b7d"), [".hh"] = L("C++", "#f34b7d"),
        [".dart"] = L("Dart", "#00b4ab"), [".lua"] = L("Lua", "#000080"), [".pl"] = L("Perl", "#0298c3"), [".r"] = L("R", "#198ce7"),
        [".ex"] = L("Elixir", "#6e4a7e"), [".exs"] = L("Elixir", "#6e4a7e"), [".erl"] = L("Erlang", "#b83998"), [".hs"] = L("Haskell", "#5e5086"),
        [".clj"] = L("Clojure", "#db5855"), [".zig"] = L("Zig", "#ec915c"), [".gd"] = L("GDScript", "#355570"), [".jl"] = L("Julia", "#a270ba"),
        [".sh"] = L("Shell", "#4eaa25"), [".bash"] = L("Shell", "#4eaa25"), [".zsh"] = L("Shell", "#4eaa25"), [".ps1"] = L("PowerShell", "#3d6aa3"),
        [".bat"] = L("Batchfile", "#c1f12e"), [".cmd"] = L("Batchfile", "#c1f12e"), [".sql"] = L("SQL", "#e38c00"),
        [".html"] = L("HTML", "#e34c26"), [".htm"] = L("HTML", "#e34c26"), [".vue"] = L("Vue", "#41b883"), [".svelte"] = L("Svelte", "#ff3e00"),
        [".css"] = L("CSS", "#563d7c"), [".scss"] = L("SCSS", "#c6538c"), [".sass"] = L("SCSS", "#c6538c"), [".less"] = L("Less", "#1d365d"),
        [".json"] = L("JSON", "#8a8a8a"), [".yml"] = L("YAML", "#cb171e"), [".yaml"] = L("YAML", "#cb171e"), [".toml"] = L("TOML", "#9c4221"),
        [".xml"] = L("XML", "#0060ac"), [".csproj"] = L("XML", "#0060ac"), [".props"] = L("XML", "#0060ac"), [".targets"] = L("XML", "#0060ac"),
        [".md"] = L("Markdown", "#083fa1"), [".markdown"] = L("Markdown", "#083fa1"), [".rst"] = L("reStructuredText", "#6a7b8a"), [".tex"] = L("TeX", "#3d6117"),
        [".tf"] = L("HCL", "#844fba"), [".proto"] = L("Protocol Buffers", "#4a90a4"), [".graphql"] = L("GraphQL", "#e10098"),
        [".dockerfile"] = L("Dockerfile", "#384d54"),
    };

    private static readonly Dictionary<string, Language> ByFileName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dockerfile"] = L("Dockerfile", "#384d54"), ["Makefile"] = L("Makefile", "#427819"), ["CMakeLists.txt"] = L("CMake", "#da3434"),
    };

    /// <summary>Directory names whose contents are vendored or build output.</summary>
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "vendor", "third_party", "third-party", "bower_components", "Pods", "Carthage", ".yarn", "site-packages",
        "bin", "obj", "dist", "out", "target", "__pycache__", ".git",
    };

    private static readonly HashSet<string> LockFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json", "npm-shrinkwrap.json", "yarn.lock", "pnpm-lock.yaml", "bun.lockb", "composer.lock", "Gemfile.lock", "Cargo.lock",
        "poetry.lock", "Pipfile.lock", "go.sum", "packages.lock.json", "paket.lock", "mix.lock", "pubspec.lock", "Podfile.lock", "flake.lock",
    };

    private static readonly string[] GeneratedSuffixes =
    [
        ".min.js", ".min.css", ".bundle.js", ".map", ".designer.cs", ".g.cs", ".g.i.cs", ".generated.cs", ".pb.go", "_pb2.py", "_pb2_grpc.py",
        ".pb.cc", ".pb.h", ".snap",
    ];

    /// <summary>True for lockfiles, vendored and build directories and generated files.</summary>
    public static bool IsExcluded(string path)
    {
        var p = path.Replace('\\', '/');
        var segments = p.Split('/');
        var name = segments[^1];
        if (LockFileNames.Contains(name) || name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) return true;
        for (var i = 0; i < segments.Length - 1; i++)
            if (ExcludedDirectories.Contains(segments[i])) return true;
        if (p.Contains("/wwwroot/lib/", StringComparison.OrdinalIgnoreCase) || p.StartsWith("wwwroot/lib/", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var s in GeneratedSuffixes)
            if (name.EndsWith(s, StringComparison.OrdinalIgnoreCase)) return true;
        return name.Contains(".generated.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The language of a path, or null if its extension isn't in the table.</summary>
    public static Language? Of(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (ByFileName.TryGetValue(name, out var byName)) return byName;
        var dot = name.LastIndexOf('.');
        return dot >= 0 && ByExtension.TryGetValue(name[dot..], out var lang) ? lang : null;
    }
}
