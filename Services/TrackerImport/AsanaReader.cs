using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using ByteBench.Data;
using static ByteBench.Services.TrackerImport.TrackerJson;

namespace ByteBench.Services.TrackerImport;

/// <summary>
/// Reads one Asana project through the REST API 1.0. The personal access token goes out only as the <c>Authorization: Bearer</c> header.
/// Imported: tasks (notes as the description, tags as labels, assignee, due date, completion, the section as the status, a
/// story-points number field, a priority field or tag), subtasks (one level, as sub-tasks), dependents as "blocks" links, comments
/// (stories of type comment) and attachment links. A milestone task gets a <c>milestone</c> label. Not imported: portfolios, goals,
/// forms, rules, followers, likes, rich-text formatting (the plain <c>notes</c> are used) and attachment files. Asana has no
/// milestones of its own in this sense, so none are created. Tasks are read in the project's own order.
/// </summary>
public sealed partial class AsanaReader(SourceConnection connection, TrackerHttp http) : ISourceReader
{
    private const string What = "Asana";
    private const string TaskFields = "name,notes,completed,completed_at,created_at,modified_at,due_on,due_at,assignee.name,assignee.email,created_by.name,"
        + "tags.name,tags.color,memberships.project.gid,memberships.section.name,parent.gid,num_subtasks,permalink_url,resource_subtype,"
        + "custom_fields.name,custom_fields.type,custom_fields.number_value,custom_fields.display_value,custom_fields.enum_value.name";
    private readonly string api = NormalizeBase(connection.BaseUrl);
    private string Gid => connection.Project.Trim();
    private JsonElement project;
    private bool projectLoaded;
    private List<SourceStatus>? sections;

    private static string NormalizeBase(string? baseUrl)
    {
        var b = string.IsNullOrWhiteSpace(baseUrl) ? "https://app.asana.com/api/1.0" : baseUrl.Trim().TrimEnd('/');
        return b.EndsWith("/api/1.0", StringComparison.OrdinalIgnoreCase) ? b : b + "/api/1.0";
    }

