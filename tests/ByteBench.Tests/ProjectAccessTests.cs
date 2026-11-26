using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Tests.Infrastructure;

namespace ByteBench.Tests;

public class ProjectAccessTests(TestApp app) : IClassFixture<TestApp>
{
    public static TheoryData<ProjectRole, Permission, bool> Matrix()
    {
        var data = new TheoryData<ProjectRole, Permission, bool>();
        // The table in README "Roles & permissions": each role has everything of the roles below it.
        var minimum = new Dictionary<Permission, ProjectRole>
        {
            [Permission.ViewProject] = ProjectRole.Viewer,
            [Permission.EditIssues] = ProjectRole.Developer,
            [Permission.Comment] = ProjectRole.Developer,
            [Permission.PushCode] = ProjectRole.Developer,
            [Permission.CreatePullRequest] = ProjectRole.Developer,
            [Permission.ReviewPullRequest] = ProjectRole.Developer,
            [Permission.ManageLabelsAndMilestones] = ProjectRole.Maintainer,
            [Permission.MergePullRequest] = ProjectRole.Maintainer,
            [Permission.DeleteBranch] = ProjectRole.Maintainer,
            [Permission.ManageRepositories] = ProjectRole.Maintainer,
            [Permission.ManageMembers] = ProjectRole.Owner,
            [Permission.ManageProjectSettings] = ProjectRole.Owner,
            [Permission.DeleteProject] = ProjectRole.Owner,
            [Permission.PublishPackages] = ProjectRole.Developer,
            [Permission.DeletePackages] = ProjectRole.Maintainer,
            [Permission.ToggleFlags] = ProjectRole.Developer,
            [Permission.ManageFlags] = ProjectRole.Maintainer,
            [Permission.RunTests] = ProjectRole.Developer,
            [Permission.ManageTests] = ProjectRole.Maintainer,
        };
        Assert.Equal(Enum.GetValues<Permission>().Length, minimum.Count); // a new permission must be added to this table
        foreach (var role in Enum.GetValues<ProjectRole>())
            foreach (var (p, min) in minimum)
                data.Add(role, p, role >= min);
        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Role_matrix_matches_the_documented_table(ProjectRole role, Permission p, bool expected) =>
        Assert.Equal(expected, ProjectAccess.RoleHas(role, p));

    [Fact]
    public async Task Members_get_their_role_and_checks_follow_it()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private,
            ("bob", ProjectRole.Viewer), ("dan", ProjectRole.Developer));
        await using var s = app.Scope();
        var access = s.Get<ProjectAccess>();

        Assert.Equal(ProjectRole.Owner, await access.GetRoleAsync(project.Id, app.UserId("carol")));
        Assert.Equal(ProjectRole.Viewer, await access.GetRoleAsync(project.Id, app.UserId("bob")));
        Assert.Equal(ProjectRole.Developer, await access.GetRoleAsync(project.Id, app.UserId("dan")));

