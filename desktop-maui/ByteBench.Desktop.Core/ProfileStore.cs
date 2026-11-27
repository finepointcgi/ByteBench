using System.Text.Json;
using System.Text.Json.Serialization;

namespace ByteBench.Desktop.Core;

/// <summary>A saved server. The id is a GUID (32 hex digits) and doubles as the name of the profile's isolated web-data store.</summary>
public sealed class ServerProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Origin { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastOpenedAt { get; set; }
    /// <summary>Highest notification id already announced. Null: never polled (the first poll only records a baseline).</summary>
    public int? LastNotifiedId { get; set; }

    public ServerProfile Clone() => (ServerProfile)MemberwiseClone();
}

public sealed class AppSettings
{
    public string? LastProfileId { get; set; }
    public bool NotificationsEnabled { get; set; } = true;
}

public sealed record StoreResult<T>(bool Ok, T? Value, string? Error)
{
    public static StoreResult<T> Success(T v) => new(true, v, null);
    public static StoreResult<T> Fail(string e) => new(false, default, e);
}

/// <summary>
/// Saved servers and settings in one JSON file. Holds no secrets (tokens go through <see cref="ITokenStore"/>). Writes are atomic
/// (temp file + rename) and the file is created owner-only on Unix. A missing or corrupt file starts empty.
/// </summary>
public sealed class ProfileStore
{
    public const int MaxProfiles = 50;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private sealed class FileShape
    {
        public int Version { get; set; } = 1;
        public List<ServerProfile> Profiles { get; set; } = [];
        public AppSettings Settings { get; set; } = new();
    }

    private readonly object _gate = new();
    private readonly string _file;
    private FileShape _data = new();

    public ProfileStore(string file)
    {
        _file = file;
        Load();
    }

    public event Action? Changed;

    public IReadOnlyList<ServerProfile> List()
    {
        lock (_gate) return [.. _data.Profiles.Select(p => p.Clone())];
    }

    public ServerProfile? Get(string id)
    {
        lock (_gate) return _data.Profiles.FirstOrDefault(p => p.Id == id)?.Clone();
    }

    public AppSettings Settings()
    {
        lock (_gate) return new AppSettings { LastProfileId = _data.Settings.LastProfileId, NotificationsEnabled = _data.Settings.NotificationsEnabled };
    }

    public void SetNotificationsEnabled(bool enabled)
    {
        lock (_gate) { _data.Settings.NotificationsEnabled = enabled; Save(); }
        Changed?.Invoke();
    }

    public StoreResult<ServerProfile> Add(string? name, string url, bool confirmInsecureHttp = false)
    {
        StoreResult<ServerProfile> result;
        lock (_gate)
        {
            if (_data.Profiles.Count >= MaxProfiles) return StoreResult<ServerProfile>.Fail($"At most {MaxProfiles} servers can be saved.");
            var n = ServerUrl.Normalize(url, confirmInsecureHttp);
            if (!n.Ok) return StoreResult<ServerProfile>.Fail(n.Error!);
            if (_data.Profiles.Any(p => p.Origin == n.Origin)) return StoreResult<ServerProfile>.Fail("That server is already in the list.");
            var profile = new ServerProfile
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = CleanName(name) is { Length: > 0 } cleaned ? cleaned : ServerUrl.SuggestName(n.Origin),
                Origin = n.Origin,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            _data.Profiles.Add(profile);
            Save();
            result = StoreResult<ServerProfile>.Success(profile.Clone());
        }
        Changed?.Invoke();
        return result;
    }

    /// <summary>Only the name can change: a different address is a different server with its own sign-in.</summary>
    public StoreResult<ServerProfile> Rename(string id, string name)
    {
        lock (_gate)
        {
            var p = _data.Profiles.FirstOrDefault(x => x.Id == id);
            if (p is null) return StoreResult<ServerProfile>.Fail("Unknown server.");
            if (CleanName(name) is { Length: > 0 } cleaned) p.Name = cleaned;
            Save();
            return StoreResult<ServerProfile>.Success(p.Clone());
        }
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _data.Profiles.RemoveAll(p => p.Id == id) > 0;
            if (_data.Settings.LastProfileId == id) _data.Settings.LastProfileId = null;
            if (removed) Save();
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    public void MarkOpened(string id)
    {
        lock (_gate)
        {
            var p = _data.Profiles.FirstOrDefault(x => x.Id == id);
            if (p is null) return;
            p.LastOpenedAt = DateTimeOffset.UtcNow;
            _data.Settings.LastProfileId = id;
            Save();
        }
    }

    /// <summary>Records the highest announced notification id. Never lowers it.</summary>
    public void SetLastNotified(string id, int notificationId)
    {
        lock (_gate)
        {
            var p = _data.Profiles.FirstOrDefault(x => x.Id == id);
            if (p is null || (p.LastNotifiedId ?? -1) >= notificationId) return;
            p.LastNotifiedId = notificationId;
            Save();
        }
    }

    private void Load()
    {
        try
        {
            var raw = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(_file), Json);
            if (raw is null) return;
            var seen = new HashSet<string>();
            var profiles = new List<ServerProfile>();
            foreach (var p in raw.Profiles ?? [])
            {
                // Re-validate everything read from disk: the file is user-writable.
                if (p is null || !Guid.TryParseExact(p.Id, "N", out _) || !seen.Add(p.Id)) continue;
                var n = ServerUrl.Normalize(p.Origin, allowInsecureRemote: true);
                if (!n.Ok || n.Origin != p.Origin) continue;
                p.Name = CleanName(p.Name) is { Length: > 0 } c ? c : ServerUrl.SuggestName(p.Origin);
                if (p.LastNotifiedId < 0) p.LastNotifiedId = null;
                profiles.Add(p);
            }
            var s = raw.Settings ?? new AppSettings();
            if (s.LastProfileId is not null && !profiles.Any(p => p.Id == s.LastProfileId)) s.LastProfileId = null;
            _data = new FileShape { Profiles = profiles, Settings = s };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // First run, or unreadable: start empty (a corrupt file is replaced on the next save).
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_file))!;
        Directory.CreateDirectory(dir);
        var tmp = $"{_file}.{Environment.ProcessId}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var fs = new FileStream(tmp, options))
        {
            JsonSerializer.Serialize(fs, _data, Json);
        }
        File.Move(tmp, _file, overwrite: true);
    }

    private static string CleanName(string? name)
    {
        var t = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return t.Length > 60 ? t[..60] : t;
    }
}
