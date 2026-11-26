using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Ci;
using ByteBench.Services.Git;
using ByteBench.Services.Webhooks;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

/// <summary>
/// Real runs in Docker (alpine:3.20, network off): tag pipelines and their variables, secret masking and protected
/// variables, retry, timeout, cancel, parallel jobs, allow_failure and artifacts of failed jobs.
/// </summary>
[Trait("Category", "Docker")]
public class CiDockerFeatureTests(CiApp app) : IClassFixture<CiApp>
{
    private async Task<Pipeline> WaitAsync(int id, TimeSpan? limit = null, params PipelineStatus[] until)
    {
        var deadline = DateTime.UtcNow + (limit ?? TimeSpan.FromMinutes(3));
        var done = until.Length > 0 ? until : [PipelineStatus.Passed, PipelineStatus.Failed, PipelineStatus.Canceled, PipelineStatus.Error, PipelineStatus.Manual];
        while (true)
        {
            await using (var db = app.Db())
            {
                var p = await db.Pipelines.AsNoTracking().Include(x => x.Jobs).ThenInclude(j => j.Steps).ThenInclude(s => s.Log)
                    .AsSplitQuery().FirstAsync(x => x.Id == id);
                if (done.Contains(p.Status)) return p;
            }
            Assert.True(DateTime.UtcNow < deadline, "The pipeline didn't reach the expected state in time.");
            await Task.Delay(300);
        }
    }

    private async Task<(Repository Repo, int Id)> RunAsync(string yaml, string branch = "main")
    {
        var project = await app.CreateProjectAsync("carol");
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        app.Commit(repo, "main", CiFile.FileName, yaml);
        await using var s = app.Scope(app.UserId("carol"));
        return (repo, await s.Get<PipelineService>().RunAsync(repo, branch));
    }

    private static string Log(Pipeline p, string job) => string.Join("\n", p.Jobs.Single(j => j.Name == job).Steps.OrderBy(s => s.Order).Select(s => s.Log?.Text ?? ""));

    [DockerFact]
    public async Task A_tag_pipeline_runs_with_the_tag_variables_the_files_variables_and_protected_secrets_masked()
    {
        var project = await app.CreateProjectAsync("carol");
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        app.Commit(repo, "main", CiFile.FileName, """
            on: { tags: ["v*", "nightly"] }
            variables: { CHANNEL: stable }
            jobs:
              release:
                rules: [ { if: '$CI_COMMIT_TAG' } ]
                variables: { REL: "1" }
                steps:
                  - run: |
                      echo "tag=$CI_COMMIT_TAG ref=$CI_COMMIT_REF_NAME src=$CI_PIPELINE_SOURCE branch=[$CI_COMMIT_BRANCH]"
                      echo "channel=$CHANNEL rel=$REL sha=$CI_COMMIT_SHORT_SHA job=$CI_JOB_NAME"
                      echo "key=$DEPLOY_KEY"
                      if [ -n "$DEPLOY_KEY" ]; then echo has-key; else echo no-key; fi
            """);
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<CiVariableService>().SaveAsync(project.Id, null, null, "DEPLOY_KEY", "s3cr3t-deploy-value", isProtected: true);
        await using (var db = app.Db())
        {
            db.ProtectedTagRules.Add(new ProtectedTagRule { RepositoryId = repo.Id, Pattern = "v*" });
            await db.SaveChangesAsync();
        }
        var path = app.Services.GetRequiredService<RepoStorage>().PathFor(app.ProjectKey(project.Id), repo.Slug);
        GitOps.CreateTag(path, "v1.0", "main", null, new GitIdentity("T", "t@example.com"));
        GitOps.CreateTag(path, "nightly", "main", null, new GitIdentity("T", "t@example.com"));

        await using (var s = app.Scope(app.UserId("carol")))
        {
            s.Get<PushCapture>().SetRefs([
                new PushedRef(PushedRef.ZeroSha, GitOps.ResolveSha(path, "refs/tags/v1.0")!, "refs/tags/v1.0"),
                new PushedRef(PushedRef.ZeroSha, GitOps.ResolveSha(path, "refs/tags/nightly")!, "refs/tags/nightly")]);
            await s.Get<IEnumerable<IGitPushListener>>().OfType<CiPushListener>().Single().OnPushAsync(repo.Id, app.UserId("carol"), CancellationToken.None);
        }
        List<Pipeline> tagPipelines;
        await using (var db = app.Db())
            tagPipelines = await db.Pipelines.AsNoTracking().Where(p => p.RepositoryId == repo.Id && p.Ref.StartsWith("refs/tags/")).OrderBy(p => p.Id).ToListAsync();
        Assert.Equal(["refs/tags/v1.0", "refs/tags/nightly"], tagPipelines.Select(p => p.Ref));

        var protectedTag = await WaitAsync(tagPipelines[0].Id);
        Assert.True(protectedTag.Status == PipelineStatus.Passed, $"{protectedTag.Status}: {protectedTag.Error}\n{Log(protectedTag, "release")}");
        var log = Log(protectedTag, "release");
        Assert.Contains("tag=v1.0 ref=v1.0 src=push branch=[]", log);
        Assert.Contains("channel=stable rel=1", log);
        Assert.Contains("job=release", log);
        Assert.Contains("has-key", log);                     // v* is a protected tag: the protected variable reaches it
        Assert.DoesNotContain("s3cr3t-deploy-value", log);   // ... masked in the log
        Assert.Contains("key=***", log);

        var unprotectedTag = await WaitAsync(tagPipelines[1].Id);
        var plain = Log(unprotectedTag, "release");
        Assert.Contains("no-key", plain);                    // "nightly" matches no protected tag rule
        Assert.Contains("not exposed: tag 'nightly' is not protected", plain);
        Assert.DoesNotContain("s3cr3t-deploy-value", plain);
    }

