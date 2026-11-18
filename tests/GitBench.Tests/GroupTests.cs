using System.Net;
using System.Text.Json;
using GitBench.Data;
using GitBench.Services;
using GitBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Tests;

public class GroupTests(TestApp app) : IClassFixture<TestApp>
{
    private static int counter;
    private static string Unique(string prefix) => $"{prefix}{Interlocked.Increment(ref counter)}";

    private async Task<Group> CreateGroupAsync(string owner, int? parentId = null, ProjectVisibility visibility = ProjectVisibility.Private, string? path = null)
    {
        await using var s = app.Scope(app.UserId(owner));
        path ??= Unique("g");
        return await s.Get<GroupService>().CreateAsync(path.ToUpperInvariant(), path, null, visibility, null, parentId);
    }

    private async Task AddGroupMemberAsync(Group g, string actor, string user, ProjectRole role)
    {
        await using var s = app.Scope(app.UserId(actor));
        await s.Get<GroupService>().AddMemberAsync(g.Id, user, role);
    }

    private async Task<Project> CreateProjectInAsync(Group g, string owner, ProjectVisibility visibility = ProjectVisibility.Private)
    {
        var key = "G" + Interlocked.Increment(ref counter).ToString("D3");
        await using var s = app.Scope(app.UserId(owner));
        return await s.Get<IssueService>().CreateProjectAsync(new Project { Key = key, Name = "Grouped " + key, Visibility = visibility, GroupId = g.Id });
    }

    // ---------- Effective roles ----------

    [Fact]
    public async Task Group_roles_are_inherited_by_nested_subgroups_and_their_projects()
    {
        var top = await CreateGroupAsync("carol");
        var mid = await CreateGroupAsync("carol", top.Id);
        var leaf = await CreateGroupAsync("carol", mid.Id);
        Assert.Equal(3, leaf.Depth);
        var project = await CreateProjectInAsync(leaf, "carol");
        var dev = await app.CreateUserAsync(Unique("dev"));
        var outsider = await app.CreateUserAsync(Unique("out"));
        await AddGroupMemberAsync(top, "carol", dev.UserName!, ProjectRole.Developer);

        await using var s = app.Scope();
        var access = s.Get<ProjectAccess>();
        Assert.Equal(ProjectRole.Developer, await access.GetRoleAsync(project.Id, dev.Id));
        Assert.Equal(ProjectRole.Developer, await access.GetGroupRoleAsync(leaf.Id, dev.Id));
        Assert.Null(await access.GetRoleAsync(project.Id, outsider.Id));
        Assert.Contains(project.Id, await access.VisibleProjectIdsAsync(dev.Id));
        Assert.DoesNotContain(project.Id, await access.VisibleProjectIdsAsync(outsider.Id));
        Assert.True(await access.CanAsync(project.Id, Permission.PushCode, dev.Id));
        Assert.False(await access.CanAsync(project.Id, Permission.MergePullRequest, dev.Id));
        // carol created every group and the project: Owner of each, directly
        Assert.Equal(ProjectRole.Owner, await access.GetGroupRoleAsync(leaf.Id, app.UserId("carol")));
        // site admin: Owner without any membership
        Assert.Equal(ProjectRole.Owner, await access.GetGroupRoleAsync(leaf.Id, app.UserId("alice")));
        Assert.Equal(ProjectRole.Owner, await access.GetRoleAsync(project.Id, app.UserId("alice")));
    }

    [Fact]
    public async Task Effective_role_is_the_highest_of_project_group_and_internal_roles()
    {
        var top = await CreateGroupAsync("carol");
        var sub = await CreateGroupAsync("carol", top.Id);
        var project = await CreateProjectInAsync(sub, "carol", ProjectVisibility.Internal);
        var u = await app.CreateUserAsync(Unique("mix"));
        var outsider = await app.CreateUserAsync(Unique("out"));
        await using var s = app.Scope();
        var access = s.Get<ProjectAccess>();

        Assert.Equal(ProjectRole.Viewer, await access.GetRoleAsync(project.Id, outsider.Id)); // Internal

        await AddGroupMemberAsync(sub, "carol", u.UserName!, ProjectRole.Developer);
        Assert.Equal(ProjectRole.Developer, await access.GetRoleAsync(project.Id, u.Id)); // group beats Internal

        await using (var s2 = app.Scope(app.UserId("carol")))
            await s2.Get<MemberService>().AddMemberAsync(project.Id, u.UserName!, ProjectRole.Viewer);
        Assert.Equal(ProjectRole.Developer, await access.GetRoleAsync(project.Id, u.Id)); // lower direct role doesn't demote

        await AddGroupMemberAsync(top, "carol", u.UserName!, ProjectRole.Maintainer);
        Assert.Equal(ProjectRole.Maintainer, await access.GetRoleAsync(project.Id, u.Id)); // higher ancestor role wins
        Assert.Equal(ProjectRole.Maintainer, await access.GetGroupRoleAsync(sub.Id, u.Id));

        await using (var s3 = app.Scope(app.UserId("carol")))
            await s3.Get<MemberService>().ChangeRoleAsync(project.Id, u.Id, ProjectRole.Owner);
        Assert.Equal(ProjectRole.Owner, await access.GetRoleAsync(project.Id, u.Id)); // higher direct role wins
    }

