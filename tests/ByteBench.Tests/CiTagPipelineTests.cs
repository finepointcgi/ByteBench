using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Ci;
using ByteBench.Services.Git;
using ByteBench.Services.Releases;
using ByteBench.Services.Webhooks;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

/// <summary>Rule expressions, tag rules, variables, retry and timeout in <c>.bytebench-ci.yml</c> (no database, no Docker).</summary>
public class CiRulesAndVariablesFileTests
{
    private static CiDefinition Ok(string yaml)
    {
        var r = CiFile.Parse(yaml);
        Assert.True(r.Ok, r.Error);
        return r.Definition!;
    }

    private static string Err(string yaml)
    {
        var r = CiFile.Parse(yaml);
        Assert.False(r.Ok, "expected a parse error");
        return r.Error!;
    }

    private static bool Eval(string expression, params (string, string)[] vars)
    {
        var (e, error) = CiRuleExpression.Parse(expression);
        Assert.True(e != null, error);
        return e!.Evaluate(vars.ToDictionary(v => v.Item1, v => v.Item2));
    }

    [Fact]
    public void Expressions_compare_match_and_combine()
    {
        Assert.True(Eval("$CI_COMMIT_TAG", ("CI_COMMIT_TAG", "v1")));
        Assert.False(Eval("$CI_COMMIT_TAG"));
        Assert.False(Eval("$CI_COMMIT_TAG", ("CI_COMMIT_TAG", "")));
        Assert.True(Eval("$CI_COMMIT_TAG == null"));
        Assert.True(Eval("$A == \"x\" && $B != 'y'", ("A", "x"), ("B", "z")));
        Assert.False(Eval("$A == \"x\" && $B != 'y'", ("A", "x"), ("B", "y")));
        Assert.True(Eval("$A == \"x\" || $B == \"y\"", ("B", "y")));
        Assert.True(Eval("$CI_COMMIT_TAG =~ /^v\\d+\\.\\d+$/", ("CI_COMMIT_TAG", "v1.20")));
        Assert.False(Eval("$CI_COMMIT_TAG =~ /^v\\d+\\.\\d+$/", ("CI_COMMIT_TAG", "v1.2-rc1")));
        Assert.True(Eval("$CI_COMMIT_TAG !~ /rc/", ("CI_COMMIT_TAG", "v1.2")));
        Assert.True(Eval("$X =~ /abc/i", ("X", "xABCx")));
        Assert.True(Eval("!($A == \"x\")", ("A", "y")));
        Assert.True(Eval("($A || $B) && $C", ("B", "1"), ("C", "1")));
        Assert.False(Eval("$A =~ /x/")); // an unset variable never matches
    }

