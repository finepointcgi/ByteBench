using System.Diagnostics;
using GitBench.Data;
using GitBench.Services.Ci;
using GitBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Tests;

/// <summary>A test that needs a reachable Docker daemon; it is skipped otherwise. Exclude it with <c>--filter "Category!=Docker"</c>.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "version", "--format", "{{.Server.Version}}" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            if (!p.WaitForExit(15_000)) { p.Kill(); return false; }
            return p.ExitCode == 0 && p.StandardOutput.ReadToEnd().Trim().Length > 0;
        }
        catch (Exception) { return false; } // no docker CLI
    });

    public DockerFactAttribute()
    {
        if (!Available.Value) Skip = "Docker is not available.";
    }
}

/// <summary>The app with CI turned on (default run keeps it off).</summary>
public class CiApp : TestApp
{
    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["Ci:Enabled"] = "true";
        settings["Ci:Network"] = "none";
        settings["Ci:MaxConcurrentJobs"] = "1";
    }
}

[Trait("Category", "Docker")]
public class DockerPipelineTests(CiApp app) : IClassFixture<CiApp>
{
    [DockerFact]
    public async Task A_pipeline_runs_its_steps_in_docker()
    {
        var project = await app.CreateProjectAsync("carol");
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        app.Commit(repo, "main", ".gitbench-ci.yml", """
            image: alpine:3.20
            jobs:
              check:
                steps:
                  - name: Greet
                    run: echo "hello from $GITBENCH_JOB" && test -f /workspace/README.md
              fail:
                needs: [check]
                steps:
                  - run: exit 3
            """);

        int id;
        await using (var s = app.Scope(app.UserId("carol")))
            id = await s.Get<PipelineService>().RunAsync(repo, "main");

        var deadline = DateTime.UtcNow.AddMinutes(3);
        Pipeline pipeline;
        while (true)
        {
            await using (var db = app.Db())
                pipeline = await db.Pipelines.AsNoTracking().Include(p => p.Jobs).ThenInclude(j => j.Steps).ThenInclude(s => s.Log)
                    .AsSplitQuery().FirstAsync(p => p.Id == id);
            if (pipeline.Status is not (PipelineStatus.Queued or PipelineStatus.Running)) break;
            Assert.True(DateTime.UtcNow < deadline, "The pipeline didn't finish in time.");
            await Task.Delay(500);
        }

        Assert.True(pipeline.Status == PipelineStatus.Failed, $"status {pipeline.Status}: {pipeline.Error}");
        var check = pipeline.Jobs.Single(j => j.Name == "check");
        Assert.Equal(PipelineStatus.Passed, check.Status);
        Assert.Contains("hello from check", check.Steps.Single().Log?.Text);
        var fail = pipeline.Jobs.Single(j => j.Name == "fail");
        Assert.Equal(PipelineStatus.Failed, fail.Status);
        Assert.Equal(3, fail.Steps.Single().ExitCode);
    }

