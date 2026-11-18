using GitBench.Data;

namespace GitBench.Services.Boards;

/// <summary>A board column as shown: its workflow statuses (in drop preference order) and WIP limit.</summary>
/// <param name="Key">Stable id for the UI and API: <c>c{columnId}</c> for a stored column, <c>s{statusId}</c> for a column of a board that follows the workflow.</param>
public sealed record BoardColumnView(string Key, int? Id, string Name, IReadOnlyList<WorkflowStatus> Statuses, int? WipLimit)
{
    public bool Contains(int? statusId) => statusId != null && Statuses.Any(s => s.Id == statusId);
}

/// <summary>The epic an issue belongs to (its parent, or its parent's parent for a sub-task).</summary>
public sealed record EpicRef(int Id, string Key, string Title);

/// <summary>A swimlane: a heading and its issues, in board order.</summary>
public sealed record BoardLane(string Key, string Title, List<Issue> Issues, User? Assignee = null);

/// <summary>
/// Pure board logic, kept apart from <see cref="BoardService"/> so it is easy to test: which column an issue is in,
/// which status a drop picks, WIP limits and swimlanes.
/// </summary>
public static class BoardLayout
{
    /// <summary>The board's columns. A board that follows the workflow gets one column per status, in workflow order.</summary>
    public static List<BoardColumnView> Columns(Board board, ProjectWorkflow workflow)
    {
        if (board.FollowsWorkflow)
            return workflow.Statuses.Select(s => new BoardColumnView("s" + s.Id, null, s.Name, [s], null)).ToList();
        return board.Columns.OrderBy(c => c.Order).ThenBy(c => c.Id)
            .Select(c => new BoardColumnView("c" + c.Id, c.Id, c.Name,
                c.StatusIds.Distinct().Select(id => workflow.Find(id)).OfType<WorkflowStatus>().ToList(), c.WipLimit))
            .ToList();
    }

    /// <summary>Workflow statuses no column shows: issues in them are not on the board.</summary>
    public static List<WorkflowStatus> UnmappedStatuses(IReadOnlyList<BoardColumnView> columns, ProjectWorkflow workflow) =>
        workflow.Statuses.Where(s => !columns.Any(c => c.Contains(s.Id))).ToList();

    /// <summary>The column showing <paramref name="issue"/> (by its effective workflow status), or null if none does.</summary>
    public static BoardColumnView? ColumnOf(IReadOnlyList<BoardColumnView> columns, ProjectWorkflow workflow, Issue issue) =>
        workflow.Effective(issue) is { } s ? columns.FirstOrDefault(c => c.Contains(s.Id)) : null;

    /// <summary>Issues per column key, keeping the input order. Issues in unmapped statuses are left out.</summary>
    public static Dictionary<string, List<Issue>> Group(IReadOnlyList<BoardColumnView> columns, ProjectWorkflow workflow, IEnumerable<Issue> issues)
    {
        var result = columns.ToDictionary(c => c.Key, _ => new List<Issue>());
        foreach (var i in issues)
            if (ColumnOf(columns, workflow, i) is { } c) result[c.Key].Add(i);
        return result;
    }

    /// <summary>
    /// The status a card dropped on <paramref name="column"/> moves to: its current status if the column already shows it
    /// (a reorder), else the column's first status the workflow allows moving to; null if the workflow allows none.
    /// </summary>
    public static WorkflowStatus? DropTarget(BoardColumnView column, ProjectWorkflow workflow, Issue issue)
    {
        var from = workflow.Effective(issue);
        if (from != null && column.Contains(from.Id)) return from;
        return column.Statuses.FirstOrDefault(s => workflow.IsAllowed(from?.Id, s.Id));
    }

    /// <summary>More issues than the column's WIP limit: shown in the warning colour.</summary>
    public static bool IsOverWip(BoardColumnView column, int count) => column.WipLimit is { } l && count > l;

    /// <summary>Whether moving one more issue into the column (with <paramref name="count"/> now) would exceed its WIP limit.</summary>
    public static bool WouldExceedWip(BoardColumnView column, int count, bool alreadyInColumn) =>
        !alreadyInColumn && column.WipLimit is { } l && count + 1 > l;

    /// <summary>
    /// Splits issues into swimlanes, keeping their order within each lane. Lanes: assignees by name (Unassigned last),
    /// epics by number (No epic last), priorities highest first, types in declaration order. <see cref="BoardSwimlane.None"/>
    /// gives one lane with every issue.
    /// </summary>
    public static List<BoardLane> Lanes(IEnumerable<Issue> issues, BoardSwimlane mode, IReadOnlyDictionary<int, EpicRef>? epics = null)
    {
        var list = issues.ToList();
        epics ??= new Dictionary<int, EpicRef>();
        switch (mode)
        {
            case BoardSwimlane.Assignee:
                var byUser = list.GroupBy(i => i.AssigneeId).ToList();
                return byUser.Where(g => g.Key != null)
                    .Select(g => new BoardLane("a" + g.Key, g.First().Assignee?.DisplayName ?? $"User {g.Key}", g.ToList(), g.First().Assignee))
                    .OrderBy(l => l.Title, StringComparer.OrdinalIgnoreCase)
                    .Concat(byUser.Where(g => g.Key == null).Select(g => new BoardLane("a0", "Unassigned", g.ToList())))
                    .ToList();
            case BoardSwimlane.Epic:
                var byEpic = list.GroupBy(i => epics.GetValueOrDefault(i.Id)?.Id).ToList();
                return byEpic.Where(g => g.Key != null)
                    .Select(g => (Epic: epics[g.First().Id], Issues: g.ToList()))
                    .OrderBy(x => x.Epic.Key.Length).ThenBy(x => x.Epic.Key, StringComparer.Ordinal)
                    .Select(x => new BoardLane("e" + x.Epic.Id, $"{x.Epic.Key} {x.Epic.Title}", x.Issues))
                    .Concat(byEpic.Where(g => g.Key == null).Select(g => new BoardLane("e0", "No epic", g.ToList())))
                    .ToList();
            case BoardSwimlane.Priority:
                return list.GroupBy(i => i.Priority).OrderByDescending(g => g.Key)
                    .Select(g => new BoardLane("p" + (int)g.Key, g.Key.ToString(), g.ToList())).ToList();
            case BoardSwimlane.Type:
                return list.GroupBy(i => i.Type).OrderBy(g => g.Key)
                    .Select(g => new BoardLane("t" + (int)g.Key, g.Key.ToString(), g.ToList())).ToList();
            default:
                return [new BoardLane("all", "", list)];
        }
    }
}