    [Theory]
    [InlineData("")]
    [InlineData("$")]
    [InlineData("$A ==")]
    [InlineData("$A == \"x")]
    [InlineData("$A =~ \"x\"")]
    [InlineData("$A =~ /(/")]
    [InlineData("($A")]
    [InlineData("$A $B")]
    [InlineData("foo")]
    public void Bad_expressions_are_rejected_with_a_message(string expression)
    {
        var (e, error) = CiRuleExpression.Parse(expression);
        Assert.Null(e);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void A_file_with_a_bad_condition_is_an_error_naming_it()
    {
        var e = Err("""
            jobs:
              a:
                rules: [ { if: '$X ==' } ]
                steps: [ { run: "true" } ]
            """);
        Assert.Contains("if: $X ==", e);
    }

    private const string TagFile = """
        on:
          tags: ["v*"]
        jobs:
          build:
            rules: [ { if: '$CI_COMMIT_BRANCH' } ]
            steps: [ { run: echo build } ]
          release:
            rules: [ { tags: ["v*"], if: '$CI_COMMIT_TAG =~ /^v\d+/' } ]
            steps: [ { run: echo release } ]
          always:
            steps: [ { run: echo always } ]
        """;

    [Fact]
    public void Rules_pick_jobs_for_a_tag_or_a_branch()
    {
        var d = Ok(TagFile);
        Assert.Equal(["build", "always"], d.ForBranch("main").Jobs.Select(j => j.Name));
        Assert.Equal(["release", "always"], d.ForRef(CiRuleContext.For("refs/tags/v1.2", "push", null)).Jobs.Select(j => j.Name));
        // a tag that matches on.tags but not the release rule's condition
        Assert.Equal(["always"], d.ForRef(CiRuleContext.For("refs/tags/vnext", "push", null)).Jobs.Select(j => j.Name));
    }

    [Fact]
    public void Branch_only_rules_never_match_a_tag_and_tag_rules_never_match_a_branch()
    {
        var d = Ok("""
            jobs:
              a: { only: [main, "v*"], steps: [ { run: "true" } ] }
              b: { rules: [ { tags: ["v*"] } ], steps: [ { run: "true" } ] }
            """);
        Assert.Equal(["a"], d.ForBranch("main").Jobs.Select(j => j.Name));
        Assert.DoesNotContain(d.ForRef(CiRuleContext.For("refs/tags/main", "push", null)).Jobs, j => j.Name == "a"); // the tag "main" is not the branch "main"
        Assert.Equal(["b"], d.ForRef(CiRuleContext.For("refs/tags/v1", "push", null)).Jobs.Select(j => j.Name)); // `only` is for branches
        Assert.DoesNotContain(d.ForBranch("v1").Jobs, j => j.Name == "b"); // and tag rules never match a branch
    }

    [Fact]
    public void Tags_only_start_pipelines_when_the_file_asks_for_it()
    {
        // No on.tags and no tag rules: files written before tag pipelines existed never run on a tag.
        Assert.False(Ok("jobs:\n  a: { steps: [ { run: \"true\" } ] }").RunsOnTag("v1"));
        Assert.False(Ok("jobs:\n  a: { only: [main], steps: [ { run: \"true\" } ] }").RunsOnTag("v1"));
        // on.tags decides when present
        var d = Ok("on: { tags: [\"v*\"] }\njobs:\n  a: { steps: [ { run: \"true\" } ] }");
        Assert.True(d.RunsOnTag("v1"));
        Assert.False(d.RunsOnTag("nightly"));
        // a rule about tags opts in
        Assert.True(Ok("jobs:\n  a: { rules: [ { if: '$CI_COMMIT_TAG' } ], steps: [ { run: \"true\" } ] }").RunsOnTag("anything"));
        Assert.True(Ok("jobs:\n  a: { rules: [ { tags: [\"r*\"] } ], steps: [ { run: \"true\" } ] }").RunsOnTag("anything"));
        Assert.Contains("on.tags", Err("on: { tags: [] }\njobs:\n  a: { steps: [ { run: \"true\" } ] }"));
    }

    [Fact]
    public void Variables_retry_and_timeout_are_parsed_and_merged()
    {
        var d = Ok("""
            variables:
              A: one
              B: "2"
              FLAG: true
            jobs:
              a:
                retry: 2
                timeout: 90m
                variables: { B: job, C: three }
                steps: [ { run: "true" } ]
              b:
                steps: [ { run: "true" } ]
            """);
        var a = d.Jobs.Single(j => j.Name == "a");
        Assert.Equal(2, a.Retry);
        Assert.Equal(TimeSpan.FromMinutes(90), a.Timeout);
        Assert.Equal("one", a.Variables["A"]);
        Assert.Equal("job", a.Variables["B"]);
        Assert.Equal("three", a.Variables["C"]);
        Assert.Equal("true", a.Variables["FLAG"]);
        var b = d.Jobs.Single(j => j.Name == "b");
        Assert.Equal(0, b.Retry);
        Assert.Null(b.Timeout);
        Assert.Equal("2", b.Variables["B"]);

        // They survive being stored on the pipeline job.
        var extras = CiJobExtras.Parse(CiJobExtras.Serialize(a));
        Assert.Equal(2, extras.Retry);
        Assert.Equal(TimeSpan.FromMinutes(90), extras.Timeout);
        Assert.Equal("job", extras.Variables!["B"]);
        Assert.Null(CiJobExtras.Serialize(b with { Variables = new Dictionary<string, string>() }));
    }

    [Theory]
    [InlineData("variables: { BYTEBENCH_X: 1 }", "reserved")]
    [InlineData("variables: { GITBENCH_X: 1 }", "reserved")]
    [InlineData("variables: { CI: x }", "reserved")]
    [InlineData("variables: { 1BAD: x }", "invalid")]
    [InlineData("variables: { \"A-B\": x }", "invalid")]
    [InlineData("variables: { A: [1, 2] }", "plain value")]
    public void Bad_variables_are_rejected(string variables, string expected)
    {
        Assert.Contains(expected, Err($"{variables}\njobs:\n  a: {{ steps: [ {{ run: \"true\" }} ] }}"));
        Assert.Contains(expected, Err($"jobs:\n  a:\n    {variables}\n    steps: [ {{ run: \"true\" }} ]"));
    }

    [Theory]
    [InlineData("retry: 4")]
    [InlineData("retry: -1")]
    [InlineData("timeout: soon")]
    [InlineData("timeout: 30d")]
    public void Bad_retry_and_timeout_are_rejected(string line) =>
        Err($"jobs:\n  a:\n    {line}\n    steps: [ {{ run: \"true\" }} ]");

    [Fact]
    public void Tag_refs_are_told_apart_from_branches()
    {
        Assert.True(CiRef.IsTag("refs/tags/v1"));
        Assert.False(CiRef.IsTag("main"));
        Assert.False(CiRef.IsTag("refs/pull/3/head"));
        Assert.Equal("v1/rc", CiRef.TagName("refs/tags/v1/rc"));
        Assert.Equal("refs/tags/v1", CiRef.ForTag("v1"));
        var tag = CiRef.Predefined("refs/tags/v1", "push");
        Assert.Equal("v1", tag["CI_COMMIT_TAG"]);
        Assert.Equal("v1", tag["CI_COMMIT_REF_NAME"]);
        Assert.False(tag.ContainsKey("CI_COMMIT_BRANCH"));
        var branch = CiRef.Predefined("feature/x", "push");
        Assert.Equal("feature/x", branch["CI_COMMIT_BRANCH"]);
        Assert.False(branch.ContainsKey("CI_COMMIT_TAG"));
        Assert.False(CiRef.Predefined("refs/pull/3/head", "pull_request").ContainsKey("CI_COMMIT_BRANCH"));
    }
}

/// <summary>Tag pipelines, manual-run variables and the merge check, against the database (Docker is not available, so pipelines end in Error).</summary>
public class CiTagPipelineTests(CiNoDockerApp app) : IClassFixture<CiNoDockerApp>
{
    private const string File = """
        on:
          tags: ["v*"]
        jobs:
          build:
            rules: [ { if: '$CI_COMMIT_BRANCH' } ]
            steps: [ { run: echo build } ]
          release:
            rules: [ { if: '$CI_COMMIT_TAG' } ]
            steps: [ { run: echo release } ]
        """;

