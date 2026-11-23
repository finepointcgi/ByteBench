using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using ByteBench.Data;

namespace ByteBench.Services.TrackerImport;

/// <summary>
/// Reads a GitHub repository through the REST API v3 (github.com or GitHub Enterprise Server). <see cref="SourceConnection.BaseUrl"/> is the
/// API root (blank: https://api.github.com; GHES: https://ghe.example.com/api/v3), <see cref="SourceConnection.Project"/> is <c>owner/repo</c>,
/// and an optional token is sent as a Bearer token. Every request goes through <see cref="TrackerHttp"/> (guarded, rate-limit aware); the
/// token is never put in a message. Paging follows the <c>Link: rel="next"</c> header, and only to the same host as the API root so the
/// token can't be sent elsewhere.
/// <para>
/// Mapping notes: the issues endpoint also returns pull requests (items with a <c>pull_request</c> key); they are skipped unless asked for,
/// and then imported as issues labelled "pull-request". GitHub has only open/closed, so statuses are Todo and Done. The external id is the
/// issue's numeric <c>id</c> (stable), the display key is <c>#number</c>. Sub-issues (<c>parent_issue_url</c>) are linked to their parent
/// when the parent was read earlier in the stream (oldest first); a parent that is newer than its child, or not read at all, leaves the
/// child without a parent and adds a note.
/// </para>
/// </summary>
public sealed class GitHubReader(SourceConnection connection, TrackerHttp http) : ISourceReader
{
    private const string What = "GitHub";
    private const string DefaultApi = "https://api.github.com";
    private static readonly Regex IssueNumberAtEnd = new(@"/issues/(\d+)/?$", RegexOptions.Compiled);
    private static readonly Regex PriorityLabel = new(@"^\s*priority\s*[:/]\s*(.+?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal SourceConnection Connection { get; } = connection;
    internal TrackerHttp Http { get; } = http;

    private string Api => string.IsNullOrWhiteSpace(Connection.BaseUrl) ? DefaultApi : Connection.BaseUrl.Trim().TrimEnd('/');
    private string Repo
    {
        get
        {
            var parts = Connection.Project.Trim().Split('/');
            return "/repos/" + string.Join('/', parts.Select(Uri.EscapeDataString));
        }
    }

    private void Configure(HttpRequestMessage request)
    {
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(Connection.Secret)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Connection.Secret.Trim());
    }

    private Task<TrackerResponse> GetAsync(string url, CancellationToken ct) => Http.GetJsonAsync(url, Configure, ct, What);

    /// <summary>Yields every element of every page of a list endpoint.</summary>
    private async IAsyncEnumerable<JsonElement> ListAsync(string firstUrl, [EnumeratorCancellation] CancellationToken ct)
    {
        var url = firstUrl;
        var origin = new Uri(Api).GetLeftPart(UriPartial.Authority);
        for (var page = 0; url != null; page++)
        {
            if (page > 10_000) throw new SourceException("GitHub's paging didn't end; stopping.");
            var response = await GetAsync(url, ct);
            if (response.Body.ValueKind == JsonValueKind.Array)
                foreach (var item in response.Body.EnumerateArray()) yield return item;
            var next = response.NextLink();
            if (next != null && (!Uri.TryCreate(next, UriKind.Absolute, out var nextUri) ||
                !string.Equals(nextUri.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase)))
                throw new SourceException("GitHub's paging pointed at a different address, so the import stopped (the token is only sent to the configured address).");
            url = next;
        }
    }

