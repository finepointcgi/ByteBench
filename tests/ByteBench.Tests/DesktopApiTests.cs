using System.Net;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Tests.Infrastructure;

namespace ByteBench.Tests;

/// <summary>The endpoints the desktop app relies on: anonymous server-info and the caller's own notifications.</summary>
public class DesktopApiTests(TestApp app) : IClassFixture<TestApp>
{
    private async Task<int> AddNotificationAsync(string user, string title, string url = "/p/X/issues/1")
    {
        await using var db = app.Db();
        var n = new Notification { UserId = app.UserId(user), ProjectId = app.ProjectId(await EnsureProjectKeyAsync()), Kind = NotificationKind.Assigned, Title = title, Url = url };
        db.Notifications.Add(n);
        await db.SaveChangesAsync();
        return n.Id;
    }

    private async Task<string> EnsureProjectKeyAsync()
    {
        await using var db = app.Db();
        var key = db.Projects.Select(p => p.Key).FirstOrDefault();
        return key ?? (await app.CreateProjectAsync("alice")).Key;
    }

    [Fact]
    public async Task ServerInfo_is_anonymous_and_has_no_secrets()
    {
        using var anon = app.CreateClient();
        var res = await anon.GetAsync("/api/v1/server-info");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("ByteBench", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("apiVersion").GetInt32());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("version").GetString()));
        Assert.Contains("notifications", doc.RootElement.GetProperty("capabilities").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Notifications_need_authentication()
    {
        using var anon = app.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/notifications")).StatusCode);
    }

    [Fact]
    public async Task Notifications_list_only_the_callers_and_support_afterId_and_unread()
    {
        var mine = await AddNotificationAsync("bob", "for bob");
        var theirs = await AddNotificationAsync("carol", "for carol");
        var evil = await AddNotificationAsync("bob", "bad url", "https://evil.example/x");
        using var bob = await app.ApiClientAsync("bob");

        using var all = JsonDocument.Parse(await bob.GetStringAsync("/api/v1/notifications?limit=100"));
        var ids = all.RootElement.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetInt32()).ToList();
        Assert.Contains(mine, ids);
        Assert.DoesNotContain(theirs, ids);
        var bad = all.RootElement.GetProperty("items").EnumerateArray().First(e => e.GetProperty("id").GetInt32() == evil);
        Assert.StartsWith("/", bad.GetProperty("url").GetString()); // absolute URLs are never handed out

        using var after = JsonDocument.Parse(await bob.GetStringAsync($"/api/v1/notifications?afterId={mine}"));
        Assert.All(after.RootElement.GetProperty("items").EnumerateArray(), e => Assert.True(e.GetProperty("id").GetInt32() > mine));

        // another user's notification cannot be marked read
        Assert.Equal(HttpStatusCode.NoContent, (await bob.PostAsync($"/api/v1/notifications/{theirs}/read", null)).StatusCode);
        await using (var db = app.Db()) Assert.False(db.Notifications.Single(n => n.Id == theirs).IsRead);
        Assert.Equal(HttpStatusCode.NoContent, (await bob.PostAsync($"/api/v1/notifications/{mine}/read", null)).StatusCode);
        await using (var db = app.Db()) Assert.True(db.Notifications.Single(n => n.Id == mine).IsRead);
    }
}