    [Fact]
    public async Task Inherited_members_are_listed_on_the_project_with_their_source()
    {
        var top = await CreateGroupAsync("carol");
        var sub = await CreateGroupAsync("carol", top.Id);
        var project = await CreateProjectInAsync(sub, "carol");
        await AddGroupMemberAsync(top, "carol", "bob", ProjectRole.Developer);
        await using var s = app.Scope(app.UserId("bob"));
        var inherited = await s.Get<GroupService>().GetInheritedProjectMembersAsync(project.Id);
        var bob = Assert.Single(inherited, m => m.User.UserName == "bob");
        Assert.Equal(top.FullPath, bob.GroupPath);
        Assert.Equal(ProjectRole.Developer, bob.Role);
        Assert.Contains(inherited, m => m.User.UserName == "carol" && m.GroupPath == sub.FullPath);
    }

    [Fact]
    public async Task Groups_nest_at_most_four_levels()
    {
        var g = await CreateGroupAsync("carol");
        for (var i = 0; i < 3; i++) g = await CreateGroupAsync("carol", g.Id);
        Assert.Equal(Group.MaxDepth, g.Depth);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateGroupAsync("carol", g.Id));
    }

    [Fact]
    public async Task Creating_in_a_group_needs_maintainer()
    {
        var g = await CreateGroupAsync("carol");
        var dev = await app.CreateUserAsync(Unique("dev"));
        var maint = await app.CreateUserAsync(Unique("mnt"));
        await AddGroupMemberAsync(g, "carol", dev.UserName!, ProjectRole.Developer);
        await AddGroupMemberAsync(g, "carol", maint.UserName!, ProjectRole.Maintainer);

        await Assert.ThrowsAsync<ForbiddenException>(() => CreateProjectInAsync(g, dev.UserName!));
        await Assert.ThrowsAsync<ForbiddenException>(() => CreateGroupAsync(dev.UserName!, g.Id));
        var p = await CreateProjectInAsync(g, maint.UserName!);
        Assert.Equal(g.Id, p.GroupId);
        var sub = await CreateGroupAsync(maint.UserName!, g.Id);
        Assert.Equal(g.Id, sub.ParentId);
    }

    // ---------- Visibility ----------

    [Fact]
    public async Task Group_lists_show_only_groups_the_user_can_see()
    {
        var priv = await CreateGroupAsync("carol");
        var privA = await CreateGroupAsync("carol", priv.Id);
        var privB = await CreateGroupAsync("carol", priv.Id);
        var deep = await CreateGroupAsync("carol", privA.Id);
        var open = await CreateGroupAsync("carol", visibility: ProjectVisibility.Internal);
        var subMember = await app.CreateUserAsync(Unique("sub"));
        var topMember = await app.CreateUserAsync(Unique("top"));
        var projMember = await app.CreateUserAsync(Unique("prj"));
        var outsider = await app.CreateUserAsync(Unique("out"));
        await AddGroupMemberAsync(privA, "carol", subMember.UserName!, ProjectRole.Viewer);
        await AddGroupMemberAsync(priv, "carol", topMember.UserName!, ProjectRole.Viewer);
        var project = await CreateProjectInAsync(privB, "carol");
        await using (var sm = app.Scope(app.UserId("carol")))
            await sm.Get<MemberService>().AddMemberAsync(project.Id, projMember.UserName!, ProjectRole.Viewer);

        async Task<HashSet<int>> Visible(User u)
        {
            await using var s = app.Scope(u.Id);
            return (await s.Get<GroupService>().ListAsync()).Select(g => g.Id).ToHashSet();
        }

        var o = await Visible(outsider);
        Assert.Contains(open.Id, o);
        Assert.DoesNotContain(priv.Id, o);
        Assert.DoesNotContain(privA.Id, o);

        var sub = await Visible(subMember); // its group, the groups below it, and its ancestors; not the sibling
        Assert.Contains(privA.Id, sub);
        Assert.Contains(deep.Id, sub);
        Assert.Contains(priv.Id, sub);
        Assert.DoesNotContain(privB.Id, sub);

        var top = await Visible(topMember); // everything under it
        Assert.True(new[] { priv.Id, privA.Id, privB.Id, deep.Id }.All(top.Contains));

        var pm = await Visible(projMember); // the project's group chain
        Assert.Contains(privB.Id, pm);
        Assert.Contains(priv.Id, pm);
        Assert.DoesNotContain(privA.Id, pm);

        await using var s2 = app.Scope(outsider.Id);
        Assert.Null((await s2.Get<GroupService>().FindAsync(priv.FullPath)).Group); // not found, not forbidden
        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => s2.Get<ProjectAccess>().RequireAsync(priv.Id, GroupPermission.ViewGroup));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task Group_issues_aggregate_only_visible_projects()
    {
        var g = await CreateGroupAsync("carol");
        var sub = await CreateGroupAsync("carol", g.Id);
        var a = await CreateProjectInAsync(g, "carol");
        var b = await CreateProjectInAsync(sub, "carol");
        await app.CreateIssueAsync(a.Id, "carol", "in A");
        await app.CreateIssueAsync(b.Id, "carol", "in B");
        var viewer = await app.CreateUserAsync(Unique("v"));
        await using (var sm = app.Scope(app.UserId("carol")))
            await sm.Get<MemberService>().AddMemberAsync(a.Id, viewer.UserName!, ProjectRole.Viewer);

        await using var carol = app.Scope(app.UserId("carol"));
        var all = await carol.Get<GroupService>().GetIssuesAsync(g.Id, new IssueFilter());
        Assert.Equal(["in A", "in B"], all.Select(i => i.Title).Order());

        await using var v = app.Scope(viewer.Id);
        var mine = await v.Get<GroupService>().GetIssuesAsync(g.Id, new IssueFilter());
        Assert.Equal("in A", Assert.Single(mine).Title);
    }

    // ---------- Transfers ----------

    [Fact]
    public async Task Transfer_needs_project_owner_and_maintainer_in_the_target()
    {
        var source = await CreateGroupAsync("carol");
        var target = await CreateGroupAsync("alice");
        var project = await CreateProjectInAsync(source, "carol");
        var dan = app.UserId("dan");
        await using (var sm = app.Scope(app.UserId("carol")))
            await sm.Get<MemberService>().AddMemberAsync(project.Id, "dan", ProjectRole.Maintainer);

        // Owner of the project, but not a member of the target group
        await using (var s = app.Scope(app.UserId("carol")))
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<GroupService>().TransferProjectAsync(project.Id, target.Id));
        // Maintainer of the target, but only Maintainer (not Owner) of the project
        await AddGroupMemberAsync(target, "alice", "dan", ProjectRole.Maintainer);
        await using (var s = app.Scope(dan))
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<GroupService>().TransferProjectAsync(project.Id, target.Id));
        // Developer in the target isn't enough either
        await AddGroupMemberAsync(target, "alice", "carol", ProjectRole.Developer);
        await using (var s = app.Scope(app.UserId("carol")))
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<GroupService>().TransferProjectAsync(project.Id, target.Id));

        await using (var s = app.Scope(app.UserId("alice")))
            await s.Get<GroupService>().ChangeMemberRoleAsync(target.Id, app.UserId("carol"), ProjectRole.Maintainer);
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<GroupService>().TransferProjectAsync(project.Id, target.Id);

        await using var db = app.Db();
        Assert.Equal(target.Id, (await db.Projects.FindAsync(project.Id))!.GroupId);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "project.transferred" && e.ProjectId == project.Id));
        // Key and URL unchanged; Owner can move it back out to the top level
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<GroupService>().TransferProjectAsync(project.Id, null);
        await using var db2 = app.Db();
        Assert.Null((await db2.Projects.FindAsync(project.Id))!.GroupId);
        Assert.Equal(project.Key, (await db2.Projects.FindAsync(project.Id))!.Key);
    }

    [Fact]
    public async Task Group_labels_are_inherited_and_copied_into_a_project_that_leaves()
    {
        var g = await CreateGroupAsync("carol");
        var sub = await CreateGroupAsync("carol", g.Id);
        var project = await CreateProjectInAsync(sub, "carol");
        var carol = app.UserId("carol");
        Label label;
        await using (var s = app.Scope(carol))
        {
            label = await s.Get<GroupService>().SaveLabelAsync(g.Id, 0, "shared", "#123456");
            await s.Get<GroupService>().SaveMilestoneAsync(g.Id, new Milestone { Title = "Group M1" });
            var p = (await s.Get<IssueService>().GetProjectAsync(project.Key))!;
            Assert.Contains(p.Labels, l => l.Id == label.Id);
            Assert.Contains(p.Milestones, m => m.Title == "Group M1");
            var milestoneId = p.Milestones.First(m => m.Title == "Group M1").Id;
            var issue = await s.Get<IssueService>().CreateIssueAsync(project.Id, new Issue { Title = "labelled", MilestoneId = milestoneId }, [label.Id], carol);
            // A project can't edit a group label through the project API
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<IssueService>().SaveLabelAsync(new Label { Id = label.Id, ProjectId = project.Id, Name = "x" }));
            await s.Get<GroupService>().TransferProjectAsync(project.Id, null);
        }
        await using var db = app.Db();
        var moved = await db.Issues.Include(i => i.Labels).Include(i => i.Milestone).FirstAsync(i => i.ProjectId == project.Id);
        var copy = Assert.Single(moved.Labels);
        Assert.Equal("shared", copy.Name);
        Assert.Equal(project.Id, copy.ProjectId);
        Assert.Equal(project.Id, moved.Milestone!.ProjectId);
        Assert.Equal("Group M1", moved.Milestone.Title);
    }

    // ---------- Paths ----------

    [Fact]
    public async Task Changing_a_path_redirects_the_old_paths_of_the_group_and_its_subgroups()
    {
        var top = await CreateGroupAsync("carol");
        var sub = await CreateGroupAsync("carol", top.Id);
        var oldTop = top.FullPath;
        var oldSub = sub.FullPath;
        var newPath = Unique("renamed");
        await using var s = app.Scope(app.UserId("carol"));
        var groups = s.Get<GroupService>();
        await groups.ChangePathAsync(top.Id, newPath);

        Assert.Equal(newPath, (await groups.FindAsync(oldTop)).RedirectTo);
        Assert.Equal($"{newPath}/{sub.Path}", (await groups.FindAsync(oldSub)).RedirectTo);
        Assert.Equal(sub.Id, (await groups.FindAsync($"{newPath}/{sub.Path}")).Group?.Id);

        // A redirect is only followed by users who can see the group
        var outsider = await app.CreateUserAsync(Unique("out"));
        await using (var o = app.Scope(outsider.Id))
            Assert.Null((await o.Get<GroupService>().FindAsync(oldTop)).RedirectTo);

        // A new group can reclaim the old path; then it no longer redirects
        var reclaimed = await CreateGroupAsync("carol", path: oldTop);
        var found = await groups.FindAsync(oldTop);
        Assert.Equal(reclaimed.Id, found.Group?.Id);
        Assert.Null(found.RedirectTo);

        await using var db = app.Db();
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "group.path_changed" && e.TargetId == top.Id.ToString()));
    }

    [Fact]
    public async Task Moving_a_group_repaths_its_subtree_and_respects_the_depth_limit()
    {
        var a = await CreateGroupAsync("carol");
        var b = await CreateGroupAsync("carol");
        var b1 = await CreateGroupAsync("carol", b.Id);
        var b2 = await CreateGroupAsync("carol", b1.Id);
        await using var s = app.Scope(app.UserId("carol"));
        var groups = s.Get<GroupService>();
        await groups.MoveAsync(b.Id, a.Id);
        Assert.Equal($"{a.FullPath}/{b.Path}/{b1.Path}/{b2.Path}", (await groups.GetAsync(b2.Id))!.FullPath);
        Assert.Equal($"{a.FullPath}/{b.Path}/{b1.Path}", (await groups.FindAsync(b1.FullPath)).RedirectTo);
        await Assert.ThrowsAsync<InvalidOperationException>(() => groups.MoveAsync(a.Id, b2.Id)); // into its own subtree

        var deep = await CreateGroupAsync("carol");
        var deep2 = await CreateGroupAsync("carol", deep.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => groups.MoveAsync(deep.Id, b1.Id)); // would be 5 deep
        _ = deep2;
    }

    [Fact]
    public async Task Deleting_a_group_with_content_needs_cascade_and_its_path()
    {
        var g = await CreateGroupAsync("carol");
        var sub = await CreateGroupAsync("carol", g.Id);
        var project = await CreateProjectInAsync(sub, "carol");
        await using var s = app.Scope(app.UserId("carol"));
        var groups = s.Get<GroupService>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => groups.DeleteAsync(g.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => groups.DeleteAsync(g.Id, cascade: true, confirmPath: "wrong"));

        var dev = await app.CreateUserAsync(Unique("dev"));
        await AddGroupMemberAsync(g, "carol", dev.UserName!, ProjectRole.Maintainer);
        await using (var d = app.Scope(dev.Id))
            await Assert.ThrowsAsync<ForbiddenException>(() => d.Get<GroupService>().DeleteAsync(g.Id, true, g.FullPath));

        await groups.DeleteAsync(g.Id, cascade: true, confirmPath: g.FullPath);
        await using var db = app.Db();
        Assert.False(await db.Groups.AnyAsync(x => x.Id == g.Id || x.Id == sub.Id));
        Assert.False(await db.Projects.AnyAsync(p => p.Id == project.Id));
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "group.deleted" && e.TargetId == g.Id.ToString()));

        var empty = await CreateGroupAsync("carol");
        await groups.DeleteAsync(empty.Id);
    }

    [Fact]
    public async Task Top_level_group_keeps_an_owner()
    {
        var g = await CreateGroupAsync("carol");
        await using var s = app.Scope(app.UserId("carol"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<GroupService>().RemoveMemberAsync(g.Id, app.UserId("carol")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<GroupService>().ChangeMemberRoleAsync(g.Id, app.UserId("carol"), ProjectRole.Developer));
    }

    // ---------- Seed & API ----------

    [Fact]
    public async Task Seed_puts_web_and_ops_in_acme_platform_with_dan_as_group_developer()
    {
        await using var db = app.Db();
        var platform = await db.Groups.Include(g => g.Parent).FirstAsync(g => g.FullPath == "acme/platform");
        Assert.Equal("acme", platform.Parent!.FullPath);
        Assert.Equal(platform.Id, (await db.Projects.FirstAsync(p => p.Key == "WEB")).GroupId);
        Assert.Equal(platform.Id, (await db.Projects.FirstAsync(p => p.Key == "OPS")).GroupId);
        Assert.Equal(ProjectRole.Developer, (await db.GroupMembers.FirstAsync(m => m.GroupId == platform.ParentId && m.User.UserName == "dan")).Role);

        await using var s = app.Scope();
        var access = s.Get<ProjectAccess>();
        Assert.Equal(ProjectRole.Maintainer, await access.GetRoleAsync(app.ProjectId("OPS"), app.UserId("dan"))); // direct beats group
        Assert.Null(await access.GetRoleAsync(app.ProjectId("OPS"), app.UserId("bob"))); // Internal group doesn't open a Private project
        Assert.True(await access.CanAsync(platform.Id, GroupPermission.ViewGroup, app.UserId("bob"))); // but the Internal group is visible
    }

    [Fact]
    public async Task Api_lists_groups_projects_and_issues()
    {
        var client = await app.ApiClientAsync("carol");
        var groups = JsonDocument.Parse(await client.GetStringAsync("/api/v1/groups")).RootElement;
        Assert.Contains(groups.EnumerateArray(), g => g.GetProperty("fullPath").GetString() == "acme/platform");

        var g = JsonDocument.Parse(await client.GetStringAsync("/api/v1/groups/acme%2Fplatform")).RootElement;
        Assert.Equal("Platform", g.GetProperty("name").GetString());

        var projects = JsonDocument.Parse(await client.GetStringAsync("/api/v1/groups/acme%2Fplatform/projects")).RootElement;
        Assert.Equal(["OPS", "WEB"], projects.EnumerateArray().Select(p => p.GetProperty("key").GetString()!).Order());
        var none = JsonDocument.Parse(await client.GetStringAsync("/api/v1/groups/acme/projects")).RootElement;
        Assert.Empty(none.EnumerateArray());
        var nested = JsonDocument.Parse(await client.GetStringAsync("/api/v1/groups/acme/projects?includeSubgroups=true")).RootElement;
        Assert.Equal(2, nested.GetArrayLength());

        var issues = JsonDocument.Parse(await client.GetStringAsync("/api/v1/groups/acme/issues?status=all&perPage=100")).RootElement;
        var keys = issues.EnumerateArray().Select(i => i.GetProperty("key").GetString()!).ToList();
        Assert.Contains(keys, k => k.StartsWith("WEB-"));
        Assert.Contains(keys, k => k.StartsWith("OPS-"));

        // bob can't see OPS: the group issue list leaves it out
        var bob = await app.ApiClientAsync("bob");
        var bobIssues = JsonDocument.Parse(await bob.GetStringAsync("/api/v1/groups/acme/issues?status=all&perPage=100")).RootElement;
        Assert.DoesNotContain(bobIssues.EnumerateArray(), i => i.GetProperty("key").GetString()!.StartsWith("OPS-"));

        var priv = await CreateGroupAsync("carol");
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/v1/groups/{priv.Id}")).StatusCode);
    }
}