    private async Task<Pipeline> WaitAsync(int id, params PipelineStatus[] until)
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (true)
        {
            await using (var db = app.Db())
            {
                var p = await db.Pipelines.AsNoTracking().Include(x => x.Jobs).ThenInclude(j => j.Steps).ThenInclude(s => s.Log)
                    .AsSplitQuery().FirstAsync(x => x.Id == id);
                if (until.Contains(p.Status)) return p;
            }
            Assert.True(DateTime.UtcNow < deadline, "The pipeline didn't reach the expected state in time.");
            await Task.Delay(500);
        }
    }

    [DockerFact]
    public async Task Artifacts_flow_through_needs_and_a_manual_deploy_is_recorded()
    {
        var project = await app.CreateProjectAsync("carol");
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        app.Commit(repo, "main", ".gitbench-ci.yml", """
            jobs:
              build:
                cache: { key: "deps-{{ hashFiles('README.md') }}", paths: [cache/] }
                artifacts: { paths: [dist/], expire_in: 1d }
                steps:
                  - run: |
                      mkdir -p dist/sub cache
                      echo "built by $GITBENCH_JOB" > dist/sub/out.txt
                      echo cached > cache/c.txt
                      ln -s /etc/hostname dist/leak
              test:
                needs: [build]
                steps:
                  - run: grep "built by build" dist/sub/out.txt && test ! -e dist/leak
              deploy:
                needs: [build, test]
                when: manual
                allow_failure: false
                environment: { name: staging, url: "https://staging.example.com" }
                steps:
                  - run: test "$CI_ENVIRONMENT_NAME" = staging && test "$CI_PIPELINE_SOURCE" = manual && cat dist/sub/out.txt
            """);

        int id;
        await using (var s = app.Scope(app.UserId("carol")))
            id = await s.Get<PipelineService>().RunAsync(repo, "main");

        // build and test run; the blocking manual deploy holds the pipeline.
        var p = await WaitAsync(id, PipelineStatus.Manual, PipelineStatus.Failed, PipelineStatus.Error, PipelineStatus.Passed);
        Assert.True(p.Status == PipelineStatus.Manual, $"status {p.Status}: {p.Error} / " + string.Join("\n", p.Jobs.SelectMany(j => j.Steps).Select(x => x.Log?.Text)));
        Assert.Equal(PipelineStatus.Passed, p.Jobs.Single(j => j.Name == "test").Status);
        var buildLog = p.Jobs.Single(j => j.Name == "build").Steps.Single().Log!.Text;
        Assert.Contains("Saved artifacts: 1 file(s)", buildLog);
        Assert.Contains("skipped symbolic link dist/leak", buildLog);
        Assert.Contains("Saved cache 'deps-", buildLog);
        Assert.Contains("Downloaded artifacts of 'build': 1 file(s)", p.Jobs.Single(j => j.Name == "test").Steps.Single().Log!.Text);
        var deploy = p.Jobs.Single(j => j.Name == "deploy");
        Assert.Equal(PipelineStatus.Manual, deploy.Status);

        // The artifact zip downloads as an attachment.
        var buildJob = p.Jobs.Single(j => j.Name == "build");
        var client = await app.CookieClientAsync("carol");
        var response = await client.GetAsync(CiServiceCollectionExtensions.ArtifactZipUrl(repo, id, buildJob.Id));
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        var file = await client.GetStringAsync(CiServiceCollectionExtensions.ArtifactFileUrl(repo, id, buildJob.Id, "dist/sub/out.txt"));
        Assert.Equal("built by build\n", file);
        var outsider = await app.CookieClientAsync("bob"); // not a member of this private project
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await outsider.GetAsync(CiServiceCollectionExtensions.ArtifactZipUrl(repo, id, buildJob.Id))).StatusCode);

        // Run the manual job: the pipeline resumes and passes, and the deployment is recorded.
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<PipelineService>().PlayJobAsync(repo, id, deploy.Id);
        p = await WaitAsync(id, PipelineStatus.Passed, PipelineStatus.Failed, PipelineStatus.Error);
        Assert.True(p.Status == PipelineStatus.Passed, $"status {p.Status}: " + p.Jobs.Single(j => j.Name == "deploy").Steps.Single().Log?.Text);
        Assert.Contains("built by build", p.Jobs.Single(j => j.Name == "deploy").Steps.Single().Log!.Text);

        await using var db = app.Db();
        var d = await db.Deployments.AsNoTracking().Include(x => x.Environment).SingleAsync(x => x.PipelineId == id);
        Assert.Equal("staging", d.Environment.Name);
        Assert.Equal("https://staging.example.com", d.Environment.ExternalUrl);
        Assert.Equal(PipelineStatus.Passed, d.Status);
        Assert.Equal(app.UserId("carol"), d.UserId);
        Assert.Equal(p.CommitSha, d.CommitSha);
    }
}
