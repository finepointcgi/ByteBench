using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using GitBench.Services.Webhooks;
using Microsoft.Extensions.Options;

namespace GitBench.Services.ChatIntegrations;

/// <summary>
/// Outgoing HTTP for chat integrations and the Slack app: the named client <see cref="ClientName"/> (SSRF-guarded like
/// webhooks: checked addresses, no proxy, no redirects; tests swap its handler for a fake), a 10 s timeout, and secret
/// scrubbing of anything that ends up in the delivery log.
/// </summary>
public sealed class ChatHttp(IHttpClientFactory factory, IOptionsMonitor<SlackOptions> slack)
{
    public const string ClientName = "GitBench.Chat";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    public const int MaxResponseChars = 4096;

    /// <summary>POSTs a JSON body to an incoming webhook / Workflows URL.</summary>
    public Task<WebhookAttempt> PostWebhookAsync(string url, string body, IEnumerable<string> secrets, CancellationToken ct) =>
        SendAsync(url, body, bearer: null, slackApi: false, secrets, ct);

    /// <summary>Calls a Slack Web API method (e.g. <c>chat.postMessage</c>) with a bot token. Slack reports errors as 200 + <c>{"ok":false}</c>.</summary>
    public Task<WebhookAttempt> CallSlackAsync(string method, string token, string body, IEnumerable<string> secrets, CancellationToken ct) =>
        SendAsync(slack.CurrentValue.ApiBaseUrl.TrimEnd('/') + "/" + method, body, token, slackApi: true, secrets.Append(token), ct);

    private async Task<WebhookAttempt> SendAsync(string url, string body, string? bearer, bool slackApi, IEnumerable<string> secrets, CancellationToken ct)
    {
        var scrub = secrets.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
        var sw = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (bearer != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            var client = factory.CreateClient(ClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            if (text.Length > MaxResponseChars) text = text[..MaxResponseChars];
            var status = (int)response.StatusCode;
            var ok = status is >= 200 and < 300;
            string? error = ok ? null : $"HTTP {status} {response.ReasonPhrase}" + (string.IsNullOrWhiteSpace(text) ? "" : $": {Truncate(text, 200)}");
            var retryable = status >= 500 || status == 429;
            if (ok && slackApi)
            {
                // {"ok":false,"error":"channel_not_found"}
                JsonNode? json = null;
                try { json = JsonNode.Parse(text); } catch (System.Text.Json.JsonException) { }
                if (json?["ok"]?.GetValue<bool>() != true)
                {
                    ok = false;
                    var code = json?["error"]?.GetValue<string>() ?? "unexpected response";
                    error = "Slack: " + code;
                    retryable = code is "ratelimited" or "internal_error" or "service_unavailable" or "request_timeout" or "fatal_error";
                }
            }
            return new(ok, status, Redact(error, scrub), Redact(text.Length == 0 ? null : text, scrub), (int)sw.ElapsedMilliseconds, Retryable: retryable);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, null, $"Timed out after {Timeout.TotalSeconds:0} s.", null, (int)sw.ElapsedMilliseconds, Retryable: true);
        }
        catch (HttpRequestException ex)
        {
            var blocked = ex.InnerException is WebhookBlockedException || ex.InnerException?.InnerException is WebhookBlockedException;
            return new(false, null, Redact(Truncate(ex.InnerException?.Message ?? ex.Message, 1000), scrub), null, (int)sw.ElapsedMilliseconds, Retryable: !blocked);
        }
    }

    /// <summary>Replaces every occurrence of a secret (URL, token) in <paramref name="text"/> with <c>[redacted]</c>.</summary>
    public static string? Redact(string? text, IEnumerable<string> secrets)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var s in secrets.Where(s => s.Length >= 4).OrderByDescending(s => s.Length))
            text = text.Replace(s, "[redacted]", StringComparison.Ordinal);
        return text;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
