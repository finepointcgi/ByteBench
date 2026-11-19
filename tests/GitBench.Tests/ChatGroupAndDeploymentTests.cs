using System.Text.Json.Nodes;
using GitBench.Data;
using GitBench.Services;
using GitBench.Services.ChatIntegrations;
using GitBench.Services.Webhooks;
using GitBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GitBench.Tests;

/// <summary>
/// Group-level Slack/Teams integrations (inherited by the group's projects and subgroups, a project can opt out) and the
/// messages built from <c>deployment.status</c> events. Nothing leaves the process: the chat HttpClient is the fake.
/// </summary>
public class ChatGroupAndDeploymentTests(ChatApp app) : IClassFixture<ChatApp>
{
    private static int counter;
    private static string Unique() => Interlocked.Increment(ref counter).ToString("D4") + Guid.NewGuid().ToString("N")[..6];
    private static string UniquePath() => "chatg" + Unique().ToLowerInvariant();

    private async Task<Group> GroupAsync(string owner = "carol", int? parentId = null)
    {
        await using var s = app.Scope(app.UserId(owner));
        var path = UniquePath();
        return await s.Get<GroupService>().CreateAsync(path.ToUpperInvariant(), path, null, ProjectVisibility.Private, null, parentId);
    }

    private async Task<Project> ProjectInAsync(Group g, string owner = "carol", params (string User, ProjectRole Role)[] members)
    {
        var key = "CG" + Unique()[..4].ToUpperInvariant() + Interlocked.Increment(ref counter);
        await using var s = app.Scope(app.UserId(owner));
        var project = await s.Get<IssueService>().CreateProjectAsync(new Project { Key = key, Name = "Chat group " + key, Visibility = ProjectVisibility.Private, GroupId = g.Id });
        foreach (var (user, role) in members) await s.Get<MemberService>().AddMemberAsync(project.Id, user, role);
        return project;
    }

    private async Task<ChatIntegrationInfo> AddGroupHookAsync(Group g, string url, string by = "carol", params string[] events)
    {
        await using var s = app.Scope(app.UserId(by));
        return await s.Get<ChatIntegrationService>().CreateAsync(ChatScope.Group(g.Id), new ChatIntegrationInput
        {
            Kind = WebhookKind.Slack, Mode = ChatMode.SlackWebhook, Url = url, Name = "Group channel",
            Events = events.Length > 0 ? [.. events] : [ChatEvents.IssueCreated],
        });
    }

    private static string HookUrl() => $"https://hooks.slack.com/services/T0TEST/B{Unique()}/secretpart{Unique()}";

    // ---------------------------------------------------------------- group integrations

