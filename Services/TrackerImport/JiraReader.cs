using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ByteBench.Data;

namespace ByteBench.Services.TrackerImport;

/// <summary>
/// Reads one Jira project, from Jira Cloud (REST v3: Atlassian Document Format, token-paged enhanced search) or Jira Server / Data
/// Center (REST v2: wiki markup, startAt paging). The flavour is detected once from <c>serverInfo</c>. Basic auth when there is a
/// user name (Cloud: email + API token), else a bearer token (personal access token). Credentials only ever go into the
/// <c>Authorization</c> header, and only on requests to the Jira address itself (attachment downloads included). Sprints become real
/// sprints: listed through the agile API (<c>/rest/agile/1.0/board/{id}/sprint</c>, which carries goals), else collected from the issues'
/// Sprint field (Cloud objects, Server's old <c>...Sprint@1[id=3,state=ACTIVE,name=...]</c> strings); an issue in several sprints names
/// the latest. Only when a sprint can't be read does the issue get a <c>sprint: …</c> label instead.
/// </summary>
public sealed class JiraReader(SourceConnection connection, TrackerHttp http) : ISourceReader
{
    private const string What = "Jira";
    private const int PageSize = 100;
    private static readonly string[] BaseFields =
    [
        "summary", "description", "issuetype", "status", "priority", "labels", "components", "fixVersions", "duedate", "created", "updated",
        "resolutiondate", "reporter", "assignee", "parent", "issuelinks", "subtasks", "attachment",
    ];

    private bool? cloud;
    private string? epicLinkField, pointsField, sprintField;
    private bool fieldsLoaded;
    private HashSet<string>? knownSprints;

    internal SourceConnection Connection { get; } = connection;
    internal TrackerHttp Http { get; } = http;

    private string Root => (Connection.BaseUrl ?? throw new SourceException("Enter your Jira address (for example https://example.atlassian.net).")).Trim().TrimEnd('/');
    private string Key => Connection.Project.Trim();
    private string Api => Root + "/rest/api/" + (cloud == true ? "3" : "2");
    private static string Esc(string s) => Uri.EscapeDataString(s);
    private string Jql(bool includeClosed) =>
        $"project = \"{Key.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"" + (includeClosed ? "" : " AND statusCategory != Done") + " ORDER BY created ASC";

