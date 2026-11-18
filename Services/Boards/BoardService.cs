using System.Text.RegularExpressions;
using GitBench.Data;
using GitBench.Services.Audit;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Boards;

/// <summary>The editable settings of a board (everything but its columns and quick filters).</summary>
public sealed record BoardSettings(
    string Name,
    BoardType Type,
    string? FilterQuery = null,
    IReadOnlyList<int>? FilterLabelIds = null,
    int? FilterAssigneeId = null,
    IssueType? FilterType = null,
    int? FilterEpicId = null,
    BoardSwimlane Swimlane = BoardSwimlane.None,
    BoardCardFields CardFields = BoardCardFields.Default,
    bool BlockOverWip = false,
    int? DoneDays = null,
    bool StartOnActiveSprint = false)
{
    public static BoardSettings Of(Board b) => new(b.Name, b.Type, b.FilterQuery, b.FilterLabelIds, b.FilterAssigneeId, b.FilterType, b.FilterEpicId,
        b.Swimlane, b.CardFields, b.BlockOverWip, b.DoneDays, b.StartOnActiveSprint);
}

/// <summary>A column as edited in board settings.</summary>
public sealed record BoardColumnDraft(string Name, int? WipLimit, IReadOnlyList<int> StatusIds);

/// <summary>A quick filter as edited in board settings.</summary>
public sealed record BoardQuickFilterDraft(string Name, string Query);

/// <summary>Narrowing applied on top of the board's own filter when viewing it (toolbar filters and quick filters).</summary>
public sealed record BoardViewOptions
{
    /// <summary>Kanban only: one sprint; null = all issues. Scrum boards always show the active sprint.</summary>
    public int? SprintId { get; init; }
    public int? MilestoneId { get; init; }
    public int? AssigneeId { get; init; }
    public string? Search { get; init; }
    public IReadOnlyCollection<int> QuickFilterIds { get; init; } = [];
}

/// <summary>A board ready to render: its columns, the visible issues and the per-column counts that WIP limits use.</summary>
public sealed class BoardData
{
    public required Board Board { get; init; }
    public required ProjectWorkflow Workflow { get; init; }
    public required List<BoardColumnView> Columns { get; init; }
    /// <summary>Issues per column within the board's scope (its filter and sprint), before view options: what WIP limits count.</summary>
    public required Dictionary<string, int> ScopeCounts { get; init; }
    /// <summary>Visible issues in board order (scope plus view options).</summary>
    public required List<Issue> Issues { get; init; }
    public required Dictionary<int, EpicRef> Epics { get; init; }
    /// <summary>Scrum: the active sprint (null when there is none).</summary>
    public Sprint? Sprint { get; init; }

    public Dictionary<string, List<Issue>> ByColumn() => BoardLayout.Group(Columns, Workflow, Issues);
    public bool IsOverWip(BoardColumnView c) => BoardLayout.IsOverWip(c, ScopeCounts.GetValueOrDefault(c.Key));
}

