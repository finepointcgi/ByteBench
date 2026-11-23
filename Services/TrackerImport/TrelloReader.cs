using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ByteBench.Data;
using static ByteBench.Services.TrackerImport.TrackerJson;

namespace ByteBench.Services.TrackerImport;

/// <summary>
/// Reads one Trello board through the REST API 1.0. Trello authenticates with an API key and a token sent as the <c>key</c> and
/// <c>token</c> query parameters (<see cref="SourceConnection.Username"/> is the key, <see cref="SourceConnection.Secret"/> the token), so
/// request addresses are never put into a message: <see cref="TrackerHttp"/> doesn't include them and this reader doesn't either.
/// Imported: cards (title, description, labels, due date, first member, checklists appended to the description, comments, attachment
/// links), with the card's list as its status. Not imported: card covers, custom fields, stickers, votes, power-up data and
/// attachment files. Trello has no parents, links or milestones, so none are created.
/// </summary>
public sealed partial class TrelloReader : ISourceReader
{
    private const string What = "Trello";
    private readonly SourceConnection connection;
    private readonly TrackerHttp http;
    private readonly string api;
    private readonly string board;

    private JsonElement boardInfo;
    private bool boardLoaded;
    private List<TrelloList>? lists;
    private Dictionary<string, SourceUser>? members;

    private sealed record TrelloList(string Id, string Name, bool Closed, IssueStatus Category);

    public TrelloReader(SourceConnection connection, TrackerHttp http)
    {
        this.connection = connection;
        this.http = http;
        api = NormalizeBase(connection.BaseUrl);
        board = BoardId(connection.Project);
        if (string.IsNullOrWhiteSpace(connection.Username))
            throw new SourceException("Trello needs your API key (Username) and a token (Token). Get the key at trello.com/power-ups/admin.");
    }

    /// <summary>The board's id or short link from what the user typed: the id/short link itself, or a <c>trello.com/b/&lt;id&gt;/name</c> address.</summary>
    public static string BoardId(string project)
    {
        project = project.Trim();
        var m = BoardUrl().Match(project);
        return m.Success ? m.Groups[1].Value : project;
    }

