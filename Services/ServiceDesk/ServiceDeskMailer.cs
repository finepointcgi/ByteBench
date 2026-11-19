using System.Net;
using System.Security.Cryptography;
using System.Text;
using ByteBench.Data;
using ByteBench.Services.Attachments;
using ByteBench.Services.Email;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.ServiceDesk;

/// <summary>
/// Emails to requesters, through the app's email queue (<see cref="EmailQueue"/>: pickup directory in Development). Each
/// one has a fresh Message-ID recorded on the ticket, In-Reply-To/References to the thread, and a Reply-To with the
/// ticket's reply token, so a reply lands back on the same issue. Only three things ever email a requester: the
/// auto-reply, a "reply to requester" comment, and a move into a status chosen in the settings.
/// </summary>
public sealed class ServiceDeskMailer(EmailQueue queue, NotificationEmailComposer links, ServiceDeskAddresses addresses)
{
    public const string DefaultAutoReply =
        "Hi {{requester}},\n\nThanks for getting in touch. We've received your request **{{subject}}** and filed it as **{{key}}**. " +
        "We'll get back to you as soon as we can.\n\nYou can add more information by replying to this email, and follow the request at {{tracking_url}}";

    public bool Enabled => queue.IsEnabled;

    public string TrackingUrl(ServiceDeskTicket t) => links.Absolute("/desk/t/" + t.TrackingToken);

    /// <summary>Fills an admin-written template. Values are Markdown-escaped, so a requester's name or subject can't inject links.</summary>
    public static string Fill(string template, IReadOnlyDictionary<string, string> values) =>
        values.Aggregate(template, (s, kv) => s.Replace("{{" + kv.Key + "}}", kv.Key is "tracking_url" or "body" ? kv.Value : AttachmentFormat.EscapeMarkdown(kv.Value)));

    public Task<bool> SendAutoReplyAsync(AppDbContext db, ServiceDeskTicket ticket, ServiceDeskContact contact, string issueKey, ServiceDeskSettings settings)
    {
        var body = Fill(string.IsNullOrWhiteSpace(settings.AutoReplyTemplate) ? DefaultAutoReply : settings.AutoReplyTemplate!,
            new Dictionary<string, string>
            {
                ["requester"] = contact.DisplayName, ["key"] = issueKey, ["subject"] = ticket.Subject, ["tracking_url"] = TrackingUrl(ticket),
            });
        return SendAsync(db, ticket, contact, $"[{issueKey}] {ticket.Subject}", body, null, autoSubmitted: "auto-replied");
    }

    public Task<bool> SendReplyAsync(AppDbContext db, ServiceDeskTicket ticket, ServiceDeskContact contact, string issueKey, Comment comment, string agentName) =>
        SendAsync(db, ticket, contact, $"Re: [{issueKey}] {ticket.Subject}", comment.Body, comment.Id, autoSubmitted: null, signature: agentName);

    public Task<bool> SendStatusAsync(AppDbContext db, ServiceDeskTicket ticket, ServiceDeskContact contact, string issueKey, string status) =>
        SendAsync(db, ticket, contact, $"[{issueKey}] {ticket.Subject}: {status}",
            $"Your request **{AttachmentFormat.EscapeMarkdown(ticket.Subject)}** ({issueKey}) is now **{AttachmentFormat.EscapeMarkdown(status)}**.",
            null, autoSubmitted: "auto-generated");

    private async Task<bool> SendAsync(AppDbContext db, ServiceDeskTicket ticket, ServiceDeskContact contact, string subject, string markdown,
        int? commentId, string? autoSubmitted, string? signature = null)
    {
        if (!queue.IsEnabled) return false;
        if (subject.Length > 200) subject = subject[..199] + "…";
        var tracking = TrackingUrl(ticket);
        var messageId = $"sd.{ticket.Id}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}@{addresses.Domain}";
        var thread = await db.ServiceDeskMessages.Where(m => m.TicketId == ticket.Id).OrderByDescending(m => m.Id)
            .Select(m => m.MessageId).Take(10).ToListAsync();

        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html><body style=\"margin:0;padding:24px;background:#f6f7f9;font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;color:#1f2328;\">");
        html.Append($"<div style=\"color:#8c959f;font-size:12px;margin-bottom:12px;\">{E(ServiceDeskMail.ReplyMarker)}</div>");
        html.Append("<div style=\"max-width:600px;background:#ffffff;border:1px solid #d0d7de;border-radius:8px;padding:20px;\">");
        html.Append(Display.Markdown(markdown).Value);
        if (signature != null) html.Append($"<p style=\"color:#656d76;\">— {E(signature)}</p>");
        html.Append("</div>");
        html.Append($"<p style=\"max-width:600px;color:#656d76;font-size:12px;\">Reply to this email to add a comment. <a href=\"{E(tracking)}\" style=\"color:#4f46e5;\">View your request</a>.</p>");
        html.Append("</body></html>");

        var text = new StringBuilder();
        text.Append(ServiceDeskMail.ReplyMarker).Append("\n\n").Append(markdown.Trim()).Append("\n\n");
        if (signature != null) text.Append("— ").Append(signature).Append("\n\n");
        text.Append("--\nReply to this email to add a comment.\nView your request: ").Append(tracking).Append('\n');

        var headers = new Dictionary<string, string>
        {
            ["Message-Id"] = $"<{messageId}>",
            ["Reply-To"] = addresses.ForReply(ticket.ReplyToken),
            ["X-Auto-Response-Suppress"] = "All", // ask Exchange not to send out-of-office replies back
        };
        if (autoSubmitted != null) headers["Auto-Submitted"] = autoSubmitted;
        if (thread.Count > 0)
        {
            headers["In-Reply-To"] = $"<{thread[0]}>";
            headers["References"] = string.Join(' ', thread.AsEnumerable().Reverse().Select(id => $"<{id}>"));
        }

        if (!queue.Enqueue(new EmailMessage(contact.Email, contact.Name, subject, html.ToString(), text.ToString()) { Headers = headers })) return false;
        db.ServiceDeskMessages.Add(new ServiceDeskMessage { TicketId = ticket.Id, Direction = ServiceDeskDirection.Outbound, MessageId = messageId, CommentId = commentId });
        await db.SaveChangesAsync();
        return true;
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
}
