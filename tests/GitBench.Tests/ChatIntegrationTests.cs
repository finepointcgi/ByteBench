using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using GitBench.Data;
using GitBench.Services;
using GitBench.Services.ChatIntegrations;
using GitBench.Services.Webhooks;
using GitBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GitBench.Tests;

/// <summary>Records every request the chat HttpClient makes; nothing ever leaves the process.</summary>
public sealed class FakeChatHandler
{
    public sealed record Sent(string Url, string? Authorization, string Body);

    private readonly ConcurrentQueue<Sent> sent = new();
    private readonly SemaphoreSlim signal = new(0);

    /// <summary>Answer for a request (default: Slack's "ok").</summary>
    public Func<Sent, HttpResponseMessage> Respond { get; set; } = s => s.Url.StartsWith("https://slack.com/api/", StringComparison.Ordinal)
        ? Json("{\"ok\":true}") : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public IReadOnlyList<Sent> All => sent.ToList();

    internal async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
        var s = new Sent(request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body);
        sent.Enqueue(s);
        signal.Release();
        return Respond(s);
    }

    public async Task<Sent> WaitForAsync(Func<Sent, bool> match, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (true)
        {
            if (sent.FirstOrDefault(match) is { } hit) return hit;
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !await signal.WaitAsync(left)) throw new TimeoutException("The expected chat request wasn't sent.");
        }
    }

    /// <summary>A fresh primary handler per factory rotation, all feeding this recorder.</summary>
    public HttpMessageHandler CreateHandler() => new Forwarder(this);

    private sealed class Forwarder(FakeChatHandler owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => owner.HandleAsync(request, ct);
    }
}

/// <summary>The app with a Slack app configured (test-only values) and the chat HttpClient faked.</summary>
public class ChatApp : TestApp
{
    public const string SigningSecret = "test-signing-secret-not-real-0123456789";
    public const string BotToken = "xoxb-test-bot-token-not-real";
    public const string TeamId = "T0TEST";

    public FakeChatHandler Fake { get; } = new();

    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["Slack:SigningSecret"] = SigningSecret;
        settings["Slack:BotToken"] = BotToken;
        settings["Slack:TeamId"] = TeamId;
        settings["PublicBaseUrl"] = "http://localhost";
    }

    protected override void ConfigureServices(IServiceCollection services) =>
        services.AddHttpClient(ChatHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => Fake.CreateHandler());
}

public class ChatIntegrationTests(ChatApp app) : IClassFixture<ChatApp>
{
    private static int counter;
    private static string Unique() => Interlocked.Increment(ref counter).ToString("D4") + Guid.NewGuid().ToString("N")[..6];

    // =====================================================================
    // Formatting
    // =====================================================================

    private static ChatMessage Sample(string? title = null, string? body = null, string? url = "http://localhost/p/WEB/issues/1") => new()
    {
        Event = ChatEvents.IssueCreated,
        Summary = "alice created WEB-1",
        Title = title ?? "WEB-1 Login button",
        Url = url,
        Status = ChatStatus.Danger,
        Actor = "Alice",
        Context = "WEB",
        Body = body,
        Fields = [new("Status", "To do"), new("Priority", "High")],
    };

