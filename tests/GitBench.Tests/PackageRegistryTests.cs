using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GitBench.Data;
using GitBench.Services;
using GitBench.Services.Ci;
using GitBench.Services.Packages;
using GitBench.Services.Webhooks;
using GitBench.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GitBench.Tests;

/// <summary>The test app with the registry's blob store inside its data folder.</summary>
public class PackageTestApp : TestApp
{
    public string PackageRoot => Path.Combine(DataRoot, "packages");

    protected override void AddSettings(IDictionary<string, string?> settings) => settings["Packages:Root"] = PackageRoot;
}

/// <summary>
/// The package and container registry at the protocol level (HttpClient against the app, no docker/dotnet/npm CLIs):
/// NuGet push/restore/search/delete, npm publish/install metadata/dist-tags/unpublish, OCI blob and manifest push/pull,
/// auth and permission failures, CI job tokens, the cleanup policy, audit events and webhooks.
/// </summary>
public class PackageRegistryTests(PackageTestApp app) : IClassFixture<PackageTestApp>
{
    // ---------------------------------------------------------------- helpers

    /// <summary>carol owns it; bob is a Developer, dan a Viewer (and alice is a site admin).</summary>
    private Task<Project> ProjectAsync(ProjectVisibility visibility = ProjectVisibility.Private) =>
        app.CreateProjectAsync("carol", visibility, ("bob", ProjectRole.Developer), ("dan", ProjectRole.Viewer));

    private HttpClient Client() => app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    private static AuthenticationHeaderValue Basic(string user, string secret) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{secret}")));

    private async Task<HttpClient> BasicClientAsync(string user, TokenScopes scopes = TokenScopes.ReadRepository | TokenScopes.WriteRepository)
    {
        var c = Client();
        c.DefaultRequestHeaders.Authorization = Basic(user, await app.CreateTokenAsync(user, scopes));
        return c;
    }

