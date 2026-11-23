using System.Text.Json.Nodes;
using ByteBench.Data;
using ByteBench.Services.TrackerImport;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

public sealed class GitHubAttachmentApp : TestApp
{
    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["TrackerImport:AllowPrivateNetworks"] = "true";
        settings["TrackerImport:Enabled"] = "false";
        settings["Attachments:MaxBytes"] = "4096";
    }
}

/// <summary>
/// Copying files linked from GitHub issues and comments. The upload hosts (github.com/user-attachments, the user-images host) are mapped
/// onto a local fake through the importer's test hook, so the real address rules still apply to every hop.
/// </summary>
public class GitHubAttachmentImportTests : IClassFixture<GitHubAttachmentApp>, IDisposable
{
    private const string Asset = "https://github.com/user-attachments/assets/aaaa-bbbb";
    private const string Old = "https://user-images.githubusercontent.com/1/old.png";
    private readonly GitHubAttachmentApp app;
    private readonly FakeGitHub gh = new() { RequireToken = true };
    private readonly FakeFileHost uploads = new();
    private readonly FakeFileHost cdn = new();

    public GitHubAttachmentImportTests(GitHubAttachmentApp app)
    {
        this.app = app;
        var hooks = app.Services.GetRequiredService<TrackerImportTestHooks>();
        hooks.MapUrl = u => u.Host is "github.com" or "user-images.githubusercontent.com" ? new Uri(uploads.Url + "/" + u.Host + u.PathAndQuery) : u;
        lock (uploads.Files)
        {
            uploads.Files["/github.com/user-attachments/assets/aaaa-bbbb"] = new FakeFile(RedirectTo: cdn.Url + "/signed/aaaa.png?jwt=secretjwt123");
            uploads.Files["/github.com/user-attachments/files/123/trace.log"] = new FakeFile("trace line\n"u8.ToArray());
            uploads.Files["/github.com/user-attachments/files/9/big.zip"] = new FakeFile(FakeFiles.Filler(10240));
            uploads.Files["/user-images.githubusercontent.com/1/old.png"] = new FakeFile(FakeFiles.Png);
            // assets/gone-uuid: no file, the host answers 404
        }
        lock (cdn.Files) cdn.Files["/signed/aaaa.png"] = new FakeFile(FakeFiles.Png);
        gh.Issues[0]["body"] = $"Shot ![shot]({Asset}) log [trace](https://github.com/user-attachments/files/123/trace.log) old {Old} and https://example.com/not-an-upload.png";
        gh.Comments[1][0]["body"] = $"Again ![shot]({Asset}) and [big](https://github.com/user-attachments/files/9/big.zip) and ![gone](https://github.com/user-attachments/assets/gone-uuid)";
    }

    public void Dispose()
    {
        app.Services.GetRequiredService<TrackerImportTestHooks>().MapUrl = null;
        gh.Dispose(); uploads.Dispose(); cdn.Dispose();
    }

