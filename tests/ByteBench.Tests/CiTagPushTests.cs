using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Ci;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Tests;

/// <summary>A real server with CI on (no docker, so pipelines end in Error) to push tags to with the git CLI.</summary>
public class CiKestrelApp : KestrelApp
{
    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["Ci:Enabled"] = "true";
        settings["Ci:DockerExecutable"] = "/nonexistent/docker-for-tests";
        settings["Ci:SchedulesEnabled"] = "false";
    }
}

/// <summary>Real <c>git push</c> of tags over Smart HTTP: the push capture feeds the CI listener, exactly as in production.</summary>
public class CiTagPushTests(CiKestrelApp app) : IClassFixture<CiKestrelApp>, IDisposable
{
    private readonly GitCli git = new();
    public void Dispose() => git.Dispose();

    [Fact]
    public async Task Pushing_a_tag_starts_a_tag_pipeline_only_when_the_file_asks_for_that_tag()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        var dir = git.Dir("tagclone");
        await git.OkAsync(null, "clone", app.GitUrl(project.Key, repo.Slug, "bob", SeedData.DemoPassword), dir);
        await File.WriteAllTextAsync(Path.Combine(dir, CiFile.FileName),
            "on: { tags: [\"v*\"] }\njobs:\n  ship:\n    rules: [ { if: '$CI_COMMIT_TAG' } ]\n    steps: [ { run: echo shipping } ]\n");
        await git.OkAsync(dir, "add", CiFile.FileName);
        await git.OkAsync(dir, "commit", "-m", "add ci");
        await git.OkAsync(dir, "tag", "-a", "v1.0", "-m", "first");
        await git.OkAsync(dir, "tag", "docs-snapshot");
        await git.OkAsync(dir, "push", "origin", "main", "v1.0", "docs-snapshot");

        var p = Assert.Single(await WaitForTagPipelinesAsync(repo, 1));
        Assert.Equal("refs/tags/v1.0", p.Ref);            // v1.0 matches on.tags; docs-snapshot doesn't
        Assert.Equal(PipelineTrigger.Tag, p.Trigger);
        Assert.Equal(app.UserId("bob"), p.TriggeredById);
        Assert.Equal(["ship"], p.Jobs.Select(j => j.Name));

        // Another tag on a later commit gets its own pipeline; nothing is started twice.
        await File.WriteAllTextAsync(Path.Combine(dir, "x.txt"), "x");
        await git.OkAsync(dir, "add", "x.txt");
        await git.OkAsync(dir, "commit", "-m", "x");
        await git.OkAsync(dir, "tag", "v1.1");
        await git.OkAsync(dir, "push", "origin", "main", "v1.1");
        Assert.Equal(["refs/tags/v1.0", "refs/tags/v1.1"], (await WaitForTagPipelinesAsync(repo, 2)).Select(x => x.Ref));
    }

    private async Task<List<Pipeline>> WaitForTagPipelinesAsync(Repository repo, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            await using var db = app.Db();
            var list = await db.Pipelines.AsNoTracking().Include(p => p.Jobs).Where(p => p.RepositoryId == repo.Id && p.Ref.StartsWith("refs/tags/")).OrderBy(p => p.Id).ToListAsync();
            if (list.Count >= count || DateTime.UtcNow > deadline) return list;
            await Task.Delay(200);
        }
    }
}
