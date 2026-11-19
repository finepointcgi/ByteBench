using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Ci;
using ByteBench.Services.Pages;
using ByteBench.Services.Webhooks;
using ByteBench.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Tests;

/// <summary>Zip helpers shared by the Pages tests.</summary>
internal static class PagesZip
{
    public static string Make(string dir, params (string Name, string Content)[] files) =>
        Make(dir, null, files);

    public static string Make(string dir, Action<ZipArchive>? extra, params (string Name, string Content)[] files)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, content) in files)
            {
                var e = zip.CreateEntry(name);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(content);
            }
            extra?.Invoke(zip);
        }
        return path;
    }

    /// <summary>An entry that claims to be a Unix symlink (or another file type) through its mode bits.</summary>
    public static void Typed(ZipArchive zip, string name, int unixType, string content)
    {
        var e = zip.CreateEntry(name);
        e.ExternalAttributes = (unixType | 0x1FF) << 16;
        using var w = new StreamWriter(e.Open());
        w.Write(content);
    }
}

public class PagesUnitTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "bytebench-pages-unit", Guid.NewGuid().ToString("N"));
    private static readonly PagesLimits Limits = new(1024 * 1024, 100, 512 * 1024);

    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    private string Dest() => Path.Combine(dir, "out-" + Guid.NewGuid().ToString("N")[..6]);

    [Fact]
    public void Extracts_a_normal_site_with_folders()
    {
        var zip = PagesZip.Make(dir, ("index.html", "<h1>hi</h1>"), ("css/site.css", "body{}"), ("a/b/c.txt", "c"));
        var dest = Dest();
        var r = PagesExtractor.ExtractZip(zip, dest, Limits);
        Assert.Equal(3, r.FileCount);
        Assert.Equal("c", File.ReadAllText(Path.Combine(dest, "a", "b", "c.txt")));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("a\\..\\..\\evil.txt")]
    [InlineData("/etc/evil.txt")]
    [InlineData("C:/evil.txt")]
    [InlineData("a//b.txt")]
    [InlineData("./x.txt")]
    [InlineData("~/x.txt")]
    public void Zip_slip_and_unsafe_names_refuse_the_whole_archive(string name)
    {
        var zip = PagesZip.Make(dir, ("index.html", "ok"), (name, "pwned"));
        var dest = Dest();
        var ex = Assert.Throws<PagesException>(() => PagesExtractor.ExtractZip(zip, dest, Limits));
        Assert.Contains("Unsafe entry", ex.Message);
        Assert.False(File.Exists(Path.Combine(dir, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(dest)!, "evil.txt")));
    }

    [Fact]
    public void Symlink_entries_and_special_files_are_refused()
    {
        var link = PagesZip.Make(dir, z => PagesZip.Typed(z, "link", 0xA000, "/etc/passwd"), ("index.html", "ok"));
        var ex = Assert.Throws<PagesException>(() => PagesExtractor.ExtractZip(link, Dest(), Limits));
        Assert.Contains("symbolic link", ex.Message);

        var fifo = PagesZip.Make(dir, z => PagesZip.Typed(z, "pipe", 0x1000, ""), ("index.html", "ok"));
        Assert.Throws<PagesException>(() => PagesExtractor.ExtractZip(fifo, Dest(), Limits));

        var regular = PagesZip.Make(dir, z => PagesZip.Typed(z, "file.txt", 0x8000, "fine"));
        Assert.Equal(1, PagesExtractor.ExtractZip(regular, Dest(), Limits).FileCount);
    }

    [Fact]
    public void Duplicates_and_file_folder_clashes_are_refused()
    {
        Assert.Throws<PagesException>(() => PagesExtractor.ExtractZip(PagesZip.Make(dir, ("a.txt", "1"), ("A.TXT", "2")), Dest(), Limits));
        Assert.Throws<PagesException>(() => PagesExtractor.ExtractZip(PagesZip.Make(dir, ("a", "1"), ("a/b.txt", "2")), Dest(), Limits));
        Assert.Throws<PagesException>(() => PagesExtractor.ExtractZip(PagesZip.Make(dir, ("a/b.txt", "2"), ("a", "1")), Dest(), Limits));
    }

    [Fact]
    public void Size_and_count_limits_apply()
    {
        var big = PagesZip.Make(dir, ("big.bin", new string('x', 600 * 1024)));
        Assert.Contains("file limit", Assert.Throws<PagesException>(() => PagesExtractor.ExtractZip(big, Dest(), Limits)).Message);

        var total = PagesZip.Make(dir, ("1.bin", new string('x', 400 * 1024)), ("2.bin", new string('x', 400 * 1024)), ("3.bin", new string('x', 400 * 1024)));
        Assert.Contains("limit", Assert.Throws<PagesException>(() => PagesExtractor.ExtractZip(total, Dest(), Limits)).Message);

        var many = PagesZip.Make(dir, Enumerable.Range(0, 101).Select(i => ($"f{i}.txt", "x")).ToArray());
        Assert.Contains("more than 100 files", Assert.Throws<PagesException>(() => PagesExtractor.ExtractZip(many, Dest(), Limits)).Message);
    }

    [Fact]
    public void A_zip_that_understates_its_sizes_cannot_exceed_the_limit()
    {
        // Declared size is read from the central directory; patch it down to 1 byte so only the real byte count can catch it.
        var zip = PagesZip.Make(dir, ("lie.bin", new string('x', 300 * 1024)));
        var bytes = File.ReadAllBytes(zip);
        var cd = bytes.AsSpan().LastIndexOf(new byte[] { 0x50, 0x4B, 0x01, 0x02 });
        BitConverter.GetBytes(1).CopyTo(bytes, cd + 24); // uncompressed size
        File.WriteAllBytes(zip, bytes);
        var tight = new PagesLimits(100 * 1024, 10, 100 * 1024);
        // The reader never hands out more than the declared size, and what is written is counted either way: the limit holds.
        var dest = Dest();
        try { Assert.True(PagesExtractor.ExtractZip(zip, dest, tight).Bytes <= tight.MaxBytes); }
        catch (Exception ex) when (ex is PagesException or InvalidDataException) { }
        Assert.True(Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) <= tight.MaxBytes);
    }

    [Fact]
    public void Garbage_is_not_a_zip()
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "bad.zip");
        File.WriteAllText(path, "not a zip");
        Assert.Contains("not a valid zip", Assert.Throws<PagesException>(() => PagesExtractor.ExtractZip(path, Dest(), Limits)).Message);
    }

    [Fact]
    public void Workspace_folder_must_exist_and_not_be_a_link()
    {
        var ws = Path.Combine(dir, "ws");
        Directory.CreateDirectory(Path.Combine(ws, "public"));
        Assert.NotNull(PagesDeployer.ResolveFolder(ws, "public"));
        Assert.Null(PagesDeployer.ResolveFolder(ws, "nope"));
        var outside = Path.Combine(dir, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(ws, "linked"), outside);
        Assert.Null(PagesDeployer.ResolveFolder(ws, "linked"));
        Assert.Null(PagesDeployer.ResolveFolder(ws, "linked/sub"));
    }

    [Fact]
    public void Resolve_never_leaves_the_site_folder()
    {
        var root = Path.Combine(dir, "site");
        Directory.CreateDirectory(Path.Combine(root, "d"));
        File.WriteAllText(Path.Combine(root, "index.html"), "i");
        File.WriteAllText(Path.Combine(root, "d", "index.html"), "d");
        File.WriteAllText(Path.Combine(root, "page.html"), "p");
        File.WriteAllText(Path.Combine(dir, "secret.txt"), "s");
        Assert.Equal(Path.Combine(root, "index.html"), PagesMiddleware.Resolve(root, [], true, out _));
        Assert.Equal(Path.Combine(root, "page.html"), PagesMiddleware.Resolve(root, ["page"], false, out _));
        Assert.Null(PagesMiddleware.Resolve(root, ["d"], false, out var redirect));
        Assert.True(redirect);
        Assert.Equal(Path.Combine(root, "d", "index.html"), PagesMiddleware.Resolve(root, ["d"], true, out _));
        Assert.Null(PagesMiddleware.Resolve(root, ["..", "secret.txt"], false, out _));
        Assert.Null(PagesMiddleware.Resolve(root, ["d", "..", "..", "secret.txt"], false, out _));
        Assert.Null(PagesMiddleware.Resolve(root, ["..\\secret.txt"], false, out _));
        // A symlink inside the site is never served.
        File.CreateSymbolicLink(Path.Combine(root, "leak.txt"), Path.Combine(dir, "secret.txt"));
        Assert.Null(PagesMiddleware.Resolve(root, ["leak.txt"], false, out _));
    }

    [Fact]
    public void Review_slots_are_dns_safe_and_distinct()
    {
        Assert.Equal("main-x", PagesSlots.ForRef("main-x"));
        var a = PagesSlots.ForRef("feature/Login Page");
        var b = PagesSlots.ForRef("feature-login-page");
        Assert.True(PagesSlots.IsValid(a));
        Assert.NotEqual(a, b); // different branches never share a site
        Assert.StartsWith("feature-login-page-", a);
        Assert.True(PagesSlots.IsValid(PagesSlots.ForRef(new string('x', 200))));
        Assert.True(PagesSlots.IsValid(PagesSlots.ForRef("###")));
        Assert.False(PagesSlots.IsValid("-bad"));
        Assert.False(PagesSlots.IsValid("a.b"));
    }

    [Fact]
    public void Hosts_parse_to_sites()
    {
        var links = new PagesLinks(new TestOptions<PagesOptions>(new PagesOptions { BaseDomain = "Pages.Example.com" }), new ByteBench.Api.PublicBaseUrl(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()));
        Assert.Equal(new PagesTarget("WEB", "docs", ""), links.ParseHost("docs.web.pages.example.com"));
        Assert.Equal(new PagesTarget("WEB", "docs", "feat-x"), links.ParseHost("feat-x.docs.web.pages.example.com"));
        Assert.Null(links.ParseHost("web.pages.example.com")); // one label
        Assert.Null(links.ParseHost("a.b.c.d.pages.example.com"));
        Assert.Null(links.ParseHost("docs.web.example.com"));
        Assert.Null(links.ParseHost("evilpages.example.com"));
        Assert.Equal("docs.web.pages.example.com", links.HostFor("WEB", "docs", ""));
        Assert.Null(links.HostFor("WEB", "my_docs", "")); // underscore: path mode only
        Assert.Null(links.HostFor("WEB", "my.docs", ""));
    }

    private sealed class TestOptions<T>(T value) : Microsoft.Extensions.Options.IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

