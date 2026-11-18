using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitBench.Data;
using GitBench.Services;
using GitBench.Services.Git;
using GitBench.Services.Lfs;
using GitBench.Services.Mirroring;
using GitBench.Services.Releases;
using GitBench.Services.Webhooks;
using GitBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GitBench.Tests;

/// <summary>Protected tags (push and UI), pull and push mirrors (against repositories on the same server) and the Git LFS API.</summary>
public class MirrorTagsLfsTests(KestrelApp app) : IClassFixture<KestrelApp>, IDisposable
{
    private readonly GitCli git = new();
    private const string Password = SeedData.DemoPassword;
    private const string LfsJson = "application/vnd.git-lfs+json";

    public void Dispose() => git.Dispose();

    private async Task<(Project Project, Repository Repo)> SetupAsync(string name = "code")
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private,
            ("bob", ProjectRole.Developer), ("dan", ProjectRole.Maintainer));
        var repo = await app.CreateRepoAsync(project.Id, "carol", name);
        return (project, repo);
    }

    private async Task<string> CloneAsync(Project p, Repository r, string user)
    {
        var dir = git.Dir("clone-" + Guid.NewGuid().ToString("N")[..8]);
        await git.OkAsync(null, "clone", app.GitUrl(p.Key, r.Slug, user, Password), dir);
        return dir;
    }

    private async Task CommitAsync(string dir, string file, string content)
    {
        await File.WriteAllTextAsync(Path.Combine(dir, file), content);
        await git.OkAsync(dir, "add", file);
        await git.OkAsync(dir, "commit", "-m", "edit " + file);
    }

    private string RepoPath(Project p, Repository r) => app.Services.GetRequiredService<RepoStorage>().PathFor(p.Key, r.Slug);

    /// <summary>refs/heads and refs/tags of a bare repository: ref → sha.</summary>
    private async Task<Dictionary<string, string>> RefsAsync(string bare)
    {
        var output = await git.OkAsync(null, "--git-dir=" + bare, "for-each-ref", "--format=%(refname) %(objectname)", "refs/heads", "refs/tags");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(' ')).ToDictionary(a => a[0], a => a[1]);
    }

    /// <summary>The clone URL without credentials (what a mirror stores).</summary>
    private string PlainUrl(Project p, Repository r) => new Uri(app.ServerAddress, $"/git/{p.Key}/{r.Slug}.git").ToString();

    // ================================================================ protected tags

    [Fact]
    public async Task Protected_tag_pushes_are_refused_over_http()
    {
        var (p, r) = await SetupAsync();
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<ProtectedTagService>().SaveAsync(r.Id, new ProtectedTagRule { Pattern = "v*", CreateRole = ProjectRole.Maintainer, BlockDeletion = true, BlockUpdate = true });

        // A Developer can't create a protected tag...
        var bobDir = await CloneAsync(p, r, "bob");
        await git.OkAsync(bobDir, "tag", "v1.0");
        var push = await git.RunAsync(bobDir, "push", "origin", "v1.0");
        Assert.False(push.Ok);
        Assert.Contains("[remote rejected] v1.0 -> v1.0", push.Output);
        Assert.Contains("only Maintainers and Owners can create it", push.Output);
        // ...but an unprotected one is fine.
        await git.OkAsync(bobDir, "tag", "build-1");
        await git.OkAsync(bobDir, "push", "origin", "build-1");

        // A Maintainer can create it, but not delete or move it.
        var danDir = await CloneAsync(p, r, "dan");
        await git.OkAsync(danDir, "tag", "v1.0");
        await git.OkAsync(danDir, "push", "origin", "v1.0");

        var delete = await git.RunAsync(danDir, "push", "origin", ":refs/tags/v1.0");
        Assert.False(delete.Ok);
        Assert.Contains("tag 'v1.0' is protected and can't be deleted", delete.Output);

        await CommitAsync(danDir, "f.txt", "moved");
        await git.OkAsync(danDir, "tag", "-f", "v1.0");
        var move = await git.RunAsync(danDir, "push", "--force", "origin", "v1.0");
        Assert.False(move.Ok);
        Assert.Contains("can't be moved", move.Output);

        var refs = await RefsAsync(RepoPath(p, r));
        Assert.True(refs.ContainsKey("refs/tags/v1.0"));
        Assert.True(refs.ContainsKey("refs/tags/build-1"));

        // Rule changes are audited.
        await using var db = app.Db();
        Assert.Contains(await db.AuditEvents.Where(e => e.ProjectId == p.Id).Select(e => e.Action).ToListAsync(), a => a == "protected_tag.created");
    }

    [Fact]
    public async Task Protected_tags_are_enforced_by_the_tag_service()
    {
        var (_, r) = await SetupAsync();
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<ProtectedTagService>().SaveAsync(r.Id, new ProtectedTagRule { Pattern = "release/**", CreateRole = ProjectRole.Maintainer, BlockDeletion = true });

        await using (var s = app.Scope(app.UserId("bob")))
        {
            var ex = await Assert.ThrowsAsync<BranchProtectedException>(() => s.Get<ReleaseService>().CreateTagAsync(r, "release/1.0", "main", null));
            Assert.Contains("only Maintainers and Owners can create it", ex.Message);
            await s.Get<ReleaseService>().CreateTagAsync(r, "nightly", "main", null); // not protected
        }

        await using (var s = app.Scope(app.UserId("dan")))
        {
            var releases = s.Get<ReleaseService>();
            await releases.CreateTagAsync(r, "release/1.0", "main", "First");
            var ex = await Assert.ThrowsAsync<BranchProtectedException>(() => releases.DeleteTagAsync(r, "release/1.0"));
            Assert.Contains("is protected and can't be deleted", ex.Message);
            await releases.DeleteTagAsync(r, "nightly"); // unprotected: a Maintainer can delete it

            // A release whose tag is protected can be deleted, but "delete the tag too" is refused up front.
            var release = await releases.CreateAsync(r, new ReleaseInput { TagName = "release/1.0", Title = "1.0" });
            await Assert.ThrowsAsync<BranchProtectedException>(() => releases.DeleteAsync(r, release.Id, deleteTag: true));
            Assert.NotNull(await releases.GetAsync(r, release.Id));
        }

        // Owners can't delete it either while the rule blocks deletion; removing the rule lifts it.
        await using (var s = app.Scope(app.UserId("carol")))
        {
            var rule = Assert.Single(await s.Get<ProtectedTagService>().ListAsync(r.Id));
            await s.Get<ProtectedTagService>().DeleteAsync(r.Id, rule.Id);
            await s.Get<ReleaseService>().CreateTagAsync(r, "release/2.0", "main", null);
            await s.Get<ReleaseService>().DeleteTagAsync(r, "release/2.0");
        }

        // Only Maintainers manage the rules.
        await using (var s = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ProtectedTagService>().SaveAsync(r.Id, new ProtectedTagRule { Pattern = "x*" }));
    }

    [Fact]
    public void Protected_tag_rules_combine_strictest_first()
    {
        List<ProtectedTagRule> rules =
        [
            new() { Pattern = "v*", CreateRole = ProjectRole.Developer, BlockDeletion = false, BlockUpdate = false },
            new() { Pattern = "v2.*", CreateRole = ProjectRole.Maintainer, BlockDeletion = true, BlockUpdate = false },
        ];
        Assert.Null(ProtectedTags.Refusal(rules, "v1.0", RefChangeKind.Create, ProjectRole.Developer));
        Assert.Null(ProtectedTags.Refusal(rules, "v1.0", RefChangeKind.Delete, ProjectRole.Developer));
        Assert.NotNull(ProtectedTags.Refusal(rules, "v2.0", RefChangeKind.Create, ProjectRole.Developer));
        Assert.Null(ProtectedTags.Refusal(rules, "v2.0", RefChangeKind.Create, ProjectRole.Maintainer));
        Assert.NotNull(ProtectedTags.Refusal(rules, "v2.0", RefChangeKind.Delete, ProjectRole.Owner));
        Assert.Null(ProtectedTags.Refusal(rules, "v2.0", RefChangeKind.Update, ProjectRole.Maintainer));
        Assert.Null(ProtectedTags.Refusal(rules, "other", RefChangeKind.Delete, ProjectRole.Developer));
        Assert.NotNull(ProtectedTags.Validate("refs/tags/v1"));
        Assert.NotNull(ProtectedTags.Validate("a b"));
    }

    // ================================================================ mirroring

    [Fact]
    public void Mirror_urls_are_checked_against_ssrf()
    {
        var strict = new MirrorUrlGuard(
            new FixedOptions<MirrorOptions>(new MirrorOptions { AllowPrivateNetworks = false }),
            new FixedOptions<WebhookOptions>(new WebhookOptions { AllowPrivateNetworks = true }));
        Assert.Null(strict.Validate("https://github.com/example/project.git"));
        Assert.Null(strict.Validate("ssh://git@github.com/example/project.git"));
        Assert.NotNull(strict.Validate("file:///etc/passwd"));
        Assert.NotNull(strict.Validate("ext::sh -c touch% /tmp/x"));
        Assert.NotNull(strict.Validate("git@github.com:example/project.git"));
        Assert.NotNull(strict.Validate("https://user:secret@github.com/example/project.git"));
        Assert.NotNull(strict.Validate("http://127.0.0.1:8080/x.git"));
        Assert.NotNull(strict.Validate("http://localhost/x.git"));
        Assert.NotNull(strict.Validate("http://169.254.169.254/latest.git"));
        Assert.NotNull(strict.Validate("http://10.1.2.3/x.git"));
        Assert.NotNull(strict.Validate("ssh://-oProxyCommand=x/y.git"));
        Assert.NotNull(strict.Validate("https://github.com/"));
        Assert.NotNull(strict.Validate("https://github.com/a b.git"));

        // Mirroring:AllowPrivateNetworks unset: follows Webhooks:AllowPrivateNetworks.
        var lax = new MirrorUrlGuard(new FixedOptions<MirrorOptions>(new MirrorOptions()), new FixedOptions<WebhookOptions>(new WebhookOptions { AllowPrivateNetworks = true }));
        Assert.Null(lax.Validate("http://127.0.0.1:8080/x.git"));
        Assert.NotNull(lax.Validate("file:///etc/passwd"));
    }

    [Fact]
    public async Task Resolving_a_private_host_is_blocked()
    {
        var strict = new MirrorUrlGuard(new FixedOptions<MirrorOptions>(new MirrorOptions { AllowPrivateNetworks = false }),
            new FixedOptions<WebhookOptions>(new WebhookOptions()));
        // An IPv4-mapped IPv6 loopback literal is still loopback.
        var ex = await Assert.ThrowsAsync<MirrorException>(() => strict.ResolveAsync(new Uri("http://[::ffff:127.0.0.1]/x.git"), CancellationToken.None));
        Assert.Contains("local addresses", ex.Message);
        ex = await Assert.ThrowsAsync<MirrorException>(() => strict.ResolveAsync(new Uri("http://[::ffff:10.0.0.1]/x.git"), CancellationToken.None));
        Assert.Contains("private", ex.Message);
    }

    [Fact]
    public async Task Pull_mirror_fetches_branches_and_tags_and_blocks_pushes()
    {
        // The upstream is another repository on this server, read with bob's credentials.
        var (up, upstream) = await SetupAsync("upstream");
        var (p, mirror) = await SetupAsync("mirror");
        var upDir = await CloneAsync(up, upstream, "bob");
        await CommitAsync(upDir, "a.txt", "upstream");
        await git.OkAsync(upDir, "push", "origin", "main");
        await git.OkAsync(upDir, "push", "origin", "HEAD:refs/heads/feature/x");
        await git.OkAsync(upDir, "tag", "v1");
        await git.OkAsync(upDir, "push", "origin", "v1");

        int mirrorId;
        await using (var s = app.Scope(app.UserId("dan")))
        {
            var created = await s.Get<MirrorService>().SavePullAsync(mirror.Id,
                new MirrorInput { Url = PlainUrl(up, upstream), Username = "bob", Secret = Password, IntervalMinutes = 30 });
            mirrorId = created.Id;
            Assert.NotNull(created.ProtectedSecret);
            Assert.DoesNotContain(Password, created.ProtectedSecret);
        }
        var result = await app.Services.GetRequiredService<MirrorSyncRunner>().SyncAsync(mirrorId, wait: true);
        Assert.True(result!.Success, result.Message);

        var upRefs = await RefsAsync(RepoPath(up, upstream));
        var mirRefs = await RefsAsync(RepoPath(p, mirror));
        Assert.Equal(upRefs["refs/heads/main"], mirRefs["refs/heads/main"]);
        Assert.Equal(upRefs["refs/heads/feature/x"], mirRefs["refs/heads/feature/x"]);
        Assert.Equal(upRefs["refs/tags/v1"], mirRefs["refs/tags/v1"]);

        await using (var db = app.Db())
        {
            var row = await db.RepositoryMirrors.SingleAsync(m => m.Id == mirrorId);
            Assert.Equal(MirrorSyncStatus.Succeeded, row.LastStatus);
            Assert.NotNull(row.LastSuccessAt);
            Assert.True(row.NextSyncAt > DateTime.UtcNow.AddMinutes(25));
        }

        // Pushes to the mirror are refused, branch and tag alike, for everyone.
        var mirDir = await CloneAsync(p, mirror, "carol");
        await CommitAsync(mirDir, "b.txt", "local");
        var push = await git.RunAsync(mirDir, "push", "origin", "main");
        Assert.False(push.Ok);
        Assert.Contains("pull mirror", push.Output);
        await using (var s = app.Scope(app.UserId("carol")))
            await Assert.ThrowsAsync<BranchProtectedException>(() => s.Get<ReleaseService>().CreateTagAsync(mirror, "local-tag", "main", null));

        // Upstream deletes a branch and moves main: the next sync follows (--prune).
        await git.OkAsync(upDir, "push", "origin", "--delete", "feature/x");
        await CommitAsync(upDir, "a.txt", "upstream 2");
        await git.OkAsync(upDir, "push", "origin", "main");
        await using (var s = app.Scope(app.UserId("dan")))
        {
            var again = await SyncNowWithRetryAsync(s.Get<MirrorService>(), mirror.Id, mirrorId);
            Assert.True(again.Success, again.Message);
            Assert.Contains("updated", again.Message);
        }
        mirRefs = await RefsAsync(RepoPath(p, mirror));
        Assert.False(mirRefs.ContainsKey("refs/heads/feature/x"));
        Assert.Equal((await RefsAsync(RepoPath(up, upstream)))["refs/heads/main"], mirRefs["refs/heads/main"]);

        // Wrong credentials: the sync fails with git's message, recorded on the mirror; the token never shows up.
        await using (var s = app.Scope(app.UserId("dan")))
            await s.Get<MirrorService>().SavePullAsync(mirror.Id, new MirrorInput { Url = PlainUrl(up, upstream), Username = "bob", Secret = "wrong-token", IntervalMinutes = 30 });
        var failed = await app.Services.GetRequiredService<MirrorSyncRunner>().SyncAsync(mirrorId, wait: true);
        Assert.False(failed!.Success);
        Assert.DoesNotContain("wrong-token", failed.Message);
        await using (var db = app.Db())
        {
            var row = await db.RepositoryMirrors.SingleAsync(m => m.Id == mirrorId);
            Assert.Equal(MirrorSyncStatus.Failed, row.LastStatus);
            Assert.NotNull(row.LastMessage);
            var actions = await db.AuditEvents.Where(e => e.ProjectId == p.Id).Select(e => e.Action).ToListAsync();
            Assert.Contains("mirror.created", actions);
            Assert.Contains("mirror.updated", actions);
            Assert.Contains("mirror.sync_requested", actions);
        }

        // Removing the mirror makes the repository writable again.
        await using (var s = app.Scope(app.UserId("dan")))
            await s.Get<MirrorService>().DeleteAsync(mirror.Id, mirrorId);
        await git.OkAsync(mirDir, "pull", "--rebase", "origin", "main");
        await git.OkAsync(mirDir, "push", "origin", "main");
    }

    [Fact]
    public async Task Pull_mirror_can_follow_only_protected_branches()
    {
        var (up, upstream) = await SetupAsync("upstream");
        var (p, mirror) = await SetupAsync("mirror");
        var upDir = await CloneAsync(up, upstream, "bob");
        await CommitAsync(upDir, "a.txt", "upstream");
        await git.OkAsync(upDir, "push", "origin", "main", "HEAD:refs/heads/release/1", "HEAD:refs/heads/topic");

        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<BranchProtectionService>().SaveAsync(mirror.Id, new BranchProtectionRule { Pattern = "release/*" });
        int id;
        await using (var s = app.Scope(app.UserId("carol")))
            id = (await s.Get<MirrorService>().SavePullAsync(mirror.Id,
                new MirrorInput { Url = PlainUrl(up, upstream), Username = "bob", Secret = Password, OnlyProtectedBranches = true })).Id;
        var result = await app.Services.GetRequiredService<MirrorSyncRunner>().SyncAsync(id, wait: true);
        Assert.True(result!.Success, result.Message);

        var refs = await RefsAsync(RepoPath(p, mirror));
        Assert.True(refs.ContainsKey("refs/heads/release/1"));
        Assert.False(refs.ContainsKey("refs/heads/topic"));
        Assert.NotEqual((await RefsAsync(RepoPath(up, upstream)))["refs/heads/main"], refs["refs/heads/main"]); // main isn't protected here
    }

    [Fact]
    public async Task Push_mirror_runs_after_a_push_and_on_demand()
    {
        var (p, source) = await SetupAsync("source");
        var (tp, target) = await SetupAsync("target");
        int id;
        await using (var s = app.Scope(app.UserId("dan")))
            id = (await s.Get<MirrorService>().SavePushAsync(source.Id, null,
                new MirrorInput { Url = PlainUrl(tp, target), Username = "dan", Secret = Password })).Id;

        // On demand.
        await using (var s = app.Scope(app.UserId("dan")))
        {
            var r = await SyncNowWithRetryAsync(s.Get<MirrorService>(), source.Id, id);
            Assert.True(r.Success, r.Message);
        }
        Assert.Equal((await RefsAsync(RepoPath(p, source)))["refs/heads/main"], (await RefsAsync(RepoPath(tp, target)))["refs/heads/main"]);

        // After a push (the push listener queues it for the background service).
        var dir = await CloneAsync(p, source, "bob");
        await CommitAsync(dir, "c.txt", "pushed");
        await git.OkAsync(dir, "push", "origin", "main", "HEAD:refs/heads/feature/y");
        await git.OkAsync(dir, "tag", "v9");
        await git.OkAsync(dir, "push", "origin", "v9");
        var expected = (await RefsAsync(RepoPath(p, source)))["refs/heads/main"];
        var sw = Stopwatch.StartNew();
        Dictionary<string, string> targetRefs;
        do
        {
            await Task.Delay(250);
            targetRefs = await RefsAsync(RepoPath(tp, target));
        } while ((!targetRefs.TryGetValue("refs/tags/v9", out _) || targetRefs["refs/heads/main"] != expected) && sw.Elapsed < TimeSpan.FromSeconds(30));
        Assert.Equal(expected, targetRefs["refs/heads/main"]);
        Assert.True(targetRefs.ContainsKey("refs/heads/feature/y"));
        Assert.True(targetRefs.ContainsKey("refs/tags/v9"));

        // Deleting a branch here deletes it there (--prune).
        await git.OkAsync(dir, "push", "origin", "--delete", "feature/y");
        await using (var s = app.Scope(app.UserId("dan")))
            Assert.True((await SyncNowWithRetryAsync(s.Get<MirrorService>(), source.Id, id)).Success);
        Assert.False((await RefsAsync(RepoPath(tp, target))).ContainsKey("refs/heads/feature/y"));

        // Developers can't manage mirrors.
        await using (var s = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<MirrorService>().ListAsync(source.Id));
    }

    private static async Task<MirrorSyncResult> SyncNowWithRetryAsync(MirrorService mirrors, int repoId, int mirrorId)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await mirrors.SyncNowAsync(repoId, mirrorId) is { } r) return r;
            await Task.Delay(200); // the background service is running it right now
        }
        throw new TimeoutException("The mirror stayed busy.");
    }

    // ================================================================ Git LFS

    private HttpClient LfsClient(string? user = null, string? secret = null)
    {
        var client = new HttpClient { BaseAddress = app.ServerAddress };
        if (user != null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{secret ?? Password}")));
        return client;
    }

    private static HttpContent Json(object body)
    {
        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue(LfsJson);
        return content;
    }

    private static async Task<JsonElement> BatchAsync(HttpClient client, string url, string operation, string oid, long size, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await client.PostAsync(url + "/info/lfs/objects/batch",
            Json(new { operation, transfers = new[] { "basic" }, objects = new[] { new { oid, size } }, hash_algo = "sha256" }));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(expected == response.StatusCode, $"{(int)response.StatusCode}: {text}");
        Assert.Equal(LfsJson, response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(text).RootElement;
    }

    private static (string Oid, byte[] Bytes) Blob(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return (Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes);
    }

    [Fact]
    public async Task Lfs_batch_upload_verify_and_download()
    {
        var (p, r) = await SetupAsync();
        var url = $"/git/{p.Key}/{r.Slug}.git";
        var (oid, bytes) = Blob("large binary " + Guid.NewGuid());
        var bob = LfsClient("bob");

        // Upload: the batch hands out an upload and a verify action with a short-lived token.
        var batch = await BatchAsync(bob, url, "upload", oid, bytes.Length);
        Assert.Equal("basic", batch.GetProperty("transfer").GetString());
        var obj = batch.GetProperty("objects")[0];
        var upload = obj.GetProperty("actions").GetProperty("upload");
        var href = upload.GetProperty("href").GetString()!;
        var auth = upload.GetProperty("header").GetProperty("Authorization").GetString()!;
        Assert.StartsWith("Bearer gblfs.", auth);
        Assert.DoesNotContain(Password, batch.GetRawText());

        var plain = LfsClient();
        // A wrong hash is refused and nothing is stored.
        var bad = new HttpRequestMessage(HttpMethod.Put, href) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("something else")) };
        bad.Headers.TryAddWithoutValidation("Authorization", auth);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await plain.SendAsync(bad)).StatusCode);

        var put = new HttpRequestMessage(HttpMethod.Put, href) { Content = new ByteArrayContent(bytes) };
        put.Headers.TryAddWithoutValidation("Authorization", auth);
        Assert.Equal(HttpStatusCode.OK, (await plain.SendAsync(put)).StatusCode);

        var verify = new HttpRequestMessage(HttpMethod.Post, obj.GetProperty("actions").GetProperty("verify").GetProperty("href").GetString()!)
            { Content = Json(new { oid, size = bytes.Length }) };
        verify.Headers.TryAddWithoutValidation("Authorization", auth);
        Assert.Equal(HttpStatusCode.OK, (await plain.SendAsync(verify)).StatusCode);
        var wrongSize = new HttpRequestMessage(HttpMethod.Post, verify.RequestUri) { Content = Json(new { oid, size = bytes.Length + 1 }) };
        wrongSize.Headers.TryAddWithoutValidation("Authorization", auth);
        Assert.Equal(HttpStatusCode.NotFound, (await plain.SendAsync(wrongSize)).StatusCode);

        // Uploading again: nothing to do.
        var again = await BatchAsync(bob, url, "upload", oid, bytes.Length);
        Assert.False(again.GetProperty("objects")[0].TryGetProperty("actions", out _));

        // Download as a Developer with a read token (Bearer), then fetch the bytes with the action's token.
        var readToken = await app.CreateTokenAsync("dan", TokenScopes.ReadRepository);
        var dan = LfsClient();
        dan.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", readToken);
        var down = (await BatchAsync(dan, url, "download", oid, bytes.Length)).GetProperty("objects")[0].GetProperty("actions").GetProperty("download");
        var get = new HttpRequestMessage(HttpMethod.Get, down.GetProperty("href").GetString());
        get.Headers.TryAddWithoutValidation("Authorization", down.GetProperty("header").GetProperty("Authorization").GetString());
        var got = await plain.SendAsync(get);
        Assert.Equal(HttpStatusCode.OK, got.StatusCode);
        Assert.Equal(bytes, await got.Content.ReadAsByteArrayAsync());

        // A missing object answers 404 inside the batch.
        var (missing, _) = Blob("never uploaded " + Guid.NewGuid());
        var none = await BatchAsync(dan, url, "download", missing, 5);
        Assert.Equal(404, none.GetProperty("objects")[0].GetProperty("error").GetProperty("code").GetInt32());

        // The file is content-addressed under Lfs:Root, and the repository knows it has it.
        var storage = app.Services.GetRequiredService<LfsStorage>();
        Assert.True(File.Exists(storage.PathFor(oid)));
        Assert.True(await storage.RepositoryHasAsync(r.Id, oid));
    }

    [Fact]
    public async Task Lfs_requires_auth_and_permissions()
    {
        var (p, r) = await SetupAsync();
        var url = $"/git/{p.Key}/{r.Slug}.git";
        var (oid, bytes) = Blob("perm test " + Guid.NewGuid());

        // No credentials: 401 with LFS-Authenticate, so git-lfs asks for credentials.
        var anonymous = await LfsClient().PostAsync(url + "/info/lfs/objects/batch", Json(new { operation = "download", objects = new[] { new { oid, size = 1 } } }));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.True(anonymous.Headers.Contains("LFS-Authenticate"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await LfsClient("bob", "wrong-password").GetAsync(url + "/info/lfs/objects/" + oid)).StatusCode);

        // Not a member: the repository doesn't exist.
        await app.CreateUserAsync("lfsoutsider");
        await BatchAsync(LfsClient("lfsoutsider"), url, "download", oid, bytes.Length, HttpStatusCode.NotFound);

        // A Viewer can download but not upload.
        await app.CreateUserAsync("lfsviewer");
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<MemberService>().AddMemberAsync(p.Id, "lfsviewer", ProjectRole.Viewer);
        await BatchAsync(LfsClient("lfsviewer"), url, "download", oid, bytes.Length);
        await BatchAsync(LfsClient("lfsviewer"), url, "upload", oid, bytes.Length, HttpStatusCode.Forbidden);
        var direct = await LfsClient("lfsviewer").PutAsync(url + "/info/lfs/objects/" + oid, new ByteArrayContent(bytes));
        Assert.Equal(HttpStatusCode.Forbidden, direct.StatusCode);

        // A read-only token can't upload; a write token can (directly, with Basic and the token as the password).
        var readToken = await app.CreateTokenAsync("bob", TokenScopes.ReadRepository);
        await BatchAsync(LfsClient("bob", readToken), url, "upload", oid, bytes.Length, HttpStatusCode.Forbidden);
        var writeToken = await app.CreateTokenAsync("bob", TokenScopes.WriteRepository);
        Assert.Equal(HttpStatusCode.OK, (await LfsClient("bob", writeToken).PutAsync(url + "/info/lfs/objects/" + oid, new ByteArrayContent(bytes))).StatusCode);

        // An action token only works for its own repository.
        var (p2, r2) = await SetupAsync("other");
        var auth = (await BatchAsync(LfsClient("bob"), url, "download", oid, bytes.Length))
            .GetProperty("objects")[0].GetProperty("actions").GetProperty("download").GetProperty("header").GetProperty("Authorization").GetString();
        var cross = new HttpRequestMessage(HttpMethod.Get, $"/git/{p2.Key}/{r2.Slug}.git/info/lfs/objects/{oid}");
        cross.Headers.TryAddWithoutValidation("Authorization", auth);
        Assert.Equal(HttpStatusCode.Forbidden, (await LfsClient().SendAsync(cross)).StatusCode);

        // Another repository can't download the object just because the content exists on the server.
        await BatchAsync(LfsClient("bob"), $"/git/{p2.Key}/{r2.Slug}.git", "download", oid, bytes.Length)
            .ContinueWith(t => Assert.Equal(404, t.Result.GetProperty("objects")[0].GetProperty("error").GetProperty("code").GetInt32()));
    }

    [Fact]
    public async Task Deleting_a_repository_removes_lfs_objects_no_other_repository_has()
    {
        var (p, r1) = await SetupAsync("one");
        var r2 = await app.CreateRepoAsync(p.Id, "carol", "two");
        var (shared, sharedBytes) = Blob("shared " + Guid.NewGuid());
        var (own, ownBytes) = Blob("own " + Guid.NewGuid());
        var bob = LfsClient("bob");
        Assert.Equal(HttpStatusCode.OK, (await bob.PutAsync($"/git/{p.Key}/{r1.Slug}.git/info/lfs/objects/{shared}", new ByteArrayContent(sharedBytes))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.PutAsync($"/git/{p.Key}/{r1.Slug}.git/info/lfs/objects/{own}", new ByteArrayContent(ownBytes))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.PutAsync($"/git/{p.Key}/{r2.Slug}.git/info/lfs/objects/{shared}", new ByteArrayContent(sharedBytes))).StatusCode);

        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<RepositorySettingsService>().DeleteAsync(r1.Id, r1.Name);
        var storage = app.Services.GetRequiredService<LfsStorage>();
        Assert.False(File.Exists(storage.PathFor(own)));
        Assert.True(File.Exists(storage.PathFor(shared)));
        Assert.True(await storage.RepositoryHasAsync(r2.Id, shared));
    }

    [Fact]
    public void Lfs_pointer_files_are_recognized()
    {
        var oid = new string('a', 64);
        var pointer = LfsPointer.TryParse($"version https://git-lfs.github.com/spec/v1\noid sha256:{oid}\nsize 12345\n");
        Assert.Equal(new LfsPointer(oid, 12345), pointer);
        Assert.Null(LfsPointer.TryParse("hello world\n"));
        Assert.Null(LfsPointer.TryParse($"version https://git-lfs.github.com/spec/v1\noid sha256:{oid[..10]}\nsize 1\n"));
        Assert.Null(LfsPointer.TryParse(null));
    }

    [LfsFact]
    public async Task Git_lfs_push_and_clone_end_to_end()
    {
        var (p, r) = await SetupAsync();
        var dir = await CloneAsync(p, r, "bob");
        await git.OkAsync(dir, "lfs", "install", "--local");
        await git.OkAsync(dir, "lfs", "track", "*.bin");
        var data = RandomNumberGenerator.GetBytes(200_000);
        await File.WriteAllBytesAsync(Path.Combine(dir, "big.bin"), data);
        await git.OkAsync(dir, "add", ".gitattributes", "big.bin");
        await git.OkAsync(dir, "commit", "-m", "lfs");
        await git.OkAsync(dir, "push", "origin", "main");

        var oid = Convert.ToHexStringLower(SHA256.HashData(data));
        Assert.True(await app.Services.GetRequiredService<LfsStorage>().RepositoryHasAsync(r.Id, oid));

        var clone = await CloneAsync(p, r, "dan");
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(clone, "big.bin")));
    }
}

/// <summary>A test that needs the git-lfs extension; skipped when it isn't installed.</summary>
public sealed class LfsFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("lfs");
            psi.ArgumentList.Add("version");
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            return p.WaitForExit(10_000) && p.ExitCode == 0;
        }
        catch (Exception) { return false; }
    });

    public LfsFactAttribute()
    {
        if (!Available.Value) Skip = "git-lfs is not installed.";
    }
}

/// <summary>A fixed <see cref="IOptionsMonitor{T}"/> for unit tests.</summary>
internal sealed class FixedOptions<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
