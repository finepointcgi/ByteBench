using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Webhooks;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Tests;

/// <summary>A local HTTP receiver that records webhook deliveries.</summary>
public sealed class WebhookReceiver : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly List<Received> received = [];
    private readonly SemaphoreSlim signal = new(0);

    public sealed record Received(string Event, string? Signature, byte[] Body);

    public string Url { get; }

    public WebhookReceiver()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Url = $"http://127.0.0.1:{port}/hook/";
        listener.Prefixes.Add(Url);
        listener.Start();
        _ = LoopAsync();
    }

    private async Task LoopAsync()
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); } catch { return; }
            using var ms = new MemoryStream();
            await ctx.Request.InputStream.CopyToAsync(ms);
            lock (received) received.Add(new(ctx.Request.Headers["X-ByteBench-Event"] ?? "", ctx.Request.Headers["X-ByteBench-Signature-256"], ms.ToArray()));
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
            signal.Release();
        }
    }

    /// <summary>Waits for a delivery of <paramref name="eventName"/>.</summary>
    public async Task<Received> WaitForAsync(string eventName, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (true)
        {
            lock (received)
                if (received.FirstOrDefault(r => r.Event == eventName) is { } r) return r;
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !await signal.WaitAsync(left)) throw new TimeoutException($"No {eventName} delivery arrived.");
        }
    }

    public int Count(string eventName) { lock (received) return received.Count(r => r.Event == eventName); }

    public void Dispose()
    {
        // The managed HttpListener can throw from Close when the port was reused meanwhile; nothing to clean up then.
        try { listener.Close(); } catch (HttpListenerException) { } catch (ObjectDisposedException) { }
    }
}