public class PagesCiKeywordTests
{
    private static CiPagesDefinition? PagesOf(string job, string name = "site")
    {
        var r = CiFile.Parse($"jobs:\n  {name}:\n    steps: [ {{ run: x }} ]\n{job}");
        Assert.True(r.Ok, r.Error);
        return r.Definition!.Jobs[0].Pages;
    }

    [Fact]
    public void A_job_named_pages_publishes_public_by_default()
    {
        var p = PagesOf("", "pages");
        Assert.Equal(new CiPagesDefinition("public", false, null), p);
    }

    [Fact]
    public void Other_names_publish_only_with_the_keyword()
    {
        Assert.Null(PagesOf(""));
        Assert.Equal("public", PagesOf("    pages: true")!.Path);
        Assert.Equal("dist", PagesOf("    pages: dist")!.Path);
        Assert.Equal("dist/web", PagesOf("    publish: dist/web/")!.Path);
        Assert.Null(PagesOf("    pages: false", "pages"));
        Assert.Null(PagesOf("    pages: false"));
    }

    [Fact]
    public void Mapping_form_sets_path_review_and_expiry()
    {
        var p = PagesOf("    pages: { path: _site, review: true, expire_in: 3d }")!;
        Assert.Equal("_site", p.Path);
        Assert.True(p.Review);
        Assert.Equal(TimeSpan.FromDays(3), p.ReviewExpireIn);
        Assert.Equal("public", PagesOf("    pages:\n      review: true")!.Path);
    }

