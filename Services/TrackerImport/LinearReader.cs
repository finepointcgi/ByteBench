using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ByteBench.Data;
using static ByteBench.Services.TrackerImport.TrackerJson;

namespace ByteBench.Services.TrackerImport;

/// <summary>
/// Reads one Linear team through the GraphQL API. A personal API key goes out as the bare <c>Authorization</c> header; an OAuth access
/// token (<c>lin_oauth_...</c>) as <c>Bearer</c>. Imported: issues (identifier as the key, markdown description, priority, estimate as
/// story points, due date, labels with their group, sub-issue parent, the Linear project as a milestone, relations blocks / duplicate /
/// related, comments and attachment links). Linear gives people's email to the key's owner, so assignees and authors are matched by it.
/// The cycle an issue belongs to becomes a <c>cycle: ...</c> label. Not imported: cycles as sprints, initiatives, roadmaps, SLAs,
/// reactions, customer requests and attachment files.
/// Issues are read 50 per request, oldest first (backward cursor paging on a newest-first list), with a smaller page if Linear
/// refuses a query as too complex. Errors, including <c>RATELIMITED</c>, are turned into readable messages.
/// </summary>
public sealed class LinearReader(SourceConnection connection, TrackerHttp http) : ISourceReader
{
    private const string What = "Linear";
    private const int PageSize = 50;
    private const string BadRequest = "Linear rejected the request (HTTP 400). If this keeps happening the team is too large for the query: run the import again, or turn off comments.";
    private readonly string endpoint = NormalizeEndpoint(connection.BaseUrl);
    private string? teamId;
    private string? teamKey;
    private JsonElement team;
    private HashSet<string>? projectIds;

    private static string NormalizeEndpoint(string? baseUrl)
    {
        var b = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.linear.app" : baseUrl.Trim().TrimEnd('/');
        return b.EndsWith("/graphql", StringComparison.OrdinalIgnoreCase) ? b : b + "/graphql";
    }

    private void Auth(HttpRequestMessage r)
    {
        var token = connection.Secret!.Trim();
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = token[7..].Trim();
        r.Headers.TryAddWithoutValidation("Authorization", token.StartsWith("lin_oauth_", StringComparison.Ordinal) ? "Bearer " + token : token);
    }

