using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Digests;
using ByteBench.Services.Email;
using ByteBench.Tests.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ByteBench.Tests;

/// <summary>Captures emails; sends to addresses in <see cref="FailFor"/> throw (an SMTP outage for that recipient).</summary>
public sealed class DigestEmailSender : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Sent { get; } = new();
    public ConcurrentDictionary<string, bool> FailFor { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        if (FailFor.ContainsKey(message.To)) throw new IOException("SMTP is down");
        Sent.Enqueue(message);
        return Task.CompletedTask;
    }

    public List<EmailMessage> DigestsTo(string address) =>
        Sent.Where(m => m.To.Equals(address, StringComparison.OrdinalIgnoreCase) && m.Subject.Contains("digest", StringComparison.Ordinal)).ToList();
}

/// <summary>Email on (captured), the digest worker off: tests run <see cref="DigestService.RunOnceAsync"/> at chosen times.</summary>
public sealed class DigestTestApp : TestApp
{
    public DigestEmailSender Mail { get; } = new();
    /// <summary>A "Pipeline" merge check the tests set per pull request (the digest's "failing check" and "ready to merge" notes).</summary>
    public ControllableCheck Check { get; } = new();

    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["Email:Enabled"] = "true";
        settings["Email:BaseUrl"] = "http://digest.test";
        settings["Digest:Enabled"] = "false";
        settings["Push:DeliveryWorker"] = "false";
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.RemoveAll<IEmailSender>();
        services.AddSingleton<IEmailSender>(Mail);
        services.AddSingleton<IMergeCheck>(Check);
    }
}

/// <summary>A clock tests move by hand.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
}