    [Fact]
    public void Slack_message_uses_block_kit_in_a_coloured_attachment()
    {
        var msg = SlackFormat.Message(Sample(body: "Steps to reproduce"));
        Assert.Equal("alice created WEB-1", msg["text"]!.GetValue<string>());
        Assert.False(msg["unfurl_links"]!.GetValue<bool>());
        Assert.Null(msg["channel"]);
        var attachment = msg["attachments"]![0]!;
        Assert.Equal(SlackFormat.Color(ChatStatus.Danger), attachment["color"]!.GetValue<string>());
        var blocks = attachment["blocks"]!.AsArray();
        Assert.Equal("*<http://localhost/p/WEB/issues/1|WEB-1 Login button>*", blocks[0]!["text"]!["text"]!.GetValue<string>());
        Assert.Equal("*Status*\nTo do", blocks[1]!["fields"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("Steps to reproduce", blocks[2]!["text"]!["text"]!.GetValue<string>());
        Assert.Equal("context", blocks[3]!["type"]!.GetValue<string>());
        Assert.Equal("Alice · WEB", blocks[3]!["elements"]![0]!["text"]!.GetValue<string>());

        // Bot mode names the channel.
        Assert.Equal("#dev", SlackFormat.Message(Sample(), "#dev")["channel"]!.GetValue<string>());
    }

    [Fact]
    public void Slack_escapes_user_content_and_link_targets()
    {
        var msg = SlackFormat.Message(Sample(title: "<!channel> & <@U123> <http://evil|click>", body: "<script>alert(1)</script> & co",
            url: "http://localhost/p/WEB/issues/1?a=<b>|c"));
        var json = msg.ToJsonString();
        Assert.DoesNotContain("<!channel>", json);
        Assert.DoesNotContain("<@U123>", json);
        var title = msg["attachments"]![0]!["blocks"]![0]!["text"]!["text"]!.GetValue<string>();
        Assert.Equal("*<http://localhost/p/WEB/issues/1?a=%3Cb%3E%7Cc|&lt;!channel&gt; &amp; &lt;@U123&gt; &lt;http://evil|click&gt;>*", title);
        Assert.Equal("&lt;script&gt;alert(1)&lt;/script&gt; &amp; co", msg["attachments"]![0]!["blocks"]![2]!["text"]!["text"]!.GetValue<string>());

        // A non-http link target is never linked.
        Assert.Equal("x", SlackFormat.Link("javascript:alert(1)", "x"));
    }

    [Fact]
    public void Slack_truncates_without_splitting_entities_or_surrogates()
    {
        var text = SlackFormat.Text(new string('&', 5000), SlackFormat.MaxBody);
        Assert.True(text.Length <= SlackFormat.MaxBody);
        Assert.EndsWith("&amp;…", text);
        var emoji = SlackFormat.Text(string.Concat(Enumerable.Repeat("😀", 2000)), 101);
        Assert.True(emoji.Length <= 101);
        Assert.False(char.IsHighSurrogate(emoji[^2]));

        var msg = SlackFormat.Message(Sample(title: new string('t', 1000), body: new string('<', 10_000)));
        foreach (var block in msg["attachments"]![0]!["blocks"]!.AsArray())
            if (block!["text"]?["text"]?.GetValue<string>() is { } t) Assert.True(t.Length <= SlackFormat.MaxSectionText);
        var many = Sample();
        for (var i = 0; i < 20; i++) many.Fields.Add(new($"F{i}", new string('x', 5000)));
        var fields = SlackFormat.Attachment(many)["blocks"]![1]!["fields"]!.AsArray();
        Assert.Equal(SlackFormat.MaxFields, fields.Count);
        Assert.All(fields, f => Assert.True(f!["text"]!.GetValue<string>().Length <= SlackFormat.MaxFieldText));
    }

    [Fact]
    public void Teams_message_is_an_adaptive_card_with_plain_text_runs()
    {
        var msg = TeamsFormat.Message(Sample(title: "**bold** [link](http://evil) <b>x</b>", body: "line1\nline2"));
        Assert.Equal("message", msg["type"]!.GetValue<string>());
        var attachment = msg["attachments"]![0]!;
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment["contentType"]!.GetValue<string>());
        var card = attachment["content"]!;
        Assert.Equal("AdaptiveCard", card["type"]!.GetValue<string>());
        Assert.Equal("1.4", card["version"]!.GetValue<string>());
        var body = card["body"]!.AsArray();
        Assert.Equal("attention", body[0]!["style"]!.GetValue<string>());
        Assert.Equal("alice created WEB-1", body[0]!["items"]![0]!["inlines"]![0]!["text"]!.GetValue<string>());
        // User content is a TextRun (never Markdown): kept verbatim, not escaped or interpreted.
        var title = body[1]!["inlines"]![0]!;
        Assert.Equal("TextRun", title["type"]!.GetValue<string>());
        Assert.Equal("**bold** [link](http://evil) <b>x</b>", title["text"]!.GetValue<string>());
        Assert.Equal("http://localhost/p/WEB/issues/1", title["selectAction"]!["url"]!.GetValue<string>());
        Assert.DoesNotContain("\"TextBlock\"", msg.ToJsonString());
        Assert.DoesNotContain("\"FactSet\"", msg.ToJsonString());
        Assert.Equal("Status", body[2]!["columns"]![0]!["items"]![0]!["inlines"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("Action.OpenUrl", card["actions"]![0]!["type"]!.GetValue<string>());

        var unsafeUrl = TeamsFormat.Message(Sample(url: "javascript:alert(1)", body: new string('b', 50_000)));
        Assert.Null(unsafeUrl["attachments"]![0]!["content"]!["actions"]);
        Assert.DoesNotContain("javascript:", unsafeUrl.ToJsonString());
        Assert.True(unsafeUrl.ToJsonString().Length < 10_000);
    }

    [Fact]
    public void Filters_match_branches_labels_and_priority()
    {
        Assert.True(ChatFilters.BranchMatches("release/*", "release/1.2"));
        Assert.False(ChatFilters.BranchMatches("release/*", "main"));
        Assert.True(ChatFilters.BranchMatches("main", "main"));
        Assert.False(ChatFilters.BranchMatches("mai", "main"));

        var f = new ChatFilters { Branches = ["main"], Labels = ["bug"], MinPriority = IssuePriority.High };
        Assert.True(f.Matches(new ChatMessage { Event = "x", Summary = "", Title = "", Branch = "main" }));
        Assert.False(f.Matches(new ChatMessage { Event = "x", Summary = "", Title = "", Branch = "dev" }));
        Assert.True(f.Matches(new ChatMessage { Event = "x", Summary = "", Title = "", IsIssue = true, Labels = ["BUG"], Priority = IssuePriority.Highest }));
        Assert.False(f.Matches(new ChatMessage { Event = "x", Summary = "", Title = "", IsIssue = true, Labels = ["ui"], Priority = IssuePriority.Highest }));
        Assert.False(f.Matches(new ChatMessage { Event = "x", Summary = "", Title = "", IsIssue = true, Labels = ["bug"], Priority = IssuePriority.Medium }));
    }

    // =====================================================================
    // Slack request signatures
    // =====================================================================

    private static string Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

    [Fact]
    public void Signature_verification_accepts_valid_and_rejects_tampered_expired_and_replayed()
    {
        var verifier = app.Services.GetRequiredService<SlackSignature>();
        var body = Encoding.UTF8.GetBytes("token=x&text=help&nonce=" + Unique());
        var ts = Now();
        var sig = SlackSignature.Compute(ChatApp.SigningSecret, ts, body);

        Assert.Equal(SlackSignatureResult.Invalid, verifier.Verify(ts, SlackSignature.Compute("wrong-secret", ts, body), body));
        Assert.Equal(SlackSignatureResult.Invalid, verifier.Verify(ts, sig, Encoding.UTF8.GetBytes("token=x&text=other")));
        Assert.Equal(SlackSignatureResult.Invalid, verifier.Verify(ts, sig[..^1] + (sig[^1] == '0' ? '1' : '0'), body));
        Assert.Equal(SlackSignatureResult.Missing, verifier.Verify(null, sig, body));
        Assert.Equal(SlackSignatureResult.Missing, verifier.Verify(ts, "", body));
        Assert.Equal(SlackSignatureResult.Invalid, verifier.Verify("12abc", sig, body));
        Assert.Equal(SlackSignatureResult.Valid, verifier.Verify(ts, sig, body));
        Assert.Equal(SlackSignatureResult.Replayed, verifier.Verify(ts, sig, body)); // same request again

        var old = DateTimeOffset.UtcNow.AddMinutes(-6).ToUnixTimeSeconds().ToString();
        Assert.Equal(SlackSignatureResult.Expired, verifier.Verify(old, SlackSignature.Compute(ChatApp.SigningSecret, old, body), body));
        var future = DateTimeOffset.UtcNow.AddMinutes(6).ToUnixTimeSeconds().ToString();
        Assert.Equal(SlackSignatureResult.Expired, verifier.Verify(future, SlackSignature.Compute(ChatApp.SigningSecret, future, body), body));
    }

    private async Task<HttpResponseMessage> PostSlackAsync(string path, string body, string? contentType = null, string? timestamp = null, string? secret = null)
    {
        var client = app.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        var ts = timestamp ?? Now();
        var bytes = Encoding.UTF8.GetBytes(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new(contentType ?? "application/x-www-form-urlencoded");
        request.Headers.Add(SlackSignature.TimestampHeader, ts);
        request.Headers.Add(SlackSignature.SignatureHeader, SlackSignature.Compute(secret ?? ChatApp.SigningSecret, ts, bytes));
        return await client.SendAsync(request);
    }

    private static string Form(params (string Key, string Value)[] fields) =>
        string.Join("&", fields.Select(f => $"{Uri.EscapeDataString(f.Key)}={Uri.EscapeDataString(f.Value)}"));

    private Task<HttpResponseMessage> CommandAsync(string slackUser, string text, string team = ChatApp.TeamId) =>
        PostSlackAsync("/integrations/slack/commands", Form(("team_id", team), ("user_id", slackUser), ("user_name", "slacker"),
            ("command", "/gitbench"), ("text", text), ("response_url", "https://hooks.slack.com/commands/T0TEST/1/abc"), ("nonce", Unique())));

    private static async Task<string> ReplyTextAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Endpoints_reject_unsigned_bad_and_expired_requests()
    {
        var body = Form(("team_id", ChatApp.TeamId), ("user_id", "U" + Unique()), ("text", "help"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostSlackAsync("/integrations/slack/commands", body, secret: "not-the-secret")).StatusCode);
        var old = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds().ToString();
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostSlackAsync("/integrations/slack/commands", body, timestamp: old)).StatusCode);
        var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/integrations/slack/commands", new StringContent(body))).StatusCode);

        // A correctly signed request works once; the identical request again is a replay.
        var ts = Now();
        var ok = await PostSlackAsync("/integrations/slack/commands", body, timestamp: ts);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("GitBench commands", await ok.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostSlackAsync("/integrations/slack/commands", body, timestamp: ts)).StatusCode);

        // Events: the url_verification handshake is answered only when signed.
        var challenge = "{\"type\":\"url_verification\",\"challenge\":\"abc123\"}";
        var verified = await PostSlackAsync("/integrations/slack/events", challenge, "application/json");
        Assert.Equal("abc123", JsonNode.Parse(await verified.Content.ReadAsStringAsync())!["challenge"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostSlackAsync("/integrations/slack/events", challenge, "application/json", secret: "nope")).StatusCode);
    }

    // =====================================================================
    // Account linking and slash commands
    // =====================================================================

    /// <summary>Links a fresh Slack user to <paramref name="user"/> through the real token flow; returns the Slack user id.</summary>
    private async Task<string> LinkAsync(string user)
    {
        var slackUser = "U" + Unique();
        var reply = await ReplyTextAsync(await CommandAsync(slackUser, "issue WEB-1"));
        var url = JsonNode.Parse(reply)!["blocks"]![1]!["elements"]![0]!["url"]!.GetValue<string>();
        var token = Uri.UnescapeDataString(url[(url.IndexOf("t=", StringComparison.Ordinal) + 2)..]);
        await using var s = app.Scope(app.UserId(user));
        await s.Get<SlackAccountLinks>().LinkAsync(token, app.UserId(user));
        return slackUser;
    }

    [Fact]
    public async Task Unlinked_users_get_a_one_time_signed_link_before_anything_else()
    {
        var slackUser = "U" + Unique();
        var reply = JsonNode.Parse(await ReplyTextAsync(await CommandAsync(slackUser, "create WEB Should not be created")))!;
        Assert.Equal("ephemeral", reply["response_type"]!.GetValue<string>());
        var url = reply["blocks"]![1]!["elements"]![0]!["url"]!.GetValue<string>();
        Assert.StartsWith("http://localhost/integrations/slack/link?t=", url);
        var token = Uri.UnescapeDataString(url[(url.IndexOf("t=", StringComparison.Ordinal) + 2)..]);

        await using var s = app.Scope(app.UserId("bob"));
        var links = s.Get<SlackAccountLinks>();
        var request = links.Read(token)!;
        Assert.Equal(ChatApp.TeamId, request.TeamId);
        Assert.Equal(slackUser, request.SlackUserId);
        Assert.True(request.Expires <= DateTimeOffset.UtcNow + SlackAccountLinks.TokenLifetime);
        Assert.Null(links.Read(token[..^2] + "xx")); // tampered
        Assert.Null(links.Read("not-a-token"));

        await links.LinkAsync(token, app.UserId("bob"));
        Assert.Equal(app.UserId("bob"), await links.FindUserAsync(ChatApp.TeamId, slackUser));
        var reuse = await Assert.ThrowsAsync<InvalidOperationException>(() => links.LinkAsync(token, app.UserId("dan")));
        Assert.Contains("already been used", reuse.Message);

        // Another workspace isn't served at all.
        Assert.Contains("isn't connected", await ReplyTextAsync(await CommandAsync(slackUser, "help", team: "TOTHER")));

        // Unlink, then the old token still can't be replayed.
        Assert.Contains("no longer linked", await ReplyTextAsync(await CommandAsync(slackUser, "unlink")));
        Assert.Null(await links.FindUserAsync(ChatApp.TeamId, slackUser));
        await Assert.ThrowsAsync<InvalidOperationException>(() => links.LinkAsync(token, app.UserId("bob")));
    }

    [Fact]
    public async Task Slash_commands_respect_project_access()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("dan", ProjectRole.Viewer));
        var issue = await app.CreateIssueAsync(project.Id, "carol", "Searchable <b>gizmo</b> " + Unique());
        var outsider = await app.CreateUserAsync("chatout" + Unique()[..6]);
        var bobSlack = await LinkAsync("bob");
        var danSlack = await LinkAsync("dan");
        var outsiderSlack = await LinkAsync(outsider.UserName!);