    public async Task<SourceProbe> ProbeAsync(CancellationToken ct)
    {
        var repo = (await GetAsync(Api + Repo, ct)).Body;
        if (repo.ValueKind != JsonValueKind.Object) throw new SourceException("GitHub's answer wasn't a repository (is the address the API root?).");
        var name = Str(repo, "full_name") ?? Connection.Project;
        if (repo.TryGetProperty("has_issues", out var hi) && hi.ValueKind == JsonValueKind.False)
            throw new SourceException($"Issues are turned off for {name} on GitHub, so there is nothing to import.");
        var open = repo.TryGetProperty("open_issues_count", out var oc) && oc.ValueKind == JsonValueKind.Number ? oc.GetInt32() : 0;

        var samples = new List<string>();
        var sample = await GetAsync($"{Api}{Repo}/issues?state=all&sort=created&direction=asc&per_page=3", ct);
        if (sample.Body.ValueKind == JsonValueKind.Array)
            foreach (var i in sample.Body.EnumerateArray())
                if (!i.TryGetProperty("pull_request", out _) && Str(i, "title") is { } t) samples.Add(t);

        var warnings = new List<string> { $"GitHub counts about {open} open (issues and pull requests together)." };
        if (string.IsNullOrWhiteSpace(Connection.Secret))
            warnings.Add("No token: GitHub limits unauthenticated requests to 60 per hour, so a large import may stop early. Private repositories need a token.");
        return new SourceProbe(name, Str(repo, "description"), open, samples, string.Join(" ", warnings));
    }

    public Task<IReadOnlyList<SourceStatus>> ReadStatusesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SourceStatus>>([new("Open", IssueStatus.Todo), new("Closed", IssueStatus.Done)]);

    public async Task<IReadOnlyList<SourceLabel>> ReadLabelsAsync(CancellationToken ct)
    {
        var labels = new List<SourceLabel>();
        await foreach (var l in ListAsync($"{Api}{Repo}/labels?per_page=100", ct))
            if (Str(l, "name") is { Length: > 0 } n) labels.Add(new SourceLabel(n, Str(l, "color")));
        return labels;
    }

    public async Task<IReadOnlyList<SourceMilestone>> ReadMilestonesAsync(CancellationToken ct)
    {
        var list = new List<SourceMilestone>();
        await foreach (var m in ListAsync($"{Api}{Repo}/milestones?state=all&per_page=100", ct))
        {
            if (!m.TryGetProperty("number", out var num) || num.ValueKind != JsonValueKind.Number) continue;
            list.Add(new SourceMilestone(num.GetInt64().ToString(CultureInfo.InvariantCulture), Str(m, "title") ?? "Milestone " + num,
                Str(m, "description"), null, Date(m, "due_on"), Str(m, "state") == "closed"));
        }
        return list;
    }

