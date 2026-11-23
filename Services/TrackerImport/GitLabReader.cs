using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using ByteBench.Data;

namespace ByteBench.Services.TrackerImport;

/// <summary>
/// Reads one GitLab project through the REST API v4 (gitlab.com, self-managed, with or without a relative URL root). The token goes
/// out only as the <c>PRIVATE-TOKEN</c> header (personal, project and group access tokens all work there). Imported: issues (with
/// the epic they belong to when the payload names one), labels, project milestones, notes and issue links. Not imported: time
/// tracking, iterations, boards, merge requests and uploaded files (relative <c>/uploads/</c> links are made absolute so they still resolve).
/// </summary>
public sealed partial class GitLabReader(SourceConnection connection, TrackerHttp http) : ISourceReader
{
    private const string What = "GitLab";
    private readonly string site = NormalizeBase(connection.BaseUrl);
    private string Api => site + "/api/v4";
    private string ProjectPath => "/projects/" + EncodeProject(connection.Project);

    private JsonElement project;
    private bool projectLoaded;
    private HashSet<string>? projectMilestoneIds;

    private static string NormalizeBase(string? baseUrl)
    {
        var b = string.IsNullOrWhiteSpace(baseUrl) ? "https://gitlab.com" : baseUrl.Trim().TrimEnd('/');
        if (b.EndsWith("/api/v4", StringComparison.OrdinalIgnoreCase)) b = b[..^7].TrimEnd('/');
        return b;
    }

    private static string EncodeProject(string project)
    {
        project = project.Trim().Trim('/');
        return project.All(char.IsDigit) ? project : Uri.EscapeDataString(project);
    }

    private void Auth(HttpRequestMessage r)
    {
        if (!string.IsNullOrEmpty(connection.Secret)) r.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", connection.Secret);
    }

    private Task<TrackerResponse> GetAsync(string url, CancellationToken ct) => http.GetJsonAsync(url, Auth, ct, What);

    // --- project -------------------------------------------------------------------------------------------------------

