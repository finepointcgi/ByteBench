using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Email;
using ByteBench.Services.ServiceDesk;
using ByteBench.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ByteBench.Tests;

/// <summary>Captures every email the app sends (instead of the pickup directory), for assertions.</summary>
public sealed class FakeEmailSender : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Sent { get; } = new();

    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        Sent.Enqueue(message);
        return Task.CompletedTask;
    }

    /// <summary>Waits (up to 10 s) for an email matching <paramref name="match"/>; the email worker sends in the background.</summary>
    public async Task<EmailMessage> WaitForAsync(Func<EmailMessage, bool> match)
    {
        for (var i = 0; i < 200; i++)
        {
            if (Sent.FirstOrDefault(match) is { } m) return m;
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException("Expected email was not sent. Sent: " + string.Join(" | ", Sent.Select(m => $"{m.To}: {m.Subject}")));
    }
}

/// <summary>The app with email on (captured by <see cref="FakeEmailSender"/>), a webhook secret and a low per-sender limit.</summary>
public sealed class ServiceDeskTestApp : TestApp
{
    public FakeEmailSender Mail { get; } = new();
    public string WebhookSecret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    public const int SenderLimit = 5;

    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["Email:Enabled"] = "true";
        settings["Email:From"] = "ByteBench <bytebench@desk.test>";
        settings["Email:BaseUrl"] = "http://desk.test";
        settings["ServiceDesk:IncomingAddress"] = "desk@desk.test";
        settings["ServiceDesk:WebhookSecret"] = WebhookSecret;
        settings["ServiceDesk:MaxMessagesPerSenderPerHour"] = SenderLimit.ToString();
        settings["ServiceDesk:MaxFormSubmissionsPerHour"] = "50";
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.RemoveAll<IEmailSender>();
        services.AddSingleton<IEmailSender>(Mail);
    }
}

public class ServiceDeskTests(ServiceDeskTestApp app) : IClassFixture<ServiceDeskTestApp>
{
    private static int counter;

    /// <summary>A unique sender per test, so the per-sender rate limit never couples tests.</summary>
    private static string Sender(string name = "jane") => $"{name}{Interlocked.Increment(ref counter)}@customer.test";

    private static string NewMessageId() => $"{Guid.NewGuid():N}@customer.test";

