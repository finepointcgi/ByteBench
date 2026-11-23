using System.Reflection;
using System.Text;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.DependencyBot;
using ByteBench.Services.Git;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

/// <summary>Private registries of the dependency bot against fake registries on 127.0.0.1 (private networks allowed; the scheduled worker is off).</summary>
public class DependencyBotPrivateRegistryTests(DepBotApp app) : IClassFixture<DepBotApp>
{
    private const string Secret = "s3cr3t-tok-7f3a9c1d";
    private const string Password = "p4ssw0rd-b1e2d3c4";
    private static int counter;

    private sealed record Repo(Project Project, Repository Repository, string Path, string Sfx);

    private static readonly SemaphoreSlim UserGate = new(1, 1);

    private async Task EnsureMaintainerAsync()
    {
        await UserGate.WaitAsync();
        try { await using var db = app.Db(); if (!await db.Users.AnyAsync(u => u.UserName == "erin")) await app.CreateUserAsync("erin"); }
        finally { UserGate.Release(); }
    }

    private async Task<Repo> NewAsync(Dictionary<string, string?> files)
    {
        await EnsureMaintainerAsync();
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("dan", ProjectRole.Viewer), ("erin", ProjectRole.Maintainer));
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        var path = app.Services.GetRequiredService<RepoStorage>().PathFor(project.Key, repo.Slug);
        var initial = files.Where(f => f.Value != null).ToDictionary(f => f.Key, f => f.Value);
        initial["README.md"] = "# demo\n";
        GitOps.CommitFiles(path, "main", initial, "Add files", new GitIdentity("Test", "test@example.com"), DateTimeOffset.UtcNow);
        return new Repo(project, repo, path, Interlocked.Increment(ref counter).ToString());
    }

    /// <summary>Commits one more file on top of main (the names the test needs carry the repository's suffix, known only after creating it).</summary>
    private static Task WriteFileAsync(Repo r, string file, string content)
    {
        GitOps.CommitFiles(r.Path, "main", new Dictionary<string, string?> { [file] = content }, "Update " + file, new GitIdentity("Test", "test@example.com"), DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    private static Task WritePackageJsonAsync(Repo r, string json) => WriteFileAsync(r, "package.json", json);

    private static string PackageJson(params string[] names) =>
        "{\n  \"name\": \"demo\",\n  \"dependencies\": {\n" + string.Join(",\n", names.Select(n => $"    \"{n}\": \"^1.0.0\"")) + "\n  }\n}\n";

    private async Task<Repository> RepoAsync(Repo r, string user)
    {
        await using var scope = app.Scope(app.UserId(user));
        return (await scope.Get<RepositoryService>().GetRepositoryAsync(r.Project.Key, r.Repository.Slug))!;
    }

    private async Task<RegistryView> SaveRegistryAsync(Repo r, RegistryInput input, string user = "carol")
    {
        var repo = await RepoAsync(r, user);
        await using var scope = app.Scope(app.UserId(user));
        return await scope.Get<DependencyRegistryService>().SaveAsync(repo, input, app.UserId(user));
    }

    private async Task<DependencyReport> CheckAsync(Repo r, string user = "carol")
    {
        var repo = await RepoAsync(r, user);
        await using var scope = app.Scope(app.UserId(user));
        return await scope.Get<DependencyBotService>().CheckNowAsync(repo);
    }

    private static RegistryInput Bearer(string eco, string url, string token = Secret, string? scope = null) =>
        new() { Ecosystem = eco, Url = url, AuthKind = DependencyRegistryAuth.Bearer, Secret = token, Scope = scope };

    private static RegistryInput Basic(string eco, string url, string user = "ci-user", string password = Password, string? scope = null) =>
        new() { Ecosystem = eco, Url = url, AuthKind = DependencyRegistryAuth.Basic, Username = user, Secret = password, Scope = scope };

    private static string BasicHeader(string user, string password) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));

    private static OutdatedItem Item(DependencyReport r, string name) => r.Items.Single(i => i.Name == name);

    // ------------------------------------------------------------------ npm

    [Fact]
    public async Task A_private_npm_registry_with_a_bearer_token_is_asked_with_the_token_and_the_public_one_is_not()
    {
        using var priv = new FakeRegistry();
        var r = await NewAsync(new() { ["package.json"] = null });
        var name = "left-pad-pr" + r.Sfx;
        await WritePackageJsonAsync(r, PackageJson(name));
        priv.NpmPackage(name, ["1.0.0", "1.2.0"]).RequireBearer("/npm/", Secret);
        await SaveRegistryAsync(r, Bearer(Ecosystems.Npm, priv.Npm));

        var report = await CheckAsync(r);

        Assert.Empty(report.Warnings);
        Assert.Equal("1.2.0", Item(report, name).Proposed);
        Assert.NotEmpty(priv.Auths);
        Assert.All(priv.Auths, a => Assert.Equal("Bearer " + Secret, a.Authorization));
        Assert.DoesNotContain(app.Registry.Requests, p => p.Contains(name)); // the public registry never heard of the private package
    }

    [Fact]
    public async Task A_private_npm_registry_with_basic_authentication_works_and_other_ecosystems_stay_public()
    {
        using var priv = new FakeRegistry();
        var r = await NewAsync(new() { ["package.json"] = null });
        var name = "basic-pkg-" + r.Sfx;
        await WritePackageJsonAsync(r, PackageJson(name));
        priv.NpmPackage(name, ["1.0.0", "1.1.0"]).RequireBasic("/npm/", "ci-user", Password);
        await SaveRegistryAsync(r, Basic(Ecosystems.Npm, priv.Npm));

        var report = await CheckAsync(r);

        Assert.Empty(report.Warnings);
        Assert.Equal("1.1.0", Item(report, name).Proposed);
        Assert.All(priv.Auths, a => Assert.Equal(BasicHeader("ci-user", Password), a.Authorization));
    }

    [Fact]
    public async Task Scoped_npm_registries_take_their_scope_and_everything_else_stays_on_the_public_registry_without_credentials()
    {
        using var acme = new FakeRegistry();
        using var other = new FakeRegistry();
        var r = await NewAsync(new() { ["package.json"] = null });
        var scoped = "@acme/lib-" + r.Sfx;
        var scoped2 = "@other/lib-" + r.Sfx;
        var plain = "plain-pkg-" + r.Sfx;
        await WritePackageJsonAsync(r, PackageJson(scoped, scoped2, plain));
        acme.NpmPackage(scoped, ["1.0.0", "1.3.0"]).RequireBearer("/npm/", Secret);
        other.NpmPackage(scoped2, ["1.0.0", "1.4.0"]).RequireBasic("/npm/", "u", Password);
        app.Registry.NpmPackage(plain, ["1.0.0", "1.5.0"]);
        await SaveRegistryAsync(r, Bearer(Ecosystems.Npm, acme.Npm, scope: "@acme"));
        await SaveRegistryAsync(r, Basic(Ecosystems.Npm, other.Npm, "u", scope: "@other"));

        var report = await CheckAsync(r);

        Assert.Empty(report.Warnings);
        Assert.Equal("1.3.0", Item(report, scoped).Proposed);
        Assert.Equal("1.4.0", Item(report, scoped2).Proposed);
        Assert.Equal("1.5.0", Item(report, plain).Proposed);
        Assert.All(acme.Auths, a => Assert.Equal("Bearer " + Secret, a.Authorization));
        Assert.DoesNotContain(acme.Requests, p => p.Contains(plain) || p.Contains("other"));
        Assert.DoesNotContain(app.Registry.Auths.Where(a => a.Path.Contains(r.Sfx)), a => a.Authorization != null);
    }

    [Fact]
    public async Task A_redirect_to_another_host_is_refused_and_that_host_never_sees_the_credential()
    {
        using var priv = new FakeRegistry();
        using var evil = new FakeRegistry();
        var r = await NewAsync(new() { ["package.json"] = null });
        var name = "redir-" + r.Sfx;
        await WritePackageJsonAsync(r, PackageJson(name));
        evil.NpmPackage(name, ["1.0.0", "9.9.9"]);
        priv.Redirect("/npm/" + name, evil.Npm + name);
        await SaveRegistryAsync(r, Bearer(Ecosystems.Npm, priv.Npm));

        var report = await CheckAsync(r);

        Assert.Contains(report.Warnings, w => w.Contains(name) && w.Contains("redirect"));
        Assert.Empty(report.Items);
        Assert.Equal(0, evil.Count);
        Assert.DoesNotContain(report.Warnings, w => w.Contains(Secret));
    }

    [Fact]
    public async Task A_rejected_credential_is_a_clear_warning_and_ends_the_lookups_for_that_ecosystem_only()
    {
        using var priv = new FakeRegistry();
        var r = await NewAsync(new() { ["package.json"] = null, ["go.mod"] = null });
        var names = new[] { "rej-a-" + r.Sfx, "rej-b-" + r.Sfx, "rej-c-" + r.Sfx };
        var mod = "github.com/acme/gomod" + r.Sfx;
        await WritePackageJsonAsync(r, PackageJson(names));
        await WriteFileAsync(r, "go.mod", $"module example.com/app\n\ngo 1.22\n\nrequire {mod} v1.0.0\n");
        foreach (var n in names) priv.NpmPackage(n, ["1.0.0", "1.1.0"]);
        priv.RequireBearer("/npm/", "the-right-token");
        app.Registry.GoModule(mod, "v1.0.0", "v1.0.1");
        await SaveRegistryAsync(r, Bearer(Ecosystems.Npm, priv.Npm, "the-wrong-token"));

        var report = await CheckAsync(r);

        Assert.Equal(1, priv.Count); // the first answer was a 401: the other packages are not even asked
        Assert.Contains(report.Warnings, w => w.Contains("the registry rejected the credential", StringComparison.OrdinalIgnoreCase) || w.Contains("rejected the credential"));
        Assert.Contains(report.Warnings, w => w.Contains("remaining packages were skipped"));
        Assert.Equal("v1.0.1", Item(report, mod).Proposed); // Go is unaffected
        Assert.DoesNotContain(report.Warnings, w => w.Contains("the-wrong-token"));
    }

    // ------------------------------------------------------------------ NuGet

    [Fact]
    public async Task Nuget_sources_are_asked_in_order_and_the_first_one_that_knows_the_package_wins()
    {
        using var a = new FakeRegistry();
        using var b = new FakeRegistry();
        var r = await NewAsync(new() { ["src/App.csproj"] = null });
        var shared = "Contoso.Shared" + r.Sfx;
        var onlyB = "Contoso.OnlyB" + r.Sfx;
        await WriteFileAsync(r, "src/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"" + shared + "\" Version=\"1.0.0\" />\n    <PackageReference Include=\"" + onlyB + "\" Version=\"1.0.0\" />\n  </ItemGroup>\n</Project>\n");
        a.NuGetPackage(shared, "1.0.0", "1.5.0").RequireBearer("/", Secret);
        b.NuGetServiceIndex("/v3/index.json", b.Url + "/nuget/").NuGetPackage(shared, "1.0.0", "2.0.0").NuGetPackage(onlyB, "1.0.0", "1.2.0").RequireBasic("/", "feed", Password);
        // Source A is a flat container address, source B a v3 service index; A is listed first.
        await SaveRegistryAsync(r, Bearer(Ecosystems.NuGet, a.NuGet));
        await SaveRegistryAsync(r, Basic(Ecosystems.NuGet, b.Url + "/v3/index.json", "feed"));

        var report = await CheckAsync(r);

        Assert.Empty(report.Warnings);
        Assert.Equal("1.5.0", Item(report, shared).Latest); // from source A, not B's 2.0.0
        Assert.Equal("1.2.0", Item(report, onlyB).Proposed); // A answered 404, B knew it
        Assert.DoesNotContain(b.Requests, p => p.Contains(shared.ToLowerInvariant()));
        Assert.All(a.Auths, x => Assert.Equal("Bearer " + Secret, x.Authorization));
        Assert.All(b.Auths, x => Assert.Equal(BasicHeader("feed", Password), x.Authorization));
    }

    [Fact]
    public async Task A_service_index_that_names_a_resource_on_another_origin_is_refused_and_that_origin_is_never_contacted()
    {
        using var feed = new FakeRegistry();
        using var foreign = new FakeRegistry();
        var r = await NewAsync(new() { ["src/App.csproj"] = null });
        var id = "Contoso.Foreign" + r.Sfx;
        await WriteFileAsync(r, "src/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"" + id + "\" Version=\"1.0.0\" />\n  </ItemGroup>\n</Project>\n");
        feed.NuGetServiceIndex("/v3/index.json", foreign.Url + "/nuget/").RequireBearer("/", Secret);
        foreign.NuGetPackage(id, "1.0.0", "1.1.0");
        await SaveRegistryAsync(r, Bearer(Ecosystems.NuGet, feed.Url + "/v3/index.json"));

        var report = await CheckAsync(r);

        Assert.Empty(report.Items);
        Assert.Contains(report.Warnings, w => w.Contains(id) && w.Contains("points to another address"));
        Assert.Equal(0, foreign.Count);
        Assert.DoesNotContain(report.Warnings, w => w.Contains(Secret));

        // Listed as an additional origin, the maintainer trusts it with the credential.
        await using (var db = app.Db())
        {
            var row = await db.DependencyBotRegistries.SingleAsync(x => x.RepositoryId == r.Repository.Id);
            await SaveRegistryAsync(r, new RegistryInput { Id = row.Id, Ecosystem = Ecosystems.NuGet, Url = row.Url, AuthKind = DependencyRegistryAuth.Bearer, ExtraOrigins = foreign.Url });
        }
        foreign.RequireBearer("/", Secret);
        var trusted = await CheckAsync(r);
        Assert.Equal("1.1.0", Item(trusted, id).Proposed);
        Assert.All(foreign.Auths, x => Assert.Equal("Bearer " + Secret, x.Authorization));
    }

    // ------------------------------------------------------------------ PyPI and Go

    [Fact]
    public async Task A_private_pip_simple_index_and_a_private_go_proxy_use_basic_authentication()
    {
        using var py = new FakeRegistry();
        using var go = new FakeRegistry();
        var r = await NewAsync(new() { ["requirements.txt"] = null, ["go.mod"] = null });
        var pkg = "privpkg-" + r.Sfx;
        var mod = "git.corp.example/team/mod" + r.Sfx;
        await WriteFileAsync(r, "requirements.txt", pkg + "==1.0.0\n");
        await WriteFileAsync(r, "go.mod", $"module example.com/app\n\ngo 1.22\n\nrequire {mod} v1.0.0\n");
        py.PySimple("/simple", pkg, "1.0.0", "1.4.0").RequireBasic("/simple", "u", Password);
        go.GoModule(mod, "v1.0.0", "v1.0.3").RequireBasic("/go/", "u", Password);
        await SaveRegistryAsync(r, Basic(Ecosystems.Pip, py.Url + "/simple", "u"));
        await SaveRegistryAsync(r, Basic(Ecosystems.Go, go.Go, "u"));

        var report = await CheckAsync(r);

        Assert.Empty(report.Warnings);
        Assert.Equal("1.4.0", Item(report, pkg).Proposed);
        Assert.Equal("v1.0.3", Item(report, mod).Proposed);
        Assert.All(py.Auths.Concat(go.Auths), x => Assert.Equal(BasicHeader("u", Password), x.Authorization));
    }

    // ------------------------------------------------------------------ storage, secrecy

    [Fact]
    public async Task The_credential_is_stored_encrypted_round_trips_and_is_never_returned()
    {
        var r = await NewAsync(new() { ["README.md"] = "x\n" });
        var view = await SaveRegistryAsync(r, Basic(Ecosystems.Npm, "https://npm.corp.example/", "alice"));

        await using var db = app.Db();
        var row = await db.DependencyBotRegistries.SingleAsync(x => x.Id == view.Id);
        Assert.NotNull(row.EncryptedCredential);
        Assert.DoesNotContain(Password, row.EncryptedCredential);
        Assert.DoesNotContain("alice", row.EncryptedUsername!);
        var protector = app.Services.GetRequiredService<RegistryCredentialProtector>();
        Assert.Equal(Password, protector.UnprotectCredential(row.EncryptedCredential!));
        Assert.Equal("alice", protector.UnprotectUsername(row.EncryptedUsername!));

        // Neither the view nor anything the page can read carries the credential.
        Assert.True(view.HasCredential);
        foreach (var p in typeof(RegistryView).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Assert.DoesNotContain(Password, p.GetValue(view)?.ToString() ?? "");
        var repo = await RepoAsync(r, "carol");
        await using var scope = app.Scope(app.UserId("carol"));
        var listed = await scope.Get<DependencyRegistryService>().ListAsync(repo);
        Assert.DoesNotContain(Password, string.Join("|", listed.Select(l => l.ToString())));

        // Replacing keeps one row; clearing is explicit; an empty secret on save keeps the stored credential.
        var kept = await SaveRegistryAsync(r, new RegistryInput { Id = view.Id, Ecosystem = Ecosystems.Npm, Url = "https://npm.corp.example/", AuthKind = DependencyRegistryAuth.Basic });
        Assert.True(kept.HasCredential);
        await using (var db2 = app.Db()) Assert.Equal(row.EncryptedCredential, (await db2.DependencyBotRegistries.SingleAsync(x => x.Id == view.Id)).EncryptedCredential);
        var replacement = Basic(Ecosystems.Npm, "https://npm.corp.example/", "alice", "second-password-99");
        replacement.Id = view.Id;
        await SaveRegistryAsync(r, replacement);
        await using (var db3 = app.Db())
            Assert.Equal("second-password-99", protector.UnprotectCredential((await db3.DependencyBotRegistries.SingleAsync(x => x.Id == view.Id)).EncryptedCredential!));
        await scope.Get<DependencyRegistryService>().ClearCredentialAsync(repo, view.Id, app.UserId("carol"));
        await using var db4 = app.Db();
        var cleared = await db4.DependencyBotRegistries.SingleAsync(x => x.Id == view.Id);
        Assert.Equal((DependencyRegistryAuth.None, null, null), (cleared.AuthKind, cleared.EncryptedCredential, cleared.EncryptedUsername));
    }

    [Fact]
    public async Task The_credential_never_appears_in_warnings_audit_pull_requests_settings_or_exception_messages()
    {
        using var priv = new FakeRegistry();
        var r = await NewAsync(new() { ["package.json"] = null });
        var name = "leak-" + r.Sfx;
        var broken = "leak-b-" + r.Sfx;
        await WritePackageJsonAsync(r, PackageJson(name));
        priv.NpmPackage(name, ["1.0.0", "1.1.0"]).RequireBearer("/npm/", Secret);
        var texts = new List<string>();

        // Exceptions from refused saves (the secret was typed into the form each time).
        foreach (var bad in new[] { "http://registry.corp.example/", "https://user:pw@registry.corp.example/", "ftp://x/", "not a url" })
        {
            var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => SaveRegistryAsync(r, Bearer(Ecosystems.Npm, bad)));
            texts.Add(ex.Message);
        }
        texts.Add((await Assert.ThrowsAsync<InvalidOperationException>(() => SaveRegistryAsync(r, Basic(Ecosystems.Npm, priv.Npm, "bad:user")))).Message);
        var view = await SaveRegistryAsync(r, Bearer(Ecosystems.Npm, priv.Npm));
        texts.Add((await Assert.ThrowsAsync<InvalidOperationException>(() => SaveRegistryAsync(r, Bearer(Ecosystems.Npm, priv.Npm)))).Message); // duplicate default
        texts.Add((await Assert.ThrowsAsync<InvalidOperationException>(() => SaveRegistryAsync(r, new RegistryInput { Id = view.Id, Ecosystem = Ecosystems.Npm, Url = "https://elsewhere.corp.example/", AuthKind = DependencyRegistryAuth.Bearer }))).Message);

        var report = await CheckAsync(r);
        Assert.Equal("1.1.0", Item(report, name).Proposed);
        var apply = await CreateAsync(r);
        Assert.Single(apply.PullRequests);
        texts.AddRange(apply.Warnings);

        // A rejected credential and an unreadable one produce warnings too.
        priv.RequireBearer("/npm/", "other-token");
        texts.AddRange((await CheckAsync(r)).Warnings);
        await using (var db = app.Db())
        {
            var row = await db.DependencyBotRegistries.SingleAsync(x => x.Id == view.Id);
            row.EncryptedCredential = "not-a-valid-protected-payload";
            await db.SaveChangesAsync();
        }
        var unreadable = await CheckAsync(r);
        texts.AddRange(unreadable.Warnings);
        Assert.Contains(unreadable.Warnings, w => w.Contains("can't be read"));
        Assert.Equal(0, app.Registry.Requests.Count(p => p.Contains(broken))); // never a fall back to the public registry

        await using var check = app.Db();
        texts.AddRange(await check.AuditEvents.Where(e => e.ProjectId == r.Project.Id).Select(e => (e.Details ?? "") + e.Action).ToListAsync());
        texts.AddRange(await check.PullRequests.Where(p => p.RepositoryId == r.Repository.Id).Select(p => p.Title + "\n" + p.Description).ToListAsync());
        var settings = await check.DependencyBotSettings.SingleAsync(s => s.RepositoryId == r.Repository.Id);
        texts.Add(settings.LastMessage + settings.LastReportJson + settings.LastStatus);
        Assert.Contains(await check.AuditEvents.Where(e => e.ProjectId == r.Project.Id).Select(e => e.Action).ToListAsync(), a => a == "repository.dependency_registry_saved");

        var all = string.Join("\n", texts);
        foreach (var secret in new[] { Secret, "other-token", "not-a-valid-protected-payload" }) Assert.DoesNotContain(secret, all);
    }

    private async Task<ApplyResult> CreateAsync(Repo r)
    {
        var repo = await RepoAsync(r, "carol");
        await using var scope = app.Scope(app.UserId("carol"));
        return await scope.Get<DependencyBotService>().CreatePullRequestAsync(repo, null, app.UserId("carol"));
    }

    // ------------------------------------------------------------------ address rules

    [Fact]
    public async Task Address_rules_https_only_scope_and_credential_changes_are_validated()
    {
        var r = await NewAsync(new() { ["README.md"] = "x\n" });
        var http = await Assert.ThrowsAsync<InvalidOperationException>(() => SaveRegistryAsync(r, Bearer(Ecosystems.Npm, "http://registry.corp.example/")));
        Assert.Contains("https", http.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SaveRegistryAsync(r, Bearer(Ecosystems.Npm, "https://npm.corp.example/", scope: "acme"))); // not @scope
        await Assert.ThrowsAsync<InvalidOperationException>(() => SaveRegistryAsync(r, new RegistryInput { Ecosystem = Ecosystems.Npm, Url = "https://npm.corp.example/", AuthKind = DependencyRegistryAuth.Bearer })); // token missing
        await Assert.ThrowsAsync<InvalidOperationException>(() => SaveRegistryAsync(r, new RegistryInput { Ecosystem = Ecosystems.Npm, Url = "https://npm.corp.example/", AuthKind = DependencyRegistryAuth.Basic, Secret = "x" })); // user missing
        await Assert.ThrowsAsync<InvalidOperationException>(() => SaveRegistryAsync(r, new RegistryInput { Ecosystem = "maven", Url = "https://m.corp.example/" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SaveRegistryAsync(r, Bearer(Ecosystems.Npm, "https://npm.corp.example/", "line\nbreak")));

        // https on a loopback address is fine here (private networks allowed); a scoped and a default registry can coexist.
        var scoped = await SaveRegistryAsync(r, Bearer(Ecosystems.Npm, "https://npm.corp.example/", scope: "@Acme"));
        Assert.Equal("@acme", scoped.Scope);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SaveRegistryAsync(r, Bearer(Ecosystems.Npm, "https://other.corp.example/", scope: "@acme")));
        var plain = await SaveRegistryAsync(r, new RegistryInput { Ecosystem = Ecosystems.Npm, Url = "https://npm2.corp.example/" });
        Assert.False(plain.HasCredential);

        // A stored credential is never moved to another origin without entering it again.
        var moved = await Assert.ThrowsAsync<InvalidOperationException>(() => SaveRegistryAsync(r, new RegistryInput { Id = scoped.Id, Ecosystem = Ecosystems.Npm, Url = "https://evil.example/", AuthKind = DependencyRegistryAuth.Bearer }));
        Assert.Contains("enter the credential again", moved.Message);
        var samePath = await SaveRegistryAsync(r, new RegistryInput { Id = scoped.Id, Ecosystem = Ecosystems.Npm, Url = "https://npm.corp.example/other/path/", AuthKind = DependencyRegistryAuth.Bearer });
        Assert.True(samePath.HasCredential);

        // Registries of one repository can't be edited through another repository's page.
        var r2 = await NewAsync(new() { ["README.md"] = "x\n" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => SaveRegistryAsync(r2, new RegistryInput { Id = scoped.Id, Ecosystem = Ecosystems.Npm, Url = "https://npm.corp.example/", AuthKind = DependencyRegistryAuth.None }));
    }

    [Fact]
    public async Task The_http_client_refuses_to_send_a_credential_to_another_origin_or_over_plain_http()
    {
        using var one = new FakeRegistry();
        using var two = new FakeRegistry();
        one.Serve("/x", "{}");
        two.Serve("/x", "{}");
        var client = app.Services.GetRequiredService<RegistryHttp>();
        var auth = RegistryAuth.Bearer(RegistryOrigin.Of(one.Url)!, Secret);

        var other = await Assert.ThrowsAsync<RegistryException>(() => client.GetStringAsync(two.Url + "/x", "application/json", CancellationToken.None, auth));
        Assert.Equal(0, two.Count);
        Assert.DoesNotContain(Secret, other.Message);
        var plain = await Assert.ThrowsAsync<RegistryException>(() => client.GetStringAsync("http://registry.corp.example/x", "application/json", CancellationToken.None, RegistryAuth.Bearer("http://registry.corp.example:80", Secret)));
        Assert.Contains("plain http", plain.Message);
        Assert.DoesNotContain(Secret, plain.Message);
        Assert.DoesNotContain(Secret, auth.ToString());
        // A loopback http registry works while private networks are allowed (this fixture).
        Assert.Equal("{}", await client.GetStringAsync(one.Url + "/x", "application/json", CancellationToken.None, auth));
        Assert.Equal("Bearer " + Secret, one.Auths.Last().Authorization);
    }

    // ------------------------------------------------------------------ test connection

    [Fact]
    public async Task Test_connection_reports_success_a_rejected_credential_and_an_unreachable_registry_without_ever_showing_the_credential()
    {
        using var priv = new FakeRegistry();
        var r = await NewAsync(new() { ["README.md"] = "x\n" });
        priv.Serve("/npm/-/ping", "{}").RequireBearer("/npm/", Secret);
        var good = await SaveRegistryAsync(r, Bearer(Ecosystems.Npm, priv.Npm));
        var bad = await SaveRegistryAsync(r, Bearer(Ecosystems.Npm, priv.Npm, "wrong-token-123", scope: "@bad"));
        var gone = await SaveRegistryAsync(r, Bearer(Ecosystems.Go, "http://127.0.0.1:1/"));

        var repo = await RepoAsync(r, "carol");
        await using var scope = app.Scope(app.UserId("carol"));
        var svc = scope.Get<DependencyRegistryService>();
        var ok = await svc.TestAsync(repo, good.Id);
        var rejected = await svc.TestAsync(repo, bad.Id);
        var unreachable = await svc.TestAsync(repo, gone.Id);

        Assert.True(ok.Ok);
        Assert.Contains("accepted the credential", ok.Message);
        Assert.False(rejected.Ok);
        Assert.Contains("rejected the credential", rejected.Message);
        Assert.False(unreachable.Ok);
        foreach (var m in new[] { ok.Message, rejected.Message, unreachable.Message })
        {
            Assert.DoesNotContain(Secret, m);
            Assert.DoesNotContain("wrong-token-123", m);
        }
        Assert.Equal("Bearer " + Secret, priv.Auths.First(a => a.Path.EndsWith("-/ping")).Authorization);
    }

    [Fact]
    public async Task Test_connection_of_a_nuget_service_index_checks_the_resource_origin_too()
    {
        using var feed = new FakeRegistry();
        using var foreign = new FakeRegistry();
        var r = await NewAsync(new() { ["README.md"] = "x\n" });
        feed.NuGetServiceIndex("/v3/index.json", feed.Url + "/nuget/").RequireBearer("/", Secret);
        var good = await SaveRegistryAsync(r, Bearer(Ecosystems.NuGet, feed.Url + "/v3/index.json"));
        using var feed2 = new FakeRegistry();
        feed2.NuGetServiceIndex("/v3/index.json", foreign.Url + "/nuget/").RequireBearer("/", Secret);
        var odd = await SaveRegistryAsync(r, Bearer(Ecosystems.NuGet, feed2.Url + "/v3/index.json"));

        var repo = await RepoAsync(r, "carol");
        await using var scope = app.Scope(app.UserId("carol"));
        var svc = scope.Get<DependencyRegistryService>();
        Assert.True((await svc.TestAsync(repo, good.Id)).Ok);
        var refused = await svc.TestAsync(repo, odd.Id);
        Assert.False(refused.Ok);
        Assert.Contains("points to another address", refused.Message);
        Assert.Equal(0, foreign.Count);
    }

    // ------------------------------------------------------------------ permissions

    [Theory]
    [InlineData("bob")]  // Developer
    [InlineData("dan")]  // Viewer
    public async Task Only_maintainers_can_see_or_change_private_registries(string user)
    {
        var r = await NewAsync(new() { ["README.md"] = "x\n" });
        var view = await SaveRegistryAsync(r, Bearer(Ecosystems.Npm, "https://npm.corp.example/"), "erin"); // a Maintainer may
        var repo = await RepoAsync(r, user);
        await using var scope = app.Scope(app.UserId(user));
        var svc = scope.Get<DependencyRegistryService>();

        await Assert.ThrowsAsync<ForbiddenException>(() => svc.ListAsync(repo));
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.SaveAsync(repo, Bearer(Ecosystems.Go, "https://go.corp.example/"), app.UserId(user)));
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.ClearCredentialAsync(repo, view.Id, app.UserId(user)));
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.RemoveAsync(repo, view.Id, app.UserId(user)));
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.MoveAsync(repo, view.Id, true));
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.TestAsync(repo, view.Id));
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.DetectAsync(repo));
        await using var db = app.Db();
        Assert.True((await db.DependencyBotRegistries.SingleAsync(x => x.Id == view.Id)).EncryptedCredential != null); // untouched
    }

    [Fact]
    public async Task Removing_a_registry_audits_it_without_the_credential_and_nuget_sources_can_be_reordered()
    {
        var r = await NewAsync(new() { ["README.md"] = "x\n" });
        var first = await SaveRegistryAsync(r, Bearer(Ecosystems.NuGet, "https://nuget.corp.example/v3/index.json"));
        var second = await SaveRegistryAsync(r, new RegistryInput { Ecosystem = Ecosystems.NuGet, Url = "https://nuget2.corp.example/v3/index.json" });
        var repo = await RepoAsync(r, "carol");
        await using var scope = app.Scope(app.UserId("carol"));
        var svc = scope.Get<DependencyRegistryService>();
        Assert.Equal([first.Id, second.Id], (await svc.ListAsync(repo)).Select(x => x.Id));
        await svc.MoveAsync(repo, second.Id, up: true);
        Assert.Equal([second.Id, first.Id], (await svc.ListAsync(repo)).Select(x => x.Id));
        await svc.RemoveAsync(repo, first.Id, app.UserId("carol"));
        Assert.Single(await svc.ListAsync(repo));
        await using var db = app.Db();
        var events = await db.AuditEvents.Where(e => e.ProjectId == r.Project.Id && e.Action.StartsWith("repository.dependency_registry")).ToListAsync();
        Assert.Contains(events, e => e.Action == "repository.dependency_registry_removed");
        Assert.DoesNotContain(events, e => (e.Details ?? "").Contains(Secret));
    }

    // ------------------------------------------------------------------ detection

    [Fact]
    public void Detection_proposes_addresses_only_and_ignores_credentials_variables_and_odd_values()
    {
        var found = RegistryDetector.Detect(
        [
            (".npmrc", "# comment\nregistry=https://npm.corp.example/\n@acme:registry=https://npm.pkg.corp.example/\n//npm.corp.example/:_authToken=" + Secret + "\n@env:registry=${NPM_REGISTRY}\n@cred:registry=https://user:" + Password + "@npm.x.example/\n"),
            ("nuget.config", "<?xml version=\"1.0\"?><configuration><packageSources><clear /><add key=\"corp\" value=\"https://nuget.corp.example/v3/index.json\" /><add key=\"local\" value=\"./packages\" /><add key=\"env\" value=\"%FEED%\" /></packageSources><packageSourceCredentials><corp><add key=\"Password\" value=\"" + Password + "\" /></corp></packageSourceCredentials></configuration>"),
            ("sub/pip.conf", "[global]\nindex-url = https://" + "user:" + Password + "@pypi.corp.example/simple\nextra-index-url = https://extra.example/simple\n"),
            ("pyproject.toml", "[project]\nname='x'\n[[tool.uv.index]]\nname = \"corp\"\nurl = \"https://uv.corp.example/simple\"\n"),
            ("requirements-dev.txt", "--index-url https://req.corp.example/simple\nflask==1.0\n"),
            ("nuget.config.bad", "<x>"),
        ]);

        Assert.Equal(
        [
            (Ecosystems.Npm, "", "https://npm.corp.example/"), (Ecosystems.Npm, "@acme", "https://npm.pkg.corp.example/"),
            (Ecosystems.NuGet, "", "https://nuget.corp.example/v3/index.json"), (Ecosystems.Pip, "", "https://uv.corp.example/simple"),
        ], found.Select(f => (f.Ecosystem, f.Scope, f.Url)));
        var text = string.Join("\n", found.Select(f => f.Url + f.Source));
        Assert.DoesNotContain(Secret, text);
        Assert.DoesNotContain(Password, text);
        Assert.DoesNotContain("user:", text);
    }

    [Fact]
    public void Detection_survives_hostile_nuget_config_without_resolving_anything()
    {
        var xxe = "<?xml version=\"1.0\"?><!DOCTYPE c [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><configuration><packageSources><add key=\"a\" value=\"&x;\"/></packageSources></configuration>";
        Assert.Empty(RegistryDetector.Detect([("nuget.config", xxe)]));
        Assert.Empty(RegistryDetector.Detect([("nuget.config", new string('<', 500_000))]));
        Assert.Empty(RegistryDetector.Detect([(".npmrc", "registry=" + new string('a', 5000))]));
    }

    [Fact]
    public async Task Detected_addresses_are_listed_from_the_repository_and_accepted_with_one_click_without_a_credential()
    {
        var r = await NewAsync(new()
        {
            [".npmrc"] = "@acme:registry=https://npm.pkg.corp.example/\nregistry=https://registry.npmjs.org/\n//npm.pkg.corp.example/:_authToken=" + Secret + "\n",
            ["nuget.config"] = "<configuration><packageSources><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" /><add key=\"corp\" value=\"https://nuget.corp.example/v3/index.json\" /></packageSources></configuration>",
            ["node_modules/x/.npmrc"] = "registry=https://skipped.example/\n",
        });
        var repo = await RepoAsync(r, "carol");
        await using var scope = app.Scope(app.UserId("carol"));
        var svc = scope.Get<DependencyRegistryService>();

        var detected = await svc.DetectAsync(repo);

        Assert.Equal(
        [
            (Ecosystems.Npm, "@acme", "https://npm.pkg.corp.example/"), (Ecosystems.NuGet, "", "https://nuget.corp.example/v3/index.json"),
        ], detected.Select(d => (d.Ecosystem, d.Scope, d.Url))); // the public registries and node_modules are not proposed
        Assert.DoesNotContain(Secret, string.Join("|", detected));

        var view = await svc.AcceptDetectedAsync(repo, detected[0], app.UserId("carol"));
        Assert.Equal((DependencyRegistryAuth.None, false), (view.AuthKind, view.HasCredential));
        Assert.Single(await svc.DetectAsync(repo)); // the accepted one is no longer proposed
    }
}