/// <summary>
/// Project boards. Every project has a default board (created lazily, following the workflow: one column per status,
/// all issues), which is what <c>/p/{KEY}/board</c> shows; Maintainers can add more and configure columns, WIP limits,
/// filters, swimlanes, quick filters and card fields. Viewing needs ViewProject; managing boards needs Maintainer.
/// Moving a card goes through <see cref="IssueService.MoveOnBoardAsync(int,int,int?,int)"/>, so workflow transitions apply.
/// </summary>
public partial class BoardService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, IssueService issues, SprintService sprints,
    AuditLog audit, LiveEvents live)
{
    public const int MaxBoards = 30, MaxColumns = 20, MaxQuickFilters = 12;
    public const int DefaultKanbanDoneDays = 14;

    [GeneratedRegex(@"(^|[?&])assigneeId=me(?=&|$)", RegexOptions.IgnoreCase)]
    private static partial Regex AssigneeMeRx();

    // ---------- Reading ----------

    /// <summary>The project's boards, default first then by name (creating the default board if needed). Empty if the user can't view the project.</summary>
    public async Task<List<Board>> GetBoardsAsync(int projectId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await EnsureDefaultAsync(projectId);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Boards.AsNoTracking().Where(b => b.ProjectId == projectId)
            .OrderByDescending(b => b.IsDefault).ThenBy(b => b.Name).ThenBy(b => b.Id).ToListAsync();
    }

    /// <summary>A board of the project with its columns and quick filters; null if missing or not visible. Null <paramref name="boardId"/>: the default board.</summary>
    public async Task<Board?> GetBoardAsync(int projectId, int? boardId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return null;
        if (boardId == null) await EnsureDefaultAsync(projectId);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Boards.AsNoTracking().Include(b => b.Columns).Include(b => b.QuickFilters).AsSplitQuery()
            .FirstOrDefaultAsync(b => b.ProjectId == projectId && (boardId == null ? b.IsDefault : b.Id == boardId));
    }

    /// <summary>Gives the project its default board if it has none: follows the workflow, shows every issue, starts on the active sprint.</summary>
    internal async Task EnsureDefaultAsync(int projectId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.Boards.AnyAsync(b => b.ProjectId == projectId && b.IsDefault)) return;
        if (!await db.Projects.AnyAsync(p => p.Id == projectId)) return;
        db.Boards.Add(DefaultBoard(projectId));
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { /* created concurrently: the unique index kept it to one */ }
    }

    /// <summary>The default board: equivalent to the classic single board.</summary>
    public static Board DefaultBoard(int projectId) => new()
    {
        ProjectId = projectId, Name = "Board", Type = BoardType.Kanban, IsDefault = true, FollowsWorkflow = true,
        StartOnActiveSprint = true, DoneDays = null, CardFields = BoardCardFields.Default,
    };

    /// <summary>
    /// The board's columns and issues. Scope: the board's filter (query string plus simple fields), the active sprint on a
    /// Scrum board, and on a Kanban board with <see cref="Board.DoneDays"/> only recently closed Done issues. Then
    /// <paramref name="options"/> narrow what is shown (WIP counts ignore them).
    /// </summary>
    public async Task<BoardData> GetDataAsync(Board board, BoardViewOptions? options = null)
    {
        options ??= new BoardViewOptions();
        var workflow = await access.CanAsync(board.ProjectId, Permission.ViewProject)
            ? await WorkflowService.LoadAsync(dbFactory, board.ProjectId) : ProjectWorkflow.Empty;
        var columns = BoardLayout.Columns(board, workflow);
        var (scope, sprint) = await ScopeAsync(board);
        var grouped = BoardLayout.Group(columns, workflow, scope);

        IEnumerable<Issue> visible = scope;
        if (board.Type == BoardType.Kanban && options.SprintId is { } sid) visible = visible.Where(i => i.SprintId == sid);
        if (options.MilestoneId is { } m) visible = visible.Where(i => i.MilestoneId == m);
        if (options.AssigneeId is { } a) visible = visible.Where(i => a == 0 ? i.AssigneeId == null : i.AssigneeId == a);
        if (!string.IsNullOrWhiteSpace(options.Search))
        {
            var s = options.Search.Trim();
            visible = visible.Where(i => i.Title.Contains(s, StringComparison.OrdinalIgnoreCase) || i.Key.Contains(s, StringComparison.OrdinalIgnoreCase));
        }
        var shown = visible.ToList();
        foreach (var qf in board.QuickFilters.Where(q => options.QuickFilterIds.Contains(q.Id)))
        {
            var ids = (await issues.GetIssuesAsync(board.ProjectId, await ParseFilterAsync(qf.Query))).Select(i => i.Id).ToHashSet();
            shown = shown.Where(i => ids.Contains(i.Id)).ToList();
        }

        return new BoardData
        {
            Board = board, Workflow = workflow, Columns = columns, Sprint = sprint,
            ScopeCounts = grouped.ToDictionary(g => g.Key, g => g.Value.Count),
            Issues = shown,
            Epics = await EpicsAsync(shown),
        };
    }

    /// <summary>Issues within the board's scope, in board order, and the sprint a Scrum board shows.</summary>
    private async Task<(List<Issue> Issues, Sprint? Sprint)> ScopeAsync(Board board)
    {
        var f = await ParseFilterAsync(board.FilterQuery);
        if (board.FilterAssigneeId is { } a) f.AssigneeId = a;
        if (board.FilterType is { } t) f.Type = t;
        if (board.FilterEpicId is { } e) f.EpicId = e;
        Sprint? sprint = null;
        if (board.Type == BoardType.Scrum)
        {
            sprint = await sprints.GetActiveAsync(board.ProjectId);
            if (sprint == null) return ([], null);
            f.SprintId = sprint.Id;
        }
        IEnumerable<Issue> list = await issues.GetIssuesAsync(board.ProjectId, f);
        if (board.FilterLabelIds.Count > 0) list = list.Where(i => i.Labels.Any(l => board.FilterLabelIds.Contains(l.Id)));
        if (board.Type == BoardType.Kanban && board.DoneDays is { } days)
        {
            var cutoff = DateTime.UtcNow.AddDays(-days);
            list = list.Where(i => i.Status != IssueStatus.Done || (i.ClosedAt ?? i.UpdatedAt) >= cutoff);
        }
        return (list.OrderBy(i => i.SortOrder).ThenBy(i => i.Number).ToList(), sprint);
    }

    /// <summary>
    /// A board or quick filter query: the issue list's query string, where <c>assigneeId=me</c> means the viewer. Unlike
    /// the issue list, a query without <c>open=</c> covers open and closed issues (the board has a Done column).
    /// </summary>
    public async Task<IssueFilter> ParseFilterAsync(string? query) => ParseFilter(query, await access.ResolveUserIdAsync());

    public static IssueFilter ParseFilter(string? query, int? viewerId)
    {
        var q = (query ?? "").Trim().TrimStart('?');
        q = AssigneeMeRx().Replace(q, m => $"{m.Groups[1].Value}assigneeId={viewerId ?? -1}");
        var filter = IssueFilterQuery.Parse(q);
        if (!QueryHelpers.ParseQuery("?" + q).ContainsKey("open")) filter.Open = null;
        return filter;
    }

    /// <summary>The epic of each issue: its parent if that's an epic, else its grandparent if that is (sub-tasks of stories).</summary>
    private async Task<Dictionary<int, EpicRef>> EpicsAsync(List<Issue> list)
    {
        var result = new Dictionary<int, EpicRef>();
        var parentIds = list.Where(i => i.ParentId != null).Select(i => i.ParentId!.Value).Distinct().ToList();
        if (parentIds.Count == 0) return result;
        await using var db = await dbFactory.CreateDbContextAsync();
        var parents = await db.Issues.Where(i => parentIds.Contains(i.Id))
            .Select(i => new { i.Id, i.ParentId, i.Type, i.Number, i.Title, i.Project.Key }).ToDictionaryAsync(x => x.Id);
        var grandIds = parents.Values.Where(p => p.Type != IssueType.Epic && p.ParentId != null).Select(p => p.ParentId!.Value).Distinct().ToList();
        var grand = grandIds.Count == 0 ? [] : await db.Issues.Where(i => grandIds.Contains(i.Id) && i.Type == IssueType.Epic)
            .Select(i => new { i.Id, i.ParentId, i.Type, i.Number, i.Title, i.Project.Key }).ToDictionaryAsync(x => x.Id);
        foreach (var i in list)
        {
            if (i.ParentId is not { } pid || !parents.TryGetValue(pid, out var p)) continue;
            var epic = p.Type == IssueType.Epic ? p : p.ParentId is { } gid && grand.TryGetValue(gid, out var g) ? g : null;
            if (epic != null) result[i.Id] = new EpicRef(epic.Id, $"{epic.Key}-{epic.Number}", epic.Title);
        }
        return result;
    }

    // ---------- Moving cards ----------

    /// <summary>
    /// Drops an issue on a column (before <paramref name="beforeIssueId"/>, or at the end). The issue keeps its status when
    /// the column already shows it (a reorder), else moves to the column's first status the workflow allows. Refused
    /// (<see cref="InvalidOperationException"/>) when no status of the column is allowed, or when the board blocks drops
    /// past WIP limits and the column is full. Returns the status the issue is now in.
    /// </summary>
    public async Task<WorkflowStatus> MoveAsync(int boardId, int issueId, string columnKey, int? beforeIssueId, int actorId)
    {
        await access.RequireForIssueAsync(issueId, Permission.EditIssues, actorId);
        Board? board;
        Issue issue;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            board = await db.Boards.AsNoTracking().Include(b => b.Columns).Include(b => b.QuickFilters).AsSplitQuery().FirstOrDefaultAsync(b => b.Id == boardId);
            issue = await db.Issues.AsNoTracking().FirstAsync(i => i.Id == issueId);
        }
        if (board == null || board.ProjectId != issue.ProjectId) throw new InvalidOperationException("That issue isn't on this board.");
        var data = await GetDataAsync(board);
        var column = data.Columns.FirstOrDefault(c => c.Key == columnKey) ?? throw new InvalidOperationException("That column no longer exists. Reload the board.");
        var fromName = data.Workflow.Effective(issue)?.Name;
        var target = BoardLayout.DropTarget(column, data.Workflow, issue) ?? throw new InvalidOperationException(
            $"This project's workflow doesn't allow moving {(fromName != null ? $"an issue from \"{fromName}\"" : "this issue")} to the column \"{column.Name}\".");
        var alreadyIn = column.Contains(data.Workflow.Effective(issue)?.Id);
        if (board.BlockOverWip && BoardLayout.WouldExceedWip(column, data.ScopeCounts.GetValueOrDefault(column.Key), alreadyIn))
            throw new InvalidOperationException($"\"{column.Name}\" is at its WIP limit of {column.WipLimit}. Move something out of it first.");
        await issues.MoveOnBoardAsync(issueId, target.Id, beforeIssueId, actorId);
        return target;
    }

    // ---------- Managing boards (Maintainer+) ----------

    private Task RequireManageAsync(int projectId) => access.RequireRoleAsync(projectId, ProjectRole.Maintainer, "manage boards");

    /// <summary>Creates a board that follows the workflow. Kanban boards show Done issues of the last two weeks.</summary>
    public async Task<Board> CreateBoardAsync(int projectId, string name, BoardType type)
    {
        await RequireManageAsync(projectId);
        await EnsureDefaultAsync(projectId);
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.Boards.CountAsync(b => b.ProjectId == projectId) >= MaxBoards)
            throw new InvalidOperationException($"A project can have at most {MaxBoards} boards.");
        var board = new Board
        {
            ProjectId = projectId, Name = ValidName(name, 80, "Board names"), Type = type, FollowsWorkflow = true,
            DoneDays = type == BoardType.Kanban ? DefaultKanbanDoneDays : null,
        };
        db.Boards.Add(board);
        await db.SaveChangesAsync();
        await audit.RecordAsync("board.created", "board", board.Id, projectId, new { board.Name, type = type.ToString() });
        live.Publish(new BoardChanged(projectId));
        return board;
    }

    /// <summary>Saves a board's settings (name, type, filter, swimlanes, card fields, WIP blocking, Done window).</summary>
    public async Task UpdateBoardAsync(int boardId, BoardSettings s)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var board = await db.Boards.FirstOrDefaultAsync(b => b.Id == boardId) ?? throw new InvalidOperationException("Board not found.");
        await RequireManageAsync(board.ProjectId);
        if (s.DoneDays is < 1 or > 3650) throw new InvalidOperationException("The Done column window must be between 1 and 3650 days (or empty for all).");
        var query = s.FilterQuery?.Trim().TrimStart('?');
        if (query?.Length > 2000) throw new InvalidOperationException("The filter query is too long (max 2000 characters).");
        var before = BoardSettings.Of(board);
        board.Name = ValidName(s.Name, 80, "Board names");
        board.Type = s.Type;
        board.FilterQuery = string.IsNullOrEmpty(query) ? null : query;
        board.FilterLabelIds = s.FilterLabelIds?.Distinct().ToList() ?? [];
        board.FilterAssigneeId = s.FilterAssigneeId;
        board.FilterType = s.FilterType;
        board.FilterEpicId = s.FilterEpicId;
        board.Swimlane = s.Swimlane;
        board.CardFields = s.CardFields;
        board.BlockOverWip = s.BlockOverWip;
        board.DoneDays = s.DoneDays;
        board.StartOnActiveSprint = s.StartOnActiveSprint;
        await db.SaveChangesAsync();
        await audit.RecordAsync("board.settings_changed", "board", boardId, board.ProjectId, new { before = Describe(before), after = Describe(BoardSettings.Of(board)) });
        live.Publish(new BoardChanged(board.ProjectId));
    }

    private static object Describe(BoardSettings s) => new
    {
        s.Name, type = s.Type.ToString(), s.FilterQuery, labels = s.FilterLabelIds, assignee = s.FilterAssigneeId, issueType = s.FilterType?.ToString(),
        epic = s.FilterEpicId, swimlane = s.Swimlane.ToString(), cardFields = s.CardFields.ToString(), s.BlockOverWip, s.DoneDays, s.StartOnActiveSprint,
    };

    /// <summary>
    /// Replaces the board's columns (in order). Each status may be in at most one column; a status in none is not shown.
    /// The board stops following the workflow.
    /// </summary>
    public async Task SaveColumnsAsync(int boardId, IReadOnlyList<BoardColumnDraft> drafts)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var board = await db.Boards.Include(b => b.Columns).FirstOrDefaultAsync(b => b.Id == boardId) ?? throw new InvalidOperationException("Board not found.");
        await RequireManageAsync(board.ProjectId);
        if (drafts.Count is 0 or > MaxColumns) throw new InvalidOperationException($"A board needs between 1 and {MaxColumns} columns.");
        var workflow = await WorkflowService.LoadAsync(dbFactory, board.ProjectId);
        var seen = new HashSet<int>();
        var columns = new List<BoardColumn>();
        foreach (var (d, idx) in drafts.Select((d, i) => (d, i)))
        {
            var name = ValidName(d.Name, 40, "Column names");
            if (d.WipLimit is < 1 or > 999) throw new InvalidOperationException($"\"{name}\": WIP limits must be between 1 and 999 (or empty for none).");
            foreach (var sid in d.StatusIds)
            {
                var st = workflow.Find(sid) ?? throw new InvalidOperationException($"\"{name}\": that status isn't part of this project's workflow.");
                if (!seen.Add(sid)) throw new InvalidOperationException($"The status \"{st.Name}\" is in more than one column.");
            }
            columns.Add(new BoardColumn { Name = name, Order = idx, WipLimit = d.WipLimit, StatusIds = d.StatusIds.ToList() });
        }
        if (columns.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } dup)
            throw new InvalidOperationException($"There is more than one column named \"{dup.Key}\".");

        await using var tx = await db.Database.BeginTransactionAsync();
        db.BoardColumns.RemoveRange(board.Columns);
        board.FollowsWorkflow = false;
        await db.SaveChangesAsync();
        foreach (var c in columns) c.BoardId = boardId;
        db.BoardColumns.AddRange(columns);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        await audit.RecordAsync("board.columns_changed", "board", boardId, board.ProjectId, new
        {
            board.Name,
            columns = columns.Select(c => new { c.Name, c.WipLimit, statuses = c.StatusIds.Select(id => workflow.Find(id)!.Name).ToList() }).ToList(),
        });
        live.Publish(new BoardChanged(board.ProjectId));
    }

    /// <summary>Drops the board's own columns: it follows the workflow again (one column per status).</summary>
    public async Task ResetColumnsAsync(int boardId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var board = await db.Boards.Include(b => b.Columns).FirstOrDefaultAsync(b => b.Id == boardId) ?? throw new InvalidOperationException("Board not found.");
        await RequireManageAsync(board.ProjectId);
        if (board.FollowsWorkflow) return;
        db.BoardColumns.RemoveRange(board.Columns);
        board.FollowsWorkflow = true;
        await db.SaveChangesAsync();
        await audit.RecordAsync("board.columns_reset", "board", boardId, board.ProjectId, new { board.Name });
        live.Publish(new BoardChanged(board.ProjectId));
    }

    /// <summary>Replaces the board's quick filters (in order).</summary>
    public async Task SaveQuickFiltersAsync(int boardId, IReadOnlyList<BoardQuickFilterDraft> drafts)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var board = await db.Boards.Include(b => b.QuickFilters).FirstOrDefaultAsync(b => b.Id == boardId) ?? throw new InvalidOperationException("Board not found.");
        await RequireManageAsync(board.ProjectId);
        if (drafts.Count > MaxQuickFilters) throw new InvalidOperationException($"A board can have at most {MaxQuickFilters} quick filters.");
        var rows = drafts.Select((d, i) =>
        {
            var name = ValidName(d.Name, 40, "Quick filter names");
            var q = (d.Query ?? "").Trim().TrimStart('?');
            if (q.Length is 0 or > 2000) throw new InvalidOperationException($"\"{name}\": the query is required (max 2000 characters).");
            return new BoardQuickFilter { BoardId = boardId, Name = name, Query = q, Order = i };
        }).ToList();
        await using var tx = await db.Database.BeginTransactionAsync();
        db.BoardQuickFilters.RemoveRange(board.QuickFilters);
        await db.SaveChangesAsync();
        db.BoardQuickFilters.AddRange(rows);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        await audit.RecordAsync("board.quick_filters_changed", "board", boardId, board.ProjectId,
            new { board.Name, quickFilters = rows.Select(r => new { r.Name, r.Query }).ToList() });
        live.Publish(new BoardChanged(board.ProjectId));
    }

    /// <summary>Makes this board the one <c>/p/{KEY}/board</c> opens.</summary>
    public async Task MakeDefaultAsync(int boardId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var board = await db.Boards.FirstOrDefaultAsync(b => b.Id == boardId) ?? throw new InvalidOperationException("Board not found.");
        await RequireManageAsync(board.ProjectId);
        if (board.IsDefault) return;
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Boards.Where(b => b.ProjectId == board.ProjectId && b.IsDefault).ExecuteUpdateAsync(s => s.SetProperty(b => b.IsDefault, false));
        board.IsDefault = true;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        await audit.RecordAsync("board.made_default", "board", boardId, board.ProjectId, new { board.Name });
        live.Publish(new BoardChanged(board.ProjectId));
    }

    /// <summary>Deletes a board. The default board can't be deleted (make another one the default first).</summary>
    public async Task DeleteBoardAsync(int boardId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var board = await db.Boards.FirstOrDefaultAsync(b => b.Id == boardId) ?? throw new InvalidOperationException("Board not found.");
        await RequireManageAsync(board.ProjectId);
        if (board.IsDefault) throw new InvalidOperationException("The default board can't be deleted. Make another board the default first.");
        db.Boards.Remove(board);
        await db.SaveChangesAsync();
        await audit.RecordAsync("board.deleted", "board", boardId, board.ProjectId, new { board.Name });
        live.Publish(new BoardChanged(board.ProjectId));
    }

    private static string ValidName(string? name, int max, string what)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0 || name.Length > max) throw new InvalidOperationException($"{what} are required (max {max} characters).");
        return name;
    }
}
