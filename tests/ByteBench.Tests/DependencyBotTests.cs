using System.Text;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Accounts;
using ByteBench.Services.DependencyBot;
using ByteBench.Services.Git;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

/// <summary>The bot against fake registries on 127.0.0.1 (<c>DependencyBot:AllowPrivateNetworks=true</c>); the scheduled worker is off.</summary>
public sealed class DepBotApp : TestApp
{
    public FakeRegistry Registry { get; } = new();

    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["DependencyBot:Enabled"] = "false";
        settings["DependencyBot:AllowPrivateNetworks"] = "true";
        settings["DependencyBot:RequestsPerSecond"] = "0";
        settings["DependencyBot:MaxResponseBytes"] = "30000";
        settings["DependencyBot:Registries:Npm"] = Registry.Npm;
        settings["DependencyBot:Registries:NuGet"] = Registry.NuGet;
        settings["DependencyBot:Registries:PyPI"] = Registry.PyPI;
        settings["DependencyBot:Registries:Go"] = Registry.Go;
    }

    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) Registry.Dispose(); }
}

/// <summary>Private networks are not allowed (the default when Webhooks:AllowPrivateNetworks is off): the guard must refuse the registry.</summary>
public sealed class DepBotStrictApp : TestApp
{
    public FakeRegistry Registry { get; } = new();

    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["Webhooks:AllowPrivateNetworks"] = "false";
        settings["DependencyBot:Enabled"] = "false";
        settings["DependencyBot:RequestsPerSecond"] = "0";
        settings["DependencyBot:Registries:Npm"] = Registry.Npm;
    }

    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) Registry.Dispose(); }
}

public class DependencyBotParserTests
{
    private static Dependency One(string eco, string path, string text, string name) => ManifestParser.Parse(eco, path, text).Single(d => d.Name == name);

