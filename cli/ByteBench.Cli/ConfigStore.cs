using System.Text.Json;
using System.Text.Json.Serialization;

namespace ByteBench.Cli;

/// <summary>What <c>bytebench login</c> stores.</summary>
public sealed class CliConfig
{
    [JsonPropertyName("server")] public string Server { get; set; } = "";
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    /// <summary>The user passed --insecure at login: plain HTTP to a non-local server is accepted.</summary>
    [JsonPropertyName("insecure")] public bool Insecure { get; set; }
}

/// <summary>
/// The config file (<c>~/.config/bytebench/config.json</c>), which holds the access token: created with mode 0600 in a 0700
/// directory, written atomically, never through a symbolic link, and not read when other users could read it.
/// </summary>
public sealed class ConfigStore(string path)
{
    public string Path { get; } = path;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public CliConfig? Load()
    {
        if (!File.Exists(Path) && !IsLink(Path)) return null;
        RefuseLink(Path);
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(Path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                                                              | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw CliException.Failure($"{Path} can be read by other users. Run: chmod 600 \"{Path}\" (or log in again).");
        try { return JsonSerializer.Deserialize<CliConfig>(File.ReadAllText(Path)); }
        catch (JsonException) { throw CliException.Failure($"{Path} is not valid JSON. Remove it and log in again."); }
    }

    public void Save(CliConfig config)
    {
        var dir = System.IO.Path.GetDirectoryName(Path)!;
        RefuseLink(dir);
        RefuseLink(Path);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var temp = System.IO.Path.Combine(dir, $".config-{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var stream = new FileStream(temp, options))
            using (var writer = new StreamWriter(stream))
                writer.Write(JsonSerializer.Serialize(config, Options));
            File.Move(temp, Path, overwrite: true); // replaces a file, never writes through a link
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Removes the file; true if there was one.</summary>
    public bool Delete()
    {
        if (!File.Exists(Path) && !IsLink(Path)) return false;
        RefuseLink(Path);
        File.Delete(Path);
        return true;
    }

    private static bool IsLink(string path)
    {
        try { return new FileInfo(path).LinkTarget != null || new DirectoryInfo(path).LinkTarget != null; }
        catch (IOException) { return false; }
    }

    private static void RefuseLink(string path)
    {
        if (IsLink(path)) throw CliException.Failure($"{path} is a symbolic link; refusing to use it for the access token.");
    }
}