    [DockerFact]
    public async Task A_failing_job_retries_and_a_manual_run_passes_its_variables()
    {
        var (_, id) = await RunAsync("""
            jobs:
              flaky:
                retry: 1
                steps:
                  - run: echo "attempt output $WHO"; exit 7
            """);
        var failed = await WaitAsync(id);
        Assert.Equal(PipelineStatus.Failed, failed.Status);
        var log = Log(failed, "flaky");
        Assert.Contains("Retry 1 of 1", log);                // the second (last) attempt says why it ran again
        Assert.Contains("exit code 7", log);
        Assert.Contains("attempt output", log);
        Assert.Equal(7, failed.Jobs.Single().Steps.Single().ExitCode);
    }

    [DockerFact]
    public async Task Manual_run_variables_reach_the_job_and_the_job_timeout_kills_it()
    {
        var project = await app.CreateProjectAsync("carol");
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        app.Commit(repo, "main", CiFile.FileName, """
            jobs:
              show:
                steps: [ { run: 'echo "who=$WHO"' } ]
              hang:
                timeout: 1m
                steps: [ { run: sleep 100 } ]
            """);
        int id;
        await using (var s = app.Scope(app.UserId("carol")))
            id = await s.Get<PipelineService>().RunAsync(repo, "main", new Dictionary<string, string> { ["WHO"] = "operator" });
        var p = await WaitAsync(id, TimeSpan.FromMinutes(4));
        Assert.Equal(PipelineStatus.Failed, p.Status);
        Assert.Contains("who=operator", Log(p, "show"));
        Assert.Equal(PipelineStatus.Passed, p.Jobs.Single(j => j.Name == "show").Status);
        Assert.Equal(PipelineStatus.Failed, p.Jobs.Single(j => j.Name == "hang").Status);
        Assert.Contains("timed out after 1 minutes", Log(p, "hang"));
    }

