using System.Security.Cryptography;
using System.Text;
using YamlDotNet.Serialization;

namespace ByteBench.Services.Workspaces;

/// <summary>What a prebuild runs, read from the repository's own workspace environment file.</summary>
/// <param name="Setup">Shell commands, run one after another in the repository (the build stops at the first that fails).</param>
/// <param name="Submodules">Fetch git submodules (recursively) before the setup commands.</param>
/// <param name="Hash">Identifies this configuration: a prebuild is only used for a commit whose configuration has the same hash.</param>
public sealed record PrebuildConfig(IReadOnlyList<string> Setup, bool Submodules, string Hash);

public sealed record PrebuildConfigResult(PrebuildConfig? Config, string? Error);

/// <summary>
/// Reads the part of <c>.bytebench/workspace.yml</c> that prebuilds need: <c>setup:</c> (a command or a list of commands) and
/// <c>prebuild.submodules</c> (default true). Only the repository's own file at the pushed commit is used, never anything from outside it.
/// </summary>
public static class PrebuildConfigReader
{
    public const string FileName = ".bytebench/workspace.yml";
    public const int MaxFileBytes = 64 * 1024, MaxCommands = 50, MaxCommandChars = 4000;

    public static PrebuildConfigResult Parse(string yaml)
    {
        object? root;
        try { root = new DeserializerBuilder().Build().Deserialize<object?>(yaml); }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or InvalidOperationException)
        {
            return new PrebuildConfigResult(null, FileName + " isn't valid YAML: " + FirstLine(ex.InnerException?.Message ?? ex.Message));
        }
        if (root == null) return Done([], true);
        if (root is not IDictionary<object, object> map) return new PrebuildConfigResult(null, FileName + " must be a mapping.");

        var setup = new List<string>();
        if (Get(map, "setup") is { } s)
        {
            switch (s)
            {
                case string one: setup.Add(one); break;
                case IEnumerable<object> list:
                    foreach (var item in list)
                    {
                        if (item is not string cmd) return new PrebuildConfigResult(null, FileName + ": every entry of setup must be a command (text).");
                        setup.Add(cmd);
                    }
                    break;
                default: return new PrebuildConfigResult(null, FileName + ": setup must be a command or a list of commands.");
            }
        }
        setup = setup.Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
        if (setup.Count > MaxCommands) return new PrebuildConfigResult(null, $"{FileName}: at most {MaxCommands} setup commands.");
        if (setup.Any(c => c.Length > MaxCommandChars)) return new PrebuildConfigResult(null, $"{FileName}: a setup command is longer than {MaxCommandChars} characters.");

        var submodules = true;
        if (Get(map, "prebuild") is IDictionary<object, object> pb && Get(pb, "submodules") is { } sm)
        {
            if (!bool.TryParse(sm.ToString(), out submodules)) return new PrebuildConfigResult(null, FileName + ": prebuild.submodules must be true or false.");
        }
        return Done(setup, submodules);
    }

    private static PrebuildConfigResult Done(List<string> setup, bool submodules)
    {
        var canonical = (submodules ? "submodules\n" : "no-submodules\n") + string.Join("\n---\n", setup);
        return new PrebuildConfigResult(new PrebuildConfig(setup, submodules, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()), null);
    }

    private static object? Get(IDictionary<object, object> map, string key) => map.TryGetValue(key, out var v) ? v : null;

    private static string FirstLine(string s) => s.Split('\n')[0].Trim();
}
