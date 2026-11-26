using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Push;
using ByteBench.Services.Webhooks;
using ByteBench.Tests.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ByteBench.Tests;

/// <summary>The app with the push delivery worker off (tests run <see cref="PushWorker.ProcessAsync"/>) and a local fake push service allowed.</summary>
public sealed class PushTestApp : TestApp
{
    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["Push:DeliveryWorker"] = "false";
        settings["Push:AllowPrivateNetworks"] = "true";
        settings["Push:Subject"] = "mailto:ops@push.test";
    }
}

/// <summary>A fixed <see cref="IOptionsMonitor{T}"/> for constructing services by hand.</summary>
public sealed class StaticOptions<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

public class WebPushTests(PushTestApp app) : IClassFixture<PushTestApp>, IDisposable
{
    private readonly FakePushService push = new();
    private static int counter;

    public void Dispose() => push.Dispose();

    private static byte[] B(string base64url) => WebPushCrypto.FromBase64Url(base64url);

    private async Task<User> NewUserAsync() => await app.CreateUserAsync("push" + Interlocked.Increment(ref counter));

    private async Task SubscribeAsync(int userId, FakeBrowser browser)
    {
        await using var s = app.Scope();
        var error = await s.Get<PushSubscriptions>().AddAsync(userId, browser.Endpoint, browser.P256dh, browser.AuthText, "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15) Firefox/140.0");
        Assert.Null(error);
    }

