using ByteBench.Data;
using ByteBench.Services.Ci;
using ByteBench.Services.Fields;
using ByteBench.Services.Git;
using ByteBench.Services.Hosting;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Tests;

/// <summary>Installs and repositories that predate the rename from GitBench keep working.</summary>
public class RenameCompatibilityTests(CiNoRunnerApp app) : IClassFixture<CiNoRunnerApp>
{
    private const string Yaml = "jobs:\n  build: { steps: [ { run: echo hi } ] }\n";

    private async Task<(Project Project, Repository Repo)> SetupAsync()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private);
        var repo = await app.CreateRepoAsync(project.Id, "alice");
        return (project, repo);
    }

    private async Task<Repository> WithProjectAsync(Repository repo)
    {
        await using var db = app.Db();
        return await db.Repositories.AsNoTracking().Include(r => r.Project).FirstAsync(r => r.Id == repo.Id);
    }

    [Fact]
    public async Task The_pre_rename_pipeline_file_name_is_still_read()
    {
        var (_, repo) = await SetupAsync();
        var sha = app.Commit(repo, "main", ".gitbench-ci.yml", Yaml);
        await using var s = app.Scope(system: true);
        var parsed = await s.Get<CiTriggerService>().LoadDefinitionAsync(await WithProjectAsync(repo), sha);
        Assert.NotNull(parsed?.Definition);
        Assert.Contains(parsed!.Definition!.Jobs, j => j.Name == "build");
    }

    [Fact]
    public async Task The_new_pipeline_file_name_wins_when_both_exist()
    {
        var (_, repo) = await SetupAsync();
        app.Commit(repo, "main", ".gitbench-ci.yml", "jobs:\n  old: { steps: [ { run: echo old } ] }\n");
        var sha = app.Commit(repo, "main", CiFile.FileName, "jobs:\n  fresh: { steps: [ { run: echo new } ] }\n");
        await using var s = app.Scope(system: true);
        var parsed = await s.Get<CiTriggerService>().LoadDefinitionAsync(await WithProjectAsync(repo), sha);
        Assert.Equal(["fresh"], parsed!.Definition!.Jobs.Select(j => j.Name).ToArray());
    }

    [Fact]
    public async Task Pre_rename_templates_and_pull_request_template_are_still_found()
    {
        var (project, repo) = await SetupAsync();
        app.Commit(repo, "main", ".gitbench/issue_templates/bug.md", "---\nname: Old bug\n---\nSteps:\n");
        var sha = app.Commit(repo, "main", ".gitbench/pull_request_template.md", "## Old PR template\n");
        var path = app.Services.GetService(typeof(ByteBench.Services.RepoStorage)) is ByteBench.Services.RepoStorage st ? st.PathFor(project.Key, repo.Slug) : "";
        Assert.Contains(".gitbench/pull_request_template.md", PullRequestTemplates.Paths);
        Assert.Equal(".gitbench/pull_request_template.md", PullRequestTemplates.Find(path, "main")?.Path);
        Assert.Equal(".gitbench/issue_templates", IssueTemplateService.LegacyRepoFolder);
        Assert.NotNull(sha);
    }

    [Fact]
    public void The_old_database_file_is_used_when_it_is_the_only_one()
    {
        var dir = Directory.CreateTempSubdirectory("rename-db").FullName;
        try
        {
            Assert.Equal(DatabaseSetup.DefaultSqliteConnection, DatabaseSetup.DefaultFor(dir));
            File.WriteAllText(Path.Combine(dir, "gitbench.db"), "");
            Assert.Equal(DatabaseSetup.LegacySqliteConnection, DatabaseSetup.DefaultFor(dir));
            File.WriteAllText(Path.Combine(dir, "bytebench.db"), "");
            Assert.Equal(DatabaseSetup.DefaultSqliteConnection, DatabaseSetup.DefaultFor(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Ci_variables_named_like_the_old_prefix_are_reserved()
    {
        Assert.NotNull(CiVariableService.ValidateName("GITBENCH_TOKEN"));
        Assert.NotNull(CiVariableService.ValidateName("BYTEBENCH_TOKEN"));
        Assert.Null(CiVariableService.ValidateName("MY_TOKEN"));
    }
}
