using GitBench.Data;
using GitBench.Services;
using GitBench.Tests.Infrastructure;

namespace GitBench.Tests;

public class SavedFilterTests(TestApp app) : IClassFixture<TestApp>
{
    private static IssueFilter Full() => new()
    {
        Search = "crash & burn = 100% #urgent",
        Status = IssueStatus.InReview,
        Open = null, // "open or closed": differs from the default (true), so it is written as "open="
        AssigneeId = 0,
        LabelId = 7,
        MilestoneId = 3,
        Type = IssueType.Subtask,
        Priority = IssuePriority.Highest,
        Sort = "priority",
        SprintId = 12,
        WorkflowStatusId = 13,
        EpicId = 42,
        HideSubtasks = true,
        ComponentId = 5,
        FixVersionId = 0, // "no fix version"
        AffectsVersionId = 9,
        Cf = "severity:Major & co",
        Columns = "severity,components,timeSpent",
    };

    [Fact]
    public void Every_filter_field_survives_a_round_trip()
    {
        var filter = Full();
        var query = IssueFilterQuery.ToQueryString(filter);
        Assert.Contains("open=&", query + "&");
        Assert.Equal(filter, IssueFilterQuery.Parse(query));
        Assert.Equal(filter, IssueFilterQuery.Parse("?" + query));
        Assert.Equal(filter, IssueFilterQuery.FromUri($"/p/WEB/issues?{query}#top"));
        Assert.Equal(query, IssueFilterQuery.Normalize(query));

        // Every settable property is covered by Full(), so a new IssueFilter field shows up here.
        var defaults = new IssueFilter();
        foreach (var p in typeof(IssueFilter).GetProperties().Where(p => p.CanWrite))
            Assert.False(Equals(p.GetValue(defaults), p.GetValue(filter)), $"Full() should set {p.Name} to a non-default value");
    }

    [Fact]
    public void Defaults_are_left_out_and_junk_is_ignored()
    {
        Assert.Equal("", IssueFilterQuery.ToQueryString(new IssueFilter()));
        var parsed = IssueFilterQuery.Parse("type=NotAType&priority=3&assigneeId=abc&bogus=1&sort=created");
        Assert.Equal(new IssueFilter { Sort = "created" }, parsed); // numeric enum values are refused too
        Assert.Equal("sort=created", IssueFilterQuery.Normalize("bogus=1&sort=created&open=true"));
        Assert.Equal(new IssueFilter(), IssueFilterQuery.FromUri("/p/WEB/issues"));
    }

    [Fact]
    public async Task Saved_filters_round_trip_their_query_and_respect_sharing()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private,
            ("bob", ProjectRole.Developer), ("dan", ProjectRole.Viewer));
        var query = IssueFilterQuery.ToQueryString(Full());
        await using var bob = app.Scope(app.UserId("bob"));
        await using var dan = app.Scope(app.UserId("dan"));
        await using var carol = app.Scope(app.UserId("carol"));

        var mine = await bob.Get<SavedFilterService>().CreateAsync(project.Id, "Mine", "?bogus=1&" + query, shared: false);
        // Stored as given (minus '?'); readers compare normalized forms, and it parses back to the same filter.
        Assert.Equal(query, IssueFilterQuery.Normalize(mine.Query));
        Assert.Equal(Full(), IssueFilterQuery.Parse(mine.Query));

        // Private to bob.
        Assert.Empty(await dan.Get<SavedFilterService>().ListAsync(project.Id));
        // Viewers can save private filters but not share them.
        await Assert.ThrowsAsync<ForbiddenException>(() => dan.Get<SavedFilterService>().CreateAsync(project.Id, "Shared?", query, shared: true));
        await bob.Get<SavedFilterService>().SetSharedAsync(mine.Id, true);
        var seen = Assert.Single(await dan.Get<SavedFilterService>().ListAsync(project.Id));
        Assert.False(seen.CanManage);

        // Anyone who sees it can star it; Maintainers+ (here the Owner) can manage a shared filter.
        await dan.Get<SavedFilterService>().SetStarAsync(mine.Id, true);
        Assert.Single(await dan.Get<SavedFilterService>().ListAsync(project.Id, starredOnly: true));
        Assert.Empty(await bob.Get<SavedFilterService>().ListAsync(project.Id, starredOnly: true)); // stars are per user
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => dan.Get<SavedFilterService>().DeleteAsync(mine.Id));
        await carol.Get<SavedFilterService>().DeleteAsync(mine.Id);
        Assert.Empty(await bob.Get<SavedFilterService>().ListAsync(project.Id));
    }
}
