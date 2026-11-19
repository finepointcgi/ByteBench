using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ByteBench.Services.Email;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ByteBench.Services.ServiceDesk;

/// <summary>What an inbound address points at: a project (by key), a ticket (by reply token), or nothing we know.</summary>
public readonly record struct InboundTarget(string? ProjectKey, string? ReplyToken)
{
    public static readonly InboundTarget None = new(null, null);
    public bool IsNone => ProjectKey == null && ReplyToken == null;
}

/// <summary>
/// The service desk's addresses, from <c>ServiceDesk:IncomingAddress</c> (e.g. <c>desk@example.com</c>):
/// <c>desk+&lt;key&gt;@example.com</c> for a project and <c>desk+r-&lt;token&gt;@example.com</c> for replies to a ticket.
/// Project keys never contain '-', so the two can't collide.
/// </summary>
public sealed class ServiceDeskAddresses(IOptions<ServiceDeskOptions> options, IOptions<EmailOptions> email)
{
    private (string Local, string Domain) Inbox
    {
        get
        {
            var a = (options.Value.IncomingAddress ?? "").Trim().ToLowerInvariant();
            var at = a.LastIndexOf('@');
            return at <= 0 ? ("desk", "bytebench.localhost") : (a[..at], a[(at + 1)..]);
        }
    }

    public string Domain => Inbox.Domain;

    public string ForProject(string key) => $"{Inbox.Local}+{key.ToLowerInvariant()}@{Inbox.Domain}";

    public string ForReply(string token) => $"{Inbox.Local}+r-{token}@{Inbox.Domain}";

    public InboundTarget Parse(string? address)
    {
        var a = (address ?? "").Trim().ToLowerInvariant();
        var at = a.LastIndexOf('@');
        if (at <= 0) return InboundTarget.None;
        var (local, domain) = (a[..at], a[(at + 1)..]);
        var inbox = Inbox;
        if (domain != inbox.Domain || !local.StartsWith(inbox.Local + "+", StringComparison.Ordinal)) return InboundTarget.None;
        var tag = local[(inbox.Local.Length + 1)..];
        if (tag.StartsWith("r-", StringComparison.Ordinal))
            return tag.Length > 2 && tag[2..].All(char.IsAsciiLetterOrDigit) ? new(null, tag[2..]) : InboundTarget.None;
        return Regex.IsMatch(tag, "^[a-z][a-z0-9]{1,9}$") ? new(tag.ToUpperInvariant(), null) : InboundTarget.None;
    }

