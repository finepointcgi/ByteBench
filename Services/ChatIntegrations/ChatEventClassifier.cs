using System.Text.Json.Nodes;
using ByteBench.Data;
using ByteBench.Services.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.ChatIntegrations;

/// <summary>
/// Turns a generic webhook envelope (the JSON a webhook would receive) into chat messages: which chat events it is
/// (<see cref="ChatEvents"/>) and what to show. One webhook event can yield several (an issue comment that @mentions
/// someone is both <c>issue.commented</c> and <c>mention</c>), or none (a passing pipeline that wasn't failing before).
/// </summary>
public static class ChatEventClassifier
{
    public static async Task<List<ChatMessage>> ClassifyAsync(AppDbContext db, string eventName, JsonObject env, CancellationToken ct = default)
    {
        var result = new List<ChatMessage>();
        var actor = Name(env["sender"]);
        var projectKey = S(env, "project", "key");
        switch (eventName)
        {
            case WebhookEventNames.IssueCreated:
            {
                var issue = env["issue"];
                result.Add(Issue(issue, ChatEvents.IssueCreated, $"{actor ?? "Someone"} created {S(issue, "key")}", actor, projectKey,
                    body: ChatText.Excerpt(S(issue, "description"), 600)));
                await AddMentionAsync(db, result, S(issue, "description"), env, issue, actor, projectKey, ct);
                break;
            }
            case WebhookEventNames.IssueUpdated:
            {
                var issue = env["issue"];
                var fields = (env["changedFields"] as JsonArray)?.Select(n => n?.GetValue<string>()).ToList() ?? [];
                var change = ChatText.StripBold(S(env, "change"));
                if (fields.Contains("status"))
                {
                    var status = StatusName(issue);
                    result.Add(Issue(issue, ChatEvents.IssueTransitioned, $"{actor ?? "Someone"} moved {S(issue, "key")} to {status}", actor, projectKey,
                        status: S(issue, "status") == "done" ? ChatStatus.Good : ChatStatus.Info,
                        body: fields.Count == 1 ? null : ChatText.Excerpt(change, 300)));
                }
                if (fields.Contains("assignee"))
                {
                    var assignee = Name(issue?["assignee"]);
                    result.Add(Issue(issue, ChatEvents.IssueAssigned,
                        assignee == null ? $"{actor ?? "Someone"} unassigned {S(issue, "key")}" : $"{actor ?? "Someone"} assigned {S(issue, "key")} to {assignee}",
                        actor, projectKey));
                }
                break;
            }
            case WebhookEventNames.IssueCommented:
            {
                var issue = env["issue"];
                var comment = S(env, "comment", "body");
                result.Add(Issue(issue, ChatEvents.IssueCommented, $"{actor ?? "Someone"} commented on {S(issue, "key")}", actor, projectKey,
                    body: ChatText.Excerpt(comment, 800)));
                await AddMentionAsync(db, result, comment, env, issue, actor, projectKey, ct);
                break;
            }
            case WebhookEventNames.PullRequestOpened:
            {
                var pr = env["pullRequest"];
                result.Add(PullRequest(pr, ChatEvents.PullRequestOpened, $"{actor ?? "Someone"} opened {PrRef(pr)}", actor, projectKey,
                    body: ChatText.Excerpt(S(pr, "description"), 600)));
                await AddMentionAsync(db, result, S(pr, "description"), env, null, actor, projectKey, ct);
                break;
            }
            case WebhookEventNames.PullRequestMerged:
            {
                var pr = env["pullRequest"];
                result.Add(PullRequest(pr, ChatEvents.PullRequestMerged, $"{actor ?? "Someone"} merged {PrRef(pr)}", actor, projectKey, ChatStatus.Good));
                break;
            }
            case WebhookEventNames.PullRequestUpdated:
            {
                var pr = env["pullRequest"];
                switch (S(env, "action"))
                {
                    case "approved":
                        result.Add(PullRequest(pr, ChatEvents.PullRequestApproved, $"{actor ?? "Someone"} approved {PrRef(pr)}", actor, projectKey, ChatStatus.Good));
                        break;
                    case "review_requested":
                        var reviewers = (env["requestedReviewers"] as JsonArray)?.Select(Name).Where(n => n != null).ToList() ?? [];
                        var m = PullRequest(pr, ChatEvents.PullRequestReviewRequested,
                            $"{actor ?? "Someone"} requested a review of {PrRef(pr)} from {(reviewers.Count == 0 ? "reviewers" : string.Join(", ", reviewers))}",
                            actor, projectKey, ChatStatus.Warning);
                        if (reviewers.Count > 0) m.Fields.Add(new("Reviewers", string.Join(", ", reviewers)));
                        result.Add(m);
                        break;
                }
                break;
            }
            case WebhookEventNames.PullRequestCommented:
            {
                var pr = env["pullRequest"];
                var comment = S(env, "comment", "body");
                if (Mentions.Extract(comment).Count > 0)
                    await AddMentionAsync(db, result, comment, env, null, actor, projectKey, ct, prSummaryTarget: PrRef(pr), pr: pr);
                break;
            }
            case WebhookEventNames.PipelineCompleted:
            {
                var p = env["pipeline"];
                var status = S(p, "status");
                var id = (int?)p?["id"]?.GetValue<int>() ?? 0;
                var branch = ChatText.ShortRef(S(p, "ref"));
                var repo = S(env, "repository", "slug");
                string? chatEvent = null;
                if (status is "failed" or "error") chatEvent = ChatEvents.PipelineFailed;
                else if (status == "passed" && await PreviousFailedAsync(db, (int?)env["repository"]?["id"]?.GetValue<int>() ?? 0, S(p, "ref") ?? "", id, ct))
                    chatEvent = ChatEvents.PipelineFixed;
                if (chatEvent == null) break;
                var failed = chatEvent == ChatEvents.PipelineFailed;
                result.Add(new ChatMessage
                {
                    Event = chatEvent,
                    Summary = failed ? $"Pipeline #{id} failed on {branch} in {repo}" : $"Pipeline #{id} is green again on {branch} in {repo}",
                    Title = $"{repo} pipeline #{id} · {branch}",
                    Url = S(p, "url"),
                    Status = failed ? ChatStatus.Danger : ChatStatus.Good,
                    Actor = actor,
                    Context = Join(projectKey, repo),
                    Body = failed ? ChatText.Excerpt(S(p, "error"), 500) : null,
                    Fields =
                    [
                        new("Status", Humanize(status)), new("Branch", branch), new("Commit", ChatText.ShortSha(S(p, "commitSha"))),
                        new("Trigger", Humanize(S(p, "trigger"))),
                    ],
                    Branch = branch,
                });
                break;
            }
            case WebhookEventNames.DeploymentStatus:
            {
                var d = env["deployment"];
                var status = S(d, "status");
                if (status is not ("success" or "failed" or "canceled")) break; // announce finished deployments only
                var environment = S(env, "environment", "name");
                var branch = ChatText.ShortRef(S(d, "ref"));
                var repo = S(env, "repository", "slug");
                var (verb, color) = status switch
                {
                    "success" => ("deployed", ChatStatus.Good),
                    "failed" => ("failed to deploy", ChatStatus.Danger),
                    "canceled" => ("canceled deploying", ChatStatus.Warning),
                    _ => ("finished deploying", ChatStatus.Info),
                };
                result.Add(new ChatMessage
                {
                    Event = ChatEvents.Deployment,
                    Summary = $"{repo} {verb} {branch} to {environment}",
                    Title = $"{environment} · {repo}",
                    Url = S(d, "url"),
                    Status = color,
                    Actor = actor,
                    Context = Join(projectKey, repo),
                    Fields =
                    [
                        new("Environment", environment ?? ""), new("Status", Humanize(status)), new("Branch", branch),
                        new("Commit", ChatText.ShortSha(S(d, "commitSha"))), new("Job", S(d, "jobName") ?? ""),
                        new("URL", ChatText.SafeUrl(S(env, "environment", "externalUrl")) ?? ""),
                    ],
                    Branch = branch,
                });
                break;
            }
            case WebhookEventNames.FlagChanged:
            {
                var key = S(env, "flag", "key");
                var environment = S(env, "environment", "name");
                var kind = S(env, "change", "kind") ?? "changed";
                var what = kind switch
                {
                    "toggled" => $"turned {S(env, "change", "newValue")}",
                    "created" => "created",
                    "archived" => "archived",
                    "unarchived" => "restored",
                    "deleted" => "deleted",
                    "rules" => "changed the targeting rules of",
                    "default_value" => "changed the default value of",
                    "off_value" => "changed the off value of",
                    _ => "updated",
                };
                result.Add(new ChatMessage
                {
                    Event = ChatEvents.FlagChanged,
                    Summary = $"{actor ?? "Someone"} {what} flag {key}" + (environment != null ? $" in {environment}" : ""),
                    Title = $"Flag {key}" + (environment != null ? $" · {environment}" : ""),
                    Url = S(env, "url"),
                    Status = kind == "deleted" ? ChatStatus.Warning : ChatStatus.Info,
                    Actor = actor,
                    Context = projectKey,
                    Fields = [new("Flag", S(env, "flag", "name") ?? key ?? ""), new("Environment", environment ?? "all"), new("Change", Humanize(kind))],
                });
                break;
            }
            case WebhookEventNames.ReleasePublished:
            {
                var r = env["release"];
                var repo = S(env, "repository", "slug");
                var tag = S(r, "tagName");
                var title = S(r, "title");
                var pre = r?["prerelease"]?.GetValue<bool>() == true;
                result.Add(new ChatMessage
                {
                    Event = ChatEvents.ReleasePublished,
                    Summary = $"{actor ?? "Someone"} published {(pre ? "pre-release" : "release")} {tag} of {repo}",
                    Title = string.IsNullOrWhiteSpace(title) || title == tag ? $"{repo} {tag}" : $"{repo} {tag}: {title}",
                    Url = S(r, "webUrl"),
                    Status = ChatStatus.Good,
                    Actor = actor,
                    Context = Join(projectKey, repo),
                    Body = ChatText.Excerpt(S(r, "notes"), 800),
                    Fields = [new("Tag", tag ?? ""), new("Type", pre ? "Pre-release" : "Release")],
                });
                break;
            }
        }
        return result;
    }