public class DigestScheduleTests
{
    private static TimeZoneInfo Zone(string id) => DigestSchedule.FindZone(id) ?? throw new InvalidOperationException(id);
    private static DateTime Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Fact]
    public void Daily_is_the_next_local_hour_in_the_users_zone()
    {
        // Berlin in winter is UTC+1: 08:00 local = 07:00 UTC; already past today, so tomorrow.
        Assert.Equal(Utc(2026, 11, 25, 7), DigestSchedule.Next(DigestFrequency.Daily, 8, DayOfWeek.Monday, Zone("Europe/Berlin"), Utc(2026, 11, 24, 9)));
        Assert.Equal(Utc(2026, 11, 24, 7), DigestSchedule.Next(DigestFrequency.Daily, 8, DayOfWeek.Monday, Zone("Europe/Berlin"), Utc(2026, 11, 24, 6, 59)));
        // New York in summer is UTC-4.
        Assert.Equal(Utc(2027, 7, 1, 12), DigestSchedule.Next(DigestFrequency.Daily, 8, DayOfWeek.Monday, Zone("America/New_York"), Utc(2027, 7, 1, 3)));
        // Auckland (UTC+13 in November) is already on the next day.
        Assert.Equal(Utc(2026, 11, 25, 18), DigestSchedule.Next(DigestFrequency.Daily, 7, DayOfWeek.Monday, Zone("Pacific/Auckland"), Utc(2026, 11, 24, 20)));
        // Exactly at the time: the next one (strictly after).
        Assert.Equal(Utc(2026, 11, 26, 7), DigestSchedule.Next(DigestFrequency.Daily, 8, DayOfWeek.Monday, Zone("Europe/Berlin"), Utc(2026, 11, 25, 7)));
    }

    [Fact]
    public void Weekly_is_the_next_chosen_weekday()
    {
        // 2026-11-24 is a Tuesday. Monday 09:00 in Tokyo (UTC+9) = Monday 00:00 UTC.
        Assert.Equal(Utc(2026, 11, 30, 0), DigestSchedule.Next(DigestFrequency.Weekly, 9, DayOfWeek.Monday, Zone("Asia/Tokyo"), Utc(2026, 11, 24, 12)));
        // Tuesday 18:00 in Los Angeles (UTC-8) is still to come today.
        Assert.Equal(Utc(2026, 11, 25, 2), DigestSchedule.Next(DigestFrequency.Weekly, 18, DayOfWeek.Tuesday, Zone("America/Los_Angeles"), Utc(2026, 11, 24, 12)));
        // Just after it: a week later.
        Assert.Equal(Utc(2026, 12, 2, 2), DigestSchedule.Next(DigestFrequency.Weekly, 18, DayOfWeek.Tuesday, Zone("America/Los_Angeles"), Utc(2026, 11, 25, 2, 1)));
    }

    [Fact]
    public void Daylight_saving_gaps_and_overlaps_are_handled()
    {
        var berlin = Zone("Europe/Berlin");
        // 2027-03-28: 02:00 doesn't exist in Berlin (02:00 CET -> 03:00 CEST); the digest goes out at 03:00 CEST = 01:00 UTC.
        Assert.Equal(Utc(2027, 3, 28, 1), DigestSchedule.Next(DigestFrequency.Daily, 2, DayOfWeek.Monday, berlin, Utc(2027, 3, 27, 12)));
        // The day after, 02:00 CEST = 00:00 UTC.
        Assert.Equal(Utc(2027, 3, 29, 0), DigestSchedule.Next(DigestFrequency.Daily, 2, DayOfWeek.Monday, berlin, Utc(2027, 3, 28, 1)));
        // 2027-10-31: 02:00 happens twice; the first (CEST, UTC+2) = 00:00 UTC, and only once.
        Assert.Equal(Utc(2027, 10, 31, 0), DigestSchedule.Next(DigestFrequency.Daily, 2, DayOfWeek.Monday, berlin, Utc(2027, 10, 30, 12)));
        Assert.Equal(Utc(2027, 11, 1, 1), DigestSchedule.Next(DigestFrequency.Daily, 2, DayOfWeek.Monday, berlin, Utc(2027, 10, 31, 0)));
        // 08:00 New York across the March change: EST (UTC-5) then EDT (UTC-4).
        var ny = Zone("America/New_York");
        Assert.Equal(Utc(2027, 3, 13, 13), DigestSchedule.Next(DigestFrequency.Daily, 8, DayOfWeek.Monday, ny, Utc(2027, 3, 13, 1)));
        Assert.Equal(Utc(2027, 3, 14, 12), DigestSchedule.Next(DigestFrequency.Daily, 8, DayOfWeek.Monday, ny, Utc(2027, 3, 13, 13)));
    }

    [Theory]
    [InlineData("Europe/Berlin", true)]
    [InlineData("America/Argentina/Buenos_Aires", true)]
    [InlineData("UTC", true)]
    [InlineData("Mars/Olympus_Mons", false)]
    [InlineData("Europe/Berlin\n", false)]
    [InlineData("../../etc/passwd", false)]
    [InlineData("", false)]
    public void Only_IANA_zones_are_accepted(string id, bool ok) => Assert.Equal(ok, DigestSchedule.FindZone(id) != null);

    [Fact]
    public void Preview_limiter_allows_three_an_hour()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero));
        var limiter = new DigestPreviewLimiter(clock, new StaticOptions<DigestOptions>(new DigestOptions()));
        Assert.True(limiter.TryAcquire(1));
        Assert.True(limiter.TryAcquire(1));
        Assert.True(limiter.TryAcquire(1));
        Assert.False(limiter.TryAcquire(1));
        Assert.True(limiter.TryAcquire(2)); // per user
        clock.Now = clock.Now.AddMinutes(59);
        Assert.False(limiter.TryAcquire(1));
        clock.Now = clock.Now.AddMinutes(1);
        Assert.True(limiter.TryAcquire(1));
    }

    [Fact]
    public void The_email_escapes_hostile_content()
    {
        var user = new User { DisplayName = "<b>Eve</b>", Email = "eve@example.com" };
        var hostile = "<script>alert(1)</script>\"><img src=x onerror=alert(2)>";
        var d = new DigestContent(user, DigestFrequency.Daily,
            [new DigestProjectGroup("<i>Proj</i>", 1, [new DigestItem(hostile, "/p/X/issues/1\"><script>")])], 1,
            [], 0, [new DigestItem(hostile, "/p/X/issues/2", "due <u>soon</u>")], 1, [], 0, [], 0);
        var m = DigestBuilder.Compose(d, p => "http://digest.test" + p, "http://digest.test/account/digest/unsubscribe?token=a&b");
        Assert.DoesNotContain("<script>", m.Html);
        Assert.DoesNotContain("<img", m.Html);
        Assert.DoesNotContain("<b>Eve", m.Html);
        Assert.DoesNotContain("<i>Proj", m.Html);
        Assert.DoesNotContain("<u>", m.Html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", m.Html);
        Assert.Contains("token=a&amp;b", m.Html);
        Assert.Contains(hostile, m.Text); // the text part is plain text
        Assert.Equal("<http://digest.test/account/digest/unsubscribe?token=a&b>", m.Headers["List-Unsubscribe"]);
        Assert.Equal("List-Unsubscribe=One-Click", m.Headers["List-Unsubscribe-Post"]);
    }
}

