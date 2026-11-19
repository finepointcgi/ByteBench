using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitBench.Services.Hosting.Backup;

/// <summary><c>Backup:*</c> settings.</summary>
public sealed class BackupOptions
{
    /// <summary>Run scheduled backups inside the server. Off by default; "back up now" and the CLI work either way.</summary>
    public bool Enabled { get; set; }
    /// <summary>Where archives are written. Default <c>{ContentRoot}/backups</c>. Keep it on another disk or volume than the data.</summary>
    public string? Directory { get; set; }
    /// <summary>Hours between scheduled backups, counted from <see cref="StartTimeUtc"/>. Default 24.</summary>
    public double IntervalHours { get; set; } = 24;
    /// <summary>UTC time of day (HH:mm) the schedule is anchored to. Default 02:30.</summary>
    public string StartTimeUtc { get; set; } = "02:30";
    /// <summary>Keep this many newest archives (0 = no count limit). Default 7.</summary>
    public int KeepLast { get; set; } = 7;
    /// <summary>Also delete archives older than this many days, but never the newest one (0 = no age limit). Default 0.</summary>
    public int KeepDays { get; set; }
    /// <summary>Include the Data Protection key ring and SSH host key. Needed to read stored secrets after a restore. Default true.</summary>
    public bool IncludeKeys { get; set; } = true;
    /// <summary>Include CI artifacts (they can be large and are reproducible). Default false.</summary>
    public bool IncludeCiArtifacts { get; set; }
    /// <summary><c>pg_dump</c> executable, for Database:Provider=Postgres.</summary>
    public string PgDumpPath { get; set; } = "pg_dump";
    /// <summary><c>pg_restore</c> executable, for Database:Provider=Postgres.</summary>
    public string PgRestorePath { get; set; } = "pg_restore";
    /// <summary>Where a restore unpacks the archive before touching anything. Default: the system temp folder.</summary>
    public string? StagingDirectory { get; set; }

    public TimeOnly StartTime => TimeOnly.TryParse(StartTimeUtc, out var t) ? t : new TimeOnly(2, 30);
}

/// <summary>A folder of state that is part of every backup: <c>repos</c>, <c>attachments</c>, <c>packages</c>, <c>lfs</c>, <c>pages</c>, <c>keys</c>.</summary>
public sealed record BackupSection(string Name, string Path);

/// <summary>
/// Where GitBench keeps its data, resolved from configuration the same way the services do (so the CLI, which runs without the
/// web host, finds the same folders). The archive stores section <i>names</i>, never absolute paths: a backup restores onto any
/// machine or container layout.
/// </summary>
public sealed class BackupLayout
{
    public required DatabaseProvider Provider { get; init; }
    public required string ConnectionString { get; init; }
    /// <summary>The SQLite database file; null on Postgres.</summary>
    public string? SqliteFile { get; init; }
    public required IReadOnlyList<BackupSection> Sections { get; init; }
    public required string BackupDirectory { get; init; }

    public static BackupLayout Resolve(IConfiguration c, string contentRoot, BackupOptions o)
    {
        string Dir(string key, string fallback) => Path.GetFullPath(c[key] is { Length: > 0 } v ? v : Path.Combine(contentRoot, fallback));
        var sections = new List<BackupSection>
        {
            new("repos", Dir("Git:RepositoryRoot", "repos")),
            new("attachments", Dir("Attachments:Root", "attachments")),
            new("packages", Dir("Packages:Root", "packages")),
            new("lfs", Dir("Lfs:Root", "lfs")),
            new("pages", Dir("Pages:Root", "pages")),
        };
        if (o.IncludeKeys) sections.Add(new("keys", Dir("DataProtection:KeysPath", "keys")));
        if (o.IncludeCiArtifacts) sections.Add(new("ci-artifacts", Dir("Ci:ArtifactsRoot", "ci-artifacts")));
        var backupDir = Path.GetFullPath(o.Directory is { Length: > 0 } d ? d : Path.Combine(contentRoot, "backups"));
        return new BackupLayout
        {
            Provider = DatabaseSetup.GetProvider(c),
            ConnectionString = DatabaseSetup.GetConnectionString(c),
            SqliteFile = DatabaseSetup.SqliteFile(c),
            Sections = sections,
            BackupDirectory = backupDir,
        };
    }

    /// <summary>All section names a restore understands (also those switched off for backups, so old archives restore).</summary>
    public static readonly string[] KnownSections = ["repos", "attachments", "packages", "lfs", "pages", "keys", "ci-artifacts"];
}

public sealed class ManifestFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed class ManifestSection
{
    public int Files { get; set; }
    public long Bytes { get; set; }
}

/// <summary>
/// <c>manifest.json</c>, the last entry of every archive: what is inside and the SHA-256 of every file. Restore refuses an
/// archive whose contents don't match it.
/// </summary>
public sealed class BackupManifest
{
    public const int CurrentFormat = 1;
    public const string FileName = "manifest.json";

    public int Format { get; set; } = CurrentFormat;
    public string App { get; set; } = "GitBench";
    public string AppVersion { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public string Host { get; set; } = "";
    /// <summary><c>Sqlite</c> or <c>Postgres</c>; a backup only restores into the same provider.</summary>
    public string Provider { get; set; } = "Sqlite";
    /// <summary>Archive path of the database dump (<c>db/gitbench.db</c> or <c>db/postgres.dump</c>).</summary>
    public string DatabaseEntry { get; set; } = "";
    /// <summary>Id of the newest EF migration in the dumped database. A restore into an older GitBench is refused.</summary>
    public string Migration { get; set; } = "";
    public Dictionary<string, ManifestSection> Sections { get; set; } = [];
    public List<ManifestFile> Files { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };
    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static BackupManifest FromJson(string json) =>
        JsonSerializer.Deserialize<BackupManifest>(json, Json) ?? throw new BackupException("manifest.json is empty.");

    public static string CurrentAppVersion() =>
        typeof(BackupManifest).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(BackupManifest).Assembly.GetName().Version?.ToString() ?? "unknown";
}

/// <summary>A backup or restore that can't go on; the message is meant for the operator.</summary>
public sealed class BackupException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record BackupResult(string Path, long Bytes, int Files, long DataBytes, string Sha256, TimeSpan Elapsed, string Migration);

/// <summary>An archive in the backup directory.</summary>
public sealed record BackupFileInfo(string Name, long Bytes, DateTime CreatedUtc);

/// <summary>What the last attempt did; kept in <c>backup-status.json</c> next to the archives.</summary>
public sealed class BackupStatusFile
{
    public DateTime? LastAttemptUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public string? LastFile { get; set; }
    public long LastBytes { get; set; }
    public string? LastError { get; set; }
    public string? LastTrigger { get; set; }
}
