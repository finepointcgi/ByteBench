using System.Security.Cryptography;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Options;

namespace GitBench.Services.ServiceDesk;

/// <summary>
/// Somewhere inbound service desk mail comes from. Pull sources (IMAP) are polled by <see cref="InboundMailPoller"/>:
/// each <see cref="PollAsync"/> hands every new message to <see cref="ServiceDeskIntake"/> and marks it done. Push sources
/// (the signed webhook) process messages as they arrive and have nothing to poll.
/// </summary>
public interface IInboundMailSource
{
    string Name { get; }
    bool Enabled { get; }
    /// <summary>Fetches and processes new messages; returns how many were handed to the intake.</summary>
    Task<int> PollAsync(ServiceDeskIntake intake, CancellationToken ct);
}

/// <summary>
/// Polls an IMAP mailbox (<c>ServiceDesk:Imap:*</c>, off by default) with MailKit: unseen messages in the folder are
/// processed, then flagged seen (or deleted with <c>DeleteProcessed</c>). A message that fails to process is left unseen
/// and retried next poll.
/// </summary>
public sealed class ImapMailSource(IOptions<ServiceDeskOptions> options, ILogger<ImapMailSource> log) : IInboundMailSource
{
    public string Name => "imap";

    public bool Enabled => options.Value.Imap is { Enabled: true, Host.Length: > 0 };

    public async Task<int> PollAsync(ServiceDeskIntake intake, CancellationToken ct)
    {
        var o = options.Value;
        var imap = o.Imap;
        using var client = new ImapClient();
        await client.ConnectAsync(imap.Host!, imap.Port, imap.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable, ct);
        if (!string.IsNullOrEmpty(imap.User)) await client.AuthenticateAsync(imap.User, imap.Password ?? "", ct);
        var folder = await client.GetFolderAsync(imap.Folder, ct);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        var uids = await folder.SearchAsync(SearchQuery.NotSeen, ct);
        var count = 0;
        foreach (var uid in uids.Take(100))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var size = (await folder.FetchAsync([uid], MessageSummaryItems.Size, ct)).FirstOrDefault()?.Size ?? 0;
                if (size > o.MaxMessageBytes)
                    log.LogWarning("Service desk: skipping IMAP message {Uid} ({Size} bytes, over ServiceDesk:MaxMessageBytes)", uid, size);
                else
                {
                    var message = await folder.GetMessageAsync(uid, ct);
                    var result = await intake.ProcessAsync(message, Name, ct);
                    log.LogDebug("Service desk: IMAP message {Uid}: {Outcome} {Reason}", uid, result.Outcome, result.Reason);
                    count++;
                }
                if (imap.DeleteProcessed) await folder.AddFlagsAsync(uid, MessageFlags.Deleted | MessageFlags.Seen, true, ct);
                else await folder.AddFlagsAsync(uid, MessageFlags.Seen, true, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Service desk: processing IMAP message {Uid} failed; it will be retried", uid);
            }
        }
        if (imap.DeleteProcessed) await folder.ExpungeAsync(ct);
        await client.DisconnectAsync(true, ct);
        return count;
    }
}

/// <summary>
/// <c>POST /service-desk/inbound</c>: for mail providers that forward each message as raw MIME (the request body). The
/// request must carry <c>X-GitBench-Timestamp</c> (Unix seconds) and <c>X-GitBench-Signature: sha256=&lt;hex&gt;</c>,
/// the HMAC-SHA256 of <c>"{timestamp}.{body}"</c> keyed with <c>ServiceDesk:WebhookSecret</c>, compared in constant
/// time. Without a configured secret the endpoint answers 404.
/// </summary>
public sealed class WebhookMailSource(IOptions<ServiceDeskOptions> options, TimeProvider clock) : IInboundMailSource
{
    public const string SignatureHeader = "X-GitBench-Signature";
    public const string TimestampHeader = "X-GitBench-Timestamp";

