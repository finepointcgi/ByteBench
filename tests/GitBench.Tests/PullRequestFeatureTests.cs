using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GitBench.Data;
using GitBench.Services;
using GitBench.Services.Git;
using GitBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LgRepository = LibGit2Sharp.Repository;

namespace GitBench.Tests;

/// <summary>Drafts, comment threads, suggested changes, CODEOWNERS, squash/rebase merges and branch compare.</summary>
public class PullRequestFeatureTests(TestApp app) : IClassFixture<TestApp>
{
    private string RepoPath(Repository repo) => app.Services.GetRequiredService<RepoStorage>().PathFor(app.ProjectKey(repo.ProjectId), repo.Slug);

    private string CommitAs(Repository repo, string branch, Dictionary<string, string?> files, string message, string name, string email, string? from = "main") =>
        GitOps.CommitFiles(RepoPath(repo), branch, files, message, new GitIdentity(name, email), DateTimeOffset.UtcNow, from);

    private async Task<(Project Project, Repository Repo)> SetupAsync()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private,
            ("bob", ProjectRole.Developer), ("dan", ProjectRole.Developer));
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        return (project, repo);
    }

    private async Task<PullRequest> LoadAsync(Project project, Repository repo, int number)
    {
        await using var s = app.Scope(system: true);
        return (await s.Get<PullRequestService>().GetAsync(project.Key, repo.Slug, number))!;
    }

    // ---------- Drafts ----------

    [Fact]
    public async Task Draft_blocks_merge_and_holds_review_requests_until_ready()
    {
        var (project, repo) = await SetupAsync();
        app.Commit(repo, "feature/d", "d.txt", "d");
        var (bob, dan) = (app.UserId("bob"), app.UserId("dan"));
        await using var s = app.Scope(bob);
        var prs = s.Get<PullRequestService>();
        var pr = await prs.CreateAsync(repo, "Draft work", null, "feature/d", "main", bob, [dan], draft: true);

        await using (var db = app.Db())
        {
            Assert.True(await db.PullRequestReviewers.AnyAsync(r => r.PullRequestId == pr.Id && r.UserId == dan));
            Assert.False(await db.Notifications.AnyAsync(n => n.UserId == dan && n.ProjectId == project.Id && n.Kind == NotificationKind.ReviewRequested));
        }

        // Not protected at all, still blocked, and nobody can bypass it.
        var gate = await s.Get<MergeGate>().EvaluateAsync(pr.Id, app.UserId("carol"));
        Assert.True(gate.Blocked);
        Assert.False(gate.CanBypass);
        Assert.Contains(gate.Checks, c => c.Name == "Draft" && c.State == MergeCheckState.Failed);
        await using (var carol = app.Scope(app.UserId("carol")))
            await Assert.ThrowsAsync<BranchProtectedException>(() => carol.Get<PullRequestService>().MergeAsync(pr.Id, app.User("carol"), false, false));

        await prs.SetDraftAsync(pr.Id, bob, draft: false);
        await using (var db = app.Db())
            Assert.True(await db.Notifications.AnyAsync(n => n.UserId == dan && n.ProjectId == project.Id && n.Kind == NotificationKind.ReviewRequested));
        await using (var carol = app.Scope(app.UserId("carol")))
            Assert.True((await carol.Get<PullRequestService>().MergeAsync(pr.Id, app.User("carol"), false, false)).Success);
    }

    // ---------- Threads ----------

    [Fact]
    public async Task Required_thread_resolution_blocks_merge_until_resolved_and_outdated_threads_are_detected()
    {
        var (project, repo) = await SetupAsync();
        app.Commit(repo, "feature/t", "t.txt", "one\ntwo\nthree\n");
        var (bob, dan, carol) = (app.UserId("bob"), app.UserId("dan"), app.UserId("carol"));
        await using (var s = app.Scope(carol))
            await s.Get<BranchProtectionService>().SaveAsync(repo.Id, new BranchProtectionRule { Pattern = "main", RequireThreadResolution = true });

        await using var bs = app.Scope(bob);
        var prs = bs.Get<PullRequestService>();
        var pr = await prs.CreateAsync(repo, "Threads", null, "feature/t", "main", bob);
        PullRequestComment root, reply;
        await using (var ds = app.Scope(dan))
        {
            root = await ds.Get<PullRequestService>().AddCommentAsync(pr.Id, dan, "Rename this?", "t.txt", line: 2);
            reply = await ds.Get<PullRequestService>().AddCommentAsync(pr.Id, dan, "Also line 2 again", parentId: root.Id);
        }
        Assert.Equal(root.Id, reply.ParentId);
        Assert.Equal(("t.txt", 2), (reply.FilePath!, reply.Line!.Value));
        Assert.Equal("two", root.LineText);

        var gate = await bs.Get<MergeGate>().EvaluateAsync(pr.Id, carol);
        Assert.Contains("1 comment thread is unresolved", gate.Blockers);

        // A developer who is neither the PR's author nor the thread's author can't resolve it.
        var other = await app.CreateUserAsync("thread-outsider-" + Guid.NewGuid().ToString("N")[..6]);
        await using (var cs = app.Scope(carol))
            await cs.Get<MemberService>().AddMemberAsync(project.Id, other.UserName!, ProjectRole.Developer);
        await using (var os = app.Scope(other.Id))
            await Assert.ThrowsAsync<ForbiddenException>(() => os.Get<PullRequestService>().SetThreadResolvedAsync(reply.Id, other.Id, true));

        // The PR's author can (naming any comment of the thread).
        await prs.SetThreadResolvedAsync(reply.Id, bob, true);
        Assert.False((await bs.Get<MergeGate>().EvaluateAsync(pr.Id, carol)).Blocked);
        await prs.SetThreadResolvedAsync(root.Id, bob, false);
        Assert.True((await bs.Get<MergeGate>().EvaluateAsync(pr.Id, carol)).Blocked);

        // A later push that changes line 2 makes the thread outdated; one that doesn't, doesn't.
        var loaded = await LoadAsync(project, repo, pr.Number);
        var head = GitOps.ResolveSha(RepoPath(repo), "feature/t");
        Assert.Empty((await prs.ThreadsAsync(loaded, head)).Outdated);
        var moved = app.Commit(repo, "feature/t", "t.txt", "one\nTWO\nthree\n");
        var summary = await prs.ThreadsAsync(loaded, moved);
        Assert.Equal([root.Id], summary.Outdated);
        Assert.Equal(1, summary.Unresolved);
    }

    // ---------- Suggested changes ----------

    [Fact]
    public void Suggestion_blocks_are_parsed()
    {
        var s = Suggestions.Parse("Better:\n```suggestion\nfoo\n  bar\n```\nthanks")!;
        Assert.Equal(["foo", "  bar"], s.Lines);
        Assert.Equal("Better:", s.Before);
        Assert.Equal("thanks", s.After);
        Assert.Empty(Suggestions.Parse("```suggestion\n```")!.Lines);          // delete the line(s)
        Assert.Equal([""], Suggestions.Parse("```suggestion\n\n```")!.Lines);  // one blank line
        Assert.Null(Suggestions.Parse("```csharp\nx\n```"));
    }

    [Fact]
    public async Task Applying_a_suggestion_commits_to_the_source_branch_and_refuses_stale_lines()
    {
        var (project, repo) = await SetupAsync();
        app.Commit(repo, "main", "app.txt", "alpha\nbeta\ngamma\ndelta\n", from: null);
        app.Commit(repo, "feature/s", "app.txt", "alpha\nBeta\ngamma\ndelta\n");
        var (bob, dan) = (app.UserId("bob"), app.UserId("dan"));
        await using var bs = app.Scope(bob);
        var prs = bs.Get<PullRequestService>();
        var pr = await prs.CreateAsync(repo, "Suggest", null, "feature/s", "main", bob);
        PullRequestComment one, two, stale;
        await using (var ds = app.Scope(dan))
        {
            var dprs = ds.Get<PullRequestService>();
            one = await dprs.AddCommentAsync(pr.Id, dan, "Lowercase:\n```suggestion\nbeta\n```", "app.txt", line: 2);
            two = await dprs.AddCommentAsync(pr.Id, dan, "```suggestion\nGAMMA\nGamma 2\n```", "app.txt", line: 3);
            stale = await dprs.AddCommentAsync(pr.Id, dan, "```suggestion\nDELTA\n```", "app.txt", line: 4);
        }
        var path = RepoPath(repo);
        var before = GitOps.ResolveSha(path, "feature/s");

        // Batch: one commit, authored by the applier, co-authored by the suggester.
        var sha = await prs.ApplySuggestionsAsync(pr.Id, app.User("bob"), [one.Id, two.Id]);
        Assert.Equal(sha, GitOps.ResolveSha(path, "feature/s"));
        Assert.Equal("alpha\nbeta\nGAMMA\nGamma 2\ndelta\n", GitOps.GetBlob(path, "feature/s", "app.txt")!.Text);
        var commit = GitOps.GetCommit(path, sha)!;
        Assert.Equal([before!], commit.ParentShas);
        Assert.StartsWith("Apply 2 suggestions from @dan", commit.Message);
        Assert.Contains("Co-authored-by:", commit.Message);
        Assert.Equal(app.User("bob").Email, commit.AuthorEmail);
        await using (var db = app.Db())
        {
            var applied = await db.PullRequestComments.Where(c => c.Id == one.Id || c.Id == two.Id).ToListAsync();
            Assert.All(applied, c => { Assert.Equal(sha, c.AppliedCommitSha); Assert.True(c.IsResolved); });
        }

        // Line 4 moved to line 5 (a line was inserted above): the suggestion is stale and nothing is written.
        await Assert.ThrowsAsync<GitConflictException>(() => prs.ApplySuggestionsAsync(pr.Id, app.User("bob"), [stale.Id]));
        Assert.Equal(sha, GitOps.ResolveSha(path, "feature/s"));

        // A fresh suggestion on the new line 5 applies on its own ("Apply suggestion from @dan").
        PullRequestComment fresh;
        await using (var ds = app.Scope(dan))
            fresh = await ds.Get<PullRequestService>().AddCommentAsync(pr.Id, dan, "```suggestion\nDELTA\n```", "app.txt", line: 5);
        var sha2 = await prs.ApplySuggestionsAsync(pr.Id, app.User("bob"), [fresh.Id]);
        Assert.StartsWith("Apply suggestion from @dan", GitOps.GetCommit(path, sha2)!.Message);
        Assert.EndsWith("delta".ToUpperInvariant() + "\n", GitOps.GetBlob(path, "feature/s", "app.txt")!.Text);

        // Branch protection on the source branch is respected.
        await using (var cs = app.Scope(app.UserId("carol")))
            await cs.Get<BranchProtectionService>().SaveAsync(repo.Id, new BranchProtectionRule { Pattern = "feature/*", BlockDirectPushes = true });
        PullRequestComment blocked;
        await using (var ds = app.Scope(dan))
            blocked = await ds.Get<PullRequestService>().AddCommentAsync(pr.Id, dan, "```suggestion\nALPHA\n```", "app.txt", line: 1);
        await Assert.ThrowsAsync<BranchProtectedException>(() => prs.ApplySuggestionsAsync(pr.Id, app.User("bob"), [blocked.Id]));
        Assert.False(await prs.CanApplySuggestionsAsync(await LoadAsync(project, repo, pr.Number), bob));
    }

    // ---------- Code owners ----------

    [Theory]
    [InlineData("*", "any/file.txt", true)]
    [InlineData("*.md", "docs/deep/readme.md", true)]
    [InlineData("*.md", "readme.mdx", false)]
    [InlineData("/build/", "build/out/a.bin", true)]
    [InlineData("/build/", "src/build/a.bin", false)]
    [InlineData("apps/", "x/apps/main.c", true)]
    [InlineData("docs/*", "docs/getting-started.md", true)]
    [InlineData("docs/*", "docs/build/troubleshooting.md", false)]
    [InlineData("src/**/test.cs", "src/a/b/test.cs", true)]
    [InlineData("src/**/test.cs", "src/test.cs", true)]
    [InlineData("/src", "src/x.cs", true)]
    [InlineData("lib", "a/lib/x.cs", true)]
    [InlineData("a?c.txt", "abc.txt", true)]
    [InlineData("a?c.txt", "a/c.txt", false)]
    public void Codeowners_patterns_match_like_github(string pattern, string path, bool matches) =>
        Assert.Equal(matches, CodeOwnersFile.Parse($"{pattern} @someone").RuleFor(path) != null);

    [Fact]
    public void Codeowners_last_matching_rule_wins_and_comments_are_ignored()
    {
        var f = CodeOwnersFile.Parse("# owners\n*       @alice   # everyone\n*.js    @bob dev@example.com\n/vendor/\n!bad @x\n");
        Assert.Equal(["@alice"], f.OwnersOf("README"));
        Assert.Equal(["@bob", "dev@example.com"], f.OwnersOf("src/app.js"));
        Assert.Empty(f.OwnersOf("vendor/lib.js"));    // a later rule without owners clears ownership
        Assert.Single(f.Errors);                       // "!" isn't supported
    }

    [Fact]
    public async Task Code_owners_are_requested_as_reviewers_and_gate_the_merge()
    {
        var (project, repo) = await SetupAsync();
        CommitAs(repo, "main", new() { [".gitbench/CODEOWNERS"] = "*.md @dan\n/src/ @carol @team/backend\n" }, "owners", "Carol", "carol@example.com", from: null);
        CommitAs(repo, "feature/o", new() { ["src/a.cs"] = "class A {}", ["README.md"] = "# hi" }, "change", "Bob", "bob@example.com");
        var (bob, dan, carol) = (app.UserId("bob"), app.UserId("dan"), app.UserId("carol"));
        await using (var s = app.Scope(carol))
            await s.Get<BranchProtectionService>().SaveAsync(repo.Id, new BranchProtectionRule { Pattern = "main", RequireCodeOwnerApproval = true });

        await using var bs = app.Scope(bob);
        var pr = await bs.Get<PullRequestService>().CreateAsync(repo, "Owned", null, "feature/o", "main", bob);
        await using (var db = app.Db())
            Assert.Equal([carol, dan], (await db.PullRequestReviewers.Where(r => r.PullRequestId == pr.Id).Select(r => r.UserId).ToListAsync()).Order());

        var report = await bs.Get<CodeOwnersService>().ForPullRequestAsync(await LoadAsync(project, repo, pr.Number));
        Assert.Equal(".gitbench/CODEOWNERS", report.FilePath);
        Assert.Equal(["Carol"], report.Owners["src/a.cs"].Select(u => u.UserName == "carol" ? "Carol" : u.UserName));
        Assert.Contains(report.Warnings, w => w.Contains("@team/backend"));

        var gate = bs.Get<MergeGate>();
        Assert.Contains("code owners haven't approved 2 files", (await gate.EvaluateAsync(pr.Id, carol)).Blockers);
        await using (var ds = app.Scope(dan)) await ds.Get<PullRequestService>().SetReviewAsync(pr.Id, dan, ReviewState.Approved);
        Assert.Contains("code owners haven't approved 1 file", (await gate.EvaluateAsync(pr.Id, carol)).Blockers);
        await using (var cs = app.Scope(carol)) await cs.Get<PullRequestService>().SetReviewAsync(pr.Id, carol, ReviewState.Approved);
        Assert.False((await gate.EvaluateAsync(pr.Id, carol)).Blocked);
    }

    // ---------- Merge strategies ----------

    [Fact]
    public async Task Squash_writes_one_commit_with_the_same_tree_and_co_authors()
    {
        var (project, repo) = await SetupAsync();
        CommitAs(repo, "feature/sq", new() { ["a.txt"] = "a" }, "Add a", "Bob", app.User("bob").Email!);
        CommitAs(repo, "feature/sq", new() { ["b.txt"] = "b" }, "Add b", "Dan", app.User("dan").Email!);
        CommitAs(repo, "feature/sq", new() { ["a.txt"] = "a2" }, "Edit a", "Bob", app.User("bob").Email!);
        var path = RepoPath(repo);
        var mainBefore = GitOps.ResolveSha(path, "main")!;
        var bob = app.UserId("bob");
        int prId;
        await using (var s = app.Scope(bob))
            prId = (await s.Get<PullRequestService>().CreateAsync(repo, "Squash me", null, "feature/sq", "main", bob)).Id;

        await using var cs = app.Scope(app.UserId("carol"));
        var outcome = await cs.Get<PullRequestService>().MergeAsync(prId, app.User("carol"), new MergeRequest(MergeStrategy.Squash));
        Assert.True(outcome.Success, outcome.Error);
        using (var r = new LgRepository(path))
        {
            var tip = r.Branches["main"].Tip;
            Assert.Equal([mainBefore], tip.Parents.Select(p => p.Sha));                 // exactly one new commit
            Assert.Equal(r.Branches["feature/sq"].Tip.Tree.Sha, tip.Tree.Sha);         // same content as the branch
            Assert.Equal(app.User("bob").Email, tip.Author.Email);                       // authored by the PR author
            Assert.StartsWith("Squash me (#1)", tip.Message);
            Assert.Contains($"Co-authored-by: Dan <{app.User("dan").Email}>", tip.Message);
            Assert.DoesNotContain("Co-authored-by: Bob", tip.Message);
            Assert.Contains("Merge-Strategy: squash", tip.Message);
        }
        await using var db = app.Db();
        Assert.Equal(MergeStrategy.Squash, (await db.PullRequests.FindAsync(prId))!.MergeStrategy);
    }

    [Fact]
    public async Task Rebase_replays_commits_onto_the_target_and_refuses_conflicts()
    {
        var (project, repo) = await SetupAsync();
        var path = RepoPath(repo);
        CommitAs(repo, "feature/rb", new() { ["r1.txt"] = "1" }, "First", "Dan", "dan@example.com");
        CommitAs(repo, "feature/rb", new() { ["r2.txt"] = "2" }, "Second", "Dan", "dan@example.com");
        app.Commit(repo, "main", "other.txt", "main moved on", from: null);   // target diverged
        var mainBefore = GitOps.ResolveSha(path, "main")!;
        var bob = app.UserId("bob");
        int prId, conflictId;
        await using (var s = app.Scope(bob))
        {
            prId = (await s.Get<PullRequestService>().CreateAsync(repo, "Rebase me", null, "feature/rb", "main", bob)).Id;
            app.Commit(repo, "feature/cf", "README.md", "feature version");
            conflictId = (await s.Get<PullRequestService>().CreateAsync(repo, "Conflicts", null, "feature/cf", "main", bob)).Id;
        }
        app.Commit(repo, "main", "README.md", "main version", from: null);

        await using var cs = app.Scope(app.UserId("carol"));
        var prs = cs.Get<PullRequestService>();
        var mainBeforeRebase = GitOps.ResolveSha(path, "main")!;
        var outcome = await prs.MergeAsync(prId, app.User("carol"), new MergeRequest(MergeStrategy.Rebase));
        Assert.True(outcome.Success, outcome.Error);
        using (var r = new LgRepository(path))
        {
            var tip = r.Branches["main"].Tip;
            var second = tip;
            var first = tip.Parents.Single();
            Assert.Equal(mainBeforeRebase, first.Parents.Single().Sha);                  // two new linear commits
            Assert.Equal(["Second", "First"], new[] { second, first }.Select(c => c.MessageShort));
            Assert.Equal("dan@example.com", first.Author.Email);                        // authors kept
            Assert.NotNull(tip["r1.txt"]); Assert.NotNull(tip["r2.txt"]); Assert.NotNull(tip["other.txt"]);
        }
        Assert.NotEqual(mainBefore, mainBeforeRebase);

        // Conflicts: refused cleanly, nothing moves, PR stays open.
        var tipBefore = GitOps.ResolveSha(path, "main");
        var refused = await prs.MergeAsync(conflictId, app.User("carol"), new MergeRequest(MergeStrategy.Rebase));
        Assert.False(refused.Success);
        Assert.Equal(["README.md"], refused.ConflictPaths);
        Assert.Equal(tipBefore, GitOps.ResolveSha(path, "main"));
        Assert.False((await prs.MergeAsync(conflictId, app.User("carol"), new MergeRequest(MergeStrategy.Squash))).Success);
        await using var db = app.Db();
        Assert.Equal(PullRequestState.Open, (await db.PullRequests.FindAsync(conflictId))!.State);
    }

    [Fact]
    public async Task Repositories_restrict_merge_strategies()
    {
        var (project, repo) = await SetupAsync();
        app.Commit(repo, "feature/m", "m.txt", "m");
        var carol = app.UserId("carol");
        await using var s = app.Scope(carol);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            s.Get<RepositorySettingsService>().SetMergeStrategiesAsync(repo.Id, MergeStrategies.Merge, MergeStrategy.Squash, carol));
        await s.Get<RepositorySettingsService>().SetMergeStrategiesAsync(repo.Id, MergeStrategies.Merge | MergeStrategies.Rebase, MergeStrategy.Rebase, carol);
        var pr = await s.Get<PullRequestService>().CreateAsync(repo, "M", null, "feature/m", "main", app.UserId("bob"));
        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            s.Get<PullRequestService>().MergeAsync(pr.Id, app.User("carol"), new MergeRequest(MergeStrategy.Squash)));
        Assert.Contains("doesn't allow squash", ex.Message);
        Assert.Equal(MergeStrategy.Rebase, MergeStrategySettings.Default((await LoadAsync(project, repo, pr.Number)).Repository));
    }

    // ---------- API ----------

    [Fact]
    public async Task Api_supports_drafts_threads_suggestions_strategies_and_compare()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("dan", ProjectRole.Maintainer));
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        app.Commit(repo, "main", "f.txt", "x\ny\n", from: null);
        app.Commit(repo, "feature/api", "f.txt", "x\nY\n");
        var bob = await app.ApiClientAsync("bob");
        var dan = await app.ApiClientAsync("dan");
        var baseUrl = $"/api/v1/projects/{project.Key}/repos/{repo.Slug}";

        var created = await bob.PostAsJsonAsync($"{baseUrl}/pull-requests", new { title = "API", sourceBranch = "feature/api", targetBranch = "main", draft = true });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var pr = await Json(created);
        Assert.True(pr.GetProperty("isDraft").GetBoolean());
        var n = pr.GetProperty("number").GetInt32();

        var blocked = await dan.PostAsJsonAsync($"{baseUrl}/pull-requests/{n}/merge", new { strategy = "squash" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);

        var comment = await Json(await dan.PostAsJsonAsync($"{baseUrl}/pull-requests/{n}/comments", new { body = "```suggestion\nwhy\n```", path = "f.txt", line = 2 }));
        Assert.True(comment.GetProperty("hasSuggestion").GetBoolean());
        var cid = comment.GetProperty("id").GetInt32();
        var reply = await Json(await bob.PostAsJsonAsync($"{baseUrl}/pull-requests/{n}/comments", new { body = "ok", parentId = cid }));
        Assert.Equal(cid, reply.GetProperty("parentId").GetInt32());

        var applied = await bob.PostAsync($"{baseUrl}/pull-requests/{n}/comments/{cid}/apply-suggestion", null);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await bob.PostAsync($"{baseUrl}/pull-requests/{n}/comments/{cid}/apply-suggestion", null)).StatusCode);
        var unresolved = await Json(await bob.PostAsync($"{baseUrl}/pull-requests/{n}/comments/{cid}/unresolve", null));
        Assert.False(unresolved.GetProperty("resolved").GetBoolean());
        Assert.True((await Json(await bob.PostAsync($"{baseUrl}/pull-requests/{n}/comments/{reply.GetProperty("id").GetInt32()}/resolve", null))).GetProperty("resolved").GetBoolean());

        var cmp = await Json(await bob.GetAsync($"{baseUrl}/compare?base=main&head=feature/api"));
        Assert.Equal(2, cmp.GetProperty("ahead").GetInt32());
        Assert.Equal("f.txt", cmp.GetProperty("files")[0].GetProperty("path").GetString());

        var ready = await Json(await bob.PatchAsJsonAsync($"{baseUrl}/pull-requests/{n}", new { draft = false }));
        Assert.False(ready.GetProperty("isDraft").GetBoolean());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await dan.PostAsJsonAsync($"{baseUrl}/pull-requests/{n}/merge", new { strategy = "octopus" })).StatusCode);
        var merged = await dan.PostAsJsonAsync($"{baseUrl}/pull-requests/{n}/merge", new { strategy = "squash", commitMessage = "Custom squash" });
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
        Assert.Equal("squash", (await Json(merged)).GetProperty("pullRequest").GetProperty("mergeStrategy").GetString());
        Assert.StartsWith("Custom squash", GitOps.GetCommit(RepoPath(repo), "main")!.Message);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }
}