    public async IAsyncEnumerable<SourceIssue> ReadIssuesAsync(SourceReadOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        var state = options.IncludeClosed ? "all" : "open";
        var idByNumber = new Dictionary<long, string>();
        var yielded = 0;
        await foreach (var i in ListAsync($"{Api}{Repo}/issues?state={state}&sort=created&direction=asc&per_page=100", ct))
        {
            if (yielded >= options.MaxIssues) yield break;
            var isPr = i.TryGetProperty("pull_request", out var pr) && pr.ValueKind == JsonValueKind.Object;
            if (isPr && !options.IncludePullRequests) continue;
            if (!i.TryGetProperty("id", out var idEl) || !i.TryGetProperty("number", out var numEl)) continue;
            var id = idEl.GetInt64().ToString(CultureInfo.InvariantCulture);
            var number = numEl.GetInt64();
            idByNumber[number] = id;

            var notes = new List<string>();
            var labels = new List<string>();
            if (i.TryGetProperty("labels", out var ls) && ls.ValueKind == JsonValueKind.Array)
                foreach (var l in ls.EnumerateArray())
                {
                    var n = l.ValueKind == JsonValueKind.String ? l.GetString() : Str(l, "name");
                    if (!string.IsNullOrWhiteSpace(n)) labels.Add(n);
                }
            if (isPr) { labels.Add("pull-request"); notes.Add("this is a pull request; it was imported as an issue (its branches and diff were not imported)."); }

            var closed = Str(i, "state") == "closed";
            if (closed && Str(i, "state_reason") == "not_planned") notes.Add("closed as not planned");

            string? parent = null;
            if (Str(i, "parent_issue_url") is { } purl && IssueNumberAtEnd.Match(purl) is { Success: true } pm)
            {
                if (idByNumber.TryGetValue(long.Parse(pm.Groups[1].Value, CultureInfo.InvariantCulture), out var pid)) parent = pid;
                else notes.Add($"its parent #{pm.Groups[1].Value} was not read before it (newer, filtered out or a pull request), so it has no parent here.");
            }

            var comments = new List<SourceComment>();
            if (options.IncludeComments && i.TryGetProperty("comments", out var cc) && cc.ValueKind == JsonValueKind.Number && cc.GetInt32() > 0)
                await foreach (var c in ListAsync($"{Api}{Repo}/issues/{number}/comments?per_page=100", ct))
                    if (c.TryGetProperty("id", out var cid))
                        comments.Add(new SourceComment(cid.GetInt64().ToString(CultureInfo.InvariantCulture), User(c, "user"), Str(c, "body") ?? "", Date(c, "created_at") ?? DateTime.UtcNow));

            var assignee = i.TryGetProperty("assignee", out var a) && a.ValueKind == JsonValueKind.Object ? a
                : i.TryGetProperty("assignees", out var aa) && aa.ValueKind == JsonValueKind.Array && aa.GetArrayLength() > 0 ? aa[0] : (JsonElement?)null;

            yield return new SourceIssue
            {
                ExternalId = id, DisplayKey = "#" + number, Url = Str(i, "html_url"), Title = Str(i, "title") ?? "(untitled)", Body = Str(i, "body"),
                Type = TypeOf(labels), Category = closed ? IssueStatus.Done : IssueStatus.Todo, Priority = PriorityOf(labels),
                CreatedAt = Date(i, "created_at") ?? DateTime.UtcNow, UpdatedAt = Date(i, "updated_at"), ClosedAt = Date(i, "closed_at"),
                Author = User(i, "user"), Assignee = assignee is { } ae ? ToUser(ae) : null, Labels = labels,
                MilestoneId = i.TryGetProperty("milestone", out var ms) && ms.ValueKind == JsonValueKind.Object && ms.TryGetProperty("number", out var mn)
                    ? mn.GetInt64().ToString(CultureInfo.InvariantCulture) : null,
                ParentExternalId = parent, Comments = comments, Notes = notes,
            };
            yielded++;
        }
    }

    /// <summary>bug → Bug, enhancement/feature/feature request → Feature, epic → Epic, question/documentation/chore → Task; first matching label wins.</summary>
    internal static IssueType TypeOf(IEnumerable<string> labels)
    {
        foreach (var l in labels)
            switch (l.Trim().ToLowerInvariant())
            {
                case "bug": return IssueType.Bug;
                case "enhancement" or "feature" or "feature request": return IssueType.Feature;
                case "epic": return IssueType.Epic;
                case "question" or "documentation" or "chore": return IssueType.Task;
            }
        return IssueType.Task;
    }

    /// <summary>Labels like "priority: high", "priority/low", "P0".."P3"; the first that is understood wins.</summary>
    internal static IssuePriority? PriorityOf(IEnumerable<string> labels)
    {
        foreach (var l in labels)
        {
            var t = l.Trim().ToLowerInvariant();
            var v = PriorityLabel.Match(l) is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : Regex.IsMatch(t, "^p[0-3]$") ? t : null;
            switch (v)
            {
                case "p0" or "critical" or "urgent" or "highest" or "blocker": return IssuePriority.Highest;
                case "p1" or "high": return IssuePriority.High;
                case "p2" or "medium" or "normal": return IssuePriority.Medium;
                case "p3" or "low": return IssuePriority.Low;
                case "lowest": return IssuePriority.Lowest;
            }
        }
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTime? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime : null;

    private static SourceUser? User(JsonElement e, string name) =>
        e.TryGetProperty(name, out var u) && u.ValueKind == JsonValueKind.Object ? ToUser(u) : null;

    private static SourceUser? ToUser(JsonElement u) => Str(u, "login") is { Length: > 0 } login ? new SourceUser(login, null, null) : null;

    /// <summary>The reader doesn't own <see cref="TrackerHttp"/> (a shared singleton).</summary>
    public void Dispose() { }
}