        // issue: visible to members only.
        var shown = await ReplyTextAsync(await CommandAsync(bobSlack, $"issue {issue.Key}"));
        Assert.Contains("&lt;b&gt;gizmo&lt;/b&gt;", shown); // escaped
        Assert.Contains(SlackCommandService.AssignToMeAction, shown);
        var hidden = await ReplyTextAsync(await CommandAsync(outsiderSlack, $"issue {issue.Key}"));
        Assert.Contains("doesn't exist, or you don't have access", hidden);
        Assert.DoesNotContain("gizmo", hidden);
        Assert.DoesNotContain(SlackCommandService.AssignToMeAction, await ReplyTextAsync(await CommandAsync(danSlack, $"issue {issue.Key}")));

        // search: only what the user can see.
        Assert.Contains(issue.Key, await ReplyTextAsync(await CommandAsync(bobSlack, "search gizmo")));
        Assert.DoesNotContain(issue.Key, await ReplyTextAsync(await CommandAsync(outsiderSlack, "search gizmo")));

        // create: Developers can, Viewers and outsiders can't.
        Assert.Contains("permission", await ReplyTextAsync(await CommandAsync(danSlack, $"create {project.Key} From a viewer")));
        Assert.Contains("doesn't exist, or you don't have access", await ReplyTextAsync(await CommandAsync(outsiderSlack, $"create {project.Key} From outside")));
        Assert.Contains("Created", await ReplyTextAsync(await CommandAsync(bobSlack, $"create {project.Key} From Slack")));
        await using var db = app.Db();
        Assert.Equal(new[] { "From Slack" }, await db.Issues.Where(i => i.ProjectId == project.Id && i.Title.StartsWith("From")).Select(i => i.Title).ToListAsync());
        Assert.Equal(app.UserId("bob"), await db.Issues.Where(i => i.ProjectId == project.Id && i.Title == "From Slack").Select(i => i.ReporterId).FirstAsync());
    }

    [Fact]
    public async Task Assign_to_me_button_needs_a_link_and_edit_permission()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("dan", ProjectRole.Viewer));
        var issue = await app.CreateIssueAsync(project.Id, "carol", "Click me");
        var danSlack = await LinkAsync("dan");
        var bobSlack = await LinkAsync("bob");

        async Task ClickAsync(string slackUser)
        {
            var payload = new JsonObject
            {
                ["type"] = "block_actions",
                ["team"] = new JsonObject { ["id"] = ChatApp.TeamId },
                ["user"] = new JsonObject { ["id"] = slackUser, ["username"] = "x" },
                ["response_url"] = "https://hooks.slack.com/actions/T0TEST/" + Unique(),
                ["actions"] = new JsonArray { new JsonObject { ["action_id"] = SlackCommandService.AssignToMeAction, ["value"] = issue.Id.ToString() } },
            };
            var response = await PostSlackAsync("/integrations/slack/interactivity", Form(("payload", payload.ToJsonString())));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await app.Fake.WaitForAsync(s => s.Url == payload["response_url"]!.GetValue<string>());
        }

        await ClickAsync(danSlack); // a Viewer
        await ClickAsync("U" + Unique()); // unlinked
        await using (var db = app.Db())
            Assert.Null(await db.Issues.Where(i => i.Id == issue.Id).Select(i => i.AssigneeId).FirstAsync());
        await ClickAsync(bobSlack);
        await using (var db = app.Db())
            Assert.Equal(app.UserId("bob"), await db.Issues.Where(i => i.Id == issue.Id).Select(i => i.AssigneeId).FirstAsync());
    }

    [Fact]
    public async Task Link_unfurls_show_only_what_the_sharing_user_can_see()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        var issue = await app.CreateIssueAsync(project.Id, "carol", "Unfurl me " + Unique());
        var url = $"http://localhost/p/{project.Key}/issues/{issue.Number}";
        var bobSlack = await LinkAsync("bob");
        var danSlack = await LinkAsync("dan");

        JsonObject Event(string user, string ts) => new()
        {
            ["type"] = "event_callback", ["team_id"] = ChatApp.TeamId,
            ["event"] = new JsonObject
            {
                ["type"] = "link_shared", ["user"] = user, ["channel"] = "C1", ["message_ts"] = ts,
                ["links"] = new JsonArray { new JsonObject { ["domain"] = "localhost", ["url"] = url }, new JsonObject { ["domain"] = "elsewhere.example", ["url"] = "https://elsewhere.example/x" } },
            },
        };

        var ts1 = "1700000000." + Unique();
        Assert.Equal(HttpStatusCode.OK, (await PostSlackAsync("/integrations/slack/events", Event(bobSlack, ts1).ToJsonString(), "application/json")).StatusCode);
        var unfurl = await app.Fake.WaitForAsync(s => s.Url.EndsWith("/chat.unfurl") && s.Body.Contains(ts1));
        Assert.Equal("Bearer " + ChatApp.BotToken, unfurl.Authorization);
        var body = JsonNode.Parse(unfurl.Body)!;
        Assert.Contains(issue.Key, body["unfurls"]![url]!.ToJsonString());
        Assert.Null(body["unfurls"]!["https://elsewhere.example/x"]);

        // dan isn't a member: nothing is unfurled for him.
        var ts2 = "1700000001." + Unique();
        await PostSlackAsync("/integrations/slack/events", Event(danSlack, ts2).ToJsonString(), "application/json");
        await Task.Delay(300);
        Assert.DoesNotContain(app.Fake.All, s => s.Body.Contains(ts2));

        // An unlinked user is asked to link instead.
        var ts3 = "1700000002." + Unique();
        await PostSlackAsync("/integrations/slack/events", Event("U" + Unique(), ts3).ToJsonString(), "application/json");
        var prompt = JsonNode.Parse((await app.Fake.WaitForAsync(s => s.Body.Contains(ts3))).Body)!;
        Assert.StartsWith("http://localhost/integrations/slack/link?t=", prompt["user_auth_url"]!.GetValue<string>());
        Assert.Empty(prompt["unfurls"]!.AsObject());
    }

    // =====================================================================
    // Channel integrations: settings, delivery, secrets
    // =====================================================================

    private static async Task<WebhookDelivery> WaitForDeliveryAsync(ChatApp app, int hookId, string chatEvent)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            await using (var db = app.Db())
                if (await db.WebhookDeliveries.AsNoTracking().FirstOrDefaultAsync(d => d.WebhookId == hookId && d.Event == chatEvent && d.Success != null) is { } d)
                    return d;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"No completed {chatEvent} delivery.");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Slack_webhook_integration_posts_block_kit_through_the_webhook_pipeline()
    {
        var project = await app.CreateProjectAsync("carol");
        var hookUrl = $"https://hooks.slack.com/services/T0TEST/B{Unique()}/secretpart{Unique()}";
        ChatIntegrationInfo info;
        await using (var s = app.Scope(app.UserId("carol")))
            info = await s.Get<ChatIntegrationService>().CreateAsync(project.Id, new ChatIntegrationInput
            {
                Kind = WebhookKind.Slack, Mode = ChatMode.SlackWebhook, Url = hookUrl,
                Events = [ChatEvents.IssueCreated, ChatEvents.IssueTransitioned, ChatEvents.Mention],
            });

        // Never shown back; stored encrypted.
        Assert.DoesNotContain("secretpart", info.UrlHint);
        await using (var db = app.Db())
        {
            var row = await db.Webhooks.AsNoTracking().FirstAsync(h => h.Id == info.Id);
            Assert.DoesNotContain("secretpart", row.Secret);
            Assert.DoesNotContain("secretpart", row.Url);
            Assert.StartsWith(WebhookSecrets.Prefix, row.Secret);
            Assert.Equal("https://hooks.slack.com", row.Url);
            Assert.Equal("issue.commented,issue.created,issue.updated,pull_request.commented,pull_request.opened", row.Events);
        }
        await using (var s = app.Scope(app.UserId("carol")))
        {
            Assert.DoesNotContain("secretpart", System.Text.Json.JsonSerializer.Serialize(await s.Get<ChatIntegrationService>().ListAsync(project.Id)));
            Assert.Empty(await s.Get<WebhookService>().ListAsync(project.Id)); // not a generic webhook
        }

        var issue = await app.CreateIssueAsync(project.Id, "carol", "Chat <!here> me");
        var sent = await app.Fake.WaitForAsync(x => x.Url == hookUrl && x.Body.Contains(issue.Key));
        Assert.Null(sent.Authorization);
        var msg = JsonNode.Parse(sent.Body)!;
        Assert.Equal($"Carol Okafor created {issue.Key}", msg["text"]!.GetValue<string>());
        Assert.Contains("Chat &lt;!here&gt; me", msg["attachments"]![0]!["blocks"]![0]!["text"]!["text"]!.GetValue<string>());
        var delivery = await WaitForDeliveryAsync(app, info.Id, ChatEvents.IssueCreated);
        Assert.True(delivery.Success);
        Assert.Equal(200, delivery.StatusCode);
        Assert.Equal(sent.Body, delivery.Payload);

        // Status change → issue.transitioned; a comment @mentioning someone → mention (comments themselves aren't subscribed).
        await using (var s = app.Scope(app.UserId("carol")))
        {
            await s.Get<IssueService>().SetStatusAsync(issue.Id, IssueStatus.Done, app.UserId("carol"));
            await s.Get<IssueService>().AddCommentAsync(issue.Id, app.UserId("carol"), "ping @bob please");
        }
        var transitioned = await WaitForDeliveryAsync(app, info.Id, ChatEvents.IssueTransitioned);
        Assert.Contains("to Done", transitioned.Payload);
        Assert.Contains(SlackFormat.Color(ChatStatus.Good), transitioned.Payload);
        var mention = await WaitForDeliveryAsync(app, info.Id, ChatEvents.Mention);
        Assert.Contains("mentioned @bob", mention.Payload);
        await using (var db = app.Db())
            Assert.False(await db.WebhookDeliveries.AnyAsync(d => d.WebhookId == info.Id && d.Event == ChatEvents.IssueCommented));
    }

    [Fact]
    public async Task Failed_deliveries_are_logged_without_secrets()
    {
        var project = await app.CreateProjectAsync("carol");
        var hookUrl = $"https://hooks.slack.com/services/T0TEST/B{Unique()}/leaky{Unique()}";
        app.Fake.Respond = s => s.Url == hookUrl
            ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("invalid_payload for " + hookUrl) }
            : FakeChatHandler.Json("{\"ok\":true}");
        try
        {
            int id;
            await using (var s = app.Scope(app.UserId("carol")))
            {
                var svc = s.Get<ChatIntegrationService>();
                id = (await svc.CreateAsync(project.Id, new ChatIntegrationInput { Kind = WebhookKind.Slack, Url = hookUrl, Events = [ChatEvents.IssueCreated] })).Id;
                await svc.SendTestAsync(project.Id, id);
            }
            var d = await WaitForDeliveryAsync(app, id, ChatEvents.Test);
            Assert.False(d.Success);
            Assert.Equal(400, d.StatusCode);
            Assert.Equal(1, d.Attempts); // 4xx: not retried
            Assert.DoesNotContain("leaky", d.Error);
            Assert.DoesNotContain("leaky", d.ResponseBody);
            Assert.Contains("[redacted]", d.ResponseBody);
            Assert.DoesNotContain("leaky", d.Payload);
            await using var db = app.Db();
            Assert.Equal(1, await db.Webhooks.Where(h => h.Id == id).Select(h => h.ConsecutiveFailures).FirstAsync());
            Assert.DoesNotContain(await db.AuditEvents.Where(a => a.ProjectId == project.Id).Select(a => a.Details ?? "").ToListAsync(), x => x.Contains("leaky"));
        }
        finally { app.Fake.Respond = new FakeChatHandler().Respond; }
    }

    [Fact]
    public async Task Bot_mode_posts_with_the_token_to_the_group_channel()
    {
        var project = await app.CreateProjectAsync("carol");
        var token = "xoxb-project-" + Unique();
        int id;
        await using (var s = app.Scope(app.UserId("carol")))
        {
            var svc = s.Get<ChatIntegrationService>();
            id = (await svc.CreateAsync(project.Id, new ChatIntegrationInput
            {
                Kind = WebhookKind.Slack, Mode = ChatMode.SlackBot, Token = token, Channel = "#general",
                GroupChannels = { [ChatEventGroup.Issues] = "#issues" }, Events = [ChatEvents.IssueCreated],
            })).Id;
            var info = (await svc.ListAsync(project.Id)).Single();
            Assert.DoesNotContain(token, info.TokenHint);
            Assert.Equal(ChatIntegrationService.Hint(token), info.TokenHint);
            // Editing with a blank token keeps it.
            await svc.UpdateAsync(project.Id, id, new ChatIntegrationInput
            {
                Kind = WebhookKind.Slack, Mode = ChatMode.SlackBot, Channel = "#general",
                GroupChannels = { [ChatEventGroup.Issues] = "#issues" }, Events = [ChatEvents.IssueCreated],
            });
        }
        var issue = await app.CreateIssueAsync(project.Id, "carol", "Bot post");
        var sent = await app.Fake.WaitForAsync(x => x.Url == "https://slack.com/api/chat.postMessage" && x.Body.Contains(issue.Key));
        Assert.Equal("Bearer " + token, sent.Authorization);
        Assert.Equal("#issues", JsonNode.Parse(sent.Body)!["channel"]!.GetValue<string>());
        Assert.True((await WaitForDeliveryAsync(app, id, ChatEvents.IssueCreated)).Success);

        // Slack API errors come back as 200 + ok:false.
        app.Fake.Respond = x => FakeChatHandler.Json("{\"ok\":false,\"error\":\"channel_not_found\"}");
        try
        {
            await using (var s = app.Scope(app.UserId("carol"))) await s.Get<ChatIntegrationService>().SendTestAsync(project.Id, id);
            var d = await WaitForDeliveryAsync(app, id, ChatEvents.Test);
            Assert.False(d.Success);
            Assert.Equal("Slack: channel_not_found", d.Error);
            Assert.Equal("#general", JsonNode.Parse(d.Payload)!["channel"]!.GetValue<string>());
        }
        finally { app.Fake.Respond = new FakeChatHandler().Respond; }
    }

    [Fact]
    public async Task Teams_integration_posts_adaptive_cards_and_applies_filters()
    {
        var project = await app.CreateProjectAsync("carol");
        var hookUrl = $"https://contoso.webhook.office.com/webhookb2/{Unique()}";
        int id;
        await using (var s = app.Scope(app.UserId("carol")))
        {
            id = (await s.Get<ChatIntegrationService>().CreateAsync(project.Id, new ChatIntegrationInput
            {
                Kind = WebhookKind.Teams, Url = hookUrl, Events = [ChatEvents.IssueCreated], MinPriority = IssuePriority.High,
            })).Id;
        }
        await using (var s = app.Scope(app.UserId("carol")))
        {
            await s.Get<IssueService>().CreateIssueAsync(project.Id, new Issue { Title = "Low priority", Priority = IssuePriority.Low }, [], app.UserId("carol"));
            await s.Get<IssueService>().CreateIssueAsync(project.Id, new Issue { Title = "Urgent one", Priority = IssuePriority.Highest }, [], app.UserId("carol"));
        }
        var sent = await app.Fake.WaitForAsync(x => x.Url == hookUrl);
        Assert.Contains("Urgent one", sent.Body);
        Assert.Equal("AdaptiveCard", JsonNode.Parse(sent.Body)!["attachments"]![0]!["content"]!["type"]!.GetValue<string>());
        await WaitForDeliveryAsync(app, id, ChatEvents.IssueCreated);
        await Task.Delay(300);
        Assert.DoesNotContain(app.Fake.All, x => x.Url == hookUrl && x.Body.Contains("Low priority"));
    }

    [Fact]
    public async Task Integrations_validate_urls_and_need_maintainers()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        await using (var s = app.Scope(app.UserId("bob")))
        {
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ChatIntegrationService>().ListAsync(project.Id));
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ChatIntegrationService>().CreateAsync(project.Id,
                new ChatIntegrationInput { Kind = WebhookKind.Slack, Url = "https://hooks.slack.com/services/a/b/c" }));
        }
        await using var c = app.Scope(app.UserId("carol"));
        var svc = c.Get<ChatIntegrationService>();
        foreach (var bad in new[] { "http://hooks.slack.com/services/a/b/c", "https://evil.example/hook", "https://user:pw@hooks.slack.com/x", "not a url" })
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(project.Id, new ChatIntegrationInput { Kind = WebhookKind.Slack, Url = bad }));
            Assert.DoesNotContain(bad, ex.Message);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(project.Id,
            new ChatIntegrationInput { Kind = WebhookKind.Teams, Url = "https://hooks.slack.com/services/a/b/c" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(project.Id,
            new ChatIntegrationInput { Kind = WebhookKind.Slack, Mode = ChatMode.SlackBot, Token = "not-a-token", Channel = "#x" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(project.Id,
            new ChatIntegrationInput { Kind = WebhookKind.Slack, Url = "https://hooks.slack.com/services/a/b/c", Events = [] }));
        Assert.Empty(await svc.ListAsync(project.Id));
    }

    [Fact]
    public async Task Pipeline_fixed_is_announced_only_after_a_failure()
    {
        var project = await app.CreateProjectAsync("carol");
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        var hookUrl = $"https://hooks.slack.com/services/T0TEST/B{Unique()}/p";
        int id;
        await using (var s = app.Scope(app.UserId("carol")))
            id = (await s.Get<ChatIntegrationService>().CreateAsync(project.Id, new ChatIntegrationInput
            {
                Kind = WebhookKind.Slack, Url = hookUrl, Events = [ChatEvents.PipelineFailed, ChatEvents.PipelineFixed], Branches = ["main"],
            })).Id;

        async Task<int> PipelineAsync(PipelineStatus status, string @ref = "main")
        {
            await using var db = app.Db();
            var p = new Pipeline { RepositoryId = repo.Id, Ref = @ref, CommitSha = new string('a', 40), Status = status, CreatedAt = DateTime.UtcNow, FinishedAt = DateTime.UtcNow };
            db.Pipelines.Add(p);
            await db.SaveChangesAsync();
            app.Services.GetRequiredService<WebhookDispatcher>().Enqueue(project.Id, WebhookEventNames.PipelineCompleted, new
            {
                pipeline = new { id = p.Id, status = status.ToString().ToLowerInvariant(), @ref, commitSha = p.CommitSha, trigger = "push", error = (string?)null, url = $"http://localhost/x/{p.Id}" },
                repository = new { id = repo.Id, slug = repo.Slug, name = repo.Name },
            });
            return p.Id;
        }

        await PipelineAsync(PipelineStatus.Passed); // green → green: nothing
        var failed = await PipelineAsync(PipelineStatus.Failed);
        await WaitForDeliveryAsync(app, id, ChatEvents.PipelineFailed);
        await PipelineAsync(PipelineStatus.Failed, "feature"); // filtered branch
        var fixedId = await PipelineAsync(PipelineStatus.Passed);
        var fixedDelivery = await WaitForDeliveryAsync(app, id, ChatEvents.PipelineFixed);
        Assert.Contains($"#{fixedId}", fixedDelivery.Payload);
        await using var db2 = app.Db();
        var events = await db2.WebhookDeliveries.Where(d => d.WebhookId == id).Select(d => d.Event).ToListAsync();
        Assert.Equal(new[] { ChatEvents.PipelineFailed, ChatEvents.PipelineFixed }, events.Order());
        Assert.Contains($"#{failed}", (await db2.WebhookDeliveries.FirstAsync(d => d.WebhookId == id && d.Event == ChatEvents.PipelineFailed)).Payload);
    }

    [Fact]
    public async Task Slack_dms_go_to_linked_users_who_opted_in()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("dan", ProjectRole.Developer));
        var danSlack = await LinkAsync("dan");
        await using (var s = app.Scope(app.UserId("dan")))
            await s.Get<NotificationPreferences>().SetSlackDmEnabledAsync(app.UserId("dan"), true);
        var issue = await app.CreateIssueAsync(project.Id, "carol", "DM me");
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<IssueService>().UpdateIssueAsync(issue.Id, app.UserId("carol"), (i, _) => { i.AssigneeId = app.UserId("dan"); return Task.FromResult<string?>("assigned to **dan**"); });
        var dm = await app.Fake.WaitForAsync(x => x.Url.EndsWith("/chat.postMessage") && x.Body.Contains($"\"channel\":\"{danSlack}\""));
        Assert.Equal("Bearer " + ChatApp.BotToken, dm.Authorization);
        Assert.Contains(issue.Key, dm.Body);
        var danId = app.UserId("dan");
        for (var i = 0; ; i++)
        {
            await using var db = app.Db();
            if (!await db.Notifications.AnyAsync(n => n.UserId == danId && n.ChatPending && n.ProjectId == project.Id)) break;
            Assert.True(i < 50, "The DM flag wasn't cleared.");
            await Task.Delay(100);
        }
    }
}
