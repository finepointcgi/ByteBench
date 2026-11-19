using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ByteBench.Data;
using ByteBench.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ByteBench.Tests;

/// <summary>Registry limits: 6 requests per credential and 12 per client IP, per minute.</summary>
public class RegistryLimitedApp : PackageTestApp
{
    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        base.AddSettings(settings);
        settings["RateLimiting:Enabled"] = "true";
        settings["RateLimiting:Registry:PermitLimit"] = "6";
        settings["RateLimiting:Registry:WindowSeconds"] = "60";
        settings["RateLimiting:RegistryIp:PermitLimit"] = "12";
        settings["RateLimiting:RegistryIp:WindowSeconds"] = "60";
    }
}

/// <summary>Plenty for a few requests, but the per-token limit is 10: a big push must count requests, never bytes.</summary>
public class RegistryBulkApp : PackageTestApp
{
    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        base.AddSettings(settings);
        settings["RateLimiting:Enabled"] = "true";
        settings["RateLimiting:Registry:PermitLimit"] = "10";
        settings["RateLimiting:Registry:WindowSeconds"] = "60";
        settings["RateLimiting:RegistryIp:PermitLimit"] = "100";
    }
}

public class RegistryRateLimitTests(RegistryLimitedApp app) : IClassFixture<RegistryLimitedApp>
{
    private HttpClient ClientFor(string token)
    {
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("x:" + token)));
        return c;
    }

    [Fact]
    public async Task Registry_requests_are_limited_per_token_and_per_ip_with_429_and_retry_after()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("dan", ProjectRole.Viewer));
        var key = project.Key;
        using var bob = ClientFor(await app.CreateTokenAsync("bob", TokenScopes.ReadRepository | TokenScopes.WriteRepository));
        using var carol = ClientFor(await app.CreateTokenAsync("carol", TokenScopes.ReadRepository | TokenScopes.WriteRepository));
        using var dan = ClientFor(await app.CreateTokenAsync("dan", TokenScopes.ReadRepository));

        // bob: the container registry. Six requests pass, the seventh is a 429 in the registry's own error format.
        for (var i = 0; i < 6; i++) Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/v2/")).StatusCode);
        var limited = await bob.GetAsync("/v2/");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(int.Parse(limited.Headers.GetValues("Retry-After").Single()) >= 1);
        Assert.Contains("TOOMANYREQUESTS", await limited.Content.ReadAsStringAsync());
        // The same token is limited on the package paths too (one budget for the whole registry).
        Assert.Equal(HttpStatusCode.TooManyRequests, (await bob.GetAsync($"/packages/{key}/npm/-/whoami")).StatusCode);

        // Other tokens have their own budget; /packages is covered as well.
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.OK, (await carol.GetAsync($"/packages/{key}/npm/-/whoami")).StatusCode);

        // 6 (bob) + 5 (carol) + 1 (dan) = 12 used on this IP: the next request is refused by the IP ceiling although dan's token has room.
        Assert.Equal(HttpStatusCode.OK, (await dan.GetAsync("/v2/")).StatusCode);
        var ipLimited = await dan.GetAsync("/v2/");
        Assert.Equal(HttpStatusCode.TooManyRequests, ipLimited.StatusCode);
        Assert.NotEmpty(ipLimited.Headers.GetValues("Retry-After"));
        var npm = await dan.GetAsync($"/packages/{key}/npm/-/whoami");
        Assert.Equal(HttpStatusCode.TooManyRequests, npm.StatusCode);
        Assert.Contains("Too many requests", await npm.Content.ReadAsStringAsync()); // plain text outside /v2 and /api

        // Everything else is untouched by the registry limits.
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await app.CreateClient().GetAsync("/")).StatusCode);
    }
}

public class RegistryBulkTests(RegistryBulkApp app) : IClassFixture<RegistryBulkApp>
{
    [Fact]
    public async Task A_large_chunked_blob_upload_costs_requests_not_bytes()
    {
        var project = await app.CreateProjectAsync("carol");
        var repo = $"{project.Key.ToLowerInvariant()}/big";
        using var c = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("x:" + await app.CreateTokenAsync("carol", TokenScopes.ReadRepository | TokenScopes.WriteRepository))));

        var blob = RandomNumberGenerator.GetBytes(6 * 1024 * 1024);
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(blob));
        var start = await c.PostAsync($"/v2/{repo}/blobs/uploads/", null); // 1 request
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var location = start.Headers.Location!.ToString();
        for (var offset = 0; offset < blob.Length; offset += 2 * 1024 * 1024) // 3 chunks of 2 MB: 3 requests
        {
            var patch = await c.PatchAsync(location, new ByteArrayContent(blob[offset..(offset + 2 * 1024 * 1024)]));
            Assert.Equal(HttpStatusCode.Accepted, patch.StatusCode);
            location = patch.Headers.Location!.ToString();
        }
        var finish = await c.PutAsync($"{location}?digest={digest}", null); // 1 request
        Assert.Equal(HttpStatusCode.Created, finish.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/v2/{repo}/blobs/{digest}"))).StatusCode); // 1 request

        // 6 of 10 requests used for 6 MB: the budget counted requests only.
        for (var i = 0; i < 4; i++) Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/v2/")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await c.GetAsync("/v2/")).StatusCode);
    }
}
