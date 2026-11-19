using MimeKit;

namespace GitBench.Services.Email;

/// <summary>One email, with an HTML and a plain-text body. Bodies are complete and already escaped.</summary>
public sealed record EmailMessage(string To, string? ToName, string Subject, string Html, string Text)
{
    /// <summary>Extra headers, e.g. <c>List-Unsubscribe</c>.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Delivers one message, synchronously from the caller's point of view. App code doesn't call this directly: it
/// enqueues on <see cref="EmailQueue"/>, and <see cref="EmailWorker"/> calls the sender with retries.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

internal static class MimeBuilder
{
    public static MimeMessage Build(EmailMessage m, string from)
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(from));
        msg.To.Add(new MailboxAddress(m.ToName ?? "", m.To));
        msg.Subject = m.Subject;
        msg.Date = DateTimeOffset.UtcNow;
        foreach (var (name, value) in m.Headers)
        {
            // Threading headers (service desk replies) replace MimeKit's own rather than being added twice.
            if (name.Equals("Message-Id", StringComparison.OrdinalIgnoreCase)) msg.MessageId = value.Trim('<', '>', ' ');
            else if (name.Equals("In-Reply-To", StringComparison.OrdinalIgnoreCase)) msg.InReplyTo = value.Trim('<', '>', ' ');
            else if (name.Equals("References", StringComparison.OrdinalIgnoreCase))
                foreach (var id in value.Split(' ', StringSplitOptions.RemoveEmptyEntries)) msg.References.Add(id.Trim('<', '>'));
            else if (name.Equals("Reply-To", StringComparison.OrdinalIgnoreCase)) msg.ReplyTo.Add(MailboxAddress.Parse(value));
            else msg.Headers.Add(name, value);
        }
        msg.Body = new BodyBuilder { HtmlBody = m.Html, TextBody = m.Text }.ToMessageBody();
        return msg;
    }
}