    // ----- Cards for slash commands and unfurls -----

    /// <summary>A summary card of an issue (needs what <see cref="Api.Dto.Issue"/> needs loaded).</summary>
    public static ChatMessage IssueCard(Issue issue, string baseUrl, string summary) =>
        Issue(System.Text.Json.JsonSerializer.SerializeToNode(Api.Dto.Issue(issue, baseUrl), Api.Dto.Json), "issue", summary, null, issue.Project.Key,
            status: issue.Status == IssueStatus.Done ? ChatStatus.Good : ChatStatus.Info, body: ChatText.Excerpt(issue.Description, 400));

    /// <summary>A summary card of a pull request (needs what <see cref="Api.Dto.PullRequest"/> needs loaded).</summary>
    public static ChatMessage PullRequestCard(PullRequest pr, string baseUrl, string summary) =>
        PullRequest(System.Text.Json.JsonSerializer.SerializeToNode(Api.Dto.PullRequest(pr, baseUrl), Api.Dto.Json), "pull_request", summary, null,
            pr.Repository.Project.Key, pr.State switch { PullRequestState.Merged => ChatStatus.Good, PullRequestState.Declined => ChatStatus.Neutral, _ => ChatStatus.Info },
            ChatText.Excerpt(pr.Description, 400));

    // ----- Builders -----

