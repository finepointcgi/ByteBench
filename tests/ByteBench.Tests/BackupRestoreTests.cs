using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Hosting;
using ByteBench.Services.Hosting.Backup;
using ByteBench.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

/// <summary>The test app with every storage folder inside its data folder (a backup walks all of them).</summary>
public class BackupApp : TestApp
{
    public string PackageRoot => Path.Combine(DataRoot, "packages");
    public string LfsRoot => Path.Combine(DataRoot, "lfs");
    public string PagesRoot => Path.Combine(DataRoot, "pages");
    public string KeysRoot => Path.Combine(DataRoot, "keys");
    public string BackupRoot => Path.Combine(DataRoot, "backups");

    protected virtual string? SharedRoot => null;

    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        var root = SharedRoot ?? DataRoot;
        settings["Packages:Root"] = Path.Combine(root, "packages");
        settings["Lfs:Root"] = Path.Combine(root, "lfs");
        settings["Pages:Root"] = Path.Combine(root, "pages");
        settings["DataProtection:KeysPath"] = Path.Combine(root, "keys");
        settings["Backup:Directory"] = Path.Combine(root, "backups");
        if (SharedRoot == null) return;
        settings["ConnectionStrings:Default"] = $"Data Source={Path.Combine(root, "bytebench.db")}";
        settings["Git:RepositoryRoot"] = Path.Combine(root, "repos");
        settings["Attachments:Root"] = Path.Combine(root, "attachments");
    }

    public BackupLayout Layout() =>
        BackupLayout.Resolve(Services.GetRequiredService<IConfiguration>(), Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath, new BackupOptions());
}

/// <summary>A second server on a restored folder (the "same data, new machine" case).</summary>
public sealed class RestoredApp(string root) : BackupApp
{
    protected override string? SharedRoot => root;
}

public class BackupRestoreTests(BackupApp app) : IClassFixture<BackupApp>
{
    private static string Scratch() => Path.Combine(Path.GetTempPath(), "bytebench-tests", "bk-" + Guid.NewGuid().ToString("N"));

    /// <summary>A layout for an empty installation under <paramref name="root"/> (SQLite).</summary>
    private static BackupLayout EmptyLayout(string root) => new()
    {
        Provider = DatabaseProvider.Sqlite,
        ConnectionString = $"Data Source={Path.Combine(root, "bytebench.db")}",
        SqliteFile = Path.Combine(root, "bytebench.db"),
        BackupDirectory = Path.Combine(root, "backups"),
        Sections = new[] { "repos", "attachments", "packages", "lfs", "pages", "keys" }.Select(n => new BackupSection(n, Path.Combine(root, n))).ToList(),
    };

    private async Task<(string Archive, string ProjectKey, int IssueNumber, string Sha)> SeedAndBackupAsync(string dest)
    {
        var project = await app.CreateProjectAsync("alice");
        var issue = await app.CreateIssueAsync(project.Id, "alice", "Backed up issue");
        var repo = await app.CreateRepoAsync(project.Id);
        var sha = app.Commit(repo, "main", "docs/readme.md", "# restored\n", from: "main", message: "backup me");
        Directory.CreateDirectory(Path.Combine(app.AttachmentRoot, "att"));
        await File.WriteAllTextAsync(Path.Combine(app.AttachmentRoot, "att", "note.txt"), "attachment bytes");
        Directory.CreateDirectory(Path.Combine(app.PackageRoot, "blobs", "sha256", "ab"));
        await File.WriteAllTextAsync(Path.Combine(app.PackageRoot, "blobs", "sha256", "ab", "abcd"), "package bytes");
        Directory.CreateDirectory(app.LfsRoot);
        await File.WriteAllTextAsync(Path.Combine(app.LfsRoot, "obj"), "lfs bytes");
        Directory.CreateDirectory(Path.Combine(app.PagesRoot, "site"));
        await File.WriteAllTextAsync(Path.Combine(app.PagesRoot, "site", "index.html"), "<h1>pages</h1>");
        await File.WriteAllTextAsync(Path.Combine(app.RepoRoot, "stray.lock"), "skipped");

        var result = await BackupArchive.CreateAsync(app.Layout(), new BackupOptions(), dest, null, CancellationToken.None);
        return (result.Path, project.Key, issue.Number, sha);
    }