    private async Task<JsonElement> EnsureProjectAsync(CancellationToken ct)
    {
        if (projectLoaded) return project;
        var r = await GetAsync(Api + ProjectPath, ct);
        if (r.Body.ValueKind != JsonValueKind.Object) throw new SourceException("GitLab's answer wasn't a project (is the address the right site?).");
        if (r.Body.TryGetProperty("issues_enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False)
            throw new SourceException("This GitLab project has issues turned off, so there is nothing to import.");
        project = r.Body;
        projectLoaded = true;
        return project;
    }

    public async Task<SourceProbe> ProbeAsync(CancellationToken ct)
    {
        var p = await EnsureProjectAsync(ct);
        var count = 0;
        try
        {
            var stats = await GetAsync(Api + ProjectPath + "/issues_statistics", ct);
            if (stats.Body.ValueKind == JsonValueKind.Object && stats.Body.TryGetProperty("statistics", out var s)
                && s.TryGetProperty("counts", out var counts) && counts.TryGetProperty("all", out var all) && all.TryGetInt32(out var n)) count = n;
        }
        catch (SourceException ex) when (!ex.AuthFailure) { /* the count is a nicety */ }
        var samples = new List<string>();
        var first = await GetAsync(Api + ProjectPath + "/issues?scope=all&state=all&order_by=created_at&sort=asc&per_page=3", ct);
        if (first.Body.ValueKind == JsonValueKind.Array)
            foreach (var i in first.Body.EnumerateArray().Take(3)) if (Str(i, "title") is { } t) samples.Add(t);
        return new SourceProbe(Str(p, "name_with_namespace") ?? Str(p, "name") ?? connection.Project, Str(p, "description"), count, samples);
    }

    public Task<IReadOnlyList<SourceStatus>> ReadStatusesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SourceStatus>>([new SourceStatus("Open", IssueStatus.Todo), new SourceStatus("Closed", IssueStatus.Done)]);

    public async Task<IReadOnlyList<SourceLabel>> ReadLabelsAsync(CancellationToken ct)
    {
        var list = new List<SourceLabel>();
        await foreach (var l in PagesAsync(Api + ProjectPath + "/labels?per_page=100", int.MaxValue, ct))
            if (Str(l, "name") is { } name) list.Add(new SourceLabel(name, Str(l, "color")));
        return list;
    }

    public async Task<IReadOnlyList<SourceMilestone>> ReadMilestonesAsync(CancellationToken ct)
    {
        var list = new List<SourceMilestone>();
        var ids = new HashSet<string>();
        await foreach (var m in PagesAsync(Api + ProjectPath + "/milestones?state=all&per_page=100", int.MaxValue, ct))
        {
            var id = Id(m, "id");
            if (id == null || Str(m, "title") is not { } title) continue;
            ids.Add(id);
            list.Add(new SourceMilestone(id, title, Str(m, "description"), Date(m, "start_date"), Date(m, "due_date"), Str(m, "state") == "closed"));
        }
        projectMilestoneIds = ids;
        return list;
    }

    // --- issues --------------------------------------------------------------------------------------------------------

    public async IAsyncEnumerable<SourceIssue> ReadIssuesAsync(SourceReadOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        var p = await EnsureProjectAsync(ct);
        var projectId = p.TryGetProperty("id", out var pid) && pid.TryGetInt64(out var pl) ? pl : (long?)null;
        var webUrl = Str(p, "web_url")?.TrimEnd('/');
        if (projectMilestoneIds == null) await ReadMilestonesAsync(ct);

        var url = Api + ProjectPath + "/issues?scope=all&state=" + (options.IncludeClosed ? "all" : "opened") + "&order_by=created_at&sort=asc&per_page=100";
        var epics = new HashSet<string>();
        var relatedPairs = new HashSet<string>(); // "relates_to" is reported by both issues; record it once
        var count = 0;
        await foreach (var i in PagesAsync(url, options.MaxIssues, ct))
        {
            var id = Id(i, "id");
            if (id == null || !i.TryGetProperty("iid", out var iidEl) || !iidEl.TryGetInt64(out var iid)) continue;
            var notes = new List<string>();

            string? parent = null;
            if (i.TryGetProperty("epic", out var epic) && epic.ValueKind == JsonValueKind.Object && Id(epic, "id") is { } epicId)
            {
                parent = "epic:" + epicId;
                if (epics.Add(epicId))
                    yield return new SourceIssue
                    {
                        ExternalId = parent, DisplayKey = "&" + (Id(epic, "iid") ?? epicId), Url = Absolute(Str(epic, "web_url") ?? Str(epic, "url")),
                        Title = Str(epic, "title") ?? "Epic " + epicId, Body = null, Type = IssueType.Epic, Category = IssueStatus.Todo,
                        CreatedAt = DateTime.UtcNow,
                    };
            }

            var labels = i.TryGetProperty("labels", out var le) && le.ValueKind == JsonValueKind.Array
                ? le.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : [];
            var type = TypeOf(i, labels);
            if (type == IssueType.Epic) parent = null;

            string? milestone = null;
            if (i.TryGetProperty("milestone", out var ms) && ms.ValueKind == JsonValueKind.Object && Id(ms, "id") is { } mid)
            {
                if (projectMilestoneIds!.Contains(mid)) milestone = mid;
                else notes.Add($"belongs to the group milestone \"{Str(ms, "title")}\", which isn't a milestone of this project, so no milestone was set.");
            }
            if (i.TryGetProperty("confidential", out var conf) && conf.ValueKind == JsonValueKind.True)
                notes.Add("was confidential in GitLab; it is visible to everyone who can see this project.");

            var closed = Str(i, "state") == "closed";
            var comments = new List<SourceComment>();
            var links = new List<SourceLink>();
            if (options.IncludeComments)
            {
                if (i.TryGetProperty("user_notes_count", out var nc) && nc.TryGetInt32(out var ncount) && ncount > 0)
                    await foreach (var n in PagesAsync($"{Api}{ProjectPath}/issues/{iid}/notes?sort=asc&order_by=created_at&per_page=100", int.MaxValue, ct))
                    {
                        if (n.TryGetProperty("system", out var sys) && sys.ValueKind == JsonValueKind.True) continue;
                        var body = Str(n, "body");
                        if (Id(n, "id") is not { } noteId || string.IsNullOrWhiteSpace(body)) continue;
                        comments.Add(new SourceComment(noteId, User(n, "author"), FixUploads(body, webUrl)!, DateTimeOf(n, "created_at") ?? DateTime.UtcNow));
                    }
                // Links: only "blocks" (this -> other) and "relates_to" are recorded here. "is_blocked_by" is skipped on purpose: the other
                // issue reports the same link as "blocks", and a SourceLink on this issue can only point away from it.
                var skippedOtherProject = 0;
                var seen = new HashSet<(string, IssueLinkType)>();
                await foreach (var l in PagesAsync($"{Api}{ProjectPath}/issues/{iid}/links?per_page=100", int.MaxValue, ct))
                {
                    var kind = Str(l, "link_type");
                    if (kind is not ("relates_to" or "blocks")) continue;
                    if (projectId != null && l.TryGetProperty("project_id", out var lp) && lp.TryGetInt64(out var lpid) && lpid != projectId) { skippedOtherProject++; continue; }
                    if (Id(l, "id") is not { } target) continue;
                    var t = kind == "blocks" ? IssueLinkType.Blocks : IssueLinkType.RelatesTo;
                    if (t == IssueLinkType.RelatesTo && !relatedPairs.Add(string.CompareOrdinal(id, target) < 0 ? id + "|" + target : target + "|" + id)) continue;
                    if (seen.Add((target, t))) links.Add(new SourceLink(target, t));
                }
                if (skippedOtherProject > 0) notes.Add($"{skippedOtherProject} link(s) to issues in other GitLab projects weren't imported.");
            }

            var assignee = i.TryGetProperty("assignees", out var asg) && asg.ValueKind == JsonValueKind.Array && asg.GetArrayLength() > 0
                ? ToUser(asg[0]) : User(i, "assignee");
            int? weight = i.TryGetProperty("weight", out var w) && w.ValueKind == JsonValueKind.Number && w.TryGetInt32(out var wi) ? Math.Clamp(wi, 0, 100) : null;

            yield return new SourceIssue
            {
                ExternalId = id, DisplayKey = "#" + iid, Url = Str(i, "web_url"), Title = Str(i, "title") ?? "(no title)",
                Body = FixUploads(Str(i, "description"), webUrl), Type = type, Category = closed ? IssueStatus.Done : IssueStatus.Todo,
                Priority = PriorityOf(labels), StoryPoints = weight, DueDate = Date(i, "due_date"),
                CreatedAt = DateTimeOf(i, "created_at") ?? DateTime.UtcNow, UpdatedAt = DateTimeOf(i, "updated_at"),
                ClosedAt = closed ? DateTimeOf(i, "closed_at") : null,
                Author = User(i, "author"), Assignee = assignee, Labels = labels, MilestoneId = milestone, ParentExternalId = parent,
                Links = links, Comments = comments, Notes = notes,
            };
            if (++count >= options.MaxIssues) yield break;
        }
    }

    // --- paging --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Streams the items of a paged list. The next page comes from <c>X-Next-Page</c> or the <c>Link</c> header's <c>page</c> number, applied to
    /// OUR address: GitLab's own absolute links may name an internal host or miss the relative URL root.
    /// </summary>
    private async IAsyncEnumerable<JsonElement> PagesAsync(string firstUrl, int max, [EnumeratorCancellation] CancellationToken ct)
    {
        var url = firstUrl;
        var yielded = 0;
        for (var guard = 0; guard < 100000; guard++)
        {
            var r = await GetAsync(url, ct);
            if (r.Body.ValueKind != JsonValueKind.Array) yield break;
            foreach (var item in r.Body.EnumerateArray())
            {
                if (yielded >= max) yield break;
                yielded++;
                yield return item;
            }
            if (yielded >= max) yield break;
            var next = r.Header("X-Next-Page");
            if (string.IsNullOrWhiteSpace(next) && r.NextLink() is { } link && Uri.TryCreate(link, UriKind.Absolute, out var u))
                next = HttpUtility.ParseQueryString(u.Query)["page"];
            if (string.IsNullOrWhiteSpace(next) || !int.TryParse(next, out var page) || page < 1) yield break;
            var baseUrl = Regex.Replace(firstUrl, @"([?&])page=\d+&?", "$1").TrimEnd('&', '?');
            url = baseUrl + (baseUrl.Contains('?') ? "&" : "?") + "page=" + page;
        }
    }

    // --- mapping -------------------------------------------------------------------------------------------------------

    private static IssueType TypeOf(JsonElement i, List<string> labels)
    {
        var kind = (Str(i, "issue_type") ?? Str(i, "type"))?.ToLowerInvariant();
        if (kind == "incident") return IssueType.Bug;
        foreach (var l in labels)
        {
            var n = l.Trim().ToLowerInvariant();
            if (n == "bug") return IssueType.Bug;
            if (n is "feature" or "enhancement") return IssueType.Feature;
            if (n == "epic") return IssueType.Epic;
        }
        // A GitLab "task" has its parent only in the work item API, which isn't read, so it stays a plain task.
        return IssueType.Task;
    }

    private static IssuePriority? PriorityOf(List<string> labels)
    {
        foreach (var l in labels)
        {
            var m = PriorityLabel().Match(l.Trim());
            if (!m.Success) continue;
            switch (m.Groups[1].Value.ToLowerInvariant())
            {
                case "critical" or "urgent" or "highest": return IssuePriority.Highest;
                case "high": return IssuePriority.High;
                case "medium" or "normal": return IssuePriority.Medium;
                case "low": return IssuePriority.Low;
            }
        }
        return null;
    }

    [GeneratedRegex(@"^priority\s*(?:::|:|/)\s*([A-Za-z]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex PriorityLabel();

    private static string? FixUploads(string? text, string? webUrl) =>
        text == null || webUrl == null ? text : text.Replace("](/uploads/", "](" + webUrl + "/uploads/", StringComparison.Ordinal);

    private string? Absolute(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var abs) && abs.Scheme is "http" or "https") return url;
        return Uri.TryCreate(site, UriKind.Absolute, out var b) ? b.GetLeftPart(UriPartial.Authority) + (url.StartsWith('/') ? url : "/" + url) : null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Id(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.Number => v.GetRawText(), JsonValueKind.String => v.GetString(), _ => null } : null;

    private static SourceUser? User(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var u) ? ToUser(u) : null;

    private static SourceUser? ToUser(JsonElement u) =>
        u.ValueKind == JsonValueKind.Object && (Str(u, "username") ?? Str(u, "name")) is not null ? new SourceUser(Str(u, "username"), Str(u, "name"), null) : null;

    private static DateTime? DateTimeOf(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime : null;

    private static DateTime? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTime.TryParseExact(s[..Math.Min(10, s.Length)], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

    public void Dispose() { }
}
