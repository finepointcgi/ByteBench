using System.Text.Json;
using System.Text.Json.Nodes;
using GitBench.Data;
using GitBench.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Api;

/// <summary>/api/v1/projects/{key}/issues…, labels and milestones. All writes go through <see cref="IssueService"/>.</summary>
public static class IssueEndpoints
{
    public static void MapIssueEndpoints(this RouteGroupBuilder api)
    {
        var issues = api.MapGroup("/projects/{key}").WithTags("Issues");

        issues.MapGet("/issues", ListIssues).WithName("ListIssues")
            .WithSummary("List issues")
            .WithDescription("Filters: status (open [default], closed, all, or backlog/todo/inProgress/inReview/done), assignee (username or 'none'), label (name), milestone (id or 'none'), sprint (id, 'none' or 'active'), q (text or issue key), sort (updated [default], created, priority, due, number).");
        issues.MapGet("/issues/{number:int}", GetIssue).WithName("GetIssue").WithSummary("Get an issue");
        issues.MapPost("/issues", CreateIssue).WithName("CreateIssue").WithSummary("Create an issue")
            .WithJsonBody<CreateIssueRequest>().Produces<IssueDto>(StatusCodes.Status201Created);
        issues.MapPatch("/issues/{number:int}", UpdateIssue).WithName("UpdateIssue").WithSummary("Update an issue")
            .WithDescription("Only fields present in the body change. null clears assignee, milestoneId, parentId, storyPoints, dueDate and description; labels replaces the label set. Set the status with either status (a category: the issue gets that category's first workflow status unless it already has one in it) or workflowStatusId (see GET /projects/{key}/workflow); moves the workflow doesn't allow are refused with 422.")
            .WithJsonBody<UpdateIssueRequest>();
        issues.MapGet("/issues/{number:int}/comments", ListComments).WithName("ListIssueComments").WithSummary("List an issue's comments");
        issues.MapPost("/issues/{number:int}/comments", AddComment).WithName("AddIssueComment").WithSummary("Comment on an issue")
            .WithJsonBody<AddCommentRequest>().Produces<CommentDto>(StatusCodes.Status201Created);

        issues.MapGet("/workflow", GetWorkflow).WithName("GetWorkflow").WithSummary("Get the project's workflow")
            .WithDescription("Statuses in board order (each with its category, one of the issue status values) and allowed transitions. A status with no transitions listed may move to any status.")
            .WithTags("Projects");
        issues.MapGet("/labels", ListLabels).WithName("ListLabels").WithSummary("List labels").WithTags("Projects");
        issues.MapGet("/milestones", ListMilestones).WithName("ListMilestones").WithSummary("List milestones").WithTags("Projects");
    }

    public static async Task<Project> RequireProjectAsync(IssueService issues, string key) =>
        await issues.GetProjectAsync(key) ?? throw ApiException.NotFound($"Project '{key}' not found.");

    private static async Task<Issue> RequireIssueAsync(IssueService issues, string key, int number) =>
        await issues.GetIssueAsync(key, number) ?? throw ApiException.NotFound($"Issue {key.ToUpperInvariant()}-{number} not found.");