    public string Name => "webhook";

    public bool Enabled => !string.IsNullOrEmpty(options.Value.WebhookSecret);

    /// <summary>Push source: messages are processed by the endpoint as they arrive.</summary>
    public Task<int> PollAsync(ServiceDeskIntake intake, CancellationToken ct) => Task.FromResult(0);

    /// <summary>The signature header value for <paramref name="body"/> (tests and senders).</summary>
    public static string Sign(string secret, string timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes(timestamp + ".");
        var data = new byte[prefix.Length + body.Length];
        prefix.CopyTo(data, 0);
        body.CopyTo(data.AsSpan(prefix.Length));
        return "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), data));
    }

    /// <summary>Checks the timestamp window and the signature (constant-time).</summary>
    public bool Verify(string? timestamp, string? signature, ReadOnlySpan<byte> body)
    {
        var secret = options.Value.WebhookSecret;
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signature)) return false;
        if (!long.TryParse(timestamp, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ts)) return false;
        var skew = Math.Abs(clock.GetUtcNow().ToUnixTimeSeconds() - ts);
        if (skew > Math.Max(1, options.Value.WebhookToleranceSeconds)) return false;
        var expected = Encoding.ASCII.GetBytes(Sign(secret, timestamp, body));
        var given = Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/service-desk/inbound", async (HttpContext ctx, WebhookMailSource source, ServiceDeskIntake intake, IOptions<ServiceDeskOptions> o,
            ILoggerFactory lf, CancellationToken ct) =>
        {
            // Plain-text bodies: an empty error response would be re-executed into the HTML not-found page.
            if (!source.Enabled) return Results.Text("Not found.", statusCode: StatusCodes.Status404NotFound);
            var max = o.Value.MaxMessageBytes;
            if (ctx.Request.ContentLength > max) return Results.Text("Message too large.", statusCode: StatusCodes.Status413PayloadTooLarge);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int n;
            while ((n = await ctx.Request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + n > max) return Results.Text("Message too large.", statusCode: StatusCodes.Status413PayloadTooLarge);
                buffer.Write(chunk, 0, n);
            }
            if (!source.Verify(ctx.Request.Headers[TimestampHeader], ctx.Request.Headers[SignatureHeader], buffer.GetBuffer().AsSpan(0, (int)buffer.Length)))
            {
                lf.CreateLogger("GitBench.ServiceDesk").LogWarning("Service desk webhook: bad or missing signature from {Ip}", ctx.Connection.RemoteIpAddress);
                return Results.Text("Invalid or missing signature.", statusCode: StatusCodes.Status401Unauthorized);
            }
            buffer.Position = 0;
            var result = await intake.ProcessAsync(buffer, source.Name, ct);
            // 200 for anything we handled (even ignored/refused mail) so the provider doesn't retry it; the outcome is in the body.
            return Results.Ok(new { outcome = result.Outcome.ToString().ToLowerInvariant(), reason = result.Reason, issueId = result.IssueId });
        }).AllowAnonymous().DisableAntiforgery();
    }
}

/// <summary>Polls every enabled pull source every <c>ServiceDesk:Imap:PollSeconds</c>. Idle when none is enabled.</summary>
public sealed class InboundMailPoller(IEnumerable<IInboundMailSource> sources, ServiceDeskIntake intake, IOptions<ServiceDeskOptions> options,
    ILogger<InboundMailPoller> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            await PollOnceAsync(stoppingToken);
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, options.Value.Imap.PollSeconds)), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One pass over the enabled sources. A failing source is logged and doesn't stop the others.</summary>
    public async Task<int> PollOnceAsync(CancellationToken ct)
    {
        var total = 0;
        foreach (var s in sources.Where(s => s.Enabled))
        {
            try { total += await s.PollAsync(intake, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { log.LogError(ex, "Service desk: polling {Source} failed", s.Name); }
        }
        return total;
    }
}