    private async Task<(Project Project, Repository Repo)> SetupAsync(string yaml = File)
    {
        await using (var db = app.Db())
            if (!await db.Users.AnyAsync(u => u.UserName == "eve")) await app.CreateUserAsync("eve");
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("eve", ProjectRole.Viewer));
        var repo = await app.CreateRepoAsync(project.Id, "alice");
        app.Commit(repo, "main", CiFile.FileName, yaml);
        return (project, repo);
    }

    private void Tag(Repository repo, string name, string rev = "main")
    {
        var path = app.Services.GetRequiredService<RepoStorage>().PathFor(app.ProjectKey(repo.ProjectId), repo.Slug);
        GitOps.CreateTag(path, name, rev, null, new GitIdentity("Test", "test@example.com"));
    }

    private string Sha(Repository repo, string rev)
    {
        var path = app.Services.GetRequiredService<RepoStorage>().PathFor(app.ProjectKey(repo.ProjectId), repo.Slug);
        return GitOps.ResolveSha(path, rev)!;
    }

    /// <summary>Notifies the CI push listener exactly as a git push would (the ref updates come from <see cref="PushCapture"/>).</summary>
    private async Task PushAsync(Repository repo, params PushedRef[] updates)
    {
        await using var s = app.Scope(app.UserId("alice"));
        s.Get<PushCapture>().SetRefs(updates);
        var listener = s.Get<IEnumerable<IGitPushListener>>().OfType<CiPushListener>().Single();
        await listener.OnPushAsync(repo.Id, app.UserId("alice"), CancellationToken.None);
    }

    private PushedRef Created(Repository repo, string tag) => new(PushedRef.ZeroSha, Sha(repo, "refs/tags/" + tag), "refs/tags/" + tag);

