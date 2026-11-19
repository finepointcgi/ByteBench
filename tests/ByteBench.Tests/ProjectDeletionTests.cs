using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Attachments;
using ByteBench.Services.Webhooks;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

public class ProjectDeletionTests(TestApp app) : IClassFixture<TestApp>
{
    private string RepoPath(Project p, string slug) => app.Services.GetRequiredService<RepoStorage>().PathFor(p.Key, slug);

    [Fact]
    public async Task Deleting_a_project_removes_its_rows_attachment_files_and_repositories()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        var keep = await app.CreateProjectAsync("carol");
        var carol = app.UserId("carol");

        // A bit of everything that hangs off a project.
        var repo = await app.CreateRepoAsync(project.Id, "carol", "main-repo");
        var second = await app.CreateRepoAsync(project.Id, "carol", "second");
        var kept = await app.CreateRepoAsync(keep.Id, "carol", "kept-repo");
        app.Commit(repo, "feature/z", "z.txt", "z");
        var issue = await app.CreateIssueAsync(project.Id, "carol", "With a file");
        var keptIssue = await app.CreateIssueAsync(keep.Id, "carol", "Kept file");
        string file, keptFile;
        await using (var s = app.Scope(carol))
        {
            var attachments = s.Get<AttachmentService>();
            file = s.Get<AttachmentStorage>().PathFor((await attachments.UploadAsync(issue.Id, "a.txt", new MemoryStream("a"u8.ToArray()))).StoredName);
            keptFile = s.Get<AttachmentStorage>().PathFor((await attachments.UploadAsync(keptIssue.Id, "b.txt", new MemoryStream("b"u8.ToArray()))).StoredName);
            var pr = await s.Get<PullRequestService>().CreateAsync(repo, "Z", null, "feature/z", "main", app.UserId("bob"));
            await s.Get<PullRequestService>().AddCommentAsync(pr.Id, carol, "hi @bob");
            await s.Get<BranchProtectionService>().SaveAsync(repo.Id, new BranchProtectionRule { Pattern = "main" });
            await s.Get<RepositorySettingsService>().RenameAsync(second.Id, "second-renamed");
            await s.Get<SprintService>().CreateSprintAsync(project.Id, "S1");
            await s.Get<SavedFilterService>().CreateAsync(project.Id, "f", "sort=created", shared: true);
            await s.Get<WebhookService>().CreateAsync(project.Id, "https://example.com/hook", null, [WebhookEventNames.Push], true);
            await s.Get<IssueService>().AddCommentAsync(issue.Id, carol, "comment");
        }
        var repoDirs = new[] { RepoPath(project, "main-repo"), RepoPath(project, "second-renamed") };
        Assert.All(repoDirs, d => Assert.True(Directory.Exists(d)));
        Assert.True(File.Exists(file));

        // Only Owners may delete.
        await using (var b = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<ForbiddenException>(() => b.Get<IssueService>().DeleteProjectAsync(project.Id));

        await using (var s = app.Scope(carol))
            await s.Get<IssueService>().DeleteProjectAsync(project.Id);

        await using var db = app.Db();
        Assert.False(await db.Projects.AnyAsync(p => p.Id == project.Id));
        Assert.False(await db.Issues.AnyAsync(i => i.ProjectId == project.Id));
        Assert.False(await db.Repositories.AnyAsync(r => r.ProjectId == project.Id));
        Assert.False(await db.PullRequests.AnyAsync(p => p.RepositoryId == repo.Id));
        Assert.False(await db.Attachments.AnyAsync(a => a.IssueId == issue.Id));
        Assert.False(await db.Notifications.AnyAsync(n => n.ProjectId == project.Id));
        Assert.False(await db.Webhooks.AnyAsync(w => w.ProjectId == project.Id));
        Assert.False(await db.Sprints.AnyAsync(s => s.ProjectId == project.Id));
        Assert.False(await db.SavedFilters.AnyAsync(f => f.ProjectId == project.Id));
        Assert.False(await db.RepositoryRedirects.AnyAsync(r => r.ProjectId == project.Id));

        Assert.False(File.Exists(file));
        Assert.All(repoDirs, d => Assert.False(Directory.Exists(d), $"{d} should have been deleted"));
        Assert.False(Directory.Exists(Path.Combine(app.RepoRoot, project.Key)));

        // Nothing of the other project was touched.
        Assert.True(File.Exists(keptFile));
        Assert.True(Directory.Exists(RepoPath(keep, kept.Slug)));
        Assert.True(await db.Issues.AnyAsync(i => i.Id == keptIssue.Id));
    }

    [Fact]
    public async Task A_new_project_can_reuse_the_key_of_a_deleted_one()
    {
        var project = await app.CreateProjectAsync("carol");
        await app.CreateRepoAsync(project.Id, "carol", "reused");
        await using var s = app.Scope(app.UserId("carol"));
        await s.Get<IssueService>().DeleteProjectAsync(project.Id);

        // Same key and slug: a stale bare repo left on disk would make this fail.
        var again = await s.Get<IssueService>().CreateProjectAsync(new Project { Key = project.Key, Name = "Again" });
        var repo = await s.Get<RepositoryService>().CreateAsync(again.Id, "reused", null, null, app.User("carol"));
        Assert.Equal("reused", repo.Slug);
    }
}
