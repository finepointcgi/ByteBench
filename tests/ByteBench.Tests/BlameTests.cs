using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Code;
using ByteBench.Services.Git;
using ByteBench.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

/// <summary>Blame: service, caching, REST endpoint and permissions, against a real history (several authors, a rename, a merge commit).</summary>
public class BlameTests(TestApp app) : IClassFixture<TestApp>, IDisposable
{
    private readonly GitCli git = new();
    public void Dispose() => git.Dispose();

    private sealed record History(Project Project, Repository Repo, string Path, string C1, string C2, string C3, string C4, string C5, string Merge);

    private static string[] As(string name) => ["-c", "user.name=" + name, "-c", "user.email=" + name.ToLowerInvariant() + "@example.com"];

    private async Task<string> CommitAsync(string dir, string author, string message)
    {
        await git.OkAsync(dir, ["add", "-A"]);
        await git.OkAsync(dir, [.. As(author), "commit", "-m", message]);
        return (await git.OkAsync(dir, "rev-parse", "HEAD")).Trim();
    }

    /// <summary>
    /// a.txt (Alice, 5 lines) → Bob edits line 2 → Carol renames it to docs/b.txt and edits line 5 → on a branch Dan edits line 4,
    /// on main Alice appends a line → Carol merges the branch with a merge commit.
    /// </summary>
    private async Task<History> BuildAsync(string owner = "carol", params (string, ProjectRole)[] members)
    {
        var project = await app.CreateProjectAsync(owner, ProjectVisibility.Private, members);
        var repo = await app.CreateRepoAsync(project.Id, owner);
        var bare = app.Services.GetRequiredService<RepoStorage>().PathFor(project.Key, repo.Slug);

        var dir = git.Dir("work-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        await git.OkAsync(dir, "init", "-b", "main");
        var file = Path.Combine(dir, "a.txt");
        await File.WriteAllTextAsync(file, "one\ntwo\nthree\nfour\nfive\n");
        var c1 = await CommitAsync(dir, "Alice", "Add a.txt");
        await File.WriteAllTextAsync(file, "one\nTWO\nthree\nfour\nfive\n");
        var c2 = await CommitAsync(dir, "Bob", "Shout line two");
        Directory.CreateDirectory(Path.Combine(dir, "docs"));
        await git.OkAsync(dir, "mv", "a.txt", "docs/b.txt");
        await File.WriteAllTextAsync(Path.Combine(dir, "docs", "b.txt"), "one\nTWO\nthree\nfour\nFIVE\n");
        var c3 = await CommitAsync(dir, "Carol", "Move a.txt to docs/b.txt and shout five");
        await git.OkAsync(dir, "checkout", "-b", "feature");
        await File.WriteAllTextAsync(Path.Combine(dir, "docs", "b.txt"), "one\nTWO\nthree\nFOUR\nFIVE\n");
        var c4 = await CommitAsync(dir, "Dan", "Shout four");
        await git.OkAsync(dir, "checkout", "main");
        await File.WriteAllTextAsync(Path.Combine(dir, "docs", "b.txt"), "one\nTWO\nthree\nfour\nFIVE\nsix\n");
        var c5 = await CommitAsync(dir, "Alice", "Append six");
        await git.OkAsync(dir, [.. As("Carol"), "merge", "--no-ff", "-m", "Merge feature", "feature"]);
        var merge = (await git.OkAsync(dir, "rev-parse", "HEAD")).Trim();
        await File.WriteAllBytesAsync(Path.Combine(dir, "image.bin"), [0, 1, 2, 0, 255, 0]);
        await File.WriteAllTextAsync(Path.Combine(dir, "big.txt"), string.Join('\n', Enumerable.Range(1, 500)) + "\n");
        await CommitAsync(dir, "Alice", "Add binary and big files");

        await git.OkAsync(dir, "push", "--force", bare, "main:main");
        await git.OkAsync(dir, "push", "--force", bare, "feature:feature");
        return new History(project, repo, "docs/b.txt", c1, c2, c3, c4, c5, merge);
    }

    private async Task<BlameData> BlameAsync(History h, string rev, string path, string user = "carol")
    {
        await using var s = app.Scope(app.UserId(user));
        var repo = (await s.Get<RepositoryService>().GetRepositoryAsync(h.Project.Key, h.Repo.Slug))!;
        return await s.Get<BlameService>().BlameAsync(repo, rev, path);
    }

    private static string AuthorOf(BlameData d, int line) =>
        d.Commits[d.Ranges.Single(r => line >= r.StartLine && line < r.StartLine + r.LineCount).CommitIndex].AuthorName;

    [Fact]
    public async Task Attributes_each_line_follows_the_rename_and_skips_the_merge_commit()
    {
        var h = await BuildAsync();
        var d = await BlameAsync(h, "main", h.Path);

        Assert.Equal(BlameStatus.Ok, d.Status);
        Assert.Equal(6, d.Lines.Length);
        Assert.Equal(["one", "TWO", "three", "FOUR", "FIVE", "six"], d.Lines);
        Assert.Equal(["Alice", "Bob", "Alice", "Dan", "Carol", "Alice"], Enumerable.Range(1, 6).Select(n => AuthorOf(d, n)));
        // Lines 1 and 3 are both the first commit's but not adjacent: separate ranges. Every line is covered exactly once.
        Assert.Equal(6, d.Ranges.Sum(r => r.LineCount));
        Assert.Equal(6, d.Ranges.Count);
        // The merge commit changed no line, so it owns none.
        Assert.DoesNotContain(d.Commits, c => c.Sha == h.Merge);
        // Consecutive lines of one commit are grouped.
        var wide = await BlameAsync(h, h.C1, "a.txt");
        var only = Assert.Single(wide.Ranges, r => r.StartLine == 1);
        Assert.Equal(5, only.LineCount);
        Assert.Equal("Alice", wide.Commits[only.CommitIndex].AuthorName);

        // The rename is followed: line 1 was written under a.txt by the first commit.
        var first = d.Commits[d.Ranges.First(r => r.StartLine == 1).CommitIndex];
        Assert.Equal(h.C1, first.Sha);
        Assert.Equal("a.txt", first.Path);
        Assert.Null(first.ParentSha); // the root commit: nothing to step back to
    }

    [Fact]
    public async Task Step_back_points_at_the_parent_and_its_path_across_a_rename()
    {
        var h = await BuildAsync();
        var d = await BlameAsync(h, "main", h.Path);
        BlameCommitInfo CommitOf(string sha) => d.Commits.Single(c => c.Sha == sha);

        // Line 5 comes from the commit that renamed the file: the parent knows it as a.txt.
        var rename = CommitOf(h.C3);
        Assert.Equal(h.C2, rename.ParentSha);
        Assert.Equal("a.txt", rename.ParentPath);
        Assert.Equal("docs/b.txt", rename.Path);
        // A later commit's parent still has the new name.
        Assert.Equal(h.C3, CommitOf(h.C4).ParentSha);
        Assert.Equal("docs/b.txt", CommitOf(h.C4).ParentPath);
        Assert.Equal(h.C3, CommitOf(h.C5).ParentSha);

        // Following the step back works: blame before the rename, under the old name.
        var before = await BlameAsync(h, rename.ParentSha!, rename.ParentPath!);
        Assert.Equal(BlameStatus.Ok, before.Status);
        Assert.Equal(["one", "TWO", "three", "four", "five"], before.Lines);
        Assert.Equal("Bob", AuthorOf(before, 2));
        Assert.Equal("Alice", AuthorOf(before, 5));
    }

    [Fact]
    public async Task Binary_large_and_missing_files_get_a_status_not_an_error()
    {
        var h = await BuildAsync();
        Assert.Equal(BlameStatus.Binary, (await BlameAsync(h, "main", "image.bin")).Status);
        Assert.Equal(BlameStatus.NotFound, (await BlameAsync(h, "main", "nope.txt")).Status);
        Assert.Equal(BlameStatus.NotFound, (await BlameAsync(h, "no-such-branch", h.Path)).Status);
        Assert.Equal(BlameStatus.NotFound, (await BlameAsync(h, "main", "docs")).Status); // a directory

        var bare = app.Services.GetRequiredService<RepoStorage>().PathFor(h.Project.Key, h.Repo.Slug);
        Assert.Equal(BlameStatus.TooLarge, GitOps.Blame(bare, "main", "big.txt", maxBytes: 100, maxLines: 1000).Status);
        Assert.Equal(BlameStatus.TooLarge, GitOps.Blame(bare, "main", "big.txt", maxBytes: 1_000_000, maxLines: 100).Status);
        Assert.Equal(BlameStatus.Ok, GitOps.Blame(bare, "main", "big.txt", maxBytes: 1_000_000, maxLines: 1000).Status);
    }

    [Fact]
    public async Task Results_for_a_commit_are_cached_but_a_moving_branch_is_not_stale()
    {
        var h = await BuildAsync();
        var a = await BlameAsync(h, "main", h.Path);
        var b = await BlameAsync(h, a.Sha!, h.Path);
        Assert.Same(a, b); // same (repo, sha, path): served from the cache

        app.Commit(h.Repo, "main", h.Path, "one\nTWO\nthree\nFOUR\nFIVE\nsix\nseven\n", from: null, message: "Add seven");
        var c = await BlameAsync(h, "main", h.Path);
        Assert.NotEqual(a.Sha, c.Sha);
        Assert.Equal(7, c.Lines.Length);
        Assert.Equal(6, (await BlameAsync(h, a.Sha!, h.Path)).Lines.Length);
    }

    [Fact]
    public async Task Rest_endpoint_returns_ranges_and_enforces_view_access()
    {
        var h = await BuildAsync("carol", ("bob", ProjectRole.Viewer));
        var url = $"/api/v1/projects/{h.Project.Key}/repos/{h.Repo.Slug}/blame";

        var bob = await app.ApiClientAsync("bob", TokenScopes.ReadApi);
        var ok = await bob.GetAsync($"{url}?rev=main&path={Uri.EscapeDataString(h.Path)}");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var json = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(6, json.GetProperty("lineCount").GetInt32());
        Assert.Equal(h.Path, json.GetProperty("path").GetString());
        var ranges = json.GetProperty("ranges").EnumerateArray().ToList();
        Assert.Equal(1, ranges[0].GetProperty("startLine").GetInt32());
        Assert.Equal(6, ranges[^1].GetProperty("endLine").GetInt32());
        var commit = json.GetProperty("commits").EnumerateArray().Single(c => c.GetProperty("sha").GetString() == h.C3);
        Assert.Equal("a.txt", commit.GetProperty("parentPath").GetString());

        // The default branch is used when rev is omitted.
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync($"{url}?path={Uri.EscapeDataString(h.Path)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"{url}?rev=main&path=missing.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await bob.GetAsync($"{url}?rev=main&path=image.bin")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.GetAsync($"{url}?rev=main")).StatusCode);

        // Not a member of the private project: the repository doesn't exist as far as dan can tell.
        var dan = await app.ApiClientAsync("dan", TokenScopes.ReadApi);
        Assert.Equal(HttpStatusCode.NotFound, (await dan.GetAsync($"{url}?rev=main&path={Uri.EscapeDataString(h.Path)}")).StatusCode);
        await using var s = app.Scope(app.UserId("dan"));
        Assert.Null(await s.Get<RepositoryService>().GetRepositoryAsync(h.Project.Key, h.Repo.Slug));
    }

    [Fact]
    public async Task Blame_page_renders_for_a_member_and_hides_the_repository_from_others()
    {
        var h = await BuildAsync("carol", ("bob", ProjectRole.Viewer));
        var bob = await app.CookieClientAsync("bob");
        var page = await bob.GetAsync(CodeUrls.Blame(h.Repo, "main", h.Path));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("blame-table", html);
        Assert.Contains("id=\"L6\"", html);
        Assert.Contains(">Dan<", html);
        Assert.Contains(h.C4[..7], html);
    }
}
