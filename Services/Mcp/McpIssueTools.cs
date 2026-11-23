using System.Text;
using ByteBench.Data;
using ByteBench.Services.Query;
using Microsoft.AspNetCore.Identity;
using static ByteBench.Services.Mcp.McpSchema;

namespace ByteBench.Services.Mcp;

/// <summary>Tools for projects and issues. Every call runs as the token's user, through the same services and <see cref="ProjectAccess"/> checks as the REST API.</summary>
internal static class McpIssueTools
{
    private static readonly string[] Types = Enum.GetNames<IssueType>().Select(n => n.ToLowerInvariant()).ToArray();
    private static readonly string[] Priorities = Enum.GetNames<IssuePriority>().Select(n => n.ToLowerInvariant()).ToArray();

    public static IEnumerable<McpToolDef> All()
    {
        yield return new("list_projects", "List projects",
            "List the projects you can see, with your role and open/closed issue counts. Start here to find project keys (like WEB) for the other tools.",
            Obj(), ReadOnly: true, Destructive: false, Idempotent: true, ListProjects);

        yield return new("search_issues", "Search issues",
            "Search issues you can see. Pass `query` in the ByteBench query language (GQL), for example: project = WEB AND assignee = currentUser() AND statusCategory = \"In Progress\" ORDER BY priority DESC; " +
            "text search is: text ~ \"login bug\". Without `query`, pass `project` (and optionally `text` and `status`) for a simple listing. Returns at most `limit` issues per call; use `offset` to page.",
            Obj(null,
                ("query", Str("GQL query, e.g. project = WEB AND status = \"In Progress\". Searches every project you can see unless `project` narrows it.", 1000)),
                ("project", Str("Project key (see list_projects). With `query` it narrows the search to that project; without it, it is required.", 32)),
                ("text", Str("Simple search text (only without `query`): words in the title or description, or an issue key.", 200)),
                ("status", Str("Only without `query`: open (default), closed or all.", 10, values: ["open", "closed", "all"])),
                ("limit", Int("How many issues to return (default 20).", 1, 50)),
                ("offset", Int("How many issues to skip, for paging (default 0).", 0, 100000))),
            true, false, true, SearchIssues);

        yield return new("get_issue", "Get an issue",
            "Get one issue with its description, labels, assignee, recent comments, links to other issues, parent and children. `issue` is the key, like WEB-12.",
            Obj(["issue"], ("issue", Str("Issue key such as WEB-12.", 40))), true, false, true, GetIssue);

        yield return new("create_issue", "Create an issue",
            "Create an issue in a project you can edit (Developer role or higher). Returns the new issue's key and URL.",
            Obj(["project", "title"],
                ("project", Str("Project key (see list_projects).", 32)),
                ("title", Str("Short summary, at most 200 characters.", 200, minLength: 1)),
                ("description", Str("Details in Markdown.", 20000)),
                ("type", Str("Issue type (default task).", 20, values: Types)),
                ("priority", Str("Priority (default medium).", 20, values: Priorities)),
                ("assignee", Str("Username of the assignee; must be able to see the project.", 100)),
                ("labels", StrArray("Names of existing labels in the project.")),
                ("story_points", Int("Estimate in story points.", 0, 100))),
            false, false, false, CreateIssue);

        yield return new("update_issue", "Update an issue",
            "Change fields of an issue. Only the arguments you pass change. `status` is a workflow status name of the project (like \"In Progress\") or a category (backlog, todo, inProgress, inReview, done); " +
            "moves the project's workflow doesn't allow are refused. `assignee` is a username, or \"none\" to unassign. `labels` replaces the whole label set.",
            Obj(["issue"],
                ("issue", Str("Issue key such as WEB-12.", 40)),
                ("title", Str("New title (max 200 characters).", 200, minLength: 1)),
                ("description", Str("New description in Markdown (replaces the old one; an empty string clears it).", 20000)),
                ("status", Str("Workflow status name or category.", 40)),
                ("priority", Str("New priority.", 20, values: Priorities)),
                ("assignee", Str("Username, or \"none\" to unassign.", 100)),
                ("labels", StrArray("Names of existing labels; replaces the current set.")),
                ("story_points", Int("Estimate in story points.", 0, 100))),
            false, false, true, UpdateIssue);

        yield return new("comment_on_issue", "Comment on an issue",
            "Add a comment (Markdown) to an issue. Needs the Comment permission on its project.",
            Obj(["issue", "body"],
                ("issue", Str("Issue key such as WEB-12.", 40)),
                ("body", Str("The comment, in Markdown.", 20000, minLength: 1))),
            false, false, false, CommentOnIssue);

        yield return new("transition_issue", "Move an issue to a status",
            "Move an issue to another status of its project's workflow, for example \"In Progress\" or \"Done\". Use get_issue to see the current status. Refused if the workflow doesn't allow the move.",
            Obj(["issue", "status"],
                ("issue", Str("Issue key such as WEB-12.", 40)),
                ("status", Str("Workflow status name (like \"In Review\") or category (backlog, todo, inProgress, inReview, done).", 40, minLength: 1))),
            false, false, true, TransitionIssue);

        yield return new("my_work", "My assigned work",
            "List the open issues assigned to you (highest priority first, at most 25), and the open pull requests you authored.",
            Obj(), true, false, true, MyWork);
    }