    [Fact]
    public async Task A_group_integration_announces_the_events_of_its_projects_and_subgroups_only()
    {
        var top = await GroupAsync();
        var sub = await GroupAsync("carol", top.Id);
        var direct = await ProjectInAsync(top);
        var nested = await ProjectInAsync(sub);
        var outside = await app.CreateProjectAsync("carol");
        var url = HookUrl();
        var hook = await AddGroupHookAsync(top, url);

        var issueDirect = await app.CreateIssueAsync(direct.Id, "carol", "In the group");
        var issueNested = await app.CreateIssueAsync(nested.Id, "carol", "In a subgroup");
        var issueOutside = await app.CreateIssueAsync(outside.Id, "carol", "Elsewhere");
        var first = await app.Fake.WaitForAsync(x => x.Url == url && x.Body.Contains(issueDirect.Key));
        Assert.Contains($"created {issueDirect.Key}", JsonNode.Parse(first.Body)!["text"]!.GetValue<string>());
        await app.Fake.WaitForAsync(x => x.Url == url && x.Body.Contains(issueNested.Key));
        await Task.Delay(300);
        Assert.DoesNotContain(app.Fake.All, x => x.Url == url && x.Body.Contains(issueOutside.Key));

        // Stored like a project integration: encrypted, only the host in Url, hints in the listing; it belongs to the group.
        await using var db = app.Db();
        var row = await db.Webhooks.AsNoTracking().FirstAsync(h => h.Id == hook.Id);
        Assert.Null(row.ProjectId);
        Assert.Equal(top.Id, row.GroupId);
        Assert.StartsWith(WebhookSecrets.Prefix, row.Secret);
        Assert.DoesNotContain("secretpart", row.Secret + row.Url);
        Assert.DoesNotContain("secretpart", System.Text.Json.JsonSerializer.Serialize(hook));
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "chat_integration.created" && e.TargetId == hook.Id.ToString()));

        // The group hook is not one of the project's own integrations or generic webhooks.
        await using var s2 = app.Scope(app.UserId("carol"));
        Assert.Empty(await s2.Get<ChatIntegrationService>().ListAsync(ChatScope.Project(direct.Id)));
        Assert.Empty(await s2.Get<WebhookService>().ListAsync(direct.Id));
        var listed = await s2.Get<ChatIntegrationService>().ListAsync(ChatScope.Group(top.Id));
        Assert.Equal(hook.Id, Assert.Single(listed).Id);
        Assert.Single(await s2.Get<ChatIntegrationService>().ListInheritedAsync(nested.Id));
        Assert.Empty(await s2.Get<ChatIntegrationService>().ListInheritedAsync(outside.Id));
    }

    [Fact]
    public async Task A_project_can_opt_out_of_a_group_integration_and_back_in()
    {
        var group = await GroupAsync();
        var project = await ProjectInAsync(group, "carol", ("bob", ProjectRole.Developer), ("dan", ProjectRole.Maintainer));
        var url = HookUrl();
        var hook = await AddGroupHookAsync(group, url);

        // A Developer can't change the project's integrations; a Maintainer can opt out.
        await using (var s = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ChatIntegrationService>().SetOptOutAsync(project.Id, hook.Id, true));
        await using (var s = app.Scope(app.UserId("dan")))
        {
            var svc = s.Get<ChatIntegrationService>();
            Assert.False(Assert.Single(await svc.ListInheritedAsync(project.Id)).OptedOut);
            await svc.SetOptOutAsync(project.Id, hook.Id, true);
            var inherited = Assert.Single(await svc.ListInheritedAsync(project.Id));
            Assert.True(inherited.OptedOut);
            Assert.Equal(group.FullPath, inherited.GroupPath);
            await svc.SetOptOutAsync(project.Id, hook.Id, true); // idempotent
            // Not an integration that applies to this project:
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetOptOutAsync(project.Id, 987654, true));
            // …and the group's integration can't be edited or deleted from the project side.
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.DeleteAsync(ChatScope.Group(group.Id), hook.Id));
        }
        await using (var db = app.Db())
        {
            Assert.Equal(1, await db.ChatIntegrationOptOuts.CountAsync(o => o.ProjectId == project.Id && o.WebhookId == hook.Id));
            Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "chat_integration.opted_out" && e.ProjectId == project.Id));
        }

        var quiet = await app.CreateIssueAsync(project.Id, "carol", "Opted out issue");
        await Task.Delay(500);
        Assert.DoesNotContain(app.Fake.All, x => x.Url == url && x.Body.Contains(quiet.Key));

        await using (var s = app.Scope(app.UserId("dan")))
            await s.Get<ChatIntegrationService>().SetOptOutAsync(project.Id, hook.Id, false);
        var loud = await app.CreateIssueAsync(project.Id, "carol", "Opted back in");
        await app.Fake.WaitForAsync(x => x.Url == url && x.Body.Contains(loud.Key));
        await using (var db = app.Db())
            Assert.Empty(await db.ChatIntegrationOptOuts.Where(o => o.ProjectId == project.Id).ToListAsync());
    }

    [Fact]
    public async Task Only_group_owners_manage_group_integrations()
    {
        var group = await GroupAsync();
        await using (var s = app.Scope(app.UserId("carol")))
        {
            await s.Get<GroupService>().AddMemberAsync(group.Id, "bob", ProjectRole.Maintainer);
            await s.Get<GroupService>().AddMemberAsync(group.Id, "dan", ProjectRole.Developer);
        }
        var hookUrl = HookUrl();
        foreach (var user in new[] { "bob", "dan" })
        {
            await using var s = app.Scope(app.UserId(user));
            var svc = s.Get<ChatIntegrationService>();
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.ListAsync(ChatScope.Group(group.Id)));
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.CreateAsync(ChatScope.Group(group.Id),
                new ChatIntegrationInput { Kind = WebhookKind.Slack, Url = hookUrl }));
        }
        // A user who can't even see the group.
        var erin = await app.CreateUserAsync("erin" + Unique());
        await using (var s = app.Scope(erin.Id))
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ChatIntegrationService>().ListAsync(ChatScope.Group(group.Id)));

        var hook = await AddGroupHookAsync(group, hookUrl);
        await using (var s = app.Scope(app.UserId("carol")))
        {
            var svc = s.Get<ChatIntegrationService>();
            // The same validation as project integrations (SSRF/host rules, at least one event, kind can't change).
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(ChatScope.Group(group.Id),
                new ChatIntegrationInput { Kind = WebhookKind.Slack, Url = "https://evil.example/hook" }));
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.UpdateAsync(ChatScope.Group(group.Id), hook.Id,
                new ChatIntegrationInput { Kind = WebhookKind.Teams, Url = "https://contoso.webhook.office.com/webhookb2/x" }));
            // A blank URL keeps the stored secret on update.
            var updated = await svc.UpdateAsync(ChatScope.Group(group.Id), hook.Id,
                new ChatIntegrationInput { Kind = WebhookKind.Slack, Name = "Renamed", Events = [ChatEvents.IssueCreated, ChatEvents.PipelineFailed] });
            Assert.Equal("Renamed", updated.Name);
            Assert.Equal(hook.UrlHint, updated.UrlHint);
            // A project's integration id isn't reachable through another group's scope.
            var other = await GroupAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.UpdateAsync(ChatScope.Group(other.Id), hook.Id,
                new ChatIntegrationInput { Kind = WebhookKind.Slack, Events = [ChatEvents.IssueCreated] }));
        }
    }

    [Fact]
    public async Task Group_integrations_send_a_test_message_keep_a_log_and_go_with_the_group()
    {
        var group = await GroupAsync();
        var project = await ProjectInAsync(group);
        var url = HookUrl();
        var hook = await AddGroupHookAsync(group, url);

        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<ChatIntegrationService>().SendTestAsync(ChatScope.Group(group.Id), hook.Id);
        var sent = await app.Fake.WaitForAsync(x => x.Url == url);
        Assert.Contains("projects of this group", sent.Body);
        await using (var s = app.Scope(app.UserId("carol")))
        {
            var log = await s.Get<ChatIntegrationService>().RecentDeliveriesAsync(ChatScope.Group(group.Id), hook.Id);
            Assert.Single(log);
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<ChatIntegrationService>().RecentDeliveriesAsync(ChatScope.Project(project.Id), hook.Id));
        }

        // Deleting the (empty) group deletes its integration; a deleted integration also drops the projects' opt-outs.
        var empty = await GroupAsync();
        var emptyHook = await AddGroupHookAsync(empty, HookUrl());
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<GroupService>().DeleteAsync(empty.Id);
        await using var db = app.Db();
        Assert.False(await db.Webhooks.AnyAsync(h => h.Id == emptyHook.Id));
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<ChatIntegrationService>().DeleteAsync(ChatScope.Group(group.Id), hook.Id);
        Assert.False(await app.Db().Webhooks.AnyAsync(h => h.Id == hook.Id));
    }

    // ---------------------------------------------------------------- deployment messages

    private void RaiseDeployment(int projectId, Repository repo, string status, string @ref = "refs/heads/main", string environment = "production",
        string? externalUrl = "https://prod.example.com", string job = "deploy") =>
        app.Services.GetRequiredService<WebhookDispatcher>().Enqueue(projectId, WebhookEventNames.DeploymentStatus, new
        {
            deployment = new
            {
                id = 7, status, action = "start", @ref, commitSha = "0123456789abcdef0123456789abcdef01234567", isRollback = false, pipelineId = 42,
                jobId = 9, jobName = job, url = $"http://localhost/p/x/pipelines/{status}-{Unique()}",
                user = new { id = 1, userName = "carol", displayName = "Carol Okafor" },
            },
            environment = new { id = 3, name = environment, externalUrl, isProtected = true },
            repository = new { id = repo.Id, slug = repo.Slug, name = repo.Name },
        });

    [Fact]
    public async Task Deployment_messages_announce_finished_deployments_only_and_follow_the_branch_filter()
    {
        var project = await app.CreateProjectAsync("carol");
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        var slackUrl = HookUrl();
        var teamsUrl = $"https://contoso.webhook.office.com/webhookb2/{Unique()}";
        var releaseOnly = HookUrl();
        int slackId, teamsId, filteredId;
        await using (var s = app.Scope(app.UserId("carol")))
        {
            var svc = s.Get<ChatIntegrationService>();
            slackId = (await svc.CreateAsync(project.Id, new ChatIntegrationInput { Kind = WebhookKind.Slack, Url = slackUrl, Events = [ChatEvents.Deployment] })).Id;
            teamsId = (await svc.CreateAsync(project.Id, new ChatIntegrationInput { Kind = WebhookKind.Teams, Url = teamsUrl, Events = [ChatEvents.Deployment] })).Id;
            filteredId = (await svc.CreateAsync(project.Id, new ChatIntegrationInput
                { Kind = WebhookKind.Slack, Url = releaseOnly, Events = [ChatEvents.Deployment], Branches = ["release/*"] })).Id;
        }

        // In progress: waiting for approval, queued, running: nothing is announced.
        foreach (var status in new[] { "waiting", "pending", "running" }) RaiseDeployment(project.Id, repo, status);
        RaiseDeployment(project.Id, repo, "success");
        var ok = await app.Fake.WaitForAsync(x => x.Url == slackUrl);
        var msg = JsonNode.Parse(ok.Body)!;
        Assert.Equal($"{repo.Slug} deployed main to production", msg["text"]!.GetValue<string>());
        var attachment = msg["attachments"]![0]!;
        Assert.Equal(SlackFormat.Color(ChatStatus.Good), attachment["color"]!.GetValue<string>());
        var blocks = attachment["blocks"]!.AsArray();
        Assert.Contains("production · " + repo.Slug, blocks[0]!["text"]!["text"]!.GetValue<string>());
        Assert.Contains("/pipelines/success-", blocks[0]!["text"]!["text"]!.GetValue<string>()); // links to the pipeline of the deployment
        var fields = string.Join("|", blocks[1]!["fields"]!.AsArray().Select(f => f!["text"]!.GetValue<string>().Replace("*", "").Replace("\n", "=")));
        Assert.Contains("Environment=production", fields);
        Assert.Contains("Status=Success", fields);
        Assert.Contains("Branch=main", fields);
        Assert.Contains("Commit=0123456", fields);
        Assert.Contains("Job=deploy", fields);
        Assert.Contains("https://prod.example.com", fields);

        RaiseDeployment(project.Id, repo, "failed", environment: "staging", externalUrl: null);
        var failed = await app.Fake.WaitForAsync(x => x.Url == slackUrl && x.Body.Contains("failed to deploy"));
        Assert.Equal($"{repo.Slug} failed to deploy main to staging", JsonNode.Parse(failed.Body)!["text"]!.GetValue<string>());
        Assert.Equal(SlackFormat.Color(ChatStatus.Danger), JsonNode.Parse(failed.Body)!["attachments"]![0]!["color"]!.GetValue<string>());
        RaiseDeployment(project.Id, repo, "canceled", environment: "staging");
        var canceled = await app.Fake.WaitForAsync(x => x.Url == slackUrl && x.Body.Contains("canceled deploying"));
        Assert.Equal(SlackFormat.Color(ChatStatus.Warning), JsonNode.Parse(canceled.Body)!["attachments"]![0]!["color"]!.GetValue<string>());

        // Teams gets the same message as an adaptive card.
        var card = await app.Fake.WaitForAsync(x => x.Url == teamsUrl && x.Body.Contains("deployed main to production"));
        Assert.Equal("AdaptiveCard", JsonNode.Parse(card.Body)!["attachments"]![0]!["content"]!["type"]!.GetValue<string>());

        // Exactly the three finished deployments were announced; the branch filter kept "main" out of the release/* integration.
        await Task.Delay(300);
        await using var db = app.Db();
        Assert.Equal(3, await db.WebhookDeliveries.CountAsync(d => d.WebhookId == slackId && d.Event == ChatEvents.Deployment));
        Assert.Equal(3, await db.WebhookDeliveries.CountAsync(d => d.WebhookId == teamsId));
        Assert.Equal(0, await db.WebhookDeliveries.CountAsync(d => d.WebhookId == filteredId));

        RaiseDeployment(project.Id, repo, "success", @ref: "refs/heads/release/1.2");
        var release = await app.Fake.WaitForAsync(x => x.Url == releaseOnly);
        Assert.Contains("deployed release/1.2 to production", release.Body);
    }

    [Fact]
    public async Task Deployment_messages_also_reach_group_integrations()
    {
        var group = await GroupAsync();
        var project = await ProjectInAsync(group);
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        var url = HookUrl();
        await AddGroupHookAsync(group, url, "carol", ChatEvents.Deployment);
        RaiseDeployment(project.Id, repo, "success", environment: "qa");
        var sent = await app.Fake.WaitForAsync(x => x.Url == url);
        Assert.Contains($"{repo.Slug} deployed main to qa", sent.Body);
    }
}