    [Fact]
    public void Npm_replaces_only_the_version_text_keeps_the_prefix_and_skips_non_registry_specs()
    {
        const string json = "{\n  \"name\": \"demo\",\n  \"version\": \"1.0.0\",\n  \"dependencies\": {\n    \"left-pad\": \"^1.1.0\",\n    \"@types/node\": \"~20.1.0\",\n" +
                            "    \"local\": \"file:../x\",\n    \"fork\": \"github:a/b\",\n    \"star\": \"*\",\n    \"ranged\": \">=1 <2\",\n    \"alias\": \"npm:other@1.0.0\"\n  },\n" +
                            "  \"devDependencies\": { \"typescript\": \"5.0.2\" },\n  \"peerDependencies\": { \"react\": \"^18.0.0\" },\n  \"scripts\": { \"build\": \"1.2.3\" }\n}\n";
        var deps = ManifestParser.Parse(Ecosystems.Npm, "package.json", json);
        Assert.Equal(["@types/node", "left-pad", "typescript"], deps.Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal));
        var leftPad = deps.Single(d => d.Name == "left-pad");
        Assert.Equal(("^", "1.1.0", "dependencies"), (leftPad.Prefix, leftPad.Current, leftPad.Group));
        Assert.Equal("devDependencies", deps.Single(d => d.Name == "typescript").Group);
        Assert.Equal(json.Replace("\"^1.1.0\"", "\"^1.2.0\""), ManifestParser.Replace(json, leftPad, "1.2.0"));
        Assert.Equal(json.Replace("\"~20.1.0\"", "\"~20.2.0\""), ManifestParser.Replace(json, deps.Single(d => d.Name == "@types/node"), "20.2.0"));
        Assert.Equal(json.Replace("\"5.0.2\"", "\"5.1.0\""), ManifestParser.Replace(json, deps.Single(d => d.Name == "typescript"), "5.1.0"));
    }

    [Fact]
    public void Npm_keeps_a_byte_order_mark_and_windows_line_endings_exactly()
    {
        var json = "﻿{\r\n\t\"dependencies\": {\r\n\t\t\"é-pkg\": \"1.0.0\",\r\n\t\t\"left-pad\": \"1.1.0\"\r\n\t}\r\n}\r\n";
        var dep = ManifestParser.Parse(Ecosystems.Npm, "package.json", json).Single(d => d.Name == "left-pad");
        var edited = ManifestParser.Replace(json, dep, "1.2.0");
        Assert.Equal(json.Replace("\"1.1.0\"", "\"1.2.0\""), edited);
        Assert.Equal(ManifestParser.Encode(json).Length, ManifestParser.Encode(edited).Length);
        Assert.Equal(0xEF, ManifestParser.Encode(edited)[0]); // the BOM is still there
    }

    [Fact]
    public void Npm_never_trusts_unusual_names_or_broken_json()
    {
        Assert.Empty(ManifestParser.Parse(Ecosystems.Npm, "package.json", "{ not json"));
        Assert.Empty(ManifestParser.Parse(Ecosystems.Npm, "package.json", "{\"dependencies\": {\"../etc/passwd\": \"1.0.0\", \"Bad Name\": \"1.0.0\", \"a/b/c\": \"1.0.0\"}}"));
    }

    [Fact]
    public void NuGet_reads_attribute_and_child_versions_in_projects_and_central_files_and_skips_ranges()
    {
        const string csproj = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />\n" +
                              "    <PackageReference Version=\"2.0.0\" Include=\"Dapper\"/>\n    <PackageReference Include=\"Child\">\n      <Version>1.2.3</Version>\n    </PackageReference>\n" +
                              "    <PackageReference Include=\"Floating\" Version=\"1.*\" />\n    <PackageReference Include=\"Range\" Version=\"[1.0,2.0)\" />\n" +
                              "    <PackageReference Include=\"Prop\" Version=\"$(PropVersion)\" />\n    <PackageReference Include=\"Central\" />\n  </ItemGroup>\n</Project>\n";
        var deps = ManifestParser.Parse(Ecosystems.NuGet, "src/App.csproj", csproj);
        Assert.Equal(["Child", "Dapper", "Newtonsoft.Json"], deps.Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(csproj.Replace("Version=\"13.0.1\"", "Version=\"13.0.3\""), ManifestParser.Replace(csproj, One(Ecosystems.NuGet, "src/App.csproj", csproj, "Newtonsoft.Json"), "13.0.3"));
        Assert.Equal(csproj.Replace("Version=\"2.0.0\"", "Version=\"2.0.35\""), ManifestParser.Replace(csproj, One(Ecosystems.NuGet, "src/App.csproj", csproj, "Dapper"), "2.0.35"));
        Assert.Equal(csproj.Replace("<Version>1.2.3</Version>", "<Version>1.2.4</Version>"), ManifestParser.Replace(csproj, One(Ecosystems.NuGet, "src/App.csproj", csproj, "Child"), "1.2.4"));

        const string props = "<Project>\n  <ItemGroup>\n    <PackageVersion Include=\"Serilog\" Version=\"3.0.0\" />\n  </ItemGroup>\n</Project>\n";
        var central = One(Ecosystems.NuGet, "Directory.Packages.props", props, "Serilog");
        Assert.Equal("central", central.Group);
        Assert.Equal(props.Replace("3.0.0", "3.1.1"), ManifestParser.Replace(props, central, "3.1.1"));
    }

    [Fact]
    public void Pip_reads_pinned_requirements_and_pyproject_pins_only()
    {
        const string req = "# a comment==1.0.0\n-r other.txt\nrequests==2.28.0  # http\nflask[async]==2.2.0 ; python_version >= \"3.8\"\nnumpy>=1.0\nwild==1.*\nDjango==4.2.1\r\n\n";
        var deps = ManifestParser.Parse(Ecosystems.Pip, "requirements.txt", req);
        Assert.Equal(["Django", "flask", "requests"], deps.Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(req.Replace("requests==2.28.0", "requests==2.31.0"), ManifestParser.Replace(req, deps.Single(d => d.Name == "requests"), "2.31.0"));
        Assert.Equal(req.Replace("flask[async]==2.2.0", "flask[async]==2.3.0"), ManifestParser.Replace(req, deps.Single(d => d.Name == "flask"), "2.3.0"));
        Assert.Equal(req.Replace("Django==4.2.1", "Django==4.2.9"), ManifestParser.Replace(req, deps.Single(d => d.Name == "Django"), "4.2.9"));

        const string toml = "[project]\nname = \"x\"\ndependencies = [\n  \"httpx==0.24.0\",\n  \"rich>=13\",\n]\n[project.optional-dependencies]\ndev = [\"pytest==7.3.1\"]\n";
        var py = ManifestParser.Parse(Ecosystems.Pip, "pyproject.toml", toml);
        Assert.Equal(["httpx", "pytest"], py.Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(toml.Replace("pytest==7.3.1", "pytest==7.4.0"), ManifestParser.Replace(toml, py.Single(d => d.Name == "pytest"), "7.4.0"));
    }

    [Fact]
    public void Go_reads_direct_requirements_in_blocks_and_single_lines_and_skips_indirect_ones()
    {
        const string mod = "module example.com/app\n\ngo 1.22\n\nrequire (\n\tgithub.com/gorilla/mux v1.8.0\n\tgolang.org/x/text v0.3.0 // indirect\n)\n\nrequire github.com/pkg/errors v0.9.0\n\nreplace github.com/gorilla/mux => ./mux\n";
        var deps = ManifestParser.Parse(Ecosystems.Go, "go.mod", mod);
        Assert.Equal(["github.com/gorilla/mux", "github.com/pkg/errors"], deps.Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(mod.Replace("v1.8.0", "v1.9.0"), ManifestParser.Replace(mod, deps.Single(d => d.Name.EndsWith("mux")), "v1.9.0"));
        Assert.Equal(mod.Replace("v0.9.0", "v0.9.1"), ManifestParser.Replace(mod, deps.Single(d => d.Name.EndsWith("errors")), "v0.9.1"));
    }

    [Fact]
    public void A_manifest_that_changed_since_the_check_is_refused()
    {
        const string json = "{\"dependencies\": {\"left-pad\": \"^1.1.0\"}}";
        var dep = One(Ecosystems.Npm, "package.json", json, "left-pad");
        Assert.Throws<InvalidOperationException>(() => ManifestParser.Replace("{\"dependencies\": {\"left-pad\": \"^1.9.9\"}}", dep, "1.2.0"));
    }
}

public class DependencyBotVersionTests
{
    private static DepVersion V(string s) => DepVersion.Parse(s)!;
    private static IEnumerable<DepVersion> All(params string[] v) => v.Select(V);

    [Fact]
    public void Orders_versions_like_semver_and_treats_prereleases_as_older()
    {
        Assert.True(V("1.10.0") > V("1.9.9"));
        Assert.True(V("1.0.0") > V("1.0.0-rc.1"));
        Assert.True(V("1.0.0-rc.2") > V("1.0.0-rc.1"));
        Assert.True(V("v2.0.0") > V("1.99.99"));
        Assert.True(V("2.0.0rc1").IsPrerelease);
        Assert.False(V("1.0.0.post1").IsPrerelease);
        Assert.False(V("v1.2.3+incompatible").IsPrerelease);
        Assert.Equal("major", V("2.0.0").BumpFrom(V("1.9.9")));
        Assert.Equal("minor", V("1.3.0").BumpFrom(V("1.2.9")));
        Assert.Equal("patch", V("1.2.4").BumpFrom(V("1.2.3")));
        Assert.Null(DepVersion.Parse("latest"));
    }

    [Fact]
    public void Strategy_limits_how_far_an_update_may_jump_and_prereleases_are_never_proposed()
    {
        var available = All("1.1.0", "1.1.3", "1.2.0", "2.0.0", "3.0.0-beta.1");
        var none = new List<IgnoreRule>();
        Assert.Equal("1.1.3", UpdateChooser.Choose(V("1.1.0"), available, DependencyStrategy.Patch, "p", none)!.Text);
        Assert.Equal("1.2.0", UpdateChooser.Choose(V("1.1.0"), available, DependencyStrategy.Minor, "p", none)!.Text);
        Assert.Equal("2.0.0", UpdateChooser.Choose(V("1.1.0"), available, DependencyStrategy.Major, "p", none)!.Text);
        Assert.Null(UpdateChooser.Choose(V("2.0.0"), available, DependencyStrategy.Major, "p", none)); // only a beta is newer
        Assert.Equal("3.0.0-beta.1", UpdateChooser.Choose(V("2.0.0-rc.1"), All("2.0.0-rc.1", "2.0.0-rc.2", "3.0.0-beta.1"), DependencyStrategy.Major, "p", none)!.Text);
    }

    [Fact]
    public void Ignore_rules_block_names_prefixes_and_version_ranges()
    {
        var rules = IgnoreRule.ParseAll("left-pad@>=1.2\n@types/*\nlodash@4.x # keep\n# comment\n\nreact@^18 <18.3\nexact@2.0.0");
        var available = All("1.1.0", "1.1.9", "1.2.0", "2.0.0");
        Assert.Equal("1.1.9", UpdateChooser.Choose(V("1.1.0"), available, DependencyStrategy.Major, "left-pad", rules)!.Text);
        Assert.Null(UpdateChooser.Choose(V("1.0.0"), available, DependencyStrategy.Major, "@types/node", rules));
        Assert.Equal("2.0.0", UpdateChooser.Choose(V("1.0.0"), available, DependencyStrategy.Major, "other", rules)!.Text);
        Assert.Equal("5.0.0", UpdateChooser.Choose(V("4.0.0"), All("4.1.0", "5.0.0"), DependencyStrategy.Major, "lodash", rules)!.Text); // 4.x ignored, 5 allowed
        Assert.Equal("2.0.1", UpdateChooser.Choose(V("1.0.0"), All("2.0.0", "2.0.1"), DependencyStrategy.Major, "exact", rules)!.Text);
        Assert.True(VersionRange.Parse("^1.2.3").Matches(V("1.9.0")));
        Assert.False(VersionRange.Parse("^1.2.3").Matches(V("2.0.0")));
        Assert.True(VersionRange.Parse("~1.2.3").Matches(V("1.2.9")));
        Assert.False(VersionRange.Parse("~1.2.3").Matches(V("1.3.0")));
        Assert.True(VersionRange.Parse("1.2.x").Matches(V("1.2.7")));
        Assert.True(VersionRange.Parse(">=1.0, <2").Matches(V("1.5.0")));
        Assert.False(VersionRange.Parse(">=1.0 <2").Matches(V("2.0.0")));
        Assert.False(VersionRange.Parse("not a range").IsValid);
    }
}

public class DependencyBotTests(DepBotApp app) : IClassFixture<DepBotApp>
{
    private static int counter;
    private FakeRegistry Registry => app.Registry;

    /// <summary>One repository with manifests for all four ecosystems whose packages carry a unique suffix, and matching registry data.</summary>
    private sealed record Scenario(Project Project, Repository Repo, string Path, string Sfx)
    {
        public string LeftPad => "left-pad-" + Sfx;
        public string TypesNode => "@types/node-" + Sfx;
        public string TypeScript => "typescript-" + Sfx;
        public string Json => "Contoso.Json" + Sfx;
        public string Serilog => "Serilog" + Sfx;
        public string Dapper => "Dapper" + Sfx;
        public string Requests => "requests-" + Sfx;
        public string Flask => "flask-" + Sfx;
        public string Mux => "github.com/acme/mux" + Sfx;
        public string Errors => "github.com/acme/errors" + Sfx;
        public string PackageJson => "{\n  \"name\": \"demo\",\n  \"dependencies\": {\n    \"" + LeftPad + "\": \"^1.1.0\",\n    \"" + TypesNode + "\": \"~20.1.0\",\n    \"local-thing\": \"file:../x\"\n  },\n" +
                                     "  \"devDependencies\": {\n    \"" + TypeScript + "\": \"5.0.2\"\n  }\n}\n";
        public string Csproj => "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"" + Json + "\" Version=\"13.0.1\" />\n    <PackageReference Include=\"" + Dapper + "\">\n      <Version>2.0.0</Version>\n    </PackageReference>\n  </ItemGroup>\n</Project>\n";
        public string Props => "<Project>\n  <ItemGroup>\n    <PackageVersion Include=\"" + Serilog + "\" Version=\"3.0.0\" />\n  </ItemGroup>\n</Project>\n";
        public string Requirements => "# deps\n" + Requests + "==2.28.0  # http\n" + Flask + "[async]==2.2.0\nnumpy>=1.0\n";
        public string GoMod => "module example.com/app\n\ngo 1.22\n\nrequire (\n\t" + Mux + " v1.8.0\n\tgolang.org/x/text v0.3.0 // indirect\n)\n\nrequire " + Errors + " v0.9.0\n";
    }

    private static readonly SemaphoreSlim UserGate = new(1, 1);

    private async Task EnsureMaintainerAsync()
    {
        await UserGate.WaitAsync();
        try { await using var db = app.Db(); if (!await db.Users.AnyAsync(u => u.UserName == "erin")) await app.CreateUserAsync("erin"); }
        finally { UserGate.Release(); }
    }

    private async Task<Scenario> NewAsync(bool lockfile = true, bool registries = true)
    {
        await EnsureMaintainerAsync();
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("dan", ProjectRole.Viewer), ("erin", ProjectRole.Maintainer));
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        var path = app.Services.GetRequiredService<RepoStorage>().PathFor(project.Key, repo.Slug);
        var s = new Scenario(project, repo, path, Interlocked.Increment(ref counter).ToString());
        var files = new Dictionary<string, string?>
        {
            ["package.json"] = s.PackageJson, ["src/App.csproj"] = s.Csproj, ["Directory.Packages.props"] = s.Props,
            ["requirements.txt"] = s.Requirements, ["go.mod"] = s.GoMod, ["README.md"] = "# demo\n",
        };
        if (lockfile) { files["package-lock.json"] = "{}\n"; files["go.sum"] = "\n"; }
        GitOps.CommitFiles(path, "main", files, "Add manifests", new GitIdentity("Test", "test@example.com"), DateTimeOffset.UtcNow);
        if (registries)
        {
            Registry.NpmPackage(s.LeftPad, ["1.1.0", "1.1.3", "1.2.0", "2.0.0", "2.1.0-beta.1"], homepage: "https://github.com/acme/left-pad")
                .NpmPackage(s.TypesNode, ["20.1.0", "20.1.5", "20.2.0", "21.0.0"])
                .NpmPackage(s.TypeScript, ["5.0.2", "5.0.3", "5.1.0"])
                .NuGetPackage(s.Json, "13.0.1", "13.0.3", "14.0.0-beta1")
                .NuGetPackage(s.Serilog, "3.0.0", "3.1.1", "4.0.0")
                .NuGetPackage(s.Dapper, "2.0.0", "2.0.35")
                .PyPiPackage(s.Requests, "2.28.0", "2.28.2", "2.31.0", "3.0.0")
                .PyPiPackage(s.Flask, "2.2.0", "2.2.5", "2.3.0")
                .GoModule(s.Mux, "v1.8.0", "v1.8.1")
                .GoModule(s.Errors, "v0.9.0", "v0.9.1");
        }
        return s;
    }

    private static DependencyBotInput Input(Action<DependencyBotInput>? tweak = null)
    {
        var input = new DependencyBotInput { Enabled = false, Strategy = DependencyStrategy.Minor }; // enabled repositories would be picked up by the scheduler test
        tweak?.Invoke(input);
        return input;
    }

    private async Task<Repository> RepoAsync(Scenario s, string user)
    {
        await using var scope = app.Scope(app.UserId(user));
        return (await scope.Get<RepositoryService>().GetRepositoryAsync(s.Project.Key, s.Repo.Slug))!;
    }

    private async Task SaveAsync(Scenario s, DependencyBotInput input, string user = "carol")
    {
        var repo = await RepoAsync(s, user);
        await using var scope = app.Scope(app.UserId(user));
        await scope.Get<DependencyBotService>().SaveAsync(repo, input, app.UserId(user));
    }

    private async Task<DependencyReport> CheckAsync(Scenario s, string user = "carol")
    {
        var repo = await RepoAsync(s, user);
        await using var scope = app.Scope(app.UserId(user));
        return await scope.Get<DependencyBotService>().CheckNowAsync(repo);
    }

    private async Task<ApplyResult> CreateAsync(Scenario s, string? key, string user = "carol")
    {
        var repo = await RepoAsync(s, user);
        await using var scope = app.Scope(app.UserId(user));
        return await scope.Get<DependencyBotService>().CreatePullRequestAsync(repo, key, app.UserId(user));
    }

    private static OutdatedItem Item(DependencyReport r, string name) => r.Items.Single(i => i.Name == name);

    [Fact]
    public async Task Check_lists_outdated_dependencies_of_every_ecosystem_with_the_version_the_strategy_allows()
    {
        var s = await NewAsync();
        await SaveAsync(s, Input());
        var report = await CheckAsync(s);

        Assert.Empty(report.Warnings);
        Assert.Equal(5, report.ManifestsRead); // package.json, App.csproj, Directory.Packages.props, requirements.txt, go.mod
        Assert.Equal(10, report.Items.Count);
        var leftPad = Item(report, s.LeftPad);
        Assert.Equal(("1.1.0", "1.2.0", "2.0.0", "minor", ItemStatus.Update), (leftPad.Current, leftPad.Proposed, leftPad.Latest, leftPad.Kind, leftPad.Status));
        Assert.Equal("https://github.com/acme/left-pad/releases/tag/v1.2.0", leftPad.ReleaseUrl);
        Assert.Equal("20.2.0", Item(report, s.TypesNode).Proposed);
        Assert.Equal("5.1.0", Item(report, s.TypeScript).Proposed);
        Assert.Equal("devDependencies", Item(report, s.TypeScript).Group);
        Assert.Equal(("13.0.1", "13.0.3", "13.0.3", "patch"), (Item(report, s.Json).Current, Item(report, s.Json).Proposed, Item(report, s.Json).Latest, Item(report, s.Json).Kind)); // the beta is never offered
        Assert.Equal("3.1.1", Item(report, s.Serilog).Proposed);
        Assert.Equal("4.0.0", Item(report, s.Serilog).Latest);
        Assert.Equal("2.0.35", Item(report, s.Dapper).Proposed);
        Assert.Equal("2.31.0", Item(report, s.Requests).Proposed);
        Assert.Equal("2.3.0", Item(report, s.Flask).Proposed);
        Assert.Equal("v1.8.1", Item(report, s.Mux).Proposed);
        Assert.Equal("v0.9.1", Item(report, s.Errors).Proposed);
        Assert.DoesNotContain(report.Items, i => i.Name is "local-thing" or "numpy" or "golang.org/x/text");

        // The table is stored for the settings page.
        await using var scope = app.Scope(app.UserId("carol"));
        var stored = DependencyBotService.Report(await scope.Get<DependencyBotService>().GetAsync(await RepoAsync(s, "carol")))!;
        Assert.Equal(10, stored.Items.Count);
    }

    [Fact]
    public async Task Update_strategy_and_the_ignore_list_decide_the_proposed_version()
    {
        var s = await NewAsync();
        await SaveAsync(s, Input(i => i.Strategy = DependencyStrategy.Patch));
        var patch = await CheckAsync(s);
        Assert.Equal("1.1.3", Item(patch, s.LeftPad).Proposed);
        Assert.Equal("20.1.5", Item(patch, s.TypesNode).Proposed);
        var held = Item(patch, s.TypeScript); // 5.0.2 -> 5.0.3 is a patch
        Assert.Equal("5.0.3", held.Proposed);
        Assert.Equal(ItemStatus.Update, held.Status);

        await SaveAsync(s, Input(i => i.Strategy = DependencyStrategy.Major));
        var major = await CheckAsync(s);
        Assert.Equal("2.0.0", Item(major, s.LeftPad).Proposed);
        Assert.Equal("major", Item(major, s.LeftPad).Kind);
        Assert.Equal("4.0.0", Item(major, s.Serilog).Proposed);

        await SaveAsync(s, Input(i => { i.Strategy = DependencyStrategy.Major; i.IgnoreList = $"{s.LeftPad}@>=1.2\n@types/*\n{s.Serilog}"; }));
        var ignored = await CheckAsync(s);
        Assert.Equal("1.1.3", Item(ignored, s.LeftPad).Proposed); // 1.2.0 and 2.0.0 are ignored
        var types = Item(ignored, s.TypesNode);
        Assert.Equal((ItemStatus.HeldBack, (string?)null, "21.0.0"), (types.Status, types.Proposed, types.Latest));
        Assert.Equal(ItemStatus.HeldBack, Item(ignored, s.Serilog).Status);

        // Ecosystems can be switched off.
        await SaveAsync(s, Input(i => i.Ecosystems = ["npm"]));
        var npmOnly = await CheckAsync(s);
        Assert.All(npmOnly.Items, i => Assert.Equal("npm", i.Ecosystem));
        Assert.Equal(3, npmOnly.Items.Count);
    }

    [Fact]
    public async Task Create_update_PR_branches_edits_only_the_manifest_and_opens_a_pull_request_as_the_bot()
    {
        var s = await NewAsync();
        await SaveAsync(s, Input(i => { i.Labels = "dependencies, bot"; i.Reviewers = "bob"; }));
        var report = await CheckAsync(s);
        var key = Item(report, s.LeftPad).Key;
        var before = GitOps.ReadFileBytes(s.Path, "main", "package.json", 1 << 20)!;

        var result = await CreateAsync(s, key);
        Assert.Empty(result.Warnings);
        var number = Assert.Single(result.PullRequests);

        var branch = $"bytebench/deps/npm-{s.LeftPad}-1.2.0";
        Assert.True(GitOps.BranchExists(s.Path, branch));
        // The exact manifest diff: one string changed, nothing else in the file, nothing else in the tree.
        var after = GitOps.ReadFileBytes(s.Path, branch, "package.json", 1 << 20)!;
        Assert.Equal(Encoding.UTF8.GetString(before).Replace("\"^1.1.0\"", "\"^1.2.0\""), Encoding.UTF8.GetString(after));
        var allFiles = GitOps.ListFiles(s.Path, "main")!.Select(f => f.Path).ToList();
        Assert.Equal(["package.json"], GitOps.ChangedFiles(s.Path, "main", branch, allFiles));
        Assert.Equal(GitOps.ResolveSha(s.Path, "main"), GitOps.Log(s.Path, branch, null, 0, 5).Items[1].Sha); // one commit on top of main

        await using var db = app.Db();
        var pr = await db.PullRequests.Include(p => p.Author).SingleAsync(p => p.RepositoryId == s.Repo.Id && p.Number == number);
        Assert.Equal(DependencyBotAccount.UserName, pr.Author.UserName);
        Assert.Equal((branch, "main", PullRequestState.Open), (pr.SourceBranch, pr.TargetBranch, pr.State));
        Assert.Equal($"Bump {s.LeftPad} from 1.1.0 to 1.2.0", pr.Title);
        Assert.Contains("`1.1.0`", pr.Description);
        Assert.Contains("`1.2.0`", pr.Description);
        Assert.Contains("[" + s.LeftPad + " 1.2.0](https://github.com/acme/left-pad/releases/tag/v1.2.0)", pr.Description);
        Assert.Contains("package-lock.json", pr.Description); // the lockfile is not regenerated, and the PR says so
        Assert.Contains("not updated", pr.Description);
        Assert.DoesNotContain("go.sum", pr.Description);
        Assert.Contains("`dependencies`, `bot`", pr.Description);
        Assert.Contains(await db.PullRequestReviewers.Where(r => r.PullRequestId == pr.Id).Select(r => r.UserId).ToListAsync(), id => id == app.UserId("bob"));
        var remembered = await db.DependencyBotPullRequests.SingleAsync(p => p.RepositoryId == s.Repo.Id);
        Assert.Equal((s.LeftPad, "1.1.0", "1.2.0", pr.Id), (remembered.Package, remembered.FromVersion, remembered.ToVersion, remembered.PullRequestId));

        // Audited: who clicked, which package, no secrets.
        var audit = await db.AuditEvents.SingleAsync(e => e.Action == "repository.dependency_pr_opened" && e.ProjectId == s.Project.Id);
        Assert.Equal("carol", audit.ActorName);
        Assert.Contains(s.LeftPad, audit.Details);
        Assert.Contains("\"to\":\"1.2.0\"", audit.Details);
        Assert.Equal(pr.Id.ToString(), audit.TargetId);
    }

    [Fact]
    public async Task Go_edits_keep_the_v_prefix_and_say_that_go_sum_is_not_regenerated()
    {
        var s = await NewAsync();
        await SaveAsync(s, Input(i => i.Ecosystems = ["go"]));
        var report = await CheckAsync(s);
        var result = await CreateAsync(s, Item(report, s.Mux).Key);
        var number = Assert.Single(result.PullRequests);
        var branch = $"bytebench/deps/go-{s.Mux.Replace("/", "-")}-v1.8.1";
        Assert.Equal(s.GoMod.Replace("v1.8.0", "v1.8.1"), Encoding.UTF8.GetString(GitOps.ReadFileBytes(s.Path, branch, "go.mod", 1 << 20)!));
        await using var db = app.Db();
        var pr = await db.PullRequests.SingleAsync(p => p.RepositoryId == s.Repo.Id && p.Number == number);
        Assert.Contains("go.sum", pr.Description);
        Assert.DoesNotContain("package-lock.json", pr.Description);
    }

    [Fact]
    public async Task A_package_with_an_open_bot_PR_is_skipped_and_one_closed_unmerged_is_not_proposed_again()
    {
        var s = await NewAsync();
        await SaveAsync(s, Input(i => i.Ecosystems = ["npm"]));
        var key = Item(await CheckAsync(s), s.LeftPad).Key;
        var first = await CreateAsync(s, key);
        var number = Assert.Single(first.PullRequests);

        // Open PR: shown as such, can't be created again, and a second run does nothing.
        var again = await CheckAsync(s);
        var open = Item(again, s.LeftPad);
        Assert.Equal((ItemStatus.PullRequestOpen, number), (open.Status, open.PullRequestNumber));
        var repeat = await CreateAsync(s, key);
        Assert.Empty(repeat.PullRequests);
        await using (var db = app.Db()) Assert.Equal(1, await db.PullRequests.CountAsync(p => p.RepositoryId == s.Repo.Id));

        // Closed without merging: this version is remembered and not proposed again...
        await using (var scope = app.Scope(app.UserId("carol")))
        {
            var prId = (await scope.Get<PullRequestService>().GetAsync(s.Project.Key, s.Repo.Slug, number))!.Id;
            await scope.Get<PullRequestService>().DeclineAsync(prId, app.UserId("carol"));
        }
        var closed = Item(await CheckAsync(s), s.LeftPad);
        Assert.Equal((ItemStatus.Closed, number), (closed.Status, closed.PullRequestNumber));
        Assert.Empty((await CreateAsync(s, key)).PullRequests);
        await using (var db = app.Db()) Assert.Equal(1, await db.PullRequests.CountAsync(p => p.RepositoryId == s.Repo.Id));

        // ...but a newer version is.
        Registry.NpmPackage(s.LeftPad, ["1.1.0", "1.1.3", "1.2.0", "1.2.1", "2.0.0"]);
        var newer = Item(await CheckAsync(s), s.LeftPad);
        Assert.Equal((ItemStatus.Update, "1.2.1"), (newer.Status, newer.Proposed));
        var second = await CreateAsync(s, newer.Key);
        Assert.Single(second.PullRequests);
        Assert.True(GitOps.BranchExists(s.Path, $"bytebench/deps/npm-{s.LeftPad}-1.2.1"));
    }

    [Fact]
    public async Task Grouped_mode_opens_one_pull_request_for_all_updates_and_the_open_limit_is_respected()
    {
        var s = await NewAsync();
        await SaveAsync(s, Input(i => { i.Ecosystems = ["npm", "pip"]; i.Grouped = true; }));
        await CheckAsync(s);
        var grouped = await CreateAsync(s, null);
        var number = Assert.Single(grouped.PullRequests);
        await using var db = app.Db();
        var pr = await db.PullRequests.SingleAsync(p => p.RepositoryId == s.Repo.Id && p.Number == number);
        Assert.Equal("Update 5 dependencies", pr.Title);
        Assert.StartsWith("bytebench/deps/group-", pr.SourceBranch);
        var allFiles = GitOps.ListFiles(s.Path, "main")!.Select(f => f.Path).ToList();
        Assert.Equal(["package.json", "requirements.txt"], GitOps.ChangedFiles(s.Path, "main", pr.SourceBranch, allFiles));
        var json = Encoding.UTF8.GetString(GitOps.ReadFileBytes(s.Path, pr.SourceBranch, "package.json", 1 << 20)!);
        Assert.Contains($"\"{s.LeftPad}\": \"^1.2.0\"", json);
        Assert.Contains($"\"{s.TypesNode}\": \"~20.2.0\"", json);
        Assert.Contains($"\"{s.TypeScript}\": \"5.1.0\"", json);
        Assert.Contains(s.Requests + "==2.31.0", Encoding.UTF8.GetString(GitOps.ReadFileBytes(s.Path, pr.SourceBranch, "requirements.txt", 1 << 20)!));
        Assert.Equal(5, await db.DependencyBotPullRequests.CountAsync(p => p.RepositoryId == s.Repo.Id && p.PullRequestId == pr.Id));

        // One PR per package, limited to 2 open at a time.
        var t = await NewAsync();
        await SaveAsync(t, Input(i => { i.Ecosystems = ["npm"]; i.MaxOpenPullRequests = 2; }));
        await CheckAsync(t);
        var limited = await CreateAsync(t, null);
        Assert.Equal(2, limited.PullRequests.Count);
        Assert.Contains(limited.Warnings, w => w.Contains("limit"));
        await using var db2 = app.Db();
        Assert.Equal(2, await db2.PullRequests.CountAsync(p => p.RepositoryId == t.Repo.Id && p.State == PullRequestState.Open && p.AuthorId != app.UserId("carol")));
        Assert.Empty((await CreateAsync(t, null)).PullRequests); // full: nothing more until some are merged or closed
    }

    [Fact]
    public async Task Registry_failures_become_warnings_and_never_stop_the_check()
    {
        var s = await NewAsync();
        Registry.Serve("/npm/" + s.TypeScript, "{}", status: 404); // unknown package
        Registry.Serve("/npm/" + s.LeftPad, "this is not json");
        Registry.Serve("/pypi/" + s.Flask + "/json", "{}", status: 500);
        Registry.Serve("/nuget/" + s.Json.ToLowerInvariant() + "/index.json", "{}", status: 302);
        await SaveAsync(s, Input());
        var report = await CheckAsync(s);

        Assert.Contains(report.Warnings, w => w.Contains(s.TypeScript) && w.Contains("doesn't know"));
        Assert.Contains(report.Warnings, w => w.Contains(s.LeftPad) && w.Contains("wasn't JSON"));
        Assert.Contains(report.Warnings, w => w.Contains(s.Flask) && w.Contains("HTTP 500"));
        Assert.Contains(report.Warnings, w => w.Contains(s.Json) && w.Contains("redirected"));
        // The packages that worked are in the table.
        Assert.Equal("20.2.0", Item(report, s.TypesNode).Proposed);
        Assert.Equal("2.31.0", Item(report, s.Requests).Proposed);
        Assert.DoesNotContain(report.Items, i => i.Name == s.TypeScript || i.Name == s.LeftPad || i.Name == s.Flask || i.Name == s.Json);
    }

    [Fact]
    public async Task A_registry_that_rate_limits_ends_the_run_for_that_registry_only()
    {
        var s = await NewAsync();
        Registry.Serve("/npm/" + s.LeftPad, "{}", status: 429);
        await SaveAsync(s, Input());
        var calls = Registry.Count;
        var report = await CheckAsync(s);
        Assert.Contains(report.Warnings, w => w.Contains("rate limiting"));
        Assert.Contains(report.Warnings, w => w.Contains("npm registry isn't answering"));
        // The other npm packages weren't even asked (one request hit the limit), the other registries were checked.
        Assert.DoesNotContain(Registry.Requests.Skip(calls), p => p.Contains(s.TypeScript) || p == "/npm/" + s.TypesNode.Replace("/", "%2F"));
        Assert.DoesNotContain(report.Items, i => i.Ecosystem == "npm");
        Assert.Equal("2.31.0", Item(report, s.Requests).Proposed);
    }

    [Fact]
    public async Task An_oversized_registry_answer_is_refused()
    {
        var s = await NewAsync();
        var versions = Enumerable.Range(0, 3000).Select(i => $"1.0.{i}").ToArray(); // well over DependencyBot:MaxResponseBytes (30 KB)
        Registry.NpmPackage(s.LeftPad, versions);
        await SaveAsync(s, Input(i => i.Ecosystems = ["npm"]));
        var report = await CheckAsync(s);
        Assert.Contains(report.Warnings, w => w.Contains(s.LeftPad) && w.Contains("too large"));
        Assert.DoesNotContain(report.Items, i => i.Name == s.LeftPad);
    }

    [Fact]
    public async Task Only_maintainers_and_owners_can_configure_check_or_create()
    {
        var s = await NewAsync();
        foreach (var user in new[] { "bob", "dan" }) // developer, viewer
        {
            var repo = await RepoAsync(s, user);
            await using var scope = app.Scope(app.UserId(user));
            var bot = scope.Get<DependencyBotService>();
            await Assert.ThrowsAsync<ForbiddenException>(() => bot.GetAsync(repo));
            await Assert.ThrowsAsync<ForbiddenException>(() => bot.SaveAsync(repo, Input()));
            await Assert.ThrowsAsync<ForbiddenException>(() => bot.CheckNowAsync(repo));
            await Assert.ThrowsAsync<ForbiddenException>(() => bot.CreatePullRequestAsync(repo, null, app.UserId(user)));
        }
        await SaveAsync(s, Input(), "erin"); // maintainer
        Assert.NotEmpty((await CheckAsync(s, "erin")).Items);

        // The page is gated the same way.
        var bobClient = await app.CookieClientAsync("bob");
        var html = await (await bobClient.GetAsync($"/p/{s.Project.Key}/repos/{s.Repo.Slug}/settings/dependencies")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Outdated dependencies", html);
        var erinClient = await app.CookieClientAsync("erin");
        var erinHtml = await (await erinClient.GetAsync($"/p/{s.Project.Key}/repos/{s.Repo.Slug}/settings/dependencies")).Content.ReadAsStringAsync();
        Assert.Contains("Outdated dependencies", erinHtml);
        Assert.Contains("Check now", erinHtml);
    }

    [Fact]
    public async Task Settings_are_validated()
    {
        var s = await NewAsync();
        async Task Bad(Action<DependencyBotInput> tweak) => await Assert.ThrowsAsync<InvalidOperationException>(() => SaveAsync(s, Input(tweak)));
        await Bad(i => i.Ecosystems = []);
        await Bad(i => i.Ecosystems = ["cargo"]);
        await Bad(i => i.MaxOpenPullRequests = 0);
        await Bad(i => i.MaxOpenPullRequests = 51);
        await Bad(i => i.TargetBranch = "no-such-branch");
        await Bad(i => i.IgnoreList = "pkg@not a range");
        await Bad(i => i.Reviewers = "dan"); // a viewer can't review
        await Bad(i => i.Reviewers = "nobody-here");
        await Bad(i => i.Labels = string.Join(",", Enumerable.Range(0, 21).Select(n => "l" + n)));
        await SaveAsync(s, Input(i => { i.Enabled = true; i.Reviewers = "bob, erin"; i.TargetBranch = "main"; }));
        var repo = await RepoAsync(s, "carol");
        await using var scope = app.Scope(app.UserId("carol"));
        var saved = await scope.Get<DependencyBotService>().GetAsync(repo);
        Assert.Equal("bob, erin", saved.Reviewers);
        Assert.Null(saved.TargetBranch); // the default branch is stored as "default"
        Assert.NotNull(saved.NextRunAt); // enabling schedules the first run
        await SaveAsync(s, Input()); // switched off again: the scheduler test must not pick this repository up
        Assert.Null((await scope.Get<DependencyBotService>().GetAsync(repo)).NextRunAt);
    }

    [Fact]
    public async Task A_target_branch_other_than_the_default_is_the_base_of_the_update()
    {
        var s = await NewAsync();
        GitOps.CreateBranch(s.Path, "release", "main");
        GitOps.CommitFiles(s.Path, "release", new Dictionary<string, string?> { ["package.json"] = s.PackageJson.Replace("^1.1.0", "^1.1.3") }, "On release", new GitIdentity("T", "t@example.com"), DateTimeOffset.UtcNow);
        await SaveAsync(s, Input(i => { i.Ecosystems = ["npm"]; i.TargetBranch = "release"; }));
        var report = await CheckAsync(s);
        Assert.Equal("release", report.Branch);
        var item = Item(report, s.LeftPad);
        Assert.Equal(("1.1.3", "1.2.0"), (item.Current, item.Proposed));
        var number = Assert.Single((await CreateAsync(s, item.Key)).PullRequests);
        await using var db = app.Db();
        Assert.Equal("release", (await db.PullRequests.SingleAsync(p => p.RepositoryId == s.Repo.Id && p.Number == number)).TargetBranch);
    }

    [Fact]
    public async Task Scheduled_runs_only_happen_when_the_worker_and_the_repository_are_enabled_and_do_not_touch_the_network_otherwise()
    {
        var s = await NewAsync();
        var worker = app.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<DependencyBotWorker>().Single();
        var runner = app.Services.GetRequiredService<DependencyBotRunner>();

        // Disabled repository, even though its time has come: nothing runs.
        await SaveAsync(s, Input(i => { i.Enabled = false; i.Ecosystems = ["npm"]; }));
        await using (var db = app.Db())
        {
            db.DependencyBotSettings.Single(x => x.RepositoryId == s.Repo.Id).NextRunAt = DateTime.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
        }
        var requests = Registry.Count;
        Assert.Equal(0, await runner.RunDueAsync(DateTime.UtcNow, CancellationToken.None));
        Assert.Equal(requests, Registry.Count);

        // Enabled repository, but the server's worker switch (DependencyBot:Enabled=false) is off: still no network.
        await SaveAsync(s, Input(i => { i.Enabled = true; i.Ecosystems = ["npm"]; i.Schedule = DependencySchedule.Daily; i.MaxOpenPullRequests = 1; }));
        await using (var db = app.Db())
        {
            db.DependencyBotSettings.Single(x => x.RepositoryId == s.Repo.Id).NextRunAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        Assert.Equal(0, await worker.TickAsync(DateTime.UtcNow, CancellationToken.None));
        Assert.Equal(requests, Registry.Count);

        // The scheduler itself (what the worker calls when it is on): checks, opens one PR (limit 1), and plans the next run.
        var due = DateTime.UtcNow;
        Assert.Equal(1, await runner.RunDueAsync(due, CancellationToken.None));
        Assert.True(Registry.Count > requests);
        await using var check = app.Db();
        var settings = await check.DependencyBotSettings.AsNoTracking().SingleAsync(x => x.RepositoryId == s.Repo.Id);
        Assert.Equal("warnings", settings.LastStatus); // the open-PR limit left two updates for later
        Assert.InRange((settings.NextRunAt!.Value - DateTime.UtcNow).TotalHours, 23, 25);
        Assert.Equal(1, await check.PullRequests.CountAsync(p => p.RepositoryId == s.Repo.Id));
        var audit = await check.AuditEvents.SingleAsync(e => e.Action == "repository.dependency_pr_opened" && e.ProjectId == s.Project.Id);
        Assert.Equal(DependencyBotAccount.UserName, audit.ActorName);
        Assert.Contains("\"trigger\":\"schedule\"", audit.Details);
        // Not due again yet.
        Assert.Equal(0, await runner.RunDueAsync(due, CancellationToken.None));
    }

    [Fact]
    public async Task The_bot_account_is_a_system_account_that_cannot_sign_in()
    {
        var s = await NewAsync();
        await SaveAsync(s, Input(i => i.Ecosystems = ["npm"]));
        await CheckAsync(s);
        await CreateAsync(s, null);
        var bot = app.User(DependencyBotAccount.UserName);
        Assert.True(SystemAccounts.IsSystem(bot));
        Assert.Null(bot.PasswordHash);
        Assert.True(bot.LockoutEnd > DateTimeOffset.UtcNow.AddYears(100));
        Assert.Equal("ByteBench dependency bot", bot.DisplayName);
        await using var scope = app.Scope(app.UserId("carol"));
        Assert.DoesNotContain(await scope.Get<IssueService>().GetUsersAsync(), u => u.UserName == DependencyBotAccount.UserName);
        // Not a member of anything: it only opened pull requests through the system's say-so.
        await using var db = app.Db();
        Assert.False(await db.ProjectMembers.AnyAsync(m => m.UserId == bot.Id));
    }
}

public class DependencyBotStrictTests(DepBotStrictApp app) : IClassFixture<DepBotStrictApp>
{
    [Theory]
    [InlineData("http://127.0.0.1:9999/", false)]
    [InlineData("http://localhost/", false)]
    [InlineData("http://sub.localhost/x", false)]
    [InlineData("http://169.254.169.254/latest/", false)]
    [InlineData("http://10.0.0.5/", false)]
    [InlineData("http://[::1]/", false)]
    [InlineData("http://user:pw@registry.example/", false)]
    [InlineData("ftp://registry.example/", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("not a url", false)]
    [InlineData("https://registry.npmjs.org/", true)]
    public void The_guard_refuses_private_and_odd_registry_addresses(string url, bool ok) =>
        Assert.Equal(ok, app.Services.GetRequiredService<RegistryUrlGuard>().Validate(url) == null);

    [Fact]
    public async Task A_registry_on_a_private_address_is_never_contacted()
    {
        var project = await app.CreateProjectAsync("carol");
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        var path = app.Services.GetRequiredService<RepoStorage>().PathFor(project.Key, repo.Slug);
        GitOps.CommitFiles(path, "main", new Dictionary<string, string?> { ["package.json"] = "{\"dependencies\": {\"left-pad\": \"^1.1.0\"}}\n" }, "m", new GitIdentity("T", "t@example.com"), DateTimeOffset.UtcNow);
        app.Registry.NpmPackage("left-pad", ["1.1.0", "1.2.0"]);

        await using var scope = app.Scope(app.UserId("carol"));
        var r = (await scope.Get<RepositoryService>().GetRepositoryAsync(project.Key, repo.Slug))!;
        var bot = scope.Get<DependencyBotService>();
        var report = await bot.CheckNowAsync(r);

        Assert.Equal(0, app.Registry.Count); // not one request reached the fake registry
        Assert.Empty(report.Items);
        Assert.Contains(report.Warnings, w => w.Contains("left-pad") && w.Contains("local address"));
    }
}