    private static string Fixture(string name, IDictionary<string, string> values)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ServiceDesk", name));
        foreach (var (k, v) in values) text = text.Replace("%" + k + "%", v);
        return text;
    }

    private async Task<InboundResult> DeliverAsync(string raw)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        return await app.Services.GetRequiredService<ServiceDeskIntake>().ProcessAsync(stream, "test");
    }

    private async Task<Project> DeskProjectAsync(Action<ServiceDeskSettings>? configure = null)
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("carol", ProjectRole.Viewer));
        await using var s = app.Scope(app.UserId("alice"));
        var desk = s.Get<ServiceDeskService>();
        var settings = await desk.GetSettingsAsync(project.Id);
        settings.Enabled = true;
        configure?.Invoke(settings);
        await desk.SaveSettingsAsync(settings);
        return project;
    }

    private static string To(Project p) => $"desk+{p.Key.ToLowerInvariant()}@desk.test";

    private async Task<(InboundResult Result, string Sender, string MessageId)> NewRequestAsync(Project p, string? sender = null)
    {
        sender ??= Sender();
        var id = NewMessageId();
        var r = await DeliverAsync(Fixture("new-request.eml", new Dictionary<string, string>
        {
            ["FROM"] = sender, ["FROMNAME"] = "Jane Customer", ["TO"] = To(p), ["MSGID"] = id,
        }));
        return (r, sender, id);
    }

    private ServiceDeskTicket Ticket(int issueId)
    {
        using var db = app.Db();
        return db.ServiceDeskTickets.AsNoTracking().Include(t => t.Contact).Include(t => t.Issue).ThenInclude(i => i.Reporter).First(t => t.IssueId == issueId);
    }

    // ----- New issue -----

    [Fact]
    public async Task New_email_opens_an_issue_as_the_service_desk_bot_with_the_requester_and_an_auto_reply()
    {
        var project = await DeskProjectAsync(s => { s.DefaultIssueType = IssueType.Bug; s.IssueTemplate = "Reported by {{requester}} ({{email}}):\n\n{{body}}"; });
        var (result, sender, messageId) = await NewRequestAsync(project);

        Assert.Equal(InboundOutcome.Created, result.Outcome);
        var t = Ticket(result.IssueId!.Value);
        Assert.Equal(ServiceDeskBot.UserName, t.Issue.Reporter.UserName);
        Assert.Equal(sender, t.Contact.Email);
        Assert.Equal("Jane Customer", t.Contact.Name);
        Assert.Equal(IssueType.Bug, t.Issue.Type);
        Assert.Equal("The printer on floor 3 is on fire", t.Issue.Title);
        Assert.StartsWith($"Reported by Jane Customer ({sender.Replace("@", "\\@")}):", t.Issue.Description);
        Assert.Contains("grinding noise", t.Issue.Description);
        Assert.Matches("^[0-9a-f]{32}$", t.ReplyToken);
        Assert.True(t.TrackingToken.Length >= 40);

        var reply = await app.Mail.WaitForAsync(m => m.To == sender);
        Assert.Contains($"[{project.Key}-", reply.Subject);
        Assert.Equal($"desk+r-{t.ReplyToken}@desk.test", reply.Headers["Reply-To"]);
        Assert.Equal("auto-replied", reply.Headers["Auto-Submitted"]);
        Assert.Equal($"<{messageId}>", reply.Headers["In-Reply-To"]);
        Assert.Contains("/desk/t/" + t.TrackingToken, reply.Text);
        Assert.StartsWith(ServiceDeskMail.ReplyMarker, reply.Text);

        await using var db = app.Db();
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "service_desk.request_created" && e.ProjectId == project.Id));
    }

    [Fact]
    public async Task Mail_for_a_project_without_the_service_desk_or_an_unknown_address_is_refused()
    {
        var off = await app.CreateProjectAsync("alice");
        var (r1, _, _) = await NewRequestAsync(off);
        Assert.Equal(InboundOutcome.Rejected, r1.Outcome);
        Assert.Equal("service desk disabled", r1.Reason);

        var r2 = await DeliverAsync(Fixture("new-request.eml", new Dictionary<string, string>
        {
            ["FROM"] = Sender(), ["FROMNAME"] = "X", ["TO"] = "someone-else@desk.test", ["MSGID"] = NewMessageId(),
        }));
        Assert.Equal("no matching project", r2.Reason);
    }

    [Fact]
    public async Task Custom_address_reaches_the_project()
    {
        var project = await DeskProjectAsync(s => s.CustomAddress = $"Support-{Guid.NewGuid():N}@Example.test");
        string custom;
        await using (var db = app.Db()) custom = (await db.ServiceDeskSettings.FirstAsync(s => s.ProjectId == project.Id)).CustomAddress!;
        Assert.Equal(custom.ToLowerInvariant(), custom);
        var r = await DeliverAsync(Fixture("new-request.eml", new Dictionary<string, string>
        {
            ["FROM"] = Sender(), ["FROMNAME"] = "X", ["TO"] = custom, ["MSGID"] = NewMessageId(),
        }));
        Assert.Equal(InboundOutcome.Created, r.Outcome);
    }

    // ----- Threading -----

    [Fact]
    public async Task Reply_to_the_token_address_becomes_a_comment_without_the_quoted_text()
    {
        var project = await DeskProjectAsync();
        var (created, sender, firstId) = await NewRequestAsync(project);
        var t = Ticket(created.IssueId!.Value);

        var r = await DeliverAsync(Fixture("reply.eml", new Dictionary<string, string>
        {
            ["FROM"] = sender, ["FROMNAME"] = "Jane Customer", ["TO"] = $"desk+r-{t.ReplyToken}@desk.test", ["MSGID"] = NewMessageId(),
            ["INREPLYTO"] = "unrelated@elsewhere.test", ["KEY"] = project.Key,
        }));

        Assert.Equal(InboundOutcome.Commented, r.Outcome);
        Assert.Equal(t.IssueId, r.IssueId);
        await using var db = app.Db();
        var comment = await db.Comments.SingleAsync(c => c.IssueId == t.IssueId);
        Assert.Equal("It is still smoking, but the noise has stopped.", comment.Body);
        Assert.Equal(t.ContactId, comment.ServiceDeskContactId);
        Assert.False(comment.IsPublicReply);
        Assert.Equal(1, await db.Issues.CountAsync(i => i.ProjectId == project.Id));
    }

    [Fact]
    public async Task Reply_threads_by_references_only_for_the_original_requester()
    {
        var project = await DeskProjectAsync();
        var (created, sender, firstId) = await NewRequestAsync(project);
        var issueId = created.IssueId!.Value;
        // The auto-reply's Message-ID is part of the thread too.
        var autoReply = await app.Mail.WaitForAsync(m => m.To == sender);
        var autoReplyId = autoReply.Headers["Message-Id"].Trim('<', '>');

        var same = await DeliverAsync(Fixture("reply.eml", new Dictionary<string, string>
        {
            ["FROM"] = sender, ["FROMNAME"] = "Jane", ["TO"] = To(project), ["MSGID"] = NewMessageId(), ["INREPLYTO"] = autoReplyId, ["KEY"] = project.Key,
        }));
        Assert.Equal(InboundOutcome.Commented, same.Outcome);
        Assert.Equal(issueId, same.IssueId);

        // Someone else quoting the thread's Message-ID doesn't get to comment on Jane's request: it's a new one.
        var other = await DeliverAsync(Fixture("reply.eml", new Dictionary<string, string>
        {
            ["FROM"] = Sender("mallory"), ["FROMNAME"] = "Mallory", ["TO"] = To(project), ["MSGID"] = NewMessageId(), ["INREPLYTO"] = firstId, ["KEY"] = project.Key,
        }));
        Assert.Equal(InboundOutcome.Created, other.Outcome);
        Assert.NotEqual(issueId, other.IssueId);
    }

    [Fact]
    public async Task Duplicate_message_id_is_processed_once()
    {
        var project = await DeskProjectAsync();
        var sender = Sender();
        var raw = Fixture("new-request.eml", new Dictionary<string, string> { ["FROM"] = sender, ["FROMNAME"] = "J", ["TO"] = To(project), ["MSGID"] = NewMessageId() });
        Assert.Equal(InboundOutcome.Created, (await DeliverAsync(raw)).Outcome);
        var again = await DeliverAsync(raw);
        Assert.Equal(InboundOutcome.Ignored, again.Outcome);
        Assert.Equal("duplicate", again.Reason);
    }

    // ----- Attachments and HTML -----

    [Fact]
    public async Task Html_mail_is_converted_and_attachments_are_stored_within_the_type_limits()
    {
        var project = await DeskProjectAsync();
        var r = await DeliverAsync(Fixture("html-with-attachments.eml", new Dictionary<string, string>
        {
            ["FROM"] = Sender("omar"), ["TO"] = To(project), ["MSGID"] = NewMessageId(),
        }));
        Assert.Equal(InboundOutcome.Created, r.Outcome);

        await using var db = app.Db();
        var issue = await db.Issues.FirstAsync(i => i.Id == r.IssueId);
        var attachments = await db.Attachments.Where(a => a.IssueId == r.IssueId).ToListAsync();
        var png = Assert.Single(attachments);
        Assert.Equal("screenshot.png", png.FileName);
        Assert.Equal("image/png", png.ContentType);
        Assert.Equal(ServiceDeskBot.UserName, (await db.Users.FindAsync(png.UploaderId))!.UserName);

        var d = issue.Description!;
        Assert.Contains("The **export** fails with _error 42_", d);
        Assert.Contains("[the status page](https://status.example.test/incidents/42)", d);
        Assert.Contains("- Step one", d);
        Assert.Contains("Thanks & regards", d);
        Assert.DoesNotContain("alert", d);
        Assert.DoesNotContain("javascript:", d);
        Assert.DoesNotContain("color: red", d);
        Assert.Contains("![screenshot.png](/attachments/", d);
        Assert.Contains("invoice.exe (file type not allowed)", d);
    }

    [Fact]
    public void Quoted_replies_and_signatures_are_stripped()
    {
        Assert.Equal("Thanks, that worked.", ServiceDeskMail.StripQuotedReply("Thanks, that worked.\n\n-----Original Message-----\nFrom: x\nold text"));
        Assert.Equal("Yes.", ServiceDeskMail.StripQuotedReply("Yes.\n\nFrom: Support <desk@x.test>\nSent: Monday\nTo: me\nSubject: Re: thing\n\nold"));
        Assert.Equal("Fine", ServiceDeskMail.StripQuotedReply("Fine\n-- \nBob\n555"));
        Assert.Equal("Top\n> inline quote\nanswer", ServiceDeskMail.StripQuotedReply("Top\n> inline quote\nanswer\n\n> trailing\n> quote"));
        Assert.Equal("ok", ServiceDeskMail.StripQuotedReply("ok\n\nOn Tue, 17 Nov 2026 at 10:00, Service Desk <desk@x.test>\nwrote:\n> hi"));
    }

    // ----- Loop protection and abuse limits -----

    [Fact]
    public async Task Auto_replies_bounces_and_our_own_mail_are_ignored()
    {
        var project = await DeskProjectAsync();
        var values = new Dictionary<string, string> { ["FROM"] = Sender(), ["TO"] = To(project), ["MSGID"] = NewMessageId(), ["KEY"] = project.Key };

        var auto = await DeliverAsync(Fixture("auto-reply.eml", values));
        Assert.Equal(InboundOutcome.Ignored, auto.Outcome);
        Assert.Equal("auto-submitted", auto.Reason);

        values["MSGID"] = NewMessageId();
        var bounce = await DeliverAsync(Fixture("bounce.eml", values));
        Assert.Equal(InboundOutcome.Ignored, bounce.Outcome);

        var own = await DeliverAsync(Fixture("new-request.eml", new Dictionary<string, string>
        {
            ["FROM"] = "desk+r-0123456789abcdef0123456789abcdef@desk.test", ["FROMNAME"] = "Service Desk", ["TO"] = To(project), ["MSGID"] = NewMessageId(),
        }));
        Assert.Equal("own address", own.Reason);

        var list = await DeliverAsync(Fixture("new-request.eml", new Dictionary<string, string>
        {
            ["FROM"] = Sender(), ["FROMNAME"] = "List", ["TO"] = To(project), ["MSGID"] = NewMessageId(),
        }).Replace("MIME-Version: 1.0", "Precedence: bulk\nMIME-Version: 1.0"));
        Assert.Equal("precedence bulk", list.Reason);

        await using var db = app.Db();
        Assert.Equal(0, await db.Issues.CountAsync(i => i.ProjectId == project.Id));
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "service_desk.mail_ignored" && e.Details!.Contains("auto-submitted")));
    }

    [Fact]
    public async Task Each_sender_is_rate_limited_and_spam_is_refused()
    {
        var project = await DeskProjectAsync();
        var sender = Sender("flood");
        for (var i = 0; i < ServiceDeskTestApp.SenderLimit; i++)
            Assert.Equal(InboundOutcome.Created, (await NewRequestAsync(project, sender)).Result.Outcome);
        var over = await NewRequestAsync(project, sender);
        Assert.Equal(InboundOutcome.Rejected, over.Result.Outcome);
        Assert.Equal("rate limited", over.Result.Reason);

        var spam = await DeliverAsync(Fixture("new-request.eml", new Dictionary<string, string>
        {
            ["FROM"] = Sender(), ["FROMNAME"] = "S", ["TO"] = To(project), ["MSGID"] = NewMessageId(),
        }).Replace("MIME-Version: 1.0", "X-Spam-Flag: YES\nMIME-Version: 1.0"));
        Assert.Equal("spam filter", spam.Reason);
    }

    // ----- Webhook -----

    private async Task<HttpResponseMessage> PostWebhookAsync(string raw, string? signature, long? timestamp = null)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var body = Encoding.UTF8.GetBytes(raw);
        var ts = (timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString();
        var req = new HttpRequestMessage(HttpMethod.Post, "/service-desk/inbound") { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new("message/rfc822");
        req.Headers.Add(WebhookMailSource.TimestampHeader, ts);
        if (signature != null) req.Headers.Add(WebhookMailSource.SignatureHeader, signature == "valid" ? WebhookMailSource.Sign(app.WebhookSecret, ts, body) : signature);
        return await client.SendAsync(req);
    }

    [Fact]
    public async Task Webhook_requires_a_valid_fresh_hmac_signature()
    {
        var project = await DeskProjectAsync();
        var raw = Fixture("new-request.eml", new Dictionary<string, string> { ["FROM"] = Sender(), ["FROMNAME"] = "W", ["TO"] = To(project), ["MSGID"] = NewMessageId() });

        var unsigned = await PostWebhookAsync(raw, null);
        Assert.True(unsigned.StatusCode == HttpStatusCode.Unauthorized, $"{unsigned.StatusCode}: {await unsigned.Content.ReadAsStringAsync()}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostWebhookAsync(raw, "sha256=" + new string('0', 64))).StatusCode);
        var stale = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var staleSig = WebhookMailSource.Sign(app.WebhookSecret, stale.ToString(), Encoding.UTF8.GetBytes(raw));
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostWebhookAsync(raw, staleSig, stale)).StatusCode);
        var otherKey = WebhookMailSource.Sign("not-the-secret", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), Encoding.UTF8.GetBytes(raw));
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostWebhookAsync(raw, otherKey)).StatusCode);
        await using (var db = app.Db()) Assert.Equal(0, await db.Issues.CountAsync(i => i.ProjectId == project.Id));

        var ok = await PostWebhookAsync(raw, "valid");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("\"outcome\":\"created\"", await ok.Content.ReadAsStringAsync());
        await using (var db = app.Db()) Assert.Equal(1, await db.Issues.CountAsync(i => i.ProjectId == project.Id));
    }

    [Fact]
    public async Task A_custom_inbound_source_is_polled_through_the_abstraction()
    {
        var project = await DeskProjectAsync();
        var source = new QueueMailSource();
        source.Messages.Enqueue(Fixture("new-request.eml", new Dictionary<string, string> { ["FROM"] = Sender(), ["FROMNAME"] = "P", ["TO"] = To(project), ["MSGID"] = NewMessageId() }));
        var poller = new InboundMailPoller([source], app.Services.GetRequiredService<ServiceDeskIntake>(),
            app.Services.GetRequiredService<IOptions<ServiceDeskOptions>>(), app.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<InboundMailPoller>>());
        Assert.Equal(1, await poller.PollOnceAsync(CancellationToken.None));
        await using var db = app.Db();
        Assert.Equal(1, await db.ServiceDeskTickets.CountAsync(t => t.ProjectId == project.Id));
    }

    private sealed class QueueMailSource : IInboundMailSource
    {
        public Queue<string> Messages { get; } = new();
        public string Name => "queue";
        public bool Enabled => true;

        public async Task<int> PollAsync(ServiceDeskIntake intake, CancellationToken ct)
        {
            var n = 0;
            while (Messages.TryDequeue(out var raw))
            {
                await intake.ProcessAsync(new MemoryStream(Encoding.UTF8.GetBytes(raw)), Name, ct);
                n++;
            }
            return n;
        }
    }

    // ----- Public replies vs internal comments -----

    [Fact]
    public async Task Only_replies_to_the_requester_are_emailed_and_shown_on_the_tracking_page()
    {
        var project = await DeskProjectAsync(s => s.AutoReplyEnabled = false);
        var (created, sender, firstId) = await NewRequestAsync(project);
        var issueId = created.IssueId!.Value;
        var bob = app.UserId("bob");

        await using (var s = app.Scope(bob))
        {
            await s.Get<IssueService>().AddCommentAsync(issueId, bob, "Internal: probably the fuser unit, don't tell them yet");
            var (comment, emailed) = await s.Get<ServiceDeskService>().ReplyToRequesterAsync(issueId, "We're sending a technician today.");
            Assert.True(emailed);
            Assert.True(comment.IsPublicReply);
        }

        var t = Ticket(issueId);
        var mail = await app.Mail.WaitForAsync(m => m.To == sender && m.Text.Contains("technician"));
        Assert.StartsWith("Re: [", mail.Subject);
        Assert.Equal($"<{firstId}>", mail.Headers["In-Reply-To"]);
        Assert.Equal($"desk+r-{t.ReplyToken}@desk.test", mail.Headers["Reply-To"]);
        Assert.False(mail.Headers.ContainsKey("Auto-Submitted")); // a person wrote this one
        Assert.DoesNotContain(app.Mail.Sent, m => m.Text.Contains("fuser unit") || m.Html.Contains("fuser unit"));
        Assert.DoesNotContain(app.Mail.Sent, m => m.To == sender && m.Headers.GetValueOrDefault("Auto-Submitted") == "auto-replied"); // auto-reply off

        Assert.NotNull(t.FirstResponseAt);
        Assert.False(t.WaitingOnUs);

        await using (var s = app.Scope())
        {
            var view = await s.Get<ServiceDeskService>().GetTrackingViewAsync(t.TrackingToken);
            Assert.NotNull(view);
            var entry = Assert.Single(view!.Entries);
            Assert.Contains("technician", entry.Body);
            Assert.Null(await s.Get<ServiceDeskService>().GetTrackingViewAsync("wrong-token"));
        }

        await using (var db = app.Db())
            Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "service_desk.reply_sent" && e.TargetId == issueId.ToString()));
    }

    [Fact]
    public async Task Moving_into_a_chosen_status_emails_the_requester()
    {
        var project = await DeskProjectAsync(s => s.AutoReplyEnabled = false); // defaults: Done statuses notify
        var (created, sender, _) = await NewRequestAsync(project);
        await using (var s = app.Scope(app.UserId("bob")))
        {
            await s.Get<IssueService>().SetStatusAsync(created.IssueId!.Value, IssueStatus.InProgress, app.UserId("bob")); // not chosen
            await s.Get<IssueService>().SetStatusAsync(created.IssueId!.Value, IssueStatus.Done, app.UserId("bob"));
        }
        var mail = await app.Mail.WaitForAsync(m => m.To == sender);
        Assert.Contains("is now **Done**", mail.Text);
        Assert.Single(app.Mail.Sent, m => m.To == sender);
    }

    [Fact]
    public async Task Queue_shows_requests_waiting_on_the_team()
    {
        var project = await DeskProjectAsync();
        var a = (await NewRequestAsync(project)).Result.IssueId!.Value;
        var b = (await NewRequestAsync(project)).Result.IssueId!.Value;
        await using var s = app.Scope(app.UserId("bob"));
        var desk = s.Get<ServiceDeskService>();
        await desk.ReplyToRequesterAsync(a, "Answered.");

        var waiting = await desk.GetQueueAsync(project.Id, ServiceDeskQueueFilter.WaitingOnUs);
        Assert.Equal([b], waiting.Select(r => r.IssueId));
        Assert.Equal(2, (await desk.GetQueueAsync(project.Id, ServiceDeskQueueFilter.Open)).Count);
        Assert.Equal(1, await desk.CountWaitingAsync(project.Id));
    }

    [Fact]
    public async Task Only_maintainers_configure_the_service_desk_and_viewers_cannot_reply()
    {
        var project = await DeskProjectAsync();
        var issueId = (await NewRequestAsync(project)).Result.IssueId!.Value;
        await using (var s = app.Scope(app.UserId("bob")))
        {
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ServiceDeskService>().GetSettingsAsync(project.Id));
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ServiceDeskService>().SaveSettingsAsync(new ServiceDeskSettings { ProjectId = project.Id, Enabled = false }));
        }
        await using (var s = app.Scope(app.UserId("carol")))
        {
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ServiceDeskService>().ReplyToRequesterAsync(issueId, "hi"));
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ServiceDeskService>().GetQueueAsync(project.Id, ServiceDeskQueueFilter.All));
            var info = await s.Get<ServiceDeskService>().GetIssueInfoAsync(issueId);
            Assert.NotNull(info);
            Assert.Equal("", info!.Ticket.Contact.Email); // requester addresses are for Developers and above
        }
    }

    [Fact]
    public async Task Api_lists_the_queue_and_replies_to_the_requester()
    {
        var project = await DeskProjectAsync(s => s.AutoReplyEnabled = false);
        var (created, sender, _) = await NewRequestAsync(project);
        var number = Ticket(created.IssueId!.Value).Issue.Number;

        var bob = await app.ApiClientAsync("bob");
        var list = await bob.GetStringAsync($"/api/v1/projects/{project.Key}/service-desk/requests");
        Assert.Contains(sender, list);
        Assert.Contains("\"waitingOnUs\":true", list);

        var reply = await bob.PostAsync($"/api/v1/projects/{project.Key}/issues/{number}/requester-replies",
            new StringContent("{\"body\":\"Sent from the API\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, reply.StatusCode);
        await app.Mail.WaitForAsync(m => m.To == sender && m.Text.Contains("Sent from the API"));
        Assert.DoesNotContain(sender, await bob.GetStringAsync($"/api/v1/projects/{project.Key}/service-desk/requests?filter=waitingOnUs"));

        var carol = await app.ApiClientAsync("carol"); // Viewer
        Assert.Equal(HttpStatusCode.Forbidden, (await carol.GetAsync($"/api/v1/projects/{project.Key}/service-desk/requests")).StatusCode);
    }

    // ----- Public form and tracking page (HTTP) -----

    private static string Token(string html) => Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;

    [Fact]
    public async Task Public_form_needs_the_antiforgery_token_and_returns_a_tracking_link()
    {
        var project = await DeskProjectAsync(s => s.AutoReplyEnabled = false);
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var page = await client.GetStringAsync($"/desk/{project.Key}");
        Assert.Contains("Contact " + project.Name, page);
        var token = Token(page);
        Assert.NotEmpty(token);
        var email = Sender("form");

        Dictionary<string, string> Form(bool withToken) => new()
        {
            ["_handler"] = "desk-request",
            ["Input.Name"] = "Fay Former", ["Input.Email"] = email, ["Input.Subject"] = "Cannot log in", ["Input.Body"] = "The page says **error**.",
            ["Input.Website"] = "",
            ["__RequestVerificationToken"] = withToken ? token : "",
        };

        var forged = await app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false })
            .PostAsync($"/desk/{project.Key}", new FormUrlEncodedContent(Form(false)));
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);

        var posted = await client.PostAsync($"/desk/{project.Key}", new FormUrlEncodedContent(Form(true)));
        Assert.True((int)posted.StatusCode is 302 or 303, $"Expected a redirect, got {(int)posted.StatusCode}");
        var location = posted.Headers.Location!.IsAbsoluteUri ? posted.Headers.Location.PathAndQuery : posted.Headers.Location.ToString();
        Assert.StartsWith("/desk/t/", location);

        await using (var db = app.Db())
        {
            var t = await db.ServiceDeskTickets.Include(x => x.Contact).Include(x => x.Issue).SingleAsync(x => x.ProjectId == project.Id);
            Assert.Equal(ServiceDeskSource.Form, t.Source);
            Assert.Equal(email, t.Contact.Email);
            Assert.Equal("Cannot log in", t.Issue.Title);
            Assert.Contains(t.TrackingToken, location);
        }

        var tracking = await client.GetAsync(location);
        var html = await tracking.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, tracking.StatusCode);
        Assert.Contains("Cannot log in", html);
        Assert.Contains("<strong>error</strong>", html);
        Assert.Contains("<meta name=\"referrer\" content=\"no-referrer\"", html); // keeps the token out of Referer headers

        // A reply from the tracking page becomes a requester comment.
        var path = location.Split('?')[0];
        var reply = await client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "desk-reply", ["Input.Body"] = "Any news?", ["__RequestVerificationToken"] = Token(html),
        }));
        Assert.True((int)reply.StatusCode is 302 or 303);
        await using (var db = app.Db())
            Assert.True(await db.Comments.AnyAsync(c => c.Body == "Any news?" && c.ServiceDeskContactId != null));

        Assert.Contains("Request not found", await client.GetStringAsync("/desk/t/not-a-real-token"));
    }

    [Fact]
    public async Task Public_form_is_unavailable_when_the_service_desk_is_off()
    {
        var project = await app.CreateProjectAsync("alice");
        var html = await app.CreateClient().GetStringAsync($"/desk/{project.Key}");
        Assert.Contains("Not available", html);
        await using var s = app.Scope();
        var r = await s.Get<ServiceDeskIntake>().SubmitFormAsync(project.Key, "x", Sender(), "s", "b", "127.0.0.1");
        Assert.Equal(InboundOutcome.Rejected, r.Outcome);
    }

    // ----- Email pickup directory -----

    [Fact]
    public async Task Pickup_directory_email_carries_the_threading_headers()
    {
        var dir = Path.Combine(app.DataRoot, "pickup-" + Guid.NewGuid().ToString("N"));
        var sender = new PickupDirectoryEmailSender(Options.Create(new EmailOptions { PickupDirectory = dir, From = "ByteBench <bytebench@desk.test>" }),
            app.Services.GetRequiredService<IHostEnvironment>());
        await sender.SendAsync(new EmailMessage("jane@customer.test", "Jane", "Re: [T1-1] Hi", "<p>hi</p>", "hi")
        {
            Headers = new Dictionary<string, string>
            {
                ["Message-Id"] = "<sd.1.abc@desk.test>", ["In-Reply-To"] = "<orig@customer.test>", ["References"] = "<orig@customer.test> <sd.1.000@desk.test>",
                ["Reply-To"] = "desk+r-0123@desk.test", ["Auto-Submitted"] = "auto-replied",
            },
        });
        var file = Assert.Single(Directory.GetFiles(dir, "*.eml"));
        var m = await MimeMessage.LoadAsync(file);
        Assert.Equal("sd.1.abc@desk.test", m.MessageId);
        Assert.Equal("orig@customer.test", m.InReplyTo);
        Assert.Equal(["orig@customer.test", "sd.1.000@desk.test"], m.References);
        Assert.Equal("desk+r-0123@desk.test", m.ReplyTo.Mailboxes.Single().Address);
        Assert.Equal("auto-replied", m.Headers["Auto-Submitted"]);
        Assert.Single(m.Headers, h => h.Id == HeaderId.MessageId);
    }
}