    private async Task<List<Pipeline>> TagPipelinesAsync(Repository repo)
    {
        await using var db = app.Db();
        return await db.Pipelines.AsNoTracking().Include(p => p.Jobs).Where(p => p.RepositoryId == repo.Id && p.Ref.StartsWith("refs/tags/")).OrderBy(p => p.Id).ToListAsync();
    }

    [Fact]
    public async Task A_pushed_tag_starts_a_tag_pipeline_with_the_jobs_its_rules_pick()
    {
        var (_, repo) = await SetupAsync();
        Tag(repo, "v1.0");
        await PushAsync(repo, Created(repo, "v1.0"));

        var p = Assert.Single(await TagPipelinesAsync(repo));
        Assert.Equal("refs/tags/v1.0", p.Ref);
        Assert.Equal(PipelineTrigger.Tag, p.Trigger);
        Assert.Equal(Sha(repo, "main"), p.CommitSha);
        Assert.Equal(app.UserId("alice"), p.TriggeredById);
        Assert.Equal(["release"], p.Jobs.Select(j => j.Name)); // `build` is for branches only
    }

    [Fact]
    public async Task Tags_that_dont_match_or_are_deleted_or_pushed_again_start_nothing_extra()
    {
        var (_, repo) = await SetupAsync();
        Tag(repo, "nightly");
        Tag(repo, "v2.0");
        await PushAsync(repo, Created(repo, "nightly"));
        Assert.Empty(await TagPipelinesAsync(repo)); // on.tags is ["v*"]

        await PushAsync(repo, new PushedRef(Sha(repo, "main"), PushedRef.ZeroSha, "refs/tags/v2.0")); // a deletion
        Assert.Empty(await TagPipelinesAsync(repo));

        await PushAsync(repo, Created(repo, "v2.0"));
        await PushAsync(repo, Created(repo, "v2.0")); // the same tag and commit again: still one pipeline
        Assert.Single(await TagPipelinesAsync(repo));
    }

    [Fact]
    public async Task Files_that_say_nothing_about_tags_never_run_on_one()
    {
        var (_, repo) = await SetupAsync("jobs:\n  build: { steps: [ { run: echo hi } ] }");
        Tag(repo, "v1.0");
        await PushAsync(repo, Created(repo, "v1.0"));
        Assert.Empty(await TagPipelinesAsync(repo));
    }

    [Fact]
    public async Task A_tag_made_in_ByteBench_starts_a_tag_pipeline_too()
    {
        var (_, repo) = await SetupAsync();
        await using (var s = app.Scope(app.UserId("alice")))
            await s.Get<ReleaseService>().CreateTagAsync(repo, "v3.0", "main", "Release 3");
        var p = Assert.Single(await TagPipelinesAsync(repo));
        Assert.Equal("refs/tags/v3.0", p.Ref);
        Assert.Equal(PipelineTrigger.Tag, p.Trigger);
    }