public class DigestTests(DigestTestApp app) : IClassFixture<DigestTestApp>
{
    private static int counter;
    private static readonly DateTime T0 = new(2027, 2, 1, 7, 0, 0, DateTimeKind.Utc);

    private DigestService Digests => app.Services.GetRequiredService<DigestService>();

    private async Task<User> NewUserAsync() => await app.CreateUserAsync("dg" + Interlocked.Increment(ref counter));

    /// <summary>Daily digests for <paramref name="user"/>, due at <see cref="T0"/>.</summary>
    private async Task EnableAsync(User user, DateTime? dueAt = null)
    {
        Assert.Null(await Digests.SaveSettingsAsync(user.Id, DigestFrequency.Daily, 8, DayOfWeek.Monday, "Europe/Berlin"));
        await using var db = app.Db();
        await db.UserNotificationSettings.Where(s => s.UserId == user.Id).ExecuteUpdateAsync(x => x.SetProperty(s => s.NextDigestAt, dueAt ?? T0));
    }

    /// <summary>A private project of alice's with <paramref name="user"/> as a developer.</summary>
    private async Task<Project> ProjectWithAsync(User user)
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private);
        await using var s = app.Scope(app.UserId("alice"));
        await s.Get<MemberService>().AddMemberAsync(project.Id, user.UserName!, ProjectRole.Developer);
        return project;
    }

    private async Task AddNotificationAsync(User user, Project project, string title, NotificationKind kind = NotificationKind.IssueCommented, string? url = null)
    {
        await using var db = app.Db();
        db.Notifications.Add(new Notification
        {
            UserId = user.Id, ProjectId = project.Id, ActorId = app.UserId("alice"), Kind = kind,
            Title = title, Url = url ?? $"/p/{project.Key}/issues/1", CreatedAt = T0.AddHours(-3),
        });
        await db.SaveChangesAsync();
    }

    private async Task<UserNotificationSettings> SettingsAsync(User user)
    {
        await using var db = app.Db();
        return await db.UserNotificationSettings.AsNoTracking().SingleAsync(s => s.UserId == user.Id);
    }

    [Fact]
    public async Task A_due_digest_is_sent_once_and_rescheduled()
    {
        var user = await NewUserAsync();
        var project = await ProjectWithAsync(user);
        await AddNotificationAsync(user, project, $"{project.Key}-1 Fix the login");
        await AddNotificationAsync(user, project, $"{project.Key}-2 Mention", NotificationKind.Mentioned);
        await EnableAsync(user);

        await Digests.RunOnceAsync(T0.AddMinutes(-1)); // not due yet
        Assert.Empty(app.Mail.DigestsTo(user.Email!));

        await Digests.RunOnceAsync(T0.AddMinutes(2));
        var mail = Assert.Single(app.Mail.DigestsTo(user.Email!));
        Assert.Contains("daily digest", mail.Subject);
        Assert.Contains("Fix the login", mail.Html);
        Assert.Contains("Mentions", mail.Html);
        Assert.Contains($"http://digest.test/p/{project.Key}/issues/1", mail.Html);
        Assert.Contains("Fix the login", mail.Text);
        Assert.StartsWith("<http://digest.test/account/digest/unsubscribe?token=", mail.Headers["List-Unsubscribe"]);
        Assert.Contains("/account/digest/unsubscribe?token=", mail.Html);

        var s = await SettingsAsync(user);
        Assert.Equal(T0.AddMinutes(2), s.LastDigestAt);
        Assert.Equal(new DateTime(2027, 2, 2, 7, 0, 0), s.NextDigestAt); // 08:00 Berlin tomorrow

        await Digests.RunOnceAsync(T0.AddMinutes(7));
        Assert.Single(app.Mail.DigestsTo(user.Email!));
        await using var db = app.Db();
        var log = await db.DigestLogs.SingleAsync(l => l.UserId == user.Id);
        Assert.True(log.Sent);
        Assert.Equal(1, log.UnreadCount);
        Assert.Equal(1, log.MentionCount);
    }

    [Fact]
    public async Task Two_concurrent_runs_send_only_once()
    {
        var users = new List<User>();
        for (var i = 0; i < 4; i++)
        {
            var u = await NewUserAsync();
            await AddNotificationAsync(u, await ProjectWithAsync(u), "Race item");
            await EnableAsync(u, T0.AddDays(10));
            users.Add(u);
        }
        var at = T0.AddDays(10).AddMinutes(1);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Digests.RunOnceAsync(at))));
        foreach (var u in users) Assert.Single(app.Mail.DigestsTo(u.Email!));
        Assert.True(results.Sum(r => r.Claimed) >= users.Count);
        await using var db = app.Db();
        var ids = users.Select(u => u.Id).ToList();
        Assert.Equal(users.Count, await db.DigestLogs.CountAsync(l => ids.Contains(l.UserId)));
    }

    [Fact]
    public async Task Nothing_to_report_sends_nothing()
    {
        var user = await NewUserAsync();
        await ProjectWithAsync(user);
        await EnableAsync(user);
        var r = await Digests.RunOnceAsync(T0.AddMinutes(1));
        Assert.Empty(app.Mail.DigestsTo(user.Email!));
        Assert.True(r.Empty >= 1);
        await using var db = app.Db();
        var log = await db.DigestLogs.SingleAsync(l => l.UserId == user.Id);
        Assert.False(log.Sent);
        Assert.NotNull((await SettingsAsync(user)).NextDigestAt); // still scheduled
    }

    [Fact]
    public async Task Content_from_a_project_the_user_lost_access_to_is_left_out()
    {
        var user = await NewUserAsync();
        var kept = await ProjectWithAsync(user);
        var lost = await ProjectWithAsync(user);
        await AddNotificationAsync(user, kept, "Visible thing");
        await AddNotificationAsync(user, lost, "TOP-SECRET-LOST");
        await using (var s = app.Scope(app.UserId("alice"))) await s.Get<MemberService>().RemoveMemberAsync(lost.Id, user.Id);
        await EnableAsync(user);

        await Digests.RunOnceAsync(T0.AddMinutes(1));
        var mail = Assert.Single(app.Mail.DigestsTo(user.Email!));
        Assert.Contains("Visible thing", mail.Html);
        Assert.DoesNotContain("TOP-SECRET-LOST", mail.Html);
        Assert.DoesNotContain("TOP-SECRET-LOST", mail.Text);
        Assert.DoesNotContain(lost.Key, mail.Text);

        // Only the lost project: nothing to report, no email.
        var other = await NewUserAsync();
        var gone = await ProjectWithAsync(other);
        await AddNotificationAsync(other, gone, "TOP-SECRET-LOST");
        await using (var s = app.Scope(app.UserId("alice"))) await s.Get<MemberService>().RemoveMemberAsync(gone.Id, other.Id);
        await EnableAsync(other);
        await Digests.RunOnceAsync(T0.AddMinutes(2));
        Assert.Empty(app.Mail.DigestsTo(other.Email!));
    }

    [Fact]
    public async Task Preferences_groups_off_mutes_and_the_email_switch_are_respected()
    {
        var user = await NewUserAsync();
        var project = await ProjectWithAsync(user);
        var issue = await app.CreateIssueAsync(project.Id, "alice", "Muted issue");
        await using (var s = app.Scope()) await s.Get<NotificationService>().SetIssueMutedAsync(user.Id, issue.Id, true);
        await AddNotificationAsync(user, project, "MUTED-COMMENT", NotificationKind.IssueCommented, $"/p/{project.Key}/issues/{issue.Number}");
        await AddNotificationAsync(user, project, "PIPELINE-OFF", NotificationKind.PipelineFailed, $"/p/{project.Key}/pipelines/1");
        await AddNotificationAsync(user, project, "Still here", NotificationKind.Assigned, $"/p/{project.Key}/issues/99");
        await using (var s = app.Scope())
        {
            var prefs = s.Get<NotificationPreferences>();
            var channels = (await prefs.GetAsync(user.Id)).Channels.ToDictionary();
            channels[NotificationGroup.Pipelines] = NotificationChannel.Off;
            await prefs.SaveAsync(user.Id, true, channels);
        }
        await EnableAsync(user);
        await Digests.RunOnceAsync(T0.AddMinutes(1));
        var mail = Assert.Single(app.Mail.DigestsTo(user.Email!));
        Assert.Contains("Still here", mail.Html);
        Assert.DoesNotContain("MUTED-COMMENT", mail.Html);
        Assert.DoesNotContain("PIPELINE-OFF", mail.Html);

        // All notification emails off (or the all-emails unsubscribe): no digest either.
        await using (var s = app.Scope()) await s.Get<NotificationPreferences>().SetEmailEnabledAsync(user.Id, false);
        await using (var db = app.Db())
            await db.UserNotificationSettings.Where(x => x.UserId == user.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.NextDigestAt, T0.AddDays(3)));
        await Digests.RunOnceAsync(T0.AddDays(3).AddMinutes(1));
        Assert.Single(app.Mail.DigestsTo(user.Email!));
    }

    [Fact]
    public async Task Due_issues_and_review_requests_are_listed_with_hostile_titles_escaped()
    {
        var user = await NewUserAsync();
        var project = await ProjectWithAsync(user);
        var hostile = "<script>alert('x')</script> Overdue";
        var overdue = await app.CreateIssueAsync(project.Id, "alice", hostile);
        var later = await app.CreateIssueAsync(project.Id, "alice", "Due in a month");
        var done = await app.CreateIssueAsync(project.Id, "alice", "Done already");
        await using (var db = app.Db())
        {
            await db.Issues.Where(i => i.Id == overdue.Id).ExecuteUpdateAsync(x => x.SetProperty(i => i.AssigneeId, user.Id).SetProperty(i => i.DueDate, T0.AddDays(-2)));
            await db.Issues.Where(i => i.Id == later.Id).ExecuteUpdateAsync(x => x.SetProperty(i => i.AssigneeId, user.Id).SetProperty(i => i.DueDate, T0.AddDays(30)));
            await db.Issues.Where(i => i.Id == done.Id).ExecuteUpdateAsync(x => x.SetProperty(i => i.AssigneeId, user.Id).SetProperty(i => i.DueDate, T0.AddDays(1)).SetProperty(i => i.Status, IssueStatus.Done));
            await db.Notifications.Where(n => n.UserId == user.Id).ExecuteUpdateAsync(x => x.SetProperty(n => n.IsRead, true));
        }
        await EnableAsync(user);
        await Digests.RunOnceAsync(T0.AddMinutes(1));
        var mail = Assert.Single(app.Mail.DigestsTo(user.Email!));
        Assert.Contains("overdue", mail.Html);
        Assert.Contains("&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt; Overdue", mail.Html);
        Assert.DoesNotContain("<script>", mail.Html);
        Assert.DoesNotContain("Due in a month", mail.Html);
        Assert.DoesNotContain("Done already", mail.Html);
        Assert.Contains("1 due soon", mail.Subject);
    }

    /// <summary>An open pull request by <paramref name="author"/> in <paramref name="project"/> (a new repository with a feature branch).</summary>
    private async Task<PullRequest> NewPullRequestAsync(Project project, string author, string title, bool draft = false)
    {
        var repo = await app.CreateRepoAsync(project.Id, "alice", "r" + Guid.NewGuid().ToString("N")[..8]);
        app.Commit(repo, "main", "README.md", "# Hello\n", message: "base");
        var branch = "feature/" + Guid.NewGuid().ToString("N")[..6];
        app.Commit(repo, branch, "a.txt", "a", from: "main", message: "add a");
        await using var s = app.Scope(app.UserId(author));
        return await s.Get<PullRequestService>().CreateAsync(repo, title, null, branch, "main", app.UserId(author), draft: draft);
    }

    private async Task MarkAllReadAsync(User user)
    {
        await using var db = app.Db();
        await db.Notifications.Where(n => n.UserId == user.Id).ExecuteUpdateAsync(x => x.SetProperty(n => n.IsRead, true));
    }

    private async Task<DigestContent> BuildAsync(User user)
    {
        await using var s = app.Scope();
        return (await s.Get<DigestBuilder>().BuildAsync(user.Id, DigestFrequency.Daily, T0))!;
    }

    [Fact]
    public async Task Pull_requests_waiting_for_my_review_are_listed_until_i_review_and_drafts_are_left_out()
    {
        var user = await NewUserAsync();
        var project = await ProjectWithAsync(user);
        var hostile = "<img src=x onerror=alert(1)> Please review";
        var waiting = await NewPullRequestAsync(project, "alice", hostile);
        var draft = await NewPullRequestAsync(project, "alice", "Draft work", draft: true);
        var own = await NewPullRequestAsync(project, "alice", "Not requested");
        await using (var db = app.Db())
        {
            db.PullRequestReviewers.AddRange(
                new PullRequestReviewer { PullRequestId = waiting.Id, UserId = user.Id },
                new PullRequestReviewer { PullRequestId = draft.Id, UserId = user.Id });
            await db.SaveChangesAsync();
        }
        await MarkAllReadAsync(user);

        var content = await BuildAsync(user);
        Assert.Equal(1, content.ReviewTotal);
        Assert.Contains(hostile, content.Reviews[0].Text);
        Assert.StartsWith($"/p/{project.Key}/repos/", content.Reviews[0].Url);
        Assert.EndsWith($"/pull-requests/{waiting.Number}", content.Reviews[0].Url);
        Assert.DoesNotContain(content.Reviews, i => i.Text.Contains("Draft work") || i.Text.Contains("Not requested"));

        await EnableAsync(user);
        await Digests.RunOnceAsync(T0.AddMinutes(1));
        var mail = Assert.Single(app.Mail.DigestsTo(user.Email!));
        Assert.Contains("1 to review", mail.Subject);
        Assert.Contains("Pull requests waiting for your review (1)", mail.Html);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt; Please review", mail.Html);
        Assert.DoesNotContain("<img src=x", mail.Html);

        // Once I have reviewed it, it's gone.
        await using (var s = app.Scope(app.UserId(user.UserName!)))
            await s.Get<PullRequestService>().SetReviewAsync(waiting.Id, user.Id, ReviewState.Approved);
        Assert.Equal(0, (await BuildAsync(user)).ReviewTotal);
    }

    [Fact]
    public async Task My_pull_requests_show_when_ready_to_merge_or_failing_and_respect_the_groups()
    {
        var user = await NewUserAsync();
        var project = await ProjectWithAsync(user);
        var ready = await NewPullRequestAsync(project, user.UserName!, "Ready one");
        var failing = await NewPullRequestAsync(project, user.UserName!, "Failing one");
        var idle = await NewPullRequestAsync(project, user.UserName!, "Idle one");
        var draft = await NewPullRequestAsync(project, user.UserName!, "Draft one", draft: true);
        app.Check.Set(ready.Id, MergeCheckState.Passed);
        app.Check.Set(failing.Id, MergeCheckState.Failed);
        app.Check.Set(idle.Id, MergeCheckState.Passed); // passing but nobody approved it
        app.Check.Set(draft.Id, MergeCheckState.Passed);
        await using (var s = app.Scope(app.UserId("alice")))
        {
            foreach (var pr in new[] { ready, failing, draft }) await s.Get<PullRequestService>().SetReviewAsync(pr.Id, app.UserId("alice"), ReviewState.Approved);
        }
        await MarkAllReadAsync(user);

        var content = await BuildAsync(user);
        Assert.Equal(2, content.MyPullRequestTotal);
        Assert.Contains(content.MyPullRequests, i => i.Text.Contains("Ready one") && i.Note == "ready to merge");
        Assert.Contains(content.MyPullRequests, i => i.Text.Contains("Failing one") && i.Note!.StartsWith("failing: ") && i.Note.Contains("Pipeline"));
        Assert.DoesNotContain(content.MyPullRequests, i => i.Text.Contains("Idle one") || i.Text.Contains("Draft one"));

        var mail = DigestBuilder.Compose(content, p => "http://digest.test" + p, "http://digest.test/u");
        Assert.Contains("Your pull requests (2)", mail.Html);
        Assert.Contains("ready to merge", mail.Text);

        // Pipelines group off: only the "ready" PR remains. Reviews group off as well: the section is empty.
        await using (var db = app.Db())
        {
            db.NotificationPreferences.Add(new NotificationPreference { UserId = user.Id, Group = NotificationGroup.Pipelines, Channel = NotificationChannel.Off });
            await db.SaveChangesAsync();
        }
        var noPipelines = await BuildAsync(user);
        Assert.Equal(1, noPipelines.MyPullRequestTotal);
        Assert.Equal("ready to merge", noPipelines.MyPullRequests[0].Note);
        await using (var db = app.Db())
        {
            db.NotificationPreferences.Add(new NotificationPreference { UserId = user.Id, Group = NotificationGroup.Reviews, Channel = NotificationChannel.Off });
            await db.SaveChangesAsync();
        }
        Assert.Equal(0, (await BuildAsync(user)).MyPullRequestTotal);
    }

    [Fact]
    public async Task Pull_requests_of_a_project_I_left_are_not_in_the_digest()
    {
        var user = await NewUserAsync();
        var project = await ProjectWithAsync(user);
        var pr = await NewPullRequestAsync(project, "alice", "Soon hidden");
        await using (var db = app.Db())
        {
            db.PullRequestReviewers.Add(new PullRequestReviewer { PullRequestId = pr.Id, UserId = user.Id });
            await db.SaveChangesAsync();
        }
        await MarkAllReadAsync(user);
        Assert.Equal(1, (await BuildAsync(user)).ReviewTotal);
        await using (var s = app.Scope(app.UserId("alice")))
            await s.Get<MemberService>().RemoveMemberAsync(project.Id, user.Id);
        Assert.Equal(0, (await BuildAsync(user)).ReviewTotal);
    }

    [Fact]
    public async Task A_failing_send_is_retried_three_times_then_skipped_and_never_blocks_others()
    {
        var failing = await NewUserAsync();
        var fine = await NewUserAsync();
        foreach (var u in new[] { failing, fine })
        {
            await AddNotificationAsync(u, await ProjectWithAsync(u), "Retry item");
            await EnableAsync(u, T0.AddDays(20));
        }
        app.Mail.FailFor[failing.Email!] = true;
        try
        {
            var t = T0.AddDays(20).AddMinutes(1);
            await Digests.RunOnceAsync(t);
            Assert.Single(app.Mail.DigestsTo(fine.Email!));
            var s = await SettingsAsync(failing);
            Assert.Equal(1, s.DigestFailures);
            Assert.Equal(t, s.NextDigestAt); // back in the queue for the next poll

            for (var retry = 2; retry <= DigestService.MaxRetries + 1; retry++)
            {
                t = t.AddMinutes(5);
                await Digests.RunOnceAsync(t);
            }
            s = await SettingsAsync(failing);
            Assert.Equal(0, s.DigestFailures);
            Assert.Equal(new DateTime(2027, 2, 22, 7, 0, 0), s.NextDigestAt); // given up until the next period
            await using var db = app.Db();
            Assert.Equal(DigestService.MaxRetries + 1, await db.DigestLogs.CountAsync(l => l.UserId == failing.Id && l.Failed));
            Assert.Single(app.Mail.DigestsTo(fine.Email!));

            // Next period, SMTP is back.
            app.Mail.FailFor.Clear();
            await Digests.RunOnceAsync(new DateTime(2027, 2, 22, 7, 1, 0, DateTimeKind.Utc));
            Assert.Single(app.Mail.DigestsTo(failing.Email!));
        }
        finally { app.Mail.FailFor.Clear(); }
    }

    [Fact]
    public async Task Settings_are_validated()
    {
        var user = await NewUserAsync();
        Assert.NotNull(await Digests.SaveSettingsAsync(user.Id, DigestFrequency.Daily, 8, DayOfWeek.Monday, "Not/AZone"));
        Assert.NotNull(await Digests.SaveSettingsAsync(user.Id, DigestFrequency.Daily, 24, DayOfWeek.Monday, "UTC"));
        Assert.NotNull(await Digests.SaveSettingsAsync(user.Id, (DigestFrequency)9, 8, DayOfWeek.Monday, "UTC"));
        Assert.Null(await Digests.SaveSettingsAsync(user.Id, DigestFrequency.Weekly, 17, DayOfWeek.Friday, "America/Chicago"));
        var s = await SettingsAsync(user);
        Assert.Equal("America/Chicago", s.TimeZoneId);
        Assert.Equal(DayOfWeek.Friday, s.NextDigestAt.HasValue
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(s.NextDigestAt.Value, DateTimeKind.Utc), DigestSchedule.FindZone("America/Chicago")!).DayOfWeek
            : DayOfWeek.Sunday);
        Assert.Null(await Digests.SaveSettingsAsync(user.Id, DigestFrequency.Off, 17, DayOfWeek.Friday, "America/Chicago"));
        Assert.Null((await SettingsAsync(user)).NextDigestAt);
    }

    // ---------------------------------------------------------------- previews

    [Fact]
    public async Task Previews_use_the_same_builder_and_are_rate_limited()
    {
        var user = await NewUserAsync();
        await AddNotificationAsync(user, await ProjectWithAsync(user), "Preview item");
        Assert.Equal(DigestPreviewResult.Sent, await Digests.SendPreviewAsync(user.Id));
        Assert.Equal(DigestPreviewResult.Sent, await Digests.SendPreviewAsync(user.Id));
        Assert.Equal(DigestPreviewResult.Sent, await Digests.SendPreviewAsync(user.Id));
        Assert.Equal(DigestPreviewResult.RateLimited, await Digests.SendPreviewAsync(user.Id));
        Assert.Equal(3, app.Mail.DigestsTo(user.Email!).Count);
        Assert.All(app.Mail.DigestsTo(user.Email!), m => Assert.Contains("Preview item", m.Html));
        // Previews don't touch the schedule.
        await using var db = app.Db();
        Assert.Null(await db.UserNotificationSettings.Where(s => s.UserId == user.Id).Select(s => s.NextDigestAt).FirstOrDefaultAsync());
        Assert.Equal(3, await db.DigestLogs.CountAsync(l => l.UserId == user.Id && l.Preview));

        var empty = await NewUserAsync();
        Assert.Equal(DigestPreviewResult.NothingToReport, await Digests.SendPreviewAsync(empty.Id));
        Assert.Empty(app.Mail.DigestsTo(empty.Email!));
    }

    // ---------------------------------------------------------------- unsubscribe

    private static string Token(EmailMessage m) =>
        Uri.UnescapeDataString(Regex.Match(m.Headers["List-Unsubscribe"], "token=([^>]+)>").Groups[1].Value);

    [Fact]
    public async Task One_click_unsubscribe_turns_off_digests_for_that_user_only()
    {
        var user = await NewUserAsync();
        var bystander = await NewUserAsync();
        await AddNotificationAsync(user, await ProjectWithAsync(user), "Unsub item");
        await EnableAsync(user);
        await EnableAsync(bystander);
        await Digests.RunOnceAsync(T0.AddMinutes(1));
        var token = Token(Assert.Single(app.Mail.DigestsTo(user.Email!)));

        var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var page = await client.GetAsync("/account/digest/unsubscribe?token=" + Uri.EscapeDataString(token));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("method=\"post\"", await page.Content.ReadAsStringAsync());
        Assert.Equal(DigestFrequency.Daily, (await SettingsAsync(user)).DigestFrequency); // GET changes nothing

        // RFC 8058: the mail client POSTs "List-Unsubscribe=One-Click" to the URL (no cookie, no antiforgery).
        var post = await client.PostAsync("/account/digest/unsubscribe?token=" + Uri.EscapeDataString(token),
            new FormUrlEncodedContent(new Dictionary<string, string> { ["List-Unsubscribe"] = "One-Click" }));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        var s = await SettingsAsync(user);
        Assert.Equal(DigestFrequency.Off, s.DigestFrequency);
        Assert.Null(s.NextDigestAt);
        Assert.True(s.EmailEnabled); // other notification emails are untouched
        Assert.Equal(DigestFrequency.Daily, (await SettingsAsync(bystander)).DigestFrequency);

        // Re-enabled later: replaying the old link (still valid) just turns it off again.
        await EnableAsync(user);
        var form = await client.PostAsync("/account/digest/unsubscribe", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }));
        Assert.Equal(HttpStatusCode.OK, form.StatusCode);
        Assert.Equal(DigestFrequency.Off, (await SettingsAsync(user)).DigestFrequency);
    }

    [Fact]
    public async Task Forged_expired_and_foreign_tokens_are_refused()
    {
        var user = await NewUserAsync();
        await EnableAsync(user);
        var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var dp = app.Services.GetRequiredService<IDataProtectionProvider>();
        var tokens = app.Services.GetRequiredService<DigestUnsubscribeTokens>();

        var valid = tokens.Create(user.Id);
        var expired = dp.CreateProtector(DigestUnsubscribeTokens.Purpose).ToTimeLimitedDataProtector()
            .Protect("digest:" + user.Id, DateTimeOffset.UtcNow.AddSeconds(-5));
        var forged = valid[..^4] + (valid[^4] == 'A' ? "BBBB" : "AAAA");
        var otherPurpose = app.Services.GetRequiredService<UnsubscribeTokens>().Create(user.Id); // the all-emails link
        var wrongShape = dp.CreateProtector(DigestUnsubscribeTokens.Purpose).ToTimeLimitedDataProtector().Protect(user.Id.ToString(), TimeSpan.FromDays(1));

        foreach (var bad in new[] { expired, forged, otherPurpose, wrongShape, "", "not-a-token" })
        {
            Assert.Null(tokens.Read(bad));
            var get = await client.GetAsync("/account/digest/unsubscribe?token=" + Uri.EscapeDataString(bad));
            Assert.Equal(HttpStatusCode.BadRequest, get.StatusCode);
            var post = await client.PostAsync("/account/digest/unsubscribe?token=" + Uri.EscapeDataString(bad), new FormUrlEncodedContent([]));
            Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        }
        Assert.Equal(DigestFrequency.Daily, (await SettingsAsync(user)).DigestFrequency);
        Assert.Equal(user.Id, tokens.Read(valid));
        Assert.NotEqual(user.Id + 1, tokens.Read(valid)); // bound to the user it was made for
    }
}
