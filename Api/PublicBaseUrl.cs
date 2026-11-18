namespace GitBench.Api;

/// <summary>
/// The site's absolute base URL (e.g. <c>https://gitbench.example.com</c>) for links in API responses and webhook
/// payloads. Uses the <c>PublicBaseUrl</c> setting when present; otherwise it is learned from the first HTTP request
/// (see <see cref="WebhookPushCaptureMiddleware"/>), and API responses use the current request's host.
/// </summary>
public sealed class PublicBaseUrl(IConfiguration config)
{
    private string? learned;

    public string Value => (config["PublicBaseUrl"] is { Length: > 0 } c ? c : learned ?? "").TrimEnd('/');

    public void Learn(HttpRequest request)
    {
        if (learned == null && request.Host.HasValue) learned = $"{request.Scheme}://{request.Host}{request.PathBase}";
    }

    /// <summary>The configured URL, or the URL of <paramref name="request"/>.</summary>
    public string For(HttpRequest request) =>
        config["PublicBaseUrl"] is { Length: > 0 } c ? c.TrimEnd('/') : $"{request.Scheme}://{request.Host}{request.PathBase}";
}
