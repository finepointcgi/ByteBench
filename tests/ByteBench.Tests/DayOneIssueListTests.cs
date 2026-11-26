using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Tests.Infrastructure;

namespace ByteBench.Tests;

/// <summary>Found by using ByteBench for a day: the issue list rendered every issue of a project (5,000 issues made a 15 MB page).</summary>
public class DayOneIssueListTests(TestApp app) : IClassFixture<TestApp>
{
    [Fact]
    public async Task The_issue_list_can_be_limited_to_the_first_rows_of_the_current_sort()
    {
        var p = await app.CreateProjectAsync("alice", ProjectVisibility.Private);
        for (var n = 1; n <= 7; n++) await app.CreateIssueAsync(p.Id, "alice", $"Issue {n}");

        await using var s = app.Scope(app.UserId("alice"));
        var issues = s.Get<IssueService>();
        var filter = new IssueFilter { Sort = "number" };

        Assert.Equal(7, (await issues.GetIssuesAsync(p.Id, filter)).Count);
        var first = await issues.GetIssuesAsync(p.Id, filter, take: 3);
        Assert.Equal(["Issue 1", "Issue 2", "Issue 3"], first.Select(i => i.Title));
        Assert.Equal(7, (await issues.GetIssuesAsync(p.Id, filter, take: 8)).Count);
    }

    [Fact]
    public async Task A_limited_list_still_hides_projects_the_user_cannot_see()
    {
        var p = await app.CreateProjectAsync("alice", ProjectVisibility.Private);
        await app.CreateIssueAsync(p.Id, "alice", "Secret");
        await using var s = app.Scope(app.UserId("bob"));
        Assert.Empty(await s.Get<IssueService>().GetIssuesAsync(p.Id, new IssueFilter(), take: 10));
    }
}