    /// <summary>A notification for <paramref name="userId"/> through the real path: alice assigns them a new issue (with a description).</summary>
    private async Task<int> AssignIssueAsync(int userId, string description = "SECRET-DESCRIPTION-TEXT do not push")
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private);
        await using (var s = app.Scope(app.UserId("alice")))
        {
            await s.Get<MemberService>().AddMemberAsync(project.Id, (await UserNameAsync(userId)), ProjectRole.Developer);
            await s.Get<IssueService>().CreateIssueAsync(project.Id,
                new Issue { Title = "Push me", Description = description, AssigneeId = userId }, [], app.UserId("alice"));
        }
        await using var db = app.Db();
        return await db.Notifications.Where(n => n.UserId == userId).OrderByDescending(n => n.Id).Select(n => n.Id).FirstAsync();
    }

    private async Task<string> UserNameAsync(int userId)
    {
        await using var db = app.Db();
        return await db.Users.Where(u => u.Id == userId).Select(u => u.UserName!).FirstAsync();
    }

    private Task<int> ProcessAsync() => app.Services.GetRequiredService<PushWorker>().ProcessAsync(CancellationToken.None);

    // ---------------------------------------------------------------- RFC 8291 encryption

    [Fact]
    public void Encryption_matches_the_RFC_8291_appendix_A_test_vector()
    {
        // RFC 8291, Appendix A (and the message in section 5).
        var plaintext = B("V2hlbiBJIGdyb3cgdXAsIEkgd2FudCB0byBiZSBhIHdhdGVybWVsb24");
        Assert.Equal("When I grow up, I want to be a watermelon", Encoding.ASCII.GetString(plaintext));
        var asPrivate = B("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw");
        var asPublic = B("BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8");
        var uaPrivate = B("q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94");
        var uaPublic = B("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4");
        var auth = B("BTBZMqHH6r4Tts7J_aSIgg");
        var salt = B("DGv6ra1nlYgDCS1FRnbzlw");
        const string expected = "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27ml"
                              + "mlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPT"
                              + "pK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN";

        using var asKey = WebPushCrypto.ImportPrivateKey(asPrivate, asPublic);
        var body = WebPushCrypto.Encrypt(plaintext, uaPublic, auth, asKey, salt);
        Assert.Equal(expected, WebPushCrypto.Base64Url(body));

        // And the user agent side decrypts it back.
        using var uaKey = WebPushCrypto.ImportPrivateKey(uaPrivate, uaPublic);
        Assert.Equal(plaintext, WebPushCrypto.Decrypt(body, uaKey, auth));
    }

    [Fact]
    public void Each_message_uses_a_fresh_key_and_salt_and_decrypts()
    {
        using var browser = push.NewBrowser();
        var a = WebPushCrypto.Encrypt("hello"u8, B(browser.P256dh), browser.Auth);
        var b = WebPushCrypto.Encrypt("hello"u8, B(browser.P256dh), browser.Auth);
        Assert.NotEqual(a[..16], b[..16]); // salt
        Assert.NotEqual(a[21..86], b[21..86]); // ephemeral key
        Assert.Equal("hello", Encoding.UTF8.GetString(browser.Decrypt(a)));
        // Another browser's auth secret can't decrypt it.
        using var other = push.NewBrowser();
        Assert.ThrowsAny<CryptographicException>(() => WebPushCrypto.Decrypt(a, browser.Key, other.Auth));
    }

    // ---------------------------------------------------------------- VAPID

    [Fact]
    public void Vapid_jwt_has_the_RFC_8292_shape_and_a_valid_ES256_signature()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var exp = DateTimeOffset.UtcNow.AddHours(12);
        var jwt = VapidKeys.CreateJwt(key, "https://push.example.net", exp, "mailto:ops@example.com");
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        using var header = JsonDocument.Parse(B(parts[0]));
        Assert.Equal("ES256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.RootElement.GetProperty("typ").GetString());
        using var claims = JsonDocument.Parse(B(parts[1]));
        Assert.Equal("https://push.example.net", claims.RootElement.GetProperty("aud").GetString());
        Assert.Equal(exp.ToUnixTimeSeconds(), claims.RootElement.GetProperty("exp").GetInt64());
        Assert.Equal("mailto:ops@example.com", claims.RootElement.GetProperty("sub").GetString());
        var signature = B(parts[2]);
        Assert.Equal(64, signature.Length); // r | s
        Assert.True(key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        // A changed claim no longer verifies.
        var forged = WebPushCrypto.Base64Url(Encoding.UTF8.GetBytes("{\"aud\":\"https://evil.example\",\"exp\":1,\"sub\":\"x\"}"));
        Assert.False(key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + forged), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public async Task The_vapid_public_key_is_generated_once_stored_encrypted_and_served()
    {
        var client = app.CreateClient();
        var first = await client.GetFromJsonAsync<JsonElement>("/push/vapid-public-key");
        var second = await client.GetFromJsonAsync<JsonElement>("/push/vapid-public-key");
        var key = first.GetProperty("publicKey").GetString()!;
        Assert.Equal(key, second.GetProperty("publicKey").GetString());
        var bytes = B(key);
        Assert.Equal(65, bytes.Length);
        Assert.Equal(0x04, bytes[0]);
        await using var db = app.Db();
        var stored = await db.SiteSettings.FirstAsync(s => s.Key == VapidKeys.PrivateKeySetting);
        // Stored as Data Protection ciphertext: readable only with the purpose, and it is the pair of the served key.
        var dp = app.Services.GetRequiredService<IDataProtectionProvider>();
        Assert.ThrowsAny<CryptographicException>(() => dp.CreateProtector("GitBench.Something.Else.v1").Unprotect(stored.Value));
        using var privateKey = ECDsa.Create();
        privateKey.ImportPkcs8PrivateKey(Convert.FromBase64String(dp.CreateProtector(VapidKeys.Purpose).Unprotect(stored.Value)), out _);
        Assert.Equal(key, WebPushCrypto.Base64Url(WebPushCrypto.PublicKeyBytes(privateKey)));
    }

    // ---------------------------------------------------------------- end to end through a fake push service

    [Fact]
    public async Task A_notification_is_pushed_encrypted_with_vapid_and_the_browser_can_decrypt_it()
    {
        var user = await NewUserAsync();
        using var browser = push.NewBrowser();
        await SubscribeAsync(user.Id, browser);
        var notificationId = await AssignIssueAsync(user.Id);

        Assert.Equal(1, await ProcessAsync());
        var request = Assert.Single(push.Requests);
        Assert.Equal(new Uri(browser.Endpoint).AbsolutePath, request.Path);
        Assert.Equal("aes128gcm", request.Headers["Content-Encoding"]);
        Assert.Equal("normal", request.Headers["Urgency"]);
        Assert.Equal("86400", request.Headers["TTL"]);

        // VAPID: "vapid t=<jwt>, k=<server public key>", the JWT signed by that key for this push service's origin.
        var m = Regex.Match(request.Headers["Authorization"], "^vapid t=([^,]+), k=(.+)$");
        Assert.True(m.Success, request.Headers["Authorization"]);
        var serverKey = (await app.CreateClient().GetFromJsonAsync<JsonElement>("/push/vapid-public-key")).GetProperty("publicKey").GetString();
        Assert.Equal(serverKey, m.Groups[2].Value);
        var parts = m.Groups[1].Value.Split('.');
        var point = B(m.Groups[2].Value);
        using var verifier = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = point[1..33], Y = point[33..] } });
        Assert.True(verifier.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), B(parts[2]), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        using var claims = JsonDocument.Parse(B(parts[1]));
        Assert.Equal(push.BaseUrl, claims.RootElement.GetProperty("aud").GetString());
        Assert.Equal("mailto:ops@push.test", claims.RootElement.GetProperty("sub").GetString());

        // The browser decrypts it: title, a short body, a same-origin path. Never the description.
        var json = Encoding.UTF8.GetString(browser.Decrypt(request.Body));
        using var payload = JsonDocument.Parse(json);
        Assert.EndsWith("Push me", payload.RootElement.GetProperty("title").GetString());
        var body = payload.RootElement.GetProperty("body").GetString()!;
        Assert.True(body.Length <= 120);
        Assert.Contains("assigned you", body);
        Assert.StartsWith("/p/", payload.RootElement.GetProperty("url").GetString());
        Assert.DoesNotContain("SECRET-DESCRIPTION", json);

        await using var db = app.Db();
        Assert.False(await db.Notifications.Where(n => n.Id == notificationId).Select(n => n.PushPending).FirstAsync());
        Assert.NotNull((await db.WebPushSubscriptions.SingleAsync(s => s.UserId == user.Id)).LastUsedAt);
        // Delivered once: another pass sends nothing.
        Assert.Equal(0, await ProcessAsync());
        Assert.Single(push.Requests);
    }

    [Fact]
    public async Task The_payload_never_contains_the_excerpt()
    {
        var n = new Notification
        {
            Id = 7, Kind = NotificationKind.IssueCommented, Title = "WEB-1 Title", Url = "/p/WEB/issues/1",
            Excerpt = "private comment body", Actor = new User { DisplayName = new string('A', 300) },
        };
        var json = Encoding.UTF8.GetString(PushWorker.Payload(n));
        Assert.DoesNotContain("private comment body", json);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("body").GetString()!.Length <= 120);
        // A URL that isn't a local path falls back to the notifications page.
        n.Url = "//evil.example/x";
        Assert.Contains("\"/notifications\"", Encoding.UTF8.GetString(PushWorker.Payload(n)));
    }

    [Fact]
    public async Task Users_without_push_or_with_the_group_unselected_get_no_push()
    {
        var off = await NewUserAsync();
        using var browser = push.NewBrowser();
        await SubscribeAsync(off.Id, browser);
        await using (var s = app.Scope()) await s.Get<PushSubscriptions>().SaveSettingsAsync(off.Id, false, PushGroupMask.Defaults);
        await AssignIssueAsync(off.Id);

        var noGroup = await NewUserAsync();
        using var browser2 = push.NewBrowser();
        await SubscribeAsync(noGroup.Id, browser2);
        await using (var s = app.Scope()) await s.Get<PushSubscriptions>().SaveSettingsAsync(noGroup.Id, true, [NotificationGroup.Mentioned]);
        await AssignIssueAsync(noGroup.Id);

        await using var db = app.Db();
        Assert.False(await db.Notifications.AnyAsync(n => (n.UserId == off.Id || n.UserId == noGroup.Id) && n.PushPending));
        await ProcessAsync();
        Assert.Empty(push.Requests);
    }

    [Fact]
    public async Task The_queue_never_blocks_the_request_that_created_the_notification()
    {
        push.Delay = TimeSpan.FromSeconds(5); // a slow push service
        var user = await NewUserAsync();
        using var browser = push.NewBrowser();
        await SubscribeAsync(user.Id, browser);

        var sw = Stopwatch.StartNew();
        var id = await AssignIssueAsync(user.Id);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"Creating the issue took {sw.Elapsed}");
        Assert.Empty(push.Requests); // nothing was sent in the request
        await using (var db = app.Db()) Assert.True(await db.Notifications.Where(n => n.Id == id).Select(n => n.PushPending).FirstAsync());

        Assert.Equal(1, await ProcessAsync()); // the worker sends it
        Assert.Single(push.Requests);
    }

    // ---------------------------------------------------------------- push service answers

    [Theory]
    [InlineData(410)]
    [InlineData(404)]
    public async Task Gone_subscriptions_are_pruned(int status)
    {
        push.Status = status;
        var user = await NewUserAsync();
        using var browser = push.NewBrowser();
        await SubscribeAsync(user.Id, browser);
        await AssignIssueAsync(user.Id);
        await ProcessAsync();
        await using var db = app.Db();
        Assert.False(await db.WebPushSubscriptions.AnyAsync(s => s.UserId == user.Id));
    }

    [Fact]
    public async Task Five_consecutive_failures_prune_a_subscription()
    {
        push.Status = 500;
        var user = await NewUserAsync();
        using var browser = push.NewBrowser();
        await SubscribeAsync(user.Id, browser);
        for (var i = 1; i <= PushWorker.MaxFailures; i++)
        {
            await AssignIssueAsync(user.Id);
            await ProcessAsync();
            await using var db = app.Db();
            var sub = await db.WebPushSubscriptions.FirstOrDefaultAsync(s => s.UserId == user.Id);
            if (i < PushWorker.MaxFailures) Assert.Equal(i, sub!.FailureCount);
            else Assert.Null(sub);
        }
    }

    [Fact]
    public async Task Rate_limited_subscriptions_wait_for_retry_after()
    {
        push.Status = 429;
        push.RetryAfter = "120";
        var user = await NewUserAsync();
        using var browser = push.NewBrowser();
        await SubscribeAsync(user.Id, browser);
        await AssignIssueAsync(user.Id);
        await ProcessAsync();
        Assert.Single(push.Requests);
        await using (var db = app.Db())
        {
            var sub = await db.WebPushSubscriptions.SingleAsync(s => s.UserId == user.Id);
            Assert.NotNull(sub.RetryAfterUntil);
            Assert.InRange(sub.RetryAfterUntil!.Value, DateTime.UtcNow.AddSeconds(100), DateTime.UtcNow.AddSeconds(130));
            Assert.Equal(0, sub.FailureCount);
        }
        // During the pause nothing is sent to it.
        push.Status = 201;
        await AssignIssueAsync(user.Id);
        await ProcessAsync();
        Assert.Single(push.Requests);
    }

    // ---------------------------------------------------------------- SSRF and validation

    private static PushEndpointGuard StrictGuard(params string[] suffixes) =>
        new(new StaticOptions<PushOptions>(new PushOptions { AllowPrivateNetworks = false, AllowedHostSuffixes = suffixes }),
            new StaticOptions<WebhookOptions>(new WebhookOptions { AllowPrivateNetworks = false }));

    [Theory]
    [InlineData("http://push.example.com/send/1", "https")]
    [InlineData("https://user:secret@push.example.com/send/1", "credentials")]
    [InlineData("https://push.example.com/send/1#frag", "fragment")]
    [InlineData("https://10.0.0.5/send/1", "private")]
    [InlineData("https://192.168.1.10/send/1", "private")]
    [InlineData("https://169.254.169.254/latest/meta-data", "private")]
    [InlineData("https://[fd00::1]/send", "private")]
    [InlineData("https://127.0.0.1/send/1", "local")]
    [InlineData("https://localhost/send/1", "local")]
    [InlineData("http://127.0.0.1:9/send/1", "https")]
    [InlineData("ftp://push.example.com/x", "https")]
    [InlineData("not a url", "spaces")]
    public void The_guard_refuses_unsafe_endpoints(string endpoint, string reason)
    {
        var error = StrictGuard().Validate(endpoint);
        Assert.NotNull(error);
        Assert.Contains(reason, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_guard_checks_resolved_addresses_and_allowed_hosts()
    {
        var guard = StrictGuard();
        Assert.Null(guard.Validate("https://push.example.com/send/1"));
        await Assert.ThrowsAsync<PushBlockedException>(() => guard.ResolveAsync("127.0.0.1", CancellationToken.None));
        Assert.Contains("private", await guard.ValidateResolvedAsync("https://[::ffff:10.1.2.3]/x", CancellationToken.None) ?? "", StringComparison.OrdinalIgnoreCase);

        var restricted = StrictGuard("push.services.mozilla.com", ".googleapis.com");
        Assert.Null(restricted.Validate("https://updates.push.services.mozilla.com/wpush/v2/abc"));
        Assert.Null(restricted.Validate("https://fcm.googleapis.com/fcm/send/abc"));
        Assert.NotNull(restricted.Validate("https://evil-googleapis.com/x"));
        Assert.NotNull(restricted.Validate("https://push.example.com/x"));
    }

    [Fact]
    public async Task Subscriptions_to_private_addresses_are_refused_and_the_sender_never_connects()
    {
        var user = await NewUserAsync();
        var guard = StrictGuard();
        var subs = new PushSubscriptions(app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>(),
            app.Services.GetRequiredService<IDataProtectionProvider>(), guard, TimeProvider.System);
        using var browser = push.NewBrowser(); // http://127.0.0.1:port
        Assert.NotNull(await subs.AddAsync(user.Id, browser.Endpoint, browser.P256dh, browser.AuthText, null));
        Assert.NotNull(await subs.AddAsync(user.Id, "https://10.0.0.5/x", browser.P256dh, browser.AuthText, null));
        await using (var db = app.Db()) Assert.False(await db.WebPushSubscriptions.AnyAsync(s => s.UserId == user.Id));

        // The sender re-checks the endpoint and the connection's addresses.
        using var sender = new PushSender(guard, app.Services.GetRequiredService<VapidKeys>(),
            new StaticOptions<PushOptions>(new PushOptions()), TimeProvider.System);
        var r = await sender.SendAsync(browser.Endpoint.Replace("http://", "https://"), B(browser.P256dh), browser.Auth, "x"u8.ToArray(), CancellationToken.None);
        Assert.Equal(PushOutcome.Failed, r.Outcome);
        Assert.Empty(push.Requests);
    }

    [Fact]
    public async Task Invalid_keys_are_refused()
    {
        var user = await NewUserAsync();
        using var browser = push.NewBrowser();
        await using var s = app.Scope();
        var subs = s.Get<PushSubscriptions>();
        Assert.NotNull(await subs.AddAsync(user.Id, browser.Endpoint, "not-a-key", browser.AuthText, null));
        Assert.NotNull(await subs.AddAsync(user.Id, browser.Endpoint, WebPushCrypto.Base64Url(new byte[65]), browser.AuthText, null)); // not on the curve
        Assert.NotNull(await subs.AddAsync(user.Id, browser.Endpoint, browser.P256dh, WebPushCrypto.Base64Url(new byte[8]), null));
    }

    [Fact]
    public async Task At_most_ten_subscriptions_per_user_and_keys_are_stored_encrypted()
    {
        var user = await NewUserAsync();
        var browsers = Enumerable.Range(0, PushSubscriptions.MaxPerUser + 1).Select(_ => push.NewBrowser()).ToList();
        try
        {
            await using var s = app.Scope();
            var subs = s.Get<PushSubscriptions>();
            foreach (var b in browsers.Take(PushSubscriptions.MaxPerUser))
                Assert.Null(await subs.AddAsync(user.Id, b.Endpoint, b.P256dh, b.AuthText, null));
            var error = await subs.AddAsync(user.Id, browsers[^1].Endpoint, browsers[^1].P256dh, browsers[^1].AuthText, null);
            Assert.Contains("10", error);
            // Re-subscribing an existing browser is still fine.
            Assert.Null(await subs.AddAsync(user.Id, browsers[0].Endpoint, browsers[0].P256dh, browsers[0].AuthText, null));

            await using var db = app.Db();
            var rows = await db.WebPushSubscriptions.Where(x => x.UserId == user.Id).ToListAsync();
            Assert.Equal(PushSubscriptions.MaxPerUser, rows.Count);
            Assert.DoesNotContain(rows, r => r.EncryptedP256dh.Contains(browsers[0].P256dh) || r.EncryptedAuth.Contains(browsers[0].AuthText));
        }
        finally { browsers.ForEach(b => b.Dispose()); }
    }

    // ---------------------------------------------------------------- HTTP endpoints

    [Fact]
    public async Task Subscribing_requires_sign_in_and_an_antiforgery_token()
    {
        using var browser = push.NewBrowser();
        var body = new { endpoint = browser.Endpoint, keys = new { p256dh = browser.P256dh, auth = browser.AuthText } };

        var anonymous = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/push/subscriptions", body)).StatusCode);

        var user = await NewUserAsync();
        var client = await app.CookieClientAsync(user.UserName!);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/push/subscriptions", body)).StatusCode); // no token

        var html = await client.GetStringAsync("/account/profile");
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        var request = new HttpRequestMessage(HttpMethod.Post, "/push/subscriptions") { Content = JsonContent.Create(body) };
        request.Headers.Add("RequestVerificationToken", token);
        var ok = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        await using (var db = app.Db())
        {
            var row = await db.WebPushSubscriptions.SingleAsync(s => s.UserId == user.Id);
            Assert.Equal(browser.Endpoint, row.Endpoint);
            Assert.True((await db.UserNotificationSettings.SingleAsync(s => s.UserId == user.Id)).PushEnabled);
        }

        var delete = new HttpRequestMessage(HttpMethod.Delete, "/push/subscriptions") { Content = JsonContent.Create(new { endpoint = browser.Endpoint }) };
        delete.Headers.Add("RequestVerificationToken", token);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(delete)).StatusCode);
        await using (var db = app.Db()) Assert.False(await db.WebPushSubscriptions.AnyAsync(s => s.UserId == user.Id));
    }

    [Fact]
    public async Task Resubscribe_swaps_the_old_endpoint_for_the_new_one_and_needs_sign_in_and_the_header()
    {
        using var oldBrowser = push.NewBrowser();
        using var newBrowser = push.NewBrowser();
        using var otherBrowser = push.NewBrowser();
        var user = await NewUserAsync();
        var other = await NewUserAsync();
        await SubscribeAsync(user.Id, oldBrowser);
        await SubscribeAsync(other.Id, otherBrowser);
        object Body(string? old) => new { oldEndpoint = old, subscription = new { endpoint = newBrowser.Endpoint, keys = new { p256dh = newBrowser.P256dh, auth = newBrowser.AuthText } } };
        HttpRequestMessage Req(string? old, bool header = true)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, "/push/resubscribe") { Content = JsonContent.Create(Body(old)) };
            if (header) r.Headers.Add("X-ByteBench-Push", "resubscribe");
            return r;
        }

        var anonymous = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(Req(oldBrowser.Endpoint))).StatusCode);

        var client = await app.CookieClientAsync(user.UserName!);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Req(oldBrowser.Endpoint, header: false))).StatusCode);
        var cross = Req(oldBrowser.Endpoint);
        cross.Headers.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(cross)).StatusCode);
        await using (var db = app.Db()) Assert.Equal(oldBrowser.Endpoint, (await db.WebPushSubscriptions.SingleAsync(s => s.UserId == user.Id)).Endpoint);

        // Naming someone else's endpoint as "old" never removes it.
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Req(otherBrowser.Endpoint))).StatusCode);
        await using (var db = app.Db()) Assert.True(await db.WebPushSubscriptions.AnyAsync(s => s.UserId == other.Id && s.Endpoint == otherBrowser.Endpoint));

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Req(oldBrowser.Endpoint))).StatusCode);
        await using (var db = app.Db())
        {
            var endpoints = await db.WebPushSubscriptions.Where(s => s.UserId == user.Id).Select(s => s.Endpoint).ToListAsync();
            Assert.Equal([newBrowser.Endpoint], endpoints);
        }
    }

    [Fact]
    public async Task The_service_worker_handles_pushsubscriptionchange()
    {
        var text = await app.CreateClient().GetStringAsync("/sw.js");
        Assert.Contains("'pushsubscriptionchange'", text);
        Assert.Contains("/push/resubscribe", text);
        Assert.Contains("applicationServerKey", text);
    }

    [Fact]
    public async Task The_service_worker_and_script_are_served_from_the_site_root()
    {
        var client = app.CreateClient();
        var sw = await client.GetAsync("/sw.js");
        Assert.Equal(HttpStatusCode.OK, sw.StatusCode);
        Assert.Contains("javascript", sw.Content.Headers.ContentType?.MediaType);
        var text = await sw.Content.ReadAsStringAsync();
        Assert.Contains("showNotification", text);
        Assert.Contains("self.location.origin", text); // same-origin URLs only
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/js/push.js")).StatusCode);
    }
}