    private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static byte[] Nupkg(string id, string version, string description = "A test package", string? dependency = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var deps = dependency == null ? "" :
                $"<dependencies><group targetFramework=\"net8.0\"><dependency id=\"{dependency}\" version=\"1.0.0\" /></group></dependencies>";
            using (var w = new StreamWriter(zip.CreateEntry($"{id}.nuspec").Open()))
                w.Write($"""
                    <?xml version="1.0" encoding="utf-8"?>
                    <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                      <metadata><id>{id}</id><version>{version}</version><authors>Carol, Bob</authors><description>{description}</description>{deps}</metadata>
                    </package>
                    """);
            using (var w = new StreamWriter(zip.CreateEntry("lib/net8.0/readme.txt").Open())) w.Write("hello " + Guid.NewGuid());
        }
        return ms.ToArray();
    }

    /// <summary><c>dotnet nuget push</c>: a multipart PUT with the token in X-NuGet-ApiKey.</summary>
    private async Task<HttpResponseMessage> NuGetPushAsync(string key, byte[] nupkg, string? apiKey)
    {
        using var c = Client();
        var content = new MultipartFormDataContent { { new ByteArrayContent(nupkg), "package", "package.nupkg" } };
        using var req = new HttpRequestMessage(HttpMethod.Put, $"/packages/{key}/nuget") { Content = content };
        if (apiKey != null) req.Headers.Add("X-NuGet-ApiKey", apiKey);
        return await c.SendAsync(req);
    }

    private static JsonObject NpmPublishBody(string name, string version, byte[] tarball, string? shasum = null, string? tag = "latest")
    {
        var sha1 = shasum ?? Convert.ToHexStringLower(SHA1.HashData(tarball));
        var integrity = "sha512-" + Convert.ToBase64String(SHA512.HashData(tarball));
        var file = $"{(name.Contains('/') ? name[(name.IndexOf('/') + 1)..] : name)}-{version}.tgz";
        return new JsonObject
        {
            ["_id"] = name,
            ["name"] = name,
            ["dist-tags"] = tag == null ? new JsonObject() : new JsonObject { [tag] = version },
            ["versions"] = new JsonObject
            {
                [version] = new JsonObject
                {
                    ["name"] = name, ["version"] = version, ["description"] = "npm test package",
                    ["dependencies"] = new JsonObject { ["left-pad"] = "^1.3.0" },
                    ["dist"] = new JsonObject { ["shasum"] = sha1, ["integrity"] = integrity, ["tarball"] = $"http://x/{name}/-/{file}" },
                },
            },
            ["_attachments"] = new JsonObject
            {
                [file] = new JsonObject { ["content_type"] = "application/octet-stream", ["data"] = Convert.ToBase64String(tarball), ["length"] = tarball.Length },
            },
        };
    }

    private static string NpmPath(string key, string name) => $"/packages/{key}/npm/{name.Replace("/", "%2f")}";

    private static async Task<HttpResponseMessage> NpmPublishAsync(HttpClient c, string key, string name, JsonObject body) =>
        await c.PutAsync(NpmPath(key, name), new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));

    private static async Task<JsonObject> JsonAsync(HttpResponseMessage r) =>
        (JsonNode.Parse(await r.Content.ReadAsStringAsync()) as JsonObject)!;

    // ---------------------------------------------------------------- NuGet

    [Fact]
    public async Task NuGet_push_restore_search_and_delete()
    {
        var p = await ProjectAsync();
        var bobToken = await app.CreateTokenAsync("bob", TokenScopes.WriteRepository);
        var nupkg = Nupkg("Acme.Widgets", "1.2", dependency: "Newtonsoft.Json");

        // Push with the token as API key (dotnet nuget push).
        var push = await NuGetPushAsync(p.Key, nupkg, bobToken);
        Assert.Equal(HttpStatusCode.Created, push.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await NuGetPushAsync(p.Key, nupkg, bobToken)).StatusCode);

        // Restore: the service index needs credentials (401 with a Basic challenge, so NuGet asks for them).
        using (var anon = Client())
        {
            var r = await anon.GetAsync($"/packages/{p.Key}/nuget/index.json");
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.StartsWith("Basic", r.Headers.WwwAuthenticate.ToString());
        }
        using var dan = await BasicClientAsync("dan", TokenScopes.ReadRepository); // a Viewer can restore
        var index = await JsonAsync(await dan.GetAsync($"/packages/{p.Key}/nuget/index.json"));
        var resources = index["resources"]!.AsArray().Select(r => r!["@type"]!.GetValue<string>()).ToList();
        Assert.Contains("PackagePublish/2.0.0", resources);
        Assert.Contains("PackageBaseAddress/3.0.0", resources);
        Assert.Contains("RegistrationsBaseUrl/3.6.0", resources);
        Assert.Contains("SearchQueryService", resources);

        var versions = await JsonAsync(await dan.GetAsync($"/packages/{p.Key}/nuget/v3/flatcontainer/acme.widgets/index.json"));
        Assert.Equal(["1.2.0"], versions["versions"]!.AsArray().Select(v => v!.GetValue<string>()));
        var download = await dan.GetAsync($"/packages/{p.Key}/nuget/v3/flatcontainer/acme.widgets/1.2.0/acme.widgets.1.2.0.nupkg");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(nupkg, await download.Content.ReadAsByteArrayAsync());
        var nuspec = await dan.GetStringAsync($"/packages/{p.Key}/nuget/v3/flatcontainer/acme.widgets/1.2.0/acme.widgets.nuspec");
        Assert.Contains("<id>Acme.Widgets</id>", nuspec);

        var reg = await JsonAsync(await dan.GetAsync($"/packages/{p.Key}/nuget/v3/registration/acme.widgets/index.json"));
        var entry = reg["items"]![0]!["items"]![0]!["catalogEntry"]!;
        Assert.Equal("Acme.Widgets", entry["id"]!.GetValue<string>());
        Assert.Equal("1.2.0", entry["version"]!.GetValue<string>());
        Assert.Equal("Newtonsoft.Json", entry["dependencyGroups"]![0]!["dependencies"]![0]!["id"]!.GetValue<string>());
        Assert.EndsWith("/acme.widgets.1.2.0.nupkg", entry["packageContent"]!.GetValue<string>());

        var search = await JsonAsync(await dan.GetAsync($"/packages/{p.Key}/nuget/query?q=widget&prerelease=true"));
        Assert.Equal(1, search["totalHits"]!.GetValue<int>());
        Assert.Equal("Acme.Widgets", search["data"]![0]!["id"]!.GetValue<string>());

        // A Viewer can't push or delete; a Maintainer+ can delete.
        var danToken = await app.CreateTokenAsync("dan", TokenScopes.WriteRepository);
        Assert.Equal(HttpStatusCode.Forbidden, (await NuGetPushAsync(p.Key, Nupkg("Acme.Other", "1.0.0"), danToken)).StatusCode);
        using (var del = new HttpRequestMessage(HttpMethod.Delete, $"/packages/{p.Key}/nuget/Acme.Widgets/1.2.0"))
        {
            del.Headers.Add("X-NuGet-ApiKey", bobToken); // Developer: not enough
            using var c = Client();
            Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(del)).StatusCode);
        }
        using (var del = new HttpRequestMessage(HttpMethod.Delete, $"/packages/{p.Key}/nuget/Acme.Widgets/1.2.0"))
        {
            del.Headers.Add("X-NuGet-ApiKey", await app.CreateTokenAsync("carol", TokenScopes.WriteApi));
            using var c = Client();
            Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(del)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await dan.GetAsync($"/packages/{p.Key}/nuget/v3/flatcontainer/acme.widgets/index.json")).StatusCode);

        await using var db = app.Db();
        Assert.True(await db.AuditEvents.AnyAsync(e => e.ProjectId == p.Id && e.Action == "package.published" && e.ActorId == app.UserId("bob")));
        Assert.True(await db.AuditEvents.AnyAsync(e => e.ProjectId == p.Id && e.Action == "package.version_deleted" && e.ActorId == app.UserId("carol")));
    }

    [Fact]
    public async Task NuGet_rejects_bad_packages_bad_credentials_and_read_only_tokens()
    {
        var p = await ProjectAsync();
        var token = await app.CreateTokenAsync("bob", TokenScopes.WriteRepository);
        Assert.Equal(HttpStatusCode.BadRequest, (await NuGetPushAsync(p.Key, Encoding.UTF8.GetBytes("not a zip"), token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await NuGetPushAsync(p.Key, Nupkg("Bad Id!", "1.0.0"), token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await NuGetPushAsync(p.Key, Nupkg("Acme.X", "1.0.0"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await NuGetPushAsync(p.Key, Nupkg("Acme.X", "1.0.0"), "gbp_not-a-real-token")).StatusCode);
        // A read-only token can restore but not push.
        var readOnly = await app.CreateTokenAsync("bob", TokenScopes.ReadRepository);
        Assert.Equal(HttpStatusCode.Forbidden, (await NuGetPushAsync(p.Key, Nupkg("Acme.X", "1.0.0"), readOnly)).StatusCode);
        // Account passwords are refused: tokens only.
        using var c = Client();
        c.DefaultRequestHeaders.Authorization = Basic("bob", SeedData.DemoPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync($"/packages/{p.Key}/nuget/index.json")).StatusCode);
        // Someone who can't see the project gets 404, as does a project that doesn't exist.
        await app.CreateUserAsync("pkg-outsider");
        using var outsider = await BasicClientAsync("pkg-outsider");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/packages/{p.Key}/nuget/index.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync("/packages/NOPE/nuget/index.json")).StatusCode);
    }

    [Fact]
    public async Task Internal_projects_let_every_signed_in_user_pull()
    {
        var p = await ProjectAsync(ProjectVisibility.Internal);
        Assert.Equal(HttpStatusCode.Created, (await NuGetPushAsync(p.Key, Nupkg("Shared.Lib", "2.0.0"), await app.CreateTokenAsync("bob", TokenScopes.WriteRepository))).StatusCode);
        await app.CreateUserAsync("pkg-internal-reader");
        using var reader = await BasicClientAsync("pkg-internal-reader");
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/packages/{p.Key}/nuget/v3/flatcontainer/shared.lib/index.json")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await NuGetPushAsync(p.Key, Nupkg("Shared.Lib", "2.0.1"), await app.CreateTokenAsync("pkg-internal-reader", TokenScopes.WriteRepository))).StatusCode);
    }

    [Fact]
    public async Task Disabled_registry_is_not_found_and_cleanup_policy_keeps_the_newest()
    {
        var p = await ProjectAsync();
        var token = await app.CreateTokenAsync("bob", TokenScopes.WriteRepository);
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<PackageService>().SaveSettingsAsync(p.Id, enabled: true, keepLatest: 2);
        foreach (var v in new[] { "1.0.0", "1.1.0", "1.2.0" })
        {
            Assert.Equal(HttpStatusCode.Created, (await NuGetPushAsync(p.Key, Nupkg("Keep.Me", v), token)).StatusCode);
            await Task.Delay(20);
        }
        await using (var db = app.Db())
        {
            var left = await db.PackageVersions.Where(v => v.Package.ProjectId == p.Id).Select(v => v.Version).OrderBy(v => v).ToListAsync();
            Assert.Equal(["1.1.0", "1.2.0"], left);
        }

        // Developers can't change the settings; turning the registry off makes every client get 404.
        await using (var s = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<PackageService>().SaveSettingsAsync(p.Id, false, null));
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<PackageService>().SaveSettingsAsync(p.Id, enabled: false, keepLatest: null);
        using var bob = await BasicClientAsync("bob");
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/packages/{p.Key}/nuget/index.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/v2/{p.Key.ToLowerInvariant()}/app/tags/list")).StatusCode);
    }

    // ---------------------------------------------------------------- npm

    [Fact]
    public async Task Npm_publish_install_metadata_dist_tags_and_unpublish()
    {
        var p = await ProjectAsync();
        var bobToken = await app.CreateTokenAsync("bob", TokenScopes.WriteRepository);
        using var bob = Client();
        bob.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bobToken); // npm's _authToken
        const string name = "@acme/widget";
        var tarball1 = Encoding.UTF8.GetBytes("fake tarball 1 " + Guid.NewGuid());
        var tarball2 = Encoding.UTF8.GetBytes("fake tarball 2 " + Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Created, (await NpmPublishAsync(bob, p.Key, name, NpmPublishBody(name, "1.0.0", tarball1))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await NpmPublishAsync(bob, p.Key, name, NpmPublishBody(name, "1.0.0", tarball2))).StatusCode); // no overwrite
        Assert.Equal(HttpStatusCode.BadRequest, (await NpmPublishAsync(bob, p.Key, name, NpmPublishBody(name, "1.0.1", tarball2, shasum: new string('0', 40)))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await NpmPublishAsync(bob, p.Key, name, NpmPublishBody(name, "2.0.0-beta.1", tarball2, tag: "beta"))).StatusCode);

        // npm install: the packument (scoped name encoded as npm does), then the tarball.
        using var dan = await BasicClientAsync("dan", TokenScopes.ReadRepository);
        var doc = await JsonAsync(await dan.GetAsync(NpmPath(p.Key, name)));
        Assert.Equal(name, doc["name"]!.GetValue<string>());
        Assert.Equal("1.0.0", doc["dist-tags"]!["latest"]!.GetValue<string>());
        Assert.Equal("2.0.0-beta.1", doc["dist-tags"]!["beta"]!.GetValue<string>());
        var dist = doc["versions"]!["1.0.0"]!["dist"]!;
        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(tarball1)), dist["shasum"]!.GetValue<string>());
        Assert.Equal("sha512-" + Convert.ToBase64String(SHA512.HashData(tarball1)), dist["integrity"]!.GetValue<string>());
        Assert.Equal("^1.3.0", doc["versions"]!["1.0.0"]!["dependencies"]!["left-pad"]!.GetValue<string>());
        var tarballUrl = new Uri(dist["tarball"]!.GetValue<string>()).PathAndQuery;
        Assert.EndsWith("/npm/@acme/widget/-/widget-1.0.0.tgz", tarballUrl);
        Assert.Equal(tarball1, await dan.GetByteArrayAsync(tarballUrl));
        Assert.Equal("2.0.0-beta.1", (await JsonAsync(await dan.GetAsync($"/packages/{p.Key}/npm/@acme/widget/beta")))["version"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client().GetAsync(NpmPath(p.Key, name))).StatusCode);

        // dist-tags: add, list, remove (Developers); Viewers can only read them.
        Assert.Equal(HttpStatusCode.Created, (await bob.PutAsync($"/packages/{p.Key}/npm/-/package/{name.Replace("/", "%2f")}/dist-tags/stable",
            new StringContent("\"1.0.0\"", Encoding.UTF8, "application/json"))).StatusCode);
        var tags = await JsonAsync(await dan.GetAsync($"/packages/{p.Key}/npm/-/package/{name.Replace("/", "%2f")}/dist-tags"));
        Assert.Equal("1.0.0", tags["stable"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Forbidden, (await dan.DeleteAsync($"/packages/{p.Key}/npm/-/package/{name.Replace("/", "%2f")}/dist-tags/stable")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.DeleteAsync($"/packages/{p.Key}/npm/-/package/{name.Replace("/", "%2f")}/dist-tags/stable")).StatusCode);
        Assert.Equal("bob", (await JsonAsync(await bob.GetAsync($"/packages/{p.Key}/npm/-/whoami")))["username"]!.GetValue<string>());

        // npm unpublish pkg@2.0.0-beta.1: the packument without that version (Maintainer only), then whole package.
        doc.AsObject()["versions"]!.AsObject().Remove("2.0.0-beta.1");
        var rev = doc["_rev"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PutAsync($"{NpmPath(p.Key, name)}/-rev/{rev}", new StringContent(doc.ToJsonString(), Encoding.UTF8, "application/json"))).StatusCode);
        using var carol = await BasicClientAsync("carol");
        Assert.Equal(HttpStatusCode.OK, (await carol.PutAsync($"{NpmPath(p.Key, name)}/-rev/{rev}", new StringContent(doc.ToJsonString(), Encoding.UTF8, "application/json"))).StatusCode);
        var after = await JsonAsync(await dan.GetAsync(NpmPath(p.Key, name)));
        Assert.Equal(["1.0.0"], after["versions"]!.AsObject().Select(kv => kv.Key));
        Assert.Null(after["dist-tags"]!["beta"]);
        Assert.Equal(HttpStatusCode.OK, (await carol.DeleteAsync($"{NpmPath(p.Key, name)}/-rev/{rev}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await dan.GetAsync(NpmPath(p.Key, name))).StatusCode);
    }

    // ---------------------------------------------------------------- OCI

    private sealed record Pushed(string ConfigDigest, string LayerDigest, string ManifestDigest, byte[] Manifest, byte[] Layer);

    /// <summary>Pushes a config (monolithic) and a layer (chunked, two PATCHes), then the manifest under <paramref name="tag"/>.</summary>
    private static async Task<Pushed> PushImageAsync(HttpClient c, string repo, string? tag)
    {
        var config = Encoding.UTF8.GetBytes($$"""{"architecture":"amd64","os":"linux","rootfs":{"type":"layers","diff_ids":[]},"n":"{{Guid.NewGuid()}}"}""");
        var layer = RandomNumberGenerator.GetBytes(5000);
        var configDigest = "sha256:" + Sha256(config);
        var layerDigest = "sha256:" + Sha256(layer);

        var mono = await c.PostAsync($"/v2/{repo}/blobs/uploads/?digest={configDigest}", new ByteArrayContent(config));
        Assert.Equal(HttpStatusCode.Created, mono.StatusCode);
        Assert.Equal(configDigest, mono.Headers.GetValues("Docker-Content-Digest").Single());

        var start = await c.PostAsync($"/v2/{repo}/blobs/uploads/", null);
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var location = start.Headers.Location!.ToString();
        var chunk1 = new ByteArrayContent(layer[..3000]);
        chunk1.Headers.TryAddWithoutValidation("Content-Range", "0-2999"); // the registry form, not RFC 7233
        var patch1 = await c.PatchAsync(location, chunk1);
        Assert.Equal(HttpStatusCode.Accepted, patch1.StatusCode);
        Assert.Equal("0-2999", patch1.Headers.GetValues("Range").Single());
        var badChunk = new ByteArrayContent(layer[3000..]);
        badChunk.Headers.TryAddWithoutValidation("Content-Range", "100-2099"); // out of order
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, (await c.PatchAsync(patch1.Headers.Location!.ToString(), badChunk)).StatusCode);
        var patch2 = await c.PatchAsync(patch1.Headers.Location!.ToString(), new ByteArrayContent(layer[3000..])); // stream mode, no Content-Range
        Assert.Equal("0-4999", patch2.Headers.GetValues("Range").Single());
        var finish = await c.PutAsync($"{patch2.Headers.Location}?digest={layerDigest}", null);
        Assert.Equal(HttpStatusCode.Created, finish.StatusCode);

        var manifest = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["schemaVersion"] = 2,
            ["mediaType"] = OciManifest.DockerV2,
            ["config"] = new JsonObject { ["mediaType"] = "application/vnd.docker.container.image.v1+json", ["size"] = config.Length, ["digest"] = configDigest },
            ["layers"] = new JsonArray(new JsonObject { ["mediaType"] = "application/vnd.docker.image.rootfs.diff.tar.gzip", ["size"] = layer.Length, ["digest"] = layerDigest }),
        }.ToJsonString());
        var content = new ByteArrayContent(manifest);
        content.Headers.ContentType = new MediaTypeHeaderValue(OciManifest.DockerV2);
        var put = await c.PutAsync($"/v2/{repo}/manifests/{tag ?? "sha256:" + Sha256(manifest)}", content); // no tag: pushed by digest, untagged
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        var digest = put.Headers.GetValues("Docker-Content-Digest").Single();
        Assert.Equal("sha256:" + Sha256(manifest), digest);
        return new Pushed(configDigest, layerDigest, digest, manifest, layer);
    }

    [Fact]
    public async Task Oci_push_and_pull_with_bearer_token_auth()
    {
        var p = await ProjectAsync();
        var repo = $"{p.Key.ToLowerInvariant()}/team/app";

        // The /v2/ check challenges with Bearer; the token endpoint trades Basic (user + PAT) for a token.
        using var anon = Client();
        var v2 = await anon.GetAsync("/v2/");
        Assert.Equal(HttpStatusCode.Unauthorized, v2.StatusCode);
        Assert.Contains("Bearer realm=", v2.Headers.WwwAuthenticate.ToString());
        Assert.Equal("registry/2.0", v2.Headers.GetValues(OciEndpoints.ApiVersionHeader).Single());
        anon.DefaultRequestHeaders.Authorization = Basic("bob", await app.CreateTokenAsync("bob", TokenScopes.WriteRepository));
        var token = (await JsonAsync(await anon.GetAsync($"/v2/token?service=gitbench&scope=repository:{repo}:pull,push")))["token"]!.GetValue<string>();
        Assert.StartsWith(RegistryAuth.BearerPrefix, token);
        using var bob = Client();
        bob.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/v2/")).StatusCode);

        var pushed = await PushImageAsync(bob, repo, "v1");

        // Pull (a Viewer): manifest by tag and by digest, then the blobs.
        using var dan = await BasicClientAsync("dan", TokenScopes.ReadRepository);
        var byTag = await dan.GetAsync($"/v2/{repo}/manifests/v1");
        Assert.Equal(HttpStatusCode.OK, byTag.StatusCode);
        Assert.Equal(OciManifest.DockerV2, byTag.Content.Headers.ContentType!.MediaType);
        Assert.Equal(pushed.Manifest, await byTag.Content.ReadAsByteArrayAsync());
        Assert.Equal(pushed.ManifestDigest, byTag.Headers.GetValues("Docker-Content-Digest").Single());
        var head = await dan.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/v2/{repo}/manifests/{pushed.ManifestDigest}"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(pushed.Manifest.Length, head.Content.Headers.ContentLength);
        Assert.Equal(pushed.Layer, await dan.GetByteArrayAsync($"/v2/{repo}/blobs/{pushed.LayerDigest}"));
        var tags = await JsonAsync(await dan.GetAsync($"/v2/{repo}/tags/list"));
        Assert.Equal(repo, tags["name"]!.GetValue<string>());
        Assert.Equal(["v1"], tags["tags"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.NotFound, (await dan.GetAsync($"/v2/{repo}/manifests/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await dan.GetAsync($"/v2/{repo}/blobs/sha256:{new string('a', 64)}")).StatusCode);

        // The Packages page data: one image with one tag, sized as manifest + config + layer.
        await using (var s = app.Scope(app.UserId("dan")))
        {
            var list = await s.Get<PackageService>().ListAsync(p.Id);
            var image = Assert.Single(list);
            Assert.Equal(("team/app", PackageType.Container, "v1"), (image.Name, image.Type, image.LatestVersion));
            Assert.True(image.Size >= pushed.Layer.Length);
        }
    }

    [Fact]
    public async Task Oci_verifies_digests_and_references_and_enforces_roles()
    {
        var p = await ProjectAsync();
        var repo = $"{p.Key.ToLowerInvariant()}/app";
        using var bob = await BasicClientAsync("bob");

        // A blob whose content doesn't match its digest is refused.
        var bad = await bob.PostAsync($"/v2/{repo}/blobs/uploads/?digest=sha256:{new string('0', 64)}", new ByteArrayContent([1, 2, 3]));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("DIGEST_INVALID", await bad.Content.ReadAsStringAsync());

        // A manifest that references blobs nobody pushed is refused.
        var orphan = new ByteArrayContent(Encoding.UTF8.GetBytes($$"""{"schemaVersion":2,"mediaType":"{{OciManifest.OciManifestV1}}","config":{"mediaType":"application/vnd.oci.image.config.v1+json","size":2,"digest":"sha256:{{new string('b', 64)}}"},"layers":[]}"""));
        orphan.Headers.ContentType = new MediaTypeHeaderValue(OciManifest.OciManifestV1);
        var refused = await bob.PutAsync($"/v2/{repo}/manifests/latest", orphan);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("MANIFEST_BLOB_UNKNOWN", await refused.Content.ReadAsStringAsync());

        var pushed = await PushImageAsync(bob, repo, "latest");

        // Viewers can't push; Developers can't delete; outsiders don't see the repository; Maintainers delete.
        using var dan = await BasicClientAsync("dan", TokenScopes.ReadRepository | TokenScopes.WriteRepository);
        var denied = await dan.PostAsync($"/v2/{repo}/blobs/uploads/", null);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("DENIED", await denied.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.DeleteAsync($"/v2/{repo}/manifests/latest")).StatusCode);
        await app.CreateUserAsync("oci-outsider");
        using var outsider = await BasicClientAsync("oci-outsider");
        var hidden = await outsider.GetAsync($"/v2/{repo}/manifests/latest");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Contains("NAME_UNKNOWN", await hidden.Content.ReadAsStringAsync());
        using var badCreds = Client();
        badCreds.DefaultRequestHeaders.Authorization = Basic("bob", "gbp_wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await badCreds.GetAsync($"/v2/{repo}/tags/list")).StatusCode);

        using var carol = await BasicClientAsync("carol");
        Assert.Equal(HttpStatusCode.Accepted, (await carol.DeleteAsync($"/v2/{repo}/manifests/{pushed.ManifestDigest}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await dan.GetAsync($"/v2/{repo}/manifests/latest")).StatusCode);
        Assert.Empty((await JsonAsync(await dan.GetAsync($"/v2/{repo}/tags/list")))["tags"]!.AsArray());
        await using var db = app.Db();
        Assert.True(await db.AuditEvents.AnyAsync(e => e.ProjectId == p.Id && e.Action == "package.version_deleted"));
    }

    [Fact]
    public async Task Oci_image_index_needs_its_child_manifests()
    {
        var p = await ProjectAsync();
        var repo = $"{p.Key.ToLowerInvariant()}/multi";
        using var bob = await BasicClientAsync("bob");
        var child = await PushImageAsync(bob, repo, "amd64");
        var index = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["schemaVersion"] = 2,
            ["mediaType"] = OciManifest.OciIndex,
            ["manifests"] = new JsonArray(new JsonObject { ["mediaType"] = OciManifest.DockerV2, ["size"] = child.Manifest.Length, ["digest"] = child.ManifestDigest }),
        }.ToJsonString());
        var content = new ByteArrayContent(index);
        content.Headers.ContentType = new MediaTypeHeaderValue(OciManifest.OciIndex);
        Assert.Equal(HttpStatusCode.Created, (await bob.PutAsync($"/v2/{repo}/manifests/1.0", content)).StatusCode);
        var got = await bob.GetAsync($"/v2/{repo}/manifests/1.0");
        Assert.Equal(OciManifest.OciIndex, got.Content.Headers.ContentType!.MediaType);

        var unknownChild = Encoding.UTF8.GetBytes(index.Length > 0 ? Encoding.UTF8.GetString(index).Replace(child.ManifestDigest[7..], new string('c', 64)) : "");
        var c2 = new ByteArrayContent(unknownChild);
        c2.Headers.ContentType = new MediaTypeHeaderValue(OciManifest.OciIndex);
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.PutAsync($"/v2/{repo}/manifests/2.0", c2)).StatusCode);
    }

    // ---------------------------------------------------------------- image indexes, catalog, npm deprecate

    private static byte[] IndexOf(params Pushed[] children) => Encoding.UTF8.GetBytes(new JsonObject
    {
        ["schemaVersion"] = 2,
        ["mediaType"] = OciManifest.OciIndex,
        ["manifests"] = new JsonArray(children.Select(c => (JsonNode)new JsonObject
            { ["mediaType"] = OciManifest.DockerV2, ["size"] = c.Manifest.Length, ["digest"] = c.ManifestDigest }).ToArray()),
    }.ToJsonString());

    private static async Task<HttpResponseMessage> PutIndexAsync(HttpClient c, string repo, string reference, byte[] index)
    {
        var content = new ByteArrayContent(index);
        content.Headers.ContentType = new MediaTypeHeaderValue(OciManifest.OciIndex);
        return await c.PutAsync($"/v2/{repo}/manifests/{reference}", content);
    }

    private static async Task<HttpStatusCode> ManifestStatusAsync(HttpClient c, string repo, string digest) =>
        (await c.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/v2/{repo}/manifests/{digest}"))).StatusCode;

    [Fact]
    public async Task Deleting_an_image_index_deletes_its_child_manifests_unless_something_else_references_them()
    {
        var p = await ProjectAsync();
        var repo = $"{p.Key.ToLowerInvariant()}/multi";
        using var carol = await BasicClientAsync("carol");
        var a = await PushImageAsync(carol, repo, null); // children are pushed by digest: untagged
        var b = await PushImageAsync(carol, repo, null);
        var c = await PushImageAsync(carol, repo, "tagged-child"); // a tagged image the index also lists
        var loose = await PushImageAsync(carol, repo, null); // pushed for a later index, referenced by none yet
        Assert.Equal(HttpStatusCode.Created, (await PutIndexAsync(carol, repo, "1.0", IndexOf(a, b, c))).StatusCode);
        var second = IndexOf(a);
        Assert.Equal(HttpStatusCode.Created, (await PutIndexAsync(carol, repo, "2.0", second)).StatusCode);

        // Layers younger than an hour are kept (a push may be in progress): age them so the sweep can take them.
        await using (var db = app.Db())
            await db.ContainerBlobs.ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, DateTime.UtcNow.AddHours(-2)));

        // Delete the first index by tag: b (only it used it) goes; a (the second index) and c (a tag) stay.
        Assert.Equal(HttpStatusCode.Accepted, (await carol.DeleteAsync($"/v2/{repo}/manifests/1.0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, await ManifestStatusAsync(carol, repo, b.ManifestDigest));
        Assert.Equal(HttpStatusCode.OK, await ManifestStatusAsync(carol, repo, a.ManifestDigest));
        Assert.Equal(HttpStatusCode.OK, await ManifestStatusAsync(carol, repo, c.ManifestDigest));
        Assert.Equal(HttpStatusCode.OK, await ManifestStatusAsync(carol, repo, loose.ManifestDigest)); // never part of the index: untouched
        Assert.Equal(HttpStatusCode.NotFound, (await carol.GetAsync($"/v2/{repo}/blobs/{b.LayerDigest}")).StatusCode); // its layer is released with it
        Assert.Equal(HttpStatusCode.OK, (await carol.GetAsync($"/v2/{repo}/blobs/{a.LayerDigest}")).StatusCode);

        // Delete the second index by digest: a goes now, c still has its tag.
        var secondDigest = "sha256:" + Sha256(second);
        Assert.Equal(HttpStatusCode.Accepted, (await carol.DeleteAsync($"/v2/{repo}/manifests/{secondDigest}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, await ManifestStatusAsync(carol, repo, secondDigest));
        Assert.Equal(HttpStatusCode.NotFound, await ManifestStatusAsync(carol, repo, a.ManifestDigest));
        Assert.Equal(HttpStatusCode.OK, await ManifestStatusAsync(carol, repo, c.ManifestDigest));
        Assert.Equal(HttpStatusCode.OK, await ManifestStatusAsync(carol, repo, "tagged-child"));
    }

    [Fact]
    public async Task Catalog_lists_only_the_repositories_the_caller_may_pull()
    {
        var shared = await ProjectAsync(); // bob Developer, dan Viewer
        var other = await app.CreateProjectAsync("alice"); // dan has no access
        var off = await ProjectAsync();
        using var carol = await BasicClientAsync("carol");
        using var alice = await BasicClientAsync("alice");
        var sharedRepo = $"{shared.Key.ToLowerInvariant()}/web";
        var otherRepo = $"{other.Key.ToLowerInvariant()}/secret";
        var offRepo = $"{off.Key.ToLowerInvariant()}/hidden";
        await PushImageAsync(carol, sharedRepo, "1");
        await PushImageAsync(carol, $"{shared.Key.ToLowerInvariant()}/api", "1");
        await PushImageAsync(alice, otherRepo, "1");
        await PushImageAsync(carol, offRepo, "1");
        await using (var db = app.Db())
        {
            db.ProjectPackageSettings.Add(new ProjectPackageSettings { ProjectId = off.Id, Enabled = false });
            await db.SaveChangesAsync();
        }

        using var dan = await BasicClientAsync("dan", TokenScopes.ReadRepository);
        var danList = (await JsonAsync(await dan.GetAsync("/v2/_catalog")))["repositories"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Assert.Contains(sharedRepo, danList);
        Assert.Contains($"{shared.Key.ToLowerInvariant()}/api", danList);
        Assert.DoesNotContain(otherRepo, danList); // no access to that project
        Assert.DoesNotContain(offRepo, danList); // registry turned off
        Assert.Equal(danList.Order(StringComparer.Ordinal).ToList(), danList);

        var adminList = (await JsonAsync(await alice.GetAsync("/v2/_catalog")))["repositories"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Assert.Contains(otherRepo, adminList); // alice owns it (and administers the site)
        Assert.Contains(sharedRepo, adminList);

        // Paging with n and last (Link header for the next page).
        var page = await dan.GetAsync("/v2/_catalog?n=1");
        Assert.Single((await JsonAsync(page))["repositories"]!.AsArray());
        Assert.Contains("rel=\"next\"", page.Headers.GetValues("Link").Single());
        var next = await JsonAsync(await dan.GetAsync($"/v2/_catalog?n=1&last={Uri.EscapeDataString(danList[0])}"));
        Assert.Equal(danList[1], next["repositories"]![0]!.GetValue<string>());

        // Anonymous callers are challenged.
        var anon = await Client().GetAsync("/v2/_catalog");
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
        Assert.Contains("Bearer", anon.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Npm_deprecate_marks_a_version_and_clearing_it_removes_the_mark()
    {
        var p = await ProjectAsync();
        using var bob = await BasicClientAsync("bob");
        using var dan = await BasicClientAsync("dan", TokenScopes.ReadRepository | TokenScopes.WriteRepository);
        const string name = "@acme/old-lib";
        Assert.Equal(HttpStatusCode.Created, (await NpmPublishAsync(bob, p.Key, name, NpmPublishBody(name, "1.0.0", Encoding.UTF8.GetBytes("tarball one " + Guid.NewGuid())))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await NpmPublishAsync(bob, p.Key, name, NpmPublishBody(name, "1.1.0", Encoding.UTF8.GetBytes("tarball two " + Guid.NewGuid())))).StatusCode);

        // `npm deprecate pkg@1.0.0 "use 1.1.0"`: the packument is fetched, the message set on the version, and PUT back to -rev.
        var doc = await JsonAsync(await bob.GetAsync(NpmPath(p.Key, name)));
        Assert.Null(doc["versions"]!["1.0.0"]!["deprecated"]);
        var rev = doc["_rev"]!.GetValue<string>();
        doc["versions"]!["1.0.0"]!["deprecated"] = "use 1.1.0 instead";
        using (var viewer = await BasicClientAsync("dan", TokenScopes.ReadRepository))
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsync($"{NpmPath(p.Key, name)}/-rev/{rev}", new StringContent(doc.ToJsonString(), Encoding.UTF8, "application/json"))).StatusCode);
        var put = await bob.PutAsync($"{NpmPath(p.Key, name)}/-rev/{rev}", new StringContent(doc.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode); // a Developer may deprecate; nothing is unpublished

        var after = await JsonAsync(await dan.GetAsync(NpmPath(p.Key, name)));
        Assert.Equal("use 1.1.0 instead", after["versions"]!["1.0.0"]!["deprecated"]!.GetValue<string>());
        Assert.Null(after["versions"]!["1.1.0"]!["deprecated"]);
        Assert.Equal(["1.0.0", "1.1.0"], after["versions"]!.AsObject().Select(kv => kv.Key).Order().ToArray()); // both still installable

        // `npm deprecate pkg@1.0.0 ""`: an empty message undoes it.
        after["versions"]!["1.0.0"]!["deprecated"] = "";
        Assert.Equal(HttpStatusCode.OK, (await bob.PutAsync($"{NpmPath(p.Key, name)}/-rev/{after["_rev"]!.GetValue<string>()}",
            new StringContent(after.ToJsonString(), Encoding.UTF8, "application/json"))).StatusCode);
        var cleared = await JsonAsync(await dan.GetAsync(NpmPath(p.Key, name)));
        Assert.Null(cleared["versions"]!["1.0.0"]!["deprecated"]);

        // A non-string "deprecated" is ignored instead of failing the request.
        cleared["versions"]!["1.1.0"]!["deprecated"] = 42;
        Assert.Equal(HttpStatusCode.OK, (await bob.PutAsync($"{NpmPath(p.Key, name)}/-rev/{cleared["_rev"]!.GetValue<string>()}",
            new StringContent(cleared.ToJsonString(), Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Null((await JsonAsync(await dan.GetAsync(NpmPath(p.Key, name))))["versions"]!["1.1.0"]!["deprecated"]);
    }

    // ---------------------------------------------------------------- CI job tokens and webhooks

    [Fact]
    public async Task Ci_job_token_publishes_to_its_own_project_while_the_job_runs()
    {
        var p = await ProjectAsync();
        var other = await ProjectAsync();
        var repo = await app.CreateRepoAsync(p.Id, "carol");
        int jobId, pipelineId;
        await using (var db = app.Db())
        {
            var pipeline = new Pipeline { RepositoryId = repo.Id, Ref = "main", CommitSha = new string('a', 40), Status = PipelineStatus.Running, TriggeredById = app.UserId("bob") };
            var job = new PipelineJob { Pipeline = pipeline, Name = "publish", Image = "alpine", Status = PipelineStatus.Running };
            db.PipelineJobs.Add(job);
            await db.SaveChangesAsync();
            (jobId, pipelineId) = (job.Id, pipeline.Id);
        }
        var token = app.Services.GetRequiredService<CiJobTokens>().Create(jobId);
        Assert.StartsWith(CiJobTokens.Prefix, token);

        // NuGet with the job token as API key; npm/OCI with Basic gitbench-ci-token:<token>.
        Assert.Equal(HttpStatusCode.Created, (await NuGetPushAsync(p.Key, Nupkg("Ci.Built", "0.1.0"), token)).StatusCode);
        using var ci = Client();
        ci.DefaultRequestHeaders.Authorization = Basic(CiJobTokens.UserName, token);
        var pushed = await PushImageAsync(ci, $"{p.Key.ToLowerInvariant()}/ci", "build-1");
        Assert.NotNull(pushed.ManifestDigest);

        // Not into another project (even one the pipeline's user can push to), and never deletes.
        Assert.Equal(HttpStatusCode.Forbidden, (await NuGetPushAsync(other.Key, Nupkg("Ci.Built", "0.1.0"), token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ci.DeleteAsync($"/v2/{p.Key.ToLowerInvariant()}/ci/manifests/build-1")).StatusCode);

        await using (var db = app.Db())
        {
            var v = await db.PackageVersions.Include(x => x.Package).SingleAsync(x => x.Package.ProjectId == p.Id && x.Package.Type == PackageType.NuGet);
            Assert.Equal(pipelineId, v.PipelineId);
            Assert.Equal(app.UserId("bob"), v.PublishedById);
            // The job ends: its token stops working.
            await db.PipelineJobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, PipelineStatus.Passed));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await ci.GetAsync($"/v2/{p.Key.ToLowerInvariant()}/ci/tags/list")).StatusCode);
        Assert.Null(await app.Services.GetRequiredService<CiJobTokens>().ValidateAsync("gbj_forged"));
    }

    [Fact]
    public async Task Publishing_and_deleting_send_webhooks()
    {
        using var receiver = new WebhookReceiver();
        var p = await ProjectAsync();
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<WebhookService>().CreateAsync(p.Id, receiver.Url, "a-test-secret-of-sufficient-length",
                [WebhookEventNames.PackagePublished, WebhookEventNames.PackageDeleted], active: true);
        Assert.Equal(HttpStatusCode.Created, (await NuGetPushAsync(p.Key, Nupkg("Hooked.Pkg", "3.0.0"), await app.CreateTokenAsync("bob", TokenScopes.WriteRepository))).StatusCode);
        var published = JsonDocument.Parse((await receiver.WaitForAsync(WebhookEventNames.PackagePublished)).Body).RootElement;
        Assert.Equal("Hooked.Pkg", published.GetProperty("package").GetProperty("name").GetString());
        Assert.Equal("3.0.0", published.GetProperty("package").GetProperty("version").GetString());
        Assert.Equal("nuget", published.GetProperty("package").GetProperty("type").GetString());
        Assert.Equal(p.Key, published.GetProperty("project").GetProperty("key").GetString());
        Assert.Equal("bob", published.GetProperty("sender").GetProperty("username").GetString());

        // Deleting from the UI service (Maintainer+).
        int packageId;
        await using (var db = app.Db()) packageId = await db.Packages.Where(x => x.ProjectId == p.Id).Select(x => x.Id).SingleAsync();
        await using (var s = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<PackageService>().DeletePackageAsync(p.Id, packageId));
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<PackageService>().DeletePackageAsync(p.Id, packageId);
        var deleted = JsonDocument.Parse((await receiver.WaitForAsync(WebhookEventNames.PackageDeleted)).Body).RootElement;
        Assert.Equal("Hooked.Pkg", deleted.GetProperty("package").GetProperty("name").GetString());
        await using (var db = app.Db()) Assert.False(await db.Packages.AnyAsync(x => x.ProjectId == p.Id));
    }

    [Fact]
    public async Task Admin_sees_storage_usage_and_unreferenced_blobs_are_swept()
    {
        var p = await ProjectAsync();
        Assert.Equal(HttpStatusCode.Created, (await NuGetPushAsync(p.Key, Nupkg("Sized.Pkg", "1.0.0"), await app.CreateTokenAsync("bob", TokenScopes.WriteRepository))).StatusCode);
        await using (var s = app.Scope(app.UserId("alice")))
        {
            var (projects, disk) = await s.Get<PackageService>().SiteUsageAsync();
            var mine = Assert.Single(projects, u => u.ProjectId == p.Id);
            Assert.True(mine.Bytes > 0 && disk >= mine.Bytes);
        }
        await using (var s = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<PackageService>().SiteUsageAsync());

        // A blob nothing references is removed by the sweep; referenced ones stay.
        var storage = app.Services.GetRequiredService<PackageStorage>();
        var stray = await storage.SaveAsync(Encoding.UTF8.GetBytes("stray " + Guid.NewGuid()), CancellationToken.None);
        await storage.SweepAsync(TimeSpan.FromDays(-1), CancellationToken.None);
        Assert.False(storage.Exists(stray.Sha256));
        await using var db = app.Db();
        var kept = await db.PackageFiles.Where(f => db.PackageVersions.Any(v => v.Id == f.PackageVersionId && v.Package.ProjectId == p.Id)).Select(f => f.Sha256).SingleAsync();
        Assert.True(storage.Exists(kept));
    }

    [Fact]
    public void Ci_jobs_get_the_registry_variables()
    {
        var vars = PackageUrls.CiVariables("https://git.example.com:8443", "ACME");
        Assert.Equal("git.example.com:8443", vars["GITBENCH_REGISTRY"]);
        Assert.Equal("git.example.com:8443/acme", vars["GITBENCH_REGISTRY_IMAGE"]);
        Assert.Equal(CiJobTokens.UserName, vars["GITBENCH_REGISTRY_USER"]);
        Assert.Equal("https://git.example.com:8443/packages/ACME/nuget/index.json", vars["GITBENCH_NUGET_SOURCE_URL"]);
        Assert.Equal("https://git.example.com:8443/packages/ACME/npm/", vars["GITBENCH_NPM_REGISTRY_URL"]);
        Assert.DoesNotContain(vars.Keys, k => k.Contains("PASSWORD") || k.Contains("TOKEN")); // secrets go in the env file
    }

    [Theory]
    [InlineData("1.2", "1.2.0")]
    [InlineData("01.002.3.0", "1.2.3")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("1.0.0-Beta.1+build.5", "1.0.0-Beta.1")]
    [InlineData("1.x", null)]
    public void NuGet_versions_are_normalized(string input, string? expected) =>
        Assert.Equal(expected, NuGetEndpoints.NormalizeVersion(input));
}