    private static ChatMessage Issue(JsonNode? issue, string chatEvent, string summary, string? actor, string? projectKey,
        ChatStatus status = ChatStatus.Info, string? body = null)
    {
        var labels = (issue?["labels"] as JsonArray)?.Select(l => S(l, "name")).Where(n => n != null).Cast<string>().ToList() ?? [];
        var fields = new List<ChatField>
        {
            new("Status", StatusName(issue)),
            new("Priority", Humanize(S(issue, "priority"))),
            new("Type", Humanize(S(issue, "type"))),
            new("Assignee", Name(issue?["assignee"]) ?? "Unassigned"),
        };
        if (labels.Count > 0) fields.Add(new("Labels", string.Join(", ", labels)));
        return new ChatMessage
        {
            Event = chatEvent,
            Summary = summary,
            Title = $"{S(issue, "key")} {S(issue, "title")}",
            Url = S(issue, "webUrl"),
            Status = status,
            Actor = actor,
            Context = projectKey,
            Body = string.IsNullOrWhiteSpace(body) ? null : body,
            Fields = fields,
            IsIssue = true,
            Labels = labels,
            Priority = Enum.TryParse<IssuePriority>(S(issue, "priority"), ignoreCase: true, out var p) ? p : null,
        };
    }

    private static ChatMessage PullRequest(JsonNode? pr, string chatEvent, string summary, string? actor, string? projectKey,
        ChatStatus status = ChatStatus.Info, string? body = null)
    {
        var target = S(pr, "targetBranch") ?? "";
        return new ChatMessage
        {
            Event = chatEvent,
            Summary = summary,
            Title = $"{S(pr, "repository")} #{pr?["number"]?.GetValue<int>()} {S(pr, "title")}",
            Url = S(pr, "webUrl"),
            Status = status,
            Actor = actor,
            Context = Join(projectKey, S(pr, "repository")),
            Body = string.IsNullOrWhiteSpace(body) ? null : body,
            Fields =
            [
                new("Branches", $"{S(pr, "sourceBranch")} → {target}"),
                new("Author", Name(pr?["author"]) ?? ""),
                new("State", Humanize(S(pr, "state")) + (pr?["isDraft"]?.GetValue<bool>() == true ? " (draft)" : "")),
            ],
            Branch = target,
        };
    }