    [GeneratedRegex(@"^https?://(?:www\.)?trello\.com/b/([A-Za-z0-9]{8})(?:[/?#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex BoardUrl();

    private static string NormalizeBase(string? baseUrl)
    {
        var b = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.trello.com/1" : baseUrl.Trim().TrimEnd('/');
        return b.EndsWith("/1", StringComparison.Ordinal) ? b : b + "/1";
    }

    /// <summary>Builds the address of a call. The credentials are in the query, so this string must never be logged or shown.</summary>
    private string Url(string path, string? query = null)
    {
        var sb = new StringBuilder(api).Append(path).Append('?');
        if (!string.IsNullOrEmpty(query)) sb.Append(query).Append('&');
        sb.Append("key=").Append(Uri.EscapeDataString(connection.Username!.Trim()));
        if (!string.IsNullOrEmpty(connection.Secret)) sb.Append("&token=").Append(Uri.EscapeDataString(connection.Secret));
        return sb.ToString();
    }

    private Task<TrackerResponse> GetAsync(string path, string? query, CancellationToken ct) =>
        http.GetJsonAsync(Url(path, query), _ => { }, ct, What);

    // --- board ---------------------------------------------------------------------------------------------------------

    private async Task<JsonElement> EnsureBoardAsync(CancellationToken ct)
    {
        if (boardLoaded) return boardInfo;
        var r = await GetAsync("/boards/" + Uri.EscapeDataString(board), "fields=name,desc,closed,url,shortLink", ct);
        if (r.Body.ValueKind != JsonValueKind.Object) throw new SourceException("Trello's answer wasn't a board (is the address the right site?).");
        boardInfo = r.Body;
        boardLoaded = true;
        return boardInfo;
    }

    private string BoardPath => "/boards/" + Uri.EscapeDataString(board);

    public async Task<SourceProbe> ProbeAsync(CancellationToken ct)
    {
        var b = await EnsureBoardAsync(ct);
        var cards = await ReadCardsAsync("all", "id,name", false, ct);
        var samples = cards.OrderBy(c => Str(c, "id"), StringComparer.Ordinal).Take(3).Select(c => Str(c, "name")).OfType<string>().ToList();
        return new SourceProbe(Str(b, "name") ?? board, Str(b, "desc"), cards.Count, samples,
            Bool(b, "closed") ? "This board is archived in Trello." : null);
    }

    private async Task<List<TrelloList>> EnsureListsAsync(CancellationToken ct)
    {
        if (lists != null) return lists;
        var r = await GetAsync(BoardPath + "/lists", "filter=all&fields=name,closed,pos", ct);
        lists = r.Body.ValueKind == JsonValueKind.Array
            ? r.Body.EnumerateArray().Where(l => Str(l, "id") != null)
                .OrderBy(l => Num(l, "pos") ?? double.MaxValue)
                .Select(l => new TrelloList(Str(l, "id")!, Str(l, "name") ?? "(list)", Bool(l, "closed"), CategoryFromName(Str(l, "name")))).ToList()
            : [];
        return lists;
    }

    public async Task<IReadOnlyList<SourceStatus>> ReadStatusesAsync(CancellationToken ct) =>
        (await EnsureListsAsync(ct)).Select(l => new SourceStatus(l.Name, l.Category)).DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<IReadOnlyList<SourceLabel>> ReadLabelsAsync(CancellationToken ct)
    {
        var r = await GetAsync(BoardPath + "/labels", "fields=name,color&limit=1000", ct);
        var list = new List<SourceLabel>();
        if (r.Body.ValueKind == JsonValueKind.Array)
            foreach (var l in r.Body.EnumerateArray())
                if (LabelName(l) is { } name) list.Add(new SourceLabel(name, ColorHex(Str(l, "color"))));
        return list.DistinctBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Task<IReadOnlyList<SourceMilestone>> ReadMilestonesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SourceMilestone>>([]);

    private async Task<Dictionary<string, SourceUser>> EnsureMembersAsync(CancellationToken ct)
    {
        if (members != null) return members;
        var r = await GetAsync(BoardPath + "/members", "fields=fullName,username", ct);
        members = [];
        if (r.Body.ValueKind == JsonValueKind.Array)
            foreach (var m in r.Body.EnumerateArray())
                if (Str(m, "id") is { } id) members[id] = new SourceUser(Str(m, "username"), Str(m, "fullName"), null);
        return members;
    }

    // --- cards ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// All cards, oldest first. Trello pages with <c>before=&lt;id&gt;</c> and may cap a page, so pages are fetched until one brings
    /// nothing new (ids are timestamp-ordered, so <c>before</c> the smallest id seen continues towards older cards).
    /// </summary>
    private async Task<List<JsonElement>> ReadCardsAsync(string filter, string fields, bool withAttachments, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var all = new List<JsonElement>();
        string? before = null;
        for (var page = 0; page < 10000; page++)
        {
            var q = $"fields={fields}&limit=1000" + (withAttachments ? "&attachments=true&attachment_fields=name,url" : "") + (before != null ? "&before=" + Uri.EscapeDataString(before) : "");
            var r = await GetAsync(BoardPath + "/cards/" + filter, q, ct);
            if (r.Body.ValueKind != JsonValueKind.Array) break;
            var fresh = 0;
            string? min = before;
            foreach (var c in r.Body.EnumerateArray())
            {
                if (Str(c, "id") is not { } id) continue;
                if (min == null || string.CompareOrdinal(id, min) < 0) min = id;
                if (seen.Add(id)) { all.Add(c); fresh++; }
            }
            if (fresh == 0 || min == null) break;
            before = min;
        }
        all.Sort((a, b) => string.CompareOrdinal(Str(a, "id"), Str(b, "id")));
        return all;
    }

    public async IAsyncEnumerable<SourceIssue> ReadIssuesAsync(SourceReadOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        var allLists = await EnsureListsAsync(ct);
        var byId = allLists.ToDictionary(l => l.Id);
        var people = await EnsureMembersAsync(ct);
        var cards = await ReadCardsAsync(options.IncludeClosed ? "all" : "open",
            "name,desc,closed,idList,idShort,idMembers,labels,due,dueComplete,shortUrl,dateLastActivity", true, ct);
        var checklists = await ReadChecklistsAsync(ct);
        var comments = options.IncludeComments ? await ReadCommentsAsync(ct) : [];

        var count = 0;
        foreach (var card in cards)
        {
            var id = Str(card, "id")!;
            var list = Str(card, "idList") is { } lid && byId.TryGetValue(lid, out var l) ? l : null;
            var closedCard = Bool(card, "closed");
            if (!options.IncludeClosed && (closedCard || list?.Closed == true)) continue;

            var labelNames = Items(card, "labels").Select(LabelName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var done = closedCard || list?.Closed == true || Bool(card, "dueComplete");
            var category = done ? IssueStatus.Done : list?.Category ?? IssueStatus.Todo;
            // The list name is the status unless the card is closed or done by itself while its list isn't a done list.
            var statusName = list != null && (!done || list.Category == IssueStatus.Done) ? list.Name : null;

            SourceUser? assignee = null;
            foreach (var m in Items(card, "idMembers"))
                if (m.ValueKind == JsonValueKind.String && people.TryGetValue(m.GetString()!, out var u)) { assignee = u; break; }

            var body = new StringBuilder(Str(card, "desc")?.Trim());
            if (checklists.TryGetValue(id, out var cl))
                foreach (var c in cl) body.Append(body.Length > 0 ? "\n\n" : "").Append(c);

            var attachments = Items(card, "attachments")
                .Select(a => new SourceAttachment(Str(a, "name") ?? Str(a, "url") ?? "attachment", Str(a, "url"))).ToList();

            yield return new SourceIssue
            {
                ExternalId = id, DisplayKey = "#" + (Num(card, "idShort") is { } n ? ((long)n).ToString(CultureInfo.InvariantCulture) : id[^6..]),
                Url = Str(card, "shortUrl"), Title = Str(card, "name") ?? "(no title)", Body = body.Length > 0 ? body.ToString() : null,
                Type = TypeFromLabels(labelNames), StatusName = statusName, Category = category, DueDate = DateOf(card, "due"),
                CreatedAt = CreatedFromId(id) ?? DateTime.UtcNow, UpdatedAt = DateTimeOf(card, "dateLastActivity"),
                ClosedAt = done ? DateTimeOf(card, "dateLastActivity") : null,
                Assignee = assignee, Labels = labelNames,
                Comments = comments.GetValueOrDefault(id) ?? [], Attachments = attachments,
            };
            if (++count >= options.MaxIssues) yield break;
        }
    }

    /// <summary>Checklists of the board as markdown task lists, per card id.</summary>
    private async Task<Dictionary<string, List<string>>> ReadChecklistsAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var r = await GetAsync(BoardPath + "/checklists", "checkItems=all&checkItem_fields=name,state,pos&fields=name,idCard", ct);
        if (r.Body.ValueKind != JsonValueKind.Array) return result;
        foreach (var c in r.Body.EnumerateArray())
        {
            if (Str(c, "idCard") is not { } card) continue;
            var sb = new StringBuilder();
            sb.Append("**").Append(Str(c, "name") ?? "Checklist").Append("**\n");
            foreach (var item in Items(c, "checkItems").OrderBy(i => Num(i, "pos") ?? 0))
                sb.Append(Str(item, "state") == "complete" ? "- [x] " : "- [ ] ").Append(Str(item, "name")?.ReplaceLineEndings(" ")).Append('\n');
            if (!result.TryGetValue(card, out var list)) result[card] = list = [];
            list.Add(sb.ToString().TrimEnd());
        }
        return result;
    }

    /// <summary>All comment actions of the board per card, oldest first (Trello lists actions newest first and pages with <c>before=&lt;action id&gt;</c>).</summary>
    private async Task<Dictionary<string, List<SourceComment>>> ReadCommentsAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, List<SourceComment>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? before = null;
        for (var page = 0; page < 10000; page++)
        {
            var q = "filter=commentCard&limit=1000&fields=data,date,type&memberCreator_fields=fullName,username" + (before != null ? "&before=" + Uri.EscapeDataString(before) : "");
            var r = await GetAsync(BoardPath + "/actions", q, ct);
            if (r.Body.ValueKind != JsonValueKind.Array) break;
            var fresh = 0;
            string? min = before;
            foreach (var a in r.Body.EnumerateArray())
            {
                if (Str(a, "id") is not { } aid) continue;
                if (min == null || string.CompareOrdinal(aid, min) < 0) min = aid;
                if (!seen.Add(aid)) continue;
                fresh++;
                if (Str(a, "data.card.id") is not { } card || Str(a, "data.text") is not { Length: > 0 } text) continue;
                var who = (Str(a, "memberCreator.username") ?? Str(a, "memberCreator.fullName")) is not null
                    ? new SourceUser(Str(a, "memberCreator.username"), Str(a, "memberCreator.fullName"), null) : null;
                if (!result.TryGetValue(card, out var list)) result[card] = list = [];
                list.Add(new SourceComment(aid, who, text, DateTimeOf(a, "date") ?? DateTime.UtcNow));
            }
            if (fresh == 0 || min == null) break;
            before = min;
        }
        foreach (var list in result.Values) list.Sort((a, b) => a.CreatedAt != b.CreatedAt ? a.CreatedAt.CompareTo(b.CreatedAt) : string.CompareOrdinal(a.ExternalId, b.ExternalId));
        return result;
    }

    // --- mapping -------------------------------------------------------------------------------------------------------

    /// <summary>A label's name; a label without one is known by its colour.</summary>
    private static string? LabelName(JsonElement l)
    {
        var name = Str(l, "name")?.Trim();
        if (!string.IsNullOrEmpty(name)) return name;
        var color = Str(l, "color");
        return string.IsNullOrEmpty(color) ? null : color;
    }

    private static string? ColorHex(string? color) => color?.ToLowerInvariant() switch
    {
        "green" => "#61bd4f", "yellow" => "#f2d600", "orange" => "#ff9f1a", "red" => "#eb5a46", "purple" => "#c377e0",
        "blue" => "#0079bf", "sky" => "#00c2e0", "lime" => "#51e898", "pink" => "#ff78cb", "black" => "#344563",
        _ => null,
    };

    /// <summary>A Trello id is a Mongo ObjectId: its first 4 bytes are the creation time in seconds.</summary>
    private static DateTime? CreatedFromId(string id) =>
        id.Length == 24 && int.TryParse(id.AsSpan(0, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var secs) && secs > 0
            ? DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime : null;

    public void Dispose() { }
}