    /// <summary>The inbox itself, any of its plus-addresses, or the address notification emails come from.</summary>
    public bool IsOwn(string? address)
    {
        var a = (address ?? "").Trim().ToLowerInvariant();
        if (a.Length == 0) return false;
        var inbox = Inbox;
        if (a == $"{inbox.Local}@{inbox.Domain}" || a.StartsWith(inbox.Local + "+", StringComparison.Ordinal) && a.EndsWith("@" + inbox.Domain, StringComparison.Ordinal))
            return true;
        return MailboxAddress.TryParse(email.Value.From, out var from) && string.Equals(from.Address, a, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Reading inbound MIME: recipients, loop/bounce/spam signals, the body as Markdown, and quoted-reply stripping.</summary>
public static partial class ServiceDeskMail
{
    /// <summary>The line put at the top of every email we send; anything below it in a reply is dropped.</summary>
    public const string ReplyMarker = "##- Please type your reply above this line -##";

    /// <summary>Every address the message was delivered to (envelope headers included, for BCC and forwarding).</summary>
    public static IEnumerable<string> Recipients(MimeMessage m)
    {
        foreach (var a in m.To.Mailboxes.Concat(m.Cc.Mailboxes)) yield return a.Address;
        foreach (var h in new[] { "Delivered-To", "X-Original-To", "Envelope-To", "X-Envelope-To", "X-Forwarded-To" })
            foreach (var value in m.Headers.Where(x => x.Field.Equals(h, StringComparison.OrdinalIgnoreCase)).Select(x => x.Value))
                if (InternetAddressList.TryParse(value, out var list))
                    foreach (var a in list.Mailboxes) yield return a.Address;
                else yield return value.Trim().Trim('<', '>');
    }

    /// <summary>Why this message must not be answered or filed (auto-reply, bounce, list mail, our own mail), or null.</summary>
    public static string? LoopReason(MimeMessage m, ServiceDeskAddresses addresses)
    {
        var h = m.Headers;
        if (h["Auto-Submitted"] is { Length: > 0 } auto && !auto.Trim().Equals("no", StringComparison.OrdinalIgnoreCase)) return "auto-submitted";
        if (h["Precedence"] is { Length: > 0 } p && p.Trim().ToLowerInvariant() is "bulk" or "junk" or "list" or "auto_reply") return "precedence " + p.Trim().ToLowerInvariant();
        if (h.Contains("X-Autoreply") || h.Contains("X-Autorespond") || h.Contains("X-Autoresponder")) return "auto-reply";
        if (h.Contains("List-Id") || h.Contains("List-Unsubscribe")) return "mailing list";
        if (m.Body is MultipartReport || m.Body?.ContentType.IsMimeType("multipart", "report") == true) return "bounce";
        if (h["Return-Path"] is { } rp && rp.Trim() == "<>") return "bounce";
        var from = m.From.Mailboxes.FirstOrDefault()?.Address ?? "";
        var local = from.Split('@')[0].ToLowerInvariant();
        if (local is "mailer-daemon" or "postmaster" or "noreply" or "no-reply" or "donotreply") return "automated sender";
        if (addresses.IsOwn(from)) return "own address";
        return null;
    }

    /// <summary>Why this looks like spam (spam filter verdict, blocked sender, too many links), or null.</summary>
    public static string? SpamReason(MimeMessage m, string sender, string body, ServiceDeskOptions o)
    {
        if (m.Headers["X-Spam-Flag"]?.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase) == true) return "spam filter";
        if (m.Headers["X-Spam-Status"]?.TrimStart().StartsWith("yes", StringComparison.OrdinalIgnoreCase) == true) return "spam filter";
        return SpamReason(sender, m.Subject + "\n" + body, o);
    }

    public static string? SpamReason(string sender, string text, ServiceDeskOptions o)
    {
        var s = sender.ToLowerInvariant();
        foreach (var b in o.BlockedSenders.Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0))
            if (b.StartsWith('@') ? s.EndsWith(b, StringComparison.Ordinal) : s == b) return "blocked sender";
        if (o.MaxLinks > 0 && LinkPattern().Matches(text).Count > o.MaxLinks) return "too many links";
        return null;
    }

    /// <summary>The text body as Markdown: the plain-text part, else the HTML part converted. Unstripped.</summary>
    public static string Body(MimeMessage m)
    {
        if (m.TextBody is { } text && !string.IsNullOrWhiteSpace(text)) return Normalize(text);
        if (m.HtmlBody is { } html) return HtmlToMarkdown(html);
        return "";
    }

    /// <summary>Attachments and inline non-text parts (images pasted into the body).</summary>
    public static IEnumerable<MimePart> AttachmentParts(MimeMessage m) =>
        m.BodyParts.OfType<MimePart>().Where(p => p.IsAttachment || !(p.ContentType.IsMimeType("text", "plain") || p.ContentType.IsMimeType("text", "html")));

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    /// <summary>
    /// Cuts a reply at the first quote header ("On … wrote:", "-----Original Message-----", Outlook's From:/Sent: block, our reply
    /// marker), then drops trailing "&gt;" quote lines and the signature ("-- ").
    /// </summary>
    public static string StripQuotedReply(string text)
    {
        var lines = Normalize(text).Split('\n').ToList();
        var cut = lines.Count;
        for (var i = 0; i < lines.Count && cut == lines.Count; i++)
        {
            var l = lines[i].Trim();
            var bare = l.TrimStart('>', ' ');
            if (bare.Contains(ReplyMarker, StringComparison.Ordinal)
                || OriginalMessage().IsMatch(bare)
                || bare.StartsWith("________________", StringComparison.Ordinal)
                || l == "--" || lines[i] == "-- ")
                cut = i;
            else if (WroteLine().IsMatch(bare)) cut = i;
            else if (bare.StartsWith("On ", StringComparison.Ordinal) && i + 1 < lines.Count && lines[i + 1].Trim().EndsWith("wrote:", StringComparison.Ordinal)) cut = i;
            else if (FromLine().IsMatch(bare) && lines.Skip(i + 1).Take(4).Any(n => SentLine().IsMatch(n.TrimStart('>', ' ')))) cut = i;
        }
        lines = lines.Take(cut).ToList();
        while (lines.Count > 0 && (lines[^1].Trim().Length == 0 || lines[^1].TrimStart().StartsWith('>'))) lines.RemoveAt(lines.Count - 1);
        return string.Join('\n', lines).Trim();
    }

    // ----- HTML → Markdown-ish -----

    /// <summary>
    /// A small, safe HTML-to-Markdown conversion: scripts, styles and comments are dropped, every other tag is either mapped
    /// (paragraphs, line breaks, lists, headings, bold/italic, links, block quotes) or removed; entities are decoded. The
    /// result is rendered later through the app's Markdown pipeline, which escapes any raw HTML.
    /// </summary>
    public static string HtmlToMarkdown(string html)
    {
        html = DropBlocks().Replace(html, "");
        html = Comments().Replace(html, "");
        var sb = new StringBuilder();
        var quote = 0;
        var atLineStart = true;
        var links = new Stack<(int Start, string? Href)>();

        void NewLine(bool blank = false)
        {
            // Trim trailing spaces on the line, then break (at most one blank line).
            while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
            if (sb.Length == 0) { atLineStart = true; return; }
            if (!atLineStart) sb.Append('\n');
            if (blank && !(sb.Length >= 2 && sb[^1] == '\n' && sb[^2] == '\n')) sb.Append('\n');
            atLineStart = true;
        }

        void Text(string s)
        {
            if (s.Length == 0) return;
            if (atLineStart)
            {
                s = s.TrimStart();
                if (s.Length == 0) return;
                if (quote > 0) sb.Append(string.Concat(Enumerable.Repeat("> ", quote)));
                atLineStart = false;
            }
            sb.Append(s);
        }

        var pos = 0;
        foreach (Match t in Tag().Matches(html))
        {
            if (t.Index > pos) Text(Whitespace().Replace(WebUtility.HtmlDecode(html[pos..t.Index]), " "));
            pos = t.Index + t.Length;
            var closing = t.Groups[1].Value == "/";
            var name = t.Groups[2].Value.ToLowerInvariant();
            var attrs = t.Groups[3].Value;
            switch (name)
            {
                case "br": NewLine(); break;
                case "p" or "div" or "table" or "ul" or "ol" or "pre" or "section" or "article": NewLine(blank: name is "p" or "ul" or "ol" or "table"); break;
                case "tr": NewLine(); break;
                case "td" or "th": if (!closing) Text(" "); break;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    NewLine(blank: true);
                    if (!closing) Text(new string('#', name[1] - '0') + " ");
                    break;
                case "li":
                    NewLine();
                    if (!closing) Text("- ");
                    break;
                case "blockquote":
                    NewLine();
                    quote = Math.Max(0, quote + (closing ? -1 : 1));
                    break;
                case "hr": NewLine(); Text("---"); NewLine(); break;
                case "strong" or "b": Text("**"); break;
                case "em" or "i": Text("_"); break;
                case "a" when !closing:
                    var href = HrefAttr().Match(attrs) is { Success: true } hm ? WebUtility.HtmlDecode(hm.Groups[2].Value).Trim() : null;
                    if (href != null && !(href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                            || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)))
                        href = null; // javascript:, data:, relative links: keep only the text
                    links.Push((sb.Length, href));
                    break;
                case "a" when closing && links.Count > 0:
                    var (start, h) = links.Pop();
                    if (h != null && start <= sb.Length)
                    {
                        var label = sb.ToString(start, sb.Length - start).Trim();
                        if (label.Length > 0 && label != h && !h.EndsWith(label, StringComparison.Ordinal))
                        {
                            sb.Insert(start, '[');
                            sb.Append("](").Append(h.Replace(" ", "%20").Replace(")", "%29")).Append(')');
                        }
                    }
                    break;
                case "img" when !closing:
                    if (AltAttr().Match(attrs) is { Success: true } am && am.Groups[2].Value.Trim() is { Length: > 0 } alt)
                        Text($"[image: {WebUtility.HtmlDecode(alt)}]");
                    break;
            }
        }
        if (pos < html.Length) Text(Whitespace().Replace(WebUtility.HtmlDecode(html[pos..]), " "));
        var result = sb.ToString().Replace(" ", " ");
        return BlankLines().Replace(result, "\n\n").Trim();
    }

    [GeneratedRegex(@"<(script|style|head|title)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DropBlocks();
    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();
    [GeneratedRegex(@"<(/?)([a-zA-Z][a-zA-Z0-9]*)([^>]*)>")]
    private static partial Regex Tag();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
    [GeneratedRegex(@"\bhref\s*=\s*(['""])(.*?)\1", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HrefAttr();
    [GeneratedRegex(@"\balt\s*=\s*(['""])(.*?)\1", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AltAttr();
    [GeneratedRegex(@"\n[ \t]*\n(\s*\n)+")]
    private static partial Regex BlankLines();
    [GeneratedRegex(@"^On\s.{1,300}\swrote:\s*$")]
    private static partial Regex WroteLine();
    [GeneratedRegex(@"^-{2,}\s*Original Message\s*-{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex OriginalMessage();
    [GeneratedRegex(@"^From:\s+\S", RegexOptions.IgnoreCase)]
    private static partial Regex FromLine();
    [GeneratedRegex(@"^(Sent|Date):\s+\S", RegexOptions.IgnoreCase)]
    private static partial Regex SentLine();
    [GeneratedRegex(@"https?://", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();
}