    /// <summary>Adds a <c>mention</c> message when <paramref name="text"/> @mentions existing users (other than the actor).</summary>
    private static async Task AddMentionAsync(AppDbContext db, List<ChatMessage> result, string? text, JsonObject env, JsonNode? issue,
        string? actor, string? projectKey, CancellationToken ct, string? prSummaryTarget = null, JsonNode? pr = null)
    {
        var names = Mentions.Extract(text);
        if (names.Count == 0) return;
        var lowered = names.Select(n => n.ToUpperInvariant()).ToList();
        var sender = S(env, "sender", "username");
        var users = await db.Users.AsNoTracking().Where(u => u.NormalizedUserName != null && lowered.Contains(u.NormalizedUserName))
            .Select(u => u.UserName!).ToListAsync(ct);
        users = users.Where(u => !string.Equals(u, sender, StringComparison.OrdinalIgnoreCase)).OrderBy(u => u, StringComparer.OrdinalIgnoreCase).ToList();
        if (users.Count == 0) return;
        var who = string.Join(", ", users.Select(u => "@" + u));
        pr ??= env["pullRequest"];
        ChatMessage m = issue != null
            ? Issue(issue, ChatEvents.Mention, $"{actor ?? "Someone"} mentioned {who} in {S(issue, "key")}", actor, projectKey, body: ChatText.Excerpt(text, 600))
            : PullRequest(pr, ChatEvents.Mention, $"{actor ?? "Someone"} mentioned {who} in {prSummaryTarget ?? PrRef(pr)}", actor, projectKey, body: ChatText.Excerpt(text, 600));
        m.Fields.Insert(0, new("Mentioned", who));
        result.Add(m);
    }

    /// <summary>True if the last finished pipeline before <paramref name="pipelineId"/> on the same repository and ref failed.</summary>
    private static async Task<bool> PreviousFailedAsync(AppDbContext db, int repositoryId, string @ref, int pipelineId, CancellationToken ct)
    {
        if (repositoryId == 0 || pipelineId == 0) return false;
        var previous = await db.Pipelines.AsNoTracking()
            .Where(p => p.RepositoryId == repositoryId && p.Ref == @ref && p.Id < pipelineId
                && (p.Status == PipelineStatus.Passed || p.Status == PipelineStatus.Failed || p.Status == PipelineStatus.Error))
            .OrderByDescending(p => p.Id).Select(p => (PipelineStatus?)p.Status).FirstOrDefaultAsync(ct);
        return previous is PipelineStatus.Failed or PipelineStatus.Error;
    }

    // ----- JSON helpers -----

    private static string? S(JsonNode? node, params string[] path)
    {
        foreach (var p in path) node = node?[p];
        return node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    /// <summary>Display name (falling back to username) of a user object.</summary>
    private static string? Name(JsonNode? user) =>
        user == null ? null : S(user, "displayName") is { Length: > 0 } d ? d : S(user, "username");

    private static string StatusName(JsonNode? issue) => S(issue, "workflowStatus", "name") ?? Humanize(S(issue, "status"));

    private static string PrRef(JsonNode? pr) => $"{S(pr, "repository")} #{pr?["number"]?.GetValue<int>()}";

    private static string? Join(params string?[] parts) =>
        parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() is { Count: > 0 } l ? string.Join(" · ", l) : null;

    /// <summary>"inProgress" → "In progress", "failed" → "Failed".</summary>
    public static string Humanize(string? camel)
    {
        if (string.IsNullOrEmpty(camel)) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var c in camel)
        {
            if (char.IsUpper(c) && sb.Length > 0) sb.Append(' ').Append(char.ToLowerInvariant(c));
            else if (c == '_') sb.Append(' ');
            else sb.Append(sb.Length == 0 ? char.ToUpperInvariant(c) : c);
        }
        return sb.ToString();
    }
}