    private static async Task<List<IssueDto>> ListIssues(HttpContext http, IssueService issues, IDbContextFactory<AppDbContext> dbFactory, PublicBaseUrl url,
        string key, string? status, string? assignee, string? label, string? milestone, string? q, string? sort, string? sprint, SprintService sprints)
    {
        var paging = Paging.From(http.Request);
        var project = await RequireProjectAsync(issues, key);
        var filter = new IssueFilter { Search = q, Sort = sort ?? "updated" };
        switch (status?.ToLowerInvariant())
        {
            case null or "" or "open": filter.Open = true; break;
            case "closed": filter.Open = false; break;
            case "all": filter.Open = null; break;
            default:
                filter.Open = null;
                filter.Status = ParseEnum<IssueStatus>(status, "status");
                break;
        }
        if (!string.IsNullOrEmpty(assignee))
        {
            if (assignee.Equals("none", StringComparison.OrdinalIgnoreCase)) filter.AssigneeId = 0;
            else
            {
                await using var db = await dbFactory.CreateDbContextAsync();
                filter.AssigneeId = await db.Users.Where(u => u.UserName == assignee).Select(u => (int?)u.Id).FirstOrDefaultAsync() ?? -1;
            }
        }
        if (!string.IsNullOrEmpty(label))
            filter.LabelId = project.Labels.FirstOrDefault(l => l.Name.Equals(label, StringComparison.OrdinalIgnoreCase))?.Id ?? -1;
        if (!string.IsNullOrEmpty(milestone))
            filter.MilestoneId = milestone.Equals("none", StringComparison.OrdinalIgnoreCase) ? 0
                : int.TryParse(milestone, out var mid) ? mid : throw ApiException.BadRequest("milestone must be an id or 'none'.");

        if (!string.IsNullOrEmpty(sprint)) filter.SprintId = await SprintEndpoints.ResolveSprintFilterAsync(sprints, project.Id, sprint);

        var list = await issues.GetIssuesAsync(project.Id, filter);
        // GetIssuesAsync doesn't load reporters; fetch them for this page only.
        var page = list.Skip(paging.Skip).Take(paging.PerPage).ToList();
        paging.WriteHeaders(http, list.Count);
        var reporterIds = page.Select(i => i.ReporterId).Distinct().ToList();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var reporters = await db.Users.Where(u => reporterIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id);
            foreach (var i in page) i.Reporter = reporters[i.ReporterId];
        }
        var baseUrl = url.For(http.Request);
        return page.Select(i => Dto.Issue(i, baseUrl)).ToList();
    }

    private static async Task<IssueDto> GetIssue(HttpContext http, IssueService issues, PublicBaseUrl url, string key, int number) =>
        Dto.Issue(await RequireIssueAsync(issues, key, number), url.For(http.Request));

    private static async Task<IResult> CreateIssue(HttpContext http, IssueService issues, ApiCaller caller, UserManager<User> users,
        ProjectAccess access, PublicBaseUrl url, WorkflowService workflows, string key)
    {
        var project = await RequireProjectAsync(issues, key);
        var body = await ApiResults.ReadBodyAsync<CreateIssueRequest>(http.Request);
        // Check the permission before validating, so callers without EditIssues get 403 rather than validation details.
        await access.RequireAsync(project.Id, Permission.EditIssues);

        var title = body.Title?.Trim() ?? "";
        if (title.Length is 0 or > 200) throw ApiException.Unprocessable("title is required (max 200 characters).");
        var issue = new Issue
        {
            Title = title,
            Description = string.IsNullOrWhiteSpace(body.Description) ? null : body.Description,
            Type = body.Type ?? IssueType.Task,
            Priority = body.Priority ?? IssuePriority.Medium,
            Status = body.Status ?? IssueStatus.Todo,
            WorkflowStatusId = body.WorkflowStatusId is { } wsid ? await ValidWorkflowStatusAsync(workflows, project, wsid, body.Status) : null,
            StoryPoints = ValidPoints(body.StoryPoints),
            DueDate = body.DueDate?.ToDateTime(TimeOnly.MinValue),
            AssigneeId = string.IsNullOrEmpty(body.Assignee) ? null : await ResolveAssigneeAsync(users, access, project.Id, body.Assignee),
            MilestoneId = body.MilestoneId is { } mid ? ValidMilestone(project, mid) : null,
            ParentId = body.ParentId, // hierarchy rules are enforced by IssueService (422 if broken)
        };
        if (issue.Status == IssueStatus.Done) issue.ClosedAt = DateTime.UtcNow;
        var labelIds = ResolveLabels(project, body.Labels).Select(l => l.Id).ToList();

        var created = await issues.CreateIssueAsync(project.Id, issue, labelIds, caller.Id);
        var baseUrl = url.For(http.Request);
        var dto = Dto.Issue(await RequireIssueAsync(issues, project.Key, created.Number), baseUrl);
        return TypedResults.Created($"{baseUrl}/api/v1/projects/{project.Key}/issues/{created.Number}", dto);
    }

    private static async Task<IssueDto> UpdateIssue(HttpContext http, IssueService issues, ApiCaller caller, UserManager<User> users,
        ProjectAccess access, PublicBaseUrl url, WorkflowService workflows, string key, int number)
    {
        var issue = await RequireIssueAsync(issues, key, number);
        var project = await RequireProjectAsync(issues, key);
        var node = await ApiResults.ReadBodyAsync<JsonObject>(http.Request);
        UpdateIssueRequest body;
        try { body = node.Deserialize<UpdateIssueRequest>(Dto.Json) ?? new(); }
        catch (JsonException ex) { throw ApiException.BadRequest(ApiResults.InvalidJson(ex)); }
        bool Has(string name) => node.ContainsKey(name);
        await access.RequireAsync(project.Id, Permission.EditIssues);

        // Resolve and validate everything before changing anything.
        string? title = null;
        if (Has("title"))
        {
            title = body.Title?.Trim() ?? "";
            if (title.Length is 0 or > 200) throw ApiException.Unprocessable("title is required (max 200 characters).");
        }
        User? assignee = null;
        if (Has("assignee") && !string.IsNullOrEmpty(body.Assignee))
            assignee = await users.FindByIdAsync((await ResolveAssigneeAsync(users, access, project.Id, body.Assignee)).ToString());
        var milestone = Has("milestoneId") && body.MilestoneId is { } mid and not 0 ? project.Milestones.First(m => m.Id == ValidMilestone(project, mid)) : null;
        var labels = Has("labels") ? ResolveLabels(project, body.Labels) : null;
        var points = Has("storyPoints") ? ValidPoints(body.StoryPoints) : null;
        int? workflowStatusId = Has("workflowStatusId")
            ? await ValidWorkflowStatusAsync(workflows, project, body.WorkflowStatusId ?? throw ApiException.Unprocessable("workflowStatusId can't be null."), body.Status)
            : null;

        await issues.UpdateIssueAsync(issue.Id, caller.Id, async (i, db) =>
        {
            var changes = new List<string>();
            if (title != null && i.Title != title) { changes.Add($"changed the title from ~~{i.Title}~~ to **{title}**"); i.Title = title; }
            if (Has("description"))
            {
                var d = string.IsNullOrWhiteSpace(body.Description) ? null : body.Description;
                if (i.Description != d) { i.Description = d; changes.Add("updated the description"); }
            }
            var (beforeStatus, beforeWorkflow) = (i.Status, i.WorkflowStatusId);
            if (workflowStatusId is { } ws) i.WorkflowStatusId = ws;
            else if (body.Status is { } s) i.Status = s;
            if (await issues.ReconcileStatusAsync(i, beforeStatus, beforeWorkflow) is { } statusChange) changes.Add(statusChange.Describe());
            if (body.Priority is { } p && i.Priority != p) { i.Priority = p; changes.Add($"set priority to **{p}**"); }
            if (body.Type is { } t && i.Type != t) { i.Type = t; changes.Add($"changed type to **{t}**"); }
            if (Has("assignee") && i.AssigneeId != assignee?.Id)
            {
                i.AssigneeId = assignee?.Id;
                changes.Add(assignee == null ? "removed the assignee" : assignee.Id == caller.Id ? "self-assigned this issue" : $"assigned to **{assignee.DisplayName}**");
            }
            if (Has("milestoneId") && i.MilestoneId != milestone?.Id)
            {
                i.MilestoneId = milestone?.Id;
                changes.Add(milestone == null ? "removed the milestone" : $"added to milestone **{milestone.Title}**");
            }
            if (Has("storyPoints") && i.StoryPoints != points)
            {
                changes.Add(SprintHistory.EstimateText(i.StoryPoints, points));
                i.StoryPoints = points;
            }
            if (Has("dueDate"))
            {
                DateTime? due = body.DueDate?.ToDateTime(TimeOnly.MinValue);
                if (i.DueDate != due) { i.DueDate = due; changes.Add(due == null ? "removed the due date" : $"set the due date to **{due:MMM d, yyyy}**"); }
            }
            if (Has("parentId") && i.ParentId != body.ParentId)
            {
                changes.Add(await LinkedWorkService.ParentChangeTextAsync(db, i.ParentId, body.ParentId));
                i.ParentId = body.ParentId;
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
        return Dto.Issue(await RequireIssueAsync(issues, key, number), url.For(http.Request));
    }

    private static async Task<List<CommentDto>> ListComments(HttpContext http, IssueService issues, string key, int number)
    {
        var paging = Paging.From(http.Request);
        var issue = await RequireIssueAsync(issues, key, number);
        return paging.Apply(http, issue.Comments.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToList(), Dto.Comment);
    }

    private static async Task<IResult> AddComment(HttpContext http, IssueService issues, ApiCaller caller, ProjectAccess access, PublicBaseUrl url, string key, int number)
    {
        var issue = await RequireIssueAsync(issues, key, number);
        var body = await ApiResults.ReadBodyAsync<AddCommentRequest>(http.Request);
        await access.RequireAsync(issue.ProjectId, Permission.Comment);
        if (string.IsNullOrWhiteSpace(body.Body)) throw ApiException.Unprocessable("body is required.");
        var comment = await issues.AddCommentAsync(issue.Id, caller.Id, body.Body);
        comment.Author = caller.User;
        return TypedResults.Created($"{url.For(http.Request)}/api/v1/projects/{issue.Project.Key}/issues/{number}/comments", Dto.Comment(comment));
    }

    private static async Task<WorkflowDto> GetWorkflow(IssueService issues, WorkflowService workflows, string key)
    {
        var project = await RequireProjectAsync(issues, key);
        var wf = await workflows.GetAsync(project.Id);
        return new WorkflowDto(
            wf.Statuses.Select(s => new WorkflowStatusDto(s.Id, s.Name, s.Color, s.Order, s.Category)).ToList(),
            wf.Transitions.OrderBy(t => t.FromStatusId).ThenBy(t => t.ToStatusId).Select(t => new WorkflowTransitionDto(t.FromStatusId, t.ToStatusId)).ToList());
    }

    private static async Task<List<LabelDto>> ListLabels(HttpContext http, IssueService issues, string key)
    {
        var paging = Paging.From(http.Request);
        var project = await RequireProjectAsync(issues, key);
        return paging.Apply(http, project.Labels.OrderBy(l => l.Name).ToList(), Dto.Label);
    }

    private static async Task<List<MilestoneDto>> ListMilestones(HttpContext http, IssueService issues, string key)
    {
        var paging = Paging.From(http.Request);
        var project = await RequireProjectAsync(issues, key);
        var list = await issues.GetMilestonesAsync(project.Id);
        return paging.Apply(http, list, m => new MilestoneDto(m.Milestone.Id, m.Milestone.Title, m.Milestone.Description,
            Dto.Date(m.Milestone.StartDate), Dto.Date(m.Milestone.DueDate), m.Milestone.IsClosed, m.Open, m.Closed));
    }

    // ----- Validation helpers -----

    private static async Task<int> ResolveAssigneeAsync(UserManager<User> users, ProjectAccess access, int projectId, string username)
    {
        var user = await users.FindByNameAsync(username);
        if (user == null || !await access.CanAsync(projectId, Permission.ViewProject, user.Id))
            throw ApiException.Unprocessable($"'{username}' is not a user who can see this project.");
        return user.Id;
    }

    private static List<Label> ResolveLabels(Project project, IReadOnlyList<string>? names)
    {
        var result = new List<Label>();
        foreach (var name in (names ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            result.Add(project.Labels.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                       ?? throw ApiException.Unprocessable($"Unknown label '{name}'."));
        return result;
    }

    private static int ValidMilestone(Project project, int id) =>
        project.Milestones.Any(m => m.Id == id) ? id : throw ApiException.Unprocessable($"Milestone {id} doesn't exist in {project.Key}.");

    /// <summary>The id if it's a status of the project's workflow (and in <paramref name="category"/>, when that's given too).</summary>
    private static async Task<int> ValidWorkflowStatusAsync(WorkflowService workflows, Project project, int id, IssueStatus? category)
    {
        var status = (await workflows.GetAsync(project.Id)).Find(id)
                     ?? throw ApiException.Unprocessable($"Workflow status {id} doesn't exist in {project.Key}. See GET /api/v1/projects/{project.Key}/workflow.");
        if (category is { } c && c != status.Category)
            throw ApiException.Unprocessable($"status '{JsonNamingPolicy.CamelCase.ConvertName(c.ToString())}' doesn't match workflow status \"{status.Name}\" (category '{JsonNamingPolicy.CamelCase.ConvertName(status.Category.ToString())}'). Send one of them.");
        return id;
    }

    private static int? ValidPoints(int? points) =>
        points is null or (>= 0 and <= 100) ? points : throw ApiException.Unprocessable("storyPoints must be between 0 and 100.");

    public static T ParseEnum<T>(string value, string name) where T : struct, Enum =>
        Enum.TryParse<T>(value.Replace("_", "").Replace("-", ""), ignoreCase: true, out var v) && Enum.IsDefined(v)
            ? v : throw ApiException.BadRequest($"Unknown {name} '{value}'. Use one of: {string.Join(", ", Enum.GetNames<T>().Select(JsonNamingPolicy.CamelCase.ConvertName))}.");
}