    [Theory]
    [InlineData("    pages: { path: ../up }", "pages path")]
    [InlineData("    pages: { path: /abs }", "pages path")]
    [InlineData("    pages: { path: \"dist/*\" }", "not a pattern")]
    [InlineData("    pages: { review: maybe }", "true or false")]
    [InlineData("    pages: { expire_in: forever }", "expire_in")]
    [InlineData("    pages: { colour: red }", "unknown key")]
    [InlineData("    pages: true\n    publish: dist", "either 'pages' or 'publish'")]
    public void Bad_pages_settings_are_errors(string job, string message)
    {
        var r = CiFile.Parse($"jobs:\n  site:\n    steps: [ {{ run: x }} ]\n{job}");
        Assert.False(r.Ok);
        Assert.Contains(message, r.Error);
    }

    [Fact]
    public void Pages_settings_survive_the_job_extras_json()
    {
        var def = CiFile.Parse("jobs:\n  pages:\n    steps: [ { run: x } ]\n    pages: { review: true }").Definition!.Jobs[0];
        var parsed = CiJobExtras.Parse(CiJobExtras.Serialize(def));
        Assert.Equal(new CiPagesDefinition("public", true, null), parsed.Pages);
        Assert.Null(CiJobExtras.Parse(CiJobExtras.Serialize(CiFile.Parse("jobs:\n  b:\n    steps: [ { run: x } ]").Definition!.Jobs[0])).Pages);
    }

    [Fact]
    public void The_pages_deployed_webhook_is_subscribable()
    {
        Assert.True(WebhookEventNames.IsSubscribable(WebhookEventNames.PagesDeployed));
        Assert.Equal("pages.deployed", WebhookEventNames.PagesDeployed);
    }
}

public class PagesApp : TestApp
{
    public const string Domain = "pages.example.test";

    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["Pages:Root"] = Path.Combine(DataRoot, "pages");
        settings["Pages:BaseDomain"] = Domain;
        settings["Pages:KeepVersions"] = "3";
        settings["Pages:MaxFiles"] = "50";
        settings["Pages:MaxSiteBytes"] = (2 * 1024 * 1024).ToString();
        settings["Pages:MaxFileBytes"] = (1024 * 1024).ToString();
        settings["PublicBaseUrl"] = "http://bytebench.test";
    }
}