        Assert.False(await access.CanAsync(project.Id, Permission.EditIssues, app.UserId("bob")));
        Assert.True(await access.CanAsync(project.Id, Permission.EditIssues, app.UserId("dan")));
        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => access.RequireAsync(project.Id, Permission.MergePullRequest, app.UserId("dan")));
        Assert.Contains("requires Maintainer", ex.Message);
    }

    [Fact]
    public async Task Private_projects_are_invisible_to_non_members_and_internal_ones_give_viewer()
    {
        var outsider = await app.CreateUserAsync("outsider1");
        var priv = await app.CreateProjectAsync("carol", ProjectVisibility.Private);
        var internalProject = await app.CreateProjectAsync("carol", ProjectVisibility.Internal);
        await using var s = app.Scope();
        var access = s.Get<ProjectAccess>();

        Assert.Null(await access.GetRoleAsync(priv.Id, outsider.Id));
        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => access.RequireAsync(priv.Id, Permission.ViewProject, outsider.Id));
        Assert.Contains("not found", ex.Message); // doesn't reveal that the project exists

        Assert.Equal(ProjectRole.Viewer, await access.GetRoleAsync(internalProject.Id, outsider.Id));
        Assert.False(await access.CanAsync(internalProject.Id, Permission.Comment, outsider.Id));

        var visible = await access.VisibleProjectIdsAsync(outsider.Id);
        Assert.Contains(internalProject.Id, visible);
        Assert.DoesNotContain(priv.Id, visible);
    }

    [Fact]
    public async Task Site_admin_is_owner_of_every_project_even_without_membership()
    {
        var priv = await app.CreateProjectAsync("carol", ProjectVisibility.Private);
        await using var s = app.Scope();
        var access = s.Get<ProjectAccess>();
        var alice = app.UserId("alice");
        Assert.Equal(ProjectRole.Owner, await access.GetRoleAsync(priv.Id, alice));
        Assert.True(await access.IsAdminAsync(alice));
        Assert.Contains(priv.Id, await access.VisibleProjectIdsAsync(alice));
        Assert.False(await access.IsAdminAsync(app.UserId("carol")));
    }

    [Fact]
    public async Task Unknown_project_and_unknown_user_have_no_role()
    {
        await using var s = app.Scope();
        var access = s.Get<ProjectAccess>();
        Assert.Null(await access.GetRoleAsync(999_999, app.UserId("alice")));
        Assert.Null(await access.GetRoleAsync(app.ProjectId("WEB"), 999_999)); // Internal, but only for real users
        Assert.Empty(await access.VisibleProjectIdsAsync(999_999));
    }

    [Fact]
    public async Task Without_an_acting_user_reads_are_empty_and_writes_throw()
    {
        await using var s = app.Scope(); // no ActAs, no signed-in user
        var access = s.Get<ProjectAccess>();
        var web = app.ProjectId("WEB");
        Assert.Null(await access.GetRoleAsync(web));
        Assert.Empty(await access.VisibleProjectIdsAsync());
        await Assert.ThrowsAsync<ForbiddenException>(() => access.RequireAsync(web, Permission.ViewProject));
        Assert.Empty(await s.Get<IssueService>().GetProjectsAsync());
    }

    [Fact]
    public async Task ActAs_makes_that_user_the_default_for_checks()
    {
        await using var s = app.Scope(app.UserId("bob"));
        var access = s.Get<ProjectAccess>();
        Assert.Equal(ProjectRole.Developer, await access.GetRoleAsync(app.ProjectId("WEB")));
        Assert.Null(await access.GetRoleAsync(app.ProjectId("OPS"))); // bob isn't in OPS (Private)
        // An explicit userId still wins over the acting user.
        Assert.Equal(ProjectRole.Maintainer, await access.GetRoleAsync(app.ProjectId("OPS"), app.UserId("dan")));
    }

    [Fact]
    public async Task ActAsSystem_grants_everything_but_only_vouches_for_the_named_account()
    {
        var priv = await app.CreateProjectAsync("carol", ProjectVisibility.Private);
        var bot = await app.CreateUserAsync("botaccount1");
        var outsider = await app.CreateUserAsync("outsider2");
        await using var s = app.Scope();
        var access = s.Get<ProjectAccess>();
        access.ActAsSystem(onBehalfOf: bot.Id);

        Assert.Equal(ProjectRole.Owner, await access.GetRoleAsync(priv.Id));            // no user named: the system
        Assert.Equal(ProjectRole.Owner, await access.GetRoleAsync(priv.Id, bot.Id));    // the vouched-for service account
        Assert.True(await access.IsAdminAsync());
        Assert.Contains(priv.Id, await access.VisibleProjectIdsAsync());

        // Anyone else named explicitly is checked normally, so system work can't leak a private project to them.
        Assert.Null(await access.GetRoleAsync(priv.Id, outsider.Id));
        Assert.False(await access.CanAsync(priv.Id, Permission.ViewProject, outsider.Id));
        Assert.Equal(ProjectRole.Owner, await access.GetRoleAsync(priv.Id, app.UserId("carol")));

        // ActAs afterwards drops the system grant.
        access.ActAs(outsider.Id);
        Assert.Null(await access.GetRoleAsync(priv.Id));
        Assert.Null(await access.GetRoleAsync(priv.Id, bot.Id));
    }

    [Fact]
    public async Task Last_owner_cannot_be_removed_or_demoted()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("dan", ProjectRole.Developer));
        await using var s = app.Scope(app.UserId("carol"));
        var members = s.Get<MemberService>();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => members.ChangeRoleAsync(project.Id, app.UserId("carol"), ProjectRole.Maintainer));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => members.RemoveMemberAsync(project.Id, app.UserId("carol")));
        // A Developer can't manage members at all.
        await using var d = app.Scope(app.UserId("dan"));
        await Assert.ThrowsAsync<ForbiddenException>(() => d.Get<MemberService>().AddMemberAsync(project.Id, "bob", ProjectRole.Viewer));
    }
}