/// <summary>Private networks not allowed: a private registry must never be contacted, however it got into the database.</summary>
public class DependencyBotPrivateRegistryStrictTests(DepBotStrictApp app) : IClassFixture<DepBotStrictApp>
{
    private const string Secret = "strict-s3cr3t-55aa";

    [Theory]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://localhost/")]
    [InlineData("https://10.0.0.5/registry/")]
    [InlineData("https://169.254.169.254/")]
    [InlineData("http://127.0.0.1:8080/")]
    public async Task A_private_or_loopback_registry_address_is_refused_with_a_hint_about_the_server_setting(string url)
    {
        var project = await app.CreateProjectAsync("carol");
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        await using var scope = app.Scope(app.UserId("carol"));
        var r = (await scope.Get<RepositoryService>().GetRepositoryAsync(project.Key, repo.Slug))!;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Get<DependencyRegistryService>().SaveAsync(r, new RegistryInput { Ecosystem = Ecosystems.Npm, Url = url, AuthKind = DependencyRegistryAuth.Bearer, Secret = Secret }, app.UserId("carol")));

        Assert.Contains("AllowPrivateNetworks", ex.Message);
        Assert.DoesNotContain(Secret, ex.Message);
        await using var db = app.Db();
        Assert.False(await db.DependencyBotRegistries.AnyAsync(x => x.RepositoryId == repo.Id));
    }

    [Fact]
    public async Task A_stored_private_registry_on_a_local_address_is_never_contacted_with_the_credential()
    {
        var project = await app.CreateProjectAsync("carol");
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        var path = app.Services.GetRequiredService<RepoStorage>().PathFor(project.Key, repo.Slug);
        GitOps.CommitFiles(path, "main", new Dictionary<string, string?> { ["package.json"] = "{\"dependencies\": {\"left-pad\": \"^1.1.0\"}}\n" }, "m", new GitIdentity("T", "t@example.com"), DateTimeOffset.UtcNow);
        app.Registry.NpmPackage("left-pad", ["1.1.0", "1.2.0"]).RequireBearer("/npm/", Secret);
        var protector = app.Services.GetRequiredService<RegistryCredentialProtector>();
        await using (var db = app.Db())
        {
            db.DependencyBotRegistries.Add(new DependencyBotRegistry
            {
                RepositoryId = repo.Id, Ecosystem = Ecosystems.Npm, Url = app.Registry.Npm, AuthKind = DependencyRegistryAuth.Bearer, EncryptedCredential = protector.ProtectCredential(Secret),
            });
            await db.SaveChangesAsync();
        }

        await using var scope = app.Scope(app.UserId("carol"));
        var r = (await scope.Get<RepositoryService>().GetRepositoryAsync(project.Key, repo.Slug))!;
        var report = await scope.Get<DependencyBotService>().CheckNowAsync(r);

        Assert.Equal(0, app.Registry.Count);
        Assert.Contains(report.Warnings, w => w.Contains("left-pad") && w.Contains("local address"));
        Assert.DoesNotContain(report.Warnings, w => w.Contains(Secret));
    }
}
