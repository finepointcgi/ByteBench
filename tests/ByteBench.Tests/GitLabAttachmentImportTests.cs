using System.Text.Json.Nodes;
using ByteBench.Data;
using ByteBench.Services.TrackerImport;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

public sealed class GitLabAttachmentApp : TestApp
{
    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["TrackerImport:Enabled"] = "false";
        settings["TrackerImport:AllowPrivateNetworks"] = "true";
        settings["Attachments:MaxBytes"] = "4096";
    }
}

/// <summary>Copying the files GitLab issues and comments link (<c>/uploads/hash/name</c>) against a fake GitLab and a fake CDN.</summary>
public class GitLabAttachmentImportTests(GitLabAttachmentApp app) : IClassFixture<GitLabAttachmentApp>, IDisposable
{
    private const string Uploads = "/acme/team/widgets/uploads/";
    private readonly FakeGitLab fake = new();
    private readonly FakeFileHost cdn = new();

    public void Dispose() { fake.Dispose(); cdn.Dispose(); }

    private void Arrange()
    {
        fake.WebUrl = fake.Url + "/acme/team/widgets";
        fake.CdnUrl = cdn.Url;
        fake.Files[Uploads + "ab12/shot.png"] = new FakeFile(FakeFiles.Png);
        fake.Files[Uploads + "ab12/log.txt"] = new FakeFile(RedirectTo: "{cdn}/blob/log.txt?sig=xyz");
        // "fixed.png" (in a comment) has no file: GitLab answers 404.
        lock (cdn.Files) cdn.Files["/blob/log.txt"] = new FakeFile("line one\nline two\n"u8.ToArray());
        fake.Files[Uploads + "cd34/big.bin"] = new FakeFile(FakeFiles.Filler(10240));
        fake.Files[Uploads + "cd34/setup.exe"] = new FakeFile(FakeFiles.Filler(8));
        fake.Issues[1]!["description"] = "Crash. ![big](/uploads/cd34/big.bin) and [installer](/uploads/cd34/setup.exe)";
    }

    private static TrackerImportInput Input(FakeGitLab f, bool attachments) => new()
    {
        Source = TrackerSourceKind.GitLab, BaseUrl = f.Url, Project = FakeGitLab.ProjectPath, Secret = FakeGitLab.Token,
        Settings = new TrackerImportSettings { IncludeAttachments = attachments },
    };

    private async Task<(Project P, int Carol)> NewProjectAsync()
    {
        var p = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        return (p, app.UserId("carol"));
    }