    [Fact]
    public async Task A_tag_pipeline_can_be_run_by_hand_with_variables_and_sees_them_in_its_rules()
    {
        var (_, repo) = await SetupAsync("""
            jobs:
              deploy:
                rules: [ { if: '$CI_COMMIT_TAG && $TARGET == "staging"' } ]
                steps: [ { run: echo deploy } ]
            """);
        Tag(repo, "hotfix");
        int id;
        await using (var s = app.Scope(app.UserId("bob")))
            id = await s.Get<PipelineService>().RunTagAsync(repo, "hotfix", new Dictionary<string, string> { ["TARGET"] = "staging" });
        await using var db = app.Db();
        var p = await db.Pipelines.Include(x => x.Jobs).SingleAsync(x => x.Id == id);
        Assert.Equal("refs/tags/hotfix", p.Ref);
        Assert.Equal(PipelineTrigger.Manual, p.Trigger);
        Assert.Equal("staging", CiJobExtras.ParseVariables(p.VariablesJson)["TARGET"]);
        Assert.Equal(["deploy"], p.Jobs.Select(j => j.Name));

        // Without the variable no job is left, so there is nothing to run.
        await using var s2 = app.Scope(app.UserId("bob"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s2.Get<PipelineService>().RunTagAsync(repo, "hotfix"));
        Assert.Contains("tag 'hotfix'", ex.Message);
    }

    [Fact]
    public async Task Running_a_pipeline_by_hand_checks_variables_tags_and_permissions()
    {
        var (_, repo) = await SetupAsync();
        Tag(repo, "v1.0");
        await using var bob = app.Scope(app.UserId("bob"));
        var svc = bob.Get<PipelineService>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RunTagAsync(repo, "nope"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RunTagAsync(repo, "../main"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RunAsync(repo, "refs/tags/v1.0")); // only RunTagAsync runs tags
        var reserved = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RunAsync(repo, "main", new Dictionary<string, string> { ["BYTEBENCH_JOB_TOKEN"] = "x" }));
        Assert.Contains("reserved", reserved.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RunAsync(repo, "main", new Dictionary<string, string> { ["A"] = "two\nlines" }));

        await using var eve = app.Scope(app.UserId("eve")); // a Viewer
        await Assert.ThrowsAsync<ForbiddenException>(() => eve.Get<PipelineService>().RunTagAsync(repo, "v1.0"));
        await Assert.ThrowsAsync<ForbiddenException>(() => eve.Get<PipelineService>().RunAsync(repo, "main", new Dictionary<string, string> { ["A"] = "1" }));
    }

    [Fact]
    public async Task The_pipeline_check_waits_for_nothing_when_no_pipeline_would_start()
    {
        // Every job is for main: a PR from another branch gets no pipeline, so the check must not stay Pending forever.
        var (_, repo) = await SetupAsync("jobs:\n  deploy:\n    only: [main]\n    steps: [ { run: echo hi } ]");
        app.Commit(repo, "feature", "x.txt", "x");
        await using var s = app.Scope(app.UserId("bob"));
        var pr = await s.Get<PullRequestService>().CreateAsync(repo, "Feature", null, "feature", "main", app.UserId("bob"));
        var check = s.Get<IEnumerable<IMergeCheck>>().OfType<PipelineMergeCheck>().Single();
        Assert.Empty(await check.EvaluateAsync(pr.Id));

        // Pull request pipelines switched off and the branch not in on.branches: same.
        var (_, repo2) = await SetupAsync("on:\n  branches: [main]\n  pull_requests: false\njobs:\n  build: { steps: [ { run: echo hi } ] }");
        app.Commit(repo2, "topic", "x.txt", "x");
        var pr2 = await s.Get<PullRequestService>().CreateAsync(repo2, "Topic", null, "topic", "main", app.UserId("bob"));
        Assert.Empty(await check.EvaluateAsync(pr2.Id));
    }

    [Fact]
    public async Task The_pipeline_check_still_waits_when_a_pipeline_would_start()
    {
        var (_, repo) = await SetupAsync("jobs:\n  build: { steps: [ { run: echo hi } ] }");
        app.Commit(repo, "feature", "x.txt", "x");
        await using var s = app.Scope(app.UserId("bob"));
        var pr = await s.Get<PullRequestService>().CreateAsync(repo, "Feature", null, "feature", "main", app.UserId("bob"));
        var check = s.Get<IEnumerable<IMergeCheck>>().OfType<PipelineMergeCheck>().Single();
        var result = Assert.Single(await check.EvaluateAsync(pr.Id));
        Assert.NotEqual(MergeCheckState.Passed, result.State);
    }

    [Fact]
    public async Task A_tag_pipeline_on_the_head_commit_is_not_the_pull_requests_pipeline()
    {
        var (_, repo) = await SetupAsync("jobs:\n  build: { steps: [ { run: echo hi } ] }");
        var head = app.Commit(repo, "feature2", "x.txt", "x");
        await using (var db = app.Db())
        {
            db.Pipelines.Add(new Pipeline { RepositoryId = repo.Id, Ref = "refs/tags/v9", CommitSha = head, Status = PipelineStatus.Passed, Trigger = PipelineTrigger.Tag });
            await db.SaveChangesAsync();
        }
        await using var s = app.Scope(app.UserId("bob"));
        var pr = await s.Get<PullRequestService>().CreateAsync(repo, "Feature2", null, "feature2", "main", app.UserId("bob"));
        var check = s.Get<IEnumerable<IMergeCheck>>().OfType<PipelineMergeCheck>().Single();
        var result = Assert.Single(await check.EvaluateAsync(pr.Id));
        Assert.NotEqual(MergeCheckState.Passed, result.State); // the tag's green pipeline doesn't count
    }
}