    private void Auth(HttpRequestMessage request)
    {
        var secret = Connection.Secret;
        if (string.IsNullOrEmpty(secret)) return;
        request.Headers.Authorization = string.IsNullOrEmpty(Connection.Username)
            ? new AuthenticationHeaderValue("Bearer", secret)
            : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(Connection.Username + ":" + secret)));
    }

    private Task<TrackerResponse> GetAsync(string url, CancellationToken ct) => Http.GetJsonAsync(url, Auth, ct, What);

    private async Task DetectAsync(CancellationToken ct)
    {
        if (cloud != null) return;
        var info = await GetAsync(Root + "/rest/api/2/serverInfo", ct);
        cloud = string.Equals(Str(info.Body, "deploymentType"), "Cloud", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<SourceProbe> ProbeAsync(CancellationToken ct)
    {
        await DetectAsync(ct);
        var project = await GetAsync($"{Api}/project/{Esc(Key)}", ct);
        if (project.Body.ValueKind != JsonValueKind.Object) throw new SourceException($"Jira has no project with the key {Key}.");
        var name = Str(project.Body, "name") ?? Key;
        var description = project.Body.TryGetProperty("description", out var d) ? JiraMarkup.FromField(d) : null;
        var jql = Jql(true);
        var titles = new List<string>();
        int count;
        if (cloud == true)
        {
            var sample = await GetAsync($"{Api}/search/jql?jql={Esc(jql)}&fields=summary&maxResults=3", ct);
            titles.AddRange(Items(sample.Body, "issues").Select(i => Str(Obj(i, "fields"), "summary") ?? "").Where(t => t.Length > 0).Take(3));
            var counted = await Http.SendJsonAsync(HttpMethod.Post, $"{Api}/search/approximate-count", new { jql = $"project = \"{Key}\"" }, Auth, ct, What);
            count = Int(counted.Body, "count") ?? titles.Count;
        }
        else
        {
            var sample = await GetAsync($"{Api}/search?jql={Esc(jql)}&fields=summary&startAt=0&maxResults=3", ct);
            titles.AddRange(Items(sample.Body, "issues").Select(i => Str(Obj(i, "fields"), "summary") ?? "").Where(t => t.Length > 0).Take(3));
            count = Int(sample.Body, "total") ?? titles.Count;
        }
        return new SourceProbe(name, string.IsNullOrWhiteSpace(description) ? null : description, count, titles);
    }

    public async Task<IReadOnlyList<SourceStatus>> ReadStatusesAsync(CancellationToken ct)
    {
        await DetectAsync(ct);
        var response = await GetAsync($"{Api}/project/{Esc(Key)}/statuses", ct);
        var seen = new Dictionary<string, SourceStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in Items(response.Body))
            foreach (var s in Items(type, "statuses"))
                if (Str(s, "name") is { Length: > 0 } name && !seen.ContainsKey(name))
                    seen[name] = new SourceStatus(name, CategoryOf(s));
        return [.. seen.Values];
    }

    // Jira labels are free text on issues; there is no project-level list. The writer creates them from the issues.
    public Task<IReadOnlyList<SourceLabel>> ReadLabelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SourceLabel>>([]);

    public async Task<IReadOnlyList<SourceMilestone>> ReadMilestonesAsync(CancellationToken ct)
    {
        await DetectAsync(ct);
        var response = await GetAsync($"{Api}/project/{Esc(Key)}/versions", ct);
        var list = new List<SourceMilestone>();
        var items = response.Body.ValueKind == JsonValueKind.Object ? Items(response.Body, "values") : Items(response.Body);
        foreach (var v in items)
        {
            var id = IdOf(v, "id");
            var title = Str(v, "name");
            if (id == null || string.IsNullOrWhiteSpace(title)) continue;
            list.Add(new SourceMilestone(id, title, NullIfBlank(Str(v, "description")), DateOnly(Str(v, "startDate")), DateOnly(Str(v, "releaseDate")),
                v.TryGetProperty("released", out var r) && r.ValueKind == JsonValueKind.True));
        }
        return list;
    }

    public async IAsyncEnumerable<SourceIssue> ReadIssuesAsync(SourceReadOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        await DetectAsync(ct);
        await LoadFieldsAsync(ct);
        var fields = new List<string>(BaseFields);
        if (options.IncludeComments) fields.Add("comment");
        foreach (var f in new[] { epicLinkField, pointsField, sprintField }) if (f != null) fields.Add(f);
        var fieldList = Esc(string.Join(",", fields));
        var jql = Esc(Jql(options.IncludeClosed));
        var max = Math.Max(1, options.MaxIssues);
        var yielded = 0;
        string? token = null;
        var startAt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            TrackerResponse page;
            if (cloud == true)
                page = await GetAsync($"{Api}/search/jql?jql={jql}&fields={fieldList}&maxResults={PageSize}" + (token != null ? "&nextPageToken=" + Esc(token) : ""), ct);
            else
                page = await GetAsync($"{Api}/search?jql={jql}&fields={fieldList}&startAt={startAt}&maxResults={PageSize}", ct);
            var count = 0;
            foreach (var raw in Items(page.Body, "issues"))
            {
                count++;
                if (yielded >= max) yield break;
                var issue = await MapAsync(raw, options, ct);
                if (issue == null) continue;
                yielded++;
                yield return issue;
            }
            if (cloud == true)
            {
                token = Str(page.Body, "nextPageToken");
                if (string.IsNullOrEmpty(token) || count == 0 || page.Body.TryGetProperty("isLast", out var last) && last.ValueKind == JsonValueKind.True) yield break;
            }
            else
            {
                startAt += count;
                var total = Int(page.Body, "total");
                if (count == 0 || (total != null && startAt >= total)) yield break;
            }
        }
    }

    public Task<Stream> OpenAttachmentAsync(SourceAttachment attachment, CancellationToken ct, long maxBytes = TrackerHttp.DefaultAttachmentMaxBytes) =>
        attachment.DownloadUrl is { Length: > 0 } url ? Http.GetStreamAsync(url, Auth, Root, maxBytes, ct, What) : throw new SourceException("Jira gave no download address for this file.");

    /// <summary>The project's sprints: from the boards of the project (agile API, with goals), else from the Sprint field of its issues.</summary>
    public async Task<IReadOnlyList<SourceSprint>> ReadSprintsAsync(CancellationToken ct)
    {
        await DetectAsync(ct);
        await LoadFieldsAsync(ct);
        var found = new Dictionary<string, SourceSprint>(StringComparer.Ordinal);
        try
        {
            var boards = new List<string>();
            for (var start = 0; start < 500;)
            {
                var page = await GetAsync($"{Root}/rest/agile/1.0/board?projectKeyOrId={Esc(Key)}&startAt={start}&maxResults=50", ct);
                var values = Items(page.Body, "values").ToList();
                boards.AddRange(values.Select(b => IdOf(b, "id")).Where(id => id != null).Select(id => id!));
                start += values.Count;
                if (values.Count == 0 || (page.Body.TryGetProperty("isLast", out var last) && last.ValueKind == JsonValueKind.True)) break;
            }
            foreach (var board in boards.Take(20))
            {
                try
                {
                    for (var start = 0; start < 5000;)
                    {
                        var page = await GetAsync($"{Root}/rest/agile/1.0/board/{Esc(board)}/sprint?startAt={start}&maxResults=50", ct);
                        var values = Items(page.Body, "values").ToList();
                        foreach (var v in values) if (SprintOf(v) is { } sp) found.TryAdd(sp.ExternalId, sp);
                        start += values.Count;
                        if (values.Count == 0 || (page.Body.TryGetProperty("isLast", out var last) && last.ValueKind == JsonValueKind.True)) break;
                    }
                }
                catch (SourceException) { /* a kanban board has no sprints (400), or this board isn't visible: try the others */ }
            }
        }
        catch (SourceException) { /* no Jira Software / agile API for this token: fall back to the issues' Sprint field */ }

        if (found.Count == 0 && sprintField != null)
        {
            // The field on each issue carries id, name, state and dates (not always the goal).
            string? token = null;
            var startAt = 0;
            for (var pages = 0; pages < 1000; pages++)
            {
                ct.ThrowIfCancellationRequested();
                var jql = Esc(Jql(true));
                var url = cloud == true
                    ? $"{Api}/search/jql?jql={jql}&fields={Esc(sprintField)}&maxResults={PageSize}" + (token != null ? "&nextPageToken=" + Esc(token) : "")
                    : $"{Api}/search?jql={jql}&fields={Esc(sprintField)}&startAt={startAt}&maxResults={PageSize}";
                var page = await GetAsync(url, ct);
                var issues = Items(page.Body, "issues").ToList();
                foreach (var issue in issues)
                    if (Obj(issue, "fields").TryGetProperty(sprintField, out var value))
                        foreach (var sp in ParseSprints(value)) found.TryAdd(sp.ExternalId, sp);
                if (cloud == true)
                {
                    token = Str(page.Body, "nextPageToken");
                    if (string.IsNullOrEmpty(token) || issues.Count == 0) break;
                }
                else
                {
                    startAt += issues.Count;
                    if (issues.Count == 0 || (Int(page.Body, "total") is { } total && startAt >= total)) break;
                }
            }
        }
        knownSprints = [.. found.Keys];
        return [.. found.Values.OrderBy(s => s.StartDate ?? DateTime.MaxValue).ThenBy(s => long.TryParse(s.ExternalId, out var n) ? n : long.MaxValue)];
    }

    /// <summary>One sprint from the agile API or a Cloud Sprint field (<c>id, state future|active|closed, name, startDate, endDate, completeDate, goal</c>).</summary>
    private static SourceSprint? SprintOf(JsonElement v)
    {
        var id = IdOf(v, "id");
        var name = NullIfBlank(Str(v, "name"));
        if (id == null || name == null) return null;
        var state = StateOf(Str(v, "state"));
        var end = state == SprintState.Completed && Timestamp(Str(v, "completeDate")) is { } done ? done : Timestamp(Str(v, "endDate"));
        return new SourceSprint(id, name, NullIfBlank(Str(v, "goal")), Timestamp(Str(v, "startDate")), end, state);
    }

    private static SprintState StateOf(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "active" => SprintState.Active,
        "closed" or "completed" => SprintState.Completed,
        _ => SprintState.Planned,
    };

    /// <summary>Sprints out of a Sprint field value: Cloud objects, or Server's strings like <c>...Sprint@1[id=3,rapidViewId=1,state=ACTIVE,name=Sprint 3,goal=..,startDate=..,endDate=..,completeDate=..]</c>.</summary>
    internal static List<SourceSprint> ParseSprints(JsonElement value)
    {
        var list = new List<SourceSprint>();
        if (value.ValueKind != JsonValueKind.Array) return list;
        foreach (var s in value.EnumerateArray())
        {
            if (s.ValueKind == JsonValueKind.Object) { if (SprintOf(s) is { } o) list.Add(o); }
            else if (s.ValueKind == JsonValueKind.String && s.GetString() is { } text && text.IndexOf('[') is var open and >= 0)
            {
                var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match m in Regex.Matches(text[(open + 1)..], @"(?<k>\w+)=(?<v>[^,\]]*)")) pairs[m.Groups["k"].Value] = m.Groups["v"].Value.Trim();
                if (!pairs.TryGetValue("id", out var id) || !pairs.TryGetValue("name", out var name) || id.Length == 0 || name.Length == 0) continue;
                var state = StateOf(pairs.GetValueOrDefault("state"));
                pairs.TryGetValue("goal", out var goal);
                var end = state == SprintState.Completed && Timestamp(pairs.GetValueOrDefault("completeDate")) is { } done ? done : Timestamp(pairs.GetValueOrDefault("endDate"));
                list.Add(new SourceSprint(id, name, string.IsNullOrWhiteSpace(goal) || goal == "<null>" ? null : goal, Timestamp(pairs.GetValueOrDefault("startDate")), end, state));
            }
        }
        return list;
    }

    /// <summary>The active sprint if any, else the one that started last (ties: the higher id, i.e. the one created last).</summary>
    private static SourceSprint? Latest(List<SourceSprint> sprints) =>
        sprints.LastOrDefault(s => s.State == SprintState.Active)
        ?? sprints.OrderBy(s => s.StartDate ?? DateTime.MinValue).ThenBy(s => long.TryParse(s.ExternalId, out var n) ? n : 0).LastOrDefault();

    private async Task LoadFieldsAsync(CancellationToken ct)
    {
        if (fieldsLoaded) return;
        fieldsLoaded = true;
        try
        {
            var all = await GetAsync($"{Api}/field", ct);
            string? points = null, pointsFallback = null;
            foreach (var f in Items(all.Body))
            {
                var id = Str(f, "id");
                var name = Str(f, "name");
                if (id == null || name == null) continue;
                var custom = Str(Obj(f, "schema"), "custom") ?? "";
                if (name.Equals("Epic Link", StringComparison.OrdinalIgnoreCase) || custom.EndsWith("gh-epic-link", StringComparison.Ordinal)) epicLinkField ??= id;
                else if (name.Equals("Sprint", StringComparison.OrdinalIgnoreCase) || custom.EndsWith("gh-sprint", StringComparison.Ordinal)) sprintField ??= id;
                else if (name.Equals("Story Points", StringComparison.OrdinalIgnoreCase)) points ??= id;
                else if (name.Equals("Story point estimate", StringComparison.OrdinalIgnoreCase)) pointsFallback ??= id;
            }
            pointsField = points ?? pointsFallback;
        }
        catch (SourceException ex) when (!ex.AuthFailure) { /* no custom fields: no epic link, points or sprints */ }
    }

    private async Task<SourceIssue?> MapAsync(JsonElement raw, SourceReadOptions options, CancellationToken ct)
    {
        var key = Str(raw, "key");
        if (string.IsNullOrEmpty(key)) return null;
        var f = Obj(raw, "fields");
        var notes = new List<string>();
        var labels = new List<string>();
        foreach (var l in Items(f, "labels")) if (l.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(l.GetString())) labels.Add(l.GetString()!.Trim());
        foreach (var c in Items(f, "components")) if (Str(c, "name") is { Length: > 0 } cn) labels.Add("component: " + cn);
        string? sprintId = null;
        if (sprintField != null && f.TryGetProperty(sprintField, out var sprints) && sprints.ValueKind == JsonValueKind.Array && sprints.GetArrayLength() > 0)
        {
            var latest = Latest(ParseSprints(sprints));
            if (latest != null && (knownSprints == null || knownSprints.Contains(latest.ExternalId))) sprintId = latest.ExternalId;
            else if (latest != null) { labels.Add("sprint: " + latest.Name); notes.Add($"its sprint \"{latest.Name}\" isn't one this token can list, so it was labelled 'sprint: {latest.Name}' instead."); }
            else if (SprintName(sprints) is { } fallback) { labels.Add("sprint: " + fallback); notes.Add($"its sprint \"{fallback}\" couldn't be read, so it was labelled 'sprint: {fallback}' instead."); }
        }

        var prefix = Key + "-";
        var outside = new List<string>();
        var links = new List<SourceLink>();
        foreach (var link in Items(f, "issuelinks"))
        {
            var type = Str(Obj(link, "type"), "name") ?? "";
            var inward = Obj(link, "inwardIssue");
            var outward = Obj(link, "outwardIssue");
            if (outward.ValueKind != JsonValueKind.Object && inward.ValueKind == JsonValueKind.Object)
            {
                // The other issue reports this one; if it's in another project nobody would, so say so.
                if (Str(inward, "key") is { } ik && !ik.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) outside.Add(ik);
                continue;
            }
            if (Str(outward, "key") is not { Length: > 0 } target) continue;
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { outside.Add(target); continue; }
            links.Add(new SourceLink(target, type.ToLowerInvariant() switch
            {
                "blocks" or "blocker" => IssueLinkType.Blocks,
                "duplicate" or "duplicates" => IssueLinkType.Duplicates,
                "cloners" or "clones" => IssueLinkType.Clones,
                _ => IssueLinkType.RelatesTo,
            }));
        }
        if (outside.Count > 0) notes.Add($"{outside.Count} link(s) to issues in other Jira projects weren't imported ({string.Join(", ", outside.Distinct().Take(5))}{(outside.Distinct().Count() > 5 ? ", …" : "")}).");

        string? parent = Str(Obj(f, "parent"), "key");
        if (parent == null && epicLinkField != null && f.TryGetProperty(epicLinkField, out var epic) && epic.ValueKind == JsonValueKind.String) parent = epic.GetString();
        if (parent != null && !parent.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { notes.Add($"Its parent {parent} is in another Jira project, so it was imported without a parent."); parent = null; }

        var attachments = Items(f, "attachment")
            .Select(a => new SourceAttachment(Str(a, "filename") ?? "file", Str(a, "content"), Str(a, "content"), IdOf(a, "id"), null,
                a.ValueKind == JsonValueKind.Object && a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number && sz.TryGetInt64(out var szv) ? szv : null))
            .ToList();

        var comments = options.IncludeComments ? await ReadCommentsAsync(key, f, ct) : [];

        var issueType = Obj(f, "issuetype");
        var status = Obj(f, "status");
        return new SourceIssue
        {
            ExternalId = key,
            DisplayKey = key,
            Url = $"{Root}/browse/{key}",
            Title = NullIfBlank(Str(f, "summary")) ?? key,
            Body = f.TryGetProperty("description", out var d) ? NullIfBlank(JiraMarkup.FromField(d)) : null,
            Type = TypeOf(issueType),
            StatusName = Str(status, "name"),
            Category = CategoryOf(status),
            Priority = PriorityOf(Str(Obj(f, "priority"), "name")),
            StoryPoints = PointsOf(f),
            DueDate = DateOnly(Str(f, "duedate")),
            CreatedAt = Timestamp(Str(f, "created")) ?? DateTime.UtcNow,
            UpdatedAt = Timestamp(Str(f, "updated")),
            ClosedAt = Timestamp(Str(f, "resolutiondate")),
            Author = UserOf(Obj(f, "reporter")),
            Assignee = UserOf(Obj(f, "assignee")),
            Labels = labels.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            SprintId = sprintId,
            MilestoneId = Items(f, "fixVersions").Select(v => IdOf(v, "id")).FirstOrDefault(id => id != null),
            ParentExternalId = parent,
            Links = links,
            Comments = comments,
            Attachments = attachments,
            Notes = notes,
        };
    }

    private async Task<IReadOnlyList<SourceComment>> ReadCommentsAsync(string key, JsonElement fields, CancellationToken ct)
    {
        var block = Obj(fields, "comment");
        var list = new List<SourceComment>();
        foreach (var c in Items(block, "comments")) if (MapComment(c) is { } m) list.Add(m);
        var total = Int(block, "total") ?? list.Count;
        var guard = 0;
        while (list.Count < total && guard++ < 1000)
        {
            ct.ThrowIfCancellationRequested();
            var page = await GetAsync($"{Api}/issue/{Esc(key)}/comment?startAt={list.Count}&maxResults={PageSize}", ct);
            var before = list.Count;
            var rows = Items(page.Body, "comments").ToList();
            foreach (var c in rows) if (MapComment(c) is { } m) list.Add(m);
            if (rows.Count == 0 || list.Count == before) break;
        }
        return list;
    }

    private static SourceComment? MapComment(JsonElement c)
    {
        var id = IdOf(c, "id");
        if (id == null) return null;
        var body = c.TryGetProperty("body", out var b) ? JiraMarkup.FromField(b) : "";
        if (string.IsNullOrWhiteSpace(body)) return null;
        return new SourceComment(id, UserOf(Obj(c, "author")), body, Timestamp(Str(c, "created")) ?? DateTime.UtcNow);
    }

    // ------------------------------------------------------------ mapping helpers

    private static IssueType TypeOf(JsonElement t)
    {
        if (t.ValueKind == JsonValueKind.Object && t.TryGetProperty("subtask", out var s) && s.ValueKind == JsonValueKind.True) return IssueType.Subtask;
        return (Str(t, "name") ?? "").Trim().ToLowerInvariant() switch
        {
            "epic" => IssueType.Epic,
            "story" => IssueType.Story,
            "bug" => IssueType.Bug,
            "task" => IssueType.Task,
            "sub-task" or "subtask" or "sub task" => IssueType.Subtask,
            "new feature" or "improvement" or "feature" => IssueType.Feature,
            _ => IssueType.Task,
        };
    }

    private static IssueStatus CategoryOf(JsonElement status) => Str(Obj(status, "statusCategory"), "key") switch
    {
        "indeterminate" => IssueStatus.InProgress,
        "done" => IssueStatus.Done,
        _ => IssueStatus.Todo,
    };

    private static IssuePriority? PriorityOf(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "highest" or "blocker" or "critical" => IssuePriority.Highest,
        "high" or "major" => IssuePriority.High,
        "medium" => IssuePriority.Medium,
        "low" or "minor" => IssuePriority.Low,
        "lowest" or "trivial" => IssuePriority.Lowest,
        _ => null,
    };

    private int? PointsOf(JsonElement f)
    {
        if (pointsField == null || !f.TryGetProperty(pointsField, out var p) || p.ValueKind != JsonValueKind.Number || !p.TryGetDouble(out var d) || double.IsNaN(d)) return null;
        return (int)Math.Clamp(Math.Round(d, MidpointRounding.AwayFromZero), 0, 100);
    }

    private static SourceUser? UserOf(JsonElement u)
    {
        if (u.ValueKind != JsonValueKind.Object) return null;
        var login = NullIfBlank(Str(u, "name"));      // Server/Data Center only; Cloud identifies people by account id, which isn't useful here
        var name = NullIfBlank(Str(u, "displayName"));
        var email = NullIfBlank(Str(u, "emailAddress"));
        return login == null && name == null && email == null ? null : new SourceUser(login, name, email);
    }

    /// <summary>The name of the latest sprint (the active one if any): Cloud gives objects, Server 8 and older strings like <c>...Sprint@1[id=3,state=ACTIVE,name=Sprint 3,...]</c>.</summary>
    private static string? SprintName(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return null;
        var found = new List<(string Name, bool Active)>();
        foreach (var s in value.EnumerateArray())
        {
            if (s.ValueKind == JsonValueKind.Object && Str(s, "name") is { Length: > 0 } n) found.Add((n, string.Equals(Str(s, "state"), "active", StringComparison.OrdinalIgnoreCase)));
            else if (s.ValueKind == JsonValueKind.String && s.GetString() is { } text && Regex.Match(text, @"name=([^,\]]+)") is { Success: true } m)
                found.Add((m.Groups[1].Value.Trim(), Regex.IsMatch(text, @"state=ACTIVE", RegexOptions.IgnoreCase)));
        }
        if (found.Count == 0) return null;
        return found.LastOrDefault(x => x.Active) is { Name: not null } a ? a.Name : found[^1].Name;
    }

    /// <summary>Jira timestamps look like <c>2024-05-01T12:00:00.000+0200</c> (no colon in the offset).</summary>
    internal static DateTime? Timestamp(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = Regex.Replace(s.Trim(), @"([+-]\d{2})(\d{2})$", "$1:$2");
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime : null;
    }

    private static DateTime? DateOnly(string? s) =>
        !string.IsNullOrWhiteSpace(s) && System.DateOnly.TryParse(s.Length >= 10 ? s[..10] : s, CultureInfo.InvariantCulture, out var d)
            ? DateTime.SpecifyKind(d.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc) : null;

    // ------------------------------------------------------------ safe JSON access

    private static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static string? IdOf(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => NullIfBlank(v.GetString()), JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;

    private static IEnumerable<JsonElement> Items(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];
    private static IEnumerable<JsonElement> Items(JsonElement e, string name) => Items(Obj(e, name));
    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public void Dispose() { }
}
