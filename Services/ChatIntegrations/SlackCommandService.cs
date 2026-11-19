using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Services.Explore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.ChatIntegrations;

/// <summary>
/// The Slack app's behaviour (scoped, one per request): the <c>/bytebench</c> slash command, button clicks
/// (interactivity) and link unfurling. Requests reach it only after <see cref="SlackSignature"/> accepted them. The
/// Slack user must have linked a ByteBench account (<see cref="SlackAccountLinks"/>); everything then runs as that user,
/// so <see cref="ProjectAccess"/> decides what they may see and do, exactly as on the website. Replies are ephemeral
/// (only the caller sees them), so nothing a user may see leaks into a shared channel.
/// </summary>
public sealed partial class SlackCommandService(ProjectAccess access, IssueService issues, PullRequestService pullRequests, SearchService search,
    SlackAccountLinks links, ChatHttp http, IOptionsMonitor<SlackOptions> options, ILogger<SlackCommandService> log)
{
    public const string AssignToMeAction = "bytebench_assign_me";
    public const int MaxTitle = 200;

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9]{1,9})-(\d{1,9})$")]
    private static partial Regex IssueKey();

    [GeneratedRegex(@"^/p/([A-Za-z][A-Za-z0-9]{1,9})/issues/(\d{1,9})/?$")]
    private static partial Regex IssuePath();

    [GeneratedRegex(@"^/p/([A-Za-z][A-Za-z0-9]{1,9})/repos/([A-Za-z0-9._-]{1,100})/pull-requests/(\d{1,9})/?$")]
    private static partial Regex PullRequestPath();

    private const string Help =
        "*ByteBench commands*\n" +
        "• `/bytebench issue KEY-12`: show an issue\n" +
        "• `/bytebench create KEY Title of the issue`: create an issue\n" +
        "• `/bytebench search words`: search issues and pull requests\n" +
        "• `/bytebench link` / `/bytebench unlink`: connect or disconnect your ByteBench account";

    // ----- Slash command -----

    /// <summary>Handles <c>/bytebench …</c>; returns the JSON reply.</summary>
    public async Task<JsonObject> HandleCommandAsync(IReadOnlyDictionary<string, string> form, string baseUrl)
    {
        var team = form.GetValueOrDefault("team_id") ?? "";
        var slackUser = form.GetValueOrDefault("user_id") ?? "";
        if (!TeamAllowed(team)) return SlackFormat.Reply("This ByteBench server isn't connected to your Slack workspace.");
        var text = (form.GetValueOrDefault("text") ?? "").Trim();
        var space = text.IndexOf(' ');
        var verb = (space < 0 ? text : text[..space]).ToLowerInvariant();
        var rest = space < 0 ? "" : text[(space + 1)..].Trim();

        if (verb is "" or "help") return SlackFormat.ReplyMrkdwn(Help, "ByteBench commands");
        if (verb == "unlink")
            return SlackFormat.Reply(await links.UnlinkAsync(team, slackUser)
                ? "Your Slack account is no longer linked to ByteBench." : "Your Slack account isn't linked to ByteBench.");

        var userId = await links.FindUserAsync(team, slackUser);
        if (verb == "link" || userId == null)
            return userId != null && verb == "link"
                ? SlackFormat.Reply("Your Slack account is already linked to ByteBench. Use `/bytebench unlink` to disconnect it.")
                : LinkPrompt(baseUrl, team, slackUser, form.GetValueOrDefault("user_name"));

        access.ActAs(userId.Value);
        try
        {
            return verb switch
            {
                "issue" or "show" => await IssueAsync(rest, baseUrl),
                "create" or "new" => await CreateAsync(rest, userId.Value, baseUrl),
                "search" or "find" => await SearchAsync(rest, baseUrl),
                _ => SlackFormat.ReplyMrkdwn($"I don't know `{SlackFormat.Text(verb, 30)}`.\n\n" + Help, "Unknown command"),
            };
        }
        catch (InvalidOperationException ex) // ForbiddenException included: its message is meant for users
        {
            return SlackFormat.Reply(ex.Message);
        }
    }

    private JsonObject LinkPrompt(string baseUrl, string team, string slackUser, string? slackName)
    {
        var url = links.LinkUrl(baseUrl, team, slackUser, slackName);
        return SlackFormat.Reply(
            $"Link your ByteBench account first: open the link below, sign in to ByteBench and confirm. The link works once and expires in {SlackAccountLinks.TokenLifetime.TotalMinutes:0} minutes.",
            extraBlocks: [SlackFormat.LinkButton("Link ByteBench account", url, "bytebench_link")]);
    }

    private async Task<JsonObject> IssueAsync(string arg, string baseUrl)
    {
        if (IssueKey().Match(arg) is not { Success: true } m) return SlackFormat.Reply("Usage: `/bytebench issue KEY-12`");
        var issue = await issues.GetIssueAsync(m.Groups[1].Value, int.Parse(m.Groups[2].Value));
        if (issue == null) return SlackFormat.Reply($"Issue {arg.ToUpperInvariant()} doesn't exist, or you don't have access to it.");
        var card = ChatEventClassifier.IssueCard(issue, baseUrl, issue.Key);
        var canEdit = await access.CanAsync(issue.ProjectId, Permission.EditIssues);
        var actions = new JsonArray();
        if (canEdit && issue.AssigneeId != await access.ResolveUserIdAsync())
            actions.Add(new JsonObject
            {
                ["type"] = "button", ["action_id"] = AssignToMeAction, ["value"] = issue.Id.ToString(),
                ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = "Assign to me" },
            });
        if (ChatText.SafeUrl(card.Url) is { } url)
            actions.Add(new JsonObject
            {
                ["type"] = "button", ["action_id"] = "bytebench_open", ["url"] = url,
                ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = "Open in ByteBench" },
            });
        return SlackFormat.Reply($"{issue.Key}: {issue.Title}", card,
            extraBlocks: actions.Count == 0 ? null : [new JsonObject { ["type"] = "actions", ["elements"] = actions }]);
    }

    private async Task<JsonObject> CreateAsync(string arg, int userId, string baseUrl)
    {
        var space = arg.IndexOf(' ');
        if (space < 0) return SlackFormat.Reply("Usage: `/bytebench create KEY Title of the issue`");
        var key = arg[..space].Trim();
        var title = ChatText.OneLine(arg[(space + 1)..]);
        if (title.Length == 0) return SlackFormat.Reply("Give the issue a title: `/bytebench create KEY Title of the issue`");
        if (title.Length > MaxTitle) return SlackFormat.Reply($"The title is too long (max {MaxTitle} characters).");
        var project = await issues.GetProjectAsync(key);
        if (project == null) return SlackFormat.Reply($"Project {key.ToUpperInvariant()} doesn't exist, or you don't have access to it.");
        await access.RequireAsync(project.Id, Permission.EditIssues); // a friendly message before CreateIssueAsync checks again
        var created = await issues.CreateIssueAsync(project.Id, new Issue { Title = title, Type = IssueType.Task }, [], userId);
        var issue = await issues.GetIssueAsync(project.Key, created.Number);
        return issue == null
            ? SlackFormat.Reply($"Created {project.Key}-{created.Number}.")
            : SlackFormat.Reply($"Created {issue.Key}.", ChatEventClassifier.IssueCard(issue, baseUrl, issue.Key));
    }

    private async Task<JsonObject> SearchAsync(string arg, string baseUrl)
    {
        if (arg.Length == 0) return SlackFormat.Reply("Usage: `/bytebench search words`");
        var results = await search.SearchAsync(arg, [SearchType.Issues, SearchType.PullRequests], limit: 8);
        if (results.Issues.Count == 0 && results.PullRequests.Count == 0)
            return SlackFormat.Reply($"Nothing found for \"{ChatText.Truncate(arg, 100)}\".");
        var lines = new List<string> { $"*Results for \"{SlackFormat.Text(arg, 100)}\"*" };
        lines.AddRange(results.Issues.Select(i =>
            $"• {SlackFormat.Link(baseUrl + i.Url, $"{i.Key} {i.Title}", 150)} · {SlackFormat.Text(ChatEventClassifier.Humanize(i.Status.ToString()), 40)}"));
        lines.AddRange(results.PullRequests.Select(p =>
            $"• {SlackFormat.Link(baseUrl + p.Url, $"{p.Repository} #{p.Number} {p.Title}", 150)} · {SlackFormat.Text(p.State.ToString(), 40)}"));
        var more = results.IssueCount + results.PullRequestCount - results.Issues.Count - results.PullRequests.Count;
        if (more > 0) lines.Add($"…and {more} more: {SlackFormat.Link($"{baseUrl}/search?q={Uri.EscapeDataString(arg)}", "see all results")}");
        return SlackFormat.ReplyMrkdwn(string.Join("\n", lines), $"Results for {arg}");
    }


    // ----- Interactivity -----

    /// <summary>
    /// Handles a button click (<c>block_actions</c>). The answer goes to the payload's <c>response_url</c> as an ephemeral
    /// message; returns it (null when nothing was answered).
    /// </summary>
    public async Task<JsonObject?> HandleInteractionAsync(JsonObject payload, string baseUrl)
    {
        if (payload["type"]?.GetValue<string>() != "block_actions") return null;
        var team = payload["team"]?["id"]?.GetValue<string>() ?? payload["user"]?["team_id"]?.GetValue<string>() ?? "";
        var slackUser = payload["user"]?["id"]?.GetValue<string>() ?? "";
        var responseUrl = payload["response_url"]?.GetValue<string>();
        var action = (payload["actions"] as JsonArray)?.FirstOrDefault();
        var actionId = action?["action_id"]?.GetValue<string>();
        if (actionId != AssignToMeAction) return null; // link buttons need no answer

        JsonObject reply;
        if (!TeamAllowed(team)) reply = SlackFormat.Reply("This ByteBench server isn't connected to your Slack workspace.");
        else if (await links.FindUserAsync(team, slackUser) is not { } userId)
            reply = LinkPrompt(baseUrl, team, slackUser, payload["user"]?["username"]?.GetValue<string>());
        else if (!int.TryParse(action?["value"]?.GetValue<string>(), out var issueId)) reply = SlackFormat.Reply("That button is out of date.");
        else
        {
            access.ActAs(userId);
            try
            {
                await access.RequireForIssueAsync(issueId, Permission.EditIssues);
                await issues.UpdateIssueAsync(issueId, userId, (i, _) =>
                {
                    if (i.AssigneeId == userId) return Task.FromResult<string?>(null);
                    i.AssigneeId = userId;
                    return Task.FromResult<string?>("self-assigned this issue");
                });
                reply = SlackFormat.Reply("Assigned to you.");
            }
            catch (InvalidOperationException ex) { reply = SlackFormat.Reply(ex.Message); }
        }
        reply["replace_original"] = false;
        await RespondAsync(responseUrl, reply);
        return reply;
    }

    /// <summary>Posts to a Slack <c>response_url</c> (only ever to Slack's own hosts).</summary>
    private async Task RespondAsync(string? responseUrl, JsonObject reply)
    {
        if (!Uri.TryCreate(responseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !(ChatUrlRules.HostMatches("hooks.slack.com", uri.Host) || ChatUrlRules.HostMatches(".slack.com", uri.Host)))
        {
            log.LogWarning("Slack interaction without a usable response_url");
            return;
        }
        var result = await http.PostWebhookAsync(uri.AbsoluteUri, reply.ToJsonString(), [uri.AbsoluteUri], CancellationToken.None);
        if (!result.Success) log.LogWarning("Could not answer a Slack interaction: {Error}", result.Error);
    }

    // ----- Events API: link unfurling -----

    /// <summary>
    /// <c>link_shared</c>: unfurls ByteBench issue and pull request links the sharing user may see (needs the bot token).
    /// An unlinked user is offered the account-link flow instead. Returns the <c>chat.unfurl</c> body sent, if any.
    /// </summary>
    public async Task<JsonObject?> HandleEventAsync(JsonObject envelope, string baseUrl)
    {
        var ev = envelope["event"];
        if (envelope["type"]?.GetValue<string>() != "event_callback" || ev?["type"]?.GetValue<string>() != "link_shared") return null;
        var token = options.CurrentValue.BotToken;
        if (string.IsNullOrWhiteSpace(token)) return null;
        var team = envelope["team_id"]?.GetValue<string>() ?? "";
        if (!TeamAllowed(team)) return null;
        var slackUser = ev["user"]?.GetValue<string>() ?? "";
        var urls = (ev["links"] as JsonArray)?.Select(l => l?["url"]?.GetValue<string>()).Where(u => u != null).Cast<string>().Distinct().Take(10).ToList() ?? [];
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var site)) return null;
        urls = urls.Where(u => Uri.TryCreate(u, UriKind.Absolute, out var x) && x.Scheme is "http" or "https"
            && string.Equals(x.Authority, site.Authority, StringComparison.OrdinalIgnoreCase)).ToList();
        if (urls.Count == 0) return null;

        var body = new JsonObject();
        if (ev["unfurl_id"]?.GetValue<string>() is { } unfurlId && ev["source"]?.GetValue<string>() is { } source)
        {
            body["unfurl_id"] = unfurlId;
            body["source"] = source;
        }
        else
        {
            body["channel"] = ev["channel"]?.GetValue<string>();
            body["ts"] = ev["message_ts"]?.GetValue<string>();
        }

        if (await links.FindUserAsync(team, slackUser) is not { } userId)
        {
            body["user_auth_message"] = "Link your ByteBench account to see previews of ByteBench links.";
            body["user_auth_url"] = links.LinkUrl(baseUrl, team, slackUser, null);
            body["unfurls"] = new JsonObject();
        }
        else
        {
            access.ActAs(userId);
            var unfurls = new JsonObject();
            foreach (var url in urls)
                if (await CardForUrlAsync(new Uri(url), baseUrl) is { } card) unfurls[url] = SlackFormat.Attachment(card);
            if (unfurls.Count == 0) return null;
            body["unfurls"] = unfurls;
        }
        var result = await http.CallSlackAsync("chat.unfurl", token, body.ToJsonString(), [], CancellationToken.None);
        if (!result.Success) log.LogWarning("Slack chat.unfurl failed: {Error}", result.Error);
        return body;
    }

    /// <summary>A card for an issue or pull request URL the acting user can see; null otherwise.</summary>
    private async Task<ChatMessage?> CardForUrlAsync(Uri url, string baseUrl)
    {
        var path = url.AbsolutePath;
        if (IssuePath().Match(path) is { Success: true } im)
        {
            var issue = await issues.GetIssueAsync(im.Groups[1].Value, int.Parse(im.Groups[2].Value));
            return issue == null ? null : ChatEventClassifier.IssueCard(issue, baseUrl, issue.Key);
        }
        if (PullRequestPath().Match(path) is { Success: true } pm)
        {
            var pr = await pullRequests.GetAsync(pm.Groups[1].Value, pm.Groups[2].Value, int.Parse(pm.Groups[3].Value));
            return pr == null ? null : ChatEventClassifier.PullRequestCard(pr, baseUrl, $"{pr.Repository.Slug} #{pr.Number}");
        }
        return null;
    }

    private bool TeamAllowed(string team) =>
        string.IsNullOrWhiteSpace(options.CurrentValue.TeamId) || string.Equals(options.CurrentValue.TeamId, team, StringComparison.Ordinal);
}
