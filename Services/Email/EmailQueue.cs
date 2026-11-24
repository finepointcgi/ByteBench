using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Email;

/// <summary>
/// In-memory outbox. <see cref="Enqueue"/> never blocks the request; <see cref="EmailWorker"/> sends in the background.
/// Messages queued but not yet sent are lost on restart (notification emails are only marked sent once queued, so
/// at worst a restart drops a few, it never duplicates them).
/// </summary>
public sealed class EmailQueue(IOptions<EmailOptions> options, ILogger<EmailQueue> log)
{
    public const int Capacity = 1000;

    private readonly Channel<EmailMessage> channel = Channel.CreateBounded<EmailMessage>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    internal ChannelReader<EmailMessage> Reader => channel.Reader;

    public bool IsEnabled => options.Value.Enabled;

    /// <summary>Queues <paramref name="message"/>. Returns false (and logs) when email is disabled or the queue is full.</summary>
    public bool Enqueue(EmailMessage message)
    {
        if (!options.Value.Enabled)
        {
            log.LogInformation("Email is disabled (Email:Enabled=false); not sending \"{Subject}\"", message.Subject);
            return false;
        }
        if (channel.Writer.TryWrite(message)) return true;
        log.LogWarning("Email queue is full; dropped \"{Subject}\"", message.Subject);
        return false;
    }
}

/// <summary>Drains <see cref="EmailQueue"/> one message at a time, retrying failures with backoff.</summary>
public sealed class EmailWorker(EmailQueue queue, IEmailSender sender, ILogger<EmailWorker> log) : BackgroundService
{
    /// <summary>Waits before retries 1, 2 and 3.</summary>
    public static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            WorkerPulse.Report("email", true);
            await foreach (var message in queue.Reader.ReadAllAsync(stoppingToken))
            {
                WorkerPulse.Tick("email");
                try { await SendWithRetriesAsync(message, stoppingToken); WorkerPulse.Ok("email"); }
                catch (Exception ex) when (ex is not OperationCanceledException) { WorkerPulse.Fail("email", ex); throw; }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task SendWithRetriesAsync(EmailMessage message, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await sender.SendAsync(message, ct);
                log.LogInformation("Sent email \"{Subject}\" to {To} via {Sender}", message.Subject, Mask(message.To), sender.GetType().Name);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (attempt < Backoff.Length)
            {
                log.LogWarning(ex, "Sending email \"{Subject}\" to {To} failed (attempt {Attempt}); retrying in {Delay}",
                    message.Subject, Mask(message.To), attempt + 1, Backoff[attempt]);
                await Task.Delay(Backoff[attempt], ct);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Giving up on email \"{Subject}\" to {To} after {Attempts} attempts", message.Subject, Mask(message.To), attempt + 1);
                return;
            }
        }
    }

    /// <summary>"alice@example.com" → "a***@example.com", so logs don't collect addresses.</summary>
    internal static string Mask(string address)
    {
        var at = address.IndexOf('@');
        return at <= 0 ? "***" : address[0] + "***" + address[at..];
    }
}