public class WebhookTests(TestApp app) : IClassFixture<TestApp>
{
    [Fact]
    public async Task Deliveries_are_signed_with_hmac_sha256_of_the_raw_body()
    {
        using var receiver = new WebhookReceiver();
        var project = await app.CreateProjectAsync("carol");
        const string secret = "a-test-secret-of-sufficient-length";
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<WebhookService>().CreateAsync(project.Id, receiver.Url, secret, [WebhookEventNames.IssueCreated], active: true);

        var issue = await app.CreateIssueAsync(project.Id, "carol", "Hooked");
        var delivery = await receiver.WaitForAsync(WebhookEventNames.IssueCreated);

        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), delivery.Body));
        Assert.Equal(expected, delivery.Signature);
        Assert.NotEqual(expected, WebhookSender.Sign("another-secret", delivery.Body));
        var body = JsonDocument.Parse(delivery.Body).RootElement;
        Assert.Equal("issue.created", body.GetProperty("event").GetString());
        Assert.Equal(project.Key, body.GetProperty("project").GetProperty("key").GetString());
        Assert.Equal(issue.Number, body.GetProperty("issue").GetProperty("number").GetInt32());
        Assert.Equal("carol", body.GetProperty("sender").GetProperty("username").GetString());

        // The delivery is recorded, and events the hook didn't subscribe to aren't sent.
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<IssueService>().AddCommentAsync(issue.Id, app.UserId("carol"), "not subscribed");
        await Task.Delay(300);
        Assert.Equal(0, receiver.Count(WebhookEventNames.IssueCommented));
        await using var db = app.Db();
        Assert.True(await db.WebhookDeliveries.AnyAsync(d => d.Event == "issue.created" && d.StatusCode == 200));
    }

    [Fact]
    public async Task Only_maintainers_manage_webhooks()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        await using var s = app.Scope(app.UserId("bob"));
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            s.Get<WebhookService>().CreateAsync(project.Id, "https://example.com/hook", null, [WebhookEventNames.Push], true));
    }

    private sealed class FixedOptions(WebhookOptions value) : IOptionsMonitor<WebhookOptions>
    {
        public WebhookOptions CurrentValue => value;
        public WebhookOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<WebhookOptions, string?> listener) => null;
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/hook")]
    [InlineData("http://localhost/hook")]
    [InlineData("http://api.localhost/hook")]
    [InlineData("http://10.1.2.3/hook")]
    [InlineData("http://172.16.0.1/hook")]
    [InlineData("http://192.168.1.10/hook")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://100.64.0.1/hook")]
    [InlineData("http://0.0.0.0/hook")]
    [InlineData("http://[::1]/hook")]
    [InlineData("http://[fd00::1]/hook")]
    [InlineData("http://[fe80::1]/hook")]
    [InlineData("http://[::ffff:127.0.0.1]/hook")]
    [InlineData("http://[::ffff:169.254.169.254]/hook")]
    [InlineData("http://224.0.0.1/hook")]
    public async Task Ssrf_guard_blocks_private_addresses(string url)
    {
        using var sender = new WebhookSender(new FixedOptions(new WebhookOptions { AllowPrivateNetworks = false }));
        Assert.NotNull(sender.ValidateUrl(url));
        var attempt = await sender.SendAsync(url, "ping", Guid.NewGuid().ToString(), "s", "{}"u8.ToArray(), false, 1, CancellationToken.None);
        Assert.False(attempt.Success);
        Assert.False(attempt.Retryable);
        Assert.Null(attempt.StatusCode);

        using var permissive = new WebhookSender(new FixedOptions(new WebhookOptions { AllowPrivateNetworks = true }));
        Assert.Null(permissive.ValidateUrl(url));
    }

    [Theory]
    [InlineData("ftp://example.com/hook")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user:pass@example.com/hook")]
    [InlineData("/relative")]
    public void Urls_must_be_absolute_http_without_credentials(string url)
    {
        using var sender = new WebhookSender(new FixedOptions(new WebhookOptions { AllowPrivateNetworks = true }));
        Assert.NotNull(sender.ValidateUrl(url));
    }

    [Fact]
    public void Public_addresses_are_allowed()
    {
        using var sender = new WebhookSender(new FixedOptions(new WebhookOptions()));
        Assert.Null(sender.ValidateUrl("https://example.com/hook"));
        Assert.Null(sender.ValidateUrl("https://93.184.216.34/hook"));
    }

    [Fact]
    public async Task Ssrf_guard_never_connects_to_a_blocked_receiver()
    {
        using var receiver = new WebhookReceiver();
        using var sender = new WebhookSender(new FixedOptions(new WebhookOptions { AllowPrivateNetworks = false }));
        var attempt = await sender.SendAsync(receiver.Url, "ping", Guid.NewGuid().ToString(), "s", "{}"u8.ToArray(), false, 1, CancellationToken.None);
        Assert.False(attempt.Success);
        await Task.Delay(200);
        Assert.Equal(0, receiver.Count("ping"));
    }
}

/// <summary>The app with the production default for webhooks (no private targets).</summary>
public class NoPrivateNetworksApp : TestApp
{
    protected override void AddSettings(IDictionary<string, string?> settings) => settings["Webhooks:AllowPrivateNetworks"] = "false";
}

public class WebhookConfigTests(NoPrivateNetworksApp app) : IClassFixture<NoPrivateNetworksApp>
{
    [Fact]
    public async Task With_AllowPrivateNetworks_false_hooks_to_private_addresses_are_refused()
    {
        var project = await app.CreateProjectAsync("carol");
        await using var s = app.Scope(app.UserId("carol"));
        var hooks = s.Get<WebhookService>();
        foreach (var url in new[] { "http://127.0.0.1:9/hook", "http://169.254.169.254/", "http://localhost/x", "http://[::1]/" })
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => hooks.CreateAsync(project.Id, url, null, [WebhookEventNames.Push], true));
        Assert.NotNull(await hooks.CreateAsync(project.Id, "https://example.com/hook", null, [WebhookEventNames.Push], true));
    }
}
