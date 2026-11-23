using ByteBench.Services.Attachments;
using ByteBench.Services.TrackerImport;
using ByteBench.Services.Webhooks;
using ByteBench.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace ByteBench.Tests;

/// <summary>The attachment download path of <see cref="TrackerHttp"/> on its own: guarded redirects, credentials, size limits.</summary>
public sealed class TrackerHttpStreamTests : IDisposable
{
    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private readonly TrackerImportOptions options = new() { AllowPrivateNetworks = true, RequestTimeoutSeconds = 10 };
    private readonly TrackerHttp http;
    private readonly FakeFileHost a = new();
    private readonly FakeFileHost b = new();

    public TrackerHttpStreamTests()
    {
        var monitor = new Monitor<TrackerImportOptions>(options);
        http = new TrackerHttp(new TrackerUrlGuard(monitor, new Monitor<WebhookOptions>(new WebhookOptions())), monitor, new TrackerImportTestHooks());
    }

    public void Dispose() { http.Dispose(); a.Dispose(); b.Dispose(); }

    private static void Token(HttpRequestMessage r) => r.Headers.Authorization = new("Bearer", "secret-token-123");

    private async Task<byte[]> ReadAllAsync(string path, long max = 1024 * 1024)
    {
        await using var stream = await http.GetStreamAsync(a.Url + path, Token, a.Url, max, CancellationToken.None, "Source");
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task A_file_is_streamed_with_credentials_on_the_sources_own_address()
    {
        a.Files["/f"] = new FakeFile(FakeFiles.Pdf);
        Assert.Equal(FakeFiles.Pdf, await ReadAllAsync("/f"));
        Assert.Equal("Bearer secret-token-123", a.Requests.Single().Authorization);
    }

    [Fact]
    public async Task Redirects_keep_the_token_on_the_same_address_and_drop_it_for_another_host()
    {
        a.Files["/start"] = new FakeFile(RedirectTo: a.Url + "/same?x=1");
        a.Files["/same"] = new FakeFile(RedirectTo: b.Url + "/cdn?sig=abc");
        b.Files["/cdn"] = new FakeFile(FakeFiles.Pdf);
        Assert.Equal(FakeFiles.Pdf, await ReadAllAsync("/start"));

        Assert.All(a.Requests, r => Assert.Equal("Bearer secret-token-123", r.Authorization));
        Assert.Equal(2, a.Requests.Count);
        var other = Assert.Single(b.Requests);
        Assert.Null(other.Authorization);
        Assert.Equal("/cdn?sig=abc", other.PathAndQuery);
    }

    [Fact]
    public async Task At_most_three_redirects_are_followed()
    {
        a.Files["/r0"] = new FakeFile(RedirectTo: a.Url + "/r1");
        a.Files["/r1"] = new FakeFile(RedirectTo: a.Url + "/r2");
        a.Files["/r2"] = new FakeFile(RedirectTo: a.Url + "/r3");
        a.Files["/r3"] = new FakeFile(FakeFiles.Pdf);
        Assert.Equal(FakeFiles.Pdf, await ReadAllAsync("/r0"));   // three redirects: fine

        a.Files["/r3"] = new FakeFile(RedirectTo: a.Url + "/r4");
        a.Files["/r4"] = new FakeFile(FakeFiles.Pdf);
        var ex = await Assert.ThrowsAsync<SourceException>(() => ReadAllAsync("/r0"));
        Assert.Contains("too many times", ex.Message);
        Assert.DoesNotContain("secret-token-123", ex.Message);
    }

    [Fact]
    public async Task Every_redirect_hop_goes_through_the_address_guard_again()
    {
        // The first host is fine (private networks allowed); the moment it answers, private networks become forbidden, so the hop to the
        // second local host is refused before any request reaches it.
        a.Files["/start"] = new FakeFile(RedirectTo: b.Url + "/inside");
        b.Files["/inside"] = new FakeFile(FakeFiles.Pdf);
        a.OnRequest = _ => options.AllowPrivateNetworks = false;
        try
        {
            var ex = await Assert.ThrowsAsync<SourceException>(() => ReadAllAsync("/start"));
            Assert.Contains("can't be on this server", ex.Message);
            Assert.Empty(b.Requests);
        }
        finally { options.AllowPrivateNetworks = true; }

        a.Files["/metadata"] = new FakeFile(RedirectTo: "http://169.254.169.254/latest/meta-data/");
        a.OnRequest = _ => options.AllowPrivateNetworks = false;
        try { await Assert.ThrowsAsync<SourceException>(() => ReadAllAsync("/metadata")); }
        finally { options.AllowPrivateNetworks = true; }

        a.OnRequest = null;
        a.Files["/scheme"] = new FakeFile(RedirectTo: "ftp://files.example/x");
        var scheme = await Assert.ThrowsAsync<SourceException>(() => ReadAllAsync("/scheme"));
        Assert.Contains("isn't http(s)", scheme.Message);
    }

    [Fact]
    public async Task Size_limits_hold_for_a_declared_length_and_for_a_stream_that_keeps_going()
    {
        a.Files["/declared"] = new FakeFile(FakeFiles.Filler(5000));
        await Assert.ThrowsAsync<AttachmentTooLargeException>(() => ReadAllAsync("/declared", 4096));
        a.Files["/chunked"] = new FakeFile(FakeFiles.Filler(5000), Chunked: true);
        await Assert.ThrowsAsync<AttachmentTooLargeException>(() => ReadAllAsync("/chunked", 4096));
        a.Files["/fits"] = new FakeFile(FakeFiles.Filler(4096), Chunked: true);
        Assert.Equal(4096, (await ReadAllAsync("/fits", 4096)).Length);
    }

    [Fact]
    public async Task Missing_and_forbidden_files_fail_with_a_message_that_has_no_address_or_credential()
    {
        a.Files["/gone"] = new FakeFile(Status: 404);
        a.Files["/denied"] = new FakeFile(Status: 403);
        var gone = await Assert.ThrowsAsync<SourceException>(() => ReadAllAsync("/gone"));
        var denied = await Assert.ThrowsAsync<SourceException>(() => ReadAllAsync("/denied"));
        Assert.Contains("no longer has the file", gone.Message);
        Assert.Contains("refused", denied.Message);
        Assert.All(new[] { gone.Message, denied.Message }, m => { Assert.DoesNotContain("secret-token-123", m); Assert.DoesNotContain("127.0.0.1", m); });
    }
}