    private void Auth(HttpRequestMessage r) => r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + connection.Secret!.Trim());

    private Task<TrackerResponse> GetAsync(string path, string? query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connection.Secret))
            throw new SourceException("Asana needs a personal access token (developer console, Personal access tokens).", authFailure: true);
        return http.GetJsonAsync(api + path + (string.IsNullOrEmpty(query) ? "" : "?" + query), Auth, ct, What);
    }

    /// <summary>Streams the <c>data</c> items of a paged list. The next page is asked for by <c>offset</c> on OUR address, never by following Asana's own link.</summary>
    private async IAsyncEnumerable<JsonElement> PagesAsync(string path, string query, [EnumeratorCancellation] CancellationToken ct)
    {
        string? offset = null;
        for (var guard = 0; guard < 100000; guard++)
        {
            var r = await GetAsync(path, query + (offset != null ? "&offset=" + Uri.EscapeDataString(offset) : ""), ct);
            foreach (var item in Items(r.Body, "data")) yield return item;
            offset = Str(r.Body, "next_page.offset");
            if (string.IsNullOrEmpty(offset)) yield break;
        }
    }

    // --- project -------------------------------------------------------------------------------------------------------

    private async Task<JsonElement> EnsureProjectAsync(CancellationToken ct)
    {
        if (projectLoaded) return project;
        var r = await GetAsync("/projects/" + Uri.EscapeDataString(Gid), "opt_fields=name,notes,archived", ct);
        var data = Prop(r.Body, "data");
        if (data.ValueKind != JsonValueKind.Object) throw new SourceException("Asana's answer wasn't a project (is the address the right site?).");
        project = data;
        projectLoaded = true;
        return project;
    }

    public async Task<SourceProbe> ProbeAsync(CancellationToken ct)
    {
        var p = await EnsureProjectAsync(ct);
        var count = 0;
        try
        {
            var c = await GetAsync("/projects/" + Uri.EscapeDataString(Gid) + "/task_counts", "opt_fields=num_tasks,num_completed_tasks", ct);
            if (Num(c.Body, "data.num_tasks") is { } n) count = (int)n;
        }
        catch (SourceException ex) when (!ex.AuthFailure) { /* the count is a nicety */ }
        var first = await GetAsync("/projects/" + Uri.EscapeDataString(Gid) + "/tasks", "limit=3&opt_fields=name", ct);
        var samples = Items(first.Body, "data").Select(t => Str(t, "name")).OfType<string>().Take(3).ToList();
        return new SourceProbe(Str(p, "name") ?? Gid, Str(p, "notes"), count, samples, Bool(p, "archived") ? "This project is archived in Asana." : null);
    }

    public async Task<IReadOnlyList<SourceStatus>> ReadStatusesAsync(CancellationToken ct)
    {
        if (sections != null) return sections;
        var list = new List<SourceStatus>();
        await foreach (var s in PagesAsync("/projects/" + Uri.EscapeDataString(Gid) + "/sections", "limit=100&opt_fields=name", ct))
            if (Str(s, "name") is { Length: > 0 } name && !list.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                list.Add(new SourceStatus(name, CategoryFromName(name)));
        return sections = list;
    }

    /// <summary>The project's tags can't be listed without the workspace, so labels are created from the tags the tasks use.</summary>
    public Task<IReadOnlyList<SourceLabel>> ReadLabelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SourceLabel>>([]);

    public Task<IReadOnlyList<SourceMilestone>> ReadMilestonesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SourceMilestone>>([]);

    // --- tasks ---------------------------------------------------------------------------------------------------------

    public async IAsyncEnumerable<SourceIssue> ReadIssuesAsync(SourceReadOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        var statuses = (await ReadStatusesAsync(ct)).ToDictionary(s => s.Name, s => s.Category, StringComparer.OrdinalIgnoreCase);
        var query = "limit=100&opt_fields=" + TaskFields + (options.IncludeClosed ? "" : "&completed_since=now"); // "now" leaves out tasks completed earlier
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        await foreach (var task in PagesAsync("/projects/" + Uri.EscapeDataString(Gid) + "/tasks", query, ct))
        {
            if (Str(task, "gid") is not { } gid || !seen.Add(gid)) continue;
            if (!options.IncludeClosed && Bool(task, "completed")) continue;
            var issue = await MapAsync(task, statuses, null, options, ct);
            yield return issue;
            if (++count >= options.MaxIssues) yield break;

            if (Num(task, "num_subtasks") is > 0 && Str(task, "parent.gid") == null)
                await foreach (var sub in PagesAsync("/tasks/" + gid + "/subtasks", "limit=100&opt_fields=" + TaskFields, ct))
                {
                    if (Str(sub, "gid") is not { } sgid || !seen.Add(sgid)) continue;
                    if (!options.IncludeClosed && Bool(sub, "completed")) continue;
                    yield return await MapAsync(sub, statuses, (gid, issue.Type == IssueType.Epic), options, ct);
                    if (++count >= options.MaxIssues) yield break;
                }
        }
    }

    private async Task<SourceIssue> MapAsync(JsonElement t, Dictionary<string, IssueStatus> statuses, (string Gid, bool Epic)? parent, SourceReadOptions options, CancellationToken ct)
    {
        var gid = Str(t, "gid")!;
        var notes = new List<string>();
        var tags = Items(t, "tags").Select(x => Str(x, "name")?.Trim()).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var labels = tags.ToList();
        if (Str(t, "resource_subtype") == "milestone" && !labels.Contains("milestone", StringComparer.OrdinalIgnoreCase)) labels.Add("milestone");

        // A task listed as a subtask of the project (its parent is named) is a sub-task; so is every fetched subtask, unless its parent is an epic.
        string? parentGid = parent?.Gid ?? Str(t, "parent.gid");
        var type = TypeFromLabels(tags);
        if (parentGid != null)
        {
            if (parent is { Epic: true }) { if (type == IssueType.Epic) type = IssueType.Task; }
            else type = IssueType.Subtask;
        }
        if (parentGid != null && Num(t, "num_subtasks") is > 0) notes.Add("has its own sub-tasks in Asana; only one level of subtasks is imported.");

        var completed = Bool(t, "completed");
        var section = Items(t, "memberships").Where(m => Str(m, "project.gid") == Gid).Select(m => Str(m, "section.name")).FirstOrDefault(n => !string.IsNullOrEmpty(n));
        var sectionCategory = section != null && statuses.TryGetValue(section, out var sc) ? sc : IssueStatus.Todo;
        var category = completed ? IssueStatus.Done : sectionCategory;
        var statusName = section != null && (!completed || sectionCategory == IssueStatus.Done) ? section : null;

        int? points = null;
        IssuePriority? priority = null;
        foreach (var f in Items(t, "custom_fields"))
        {
            var name = Str(f, "name") ?? "";
            if (points == null && Str(f, "type") == "number" && Num(f, "number_value") is { } nv && nv >= 0 && PointsName().IsMatch(name)) points = (int)Math.Round(nv);
            if (priority == null && name.Trim().Equals("priority", StringComparison.OrdinalIgnoreCase))
                priority = PriorityFromWord(Str(f, "display_value") ?? Str(f, "enum_value.name"));
        }
        priority ??= tags.Select(PriorityFromTag).FirstOrDefault(p => p != null);

        var comments = new List<SourceComment>();
        var links = new List<SourceLink>();
        if (options.IncludeComments)
        {
            await foreach (var s in PagesAsync("/tasks/" + gid + "/stories", "limit=100&opt_fields=type,text,created_by.name,created_at", ct))
            {
                if (Str(s, "type") != "comment" || Str(s, "gid") is not { } sid || Str(s, "text") is not { Length: > 0 } text) continue;
                var by = Str(s, "created_by.name");
                comments.Add(new SourceComment(sid, by == null ? null : new SourceUser(null, by, null), text, DateTimeOf(s, "created_at") ?? DateTime.UtcNow));
            }
            // "This task blocks those": the other side's own read finds nothing, so recording it here is enough.
            await foreach (var d in PagesAsync("/tasks/" + gid + "/dependents", "limit=100&opt_fields=gid", ct))
                if (Str(d, "gid") is { } dep && !links.Any(l => l.TargetExternalId == dep)) links.Add(new SourceLink(dep, IssueLinkType.Blocks));
        }

        var attachments = new List<SourceAttachment>();
        await foreach (var a in PagesAsync("/attachments", "parent=" + gid + "&limit=100&opt_fields=name,permanent_url,view_url,download_url", ct))
        {
            var url = Str(a, "permanent_url") ?? Str(a, "view_url") ?? Str(a, "download_url");
            var fileName = Str(a, "name") ?? url;
            if (fileName != null) attachments.Add(new SourceAttachment(fileName, url));
        }

        var assigneeName = Str(t, "assignee.name");
        var assigneeEmail = Str(t, "assignee.email");
        var author = Str(t, "created_by.name");
        return new SourceIssue
        {
            ExternalId = gid, DisplayKey = "#" + (gid.Length > 6 ? gid[^6..] : gid), Url = Str(t, "permalink_url"), Title = Str(t, "name") is { Length: > 0 } title ? title : "(no title)",
            Body = Str(t, "notes") is { Length: > 0 } body ? body : null, Type = type, StatusName = statusName, Category = category,
            Priority = priority, StoryPoints = points, DueDate = DateOf(t, "due_on") ?? DateOf(t, "due_at"),
            CreatedAt = DateTimeOf(t, "created_at") ?? DateTime.UtcNow, UpdatedAt = DateTimeOf(t, "modified_at"),
            ClosedAt = completed ? DateTimeOf(t, "completed_at") : null,
            Author = author == null ? null : new SourceUser(null, author, null),
            Assignee = assigneeName == null && assigneeEmail == null ? null : new SourceUser(null, assigneeName, assigneeEmail),
            Labels = labels, ParentExternalId = parentGid, Links = links, Comments = comments, Attachments = attachments, Notes = notes,
        };
    }

    // --- mapping -------------------------------------------------------------------------------------------------------

    [GeneratedRegex(@"story\s*points?|^points?$|estimate|effort", RegexOptions.IgnoreCase)]
    private static partial Regex PointsName();

    /// <summary>A tag like <c>priority: high</c> or <c>High priority</c>.</summary>
    private static IssuePriority? PriorityFromTag(string tag)
    {
        var m = PriorityTag().Match(tag.Trim());
        return m.Success ? PriorityFromWord(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : null;
    }

    [GeneratedRegex(@"^(?:priority\s*[:/-]\s*(\w+)|(\w+)\s+priority)$", RegexOptions.IgnoreCase)]
    private static partial Regex PriorityTag();

    public void Dispose() { }
}
