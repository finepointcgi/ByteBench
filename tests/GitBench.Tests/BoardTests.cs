using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GitBench.Data;
using GitBench.Services;
using GitBench.Services.Boards;
using GitBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Tests;

public class BoardTests(TestApp app) : IClassFixture<TestApp>
{
    private int Carol => app.UserId("carol");

    private async Task<(Project Project, ProjectWorkflow Workflow)> SetupAsync()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        await using var s = app.Scope(Carol);
        return (project, await s.Get<WorkflowService>().GetAsync(project.Id));
    }

    private async Task<Issue> IssueInAsync(int projectId, WorkflowStatus status, string title = "An issue", IssueType type = IssueType.Task, int? parentId = null, int? sprintId = null)
    {
        var issue = await app.CreateIssueAsync(projectId, "carol", title, type, parentId, sprintId: sprintId);
        await using var s = app.Scope(Carol);
        if (issue.WorkflowStatusId != status.Id) await s.Get<IssueService>().SetWorkflowStatusAsync(issue.Id, status.Id, Carol);
        return issue;
    }

    private Issue Load(int issueId)
    {
        using var db = app.Db();
        return db.Issues.AsNoTracking().First(i => i.Id == issueId);
    }

    private static List<int> Ids(IEnumerable<Issue> issues) => issues.Select(i => i.Id).ToList();

    [Fact]
    public async Task Default_board_is_created_once_and_matches_the_classic_board()
    {
        var (project, wf) = await SetupAsync();
        foreach (var (st, n) in wf.Statuses.Select((st, n) => (st, n)))
            for (var k = 0; k <= n % 2; k++) await IssueInAsync(project.Id, st, $"{st.Name} {k}");

        await using var s = app.Scope(Carol);
        var boards = s.Get<BoardService>();
        var list = await boards.GetBoardsAsync(project.Id);
        Assert.Single(list);
        Assert.Single(await boards.GetBoardsAsync(project.Id)); // lazily created once
        var board = (await boards.GetBoardAsync(project.Id, null))!;
        Assert.True(board.IsDefault);
        Assert.True(board.FollowsWorkflow);
        Assert.True(board.StartOnActiveSprint);
        Assert.Equal(BoardCardFields.Default, board.CardFields);

        var data = await boards.GetDataAsync(board);
        // One column per workflow status, in workflow order...
        Assert.Equal(wf.Statuses.Select(x => x.Name), data.Columns.Select(c => c.Name));
        // ...holding exactly what the classic board showed in that status, in the same order (Done included).
        var classic = await s.Get<IssueService>().GetBoardAsync(project.Id, null, null);
        var byColumn = data.ByColumn();
        foreach (var (col, st) in data.Columns.Zip(wf.Statuses))
            Assert.Equal(Ids(classic.Where(i => wf.Effective(i)?.Id == st.Id)), Ids(byColumn[col.Key]));
        Assert.Equal(classic.Count, data.Issues.Count);

        // It follows the workflow: a new status shows up as a new column.
        var qa = await s.Get<WorkflowService>().AddStatusAsync(project.Id, "QA", "#123456", IssueStatus.InReview);
        data = await boards.GetDataAsync((await boards.GetBoardAsync(project.Id, null))!);
        Assert.Contains(data.Columns, c => c.Name == "QA" && c.Contains(qa.Id));

        // The default board can't be deleted.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => boards.DeleteBoardAsync(board.Id));
    }

    [Fact]
    public async Task Columns_map_several_statuses_and_validate()
    {
        var (project, wf) = await SetupAsync();
        await using var s = app.Scope(Carol);
        var boards = s.Get<BoardService>();
        var board = await boards.CreateBoardAsync(project.Id, "Team", BoardType.Kanban);
        var (backlog, todo, prog, review, done) = (wf.FirstOf(IssueStatus.Backlog)!, wf.FirstOf(IssueStatus.Todo)!, wf.FirstOf(IssueStatus.InProgress)!,
            wf.FirstOf(IssueStatus.InReview)!, wf.FirstOf(IssueStatus.Done)!);
        var a = await IssueInAsync(project.Id, todo);
        var b = await IssueInAsync(project.Id, review);
        var c = await IssueInAsync(project.Id, backlog);

        await boards.SaveColumnsAsync(board.Id, [
            new BoardColumnDraft("Ready", null, [todo.Id]),
            new BoardColumnDraft("Doing", 3, [prog.Id, review.Id]),
            new BoardColumnDraft("Done", null, [done.Id]),
        ]);
        var loaded = (await boards.GetBoardAsync(project.Id, board.Id))!;
        Assert.False(loaded.FollowsWorkflow);
        var data = await boards.GetDataAsync(loaded);
        Assert.Equal(["Ready", "Doing", "Done"], data.Columns.Select(x => x.Name));
        var byColumn = data.ByColumn();
        Assert.Equal([a.Id], Ids(byColumn[data.Columns[0].Key]));
        Assert.Equal([b.Id], Ids(byColumn[data.Columns[1].Key]));
        Assert.Equal(3, data.Columns[1].WipLimit);
        // Backlog is in no column: its issues aren't on the board.
        Assert.Equal([backlog.Id], BoardLayout.UnmappedStatuses(data.Columns, wf).Select(x => x.Id));
        Assert.DoesNotContain(byColumn.Values.SelectMany(x => x), i => i.Id == c.Id);

        // A status may only be in one column; statuses must belong to the project; names are required.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => boards.SaveColumnsAsync(board.Id,
            [new BoardColumnDraft("One", null, [todo.Id]), new BoardColumnDraft("Two", null, [todo.Id])]));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => boards.SaveColumnsAsync(board.Id, [new BoardColumnDraft("One", null, [999999])]));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => boards.SaveColumnsAsync(board.Id, [new BoardColumnDraft(" ", null, [todo.Id])]));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => boards.SaveColumnsAsync(board.Id, []));

        // Reset: follows the workflow again.
        await boards.ResetColumnsAsync(board.Id);
        data = await boards.GetDataAsync((await boards.GetBoardAsync(project.Id, board.Id))!);
        Assert.Equal(wf.Statuses.Count, data.Columns.Count);
        using var db = app.Db();
        Assert.False(db.BoardColumns.Any(x => x.BoardId == board.Id));
    }

    [Fact]
    public async Task Dropping_picks_the_first_allowed_status_and_enforces_transitions()
    {
        var (project, wf) = await SetupAsync();
        var (todo, prog, review, done) = (wf.FirstOf(IssueStatus.Todo)!, wf.FirstOf(IssueStatus.InProgress)!, wf.FirstOf(IssueStatus.InReview)!, wf.FirstOf(IssueStatus.Done)!);
        await using var s = app.Scope(Carol);
        var boards = s.Get<BoardService>();
        // To Do may only go to In Review; In Review may only go back to In Progress.
        await s.Get<WorkflowService>().SetTransitionsAsync(project.Id, new Dictionary<int, HashSet<int>> { [todo.Id] = [review.Id], [review.Id] = [prog.Id] });
        var board = await boards.CreateBoardAsync(project.Id, "Flow", BoardType.Kanban);
        await boards.SaveColumnsAsync(board.Id, [
            new BoardColumnDraft("Ready", null, [todo.Id]),
            new BoardColumnDraft("Doing", null, [prog.Id, review.Id]),
            new BoardColumnDraft("Done", null, [done.Id]),
        ]);
        var data = await boards.GetDataAsync((await boards.GetBoardAsync(project.Id, board.Id))!);
        var (ready, doing, doneCol) = (data.Columns[0], data.Columns[1], data.Columns[2]);
        var issue = await IssueInAsync(project.Id, todo);
        var other = await IssueInAsync(project.Id, todo, "Other");

        // The column's first status (In Progress) isn't allowed from To Do, so the drop picks In Review.
        Assert.Equal(review.Id, BoardLayout.DropTarget(doing, data.Workflow, Load(issue.Id))?.Id);
        var moved = await boards.MoveAsync(board.Id, issue.Id, doing.Key, null, Carol);
        Assert.Equal(review.Id, moved.Id);
        Assert.Equal((IssueStatus.InReview, (int?)review.Id), (Load(issue.Id).Status, Load(issue.Id).WorkflowStatusId));

        // Reordering within the column keeps the status.
        await boards.MoveAsync(board.Id, other.Id, ready.Key, issue.Id, Carol);
        Assert.Equal(review.Id, (await boards.MoveAsync(board.Id, issue.Id, doing.Key, null, Carol)).Id);

        // In Review → Done isn't allowed: refused, nothing changes.
        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => boards.MoveAsync(board.Id, issue.Id, doneCol.Key, null, Carol));
        Assert.Contains("doesn't allow", ex.Message);
        Assert.Equal(review.Id, Load(issue.Id).WorkflowStatusId);

        // Viewers/strangers can't move cards; unknown columns are refused.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => boards.MoveAsync(board.Id, issue.Id, "c0", null, Carol));
        await using var dan = app.Scope(app.UserId("dan"));
        await Assert.ThrowsAsync<ForbiddenException>(() => dan.Get<BoardService>().MoveAsync(board.Id, issue.Id, ready.Key, null, app.UserId("dan")));
    }

    [Fact]
    public async Task Wip_limits_warn_and_optionally_block()
    {
        var (project, wf) = await SetupAsync();
        var (todo, prog) = (wf.FirstOf(IssueStatus.Todo)!, wf.FirstOf(IssueStatus.InProgress)!);
        await using var s = app.Scope(Carol);
        var boards = s.Get<BoardService>();
        var board = await boards.CreateBoardAsync(project.Id, "WIP", BoardType.Kanban);
        await boards.SaveColumnsAsync(board.Id, [
            new BoardColumnDraft("Ready", null, [todo.Id]),
            new BoardColumnDraft("Doing", 1, [prog.Id]),
        ]);
        var a = await IssueInAsync(project.Id, todo, "A");
        var b = await IssueInAsync(project.Id, todo, "B");
        var c = await IssueInAsync(project.Id, todo, "C");
        var doingKey = (await boards.GetDataAsync((await boards.GetBoardAsync(project.Id, board.Id))!)).Columns[1].Key;

        // Not blocking: the column may go over its limit and is then flagged.
        await boards.MoveAsync(board.Id, a.Id, doingKey, null, Carol);
        var data = await boards.GetDataAsync((await boards.GetBoardAsync(project.Id, board.Id))!);
        Assert.False(data.IsOverWip(data.Columns[1]));
        await boards.MoveAsync(board.Id, b.Id, doingKey, null, Carol);
        data = await boards.GetDataAsync((await boards.GetBoardAsync(project.Id, board.Id))!);
        Assert.Equal(2, data.ScopeCounts[doingKey]);
        Assert.True(data.IsOverWip(data.Columns[1]));

        // Blocking: a full column refuses more, but its own cards can still be reordered.
        var loaded = (await boards.GetBoardAsync(project.Id, board.Id))!;
        await boards.UpdateBoardAsync(board.Id, BoardSettings.Of(loaded) with { BlockOverWip = true });
        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => boards.MoveAsync(board.Id, c.Id, doingKey, null, Carol));
        Assert.Contains("WIP limit", ex.Message);
        Assert.Equal(todo.Id, Load(c.Id).WorkflowStatusId);
        await boards.MoveAsync(board.Id, b.Id, doingKey, a.Id, Carol);

        // Pure rule: one more into a column at its limit exceeds it, unless the card is already there.
        var col = new BoardColumnView("x", 1, "X", [prog], 2);
        Assert.True(BoardLayout.WouldExceedWip(col, 2, alreadyInColumn: false));
        Assert.False(BoardLayout.WouldExceedWip(col, 2, alreadyInColumn: true));
        Assert.False(BoardLayout.WouldExceedWip(col, 1, alreadyInColumn: false));
        Assert.False(BoardLayout.IsOverWip(col, 2));
        Assert.True(BoardLayout.IsOverWip(col, 3));
    }

    [Fact]
    public async Task Swimlanes_group_by_assignee_epic_priority_and_type()
    {
        var (project, wf) = await SetupAsync();
        var todo = wf.FirstOf(IssueStatus.Todo)!;
        var epic = await IssueInAsync(project.Id, todo, "Checkout", IssueType.Epic);
        var story = await IssueInAsync(project.Id, todo, "Pay by card", IssueType.Story, epic.Id);
        var sub = await IssueInAsync(project.Id, todo, "Card form", IssueType.Subtask, story.Id);
        var loose = await IssueInAsync(project.Id, todo, "Loose bug", IssueType.Bug);
        await using var s = app.Scope(Carol);
        var issues = s.Get<IssueService>();
        await issues.UpdateIssueAsync(story.Id, Carol, (i, _) => { i.AssigneeId = app.UserId("bob"); i.Priority = IssuePriority.High; return Task.FromResult<string?>("x"); });
        await issues.UpdateIssueAsync(loose.Id, Carol, (i, _) => { i.AssigneeId = app.UserId("alice"); i.Priority = IssuePriority.Highest; return Task.FromResult<string?>("x"); });

        var boards = s.Get<BoardService>();
        var data = await boards.GetDataAsync((await boards.GetBoardAsync(project.Id, null))!);
        // Epics: the story's parent, and the sub-task's grandparent.
        Assert.Equal(epic.Id, data.Epics[story.Id].Id);
        Assert.Equal(epic.Id, data.Epics[sub.Id].Id);
        Assert.False(data.Epics.ContainsKey(loose.Id));

        var byEpic = BoardLayout.Lanes(data.Issues, BoardSwimlane.Epic, data.Epics);
        Assert.Equal(["e" + epic.Id, "e0"], byEpic.Select(l => l.Key));
        Assert.Equal([story.Id, sub.Id], Ids(byEpic[0].Issues));
        Assert.Equal([epic.Id, loose.Id], Ids(byEpic[1].Issues));

        var byAssignee = BoardLayout.Lanes(data.Issues, BoardSwimlane.Assignee, data.Epics);
        Assert.Equal(["Alice", "Bob", "Unassigned"], byAssignee.Select(l => l.Title.Split(' ')[0]));
        Assert.Equal([loose.Id], Ids(byAssignee[0].Issues));

        var byPriority = BoardLayout.Lanes(data.Issues, BoardSwimlane.Priority);
        Assert.Equal([IssuePriority.Highest.ToString(), IssuePriority.High.ToString(), IssuePriority.Medium.ToString()], byPriority.Select(l => l.Title));

        var byType = BoardLayout.Lanes(data.Issues, BoardSwimlane.Type);
        Assert.Equal(["Bug", "Story", "Epic", "Subtask"], byType.Select(l => l.Title));

        var none = Assert.Single(BoardLayout.Lanes(data.Issues, BoardSwimlane.None));
        Assert.Equal(4, none.Issues.Count);
    }

    [Fact]
    public async Task Scrum_boards_show_the_active_sprint_and_kanban_limits_done()
    {
        var (project, wf) = await SetupAsync();
        await using var s = app.Scope(Carol);
        var sprints = s.Get<SprintService>();
        var boards = s.Get<BoardService>();
        var scrum = await boards.CreateBoardAsync(project.Id, "Sprint board", BoardType.Scrum);
        var kanban = await boards.CreateBoardAsync(project.Id, "Flow", BoardType.Kanban);
        Assert.Equal(BoardService.DefaultKanbanDoneDays, kanban.DoneDays);

        var sprint = await sprints.CreateSprintAsync(project.Id, "Sprint 1");
        var inSprint = await IssueInAsync(project.Id, wf.FirstOf(IssueStatus.Todo)!, "In sprint", sprintId: sprint.Id);
        var outside = await IssueInAsync(project.Id, wf.FirstOf(IssueStatus.Todo)!, "Outside");
        var oldDone = await IssueInAsync(project.Id, wf.FirstOf(IssueStatus.Done)!, "Old done");
        var newDone = await IssueInAsync(project.Id, wf.FirstOf(IssueStatus.Done)!, "New done");
        await using (var db = app.Db())
            await db.Issues.Where(i => i.Id == oldDone.Id).ExecuteUpdateAsync(x => x.SetProperty(i => i.ClosedAt, DateTime.UtcNow.AddDays(-60)));

        // No active sprint yet: the Scrum board is empty.
        var data = await boards.GetDataAsync((await boards.GetBoardAsync(project.Id, scrum.Id))!);
        Assert.Null(data.Sprint);
        Assert.Empty(data.Issues);

        await sprints.StartSprintAsync(sprint.Id, DateTime.UtcNow.Date, DateTime.UtcNow.Date.AddDays(14), null);
        data = await boards.GetDataAsync((await boards.GetBoardAsync(project.Id, scrum.Id))!);
        Assert.Equal(sprint.Id, data.Sprint?.Id);
        Assert.Equal([inSprint.Id], Ids(data.Issues));

        // Kanban: everything open plus recently closed issues.
        data = await boards.GetDataAsync((await boards.GetBoardAsync(project.Id, kanban.Id))!);
        Assert.Equal(new[] { inSprint.Id, outside.Id, newDone.Id }.Order(), Ids(data.Issues).Order());
        // The default board keeps every Done issue.
        data = await boards.GetDataAsync((await boards.GetBoardAsync(project.Id, null))!);
        Assert.Contains(data.Issues, i => i.Id == oldDone.Id);
    }

    [Fact]
    public async Task Board_filter_and_quick_filters_narrow_the_issues()
    {
        var (project, wf) = await SetupAsync();
        var todo = wf.FirstOf(IssueStatus.Todo)!;
        var bug = await IssueInAsync(project.Id, todo, "A bug", IssueType.Bug);
        var mine = await IssueInAsync(project.Id, todo, "My task");
        var feature = await IssueInAsync(project.Id, todo, "A feature", IssueType.Feature);
        var doneBug = await IssueInAsync(project.Id, wf.FirstOf(IssueStatus.Done)!, "Fixed bug", IssueType.Bug);
        await using var s = app.Scope(Carol);
        await s.Get<IssueService>().UpdateIssueAsync(mine.Id, Carol, (i, _) => { i.AssigneeId = Carol; return Task.FromResult<string?>("x"); });
        var boards = s.Get<BoardService>();
        var board = await boards.CreateBoardAsync(project.Id, "Filtered", BoardType.Kanban);

        // A query without open= includes Done issues; the simple Type field narrows further.
        await boards.UpdateBoardAsync(board.Id, BoardSettings.Of(board) with { FilterQuery = "?type=Bug" });
        var data = await boards.GetDataAsync((await boards.GetBoardAsync(project.Id, board.Id))!);
        Assert.Equal(new[] { bug.Id, doneBug.Id }.Order(), Ids(data.Issues).Order());
        await boards.UpdateBoardAsync(board.Id, BoardSettings.Of(board) with { FilterType = IssueType.Feature });
        data = await boards.GetDataAsync((await boards.GetBoardAsync(project.Id, board.Id))!);
        Assert.Equal([feature.Id], Ids(data.Issues));

        // Quick filters: assigneeId=me is the viewer; several active quick filters must all match.
        await boards.UpdateBoardAsync(board.Id, BoardSettings.Of(board));
        await boards.SaveQuickFiltersAsync(board.Id, [new BoardQuickFilterDraft("Only my issues", "assigneeId=me"), new BoardQuickFilterDraft("Bugs", "type=Bug")]);
        var loaded = (await boards.GetBoardAsync(project.Id, board.Id))!;
        var (me, bugs) = (loaded.QuickFilters[0].Id, loaded.QuickFilters[1].Id);
        Assert.Equal([mine.Id], Ids((await boards.GetDataAsync(loaded, new BoardViewOptions { QuickFilterIds = [me] })).Issues));
        Assert.Equal(new[] { bug.Id, doneBug.Id }.Order(), Ids((await boards.GetDataAsync(loaded, new BoardViewOptions { QuickFilterIds = [bugs] })).Issues).Order());
        Assert.Empty((await boards.GetDataAsync(loaded, new BoardViewOptions { QuickFilterIds = [me, bugs] })).Issues);
        // WIP counts ignore quick filters.
        var withQuick = await boards.GetDataAsync(loaded, new BoardViewOptions { QuickFilterIds = [me] });
        Assert.Equal(4, withQuick.ScopeCounts.Values.Sum());
    }

    [Fact]
    public async Task Only_maintainers_manage_boards()
    {
        var (project, _) = await SetupAsync();
        await using var bob = app.Scope(app.UserId("bob")); // Developer
        await Assert.ThrowsAsync<ForbiddenException>(() => bob.Get<BoardService>().CreateBoardAsync(project.Id, "Nope", BoardType.Kanban));
        Assert.Single(await bob.Get<BoardService>().GetBoardsAsync(project.Id)); // but can view

        await using var carol = app.Scope(Carol);
        var boards = carol.Get<BoardService>();
        var board = await boards.CreateBoardAsync(project.Id, "Second", BoardType.Scrum);
        await Assert.ThrowsAsync<ForbiddenException>(() => bob.Get<BoardService>().SaveColumnsAsync(board.Id, [new BoardColumnDraft("X", null, [])]));
        await Assert.ThrowsAsync<ForbiddenException>(() => bob.Get<BoardService>().DeleteBoardAsync(board.Id));

        // Making it the default moves the flag; then the old default can be deleted.
        var oldDefault = (await boards.GetBoardAsync(project.Id, null))!;
        await boards.MakeDefaultAsync(board.Id);
        Assert.Equal(board.Id, (await boards.GetBoardAsync(project.Id, null))!.Id);
        await boards.DeleteBoardAsync(oldDefault.Id);
        Assert.Single(await boards.GetBoardsAsync(project.Id));

        // Strangers see nothing.
        await using var dan = app.Scope(app.UserId("dan"));
        Assert.Empty(await dan.Get<BoardService>().GetBoardsAsync(project.Id));
        Assert.Null(await dan.Get<BoardService>().GetBoardAsync(project.Id, board.Id));
    }

    [Fact]
    public async Task Api_lists_boards_and_returns_columns_with_issues()
    {
        var (project, wf) = await SetupAsync();
        var issue = await IssueInAsync(project.Id, wf.FirstOf(IssueStatus.InProgress)!, "On the board");
        await using (var s = app.Scope(Carol))
        {
            var team = await s.Get<BoardService>().CreateBoardAsync(project.Id, "Team", BoardType.Kanban);
            await s.Get<BoardService>().SaveColumnsAsync(team.Id, [new BoardColumnDraft("Work", 5, [wf.FirstOf(IssueStatus.Todo)!.Id, wf.FirstOf(IssueStatus.InProgress)!.Id])]);
        }
        var client = await app.ApiClientAsync("bob");
        var list = await client.GetFromJsonAsync<JsonElement>($"/api/v1/projects/{project.Key}/boards");
        Assert.Equal(2, list.GetArrayLength());
        Assert.True(list[0].GetProperty("isDefault").GetBoolean());
        var teamId = list.EnumerateArray().First(b => b.GetProperty("name").GetString() == "Team").GetProperty("id").GetInt32();

        var board = await client.GetFromJsonAsync<JsonElement>($"/api/v1/projects/{project.Key}/boards/{teamId}");
        var col = Assert.Single(board.GetProperty("columns").EnumerateArray());
        Assert.Equal("Work", col.GetProperty("name").GetString());
        Assert.Equal(5, col.GetProperty("wipLimit").GetInt32());
        Assert.Equal(2, col.GetProperty("statuses").GetArrayLength());
        Assert.Equal(issue.Id, Assert.Single(col.GetProperty("issues").EnumerateArray()).GetProperty("id").GetInt32());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/projects/{project.Key}/boards/999999")).StatusCode);
        var dan = await app.ApiClientAsync("dan");
        Assert.Equal(HttpStatusCode.NotFound, (await dan.GetAsync($"/api/v1/projects/{project.Key}/boards")).StatusCode);
    }
}
