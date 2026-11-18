using GitBench.Data;
using GitBench.Services;
using GitBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Tests;

public class SprintTests(TestApp app) : IClassFixture<TestApp>
{
    private Issue Load(int id)
    {
        using var db = app.Db();
        return db.Issues.AsNoTracking().First(i => i.Id == id);
    }

    [Fact]
    public async Task Start_complete_and_carry_over()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        var carol = app.UserId("carol");
        var a = await app.CreateIssueAsync(project.Id, "carol", "A", storyPoints: 3);
        var b = await app.CreateIssueAsync(project.Id, "carol", "B", storyPoints: 5);
        var c = await app.CreateIssueAsync(project.Id, "carol", "C");
        await using var s = app.Scope(carol);
        var sprints = s.Get<SprintService>();

        var s1 = await sprints.CreateSprintAsync(project.Id, "Sprint 1");
        var s2 = await sprints.CreateSprintAsync(project.Id, "Sprint 2");
        foreach (var i in new[] { a, b, c }) await sprints.MoveIssueAsync(i.Id, s1.Id, null);

        // Developers plan (move issues) but can't start sprints.
        await using (var bs = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<ForbiddenException>(() => bs.Get<SprintService>().StartSprintAsync(s1.Id, DateTime.UtcNow, DateTime.UtcNow.AddDays(14), null));

        await sprints.StartSprintAsync(s1.Id, DateTime.UtcNow.Date, DateTime.UtcNow.Date.AddDays(14), "Ship it");
        var started = (await sprints.GetSprintAsync(project.Id, s1.Id))!;
        Assert.Equal(SprintState.Active, started.State);
        Assert.Equal((3, 8), (started.CommittedCount, started.CommittedPoints));

        // Only one active sprint per project.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => sprints.StartSprintAsync(s2.Id, DateTime.UtcNow, DateTime.UtcNow.AddDays(7), null));
        // A sub-task created under a sprint issue joins that sprint.
        var sub = await app.CreateIssueAsync(project.Id, "carol", "sub", IssueType.Subtask, parentId: a.Id);
        Assert.Equal(s1.Id, Load(sub.Id).SprintId);

        await s.Get<IssueService>().SetStatusAsync(a.Id, IssueStatus.Done, carol);
        await s.Get<IssueService>().SetStatusAsync(sub.Id, IssueStatus.Done, carol);

        // Carry-over into an existing sprint must name a planned sprint of this project.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => sprints.CompleteSprintAsync(s1.Id, CarryOver.ExistingSprint, targetSprintId: 999_999));

        var next = await sprints.CompleteSprintAsync(s1.Id, CarryOver.NewSprint, newSprintName: "Sprint 3");
        Assert.NotNull(next);
        Assert.Equal("Sprint 3", next.Name);
        var completed = (await sprints.GetSprintAsync(project.Id, s1.Id))!;
        Assert.Equal(SprintState.Completed, completed.State);
        Assert.Equal((2, 3), (completed.CompletedCount, completed.CompletedPoints)); // a and its sub-task (unestimated)

        Assert.Equal(s1.Id, Load(a.Id).SprintId);   // done work stays in the completed sprint
        Assert.Equal(next.Id, Load(b.Id).SprintId); // open work moves on
        Assert.Equal(next.Id, Load(c.Id).SprintId);
        await using (var db = app.Db())
            Assert.True(await db.Activities.AnyAsync(x => x.IssueId == b.Id && x.Text.StartsWith("moved this issue from sprint")));

        // Completed sprints accept no new issues and can't be deleted.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => sprints.MoveIssueAsync(b.Id, s1.Id, null));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => sprints.DeleteSprintAsync(s1.Id));

        // Next sprint: carry over to the backlog this time.
        await sprints.StartSprintAsync(next.Id, DateTime.UtcNow.Date, DateTime.UtcNow.Date.AddDays(7), null);
        Assert.Null(await sprints.CompleteSprintAsync(next.Id, CarryOver.Backlog));
        Assert.Null(Load(b.Id).SprintId);

        // Velocity lists both completed sprints.
        var velocity = await sprints.GetVelocityAsync(project.Id);
        Assert.Equal(2, velocity.Count);
        Assert.Equal(3, velocity[0].CompletedPoints);
    }

