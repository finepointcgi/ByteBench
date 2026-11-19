using System.Security.Cryptography;
using GitBench.Data;
using GitBench.Services.Attachments;
using GitBench.Services.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GitBench.Services.ServiceDesk;

public enum InboundOutcome { Created, Commented, Ignored, Rejected }

/// <summary>What happened to one inbound message or form submission.</summary>
public sealed record InboundResult(InboundOutcome Outcome, string? Reason = null, int? IssueId = null, string? TrackingToken = null)
{
    public bool Accepted => Outcome is InboundOutcome.Created or InboundOutcome.Commented;
}

/// <summary>
/// Turns inbound mail and public-form submissions into issues and comments. Every call runs in its own DI scope acting as
/// the system on behalf of the <see cref="ServiceDeskBot"/> (requesters have no account), so permission checks in
/// <see cref="IssueService"/> and <see cref="AttachmentService"/> pass for the bot only.
/// <para>
/// Mail: duplicates (same Message-ID) are dropped; auto-replies, bounces, list mail and our own mail are ignored (loop
/// protection); senders over <c>ServiceDesk:MaxMessagesPerSenderPerHour</c>, blocked senders and spam are refused. A
/// message to a reply address (<c>desk+r-&lt;token&gt;@…</c>), or from the requester with In-Reply-To/References to the
/// thread, becomes a comment; anything else to a project's address opens a new issue.
/// </para>
/// </summary>
public sealed class ServiceDeskIntake(IServiceScopeFactory scopes, IDbContextFactory<AppDbContext> dbFactory, ServiceDeskAddresses addresses,
    ServiceDeskRateLimiter limiter, IOptions<ServiceDeskOptions> options, AttachmentStorage storage, ILogger<ServiceDeskIntake> log)
{
    private ServiceDeskOptions O => options.Value;

    private sealed record Ctx(AppDbContext Db, int BotId, ProjectAccess Access, IssueService Issues, AttachmentService Attachments, AuditLog Audit, ServiceDeskMailer Mailer);

    private async Task<T> RunAsync<T>(Func<Ctx, Task<T>> work, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var bot = await ServiceDeskBot.EnsureAsync(db, ct);
        sp.GetRequiredService<ProjectAccess>().ActAsSystem(bot);
        return await work(new Ctx(db, bot, sp.GetRequiredService<ProjectAccess>(), sp.GetRequiredService<IssueService>(), sp.GetRequiredService<AttachmentService>(),
            sp.GetRequiredService<AuditLog>(), sp.GetRequiredService<ServiceDeskMailer>()));
    }

    // =====================================================================
    // Mail
    // =====================================================================

    /// <summary>Parses raw MIME (at most <c>ServiceDesk:MaxMessageBytes</c>) and processes it.</summary>
    public async Task<InboundResult> ProcessAsync(Stream raw, string source, CancellationToken ct = default)
    {
        MimeMessage message;
        try { message = await MimeMessage.LoadAsync(raw, ct); }
        catch (FormatException) { return new(InboundOutcome.Rejected, "unparseable message"); }
        return await ProcessAsync(message, source, ct);
    }

    public Task<InboundResult> ProcessAsync(MimeMessage m, string source, CancellationToken ct = default) => RunAsync(async c =>
    {
        var db = c.Db;
        var from = m.From.Mailboxes.FirstOrDefault();
        var sender = from?.Address?.Trim().ToLowerInvariant() ?? "";
        var messageId = string.IsNullOrWhiteSpace(m.MessageId) ? null : m.MessageId.Trim('<', '>', ' ');
        if (messageId is { Length: > 500 }) messageId = messageId[..500];

        async Task<InboundResult> Drop(InboundOutcome outcome, string reason, int? projectId = null)
        {
            log.LogInformation("Service desk {Outcome} mail via {Source}: {Reason}", outcome, source, reason);
            await c.Audit.RecordAsync(outcome == InboundOutcome.Ignored ? "service_desk.mail_ignored" : "service_desk.mail_rejected",
                "service_desk", null, projectId, new { reason, from = sender, subject = Clip(m.Subject, 200), messageId, source }, actorId: c.BotId);
            return new(outcome, reason);
        }

        if (messageId != null && await db.ServiceDeskMessages.AnyAsync(x => x.MessageId == messageId, ct))
            return new InboundResult(InboundOutcome.Ignored, "duplicate"); // already processed (redelivery): no audit noise
        if (ServiceDeskMail.LoopReason(m, addresses) is { } loop) return await Drop(InboundOutcome.Ignored, loop);
        if (!System.Net.Mail.MailAddress.TryCreate(sender, out _)) return await Drop(InboundOutcome.Rejected, "no sender address");

        // Where is it going: a ticket (reply token), a project (plus-address or custom address)?
        ServiceDeskTicket? ticket = null;
        Project? project = null;
        foreach (var rcpt in ServiceDeskMail.Recipients(m).Select(a => a.Trim().ToLowerInvariant()).Distinct())
        {
            var target = addresses.Parse(rcpt);
            if (target.ReplyToken is { } token)
            {
                ticket = await db.ServiceDeskTickets.Include(t => t.Contact).FirstOrDefaultAsync(t => t.ReplyToken == token, ct);
                if (ticket != null) { project = await db.Projects.FindAsync([ticket.ProjectId], ct); break; }
            }
            else if (target.ProjectKey is { } key && project == null)
                project = await db.Projects.FirstOrDefaultAsync(p => p.Key == key, ct);
            else if (target.IsNone && project == null && !addresses.IsOwn(rcpt))
            {
                var pid = await db.ServiceDeskSettings.Where(s => s.CustomAddress == rcpt).Select(s => (int?)s.ProjectId).FirstOrDefaultAsync(ct);
                if (pid != null) project = await db.Projects.FindAsync([pid.Value], ct);
            }
        }
        if (project == null) return await Drop(InboundOutcome.Rejected, "no matching project");
        var settings = await db.ServiceDeskSettings.FirstOrDefaultAsync(s => s.ProjectId == project.Id, ct);
        if (settings is not { Enabled: true }) return await Drop(InboundOutcome.Rejected, "service desk disabled", project.Id);

        if (!limiter.TryAcquire("mail:" + sender, O.MaxMessagesPerSenderPerHour))
        {
            log.LogWarning("Service desk: {Sender} is over the hourly message limit; refusing mail", Email.EmailWorker.Mask(sender));
            return new InboundResult(InboundOutcome.Rejected, "rate limited");
        }

        var rawBody = ServiceDeskMail.Body(m);
        if (ServiceDeskMail.SpamReason(m, sender, rawBody, O) is { } spam) return await Drop(InboundOutcome.Rejected, spam, project.Id);

        // Threading without the token: In-Reply-To/References to a message of a ticket in this project, from its requester.
        if (ticket == null)
        {
            var refs = m.References.Select(r => r.Trim('<', '>')).Append(m.InReplyTo?.Trim('<', '>')).Where(r => !string.IsNullOrEmpty(r)).Distinct().ToList();
            if (refs.Count > 0)
                ticket = await db.ServiceDeskTickets.Include(t => t.Contact)
                    .Where(t => t.ProjectId == project.Id && t.Contact.Email == sender
                        && db.ServiceDeskMessages.Any(x => x.TicketId == t.Id && refs.Contains(x.MessageId)))
                    .OrderByDescending(t => t.Id).FirstOrDefaultAsync(ct);
        }

        var contact = await ContactAsync(db, sender, from?.Name, ct);
        var parts = ServiceDeskMail.AttachmentParts(m).ToList();
        if (ticket != null)
        {
            var body = Clip(ServiceDeskMail.StripQuotedReply(rawBody), O.MaxBodyChars);
            if (body.Length == 0 && parts.Count == 0) return await Drop(InboundOutcome.Ignored, "empty reply", project.Id);
            var comment = await AddRequesterCommentAsync(c, ticket, contact, body, parts, ct);
            if (messageId != null)
            {
                db.ServiceDeskMessages.Add(new ServiceDeskMessage { TicketId = ticket.Id, Direction = ServiceDeskDirection.Inbound, MessageId = messageId, CommentId = comment.Id });
                await db.SaveChangesAsync(ct);
            }
            await c.Audit.RecordAsync("service_desk.reply_received", "issue", ticket.IssueId, project.Id, new { from = sender, source }, actorId: c.BotId);
            return new InboundResult(InboundOutcome.Commented, null, ticket.IssueId);
        }

        var subject = string.IsNullOrWhiteSpace(m.Subject) ? "(no subject)" : m.Subject.Trim();
        var text = Clip(rawBody, O.MaxBodyChars);
        if (text.Length == 0 && parts.Count == 0 && string.IsNullOrWhiteSpace(m.Subject)) return await Drop(InboundOutcome.Ignored, "empty message", project.Id);
        var created = await CreateTicketAsync(c, project, settings, contact, subject, text, ServiceDeskSource.Email, parts, messageId, ct);
        return new InboundResult(InboundOutcome.Created, null, created.IssueId, created.TrackingToken);
    }, ct);

    // =====================================================================
    // Public form and tracking page
    // =====================================================================

    /// <summary>A request from the public form at <c>/desk/{KEY}</c>. <paramref name="clientIp"/> is rate limited.</summary>
    public Task<InboundResult> SubmitFormAsync(string projectKey, string name, string email, string subject, string body, string? clientIp, CancellationToken ct = default) => RunAsync(async c =>
    {
        var db = c.Db;
        var key = (projectKey ?? "").ToUpperInvariant();
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Key == key, ct);
        var settings = project == null ? null : await db.ServiceDeskSettings.FirstOrDefaultAsync(s => s.ProjectId == project.Id, ct);
        if (project == null || settings is not { Enabled: true, PublicFormEnabled: true }) return new InboundResult(InboundOutcome.Rejected, "not available");
        email = (email ?? "").Trim().ToLowerInvariant();
        if (!System.Net.Mail.MailAddress.TryCreate(email, out var parsed) || parsed.Address != email || email.Length > 254)
            return new InboundResult(InboundOutcome.Rejected, "Enter a valid email address.");
        if (addresses.IsOwn(email)) return new InboundResult(InboundOutcome.Rejected, "Enter your own email address.");
        if (!limiter.TryAcquire("form:" + (clientIp ?? "unknown"), O.MaxFormSubmissionsPerHour) || !limiter.TryAcquire("mail:" + email, O.MaxMessagesPerSenderPerHour))
            return new InboundResult(InboundOutcome.Rejected, "Too many requests. Please try again later.");
        if (ServiceDeskMail.SpamReason(email, subject + "\n" + body, O) is { } spam)
        {
            await c.Audit.RecordAsync("service_desk.form_rejected", "service_desk", null, project.Id, new { reason = spam, from = email }, actorId: c.BotId);
            return new InboundResult(InboundOutcome.Rejected, "Your request could not be accepted.");
        }
        var contact = await ContactAsync(db, email, name, ct);
        var created = await CreateTicketAsync(c, project, settings, contact, Clip(subject.Trim(), 200), Clip(body.Replace("\r\n", "\n").Trim(), O.MaxBodyChars),
            ServiceDeskSource.Form, [], null, ct);
        return new InboundResult(InboundOutcome.Created, null, created.IssueId, created.TrackingToken);
    }, ct);

    /// <summary>A reply typed on the tracking page: a comment from the ticket's requester.</summary>
    public Task<InboundResult> ReplyFromTrackingPageAsync(string trackingToken, string body, string? clientIp, CancellationToken ct = default) => RunAsync(async c =>
    {
        var ticket = await c.Db.ServiceDeskTickets.Include(t => t.Contact).FirstOrDefaultAsync(t => t.TrackingToken == trackingToken, ct);
        if (ticket == null) return new InboundResult(InboundOutcome.Rejected, "not found");
        var enabled = await c.Db.ServiceDeskSettings.AnyAsync(s => s.ProjectId == ticket.ProjectId && s.Enabled, ct);
        if (!enabled) return new InboundResult(InboundOutcome.Rejected, "This service desk is closed.");
        if (!limiter.TryAcquire("form:" + (clientIp ?? "unknown"), O.MaxFormSubmissionsPerHour))
            return new InboundResult(InboundOutcome.Rejected, "Too many requests. Please try again later.");
        body = Clip((body ?? "").Replace("\r\n", "\n").Trim(), O.MaxBodyChars);
        if (body.Length == 0) return new InboundResult(InboundOutcome.Rejected, "Write a message first.");
        if (ServiceDeskMail.SpamReason(ticket.Contact.Email, body, O) != null) return new InboundResult(InboundOutcome.Rejected, "Your message could not be accepted.");
        await AddRequesterCommentAsync(c, ticket, ticket.Contact, body, [], ct);
        await c.Audit.RecordAsync("service_desk.reply_received", "issue", ticket.IssueId, ticket.ProjectId, new { from = ticket.Contact.Email, source = "tracking page" }, actorId: c.BotId);
        return new InboundResult(InboundOutcome.Commented, null, ticket.IssueId);
    }, ct);

    // =====================================================================
    // Shared
    // =====================================================================

    private static async Task<ServiceDeskContact> ContactAsync(AppDbContext db, string email, string? name, CancellationToken ct)
    {
        name = string.IsNullOrWhiteSpace(name) ? null : Clip(name.Trim(), 100);
        var contact = await db.ServiceDeskContacts.FirstOrDefaultAsync(x => x.Email == email, ct);
        if (contact == null)
        {
            db.ServiceDeskContacts.Add(contact = new ServiceDeskContact { Email = email, Name = name });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                db.Entry(contact).State = EntityState.Detached;
                contact = await db.ServiceDeskContacts.FirstAsync(x => x.Email == email, ct);
            }
        }
        else if (contact.Name == null && name != null)
        {
            contact.Name = name;
            await db.SaveChangesAsync(ct);
        }
        return contact;
    }

    private async Task<ServiceDeskTicket> CreateTicketAsync(Ctx c, Project project, ServiceDeskSettings settings, ServiceDeskContact contact,
        string subject, string body, ServiceDeskSource source, IReadOnlyList<MimePart> parts, string? messageId, CancellationToken ct)
    {
        var db = c.Db;
        var template = string.IsNullOrWhiteSpace(settings.IssueTemplate) ? "{{body}}" : settings.IssueTemplate!;
        var description = ServiceDeskMailer.Fill(template, new Dictionary<string, string>
        {
            ["body"] = body, ["requester"] = contact.DisplayName, ["email"] = contact.Email, ["subject"] = subject,
        });
        var labelIds = await db.Labels.Where(l => settings.DefaultLabelIds.Contains(l.Id)).Select(l => l.Id).ToListAsync(ct);
        int? assignee = settings.DefaultAssigneeId is { } a && await c.Access.CanAsync(project.Id, Permission.EditIssues, a) ? a : null;
        var type = settings.DefaultIssueType == IssueType.Subtask ? IssueType.Task : settings.DefaultIssueType;
        var issue = await c.Issues.CreateIssueAsync(project.Id,
            new Issue { Title = Clip(subject, 200), Type = type, AssigneeId = assignee, Description = description }, labelIds, c.BotId);

        var files = await SaveAttachmentsAsync(c, issue.Id, parts, fromComment: false, ct);
        if (files.Length > 0)
            await db.Issues.Where(i => i.Id == issue.Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.Description, description + files), ct);

        var ticket = new ServiceDeskTicket
        {
            IssueId = issue.Id, ProjectId = project.Id, ContactId = contact.Id, Source = source, Subject = Clip(subject, 300),
            InitialMessage = body, ReplyToken = NewReplyToken(), TrackingToken = NewTrackingToken(),
        };
        db.ServiceDeskTickets.Add(ticket);
        await db.SaveChangesAsync(ct);
        if (messageId != null)
        {
            db.ServiceDeskMessages.Add(new ServiceDeskMessage { TicketId = ticket.Id, Direction = ServiceDeskDirection.Inbound, MessageId = messageId });
            await db.SaveChangesAsync(ct);
        }
        await c.Audit.RecordAsync("service_desk.request_created", "issue", issue.Id, project.Id,
            new { key = $"{project.Key}-{issue.Number}", from = contact.Email, source = source.ToString().ToLowerInvariant() }, actorId: c.BotId);
        if (settings.AutoReplyEnabled) await c.Mailer.SendAutoReplyAsync(db, ticket, contact, $"{project.Key}-{issue.Number}", settings);
        return ticket;
    }

    private async Task<Comment> AddRequesterCommentAsync(Ctx c, ServiceDeskTicket ticket, ServiceDeskContact contact, string body, IReadOnlyList<MimePart> parts, CancellationToken ct)
    {
        var files = await SaveAttachmentsAsync(c, ticket.IssueId, parts, fromComment: true, ct);
        var text = (body + files).Trim();
        var comment = await c.Issues.AddCommentAsync(ticket.IssueId, c.BotId, text.Length == 0 ? "_(no text)_" : text, x => x.ServiceDeskContactId = contact.Id);
        await c.Db.ServiceDeskTickets.Where(t => t.Id == ticket.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastRequesterActivityAt, DateTime.UtcNow), ct);
        return comment;
    }

    /// <summary>Stores the allowed attachments on the issue; returns Markdown listing them (and what was skipped, and why).</summary>
    private async Task<string> SaveAttachmentsAsync(Ctx c, int issueId, IReadOnlyList<MimePart> parts, bool fromComment, CancellationToken ct)
    {
        if (parts.Count == 0) return "";
        var limit = Math.Min(O.MaxAttachmentBytes, storage.MaxBytes);
        var saved = new List<string>();
        var skipped = new List<string>();
        foreach (var (part, i) in parts.Select((p, i) => (p, i)))
        {
            var name = AttachmentStorage.SanitizeFileName(part.FileName ?? part.ContentType.Name ?? $"attachment-{i + 1}{Extension(part.ContentType)}");
            if (i >= O.MaxAttachmentsPerMessage) { skipped.Add($"{AttachmentFormat.EscapeMarkdown(name)} (too many attachments)"); continue; }
            if (O.BlockedExtensions.Any(x => name.EndsWith(x, StringComparison.OrdinalIgnoreCase))) { skipped.Add($"{AttachmentFormat.EscapeMarkdown(name)} (file type not allowed)"); continue; }
            if (part.Content == null) continue;
            using var data = new MemoryStream();
            await part.Content.DecodeToAsync(data, ct);
            if (data.Length > limit) { skipped.Add($"{AttachmentFormat.EscapeMarkdown(name)} (larger than {AttachmentFormat.Size(limit)})"); continue; }
            data.Position = 0;
            try
            {
                var a = await c.Attachments.UploadAsync(issueId, name, data, fromComment, c.BotId, ct);
                saved.Add(AttachmentFormat.MarkdownLink(a.Id, a.FileName, a.ContentType));
            }
            catch (InvalidOperationException ex) { skipped.Add($"{AttachmentFormat.EscapeMarkdown(name)} ({ex.Message.TrimEnd('.')})"); } // too large, too many on the issue
        }
        var md = "";
        if (saved.Count > 0) md += "\n\n**Attachments:** " + string.Join(" · ", saved);
        if (skipped.Count > 0) md += "\n\n_Not attached: " + string.Join(", ", skipped) + "_";
        return md;
    }

    private static string Extension(ContentType t) => t.MimeType.ToLowerInvariant() switch
    {
        "image/png" => ".png", "image/jpeg" => ".jpg", "image/gif" => ".gif", "application/pdf" => ".pdf", _ => ".bin",
    };

    /// <summary>128 random bits as lower-case hex: safe in an email local part, which mail systems may lower-case.</summary>
    public static string NewReplyToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>256 random bits, URL-safe.</summary>
    public static string NewTrackingToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static string Clip(string? s, int max) => s == null ? "" : s.Length <= max ? s : s[..max];
}
