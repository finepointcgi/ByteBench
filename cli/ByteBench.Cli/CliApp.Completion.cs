using System.Reflection;

namespace ByteBench.Cli;

/// <summary><c>bytebench --version</c> and <c>bytebench completion bash|zsh|fish</c>.</summary>
public sealed partial class CliApp
{
    /// <summary>
    /// The package version (<c>Version</c> in the project, set from the release tag), without the source-revision suffix
    /// the SDK appends to the informational version.
    /// </summary>
    public static string VersionText
    {
        get
        {
            var asm = typeof(CliApp).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(info)) return info.Split('+')[0];
            return asm.GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }

    /// <summary>The words completion offers: commands and their subcommands.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> CompletionCommands = new Dictionary<string, string[]>
    {
        ["login"] = [], ["logout"] = [], ["whoami"] = [], ["projects"] = [], ["open"] = [],
        ["issues"] = ["list", "show", "create", "comment", "move", "assign"],
        ["prs"] = ["list", "show"],
        ["repos"] = ["list"],
        ["pipelines"] = ["list", "logs"],
        ["workspace"] = ["connect", "tunnel"],
        ["completion"] = ["bash", "zsh", "fish"],
    };

    private int Completion()
    {
        args.Allow();
        if (args.Words.Count != 2) throw CliException.Usage("Usage: bytebench completion bash|zsh|fish");
        var shell = args.Words[1].ToLowerInvariant();
        var options = string.Join(' ', ParsedArgs.Known.Keys.Select(k => "--" + k));
        var commands = string.Join(' ', CompletionCommands.Keys);
        string script = shell switch
        {
            "bash" => Bash(commands, options),
            "zsh" => Zsh(commands, options),
            "fish" => Fish(options),
            _ => throw CliException.Usage($"Unknown shell '{Text.Line(shell)}': use bash, zsh or fish."),
        };
        env.Out.Write(script);
        return ExitCodes.Ok;
    }

    private static string SubcommandCases(string format) =>
        string.Concat(CompletionCommands.Where(c => c.Value.Length > 0).Select(c => string.Format(format, c.Key, string.Join(' ', c.Value))));

    private static string Bash(string commands, string options) =>
        "# bytebench bash completion. Load it with: source <(bytebench completion bash)\n" +
        "_bytebench() {\n" +
        "  local cur=\"${COMP_WORDS[COMP_CWORD]}\"\n" +
        "  if [[ \"$cur\" == --* ]]; then COMPREPLY=($(compgen -W \"" + options + "\" -- \"$cur\")); return; fi\n" +
        "  if [[ $COMP_CWORD -eq 1 ]]; then COMPREPLY=($(compgen -W \"" + commands + "\" -- \"$cur\")); return; fi\n" +
        "  if [[ $COMP_CWORD -eq 2 ]]; then\n" +
        "    case \"${COMP_WORDS[1]}\" in\n" +
        SubcommandCases("      {0}) COMPREPLY=($(compgen -W \"{1}\" -- \"$cur\")) ;;\n") +
        "    esac\n" +
        "  fi\n" +
        "}\n" +
        "complete -F _bytebench bytebench\n";

    private static string Zsh(string commands, string options) =>
        "#compdef bytebench\n" +
        "# bytebench zsh completion. Load it with: source <(bytebench completion zsh)\n" +
        "_bytebench() {\n" +
        "  if [[ \"$words[CURRENT]\" == --* ]]; then compadd -- " + options + "; return; fi\n" +
        "  if (( CURRENT == 2 )); then compadd -- " + commands + "; return; fi\n" +
        "  if (( CURRENT == 3 )); then\n" +
        "    case \"$words[2]\" in\n" +
        SubcommandCases("      {0}) compadd -- {1} ;;\n") +
        "    esac\n" +
        "  fi\n" +
        "}\n" +
        "if (( $+functions[compdef] )); then compdef _bytebench bytebench; fi\n";

    private static string Fish(string options)
    {
        var sb = new System.Text.StringBuilder("# bytebench fish completion. Load it with: bytebench completion fish | source\n");
        sb.Append("complete -c bytebench -f\n");
        foreach (var (command, subs) in CompletionCommands)
        {
            sb.Append($"complete -c bytebench -n __fish_use_subcommand -a {command}\n");
            if (subs.Length > 0)
                sb.Append($"complete -c bytebench -n '__fish_seen_subcommand_from {command}' -a '{string.Join(' ', subs)}'\n");
        }
        foreach (var o in options.Split(' ')) sb.Append($"complete -c bytebench -l {o[2..]}\n");
        return sb.ToString();
    }
}