    private async Task<TrackerImportJob> RunAsync(Project p, int actor, TrackerImportInput input)
    {
        await using var s = app.Scope(actor);
        var job = await s.Get<TrackerImportService>().StartAsync(p.Id, input, actor);
        await app.Services.GetRequiredService<TrackerImportRunner>().RunAsync(job.Id);
        await using var db = app.Db();
        return await db.TrackerImportJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id);
    }

    private async Task<(Issue Issue, List<Attachment> Files)> IssueAsync(Project p, string title)
    {
        await using var db = app.Db();
        var issue = await db.Issues.AsNoTracking().Include(i => i.Comments).SingleAsync(i => i.ProjectId == p.Id && i.Title == title);
        return (issue, await db.Attachments.AsNoTracking().Where(a => a.IssueId == issue.Id).OrderBy(a => a.Id).ToListAsync());
    }

    [Fact]
    public async Task Linked_uploads_are_copied_and_the_links_point_at_the_local_files()
    {
        Arrange();
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol, Input(fake, true));

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        var (login, files) = await IssueAsync(p, "Login fails");
        Assert.Equal(["log.txt", "shot.png"], files.Select(f => f.FileName).Order().ToList());
        var shot = files.Single(f => f.FileName == "shot.png");
        var log = files.Single(f => f.FileName == "log.txt");
        Assert.Equal("image/png", shot.ContentType);
        Assert.Equal(FakeFiles.Sha256(FakeFiles.Png), shot.Sha256);
        Assert.Equal(carol, shot.UploaderId);
        Assert.False(shot.FromComment);
        Assert.Equal("line one\nline two\n"u8.ToArray(), await File.ReadAllBytesAsync(Path.Combine(app.AttachmentRoot, log.StoredName)));

        // the markdown now points at the local copies (an image stays an image link)
        Assert.Contains($"![screenshot](/attachments/{shot.Id}/shot.png)", login.Description);
        Assert.Contains($"[file](/attachments/{log.Id}/log.txt)", login.Description);
        Assert.DoesNotContain("/uploads/ab12", login.Description);
        Assert.DoesNotContain("not copied", login.Description);

        // the comment's file isn't there (404): its link stays as it was, with one warning
        var comment = login.Comments.Single(c => c.Body.Contains("Fixed, see"));
        Assert.Contains($"]({fake.WebUrl}/uploads/ab12/fixed.png)", comment.Body);
        var warnings = TrackerImportService.WarningsOf(job);
        Assert.Contains(warnings, w => w.Contains("#1") && w.Contains("fixed.png") && w.Contains("no longer has the file"));

        // the token went to GitLab only; the CDN it redirected to got nothing
        lock (fake.Requests) Assert.All(fake.Requests, r => Assert.Equal(FakeGitLab.Token, r.Token));
        lock (cdn.Requests)
        {
            Assert.Single(cdn.Requests);
            Assert.All(cdn.Requests, r => { Assert.Null(r.Authorization); Assert.Null(r.PrivateToken); });
        }
        Assert.DoesNotContain(FakeGitLab.Token, string.Join("|", warnings) + job.WarningsJson + job.SettingsJson + job.Error);
        await using var db = app.Db();
        var audit = string.Join("|", await db.AuditEvents.Where(a => a.ProjectId == p.Id).Select(a => a.Details ?? "").ToListAsync());
        Assert.DoesNotContain(FakeGitLab.Token, audit);
        Assert.DoesNotContain("sig=xyz", audit + string.Join("|", warnings));
    }

    [Fact]
    public async Task Oversized_and_blocked_files_are_skipped_with_warnings_and_their_links_stay()
    {
        Arrange();
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol, Input(fake, true));

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        var (crash, files) = await IssueAsync(p, "Crash on save");
        Assert.Empty(files);
        Assert.Contains($"]({fake.WebUrl}/uploads/cd34/big.bin)", crash.Description);
        var warnings = TrackerImportService.WarningsOf(job);
        Assert.Contains(warnings, w => w.Contains("#2") && w.Contains("big.bin") && w.Contains("over the limit of 4 KB"));
        Assert.Contains(warnings, w => w.Contains("#2") && w.Contains("setup.exe") && w.Contains("BlockedAttachmentExtensions"));
        lock (fake.Requests) Assert.DoesNotContain(fake.Requests, r => r.Path.EndsWith("setup.exe"));   // never fetched
    }

    [Fact]
    public async Task Running_again_adds_no_duplicate_files_and_does_not_edit_issues()
    {
        Arrange();
        var (p, carol) = await NewProjectAsync();
        await RunAsync(p, carol, Input(fake, true));
        var (before, files) = await IssueAsync(p, "Login fails");
        int shotRequests;
        lock (fake.Requests) shotRequests = fake.Requests.Count(r => r.Path.EndsWith("shot.png"));

        var again = await RunAsync(p, carol, Input(fake, true));

        Assert.Equal(TrackerImportStatus.Done, again.Status);
        var (after, filesAfter) = await IssueAsync(p, "Login fails");
        Assert.Equal(files.Select(f => f.Id), filesAfter.Select(f => f.Id));
        Assert.Equal(before.Description, after.Description);
        lock (fake.Requests) Assert.Equal(shotRequests, fake.Requests.Count(r => r.Path.EndsWith("shot.png")));
    }

    [Fact]
    public async Task Without_the_option_nothing_is_downloaded_and_the_text_is_unchanged()
    {
        Arrange();
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol, Input(fake, false));

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        var (login, files) = await IssueAsync(p, "Login fails");
        Assert.Empty(files);
        Assert.Contains($"]({fake.WebUrl}/uploads/cd34/", (await IssueAsync(p, "Crash on save")).Issue.Description);
        lock (fake.Requests) Assert.DoesNotContain(fake.Requests, r => r.Path.Contains("/uploads/"));
        Assert.DoesNotContain("not copied", login.Description);
    }
}
