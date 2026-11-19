using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Tests;

public class LinkedWorkTests(TestApp app) : IClassFixture<TestApp>
{
    [Theory]
    [InlineData(IssueType.Story, IssueType.Epic, true)]
    [InlineData(IssueType.Bug, IssueType.Epic, true)]
    [InlineData(IssueType.Subtask, IssueType.Story, true)]
    [InlineData(IssueType.Subtask, IssueType.Task, true)]
    [InlineData(IssueType.Subtask, IssueType.Epic, false)]
    [InlineData(IssueType.Subtask, IssueType.Subtask, false)]
    [InlineData(IssueType.Story, IssueType.Story, false)]
    [InlineData(IssueType.Task, IssueType.Bug, false)]
    [InlineData(IssueType.Epic, IssueType.Epic, false)]
    [InlineData(IssueType.Epic, IssueType.Story, false)]
    public void Parent_type_rules(IssueType child, IssueType parent, bool allowed) =>
        Assert.Equal(allowed, HierarchyRules.CanBeChildOf(child, parent));

    [Fact]
    public async Task Hierarchy_is_enforced_on_create_and_update()
    {
        var project = await app.CreateProjectAsync("carol");
        var carol = app.UserId("carol");
        var epic = await app.CreateIssueAsync(project.Id, "carol", "Epic", IssueType.Epic);
        var story = await app.CreateIssueAsync(project.Id, "carol", "Story", IssueType.Story, parentId: epic.Id);
        var sub = await app.CreateIssueAsync(project.Id, "carol", "Sub", IssueType.Subtask, parentId: story.Id);

        // Invalid creations.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => app.CreateIssueAsync(project.Id, "carol", "orphan", IssueType.Subtask));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => app.CreateIssueAsync(project.Id, "carol", "x", IssueType.Subtask, parentId: epic.Id));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => app.CreateIssueAsync(project.Id, "carol", "x", IssueType.Subtask, parentId: sub.Id));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => app.CreateIssueAsync(project.Id, "carol", "x", IssueType.Epic, parentId: epic.Id));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => app.CreateIssueAsync(project.Id, "carol", "x", IssueType.Task, parentId: story.Id));

        // Parent must be in the same project.
        var other = await app.CreateProjectAsync("carol");
        var otherEpic = await app.CreateIssueAsync(other.Id, "carol", "Other epic", IssueType.Epic);
        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => app.CreateIssueAsync(project.Id, "carol", "x", IssueType.Story, parentId: otherEpic.Id));
        Assert.Contains("same project", ex.Message);

        // Changing a type can be refused because of the children: a story with sub-tasks can't become an epic.
        await using var s = app.Scope(carol);
        var issues = s.Get<IssueService>();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => issues.UpdateIssueAsync(story.Id, carol, (i, _) =>
        {
            i.Type = IssueType.Epic;
            i.ParentId = null;
            return Task.FromResult<string?>("changed the type");
        }));
        // An epic can't move under its own story (cycle / epic with parent).
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => s.Get<LinkedWorkService>().SetParentAsync(epic.Id, story.Id, carol));

        // Deleting the story deletes its sub-task; deleting the epic keeps (unlinks) its children.
        var story2 = await app.CreateIssueAsync(project.Id, "carol", "Story 2", IssueType.Story, parentId: epic.Id);
        await issues.DeleteIssueAsync(story.Id);
        await issues.DeleteIssueAsync(epic.Id);
        await using var db = app.Db();
        Assert.False(await db.Issues.AnyAsync(i => i.Id == sub.Id));
        var kept = await db.Issues.AsNoTracking().FirstAsync(i => i.Id == story2.Id);
        Assert.Null(kept.ParentId);
    }

    [Fact]
    public async Task Links_are_validated_and_seen_from_both_sides()
    {
        var project = await app.CreateProjectAsync("carol");
        var carol = app.UserId("carol");
        var a = await app.CreateIssueAsync(project.Id, "carol", "A");
        var b = await app.CreateIssueAsync(project.Id, "carol", "B");
        await using var s = app.Scope(carol);
        var linked = s.Get<LinkedWorkService>();

        await linked.AddLinkAsync(a.Id, b.Id, IssueLinkRelation.Blocks, carol);
        Assert.Equal(IssueLinkRelation.Blocks, Assert.Single(await linked.GetLinksAsync(a.Id)).Relation);
        Assert.Equal(IssueLinkRelation.IsBlockedBy, Assert.Single(await linked.GetLinksAsync(b.Id)).Relation);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => linked.AddLinkAsync(a.Id, a.Id, IssueLinkRelation.RelatesTo, carol));
        // The same type again, in either direction, is refused.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => linked.AddLinkAsync(b.Id, a.Id, IssueLinkRelation.IsBlockedBy, carol));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => linked.AddLinkAsync(b.Id, a.Id, IssueLinkRelation.Blocks, carol));
        // A different type is fine.
        await linked.AddLinkAsync(b.Id, a.Id, IssueLinkRelation.RelatesTo, carol);
        Assert.Equal(2, (await linked.GetLinksAsync(a.Id)).Count);
    }

    [Fact]
    public async Task Cross_project_links_are_hidden_from_users_who_cannot_view_the_other_project()
    {
        // Public side: an Internal project everyone can see. Secret side: a Private project only carol is in.
        var open = await app.CreateProjectAsync("carol", ProjectVisibility.Internal, ("bob", ProjectRole.Developer));
        var secret = await app.CreateProjectAsync("carol", ProjectVisibility.Private);
        var visible = await app.CreateIssueAsync(open.Id, "carol", "Visible");
        var hidden = await app.CreateIssueAsync(secret.Id, "carol", "Top secret title");
        var carol = app.UserId("carol");
        var bob = app.UserId("bob");

        await using (var s = app.Scope(carol))
            await s.Get<LinkedWorkService>().AddLinkAsync(visible.Id, hidden.Id, IssueLinkRelation.RelatesTo, carol);

        await using (var s = app.Scope(carol))
            Assert.Single(await s.Get<LinkedWorkService>().GetLinksAsync(visible.Id));
        await using (var s = app.Scope(bob))
        {
            Assert.Empty(await s.Get<LinkedWorkService>().GetLinksAsync(visible.Id));
            // bob can't link to it either (and isn't told whether it exists).
            var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
                s.Get<LinkedWorkService>().AddLinkAsync(visible.Id, hidden.Id, IssueLinkRelation.Blocks, bob));
            Assert.Contains("can't view it", ex.Message);
        }

        // Same through the API, and the other issue's title never appears in the response.
        var client = await app.ApiClientAsync("bob");
        var r = await client.GetAsync($"/api/v1/projects/{open.Key}/issues/{visible.Number}/links");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Equal(0, JsonDocument.Parse(body).RootElement.GetArrayLength());
        Assert.DoesNotContain("Top secret", body);
        Assert.Equal("0", r.Headers.GetValues("X-Total-Count").Single());

        var add = await client.PostAsJsonAsync($"/api/v1/projects/{open.Key}/issues/{visible.Number}/links",
            new { type = "blocks", issue = $"{secret.Key}-{hidden.Number}" });
        Assert.True(add.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity, $"got {(int)add.StatusCode}");

        // carol, who can view both projects, still gets the link from the API.
        var carolClient = await app.ApiClientAsync("carol");
        var links = JsonDocument.Parse(await carolClient.GetStringAsync($"/api/v1/projects/{open.Key}/issues/{visible.Number}/links")).RootElement;
        Assert.Equal("relatesTo", links[0].GetProperty("type").GetString());
    }
}