    private TrackerImportInput Input(bool attachments) => new()
    {
        Source = TrackerSourceKind.GitHub, BaseUrl = gh.ApiRoot, Project = FakeGitHub.Repo, Secret = FakeGitHub.Token,
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

    private async Task<(Issue Issue, List<Attachment> Files)> CrashAsync(Project p)
    {
        await using var db = app.Db();
        var issue = await db.Issues.AsNoTracking().Include(i => i.Comments).SingleAsync(i => i.ProjectId == p.Id && i.Title == "Crash on save");
        return (issue, await db.Attachments.AsNoTracking().Where(a => a.IssueId == issue.Id).OrderBy(a => a.Id).ToListAsync());
    }

    [Fact]
    public async Task Files_linked_in_issues_and_comments_are_copied_once_and_the_links_rewritten()
    {
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol, Input(true));

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        var (issue, files) = await CrashAsync(p);
        Assert.Equal(["aaaa-bbbb.png", "old.png", "trace.log"], files.Select(f => f.FileName).Order(StringComparer.Ordinal).ToList());   // the same asset in body and comment: once
        var shot = files.Single(f => f.FileName == "aaaa-bbbb.png");
        Assert.Equal("image/png", shot.ContentType);
        Assert.Equal(FakeFiles.Sha256(FakeFiles.Png), shot.Sha256);       // arrived through the CDN redirect
        Assert.Equal(carol, shot.UploaderId);
        var trace = files.Single(f => f.FileName == "trace.log");
        var old = files.Single(f => f.FileName == "old.png");

        Assert.Contains($"![shot](/attachments/{shot.Id}/aaaa-bbbb.png)", issue.Description);
        Assert.Contains($"[trace](/attachments/{trace.Id}/trace.log)", issue.Description);
        Assert.Contains($"old /attachments/{old.Id}/old.png and", issue.Description);
        Assert.Contains("https://example.com/not-an-upload.png", issue.Description);   // other addresses are left alone
        Assert.DoesNotContain("user-attachments", issue.Description!.Replace("Imported from", ""));
        var first = issue.Comments.Single(c => c.Body.StartsWith("Again"));
        Assert.Contains($"![shot](/attachments/{shot.Id}/aaaa-bbbb.png)", first.Body);
        Assert.Contains("[big](https://github.com/user-attachments/files/9/big.zip)", first.Body);         // too big: link stays
        Assert.Contains("![gone](https://github.com/user-attachments/assets/gone-uuid)", first.Body);      // 404: link stays

        var warnings = TrackerImportService.WarningsOf(job);
        Assert.Contains(warnings, w => w.Contains("#1") && w.Contains("big.zip") && w.Contains("over the limit of 4 KB"));
        Assert.Contains(warnings, w => w.Contains("#1") && w.Contains("gone-uuid") && w.Contains("no longer has the file"));
    }

    [Fact]
    public async Task The_token_is_only_sent_to_the_api_address_never_to_upload_hosts_or_a_cdn()
    {
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol, Input(true));

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        lock (uploads.Requests) { Assert.NotEmpty(uploads.Requests); Assert.All(uploads.Requests, r => { Assert.Null(r.Authorization); Assert.Null(r.PrivateToken); }); }
        lock (cdn.Requests) { Assert.NotEmpty(cdn.Requests); Assert.All(cdn.Requests, r => { Assert.Null(r.Authorization); Assert.Null(r.PrivateToken); }); }
        lock (gh.Requests) Assert.All(gh.Requests, r => Assert.Equal("Bearer " + FakeGitHub.Token, r.Auth));
        var all = string.Join("|", TrackerImportService.WarningsOf(job)) + job.WarningsJson + job.SettingsJson + job.Error;
        Assert.DoesNotContain(FakeGitHub.Token, all);
        Assert.DoesNotContain("secretjwt123", all);
        await using var db = app.Db();
        var audit = string.Join("|", await db.AuditEvents.Where(a => a.ProjectId == p.Id).Select(a => a.Details ?? "").ToListAsync());
        Assert.DoesNotContain(FakeGitHub.Token, audit);
        Assert.DoesNotContain("secretjwt123", audit);
    }

    [Fact]
    public async Task Running_again_adds_nothing_and_without_the_option_nothing_is_fetched()
    {
        var (p, carol) = await NewProjectAsync();
        await RunAsync(p, carol, Input(true));
        var (before, files) = await CrashAsync(p);
        var again = await RunAsync(p, carol, Input(true));

        Assert.Equal(TrackerImportStatus.Done, again.Status);
        var (after, filesAfter) = await CrashAsync(p);
        Assert.Equal(files.Select(f => f.Id), filesAfter.Select(f => f.Id));
        Assert.Equal(before.Description, after.Description);

        int fetched;
        lock (uploads.Requests) fetched = uploads.Requests.Count;
        var (p2, _) = await NewProjectAsync();
        var plain = await RunAsync(p2, carol, Input(false));
        Assert.Equal(TrackerImportStatus.Done, plain.Status);
        var (untouched, none) = await CrashAsync(p2);
        Assert.Empty(none);
        Assert.Contains(Asset, untouched.Description);
        lock (uploads.Requests) Assert.Equal(fetched, uploads.Requests.Count);
    }
}
