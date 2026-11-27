using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ByteBench.Cli;

/// <summary>Process exit codes.</summary>
public static class ExitCodes
{
    public const int Ok = 0, Error = 1, Usage = 2, Auth = 3;
}

/// <summary>A failure the user should read: printed as <c>bytebench: message</c> and turned into an exit code.</summary>
public sealed class CliException(int exitCode, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
    public static CliException Usage(string message) => new(ExitCodes.Usage, message);
    public static CliException Failure(string message) => new(ExitCodes.Error, message);
    public static CliException Auth(string message) => new(ExitCodes.Auth, message);
}

/// <summary>Everything the CLI touches outside itself, so tests can run it in-process.</summary>
public sealed class CliEnvironment
{
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Err { get; init; } = Console.Error;
    public TextReader In { get; init; } = Console.In;
    /// <summary>Raw (binary) standard input and output, for commands that relay bytes (<c>workspace tunnel</c>). Tests pass their own streams.</summary>
    public Func<Stream> OpenRawInput { get; init; } = Console.OpenStandardInput;
    public Func<Stream> OpenRawOutput { get; init; } = Console.OpenStandardOutput;
    public bool InputRedirected { get; init; } = Console.IsInputRedirected;
    public bool OutputIsTerminal { get; init; } = !Console.IsOutputRedirected;
    public Func<string, string?> GetVariable { get; init; } = Environment.GetEnvironmentVariable;
    /// <summary>The config file: <c>~/.config/bytebench/config.json</c> unless a test says otherwise.</summary>
    public string ConfigPath { get; init; } = DefaultConfigPath();
    /// <summary>Builds the HTTP handler; tests return the in-memory test server's handler.</summary>
    public Func<HttpMessageHandler> CreateHandler { get; init; } = () => new SocketsHttpHandler
    {
        AllowAutoRedirect = false, // a redirect would carry the token to another host
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };

    public static string DefaultConfigPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "bytebench", "config.json");

    /// <summary>Colour only on a terminal, and never when NO_COLOR is set (https://no-color.org).</summary>
    public bool UseColor => OutputIsTerminal && string.IsNullOrEmpty(GetVariable("NO_COLOR")) && GetVariable("TERM") != "dumb";
}

/// <summary>Text helpers for terminal output.</summary>
public static class Text
{
    /// <summary>
    /// Server-supplied text with terminal control characters made harmless: an issue title can't carry an escape sequence
    /// that rewrites the screen. Newlines and tabs stay.
    /// </summary>
    public static string Safe(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == '\r') continue;
            sb.Append(char.IsControl(c) && c is not ('\n' or '\t') ? '?' : c);
        }
        return sb.ToString();
    }

    public static string Line(string? s) => Safe(s).Replace('\n', ' ').Replace('\t', ' ');

    public static string Cut(string? s, int max)
    {
        var line = Line(s);
        return line.Length <= max ? line : line[..(max - 1)] + "…";
    }

    public static void PrintTable(TextWriter w, string[] headers, List<string[]> rows)
    {
        var widths = headers.Select(h => h.Length).ToArray();
        foreach (var r in rows)
            for (var i = 0; i < r.Length; i++) widths[i] = Math.Max(widths[i], r[i].Length);
        void Row(string[] cells) => w.WriteLine(string.Join("  ", cells.Select((c, i) => i == cells.Length - 1 ? c : c.PadRight(widths[i]))).TrimEnd());
        Row(headers);
        foreach (var r in rows) Row(r);
    }
}

/// <summary>ANSI colour for status words, only when the environment allows it.</summary>
public sealed class Style(bool enabled)
{
    public string Paint(string text, string status)
    {
        if (!enabled) return text;
        var code = status.ToLowerInvariant() switch
        {
            "done" or "passed" or "merged" or "approved" => "32",
            "failed" or "error" or "declined" or "changeschanged" => "31",
            "inprogress" or "running" or "open" => "33",
            _ => "",
        };
        return code.Length == 0 ? text : $"\u001b[{code}m{text}\u001b[0m";
    }
}

/// <summary>Json helpers over <see cref="JsonNode"/>.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string S(this JsonNode? n, string name) => n?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    public static string S(this JsonNode? n, string a, string b) => n?[a]?[b] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    public static int I(this JsonNode? n, string name) => n?[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
    public static bool B(this JsonNode? n, string name) => n?[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    public static IEnumerable<JsonNode?> Items(this JsonNode? n) => n is JsonArray a ? a : [];

    public static string Pretty(JsonNode? n) => n?.ToJsonString(Options) ?? "null";
}
