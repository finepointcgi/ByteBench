using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using GitBench.Data;
using GitBench.Services;
using GitBench.Services.Audit;
using GitBench.Services.Webhooks;
using GitBench.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Tests;

public class AuditTests(TestApp app) : IClassFixture<TestApp>
{
    private async Task<List<AuditEvent>> EventsAsync(int? projectId = null, string? prefix = null)
    {
        await using var db = app.Db();
        var q = db.AuditEvents.AsNoTracking().AsQueryable();
        if (projectId != null) q = q.Where(e => e.ProjectId == projectId);
        if (prefix != null) q = q.Where(e => e.Action.StartsWith(prefix));
        return await q.OrderBy(e => e.Id).ToListAsync();
    }

    private static JsonElement Details(AuditEvent e) => JsonDocument.Parse(e.Details ?? "{}").RootElement;

    [Fact]
    public async Task Repository_lifecycle_is_audited_with_the_acting_user()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("dan", ProjectRole.Maintainer));
        var repo = await app.CreateRepoAsync(project.Id, "dan");
        await using (var s = app.Scope(app.UserId("dan")))
        {
            var settings = s.Get<RepositorySettingsService>();
            await settings.RenameAsync(repo.Id, "audited");
            await settings.SetArchivedAsync(repo.Id, true);
            await settings.SetArchivedAsync(repo.Id, true); // no change, no event
            await settings.SetArchivedAsync(repo.Id, false);
            await settings.DeleteAsync(repo.Id, repo.Name);
        }

        var events = await EventsAsync(project.Id, "repository.");
        Assert.Equal(["repository.created", "repository.renamed", "repository.archived", "repository.unarchived", "repository.deleted"],
            events.Select(e => e.Action));
        Assert.All(events, e =>
        {
            Assert.Equal("dan", e.ActorName);
            Assert.Equal("repository", e.TargetType);
            Assert.Equal(repo.Id.ToString(), e.TargetId);
        });
        var renamed = Details(events[1]);
        Assert.Equal((repo.Slug, "audited"), (renamed.GetProperty("from").GetString(), renamed.GetProperty("to").GetString()));
        Assert.Equal("audited", Details(events[4]).GetProperty("slug").GetString());
    }

    [Fact]
    public async Task Branch_protection_changes_and_bypassed_merges_are_audited()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        app.Commit(repo, "feature/p", "p.txt", "p");
        var carol = app.UserId("carol");
        await using var s = app.Scope(carol);
        var protection = s.Get<BranchProtectionService>();

        var rule = await protection.SaveAsync(repo.Id, new BranchProtectionRule { Pattern = "main", RequiredApprovals = 1 });
        rule.AllowAdminBypass = true;
        await protection.SaveAsync(repo.Id, rule);
        var temp = await protection.SaveAsync(repo.Id, new BranchProtectionRule { Pattern = "release/*" });
        await protection.DeleteAsync(repo.Id, temp.Id);

        // carol is the project Owner, so "allow admins to bypass" lets her merge without the approval.
        var pr = await s.Get<PullRequestService>().CreateAsync(repo, "P", null, "feature/p", "main", app.UserId("bob"));
        var outcome = await s.Get<PullRequestService>().MergeAsync(pr.Id, app.User("carol"), false, false);
        Assert.True(outcome.Success);

        var events = await EventsAsync(project.Id, "branch_protection.");
        Assert.Equal(["branch_protection.created", "branch_protection.updated", "branch_protection.created", "branch_protection.deleted"],
            events.Select(e => e.Action));
        Assert.True(Details(events[1]).GetProperty("allowAdminBypass").GetBoolean());
        Assert.Equal("release/*", Details(events[3]).GetProperty("pattern").GetString());
        Assert.All(events, e => Assert.Equal("carol", e.ActorName));

        var bypass = Assert.Single(await EventsAsync(project.Id, "pull_request.merged_bypassing_protection"));
        Assert.Equal("carol", bypass.ActorName);
        Assert.Equal(pr.Id.ToString(), bypass.TargetId);
        Assert.Equal("main", Details(bypass).GetProperty("targetBranch").GetString());
        await using var db = app.Db();
        Assert.True(await db.PullRequestActivities.AnyAsync(a => a.PullRequestId == pr.Id && a.Text.StartsWith("bypassed branch protection")));
    }

    [Fact]
    public async Task A_merge_that_needs_no_bypass_is_not_audited_as_one()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("dan", ProjectRole.Developer));
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        app.Commit(repo, "feature/q", "q.txt", "q");
        await using var s = app.Scope(app.UserId("carol"));
        await s.Get<BranchProtectionService>().SaveAsync(repo.Id, new BranchProtectionRule { Pattern = "main", RequiredApprovals = 1, AllowAdminBypass = true });
        var pr = await s.Get<PullRequestService>().CreateAsync(repo, "Q", null, "feature/q", "main", app.UserId("bob"));
        await s.Get<PullRequestService>().SetReviewAsync(pr.Id, app.UserId("dan"), ReviewState.Approved);
        Assert.True((await s.Get<PullRequestService>().MergeAsync(pr.Id, app.User("carol"), false, false)).Success);
        Assert.Empty(await EventsAsync(project.Id, "pull_request.merged_bypassing_protection"));
    }

    [Fact]
    public async Task Project_deletion_is_audited()
    {
        var project = await app.CreateProjectAsync("carol");
        await app.CreateIssueAsync(project.Id, "carol");
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<IssueService>().DeleteProjectAsync(project.Id);
        var events = await EventsAsync(project.Id, "project.");
        Assert.Equal(["project.created", "project.deleted"], events.Select(e => e.Action));
        var deleted = events[1];
        Assert.Equal("carol", deleted.ActorName);
        Assert.Equal(project.Key, Details(deleted).GetProperty("key").GetString());
        Assert.Equal(1, Details(deleted).GetProperty("issues").GetInt32());
    }

    [Fact]
    public async Task Secrets_never_reach_the_audit_log()
    {
        var project = await app.CreateProjectAsync("carol");
        var carol = app.UserId("carol");
        string token, hookSecret = "hook-secret-0123456789abcdef";
        await using (var s = app.Scope(carol))
        {
            token = (await s.Get<AccessTokenService>().CreateAsync(carol, "ci", TokenScopes.ReadApi, null)).Plaintext;
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => // credentials in the URL are refused
                s.Get<WebhookService>().CreateAsync(project.Id, "https://user:pw@hooks.example.com/path?key=abc123", null, [WebhookEventNames.Push], true));
            var hook = await s.Get<WebhookService>().CreateAsync(project.Id, "https://hooks.example.com/in/abc123?sig=xyz", hookSecret, [WebhookEventNames.Push], true);
            await s.Get<WebhookService>().RegenerateSecretAsync(project.Id, hook.Id);
            // The backstop: keys that look like secrets are dropped even if a caller passes them.
            await s.Get<AuditLog>().RecordAsync("test.details", details: new
            {
                password = "p4ss", newPassword = "x", webhookSecret = "y", token = "z", tokenHash = "h", apiKey = "k",
                nested = new { clientSecret = "c", ok = "kept" }, safe = "kept",
            });
        }

        // A failed sign-in records the login, never the password tried.
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var page = await client.GetStringAsync("/account/login");
        var csrf = Regex.Match(page, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        const string wrongPassword = "Wrong-Password-987";
        var r = await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "login", ["__RequestVerificationToken"] = csrf,
            ["Input.UserNameOrEmail"] = "dan", ["Input.Password"] = wrongPassword,
        }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode); // the form again, with an error

        var all = await EventsAsync();
        var text = string.Join("\n", all.Select(e => e.Details));
        Assert.DoesNotContain(token, text);
        Assert.DoesNotContain(token[..10], text); // not even the display prefix
        Assert.DoesNotContain(hookSecret, text);
        Assert.DoesNotContain("abc123", text);    // webhook URLs are logged as scheme + host
        Assert.DoesNotContain("xyz", text);
        Assert.DoesNotContain(wrongPassword, text);
        Assert.DoesNotContain(SeedData.DemoPassword, text);

        var details = Details(all.Last(e => e.Action == "test.details"));
        Assert.Equal(["nested", "safe"], details.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(["ok"], details.GetProperty("nested").EnumerateObject().Select(p => p.Name));

        Assert.Contains(all, e => e.Action == "token.created" && e.ActorName == "carol");
        Assert.Contains(all, e => e.Action == "webhook.secret_rotated");
        var hookEvent = all.Last(e => e.Action == "webhook.created");
        Assert.Equal("https://hooks.example.com", Details(hookEvent).GetProperty("host").GetString());
        var failed = all.Last(e => e.Action == "auth.sign_in_failed");
        Assert.Null(failed.ActorId);
        Assert.Equal("dan", Details(failed).GetProperty("login").GetString());
    }

    [Fact]
    public async Task Audit_log_is_readable_only_by_admins_and_project_owners()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("dan", ProjectRole.Maintainer));
        await using (var s = app.Scope(app.UserId("carol")))
        {
            Assert.NotEmpty((await s.Get<AuditLog>().SearchAsync(new AuditQuery { ProjectId = project.Id })).Events);
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<AuditLog>().SearchAsync(new AuditQuery()));
        }
        await using (var s = app.Scope(app.UserId("dan")))
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<AuditLog>().SearchAsync(new AuditQuery { ProjectId = project.Id }));
        await using (var s = app.Scope(app.UserId("alice")))
            Assert.NotEmpty((await s.Get<AuditLog>().SearchAsync(new AuditQuery { ActionPrefix = "project." })).Events);

        // Audit rows are append-only.
        await using var db = app.Db();
        var e = await db.AuditEvents.FirstAsync();
        e.Action = "tampered";
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
}
