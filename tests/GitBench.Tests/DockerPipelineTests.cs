using System.Diagnostics;
using GitBench.Data;
using GitBench.Services.Ci;
using GitBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Tests;

/// <summary>A test that needs a reachable Docker daemon; it is skipped otherwise. Exclude it with <c>--filter "Category!=Docker"</c>.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    internal static readonly Lazy<bool> Available = new(() =>
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
}
