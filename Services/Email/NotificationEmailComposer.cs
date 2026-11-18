using System.Net;
using System.Text;
using GitBench.Api;
using GitBench.Data;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace GitBench.Services.Email;

/// <summary>
/// Builds notification emails from the same <see cref="Notification"/> rows the bell shows (actor, verb, title, link),
/// plus the Markdown excerpt rendered through the app's safe pipeline (<see cref="Display.Markdown"/>, raw HTML
/// disabled). Every other piece of user content is HTML-encoded. Each email links to the item, to the preferences
/// page, and to a signed one-click unsubscribe link.
/// </summary>
public sealed class NotificationEmailComposer(IOptions<EmailOptions> options, PublicBaseUrl publicBaseUrl, IServer server, UnsubscribeTokens tokens)
{
    public const string PreferencesPath = "/account/notifications";

    /// <summary>Absolute site URL: <c>Email:BaseUrl</c>, else <c>PublicBaseUrl</c> (configured or learned from the first request), else the server's first address.</summary>
    public string BaseUrl
    {
        get
        {
            if (options.Value.BaseUrl is { Length: > 0 } b) return b.TrimEnd('/');
            if (publicBaseUrl.Value is { Length: > 0 } p) return p;
            var first = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
            return (first ?? "http://localhost").Replace("://+", "://localhost").Replace("://*", "://localhost").Replace("://[::]", "://localhost").TrimEnd('/');
        }
    }

    public string Absolute(string path) => BaseUrl + (path.StartsWith('/') ? path : "/" + path);

    public EmailMessage Compose(User to, IReadOnlyList<Notification> items)
    {
        if (items.Count == 0) throw new ArgumentException("Nothing to send.", nameof(items));
        var single = items.Count == 1;
        var subject = single ? $"[GitBench] {Sentence(items[0])}" : $"[GitBench] {items.Count} new notifications";
        if (subject.Length > 200) subject = subject[..199] + "…";

        var prefs = Absolute(PreferencesPath);
        var unsubscribe = Absolute("/email/unsubscribe?t=" + Uri.EscapeDataString(tokens.Create(to.Id)));

        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html><body style=\"margin:0;padding:24px;background:#f6f7f9;font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;color:#1f2328;\">");
        html.Append("<div style=\"max-width:600px;margin:0 auto;background:#ffffff;border:1px solid #d0d7de;border-radius:8px;padding:20px;\">");
        html.Append("<div style=\"font-weight:700;color:#6366f1;margin-bottom:12px;\">◆ GitBench</div>");
        if (!single) html.Append($"<p style=\"margin:0 0 12px;\">Hi {E(to.DisplayName)}, here's what happened since your last email:</p>");

        var text = new StringBuilder();
        if (!single) text.Append($"Hi {to.DisplayName}, here's what happened since your last email:\n\n");

        foreach (var n in items)
        {
            var url = Absolute(n.Url);
            html.Append("<div style=\"padding:12px 0;border-top:1px solid #eaeef2;\">");
            html.Append($"<div><b>{E(ActorName(n))}</b> {E(NotificationService.Verb(n.Kind))} <a href=\"{E(url)}\" style=\"color:#4f46e5;\">{E(n.Title)}</a></div>");
            html.Append($"<div style=\"color:#656d76;font-size:12px;\">{n.CreatedAt:yyyy-MM-dd HH:mm} UTC</div>");
            if (!string.IsNullOrWhiteSpace(n.Excerpt))
                html.Append($"<div style=\"margin-top:8px;padding:8px 12px;border-left:3px solid #d0d7de;color:#1f2328;\">{Display.Markdown(n.Excerpt).Value}</div>");
            if (single)
                html.Append($"<p style=\"margin:16px 0 0;\"><a href=\"{E(url)}\" style=\"display:inline-block;background:#4f46e5;color:#ffffff;padding:8px 14px;border-radius:6px;text-decoration:none;\">View on GitBench</a></p>");
            html.Append("</div>");

            text.Append($"{Sentence(n)}\n{url}\n");
            if (!string.IsNullOrWhiteSpace(n.Excerpt))
                text.Append(string.Join("\n", n.Excerpt.Split('\n').Select(l => "> " + l.TrimEnd('\r')))).Append('\n');
            text.Append('\n');
        }

        html.Append("</div>");
        html.Append("<p style=\"max-width:600px;margin:12px auto 0;color:#656d76;font-size:12px;\">");
        html.Append($"You get this because of your <a href=\"{E(prefs)}\" style=\"color:#656d76;\">notification preferences</a>. ");
        html.Append($"<a href=\"{E(unsubscribe)}\" style=\"color:#656d76;\">Unsubscribe from all notification emails</a>.</p>");
        html.Append("</body></html>");

        text.Append("--\n");
        text.Append($"Notification preferences: {prefs}\n");
        text.Append($"Unsubscribe from all notification emails: {unsubscribe}\n");

        return new EmailMessage(to.Email!, to.DisplayName, subject, html.ToString(), text.ToString())
        {
            Headers = new Dictionary<string, string>
            {
                ["List-Unsubscribe"] = $"<{unsubscribe}>",
                ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click",
                ["Auto-Submitted"] = "auto-generated",
            },
        };
    }

    private static string ActorName(Notification n) => n.Actor?.DisplayName ?? "GitBench";

    /// <summary>"Alice Chen assigned you WEB-12 Login button" (plain text; HTML-encode before putting it in markup).</summary>
    public static string Sentence(Notification n) => $"{ActorName(n)} {NotificationService.Verb(n.Kind)} {n.Title}";

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
}