    [Fact]
    public async Task Burndown_counts_a_custom_done_category_status_as_done()
    {
        var project = await app.CreateProjectAsync("carol");
        var carol = app.UserId("carol");
        await using var s = app.Scope(carol);
        var issues = s.Get<IssueService>();
        var sprints = s.Get<SprintService>();
        var shipped = await s.Get<WorkflowService>().AddStatusAsync(project.Id, "Shipped", "#22c55e", IssueStatus.Done);
        var a = await app.CreateIssueAsync(project.Id, "carol", "A", storyPoints: 3);
        var b = await app.CreateIssueAsync(project.Id, "carol", "B", storyPoints: 5);
        var sprint = await sprints.CreateSprintAsync(project.Id, "Burn");
        await sprints.MoveIssueAsync(a.Id, sprint.Id, null);
        await sprints.MoveIssueAsync(b.Id, sprint.Id, null);
        var now = DateTime.UtcNow;
        await sprints.StartSprintAsync(sprint.Id, now.AddDays(-4), now.AddDays(3), null);

        // Real activity texts from the services, then backdated so the replay has a history:
        //   -6d created, -5d added to the sprint, -4d sprint started,
        //   -3d B → Shipped, -2d A → Shipped, -1d B reopened (Shipped → In Progress).
        async Task Do(Func<Task> action, int issueId, int daysAgo)
        {
            int before;
            await using (var db = app.Db()) before = await db.Activities.Where(x => x.IssueId == issueId).MaxAsync(x => x.Id);
            await action();
            await using (var db = app.Db())
                await db.Activities.Where(x => x.IssueId == issueId && x.Id > before)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.CreatedAt, now.AddDays(-daysAgo)));
        }
        await using (var db = app.Db())
        {
            await db.Issues.Where(i => i.Id == a.Id || i.Id == b.Id).ExecuteUpdateAsync(u => u.SetProperty(i => i.CreatedAt, now.AddDays(-6)));
            await db.Activities.Where(x => x.IssueId == a.Id || x.IssueId == b.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.CreatedAt, now.AddDays(-5)));
            await db.Sprints.Where(x => x.Id == sprint.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.StartedAt, now.AddDays(-4)));
        }
        await Do(() => issues.SetWorkflowStatusAsync(b.Id, shipped.Id, carol), b.Id, 3);
        await Do(() => issues.SetWorkflowStatusAsync(a.Id, shipped.Id, carol), a.Id, 2);
        await Do(() => issues.SetStatusAsync(b.Id, IssueStatus.InProgress, carol), b.Id, 1);

        await using (var db = app.Db())
            Assert.Contains(await db.Activities.Where(x => x.IssueId == b.Id).Select(x => x.Text).ToListAsync(),
                t => t.Contains("from **Shipped** to **In Progress**"));

        var burndown = (await sprints.GetBurndownAsync(project.Id, sprint.Id))!;
        int? Remaining(int daysAgo) => burndown.Days.Single(d => d.Date == now.AddDays(-daysAgo).Date).RemainingPoints;

        Assert.Equal(8, burndown.StartPoints);
        Assert.Equal(8, Remaining(4)); // at the start nothing was done
        Assert.Equal(3, Remaining(3)); // B shipped
        // A shipped too: 0 left. B counts as done only because "Shipped" (in its reopen activity) is a Done-category status.
        Assert.Equal(0, Remaining(2));
        Assert.Equal(5, Remaining(0)); // B reopened
        Assert.Null(burndown.Days.Last().RemainingPoints); // the future isn't drawn
    }
}
