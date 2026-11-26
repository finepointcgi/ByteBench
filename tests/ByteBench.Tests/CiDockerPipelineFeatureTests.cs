using System.Diagnostics;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Ci;
using ByteBench.Services.Git;
using ByteBench.Services.Webhooks;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

/// <summary>CI turned on with room for parallel jobs, no outside network (services get an internal Docker network).</summary>
public class CiFeatureApp : CiApp
{
    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        base.AddSettings(settings);
        settings["Ci:MaxConcurrentJobs"] = "4";
        settings["Ci:ServiceStartTimeoutSeconds"] = "90";
    }
}

/// <summary>
/// Real runs in Docker for the pipeline features: stages, extends and file includes, services (sidecars on a private network),
/// coverage, when: always/on_failure/delayed, resource_group, webhooks and environment on_stop.
/// </summary>
[Trait("Category", "Docker")]
[Collection("ci-docker")]
public class CiDockerPipelineFeatureTests(CiFeatureApp app) : IClassFixture<CiFeatureApp>
{
    private async Task<Pipeline> WaitAsync(int id, TimeSpan? limit = null, params PipelineStatus[] until)
    {
        var deadline = DateTime.UtcNow + (limit ?? TimeSpan.FromMinutes(4));
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

    private async Task<(Repository Repo, int Id)> RunAsync(string yaml, params (string Path, string Content)[] files)
    {
        var project = await app.CreateProjectAsync("carol");
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        foreach (var (path, content) in files) app.Commit(repo, "main", path, content);
        app.Commit(repo, "main", CiFile.FileName, yaml);
        await using var s = app.Scope(app.UserId("carol"));
        return (repo, await s.Get<PipelineService>().RunAsync(repo, "main"));
    }

    private static string Log(Pipeline p, string job) => string.Join("\n", p.Jobs.Single(j => j.Name == job).Steps.OrderBy(s => s.Order).Select(s => s.Log?.Text ?? ""));
    private static PipelineJob Job(Pipeline p, string name) => p.Jobs.Single(j => j.Name == name);

    private static async Task<string> DockerAsync(params string[] args)
    {
        var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return output;
    }

    // ---------------------------------------------------------------- stages, extends, include

    [DockerFact]
    public async Task Stages_extends_and_a_local_include_run_in_order()
    {
        var (_, id) = await RunAsync("""
            include:
              - local: ci/common.yml
            stages: [build, test, deploy]
            variables: { WHO: file }
            jobs:
              compile:
                extends: .base
                stage: build
                steps: [ { run: 'echo "compile who=$WHO tool=$TOOL"' } ]
              unit:
                extends: [.base, .fast]
                stage: test
                steps: [ { run: 'echo "unit"; sleep 1' } ]
              integration:
                extends: .base
                stage: test
                steps: [ { run: 'echo "integration"; sleep 1' } ]
              ship:
                stage: deploy
                steps: [ { run: echo shipping } ]
              independent:
                stage: deploy
                needs: []
                steps: [ { run: echo "no waiting" } ]
            """,
            ("ci/common.yml", """
                jobs:
                  .base:
                    image: alpine:3.20
                    variables: { TOOL: from-include }
                  .fast:
                    variables: { TOOL: fast }
                """));
        var p = await WaitAsync(id);
        Assert.True(p.Status == PipelineStatus.Passed, $"{p.Status}: {p.Error}");
        Assert.Equal(["compile", "independent", "integration", "ship", "unit"], p.Jobs.Select(j => j.Name).Order());
        Assert.Contains("compile who=file tool=from-include", Log(p, "compile"));
        var build = Job(p, "compile");
        foreach (var next in new[] { "unit", "integration" })
            Assert.True(Job(p, next).StartedAt >= build.FinishedAt, $"{next} must start after the build stage");
        Assert.True(Job(p, "ship").StartedAt >= Job(p, "unit").FinishedAt && Job(p, "ship").StartedAt >= Job(p, "integration").FinishedAt, "deploy starts after the test stage");
        Assert.Equal("compile,", string.Join(",", Job(p, "unit").NeedsList) + ",");          // implicit needs are stored as plain needs
        Assert.Empty(Job(p, "independent").NeedsList);
        Assert.Equal("test", CiJobExtras.Parse(Job(p, "unit").ExtrasJson).Stage);
    }

    [DockerFact]
    public async Task A_failing_stage_skips_the_later_stages_but_always_and_on_failure_jobs_still_run()
    {
        var (_, id) = await RunAsync("""
            stages: [build, test, deploy, cleanup]
            jobs:
              compile:  { stage: build, steps: [ { run: exit 3 } ] }
              unit:     { stage: test, steps: [ { run: echo never } ] }
              ship:     { stage: deploy, steps: [ { run: echo never } ] }
              tidy:     { stage: cleanup, when: always, steps: [ { run: echo tidied } ] }
              alert:    { stage: cleanup, when: on_failure, steps: [ { run: echo alerted } ] }
              quiet:    { stage: build, when: on_failure, steps: [ { run: echo never } ] }
              later:    { stage: cleanup, when: delayed, start_in: 3s, needs: [], steps: [ { run: echo delayed } ] }
            """);
        var p = await WaitAsync(id);
        var all = string.Join("; ", p.Jobs.Select(j => $"{j.Name}={j.Status} needs[{j.Needs}] {j.ExtrasJson}"));
        Assert.Equal(PipelineStatus.Failed, p.Status);                                   // the failure still fails the pipeline
        Assert.True(Job(p, "compile").Status == PipelineStatus.Failed, all);
        Assert.True(Job(p, "unit").Status == PipelineStatus.Skipped, all);
        Assert.True(Job(p, "ship").Status == PipelineStatus.Skipped, all);
        Assert.True(Job(p, "tidy").Status == PipelineStatus.Passed, all);                // always: runs after a failure
        Assert.True(Job(p, "alert").Status == PipelineStatus.Passed, all);               // on_failure: something before it failed
        Assert.True(Job(p, "quiet").Status == PipelineStatus.Skipped, all);              // on_failure with nothing failed before it
        Assert.True(Job(p, "later").Status == PipelineStatus.Passed, all);
        Assert.True(Job(p, "later").StartedAt >= p.CreatedAt.AddSeconds(3), "a delayed job waits its start_in");
        Assert.Contains("Delayed job", Log(p, "later"));
    }

    // ---------------------------------------------------------------- services

    [DockerFact]
    public async Task Services_run_on_a_private_network_reachable_by_alias_and_are_torn_down()
    {
        var (_, id) = await RunAsync("""
            jobs:
              web:
                services:
                  - image: alpine:3.20
                    alias: site
                    port: 8080
                    variables: { GREETING: hello-from-service }
                    command: [nc, -lk, -p, "8080", -e, sh, -c, 'printf "HTTP/1.0 200 OK\r\n\r\n%s\n" "$GREETING"']
                steps:
                  - name: Call the service
                    run: 'out=$(nc -w 2 site 8080 </dev/null); echo "$out"; echo "$out" | grep -q hello-from-service && echo reachable'
                  - name: A second step sees it too
                    run: 'nc -w 2 site 8080 </dev/null'
            """);
        var p = await WaitAsync(id);
        Assert.True(p.Status == PipelineStatus.Passed, $"{p.Status}: {p.Error}\n{Log(p, "web")}");
        var log = Log(p, "web");
        Assert.Contains("Starting service 'site'", log);
        Assert.Contains("Service 'site' is ready", log);
        Assert.Contains("hello-from-service", log);
        Assert.Contains("reachable", log);
        // The sidecar and its network are gone once the job ended.
        Assert.DoesNotContain($"p{id}-j{Job(p, "web").Id}", await DockerAsync("ps", "-a", "--format", "{{.Names}}"));
        Assert.DoesNotContain($"p{id}-j{Job(p, "web").Id}", await DockerAsync("network", "ls", "--format", "{{.Name}}"));
    }

    [DockerFact]
    public async Task A_postgres_sidecar_is_waited_for_and_reachable()
    {
        var (_, id) = await RunAsync("""
            jobs:
              db-test:
                image: postgres:17
                services:
                  - image: postgres:17
                    alias: db
                    port: 5432
                    variables: { POSTGRES_PASSWORD: pw }
                steps:
                  - run: PGPASSWORD=pw psql -h db -U postgres -tAc 'select 6*7'
            """);
        var p = await WaitAsync(id);
        Assert.True(p.Status == PipelineStatus.Passed, $"{p.Status}: {p.Error}\n{Log(p, "db-test")}");
        Assert.Contains("42", Log(p, "db-test"));
    }

    [DockerFact]
    public async Task A_service_that_stops_fails_the_job_with_its_output_and_leaves_nothing_behind()
    {
        var (_, id) = await RunAsync("""
            jobs:
              broken:
                services:
                  - { image: alpine:3.20, alias: dies, command: [sh, -c, 'echo service-says-boom; exit 1'] }
                steps: [ { run: echo should-not-run } ]
              slow-to-open:
                services:
                  - { image: alpine:3.20, alias: never, port: 9999, command: [sleep, "300"] }
                steps: [ { run: echo should-not-run } ]
            """);
        var p = await WaitAsync(id);
        Assert.Equal(PipelineStatus.Failed, p.Status);
        var broken = Log(p, "broken");
        Assert.Contains("stopped before it was ready", broken);
        Assert.Contains("service-says-boom", broken);
        Assert.DoesNotContain("should-not-run", broken);
        Assert.Equal(PipelineStatus.Failed, Job(p, "broken").Status);
        Assert.Equal(PipelineStatus.Failed, Job(p, "slow-to-open").Status);
        Assert.Contains("was not ready after", Log(p, "slow-to-open"));
        Assert.DoesNotContain("bytebench-ci-svc-p" + id, await DockerAsync("ps", "-a", "--format", "{{.Names}}"));
        Assert.DoesNotContain("bytebench-ci-net-p" + id, await DockerAsync("network", "ls", "--format", "{{.Name}}"));
    }

    [DockerFact]
    public async Task Service_containers_get_the_same_isolation_flags_as_job_containers_and_no_secrets()
    {
        var docker = app.Services.GetRequiredService<CiDocker>();
        var args = docker.ServiceRunArguments("svc", "net", new CiServiceDefinition("postgres:17", "db", ["-c", "fsync=off"], new Dictionary<string, string> { ["A"] = "1" }, 5432));
        Assert.Contains("no-new-privileges", args);
        Assert.Contains("--memory", args);
        Assert.Contains("--pids-limit", args);
        Assert.Equal("db", args[args.ToList().IndexOf("--network-alias") + 1]);
        Assert.Equal("net", args[args.ToList().IndexOf("--network") + 1]);
        Assert.DoesNotContain("--env-file", args);                       // CI secret variables never reach a service
        Assert.DoesNotContain("-v", args);                               // no volumes, no workspace, no docker socket
        Assert.DoesNotContain("--privileged", args);
        Assert.DoesNotContain("-p", args);                               // nothing is published to the host
        Assert.Equal(["postgres:17", "-c", "fsync=off"], args.SkipWhile(a => a != "postgres:17").ToList());
        await Task.CompletedTask;
    }

    // ---------------------------------------------------------------- coverage

    [DockerFact]
    public async Task Coverage_comes_from_a_regex_or_a_cobertura_and_an_lcov_report_and_is_stored_per_pipeline()
    {
        var (repo, id) = await RunAsync("""
            jobs:
              from-log:
                coverage: '/Total coverage: (\d+\.\d+)%/'
                steps: [ { run: 'echo "Total coverage: 60.00%"; echo "Total coverage: 80.50%"' } ]
              from-cobertura:
                artifacts: { reports: { cobertura: cov/cobertura.xml } }
                steps:
                  - run: |
                      mkdir cov
                      echo '<coverage line-rate="0.5" lines-valid="200" lines-covered="130"><packages/></coverage>' > cov/cobertura.xml
              from-lcov:
                artifacts: { reports: { lcov: lcov.info } }
                steps: [ { run: 'printf "SF:a\nLF:10\nLH:9\nend_of_record\n" > lcov.info' } ]
              no-coverage:
                steps: [ { run: echo nothing } ]
            """);
        var p = await WaitAsync(id);
        Assert.True(p.Status == PipelineStatus.Passed, $"{p.Status}: {p.Error}");
        Assert.Equal(80.5, Job(p, "from-log").Coverage);
        Assert.Equal(65, Job(p, "from-cobertura").Coverage);
        Assert.Equal(90, Job(p, "from-lcov").Coverage);
        Assert.Null(Job(p, "no-coverage").Coverage);
        Assert.Equal(78.5, p.Coverage);                                   // the average of the jobs that report one
        Assert.Contains("Coverage: 80.5% (from the log)", Log(p, "from-log"));
        Assert.Contains("from cov/cobertura.xml, cobertura", Log(p, "from-cobertura"));

        await using var s = app.Scope(app.UserId("carol"));
        var trend = await s.Get<CiBadgeService>().TrendAsync(repo, "main");
        Assert.Equal([(id, 78.5)], trend);
        Assert.Equal(78.5, (await s.Get<CiBadgeService>().LatestAsync(repo, "main")).Coverage);
    }

    // ---------------------------------------------------------------- resource_group

    private static (long Start, long End) Window(string log)
    {
        long Find(string tag) => long.Parse(log.Split('\n').First(l => l.StartsWith(tag)).Split('=')[1].Trim());
        return (Find("START="), Find("END="));
    }

    [DockerFact]
    public async Task Jobs_in_one_resource_group_never_overlap_while_other_jobs_do()
    {
        const string Timed = "echo START=$(date +%s%N); sleep 3; echo END=$(date +%s%N)";
        var (_, id) = await RunAsync($$"""
            jobs:
              deploy-a: { resource_group: prod, steps: [ { run: '{{Timed}}' } ] }
              deploy-b: { resource_group: prod, steps: [ { run: '{{Timed}}' } ] }
              free-a:   { steps: [ { run: '{{Timed}}' } ] }
              free-b:   { steps: [ { run: '{{Timed}}' } ] }
            """);
        var p = await WaitAsync(id);
        Assert.True(p.Status == PipelineStatus.Passed, $"{p.Status}: {p.Error}");
        var a = Window(Log(p, "deploy-a"));
        var b = Window(Log(p, "deploy-b"));
        Assert.True(a.End <= b.Start || b.End <= a.Start, "jobs of one resource group must not overlap");
        var fa = Window(Log(p, "free-a"));
        var fb = Window(Log(p, "free-b"));
        Assert.True(fa.Start < fb.End && fb.Start < fa.End, "jobs without a group run in parallel (4 slots)");
        Assert.Contains("resource group 'prod'", Log(p, "deploy-a") + Log(p, "deploy-b"));
    }

    // ---------------------------------------------------------------- webhooks

    [DockerFact]
    public async Task Pipeline_and_job_webhook_events_are_sent_and_signed()
    {
        using var receiver = new WebhookReceiver();
        var project = await app.CreateProjectAsync("carol");
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        app.Commit(repo, "main", CiFile.FileName, "stages: [build, test]\njobs:\n  compile:\n    stage: build\n    coverage: '/c=(\\d+)/'\n    steps: [ { run: echo c=77 } ]\n  unit:\n    stage: test\n    steps: [ { run: echo ok } ]\n");
        const string secret = "a-ci-webhook-secret-long-enough";
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<WebhookService>().CreateAsync(project.Id, receiver.Url, secret,
                [WebhookEventNames.PipelineCreated, WebhookEventNames.PipelineStarted, WebhookEventNames.PipelineCompleted, WebhookEventNames.JobStarted, WebhookEventNames.JobCompleted], true);
        int id;
        await using (var s = app.Scope(app.UserId("carol")))
            id = await s.Get<PipelineService>().RunAsync(repo, "main");
        await WaitAsync(id);

        foreach (var name in new[] { "pipeline.created", "pipeline.started", "pipeline.completed", "job.started", "job.completed" })
        {
            var d = await receiver.WaitForAsync(name, TimeSpan.FromSeconds(30));
            var expected = "sha256=" + Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret), d.Body));
            Assert.Equal(expected, d.Signature);
            var body = JsonDocument.Parse(d.Body).RootElement;
            Assert.Equal(name, body.GetProperty("event").GetString());
            Assert.Equal(id, body.GetProperty("pipeline").GetProperty("id").GetInt32());
            Assert.Equal(repo.Slug, body.GetProperty("repository").GetProperty("slug").GetString());
        }
        var created = JsonDocument.Parse((await receiver.WaitForAsync("pipeline.created")).Body).RootElement.GetProperty("pipeline");
        Assert.Equal("queued", created.GetProperty("status").GetString());
        var completed = JsonDocument.Parse((await receiver.WaitForAsync("pipeline.completed")).Body).RootElement.GetProperty("pipeline");
        Assert.Equal("passed", completed.GetProperty("status").GetString());
        Assert.Equal(77, completed.GetProperty("coverage").GetDouble());
        Assert.Equal(1, receiver.Count("pipeline.started"));
        Assert.Equal(2, await WaitForCountAsync(receiver, "job.completed", 2));           // one per job
        Assert.Equal(2, receiver.Count("job.started"));
        var jobBodies = new List<JsonElement>();
        await using (var db = app.Db())
            Assert.Equal(2, await db.WebhookDeliveries.CountAsync(x => x.Event == "job.completed"));
    }

    private static async Task<int> WaitForCountAsync(WebhookReceiver receiver, string name, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (receiver.Count(name) < count && DateTime.UtcNow < deadline) await Task.Delay(200);
        return receiver.Count(name);
    }

    // ---------------------------------------------------------------- environment on_stop

    [DockerFact]
    public async Task Deleting_a_branch_runs_the_on_stop_job_of_its_deployment()
    {
        var project = await app.CreateProjectAsync("carol");
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        const string File = """
            jobs:
              deploy-review:
                only: ["feature/*"]
                environment: { name: review-app, url: "https://review.example.com", on_stop: stop-review }
                steps: [ { run: echo deployed } ]
              stop-review:
                needs: [deploy-review]
                when: manual
                environment: { name: review-app, action: stop }
                steps: [ { run: echo "stopping the review app" } ]
            """;
        app.Commit(repo, "main", CiFile.FileName, File);
        app.Commit(repo, "feature/x", "x.txt", "x");
        int id;
        await using (var s = app.Scope(app.UserId("carol")))
            id = await s.Get<PipelineService>().RunAsync(repo, "feature/x");
        var p = await WaitAsync(id);
        Assert.Equal(PipelineStatus.Passed, p.Status);
        Assert.Equal(PipelineStatus.Passed, Job(p, "deploy-review").Status);
        Assert.Equal(PipelineStatus.Manual, Job(p, "stop-review").Status);              // waits for someone, or for the branch to go away

        // The branch is deleted: the next sync notices and runs the stop job.
        var path = app.Services.GetRequiredService<RepoStorage>().PathFor(app.ProjectKey(project.Id), repo.Slug);
        var refs = app.Services.GetRequiredService<CiRefTracker>();
        refs.Prime(repo.Id, GitOps.ListBranches(path, "main"));
        GitOps.DeleteBranch(path, "feature/x", "main");
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<CiTriggerService>().SyncRepositoryAsync(repo.Id, app.UserId("carol"));

        var deadline = DateTime.UtcNow.AddMinutes(3);
        Pipeline after;
        while (true)
        {
            after = await WaitAsync(id, null, PipelineStatus.Passed, PipelineStatus.Failed, PipelineStatus.Error);
            if (Job(after, "stop-review").Status == PipelineStatus.Passed) break;
            Assert.True(DateTime.UtcNow < deadline, "The stop job never ran.");
            await Task.Delay(300);
        }
        Assert.Contains("stopping the review app", Log(after, "stop-review"));
        await using var db = app.Db();
        var env = await db.CiEnvironments.AsNoTracking().SingleAsync(e => e.RepositoryId == repo.Id && e.Name == "review-app");
        Assert.NotNull(env.StoppedAt);
    }

    [DockerFact]
    public async Task A_job_that_waits_for_approval_is_not_started_by_the_branch_stopper()
    {
        var project = await app.CreateProjectAsync("carol");
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        app.Commit(repo, "main", CiFile.FileName, "jobs:\n  d:\n    only: [\"f/*\"]\n    environment: { name: prot, on_stop: s }\n    steps: [ { run: echo d } ]\n  s:\n    needs: [d]\n    when: manual\n    environment: { name: prot, action: stop }\n    steps: [ { run: echo stop } ]\n");
        app.Commit(repo, "f/y", "y.txt", "y");
        await using (var db = app.Db())
        {
            db.CiEnvironments.Add(new CiEnvironment { RepositoryId = repo.Id, Name = "prot", IsProtected = true });
            await db.SaveChangesAsync();
        }
        int id;
        await using (var s = app.Scope(app.UserId("carol")))
            id = await s.Get<PipelineService>().RunAsync(repo, "f/y");
        // The deploy job targets a protected environment, so it waits for approval itself: nothing deployed, nothing to stop.
        var p = await WaitAsync(id);
        Assert.Equal(PipelineStatus.Manual, Job(p, "d").Status);
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<CiTriggerService>().StopEnvironmentsAsync(repo, "f/y");
        await Task.Delay(500);
        await using var db2 = app.Db();
        Assert.Equal(PipelineStatus.Queued, (await db2.PipelineJobs.AsNoTracking().SingleAsync(j => j.PipelineId == id && j.Name == "s")).Status);
    }
}
