namespace ByteBench.Cli;

/// <summary>The command line split into words and options. Options are <c>--name value</c>, <c>--name=value</c> or flags.</summary>
public sealed class ParsedArgs
{
    /// <summary>Every option bytebench knows, and whether it takes a value. A command may use only some of them.</summary>
    public static readonly IReadOnlyDictionary<string, bool> Known = new Dictionary<string, bool>
    {
        ["json"] = false, ["insecure"] = false, ["none"] = false, ["help"] = false, ["version"] = false, ["verbose"] = false,
        ["server"] = true, ["project"] = true, ["query"] = true, ["status"] = true, ["limit"] = true, ["title"] = true,
        ["description"] = true, ["description-file"] = true, ["body"] = true, ["body-file"] = true, ["type"] = true,
        ["priority"] = true, ["assignee"] = true, ["label"] = true, ["to"] = true, ["state"] = true, ["branch"] = true,
        ["step"] = true, ["tail"] = true,
    };

    public List<string> Words { get; } = [];
    private readonly Dictionary<string, List<string>> options = new(StringComparer.Ordinal);

    public bool Flag(string name) => options.ContainsKey(name);
    public string? Opt(string name) => options.TryGetValue(name, out var v) ? v[^1] : null;
    public IReadOnlyList<string> All(string name) => options.TryGetValue(name, out var v) ? v : [];
    public IEnumerable<string> Names => options.Keys;

    public static ParsedArgs Parse(string[] args)
    {
        var result = new ParsedArgs();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--") { result.Words.AddRange(args.Skip(i + 1)); break; }
            if (a == "-h") a = "--help";
            if (!a.StartsWith("--", StringComparison.Ordinal) || a.Length == 2)
            {
                result.Words.Add(a);
                continue;
            }
            var name = a[2..];
            string? value = null;
            var eq = name.IndexOf('=');
            if (eq >= 0) (name, value) = (name[..eq], name[(eq + 1)..]);
            if (!Known.TryGetValue(name, out var takesValue)) throw CliException.Usage($"Unknown option --{name}. Run 'bytebench --help'.");
            if (takesValue)
            {
                if (value == null)
                {
                    if (i + 1 >= args.Length) throw CliException.Usage($"--{name} needs a value.");
                    value = args[++i];
                }
            }
            else if (value != null) throw CliException.Usage($"--{name} doesn't take a value.");
            if (!result.options.TryGetValue(name, out var list)) result.options[name] = list = [];
            list.Add(value ?? "");
        }
        return result;
    }

    /// <summary>Fails with a usage error when an option isn't one of <paramref name="allowed"/> (plus the global --json, --help, --insecure).</summary>
    public void Allow(params string[] allowed)
    {
        foreach (var n in Names)
            if (n is not ("json" or "help" or "insecure" or "version") && !allowed.Contains(n))
                throw CliException.Usage($"--{n} isn't used by this command. Run it with --help.");
    }

    public string Required(string name) => Opt(name) is { Length: > 0 } v ? v : throw CliException.Usage($"--{name} is required.");

    public int Limit(int fallback, int max = 100)
    {
        if (Opt("limit") is not { } raw) return fallback;
        return int.TryParse(raw, out var n) && n >= 1 && n <= max ? n : throw CliException.Usage($"--limit must be a number from 1 to {max}.");
    }
}
