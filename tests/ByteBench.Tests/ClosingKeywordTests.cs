using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Git;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Tests;

/// <summary>Found by using ByteBench for a day: "Fixes SHOP-1" in a pull request description linked nothing and closed nothing.</summary>
public class ClosingKeywordTests(TestApp app) : IClassFixture<TestApp>
{
    [Theory]
    [InlineData("Fixes SHOP-1", new[] { 1 })]
    [InlineData("fixed shop-2 and more", new[] { 2 })]
    [InlineData("Closes: SHOP-3", new[] { 3 })]
    [InlineData("Resolves SHOP-4, SHOP-5 and SHOP-6", new[] { 4, 5, 6 })]
    [InlineData("closes SHOP-7 & SHOP-8", new[] { 7, 8 })]
    [InlineData("See SHOP-9 for background", new int[0])]
    [InlineData("prefixes SHOP-10 in the docs", new int[0])]
    [InlineData("Fixes OTHER-11", new int[0])]
    [InlineData("Fixes SHOP-12.\nRelates to SHOP-13", new[] { 12 })]
    public void Only_a_closing_keyword_right_before_a_key_closes(string text, int[] expected) =>
        Assert.Equal(expected, IssueKeys.ClosingNumbers("SHOP", [text]));

    [Fact]
    public async Task Merging_into_the_default_branch_closes_the_issues_the_description_names()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        var fixedIssue = await app.CreateIssueAsync(project.Id, "carol", "Will be fixed");
        var mentioned = await app.CreateIssueAsync(project.Id, "carol", "Only mentioned");
        app.Commit(repo, "feature/x", "x.txt", "x");

        int prId;
        await using (var s = app.Scope(app.UserId("bob")))
            prId = (await s.Get<PullRequestService>().CreateAsync(repo, "Add x", $"Fixes {project.Key}-{fixedIssue.Number}\n\nSee {project.Key}-{mentioned.Number}", "feature/x", "main", app.UserId("bob"))).Id;

        await using (var s = app.Scope(app.UserId("carol")))
        {
            var pr = await s.Get<PullRequestService>().GetAsync(project.Key, repo.Slug, 1);
            Assert.Contains(fixedIssue.Number, (await s.Get<PullRequestService>().GetLinkedIssuesAsync(pr!)).Select(i => i.Number)); // the description links it
            Assert.True((await s.Get<PullRequestService>().MergeAsync(prId, app.User("carol"), false, false)).Success);
        }

        await using var db = app.Db();
        var issues = await db.Issues.AsNoTracking().Where(i => i.ProjectId == project.Id).ToDictionaryAsync(i => i.Id);
        Assert.Equal(IssueStatus.Done, issues[fixedIssue.Id].Status);
        Assert.NotNull(issues[fixedIssue.Id].ClosedAt);
        Assert.NotEqual(IssueStatus.Done, issues[mentioned.Id].Status);
    }

    [Fact]
    public async Task A_merge_into_another_branch_closes_nothing()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private);
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        var issue = await app.CreateIssueAsync(project.Id, "carol", "Stays open");
        app.Commit(repo, "release/1.0", "r.txt", "r");
        app.Commit(repo, "feature/y", "y.txt", "y", from: "release/1.0");

        int prId;
        await using (var s = app.Scope(app.UserId("carol")))
        {
            prId = (await s.Get<PullRequestService>().CreateAsync(repo, "Backport", $"Fixes {project.Key}-{issue.Number}", "feature/y", "release/1.0", app.UserId("carol"))).Id;
            Assert.True((await s.Get<PullRequestService>().MergeAsync(prId, app.User("carol"), false, false)).Success);
        }

        await using var db = app.Db();
        Assert.NotEqual(IssueStatus.Done, (await db.Issues.AsNoTracking().FirstAsync(i => i.Id == issue.Id)).Status);
    }
}