    // ---------------------------------------------------------------- helpers

    internal static string StatusOf(Issue i) => Display.StatusName(i);

    internal static object Summary(Issue i, string baseUrl) => new
    {
        key = i.Key, title = i.Title, type = i.Type, status = StatusOf(i), category = i.Status, priority = i.Priority,
        assignee = i.Assignee?.UserName, labels = i.Labels.Select(l => l.Name).OrderBy(n => n).ToList(),
        points = i.StoryPoints, url = $"{baseUrl}/p/{i.Project.Key}/issues/{i.Number}",
    };

    private static string Line(Issue i) =>
        $"{i.Key} [{StatusOf(i)}] {i.Type} {i.Priority}" + (i.Assignee != null ? $" @{i.Assignee.UserName}" : "") + $" - {McpLimits.Cap(i.Title, 200)}";

    private static async Task<Issue> RequireIssueAsync(McpCall call, string key)
    {
        var (project, number) = McpArgs.ParseIssueKey(key);
        var issue = await call.Get<IssueService>().GetIssueAsync(project, number)
                    ?? throw new McpToolException($"Issue {project}-{number} was not found, or you can't see it.");
        call.ProjectKey = issue.Project.Key;
        call.ProjectId = issue.ProjectId;
        return issue;
    }

    /// <summary>A workflow status id (matched by name) or a category, from a model's free text.</summary>
    private static async Task<(int? WorkflowStatusId, IssueStatus? Category)> ResolveStatusAsync(McpCall call, Project project, string text)
    {
        var wf = await call.Get<WorkflowService>().GetAsync(project.Id);
        var name = text.Trim();
        if (wf.Statuses.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } byName) return (byName.Id, null);
        var normalized = name.Replace(" ", "").Replace("_", "").Replace("-", "");
        foreach (var c in Enum.GetValues<IssueStatus>())
            if (c.ToString().Equals(normalized, StringComparison.OrdinalIgnoreCase)) return (null, c);
        throw new McpToolException($"Unknown status '{text}'. This project's statuses: {string.Join(", ", wf.Statuses.Select(s => s.Name))}.");
    }

    private static async Task<int> ResolveAssigneeAsync(McpCall call, Project project, string username)
    {
        var user = await call.Get<UserManager<User>>().FindByNameAsync(username);
        if (user == null || !await call.Get<ProjectAccess>().CanAsync(project.Id, Permission.ViewProject, user.Id))
            throw new McpToolException($"'{username}' is not a user who can see project {project.Key}.");
        return user.Id;
    }

    private static List<Label> ResolveLabels(Project project, IEnumerable<string> names)
    {
        var result = new List<Label>();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
            result.Add(project.Labels.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                       ?? throw new McpToolException($"Unknown label '{name}'. Labels in {project.Key}: {string.Join(", ", project.Labels.Select(l => l.Name).Take(30))}."));
        return result;
    }

    private static T ParseEnum<T>(string value) where T : struct, Enum => Enum.Parse<T>(value, ignoreCase: true);

    // ---------------------------------------------------------------- tools

    private static async Task<McpToolResult> ListProjects(McpCall call)
    {
        var access = call.Get<ProjectAccess>();
        var all = await call.Get<IssueService>().GetProjectsAsync();
        var items = new List<object>();
        var sb = new StringBuilder();
        foreach (var p in all.Take(McpLimits.MaxListItems))
        {
            var role = await access.GetRoleAsync(p.Id);
            items.Add(new { key = p.Key, name = p.Name, description = McpLimits.Cap(p.Description, 300), role, openIssues = p.OpenCount, closedIssues = p.ClosedCount });
            sb.AppendLine($"{p.Key} - {p.Name} ({role}; {p.OpenCount} open, {p.ClosedCount} closed)");
        }
        var truncated = all.Count > McpLimits.MaxListItems;
        if (all.Count == 0) sb.AppendLine("No projects visible to this token's user.");
        if (truncated) sb.AppendLine($"{McpLimits.TruncatedMarker} showing {McpLimits.MaxListItems} of {all.Count} projects.");
        return McpToolResult.Ok(sb.ToString().TrimEnd(), new { projects = items, total = all.Count, truncated }, truncated);
    }

    private static async Task<McpToolResult> SearchIssues(McpCall call)
    {
        var limit = call.Args.Int("limit") ?? 20;
        var offset = call.Args.Int("offset") ?? 0;
        Project? project = call.Args.Str("project") is { Length: > 0 } pk ? await call.RequireProjectAsync(McpArgs.ParseProjectKey(pk)) : null;
        List<Issue> page;
        int total;
        if (call.Args.Str("query") is { } gql && !string.IsNullOrWhiteSpace(gql))
        {
            try
            {
                var r = await call.Get<GqlService>().SearchAsync(gql, project == null ? null : [project.Id], offset, limit);
                (page, total) = (r.Issues, r.Total);
            }
            catch (GqlException ex)
            {
                throw new McpToolException($"Invalid query: {ex.Message}. Example: project = WEB AND status = \"In Progress\" ORDER BY priority DESC.");
            }
        }
        else
        {
            if (project == null) throw new McpToolException("Pass `query` (GQL) or `project`.");
            var filter = new IssueFilter { Search = call.Args.Str("text"), Sort = "updated" };
            switch ((call.Args.Str("status") ?? "open").ToLowerInvariant())
            {
                case "closed": filter.Open = false; break;
                case "all": filter.Open = null; break;
                default: filter.Open = true; break;
            }
            var all = await call.Get<IssueService>().GetIssuesAsync(project.Id, filter);
            total = all.Count;
            page = all.Skip(offset).Take(limit).ToList();
        }
        var sb = new StringBuilder();
        foreach (var i in page) sb.AppendLine(Line(i));
        if (page.Count == 0) sb.AppendLine("No issues matched.");
        var truncated = offset + page.Count < total;
        if (truncated) sb.AppendLine($"{McpLimits.TruncatedMarker} {offset + page.Count} of {total} shown; pass offset={offset + page.Count} for more.");
        var baseUrl = call.BaseUrl;
        return McpToolResult.Ok(sb.ToString().TrimEnd(), new { issues = page.Select(i => Summary(i, baseUrl)).ToList(), total, offset, truncated }, truncated);
    }

    private static async Task<McpToolResult> GetIssue(McpCall call)
    {
        var issue = await RequireIssueAsync(call, call.Args.RequiredStr("issue"));
        var linked = call.Get<LinkedWorkService>();
        var hierarchy = await linked.GetHierarchyAsync(issue.Id);
        var links = await linked.GetLinksAsync(issue.Id);

        var description = McpLimits.Cap(issue.Description, McpLimits.MaxBodyChars, out var descTruncated);
        var allComments = issue.Comments.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToList();
        const int maxComments = 20;
        var shown = allComments.Skip(Math.Max(0, allComments.Count - maxComments)).ToList();
        var comments = shown.Select(c => new
        {
            author = c.Author.UserName, createdAt = Api.Dto.Utc(c.CreatedAt), body = McpLimits.Cap(c.Body, 2000),
        }).ToList();
        var parent = hierarchy.Ancestors.LastOrDefault();
        var children = hierarchy.Children.Take(50).ToList();
        var truncated = descTruncated || allComments.Count > shown.Count || hierarchy.Children.Count > children.Count;

        var sb = new StringBuilder();
        sb.AppendLine($"{issue.Key}: {issue.Title}");
        sb.AppendLine($"Status: {StatusOf(issue)} | Type: {issue.Type} | Priority: {issue.Priority} | Assignee: {issue.Assignee?.UserName ?? "unassigned"} | Reporter: {issue.Reporter.UserName}" +
                      (issue.StoryPoints != null ? $" | Points: {issue.StoryPoints}" : ""));
        if (issue.Labels.Count > 0) sb.AppendLine("Labels: " + string.Join(", ", issue.Labels.Select(l => l.Name).OrderBy(n => n)));
        sb.AppendLine($"URL: {call.BaseUrl}/p/{issue.Project.Key}/issues/{issue.Number}");
        if (parent != null) sb.AppendLine($"Parent: {parent.Key} {McpLimits.Cap(parent.Title, 120)}");
        if (children.Count > 0) sb.AppendLine("Children: " + string.Join("; ", children.Select(c => $"{c.Key} [{c.Status.Name()}] {McpLimits.Cap(c.Title, 80)}")));
        foreach (var l in links.Take(30)) sb.AppendLine($"Link: {LinkedWorkService.Phrase(l.Relation)} {l.Other.Key} [{l.Other.Status.Name()}] {McpLimits.Cap(l.Other.Title, 80)}");
        sb.AppendLine().AppendLine("Description:").AppendLine(string.IsNullOrWhiteSpace(description) ? "(none)" : description);
        if (comments.Count > 0)
        {
            sb.AppendLine().AppendLine(allComments.Count > shown.Count ? $"Comments (last {shown.Count} of {allComments.Count}):" : "Comments:");
            foreach (var c in comments) sb.AppendLine($"- {c.author} ({c.createdAt:yyyy-MM-dd HH:mm}): {c.body}");
        }
        var structured = new
        {
            issue = Summary(issue, call.BaseUrl), reporter = issue.Reporter.UserName, description, createdAt = Api.Dto.Utc(issue.CreatedAt), updatedAt = Api.Dto.Utc(issue.UpdatedAt),
            parent = parent == null ? null : new { key = parent.Key, title = parent.Title, status = parent.Status },
            children = children.Select(c => new { key = c.Key, title = c.Title, status = c.Status }).ToList(), totalChildren = hierarchy.Children.Count,
            links = links.Take(30).Select(l => new { relation = LinkedWorkService.Phrase(l.Relation), key = l.Other.Key, title = l.Other.Title, status = l.Other.Status }).ToList(),
            comments, totalComments = allComments.Count, truncated,
        };
        return McpToolResult.Ok(sb.ToString().TrimEnd(), structured, truncated);
    }

    private static async Task<McpToolResult> CreateIssue(McpCall call)
    {
        var project = await call.RequireProjectAsync(McpArgs.ParseProjectKey(call.Args.RequiredStr("project")));
        await call.Get<ProjectAccess>().RequireAsync(project.Id, Permission.EditIssues);
        var title = call.Args.RequiredStr("title").Trim();
        if (title.Length == 0) throw new McpToolException("'title' is required.");
        var issue = new Issue
        {
            Title = title,
            Description = string.IsNullOrWhiteSpace(call.Args.Str("description")) ? null : call.Args.Str("description"),
            Type = call.Args.Str("type") is { } t ? ParseEnum<IssueType>(t) : IssueType.Task,
            Priority = call.Args.Str("priority") is { } p ? ParseEnum<IssuePriority>(p) : IssuePriority.Medium,
            Status = IssueStatus.Todo,
            StoryPoints = call.Args.Int("story_points"),
        };
        if (call.Args.Str("assignee") is { Length: > 0 } a && !a.Equals("none", StringComparison.OrdinalIgnoreCase))
            issue.AssigneeId = await ResolveAssigneeAsync(call, project, a);
        var labelIds = ResolveLabels(project, call.Args.StrList("labels") ?? []).Select(l => l.Id).ToList();
        var created = await call.Get<IssueService>().CreateIssueAsync(project.Id, issue, labelIds, call.UserId);
        var url = $"{call.BaseUrl}/p/{project.Key}/issues/{created.Number}";
        return McpToolResult.Ok($"Created {project.Key}-{created.Number}: {title}\n{url}", new { key = $"{project.Key}-{created.Number}", url });
    }

    private static async Task<McpToolResult> UpdateIssue(McpCall call)
    {
        var existing = await RequireIssueAsync(call, call.Args.RequiredStr("issue"));
        var project = await call.RequireProjectAsync(existing.Project.Key);
        await call.Get<ProjectAccess>().RequireAsync(project.Id, Permission.EditIssues);
        var args = call.Args;
        string? title = args.Has("title") && args.Str("title") != null ? args.Str("title")!.Trim() : null;
        if (title is { Length: 0 }) throw new McpToolException("'title' can't be empty.");
        if (!new[] { "title", "description", "status", "priority", "assignee", "labels", "story_points" }.Any(args.Has))
            throw new McpToolException("Nothing to change: pass at least one of title, description, status, priority, assignee, labels, story_points.");

        int? assigneeId = null;
        var changeAssignee = args.Has("assignee") && args.Str("assignee") != null;
        var unassign = changeAssignee && args.Str("assignee")!.Equals("none", StringComparison.OrdinalIgnoreCase);
        if (changeAssignee && !unassign) assigneeId = await ResolveAssigneeAsync(call, project, args.Str("assignee")!);
        var labels = args.Has("labels") && args.StrList("labels") is { } names ? ResolveLabels(project, names) : null;
        (int? WorkflowStatusId, IssueStatus? Category)? status = args.Str("status") is { } st ? await ResolveStatusAsync(call, project, st) : null;
        var issues = call.Get<IssueService>();
        var users = call.Get<UserManager<User>>();

        await issues.UpdateIssueAsync(existing.Id, call.UserId, async (i, db) =>
        {
            var changes = new List<string>();
            if (title != null && i.Title != title) { changes.Add($"changed the title from ~~{i.Title}~~ to **{title}**"); i.Title = title; }
            if (args.Has("description") && args.Str("description") != null)
            {
                var d = string.IsNullOrWhiteSpace(args.Str("description")) ? null : args.Str("description");
                if (i.Description != d) { i.Description = d; changes.Add("updated the description"); }
            }
            if (status is { } s)
            {
                var (beforeStatus, beforeWorkflow) = (i.Status, i.WorkflowStatusId);
                if (s.WorkflowStatusId is { } ws) i.WorkflowStatusId = ws; else i.Status = s.Category!.Value;
                if (await issues.ReconcileStatusAsync(i, beforeStatus, beforeWorkflow) is { } change) changes.Add(change.Describe());
            }
            if (args.Str("priority") is { } p && ParseEnum<IssuePriority>(p) is var priority && i.Priority != priority) { i.Priority = priority; changes.Add($"set priority to **{priority}**"); }
            if (changeAssignee)
            {
                var newAssignee = unassign ? null : assigneeId;
                if (i.AssigneeId != newAssignee)
                {
                    i.AssigneeId = newAssignee;
                    changes.Add(newAssignee == null ? "removed the assignee" : newAssignee == call.UserId ? "self-assigned this issue"
                        : $"assigned to **{(await users.FindByIdAsync(newAssignee.Value.ToString()))?.DisplayName}**");
                }
            }
            if (args.Has("story_points") && args.Int("story_points") is { } points && i.StoryPoints != points)
            {
                changes.Add(SprintHistory.EstimateText(i.StoryPoints, points));
                i.StoryPoints = points;
            }
            if (labels != null)
            {
                var want = labels.Select(l => l.Id).ToHashSet();
                foreach (var removed in i.Labels.Where(l => !want.Contains(l.Id)).ToList())
                {
                    i.Labels.Remove(removed);
                    changes.Add($"removed label **{removed.Name}**");
                }
                foreach (var added in labels.Where(l => i.Labels.All(x => x.Id != l.Id)))
                {
                    i.Labels.Add((await db.Labels.FindAsync(added.Id))!);
                    changes.Add($"added label **{added.Name}**");
                }
            }
            return changes.Count == 0 ? null : string.Join(", ", changes);
        });

        var updated = await RequireIssueAsync(call, existing.Key);
        return McpToolResult.Ok($"Updated {updated.Key}.\n{Line(updated)}", new { issue = Summary(updated, call.BaseUrl) });
    }

    private static async Task<McpToolResult> CommentOnIssue(McpCall call)
    {
        var issue = await RequireIssueAsync(call, call.Args.RequiredStr("issue"));
        await call.Get<ProjectAccess>().RequireAsync(issue.ProjectId, Permission.Comment);
        var body = call.Args.RequiredStr("body");
        var comment = await call.Get<IssueService>().AddCommentAsync(issue.Id, call.UserId, body);
        return McpToolResult.Ok($"Added comment {comment.Id} to {issue.Key}.", new { issue = issue.Key, commentId = comment.Id });
    }

    private static async Task<McpToolResult> TransitionIssue(McpCall call)
    {
        var issue = await RequireIssueAsync(call, call.Args.RequiredStr("issue"));
        var project = await call.RequireProjectAsync(issue.Project.Key);
        await call.Get<ProjectAccess>().RequireAsync(project.Id, Permission.EditIssues);
        var status = await ResolveStatusAsync(call, project, call.Args.RequiredStr("status"));
        var issues = call.Get<IssueService>();
        var before = StatusOf(issue);
        await issues.UpdateIssueAsync(issue.Id, call.UserId, async (i, _) =>
        {
            var (beforeStatus, beforeWorkflow) = (i.Status, i.WorkflowStatusId);
            if (status.WorkflowStatusId is { } ws) i.WorkflowStatusId = ws; else i.Status = status.Category!.Value;
            return (await issues.ReconcileStatusAsync(i, beforeStatus, beforeWorkflow))?.Describe();
        });
        var updated = await RequireIssueAsync(call, issue.Key);
        return McpToolResult.Ok($"{updated.Key}: {before} -> {StatusOf(updated)}", new { issue = Summary(updated, call.BaseUrl), from = before, to = StatusOf(updated) });
    }

    private static async Task<McpToolResult> MyWork(McpCall call)
    {
        var assigned = await call.Get<IssueService>().GetAssignedToAsync(call.UserId);
        var sb = new StringBuilder("Assigned to you:\n");
        foreach (var i in assigned) sb.AppendLine(Line(i));
        if (assigned.Count == 0) sb.AppendLine("(nothing open)");

        var prs = call.Get<PullRequestService>();
        var visible = await call.Get<ProjectAccess>().VisibleProjectIdsAsync();
        var authored = new List<PullRequest>();
        foreach (var projectId in visible.Take(200))
        {
            authored.AddRange((await prs.ListAsync(projectId, PullRequestState.Open)).Where(p => p.AuthorId == call.UserId));
            if (authored.Count >= 25) break;
        }
        authored = authored.OrderByDescending(p => p.UpdatedAt).Take(25).ToList();
        sb.AppendLine().AppendLine("Your open pull requests:");
        foreach (var p in authored) sb.AppendLine($"{p.Repository.Project.Key}/{p.Repository.Slug}#{p.Number} {McpLimits.Cap(p.Title, 150)}{(p.IsDraft ? " (draft)" : "")}");
        if (authored.Count == 0) sb.AppendLine("(none)");
        return McpToolResult.Ok(sb.ToString().TrimEnd(), new
        {
            issues = assigned.Select(i => Summary(i, call.BaseUrl)).ToList(),
            pullRequests = authored.Select(p => new { repository = $"{p.Repository.Project.Key}/{p.Repository.Slug}", number = p.Number, title = p.Title, draft = p.IsDraft }).ToList(),
        });
    }
}