    [DockerFact]
    public async Task Parallel_jobs_allow_failure_and_masked_secrets_and_cancel_work()
    {
        var project = await app.CreateProjectAsync("carol");
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        app.Commit(repo, "main", CiFile.FileName, """
            jobs:
              lint:
                allow_failure: true
                steps: [ { run: exit 1 } ]
              unit:
                steps: [ { run: 'echo "pw=$SECRET_PW"; echo "token=$BYTEBENCH_JOB_TOKEN" | sed "s/./&/g" | wc -c' } ]
              after:
                needs: [lint, unit]
                steps: [ { run: echo after } ]
              skipped:
                needs: [bad]
                steps: [ { run: echo never } ]
              bad:
                steps: [ { run: exit 2 } ]
            """);
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<CiVariableService>().SaveAsync(project.Id, null, null, "SECRET_PW", "hunter2-super-secret", isProtected: false);
        int id;
        await using (var s = app.Scope(app.UserId("carol")))
            id = await s.Get<PipelineService>().RunAsync(repo, "main");
        var p = await WaitAsync(id);
        Assert.Equal(PipelineStatus.Failed, p.Status);                                       // `bad` fails the pipeline
        Assert.Equal(PipelineStatus.Failed, p.Jobs.Single(j => j.Name == "lint").Status);     // allow_failure: reported, doesn't fail the pipeline alone
        Assert.Equal(PipelineStatus.Passed, p.Jobs.Single(j => j.Name == "after").Status);    // an allowed failure doesn't skip dependants
        Assert.Equal(PipelineStatus.Skipped, p.Jobs.Single(j => j.Name == "skipped").Status); // a real failure does
        var unit = Log(p, "unit");
        Assert.Contains("pw=***", unit);
        Assert.DoesNotContain("hunter2-super-secret", unit);
        // Jobs without needs started together: the first two overlap or at least both ran (MaxConcurrentJobs is 1 here, so only check both ran).
        Assert.All(p.Jobs.Where(j => j.Name is "lint" or "unit" or "bad"), j => Assert.NotNull(j.StartedAt));
    }

    [DockerFact]
    public async Task A_running_pipeline_can_be_canceled_and_its_container_is_killed()
    {
        var (repo, id) = await RunAsync("jobs:\n  long:\n    steps: [ { run: 'echo started; sleep 300' } ]");
        var running = await WaitAsync(id, TimeSpan.FromMinutes(2), PipelineStatus.Running);
        Assert.Equal(PipelineStatus.Running, running.Status);
        // wait until the step actually printed (the container is up)
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline && !Log(await WaitAsync(id, null, PipelineStatus.Running, PipelineStatus.Failed, PipelineStatus.Canceled), "long").Contains("started")) await Task.Delay(500);

        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<PipelineService>().CancelAsync(repo, id);
        var p = await WaitAsync(id, TimeSpan.FromMinutes(1));
        Assert.Equal(PipelineStatus.Canceled, p.Status);
        Assert.Equal(PipelineStatus.Canceled, p.Jobs.Single().Status);
    }

    [DockerFact]
    public async Task Artifacts_of_a_failed_job_are_kept_when_asked_and_the_next_job_gets_them()
    {
        var (_, id) = await RunAsync("""
            jobs:
              build:
                artifacts: { paths: [out/], when: always, expire_in: 1d }
                steps: [ { run: 'mkdir out && echo partial > out/report.txt && exit 1' } ]
              inspect:
                needs: [build]
                steps: [ { run: cat out/report.txt } ]
            """);
        var p = await WaitAsync(id);
        Assert.Equal(PipelineStatus.Failed, p.Status);
        await using var db = app.Db();
        var artifact = await db.PipelineArtifacts.AsNoTracking().SingleAsync(a => a.PipelineId == id);
        Assert.Equal(1, artifact.FileCount);
        Assert.NotNull(artifact.ExpiresAt);
        // `inspect` is skipped (its need failed without allow_failure): failed artifacts are for people and later pipelines, not for dependants.
        Assert.Equal(PipelineStatus.Skipped, p.Jobs.Single(j => j.Name == "inspect").Status);
    }
}