    [Fact]
    public async Task Backup_and_restore_round_trip_into_a_clean_installation_that_starts_with_the_data()
    {
        if (TestApp.UsingPostgres) return; // the Postgres round trip needs pg_dump/pg_restore, see the README
        var dest = Scratch();
        var (archive, key, number, sha) = await SeedAndBackupAsync(dest);
        try
        {
            Assert.True(File.Exists(archive + ".sha256"));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive))).ToLowerInvariant(), File.ReadAllText(archive + ".sha256").Split(' ')[0]);

            // The manifest lists every file with a checksum, and the sections by name (no absolute paths).
            var manifest = ReadManifest(archive);
            Assert.Equal(1, manifest.Format);
            Assert.Equal("Sqlite", manifest.Provider);
            Assert.Equal(DatabaseBackup.KnownLatestMigration(DatabaseProvider.Sqlite), manifest.Migration);
            Assert.Contains(manifest.Files, f => f.Path == "db/bytebench.db");
            Assert.Contains(manifest.Files, f => f.Path == "attachments/att/note.txt" && f.Size == 16);
            Assert.Contains(manifest.Files, f => f.Path.StartsWith("repos/") && f.Path.Contains("/objects/"));
            Assert.DoesNotContain(manifest.Files, f => f.Path.EndsWith(".lock"));
            Assert.DoesNotContain(manifest.Files, f => f.Path.Contains(dest) || f.Path.Contains(app.DataRoot));
            Assert.All(manifest.Files, f => Assert.Equal(64, f.Sha256.Length));

            // Restore into a clean installation...
            var root = Scratch();
            try
            {
                var progress = new List<string>();
                var restored = await BackupRestorer.RestoreAsync(archive, EmptyLayout(root), new BackupOptions(), force: false, progress.Add, CancellationToken.None);
                Assert.Equal(manifest.Files.Count, restored.Files);
                Assert.Equal("attachment bytes", File.ReadAllText(Path.Combine(root, "attachments", "att", "note.txt")));
                Assert.Equal("lfs bytes", File.ReadAllText(Path.Combine(root, "lfs", "obj")));
                Assert.Equal("<h1>pages</h1>", File.ReadAllText(Path.Combine(root, "pages", "site", "index.html")));

                // ...then start ByteBench on it: the data is there.
                using var second = new RestoredApp(root);
                await using (var db = second.Db())
                {
                    var project = await db.Projects.FirstAsync(p => p.Key == key);
                    Assert.True(await db.Issues.AnyAsync(i => i.ProjectId == project.Id && i.Number == number && i.Title == "Backed up issue"));
                    Assert.True(await db.Users.AnyAsync(u => u.UserName == "alice"));
                }
                var repoPath = second.Services.GetRequiredService<RepoStorage>().PathFor(key, "code");
                Assert.True(Directory.Exists(Path.Combine(repoPath, "refs", "heads")));
                using (var git = new LibGit2Sharp.Repository(repoPath))
                {
                    var commit = (LibGit2Sharp.Commit)git.Lookup(sha);
                    Assert.NotNull(commit);
                    Assert.Equal("backup me", commit.MessageShort);
                    Assert.Equal("# restored\n", ((LibGit2Sharp.Blob)commit.Tree["docs/readme.md"].Target).GetContentText());
                }
                Assert.Equal(HttpStatusCode.OK, (await second.CreateClient().GetAsync("/health/ready")).StatusCode);
            }
            finally { TryDelete(root); }
        }
        finally { TryDelete(dest); }
    }

    [Fact]
    public async Task Restore_refuses_a_non_empty_installation_unless_forced()
    {
        if (TestApp.UsingPostgres) return;
        var dest = Scratch();
        var (archive, _, _, _) = await SeedAndBackupAsync(dest);
        var root = Scratch();
        try
        {
            var layout = EmptyLayout(root);
            Directory.CreateDirectory(Path.Combine(root, "repos"));
            File.WriteAllText(Path.Combine(root, "repos", "precious.txt"), "do not lose");

            var ex = await Assert.ThrowsAsync<BackupException>(() => BackupRestorer.RestoreAsync(archive, layout, new BackupOptions(), false, null, CancellationToken.None));
            Assert.Contains("non-empty", ex.Message);
            Assert.Contains("--force", ex.Message);
            Assert.Equal("do not lose", File.ReadAllText(Path.Combine(root, "repos", "precious.txt"))); // nothing was touched
            Assert.False(File.Exists(layout.SqliteFile));

            await BackupRestorer.RestoreAsync(archive, layout, new BackupOptions(), force: true, null, CancellationToken.None);
            Assert.False(File.Exists(Path.Combine(root, "repos", "precious.txt")));
            Assert.True(File.Exists(layout.SqliteFile));

            // A database that has users is "non-empty" even when every folder is empty.
            var again = await Assert.ThrowsAsync<BackupException>(() => BackupRestorer.RestoreAsync(archive, layout, new BackupOptions(), false, null, CancellationToken.None));
            Assert.Contains("database already has users or projects", again.Message);
        }
        finally { SqliteConnection.ClearAllPools(); TryDelete(root); TryDelete(dest); }
    }

    [Fact]
    public async Task A_just_migrated_empty_database_does_not_block_a_restore()
    {
        if (TestApp.UsingPostgres) return;
        var dest = Scratch();
        var (archive, _, _, _) = await SeedAndBackupAsync(dest);
        var root = Scratch();
        try
        {
            var layout = EmptyLayout(root);
            Directory.CreateDirectory(root);
            await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(layout.ConnectionString).Options))
                await db.Database.MigrateAsync(); // what a first start does
            await BackupRestorer.RestoreAsync(archive, layout, new BackupOptions(), force: false, null, CancellationToken.None);
        }
        finally { SqliteConnection.ClearAllPools(); TryDelete(root); TryDelete(dest); }
    }

    [Fact]
    public async Task Restore_rejects_tampered_newer_foreign_and_unsafe_archives_before_touching_anything()
    {
        if (TestApp.UsingPostgres) return;
        var dest = Scratch();
        var (archive, _, _, _) = await SeedAndBackupAsync(dest);
        var root = Scratch();
        try
        {
            async Task<string> Expect(string name, Func<string, byte[], byte[]?> edit, string message, Action<BackupManifest>? manifestEdit = null, (string, byte[])? extra = null)
            {
                var bad = Path.Combine(dest, name + ".tar.gz");
                Rewrite(archive, bad, edit, manifestEdit, extra);
                var ex = await Assert.ThrowsAsync<BackupException>(() => BackupRestorer.RestoreAsync(bad, EmptyLayout(root), new BackupOptions(), false, null, CancellationToken.None));
                Assert.Contains(message, ex.Message);
                Assert.False(Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any(), "nothing may be restored from a bad archive");
                return ex.Message;
            }

            await Expect("flipped", (n, d) => n == "attachments/att/note.txt" ? Encoding.UTF8.GetBytes("attachment BYTES") : d, "is corrupt");
            await Expect("missing", (n, d) => n == "lfs/obj" ? null : d, "missing from the archive");
            await Expect("extra", (n, d) => d, "not in the manifest", extra: ("attachments/surprise.txt", [1, 2, 3]));
            await Expect("newer-migration", (n, d) => d, "newer than this ByteBench", m => m.Migration = "99991231235959_FromTheFuture");
            await Expect("newer-format", (n, d) => d, "made by a newer version", m => m.Format = 99);
            await Expect("other-provider", (n, d) => d, "only restores into the same provider", m => m.Provider = "Postgres");
            await Expect("traversal", (n, d) => d, "Unsafe path", extra: ("repos/../../evil.txt", [1]));
            await Expect("unknown-root", (n, d) => d, "Unexpected entry", extra: ("etc/passwd", [1]));

            // A truncated download isn't an archive at all.
            var cut = Path.Combine(dest, "cut.tar.gz");
            File.WriteAllBytes(cut, File.ReadAllBytes(archive)[..(int)(new FileInfo(archive).Length / 2)]);
            await Assert.ThrowsAnyAsync<Exception>(() => BackupRestorer.RestoreAsync(cut, EmptyLayout(root), new BackupOptions(), false, null, CancellationToken.None));
            Assert.False(Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any());
        }
        finally { TryDelete(root); TryDelete(dest); }
    }

    [Fact]
    public void Retention_keeps_the_newest_and_never_deletes_the_last_one()
    {
        var dir = Scratch();
        Directory.CreateDirectory(dir);
        try
        {
            string[] names = ["20260101-000000", "20260102-000000", "20260103-000000", "20260104-000000", "20260105-000000"];
            foreach (var n in names)
            {
                var path = Path.Combine(dir, $"bytebench-backup-{n}.tar.gz");
                File.WriteAllText(path, "x");
                File.WriteAllText(path + ".sha256", "x");
                File.SetLastWriteTimeUtc(path, DateTime.ParseExact(n, "yyyyMMdd-HHmmss", null));
            }
            File.WriteAllText(Path.Combine(dir, "notes.tar.gz"), "mine"); // not ours: never touched

            Assert.Equal(3, BackupService.Retention.Apply(dir, keepLast: 2, keepDays: 0));
            Assert.Equal(["bytebench-backup-20260105-000000.tar.gz", "bytebench-backup-20260104-000000.tar.gz"], BackupService.Retention.List(dir).Select(f => f.Name));
            Assert.False(File.Exists(Path.Combine(dir, "bytebench-backup-20260101-000000.tar.gz.sha256")));
            Assert.True(File.Exists(Path.Combine(dir, "notes.tar.gz")));

            // Everything is older than a day, yet the newest archive always stays.
            Assert.Equal(1, BackupService.Retention.Apply(dir, keepLast: 0, keepDays: 1));
            Assert.Single(BackupService.Retention.List(dir));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Schedule_is_the_start_time_plus_whole_intervals()
    {
        var o = new BackupOptions { StartTimeUtc = "02:30", IntervalHours = 24 };
        Assert.Equal(new DateTime(2026, 5, 5, 2, 30, 0), BackupScheduler.NextRun(new DateTime(2026, 5, 4, 12, 0, 0), o));
        Assert.Equal(new DateTime(2026, 5, 4, 2, 30, 0), BackupScheduler.NextRun(new DateTime(2026, 5, 4, 1, 0, 0), o));
        o.IntervalHours = 6;
        Assert.Equal(new DateTime(2026, 5, 4, 14, 30, 0), BackupScheduler.NextRun(new DateTime(2026, 5, 4, 12, 0, 0), o));
        Assert.Equal(new DateTime(2026, 5, 4, 2, 30, 0), BackupScheduler.NextRun(new DateTime(2026, 5, 4, 0, 5, 0), o)); // 20:30 the evening before is past
    }

    [Fact]
    public async Task Run_now_writes_status_and_applies_retention()
    {
        if (TestApp.UsingPostgres) return;
        var backups = app.Services.GetRequiredService<BackupService>();
        var result = await backups.RunAsync("test");
        Assert.StartsWith(app.BackupRoot, result.Path);
        Assert.Contains(backups.List(), f => f.Name == Path.GetFileName(result.Path));
        var status = backups.Status();
        Assert.Equal("test", status.LastTrigger);
        Assert.Null(status.LastError);
        Assert.Equal(Path.GetFileName(result.Path), status.LastFile);
        // The backup folder (inside the data folder here) is never part of an archive.
        Assert.DoesNotContain(ReadManifest(result.Path).Files, f => f.Path.Contains("bytebench-backup-"));
    }

    [Fact]
    public async Task Admin_pages_and_downloads_are_for_site_admins_only()
    {
        if (TestApp.UsingPostgres) return;
        var backups = app.Services.GetRequiredService<BackupService>();
        var result = await backups.RunAsync("test");
        var name = Path.GetFileName(result.Path);

        var alice = await app.CookieClientAsync("alice"); // the demo site admin
        var system = await alice.GetStringAsync("/admin/system");
        Assert.Contains("data-testid=\"sys-version\"", system);
        Assert.Contains(app.Services.GetRequiredService<IConfiguration>()["Database:Provider"] ?? "Sqlite", system);
        Assert.Contains("Hosting checks", system);
        Assert.Contains("Webhook deliveries", system);
        var page = await alice.GetStringAsync("/admin/backups");
        Assert.Contains(name, page);
        var download = await alice.GetAsync($"/admin/backups/download/{name}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(new FileInfo(result.Path).Length, (await download.Content.ReadAsByteArrayAsync()).Length);
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/admin/backups/download/{name}.sha256")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync("/admin/backups/download/..%2f..%2fbytebench.db")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync("/admin/backups/download/notes.txt")).StatusCode);

        var bob = await app.CookieClientAsync("bob"); // not an admin
        Assert.DoesNotContain("sys-version", await bob.GetStringAsync("/admin/system"));
        Assert.Contains("Only site administrators", await bob.GetStringAsync("/admin/backups"));
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync($"/admin/backups/download/{name}")).StatusCode);
        var anonymous = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.NotEqual(HttpStatusCode.OK, (await anonymous.GetAsync($"/admin/backups/download/{name}")).StatusCode);
    }

    // ---------------------------------------------------------------- helpers

    private static BackupManifest ReadManifest(string archive)
    {
        using var gz = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        while (tar.GetNextEntry() is { } e)
            if (e.Name == "manifest.json") { using var r = new StreamReader(e.DataStream!); return BackupManifest.FromJson(r.ReadToEnd()); }
        throw new InvalidOperationException("no manifest");
    }

    /// <summary>Copies an archive entry by entry, letting <paramref name="edit"/> change or drop file contents, the manifest be edited, and one entry be added.</summary>
    private static void Rewrite(string from, string to, Func<string, byte[], byte[]?> edit, Action<BackupManifest>? manifestEdit, (string Name, byte[] Data)? extra)
    {
        using var input = new GZipStream(File.OpenRead(from), CompressionMode.Decompress);
        using var reader = new TarReader(input);
        using var output = new GZipStream(File.Create(to), CompressionLevel.Fastest);
        using var writer = new TarWriter(output, TarEntryFormat.Pax);
        while (reader.GetNextEntry() is { } e)
        {
            if (e.EntryType == TarEntryType.Directory) { writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, e.Name) { Mode = e.Mode }); continue; }
            using var ms = new MemoryStream();
            e.DataStream!.CopyTo(ms);
            byte[]? data = ms.ToArray();
            if (e.Name == "manifest.json")
            {
                if (extra is { } x) writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, x.Name) { DataStream = new MemoryStream(x.Data) });
                var m = BackupManifest.FromJson(Encoding.UTF8.GetString(data));
                manifestEdit?.Invoke(m);
                data = Encoding.UTF8.GetBytes(m.ToJson());
            }
            else data = edit(e.Name, data);
            if (data == null) continue;
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, e.Name) { DataStream = new MemoryStream(data), Mode = e.Mode });
        }
    }

    private static void TryDelete(string dir)
    {
        try { TestApp.DeleteDirectory(dir); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

[CollectionDefinition("backup-cli", DisableParallelization = true)]
public class BackupCliCollection;

/// <summary>The CLI reads the process environment, so these tests run alone (no other fixture may start while it is changed).</summary>
[Collection("backup-cli")]
public class BackupCliTests
{
    private static string Scratch() => Path.Combine(Path.GetTempPath(), "bytebench-tests", "bk-" + Guid.NewGuid().ToString("N"));
    private static void TryDelete(string dir)
    {
        try { TestApp.DeleteDirectory(dir); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task The_cli_backs_up_and_restores_with_exit_codes()
    {
        if (TestApp.UsingPostgres) return;
        // The CLI reads the same configuration sources as the server: point it at a scratch install through the environment.
        var src = Scratch();
        var dest = Scratch();
        var target = Scratch();
        var old = Directory.GetCurrentDirectory();
        Directory.CreateDirectory(src);
        try
        {
            await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(src, "bytebench.db")}").Options))
            {
                await db.Database.MigrateAsync();
                db.Users.Add(new User { UserName = "cli", NormalizedUserName = "CLI", DisplayName = "Cli", Color = "#000000" });
                await db.SaveChangesAsync();
            }
            Directory.CreateDirectory(Path.Combine(src, "repos"));
            File.WriteAllText(Path.Combine(src, "repos", "x.txt"), "x");
            SqliteConnection.ClearAllPools();

            Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={Path.Combine(src, "bytebench.db")}");
            Environment.SetEnvironmentVariable("Git__RepositoryRoot", Path.Combine(src, "repos"));
            foreach (var (k, section) in new[] { ("Attachments__Root", "attachments"), ("Packages__Root", "packages"), ("Lfs__Root", "lfs"), ("Pages__Root", "pages"), ("DataProtection__KeysPath", "keys") })
                Environment.SetEnvironmentVariable(k, Path.Combine(src, section));
            try
            {
                Assert.True(BackupCli.IsCommand(["backup"]));
                Assert.False(BackupCli.IsCommand(["--urls", "x"]));
                Assert.Equal(0, await BackupCli.RunAsync(["backup", dest]));
                var archive = Directory.GetFiles(dest, "bytebench-backup-*.tar.gz").Single();
                Assert.Equal(2, await BackupCli.RunAsync(["restore"]));                       // no file
                Assert.Equal(2, await BackupCli.RunAsync(["backup", dest, "--nonsense"]));
                Assert.Equal(1, await BackupCli.RunAsync(["restore", Path.Combine(dest, "missing.tar.gz")]));
                Assert.Equal(1, await BackupCli.RunAsync(["restore", archive]));              // the source installation isn't empty

                // Same archive into an empty installation.
                Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={Path.Combine(target, "bytebench.db")}");
                Environment.SetEnvironmentVariable("Git__RepositoryRoot", Path.Combine(target, "repos"));
                foreach (var (k, section) in new[] { ("Attachments__Root", "attachments"), ("Packages__Root", "packages"), ("Lfs__Root", "lfs"), ("Pages__Root", "pages"), ("DataProtection__KeysPath", "keys") })
                    Environment.SetEnvironmentVariable(k, Path.Combine(target, section));
                Assert.Equal(0, await BackupCli.RunAsync(["restore", archive]));
                Assert.Equal("x", File.ReadAllText(Path.Combine(target, "repos", "x.txt")));
                await using var restored = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(target, "bytebench.db")}").Options);
                Assert.True(await restored.Users.AnyAsync(u => u.UserName == "cli"));
            }
            finally
            {
                foreach (var k in new[] { "ConnectionStrings__Default", "Git__RepositoryRoot", "Attachments__Root", "Packages__Root", "Lfs__Root", "Pages__Root", "DataProtection__KeysPath" })
                    Environment.SetEnvironmentVariable(k, null);
            }
        }
        finally { SqliteConnection.ClearAllPools(); TryDelete(src); TryDelete(dest); TryDelete(target); }
    }
}
