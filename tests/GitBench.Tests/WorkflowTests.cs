using System.Net;
using System.Net.Http.Json;
using GitBench.Data;
using GitBench.Services;
using GitBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Tests;

public class WorkflowTests(TestApp app) : IClassFixture<TestApp>
{
    private async Task<(Project Project, ProjectWorkflow Workflow, int Carol)> SetupAsync()
    {
        var project = await app.CreateProjectAsync("carol");
        await using var s = app.Scope(app.UserId("carol"));
        return (project, await s.Get<WorkflowService>().GetAsync(project.Id), app.UserId("carol"));
    }

    private Issue Load(int issueId)
    {
        using var db = app.Db();
        return db.Issues.AsNoTracking().First(i => i.Id == issueId);
    }

    /// <summary>To Do may only move to In Progress.</summary>
    private async Task RestrictTodoAsync(Project project, ProjectWorkflow wf)
    {
        await using var s = app.Scope(app.UserId("carol"));
        await s.Get<WorkflowService>().SetTransitionsAsync(project.Id, new Dictionary<int, HashSet<int>>
        {
            [wf.FirstOf(IssueStatus.Todo)!.Id] = [wf.FirstOf(IssueStatus.InProgress)!.Id],
        });
    }

    [Fact]
    public async Task Disallowed_transitions_are_refused_everywhere()
    {
        var (project, wf, carol) = await SetupAsync();
        await RestrictTodoAsync(project, wf);
        var issue = await app.CreateIssueAsync(project.Id, "carol");
        Assert.Equal(IssueStatus.Todo, issue.Status);

        await using var s = app.Scope(carol);
        var issues = s.Get<IssueService>();
        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => issues.SetStatusAsync(issue.Id, IssueStatus.Done, carol));
        Assert.Contains("doesn't allow moving", ex.Message);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => issues.MoveOnBoardAsync(issue.Id, wf.FirstOf(IssueStatus.Done)!.Id, null, carol));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => issues.SetWorkflowStatusAsync(issue.Id, wf.FirstOf(IssueStatus.InReview)!.Id, carol));
        Assert.Equal(IssueStatus.Todo, Load(issue.Id).Status);

        // The API answers 422.
        var client = await app.ApiClientAsync("carol");
        var r = await client.PatchAsJsonAsync($"/api/v1/projects/{project.Key}/issues/{issue.Number}", new { status = "done" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);

        // The allowed move works, and from In Progress (unrestricted) anything goes.
        await issues.SetStatusAsync(issue.Id, IssueStatus.InProgress, carol);
        await issues.SetStatusAsync(issue.Id, IssueStatus.Done, carol);
        var done = Load(issue.Id);
        Assert.Equal(IssueStatus.Done, done.Status);
        Assert.Equal(wf.FirstOf(IssueStatus.Done)!.Id, done.WorkflowStatusId);
        Assert.NotNull(done.ClosedAt);
    }

    [Fact]
    public async Task Status_and_category_stay_in_sync()
    {
        var (project, wf, carol) = await SetupAsync();
        await using var s = app.Scope(carol);
        var workflows = s.Get<WorkflowService>();
        var issues = s.Get<IssueService>();
        var qa = await workflows.AddStatusAsync(project.Id, "QA", "#123456", IssueStatus.InReview);
        var issue = await app.CreateIssueAsync(project.Id, "carol");

        // Setting the workflow status sets the category.
        await issues.SetWorkflowStatusAsync(issue.Id, qa.Id, carol);
        var i1 = Load(issue.Id);
        Assert.Equal((IssueStatus.InReview, (int?)qa.Id), (i1.Status, i1.WorkflowStatusId));

        // Setting the same category keeps the issue's own status (QA isn't the category's first status).
        Assert.NotEqual(qa.Id, wf.FirstOf(IssueStatus.InReview)!.Id);
        await issues.SetStatusAsync(issue.Id, IssueStatus.InReview, carol);
        Assert.Equal(qa.Id, Load(issue.Id).WorkflowStatusId);

        // Setting a category picks that category's first status.
        await issues.SetStatusAsync(issue.Id, IssueStatus.Backlog, carol);
        var i2 = Load(issue.Id);
        Assert.Equal((IssueStatus.Backlog, (int?)wf.FirstOf(IssueStatus.Backlog)!.Id), (i2.Status, i2.WorkflowStatusId));

        // Recategorising a status moves its issues' category (and closes them when it becomes Done).
        await issues.SetWorkflowStatusAsync(issue.Id, qa.Id, carol);
        await workflows.UpdateStatusAsync(project.Id, qa.Id, "Verified", "#00aa00", IssueStatus.Done);
        var i3 = Load(issue.Id);
        Assert.Equal(IssueStatus.Done, i3.Status);
        Assert.NotNull(i3.ClosedAt);

        // A status of another project is refused.
        var other = await app.CreateProjectAsync("carol");
        var foreign = (await workflows.GetAsync(other.Id)).FirstOf(IssueStatus.Todo)!;
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => issues.SetWorkflowStatusAsync(issue.Id, foreign.Id, carol));

        // Deleting a status with issues requires a target; the issues move and take its category.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => workflows.DeleteStatusAsync(project.Id, qa.Id, null));
        Assert.Equal(1, await workflows.DeleteStatusAsync(project.Id, qa.Id, wf.FirstOf(IssueStatus.Todo)!.Id));
        var i4 = Load(issue.Id);
        Assert.Equal((IssueStatus.Todo, (int?)wf.FirstOf(IssueStatus.Todo)!.Id), (i4.Status, i4.WorkflowStatusId));
        Assert.Null(i4.ClosedAt);
    }

    [Fact]
    public async Task Workflow_must_keep_a_done_and_an_open_status()
    {
        var (project, wf, carol) = await SetupAsync();
        await using var s = app.Scope(carol);
        var done = wf.FirstOf(IssueStatus.Done)!;
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => s.Get<WorkflowService>().DeleteStatusAsync(project.Id, done.Id, null));
        // Only Owners edit workflows.
        var dev = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Maintainer));
        await using var b = app.Scope(app.UserId("bob"));
        await Assert.ThrowsAsync<ForbiddenException>(() => b.Get<WorkflowService>().AddStatusAsync(dev.Id, "X", "#000000", IssueStatus.Todo));
    }

    [Fact]
    public async Task Bulk_status_change_is_refused_for_the_whole_batch()
    {
        var (project, wf, carol) = await SetupAsync();
        var todo = await app.CreateIssueAsync(project.Id, "carol", "in to do");
        var backlog = await app.CreateIssueAsync(project.Id, "carol", "in backlog");
        await using var s = app.Scope(carol);
        var issues = s.Get<IssueService>();
        await issues.SetStatusAsync(backlog.Id, IssueStatus.Backlog, carol);
        await RestrictTodoAsync(project, wf);

        // Backlog → Done is allowed, To Do → Done isn't: nothing changes.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            issues.BulkUpdateAsync([backlog.Id, todo.Id], new BulkIssueChange { Status = IssueStatus.Done, Priority = IssuePriority.Highest }, carol));
        Assert.Equal(IssueStatus.Backlog, Load(backlog.Id).Status);
        Assert.Equal(IssuePriority.Medium, Load(backlog.Id).Priority);
        Assert.Equal(IssueStatus.Todo, Load(todo.Id).Status);

        // An allowed batch applies to all, with an activity entry each.
        var result = await issues.BulkUpdateAsync([backlog.Id, todo.Id], new BulkIssueChange { Status = IssueStatus.InProgress }, carol);
        Assert.Equal(2, result.Changed);
        Assert.All(new[] { backlog.Id, todo.Id }, id => Assert.Equal(IssueStatus.InProgress, Load(id).Status));
        await using var db = app.Db();
        Assert.Equal(2, await db.Activities.CountAsync(a => (a.IssueId == backlog.Id || a.IssueId == todo.Id) && a.Text.Contains("to **In Progress**")));
    }

    [Fact]
    public async Task Bulk_edit_checks_permissions_for_every_project_before_changing_anything()
    {
        var mine = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        var notMine = await app.CreateProjectAsync("carol", ProjectVisibility.Internal); // bob is only a Viewer here
        var a = await app.CreateIssueAsync(mine.Id, "carol");
        var b = await app.CreateIssueAsync(notMine.Id, "carol");
        var bob = app.UserId("bob");
        await using var s = app.Scope(bob);
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            s.Get<IssueService>().BulkUpdateAsync([a.Id, b.Id], new BulkIssueChange { Priority = IssuePriority.Low }, bob));
        Assert.Equal(IssuePriority.Medium, Load(a.Id).Priority);
        // Developers can't bulk delete.
        await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<IssueService>().BulkUpdateAsync([a.Id], new BulkIssueChange { Delete = true }, bob));
    }
}