    private async Task<JsonElement> QueryAsync(string query, object variables, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connection.Secret))
            throw new SourceException("Linear needs a personal API key (Settings, Account, Security & access).", authFailure: true);
        TrackerResponse r;
        try { r = await http.SendJsonAsync(HttpMethod.Post, endpoint, new { query, variables }, Auth, ct, What); }
        catch (SourceException ex) when (!ex.AuthFailure && ex.Message.EndsWith("answered HTTP 400.", StringComparison.Ordinal)) { throw new SourceException(BadRequest); }
        if (r.Body.ValueKind != JsonValueKind.Object) throw new SourceException("Linear's answer wasn't a GraphQL response (is the address the right site?).");
        var first = Items(r.Body, "errors").FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Object)
        {
            var code = (Str(first, "extensions.code") ?? Str(first, "extensions.type") ?? "").ToUpperInvariant();
            var message = Str(first, "extensions.userPresentableMessage") ?? Str(first, "message") ?? "unknown error";
            if (code.Contains("RATELIMIT", StringComparison.Ordinal))
                throw new SourceException("Linear is rate limiting the import; try again in a few minutes.");
            if (code.Contains("AUTHENTICATION", StringComparison.Ordinal) || code is "UNAUTHENTICATED")
                throw new SourceException("Linear rejected the credentials (wrong, expired or missing API key).", authFailure: true);
            if (code is "FORBIDDEN" or "FORBIDDEN_ERROR")
                throw new SourceException("Linear says the API key isn't allowed to read this (check the key's access to the team).", authFailure: true);
            if (message.Contains("not found", StringComparison.OrdinalIgnoreCase))
                throw new SourceException("Linear says this doesn't exist, or the key can't see it (check the team key and the API key).");
            throw new SourceException("Linear said: " + Clip(message, 200));
        }
        var data = Prop(r.Body, "data");
        if (data.ValueKind != JsonValueKind.Object) throw new SourceException("Linear's answer had no data.");
        return data;
    }

    // --- team ----------------------------------------------------------------------------------------------------------

    private async Task EnsureTeamAsync(CancellationToken ct)
    {
        if (teamId != null) return;
        JsonElement t;
        if (Guid.TryParse(connection.Project.Trim(), out _))
            t = Prop(await QueryAsync("query($id:String!){ team(id:$id){ id key name description } }", new { id = connection.Project.Trim() }, ct), "team");
        else
            t = Items(await QueryAsync("query($key:String!){ teams(first:1, filter:{ key:{ eq:$key } }){ nodes{ id key name description } } }",
                new { key = connection.Project.Trim() }, ct), "teams.nodes").FirstOrDefault();
        if (t.ValueKind != JsonValueKind.Object || Str(t, "id") is not { } id)
            throw new SourceException("Linear says this doesn't exist, or the key can't see it (check the team key and the API key).");
        (team, teamId, teamKey) = (t, id, Str(t, "key"));
    }

    public async Task<SourceProbe> ProbeAsync(CancellationToken ct)
    {
        await EnsureTeamAsync(ct);
        var count = 0;
        try
        {
            var c = await QueryAsync("query($id:String!){ team(id:$id){ issueCount } }", new { id = teamId }, ct);
            if (Num(c, "team.issueCount") is { } n) count = (int)n;
        }
        catch (SourceException ex) when (!ex.AuthFailure) { /* the count is a nicety */ }
        var samples = new List<string>();
        var s = await QueryAsync("query($id:String!){ team(id:$id){ issues(last:3, orderBy:createdAt){ nodes{ title } } } }", new { id = teamId }, ct);
        foreach (var i in Items(s, "team.issues.nodes").Reverse()) if (Str(i, "title") is { } title) samples.Add(title);
        return new SourceProbe((Str(team, "name") ?? connection.Project) + (teamKey != null ? $" ({teamKey})" : ""), Str(team, "description"), count, samples);
    }

    public async Task<IReadOnlyList<SourceStatus>> ReadStatusesAsync(CancellationToken ct)
    {
        await EnsureTeamAsync(ct);
        var d = await QueryAsync("query($id:String!){ team(id:$id){ states(first:100){ nodes{ name type position } } } }", new { id = teamId }, ct);
        return Items(d, "team.states.nodes").OrderBy(s => Num(s, "position") ?? 0)
            .Where(s => Str(s, "name") != null).Select(s => new SourceStatus(Str(s, "name")!, CategoryOf(Str(s, "type")))).ToList();
    }

    private static IssueStatus CategoryOf(string? type) => type switch
    {
        "started" => IssueStatus.InProgress,
        "completed" or "canceled" => IssueStatus.Done,
        _ => IssueStatus.Todo, // backlog, unstarted, triage
    };

    public async Task<IReadOnlyList<SourceLabel>> ReadLabelsAsync(CancellationToken ct)
    {
        await EnsureTeamAsync(ct);
        var list = new List<SourceLabel>();
        string? after = null;
        for (var guard = 0; guard < 1000; guard++)
        {
            var d = await QueryAsync("query($id:String!,$after:String){ team(id:$id){ labels(first:100, after:$after){ nodes{ name color parent{ name } } pageInfo{ hasNextPage endCursor } } } }",
                new { id = teamId, after }, ct);
            foreach (var l in Items(d, "team.labels.nodes")) if (LabelName(l) is { } name) list.Add(new SourceLabel(name, Str(l, "color")));
            if (!Bool(d, "team.labels.pageInfo.hasNextPage") || Str(d, "team.labels.pageInfo.endCursor") is not { } next) break;
            after = next;
        }
        return list.DistinctBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Linear projects of the team are the milestones.</summary>
    public async Task<IReadOnlyList<SourceMilestone>> ReadMilestonesAsync(CancellationToken ct)
    {
        await EnsureTeamAsync(ct);
        var list = new List<SourceMilestone>();
        string? after = null;
        for (var guard = 0; guard < 1000; guard++)
        {
            var d = await QueryAsync("query($id:String!,$after:String){ team(id:$id){ projects(first:50, after:$after){ nodes{ id name description startDate targetDate state } pageInfo{ hasNextPage endCursor } } } }",
                new { id = teamId, after }, ct);
            foreach (var p in Items(d, "team.projects.nodes"))
                if (Str(p, "id") is { } id && Str(p, "name") is { } name)
                    list.Add(new SourceMilestone(id, name, Str(p, "description"), DateOf(p, "startDate"), DateOf(p, "targetDate"), Str(p, "state") is "completed" or "canceled"));
            if (!Bool(d, "team.projects.pageInfo.hasNextPage") || Str(d, "team.projects.pageInfo.endCursor") is not { } next) break;
            after = next;
        }
        projectIds = list.Select(m => m.ExternalId).ToHashSet();
        return list;
    }

    // --- issues --------------------------------------------------------------------------------------------------------

    private const string UserFields = "name displayName email";

    private static string IssueQuery(bool comments) => $$"""
        query($id:String!,$last:Int!,$before:String,$archived:Boolean,$filter:IssueFilter){
          team(id:$id){ issues(last:$last, before:$before, includeArchived:$archived, filter:$filter, orderBy:createdAt){
            nodes{
              id identifier url title description priority estimate dueDate createdAt updatedAt completedAt canceledAt archivedAt
              creator{ {{UserFields}} } assignee{ {{UserFields}} }
              state{ name type }
              labels(first:15){ nodes{ name parent{ name } } }
              parent{ id parent{ id } labels(first:10){ nodes{ name } } }
              project{ id name }
              cycle{ number name }
              attachments(first:20){ nodes{ title url } }
              relations(first:20){ nodes{ type relatedIssue{ id } } }
              {{(comments ? "comments(first:10){ nodes{ id body createdAt user{ " + UserFields + " } } pageInfo{ hasNextPage endCursor } }" : "")}}
            }
            pageInfo{ hasPreviousPage startCursor }
          } }
        }
        """;

    public async IAsyncEnumerable<SourceIssue> ReadIssuesAsync(SourceReadOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        await EnsureTeamAsync(ct);
        if (projectIds == null) await ReadMilestonesAsync(ct);
        var query = IssueQuery(options.IncludeComments);
        object? filter = options.IncludeClosed ? null : new { state = new { type = new { nin = new[] { "completed", "canceled" } } } };
        var size = PageSize;
        string? before = null;
        var count = 0;
        for (var guard = 0; guard < 100000; guard++)
        {
            JsonElement data;
            try { data = await QueryAsync(query, new { id = teamId, last = size, before, archived = options.IncludeClosed, filter }, ct); }
            catch (SourceException ex) when (ex.Message == BadRequest && size > 5) { size = Math.Max(5, size / 2); continue; } // too complex: smaller pages
            var nodes = Items(data, "team.issues.nodes").ToList();
            // The list is newest first and we page backwards from its end, so each page is read in reverse to yield oldest first.
            for (var n = nodes.Count - 1; n >= 0; n--)
            {
                var issue = await MapAsync(nodes[n], options, ct);
                if (issue == null) continue;
                yield return issue;
                if (++count >= options.MaxIssues) yield break;
            }
            if (!Bool(data, "team.issues.pageInfo.hasPreviousPage") || Str(data, "team.issues.pageInfo.startCursor") is not { } cursor || nodes.Count == 0) yield break;
            before = cursor;
        }
    }

    private async Task<SourceIssue?> MapAsync(JsonElement i, SourceReadOptions options, CancellationToken ct)
    {
        if (Str(i, "id") is not { } id) return null;
        var notes = new List<string>();
        var labels = Items(i, "labels.nodes").Select(LabelName).OfType<string>().ToList();
        if (Str(i, "cycle.name") is { Length: > 0 } cycleName) labels.Add("cycle: " + cycleName);
        else if (Num(i, "cycle.number") is { } cycleNo) labels.Add("cycle: " + ((long)cycleNo).ToString(CultureInfo.InvariantCulture));

        var type = TypeFromLabels(labels.Where(l => !l.StartsWith("cycle: ", StringComparison.Ordinal)));
        string? parent = Str(i, "parent.id");
        if (parent != null)
        {
            var parentIsEpic = Items(i, "parent.labels.nodes").Any(l => string.Equals(Str(l, "name"), "epic", StringComparison.OrdinalIgnoreCase));
            if (type == IssueType.Epic) parent = null;
            else if (!parentIsEpic)
            {
                // ByteBench allows a parent only for sub-tasks (any parent but an epic) and children of an epic; sub-sub-issues can't nest.
                if (Str(i, "parent.parent.id") != null) { notes.Add("is a sub-issue of a sub-issue; it is imported without a parent because sub-tasks can't nest."); parent = null; }
                else type = IssueType.Subtask;
            }
        }

        string? milestone = null;
        if (Str(i, "project.id") is { } pid)
        {
            if (projectIds!.Contains(pid)) milestone = pid;
            else notes.Add($"belongs to the Linear project \"{Str(i, "project.name")}\", which isn't a project of this team, so no milestone was set.");
        }

        var state = Str(i, "state.type");
        var canceled = state == "canceled";
        if (canceled) notes.Add("was canceled in Linear; it is imported as done.");
        var done = state is "completed" or "canceled";
        int? points = Num(i, "estimate") is { } est && est >= 0 ? (int)Math.Round(est) : null;

        var links = new List<SourceLink>();
        foreach (var rel in Items(i, "relations.nodes"))
        {
            if (Str(rel, "relatedIssue.id") is not { } target) continue;
            var t = Str(rel, "type") switch { "blocks" => IssueLinkType.Blocks, "duplicate" => IssueLinkType.Duplicates, "related" or "similar" => IssueLinkType.RelatesTo, _ => (IssueLinkType?)null };
            if (t != null && !links.Any(l => l.TargetExternalId == target && l.Type == t)) links.Add(new SourceLink(target, t.Value));
        }

        var comments = new List<SourceComment>();
        if (options.IncludeComments)
        {
            foreach (var c in Items(i, "comments.nodes")) AddComment(comments, c);
            if (Bool(i, "comments.pageInfo.hasNextPage") && Str(i, "comments.pageInfo.endCursor") is { } after)
                for (var guard = 0; guard < 1000; guard++)
                {
                    var d = await QueryAsync("query($id:String!,$after:String){ issue(id:$id){ comments(first:50, after:$after){ nodes{ id body createdAt user{ " + UserFields + " } } pageInfo{ hasNextPage endCursor } } } }",
                        new { id, after }, ct);
                    foreach (var c in Items(d, "issue.comments.nodes")) AddComment(comments, c);
                    if (!Bool(d, "issue.comments.pageInfo.hasNextPage") || Str(d, "issue.comments.pageInfo.endCursor") is not { } next) break;
                    after = next;
                }
            comments.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
        }

        return new SourceIssue
        {
            ExternalId = id, DisplayKey = Str(i, "identifier") ?? id, Url = Str(i, "url"), Title = Str(i, "title") ?? "(no title)",
            Body = Str(i, "description"), Type = type, StatusName = Str(i, "state.name"), Category = CategoryOf(state),
            Priority = Num(i, "priority") is { } p ? PriorityOf((int)p) : null, StoryPoints = points, DueDate = DateOf(i, "dueDate"),
            CreatedAt = DateTimeOf(i, "createdAt") ?? DateTime.UtcNow, UpdatedAt = DateTimeOf(i, "updatedAt"),
            ClosedAt = done ? DateTimeOf(i, "completedAt") ?? DateTimeOf(i, "canceledAt") : null,
            Author = ToUser(Prop(i, "creator")), Assignee = ToUser(Prop(i, "assignee")), Labels = labels,
            MilestoneId = milestone, ParentExternalId = parent, Links = links, Comments = comments, Notes = notes,
            Attachments = Items(i, "attachments.nodes").Where(a => Str(a, "url") != null || Str(a, "title") != null)
                .Select(a => new SourceAttachment(Str(a, "title") ?? Str(a, "url")!, Str(a, "url"))).ToList(),
        };
    }

    private static void AddComment(List<SourceComment> list, JsonElement c)
    {
        if (Str(c, "id") is { } cid && Str(c, "body") is { Length: > 0 } body)
            list.Add(new SourceComment(cid, ToUser(Prop(c, "user")), body, DateTimeOf(c, "createdAt") ?? DateTime.UtcNow));
    }

    // --- mapping -------------------------------------------------------------------------------------------------------

    /// <summary>1 urgent, 2 high, 3 medium, 4 low; 0 means no priority.</summary>
    private static IssuePriority? PriorityOf(int p) => p switch
    {
        1 => IssuePriority.Highest, 2 => IssuePriority.High, 3 => IssuePriority.Medium, 4 => IssuePriority.Low, _ => null,
    };

    /// <summary>A group label is named <c>Group/Label</c>.</summary>
    private static string? LabelName(JsonElement l)
    {
        var name = Str(l, "name")?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        return Str(l, "parent.name") is { Length: > 0 } group ? group + "/" + name : name;
    }

    private static SourceUser? ToUser(JsonElement u)
    {
        if (u.ValueKind != JsonValueKind.Object) return null;
        var (name, display, email) = (Str(u, "name"), Str(u, "displayName"), Str(u, "email"));
        return name == null && display == null && email == null ? null : new SourceUser(display, name ?? display, email);
    }

    public void Dispose() { }
}