/// <summary>Deploying, serving, access control, versions and limits, through the real pipeline and HTTP.</summary>
public class PagesServiceTests(PagesApp app) : IClassFixture<PagesApp>, IDisposable
{
    private readonly string tmp = Path.Combine(Path.GetTempPath(), "bytebench-pages-tests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(tmp, true); } catch (IOException) { } }

    private static readonly SemaphoreSlim OutsiderLock = new(1);

    /// <summary>A signed-in user who is a member of nothing.</summary>
    private async Task<HttpClient> OutsiderAsync()
    {
        await OutsiderLock.WaitAsync();
        try { if (!app.Db().Users.Any(u => u.UserName == "pagesoutsider")) await app.CreateUserAsync("pagesoutsider"); }
        finally { OutsiderLock.Release(); }
        return await app.CookieClientAsync("pagesoutsider");
    }

    private PagesDeployer Deployer => (PagesDeployer)app.Services.GetService(typeof(PagesDeployer))!;

    private HttpClient Anon(bool cookies = false) => app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = cookies });

    private async Task<(Project Project, Repository Repo)> SetupAsync(PagesVisibility visibility = PagesVisibility.Members, bool create = true)
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("carol", ProjectRole.Maintainer), ("bob", ProjectRole.Developer), ("dan", ProjectRole.Viewer));
        var repo = await app.CreateRepoAsync(project.Id, "alice", "site");
        if (create)
        {
            await using var s = app.Scope(app.UserId("alice"));
            await s.Get<PagesService>().UpdateSettingsAsync(repo, new PagesSettingsInput(true, visibility, PagesNotFoundMode.Site404, true, 7));
        }
        return (project, repo);
    }

    private static readonly (string, string)[] SampleSite =
    [
        ("index.html", "<html><body><h1>Home v1</h1><script src=\"app.js\"></script></body></html>"),
        ("about/index.html", "<h1>About</h1>"),
        ("page.html", "<h1>Clean URL</h1>"),
        ("404.html", "<h1>Custom missing</h1>"),
        ("app.js", "console.log('0123456789');"),
        ("data.json", "{\"a\":1}"),
        ("image.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"),
    ];

    private async Task<PagesDeployment> DeployAsync(Repository repo, string slot = "", (string, string)[]? files = null, string branch = "main",
        TimeSpan? expire = null, int? user = null)
    {
        var zip = PagesZip.Make(tmp, files ?? SampleSite);
        return await Deployer.DeployZipAsync(new PagesDeployRequest(repo.Id, slot, branch, new string('a', 40), zip, 7, 8, user ?? app.UserId("alice"), expire));
    }

    private static string Path_(Project p, Repository r, string rest = "") => $"/pages/{p.Key}/{r.Slug}/{rest}";
    private string HostUrl(Project p, Repository r, string rest = "", string slot = "") =>
        $"http://{(slot.Length > 0 ? slot + "." : "")}{r.Slug}.{p.Key.ToLowerInvariant()}.{PagesApp.Domain}/{rest}";

    // ---------- serving ----------

    [Fact]
    public async Task Public_site_is_served_in_path_mode_sandboxed_with_hardened_headers()
    {
        var (p, r) = await SetupAsync(PagesVisibility.Public);
        await DeployAsync(r);
        using var c = Anon();

        var home = await c.GetAsync(Path_(p, r));
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Equal("text/html; charset=utf-8", home.Content.Headers.ContentType!.ToString());
        Assert.Contains("Home v1", await home.Content.ReadAsStringAsync());
        var csp = home.Headers.GetValues("Content-Security-Policy").Single();
        Assert.StartsWith("sandbox", csp);
        Assert.Contains("allow-scripts", csp);
        Assert.DoesNotContain("allow-same-origin", csp);
        Assert.Equal("nosniff", home.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.False(home.Headers.Contains("Set-Cookie"));
        Assert.False(home.Headers.Contains("X-Frame-Options")); // the app's headers don't apply; the page has its own set
        Assert.NotNull(home.Headers.ETag);

        Assert.Equal("text/javascript; charset=utf-8", (await c.GetAsync(Path_(p, r, "app.js"))).Content.Headers.ContentType!.ToString());
        Assert.Equal("application/json; charset=utf-8", (await c.GetAsync(Path_(p, r, "data.json"))).Content.Headers.ContentType!.ToString());
        var svg = await c.GetAsync(Path_(p, r, "image.svg"));
        Assert.StartsWith("sandbox", svg.Headers.GetValues("Content-Security-Policy").Single());

        // The app's own pages keep their own CSP, untouched by Pages.
        var login = await c.GetAsync("/account/login");
        var appCsp = login.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'self'", appCsp);
        Assert.DoesNotContain("sandbox", appCsp);
        Assert.Equal("DENY", login.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task Index_clean_urls_directories_head_etag_and_range()
    {
        var (p, r) = await SetupAsync(PagesVisibility.Public);
        await DeployAsync(r);
        using var c = Anon();

        Assert.Contains("About", await c.GetStringAsync(Path_(p, r, "about/")));
        var dir = await c.GetAsync(Path_(p, r, "about"));
        Assert.Equal(HttpStatusCode.MovedPermanently, dir.StatusCode);
        Assert.EndsWith("/about/", dir.Headers.Location!.ToString());
        Assert.Contains("Clean URL", await c.GetStringAsync(Path_(p, r, "page")));
        var noSlash = await c.GetAsync($"/pages/{p.Key}/{r.Slug}");
        Assert.Equal(HttpStatusCode.MovedPermanently, noSlash.StatusCode);

        var first = await c.GetAsync(Path_(p, r, "app.js"));
        var etag = first.Headers.ETag!;
        var req = new HttpRequestMessage(HttpMethod.Get, Path_(p, r, "app.js"));
        req.Headers.IfNoneMatch.Add(etag);
        Assert.Equal(HttpStatusCode.NotModified, (await c.SendAsync(req)).StatusCode);

        var range = new HttpRequestMessage(HttpMethod.Get, Path_(p, r, "app.js"));
        range.Headers.Range = new RangeHeaderValue(0, 6);
        var partial = await c.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal("console", await partial.Content.ReadAsStringAsync());

        var head = await c.SendAsync(new HttpRequestMessage(HttpMethod.Head, Path_(p, r, "app.js")));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await c.PostAsync(Path_(p, r), new StringContent("x"))).StatusCode);
    }

    [Fact]
    public async Task Missing_files_follow_the_not_found_setting()
    {
        var (p, r) = await SetupAsync(PagesVisibility.Public);
        await DeployAsync(r);
        using var c = Anon();
        async Task Set(PagesNotFoundMode m)
        {
            await using var s = app.Scope(app.UserId("alice"));
            await s.Get<PagesService>().UpdateSettingsAsync(r, new PagesSettingsInput(true, PagesVisibility.Public, m, true, 7));
        }

        var custom = await c.GetAsync(Path_(p, r, "nope"));
        Assert.Equal(HttpStatusCode.NotFound, custom.StatusCode);
        Assert.Contains("Custom missing", await custom.Content.ReadAsStringAsync());

        await Set(PagesNotFoundMode.Plain);
        var plain = await c.GetAsync(Path_(p, r, "nope"));
        Assert.Equal(HttpStatusCode.NotFound, plain.StatusCode);
        Assert.DoesNotContain("Custom missing", await plain.Content.ReadAsStringAsync());

        await Set(PagesNotFoundMode.SpaFallback);
        var spa = await c.GetAsync(Path_(p, r, "app/route/1"));
        Assert.Equal(HttpStatusCode.OK, spa.StatusCode);
        Assert.Contains("Home v1", await spa.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(Path_(p, r, "missing.png"))).StatusCode); // has an extension: a real miss
    }

    [Theory]
    [InlineData("..%2f..%2fappsettings.json")]
    [InlineData("%2e%2e/%2e%2e/appsettings.json")]
    [InlineData("..%5c..%5cappsettings.json")]
    [InlineData("a/..%2f..%2f..%2fx")]
    public async Task Path_traversal_never_leaves_the_site(string rest)
    {
        var (p, r) = await SetupAsync(PagesVisibility.Public);
        await DeployAsync(r);
        using var c = Anon();
        var resp = await c.GetAsync(Path_(p, r, rest));
        Assert.True(resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.MovedPermanently, resp.StatusCode.ToString());
        Assert.DoesNotContain("ConnectionStrings", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Site_on_its_own_host_has_no_sandbox_and_review_sites_get_their_own_host()
    {
        var (p, r) = await SetupAsync(PagesVisibility.Public);
        await DeployAsync(r);
        await DeployAsync(r, "feat-x", [("index.html", "<h1>Review of feat-x</h1>")], "feat/x", TimeSpan.FromDays(2));
        using var c = Anon();

        var host = await c.GetAsync(HostUrl(p, r));
        Assert.Equal(HttpStatusCode.OK, host.StatusCode);
        Assert.Contains("Home v1", await host.Content.ReadAsStringAsync());
        Assert.False(host.Headers.Contains("Content-Security-Policy"));
        Assert.Equal("nosniff", host.Headers.GetValues("X-Content-Type-Options").Single());

        Assert.Contains("Review of feat-x", await c.GetStringAsync(HostUrl(p, r, "", "feat-x")));
        Assert.Contains("Review of feat-x", await c.GetStringAsync(Path_(p, r, "-/r/feat-x/")));
        Assert.Contains("Home v1", await c.GetStringAsync(Path_(p, r))); // the main site is a different slot
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(HostUrl(p, r, "", "other"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"http://nosuch.{p.Key.ToLowerInvariant()}.{PagesApp.Domain}/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"http://x.{PagesApp.Domain}/")).StatusCode);
    }

    [Fact]
    public async Task Disabled_and_unknown_sites_answer_404()
    {
        var (p, r) = await SetupAsync(PagesVisibility.Public);
        await DeployAsync(r);
        using var c = Anon();
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/pages/{p.Key}/nope/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/pages/ZZZ9/code/")).StatusCode);
        await using (var s = app.Scope(app.UserId("alice")))
            await s.Get<PagesService>().UpdateSettingsAsync(r, new PagesSettingsInput(false, PagesVisibility.Public, PagesNotFoundMode.Site404, true, 7));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(Path_(p, r))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(HostUrl(p, r))).StatusCode);
    }

    // ---------- access control ----------

    [Fact]
    public async Task Members_only_path_mode_needs_a_project_member_and_hands_out_a_token_url()
    {
        var (p, r) = await SetupAsync();
        await DeployAsync(r);
        var html = new MediaTypeWithQualityHeaderValue("text/html");

        // Anonymous: page loads go to sign-in, other requests get 401; nothing of the site leaks.
        using (var anon = Anon())
        {
            var req = new HttpRequestMessage(HttpMethod.Get, Path_(p, r));
            req.Headers.Accept.Add(html);
            var login = await anon.SendAsync(req);
            Assert.Equal(HttpStatusCode.Found, login.StatusCode);
            Assert.StartsWith("/account/login?returnUrl=", login.Headers.Location!.ToString());
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(Path_(p, r, "app.js"))).StatusCode);
        }

        // A signed-in user who can't see the project gets a plain 404 (not a hint that the site exists).
        using (var outsider = await OutsiderAsync())
        {
            var req = new HttpRequestMessage(HttpMethod.Get, Path_(p, r));
            req.Headers.Accept.Add(html);
            Assert.Equal(HttpStatusCode.NotFound, (await outsider.SendAsync(req)).StatusCode);
        }

        // A member's page load is redirected to a tokenized URL, which works with no cookie at all (like a sandboxed page's sub-requests).
        string tokenUrl;
        using (var carol = await app.CookieClientAsync("carol"))
        {
            var req = new HttpRequestMessage(HttpMethod.Get, Path_(p, r, "about/?x=1"));
            req.Headers.Accept.Add(html);
            var redirect = await carol.SendAsync(req);
            Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
            Assert.Equal("no-referrer", redirect.Headers.GetValues("Referrer-Policy").Single());
            tokenUrl = redirect.Headers.Location!.ToString();
            Assert.Contains("/-/t/", tokenUrl);
            Assert.EndsWith("/about/?x=1", tokenUrl);
        }
        using (var bare = Anon())
        {
            var page = await bare.GetAsync(tokenUrl);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains("About", await page.Content.ReadAsStringAsync());
            Assert.True(page.Headers.CacheControl!.Private && page.Headers.CacheControl.NoCache);
            Assert.Equal("no-referrer", page.Headers.GetValues("Referrer-Policy").Single());
            Assert.StartsWith("sandbox", page.Headers.GetValues("Content-Security-Policy").Single());
            var asset = tokenUrl[..tokenUrl.IndexOf("about/")] + "app.js"; // a relative link from the page resolves under the token
            Assert.Equal(HttpStatusCode.OK, (await bare.GetAsync(asset)).StatusCode);

            // A tampered token, or the token with its last characters changed, is no better than none.
            var broken = tokenUrl.Replace("/-/t/", "/-/t/X");
            var denied = new HttpRequestMessage(HttpMethod.Get, broken);
            denied.Headers.Accept.Add(html);
            Assert.Equal(HttpStatusCode.Found, (await bare.SendAsync(denied)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await bare.GetAsync(broken.Replace("about/", "app.js"))).StatusCode);
        }
    }

    [Fact]
    public async Task A_token_stops_working_when_the_member_is_removed_and_never_opens_another_repository()
    {
        var (p, r) = await SetupAsync();
        var (p2, r2) = await SetupAsync();
        await DeployAsync(r);
        await DeployAsync(r2);
        var tokens = (PagesAccessTokens)app.Services.GetService(typeof(PagesAccessTokens))!;
        var dan = app.UserId("dan");
        var token = tokens.Create(PagesTokenKind.PathLink, dan, r.Id, TimeSpan.FromMinutes(5));
        using var c = Anon();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(Path_(p, r, $"-/t/{token}/"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(Path_(p2, r2, $"-/t/{token}/app.js"))).StatusCode); // bound to its repository
        // A token of another kind (a host cookie) can't be used as a path link.
        var cookie = tokens.Create(PagesTokenKind.HostCookie, dan, r.Id, TimeSpan.FromMinutes(5));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(Path_(p, r, $"-/t/{cookie}/app.js"))).StatusCode);
        // Expired.
        var expired = tokens.Create(PagesTokenKind.PathLink, dan, r.Id, TimeSpan.FromMilliseconds(-1000));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(Path_(p, r, $"-/t/{expired}/app.js"))).StatusCode);

        await using (var s = app.Scope(app.UserId("alice"))) await s.Get<MemberService>().RemoveMemberAsync(p.Id, dan);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(Path_(p, r, $"-/t/{token}/"))).StatusCode);
    }

    [Fact]
    public async Task Members_only_dedicated_host_signs_in_through_bytebench_and_gets_its_own_cookie()
    {
        var (p, r) = await SetupAsync();
        await DeployAsync(r);
        var html = new MediaTypeWithQualityHeaderValue("text/html");
        var target = HostUrl(p, r, "about/");

        using var anon = Anon(cookies: true);
        var req = new HttpRequestMessage(HttpMethod.Get, target);
        req.Headers.Accept.Add(html);
        var challenge = await anon.SendAsync(req);
        Assert.Equal(HttpStatusCode.Found, challenge.StatusCode);
        var auth = challenge.Headers.Location!;
        Assert.Equal("http://bytebench.test/pages/-/auth", auth.GetLeftPart(UriPartial.Path));
        Assert.Contains(Uri.EscapeDataString(target), auth.Query);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(HostUrl(p, r, "app.js"))).StatusCode);

        // Signed in to ByteBench: the auth endpoint hands a code to the pages host...
        using var carol = await app.CookieClientAsync("carol");
        var hand = await carol.GetAsync(auth.PathAndQuery);
        Assert.Equal(HttpStatusCode.Found, hand.StatusCode);
        var login = hand.Headers.Location!;
        Assert.Equal($"{r.Slug}.{p.Key.ToLowerInvariant()}.{PagesApp.Domain}", login.Host);
        Assert.Equal("/_gb/pages-login", login.AbsolutePath);

        // ...which trades it for a host-only, HttpOnly cookie and lands on the page.
        var set = await anon.GetAsync(login);
        Assert.Equal(HttpStatusCode.Found, set.StatusCode);
        var setCookie = set.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith(PagesMiddleware.CookieName + "=", setCookie);
        Assert.Contains("httponly", setCookie.ToLowerInvariant());
        Assert.DoesNotContain("domain=", setCookie.ToLowerInvariant());
        Assert.Equal("/about/", set.Headers.Location!.ToString());
        var page = await anon.GetAsync(target);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("About", await page.Content.ReadAsStringAsync());
        Assert.False(page.Headers.Contains("Content-Security-Policy"));

        // The code can't be turned into a cookie for another site, nor forged; open redirects are ignored.
        var (p2, r2) = await SetupAsync();
        await DeployAsync(r2);
        using var other = Anon();
        var wrongHost = HostUrl(p2, r2, "_gb/pages-login") + login.Query;
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(wrongHost)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(HostUrl(p, r, "_gb/pages-login?code=abc&next=/"))).StatusCode);
        var evil = await other.GetAsync(login.ToString().Replace("next=%2Fabout%2F", "next=%2F%2Fevil.example"));
        Assert.True(evil.StatusCode == HttpStatusCode.Found && evil.Headers.Location!.ToString() == "/");

        // A user who can't see the project is not given a code.
        using var erin = await OutsiderAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await erin.GetAsync(auth.PathAndQuery)).StatusCode);
        // Only addresses under the pages domain are accepted.
        Assert.Equal(HttpStatusCode.BadRequest, (await carol.GetAsync("/pages/-/auth?return=" + Uri.EscapeDataString("http://evil.example/"))).StatusCode);
    }

    [Fact]
    public async Task Making_a_site_public_needs_an_owner_and_changing_settings_a_maintainer()
    {
        var (p, r) = await SetupAsync();
        PagesSettingsInput Input(PagesVisibility v) => new(true, v, PagesNotFoundMode.Site404, true, 7);
        await using (var bob = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<ForbiddenException>(() => bob.Get<PagesService>().UpdateSettingsAsync(r, Input(PagesVisibility.Members)));
        await using (var carol = app.Scope(app.UserId("carol")))
        {
            await carol.Get<PagesService>().UpdateSettingsAsync(r, new PagesSettingsInput(true, PagesVisibility.Members, PagesNotFoundMode.SpaFallback, false, 3));
            await Assert.ThrowsAsync<ForbiddenException>(() => carol.Get<PagesService>().UpdateSettingsAsync(r, Input(PagesVisibility.Public)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => carol.Get<PagesService>().UpdateSettingsAsync(r, new PagesSettingsInput(true, PagesVisibility.Members, PagesNotFoundMode.Site404, true, 0)));
        }
        await using (var alice = app.Scope(app.UserId("alice")))
            await alice.Get<PagesService>().UpdateSettingsAsync(r, Input(PagesVisibility.Public));
        await using var db = app.Db();
        Assert.Contains(await db.AuditEvents.Where(a => a.ProjectId == p.Id).Select(a => a.Action).ToListAsync(), a => a == "pages.settings_updated");
        // A viewer can see the site's status but can't manage it.
        await using var dan = app.Scope(app.UserId("dan"));
        Assert.NotNull(await dan.Get<PagesService>().GetAsync(r));
        await Assert.ThrowsAsync<ForbiddenException>(() => dan.Get<PagesService>().DeleteContentAsync(r));
    }

    // ---------- versions, rollback, limits ----------

    [Fact]
    public async Task Deployments_are_versions_that_switch_atomically_and_roll_back()
    {
        var (p, r) = await SetupAsync(PagesVisibility.Public);
        var v1 = await DeployAsync(r, files: [("index.html", "<h1>one</h1>")]);
        var v2 = await DeployAsync(r, files: [("index.html", "<h1>two</h1>")]);
        Assert.Equal([1, 2], new[] { v1.Version, v2.Version });
        using var c = Anon();
        Assert.Contains("two", await c.GetStringAsync(Path_(p, r)));
        Assert.NotEqual(v1.Directory, v2.Directory);

        await using (var carol = app.Scope(app.UserId("carol")))
        {
            await carol.Get<PagesService>().RollbackAsync(r, v1.Id);
            var info = await carol.Get<PagesService>().GetAsync(r);
            Assert.Equal(v1.Id, info.Current!.Id);
            Assert.True(info.Current.IsRollback);
            Assert.Single(info.Deployments, d => d.IsCurrent);
            await Assert.ThrowsAsync<InvalidOperationException>(() => carol.Get<PagesService>().RollbackAsync(r, v1.Id)); // already live
        }
        Assert.Contains("one", await c.GetStringAsync(Path_(p, r)));

        // A new deployment after a rollback gets the next version number and becomes live.
        var v3 = await DeployAsync(r, files: [("index.html", "<h1>three</h1>")]);
        Assert.Equal(3, v3.Version);
        Assert.Contains("three", await c.GetStringAsync(Path_(p, r)));

        await using (var bob = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<ForbiddenException>(() => bob.Get<PagesService>().RollbackAsync(r, v1.Id));
        await using var db = app.Db();
        var actions = await db.AuditEvents.Where(a => a.ProjectId == p.Id).Select(a => a.Action).ToListAsync();
        Assert.Contains("pages.deployed", actions);
        Assert.Contains("pages.rolled_back", actions);
    }

    [Fact]
    public async Task Old_versions_are_pruned_and_their_files_deleted()
    {
        var (p, r) = await SetupAsync(PagesVisibility.Public);
        var all = new List<PagesDeployment>();
        for (var i = 1; i <= 5; i++) all.Add(await DeployAsync(r, files: [("index.html", $"<h1>v{i}</h1>")]));
        await using (var s = app.Scope(app.UserId("alice")))
        {
            var info = await s.Get<PagesService>().GetAsync(r);
            Assert.Equal([5, 4, 3], info.Deployments.Select(d => d.Version).ToArray()); // Pages:KeepVersions = 3
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<PagesService>().RollbackAsync(r, all[0].Id)); // pruned
        }
        var storage = (PagesStorage)app.Services.GetService(typeof(PagesStorage))!;
        Assert.False(Directory.Exists(storage.DeploymentPath(r.Id, all[0].Directory)));
        Assert.True(Directory.Exists(storage.DeploymentPath(r.Id, all[4].Directory)));
    }

    [Fact]
    public async Task A_refused_deployment_changes_nothing_and_leaves_no_files_behind()
    {
        var (p, r) = await SetupAsync(PagesVisibility.Public);
        var good = await DeployAsync(r, files: [("index.html", "<h1>good</h1>")]);
        var storage = (PagesStorage)app.Services.GetService(typeof(PagesStorage))!;

        foreach (var bad in new[]
        {
            PagesZip.Make(tmp, ("index.html", "x"), ("../escape.txt", "x")),
            PagesZip.Make(tmp, z => PagesZip.Typed(z, "ln", 0xA000, "/etc/passwd"), ("index.html", "x")),
            PagesZip.Make(tmp, ("big.bin", new string('x', 1024 * 1024 + 10))),                       // over Pages:MaxFileBytes
            PagesZip.Make(tmp, Enumerable.Range(0, 51).Select(i => ($"f{i}.html", "x")).ToArray()),  // over Pages:MaxFiles
            PagesZip.Make(tmp, Enumerable.Range(0, 3).Select(i => ($"b{i}.bin", new string('x', 900 * 1024))).ToArray()), // over Pages:MaxSiteBytes
            PagesZip.Make(tmp),                                                                      // empty
        })
            await Assert.ThrowsAsync<PagesException>(() => Deployer.DeployZipAsync(new PagesDeployRequest(r.Id, "", "main", "b", bad)));

        using var c = Anon();
        Assert.Contains("good", await c.GetStringAsync(Path_(p, r)));
        await using var db = app.Db();
        Assert.Equal(1, await db.PagesDeployments.CountAsync(d => d.Site.RepositoryId == r.Id));
        var left = Directory.GetFileSystemEntries(storage.RepositoryDirectory(r.Id)).Select(Path.GetFileName).ToList();
        Assert.Equal([good.Directory], left);
        Assert.False(File.Exists(Path.Combine(storage.Root, "escape.txt")));
        Assert.False(File.Exists(Path.Combine(storage.RepositoryDirectory(r.Id), "escape.txt")));
    }

    [Fact]
    public async Task Delete_removes_the_content_but_keeps_the_settings_and_expired_review_sites_are_cleaned()
    {
        var (p, r) = await SetupAsync(PagesVisibility.Public);
        await DeployAsync(r);
        var review = await DeployAsync(r, "old-branch", [("index.html", "<h1>old</h1>")], "old/branch", TimeSpan.FromDays(1));
        await using (var db = app.Db())
            await db.PagesDeployments.Where(d => d.Id == review.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.ExpiresAt, DateTime.UtcNow.AddMinutes(-5)));
        using var c = Anon();
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(Path_(p, r, "-/r/old-branch/"))).StatusCode); // expired: gone at once
        await Deployer.CleanupAsync(CancellationToken.None);
        await using (var db = app.Db())
            Assert.False(await db.PagesDeployments.AnyAsync(d => d.Slot == "old-branch" && d.Site.RepositoryId == r.Id));

        await using (var carol = app.Scope(app.UserId("carol")))
            await carol.Get<PagesService>().DeleteContentAsync(r);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(Path_(p, r))).StatusCode);
        await using var db2 = app.Db();
        var site = await db2.PagesSites.SingleAsync(s => s.RepositoryId == r.Id);
        Assert.Equal(PagesVisibility.Public, site.Visibility);
        Assert.Equal(0, await db2.PagesDeployments.CountAsync(d => d.SiteId == site.Id));
    }

    // ---------- from CI ----------

    private string Workspace(params (string Name, string Content)[] files)
    {
        var ws = Path.Combine(tmp, "ws-" + Guid.NewGuid().ToString("N")[..6]);
        foreach (var (name, content) in files)
        {
            var f = Path.Combine(ws, name);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, content);
        }
        Directory.CreateDirectory(ws);
        return ws;
    }

    private static Pipeline Pipe(Repository r, string branch = "main", bool fork = false, int? pr = null) => new()
    {
        Id = 41, RepositoryId = r.Id, Ref = branch, CommitSha = new string('c', 40), IsForkPullRequest = fork, PullRequestId = pr, TriggeredById = 1,
    };

    private static readonly PipelineJob Job = new() { Id = 5, Name = "pages", Image = "alpine" };

    [Fact]
    public async Task A_passing_pages_job_on_the_default_branch_publishes_the_main_site_and_raises_events()
    {
        var (p, r) = await SetupAsync(create: false);
        var ws = Workspace(("public/index.html", "<h1>from ci</h1>"), ("public/css/a.css", "a{}"), ("README.md", "not published"));
        var (ok, notes) = await Deployer.DeployFromWorkspaceAsync(Pipe(r), Job, ws, new CiPagesDefinition("public", false, null));
        Assert.True(ok, string.Join("\n", notes));
        Assert.Contains(notes, n => n.Contains("published version 1"));

        await using var db = app.Db();
        var site = await db.PagesSites.SingleAsync(s => s.RepositoryId == r.Id); // created by the first deployment, members-only by default
        Assert.Equal(PagesVisibility.Members, site.Visibility);
        var d = await db.PagesDeployments.SingleAsync(x => x.SiteId == site.Id);
        Assert.Equal((2, "main", 41, 5), (d.FileCount, d.Ref, d.PipelineId!.Value, d.JobId!.Value));
        Assert.True(d.IsCurrent);
        Assert.Contains(await db.AuditEvents.Where(a => a.ProjectId == p.Id).Select(a => a.Action).ToListAsync(), a => a == "pages.deployed");
        var storage = (PagesStorage)app.Services.GetService(typeof(PagesStorage))!;
        Assert.False(File.Exists(Path.Combine(storage.DeploymentPath(r.Id, d.Directory), "README.md"))); // only the folder is published
        Assert.True(File.Exists(Path.Combine(storage.DeploymentPath(r.Id, d.Directory), "css", "a.css")));
    }

    [Fact]
    public async Task Other_branches_publish_only_as_review_sites_and_never_for_fork_pull_requests()
    {
        var (p, r) = await SetupAsync();
        var ws = Workspace(("public/index.html", "<h1>x</h1>"));
        var plain = new CiPagesDefinition("public", false, null);
        var review = new CiPagesDefinition("public", true, null);

        var (ok, notes) = await Deployer.DeployFromWorkspaceAsync(Pipe(r, "feature/login"), Job, ws, plain);
        Assert.True(ok);
        Assert.Contains(notes, n => n.Contains("not published") && n.Contains("review: true"));

        (ok, notes) = await Deployer.DeployFromWorkspaceAsync(Pipe(r, "feature/login"), Job, ws, review);
        Assert.True(ok, string.Join("\n", notes));
        await using (var db = app.Db())
        {
            var d = await db.PagesDeployments.SingleAsync(x => x.Site.RepositoryId == r.Id);
            Assert.Equal(PagesSlots.ForRef("feature/login"), d.Slot);
            Assert.NotNull(d.ExpiresAt); // site default: 7 days
            Assert.InRange(d.ExpiresAt!.Value, DateTime.UtcNow.AddDays(6.9), DateTime.UtcNow.AddDays(7.1));
        }

        (ok, notes) = await Deployer.DeployFromWorkspaceAsync(Pipe(r, "main", fork: true), Job, ws, plain);
        Assert.True(ok);
        Assert.Contains(notes, n => n.Contains("fork pull requests"));
        await using (var s = app.Scope(app.UserId("alice")))
            await s.Get<PagesService>().UpdateSettingsAsync(r, new PagesSettingsInput(true, PagesVisibility.Members, PagesNotFoundMode.Site404, false, 7));
        (ok, notes) = await Deployer.DeployFromWorkspaceAsync(Pipe(r, "feature/other"), Job, ws, review);
        Assert.True(ok);
        Assert.Contains(notes, n => n.Contains("review sites are off"));
        await using var db2 = app.Db();
        Assert.Equal(1, await db2.PagesDeployments.CountAsync(x => x.Site.RepositoryId == r.Id));
    }

    [Fact]
    public async Task A_disabled_site_or_missing_folder_or_oversized_site_is_handled()
    {
        var (p, r) = await SetupAsync();
        var def = new CiPagesDefinition("public", false, null);
        var (ok, notes) = await Deployer.DeployFromWorkspaceAsync(Pipe(r), Job, Workspace(("other/index.html", "x")), def);
        Assert.False(ok);
        Assert.Contains(notes, n => n.Contains("was not found"));

        (ok, notes) = await Deployer.DeployFromWorkspaceAsync(Pipe(r), Job, Workspace(("public/big.bin", new string('x', 1024 * 1024 + 5))), def);
        Assert.False(ok);
        Assert.Contains(notes, n => n.Contains("not published"));

        var empty = Workspace();
        Directory.CreateDirectory(Path.Combine(empty, "public"));
        (ok, notes) = await Deployer.DeployFromWorkspaceAsync(Pipe(r), Job, empty, def);
        Assert.False(ok);

        await using (var s = app.Scope(app.UserId("alice")))
            await s.Get<PagesService>().UpdateSettingsAsync(r, new PagesSettingsInput(false, PagesVisibility.Members, PagesNotFoundMode.Site404, true, 7));
        (ok, notes) = await Deployer.DeployFromWorkspaceAsync(Pipe(r), Job, Workspace(("public/index.html", "x")), def);
        Assert.True(ok); // the job isn't failed by a disabled site
        Assert.Contains(notes, n => n.Contains("disabled"));
        await using var db = app.Db();
        Assert.Equal(0, await db.PagesDeployments.CountAsync(x => x.Site.RepositoryId == r.Id));
    }

    [Fact]
    public async Task Symbolic_links_in_the_published_folder_are_never_published()
    {
        var (p, r) = await SetupAsync(PagesVisibility.Public);
        var secret = Path.Combine(tmp, "host-secret.txt");
        Directory.CreateDirectory(tmp);
        File.WriteAllText(secret, "TOP SECRET");
        var ws = Workspace(("public/index.html", "<h1>ok</h1>"));
        File.CreateSymbolicLink(Path.Combine(ws, "public", "passwd.txt"), secret);
        Directory.CreateSymbolicLink(Path.Combine(ws, "public", "dir"), tmp);

        var (ok, notes) = await Deployer.DeployFromWorkspaceAsync(Pipe(r), Job, ws, new CiPagesDefinition("public", false, null));
        Assert.True(ok, string.Join("\n", notes));
        using var c = Anon();
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(Path_(p, r, "passwd.txt"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(Path_(p, r, "dir/host-secret.txt"))).StatusCode);
        Assert.DoesNotContain("TOP SECRET", await (await c.GetAsync(Path_(p, r, "passwd.txt"))).Content.ReadAsStringAsync());

        // And a publish folder that is itself a link is refused.
        var ws2 = Workspace();
        Directory.CreateSymbolicLink(Path.Combine(ws2, "public"), tmp);
        (ok, _) = await Deployer.DeployFromWorkspaceAsync(Pipe(r), Job, ws2, new CiPagesDefinition("public", false, null));
        Assert.False(ok);
    }
}
