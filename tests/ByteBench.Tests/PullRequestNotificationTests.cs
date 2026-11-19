using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Tests;

/// <summary>PR merged / declined / reopened notifications go to the PR's watchers (never the actor, never outsiders).</summary>
public class PullRequestNotificationTests(TestApp app) : IClassFixture<TestApp>
{
    private async Task<List<(string User, NotificationKind Kind)>> NotificationsSince(int afterId, int projectId)
    {
        await using var db = app.Db();
        return (await db.Notifications.Where(n => n.Id > afterId && n.ProjectId == projectId)
                .Select(n => new { n.User.UserName, n.Kind }).ToListAsync())
            .Select(n => (n.UserName!, n.Kind)).OrderBy(n => n.Item1).ToList();
    }

    private async Task<int> LastNotificationId()
    {
        await using var db = app.Db();
        return await db.Notifications.MaxAsync(n => (int?)n.Id) ?? 0;
    }

    [Fact]
    public async Task Merge_decline_and_reopen_notify_watchers_except_the_actor()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private,
            ("bob", ProjectRole.Developer), ("dan", ProjectRole.Developer));
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        app.Commit(repo, "feature/n", "n.txt", "n");
        var (bob, carol, dan) = (app.UserId("bob"), app.UserId("carol"), app.UserId("dan"));
        await using var s = app.Scope(bob);
        var prs = s.Get<PullRequestService>();

        // bob opens (auto-watches as author); dan comments (auto-watches as commenter). carol doesn't watch.
        var pr = await prs.CreateAsync(repo, "Feature N", null, "feature/n", "main", bob);
        await prs.AddCommentAsync(pr.Id, dan, "Looks good");

        // Declined by its author: dan hears about it, bob (the actor) doesn't.
        var mark = await LastNotificationId();
        await prs.DeclineAsync(pr.Id, bob);
        Assert.Equal([("dan", NotificationKind.PullRequestDeclined)], await NotificationsSince(mark, project.Id));

        // Reopened by a Maintainer+ (carol, the Owner): both watchers hear about it.
        mark = await LastNotificationId();
        await prs.ReopenAsync(pr.Id, carol);
        Assert.Equal([("bob", NotificationKind.PullRequestReopened), ("dan", NotificationKind.PullRequestReopened)],
            await NotificationsSince(mark, project.Id));

        // Merged by carol.
        mark = await LastNotificationId();
        await prs.MergeAsync(pr.Id, app.User("carol"), deleteSourceBranch: false, allowFastForward: false);
        Assert.Equal([("bob", NotificationKind.PullRequestMerged), ("dan", NotificationKind.PullRequestMerged)],
            await NotificationsSince(mark, project.Id));

        // Each notification links to the PR.
        await using var db = app.Db();
        var url = await db.Notifications.Where(n => n.Id > mark).Select(n => n.Url).FirstAsync();
        Assert.Equal($"/p/{project.Key}/repos/{repo.Slug}/pull-requests/{pr.Number}", url);
    }

    [Fact]
    public async Task Watchers_who_lost_access_are_not_notified()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private,
            ("bob", ProjectRole.Developer), ("dan", ProjectRole.Developer));
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        app.Commit(repo, "feature/m", "m.txt", "m");
        var (bob, dan) = (app.UserId("bob"), app.UserId("dan"));
        await using var s = app.Scope(bob);
        var prs = s.Get<PullRequestService>();
        var pr = await prs.CreateAsync(repo, "Feature M", null, "feature/m", "main", bob);
        await prs.AddCommentAsync(pr.Id, dan, "watching");

        await using (var c = app.Scope(app.UserId("carol")))
            await c.Get<MemberService>().RemoveMemberAsync(project.Id, dan);

        var mark = await LastNotificationId();
        await prs.MergeAsync(pr.Id, app.User("carol"), false, false);
        Assert.Equal([("bob", NotificationKind.PullRequestMerged)], await NotificationsSince(mark, project.Id));
    }

    [Fact]
    public async Task Only_the_author_or_a_merger_can_decline_and_reopen()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private,
            ("bob", ProjectRole.Developer), ("dan", ProjectRole.Developer));
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        app.Commit(repo, "feature/o", "o.txt", "o");
        await using var s = app.Scope(app.UserId("bob"));
        var prs = s.Get<PullRequestService>();
        var pr = await prs.CreateAsync(repo, "Feature O", null, "feature/o", "main", app.UserId("bob"));
        await Assert.ThrowsAsync<ForbiddenException>(() => prs.DeclineAsync(pr.Id, app.UserId("dan")));
        await prs.DeclineAsync(pr.Id, app.UserId("bob"));
        await Assert.ThrowsAsync<ForbiddenException>(() => prs.ReopenAsync(pr.Id, app.UserId("dan")));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => prs.DeclineAsync(pr.Id, app.UserId("bob"))); // already declined
    }
}
