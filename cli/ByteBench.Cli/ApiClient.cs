using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ByteBench.Cli;

/// <summary>Where the server is and whether plain HTTP to it is acceptable.</summary>
public static class ServerUrl
{
    /// <summary>
    /// Normalizes <paramref name="value"/> to <c>scheme://host[:port][/path]</c>. HTTPS is required, except for localhost
    /// (<c>localhost</c>, <c>*.localhost</c>, <c>127.0.0.0/8</c>, <c>::1</c>) or when <paramref name="insecure"/> is set.
    /// </summary>
    public static Uri Parse(string value, bool insecure)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
            throw CliException.Usage($"'{Text.Line(value)}' is not a server address. Use something like https://bytebench.example.com.");
        if (!string.IsNullOrEmpty(uri.UserInfo)) throw CliException.Usage("Don't put credentials in the server address.");
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) throw CliException.Usage("The server address must not have a query or fragment.");
        if (uri.Scheme == "http" && !IsLocal(uri) && !insecure)
            throw CliException.Usage($"Refusing to send an access token over plain HTTP to {uri.Host}. Use https://, or pass --insecure if you really mean it.");
        return new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
    }

    public static bool IsLocal(Uri uri)
    {
        var host = uri.Host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }

    /// <summary>The server address without the trailing slash, for printing links.</summary>
    public static string Display(Uri server) => server.ToString().TrimEnd('/');
}

/// <summary>A response from the REST API.</summary>
public sealed record ApiResponse(HttpStatusCode Status, JsonNode? Body, string Text, HttpResponseHeaders Headers);

/// <summary>Calls the REST API (<c>/api/v1</c>) with the access token as a Bearer credential. Never follows redirects and never prints the token.</summary>
public sealed class ApiClient : IDisposable
{
    private readonly HttpClient http;
    public Uri Server { get; }

    public ApiClient(Uri server, string token, HttpMessageHandler handler)
    {
        Server = server;
        http = new HttpClient(handler, disposeHandler: true) { BaseAddress = server, Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("bytebench-cli/" + (typeof(ApiClient).Assembly.GetName().Version?.ToString(3) ?? "1"));
    }

    public Task<ApiResponse> GetAsync(string path, CancellationToken ct = default) => SendAsync(HttpMethod.Get, path, null, ct);
    public Task<ApiResponse> PostAsync(string path, object body, CancellationToken ct = default) => SendAsync(HttpMethod.Post, path, body, ct);
    public Task<ApiResponse> PatchAsync(string path, object body, CancellationToken ct = default) => SendAsync(HttpMethod.Patch, path, body, ct);

    public async Task<ApiResponse> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path.TrimStart('/'));
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct); }
        catch (HttpRequestException ex)
        {
            throw CliException.Failure($"Can't reach {ServerUrl.Display(Server)}: {Text.Line(ex.InnerException?.Message ?? ex.Message)}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw CliException.Failure($"{ServerUrl.Display(Server)} did not answer in time.");
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            var status = response.StatusCode;
            if ((int)status is >= 300 and < 400)
                throw CliException.Failure($"The server redirected the request ({(int)status}); not following it so the access token stays with {ServerUrl.Display(Server)}. Check the server address (http vs https?).");
            JsonNode? json = null;
            if (text.Length > 0 && response.Content.Headers.ContentType?.MediaType is { } mt && mt.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                try { json = JsonNode.Parse(text); } catch (JsonException) { /* plain text error */ }
            }
            if ((int)status >= 400) throw Failure(status, json, text);
            return new ApiResponse(status, json, text, response.Headers);
        }
    }

    private static CliException Failure(HttpStatusCode status, JsonNode? json, string text)
    {
        var detail = json.S("detail");
        if (detail.Length == 0) detail = json.S("title");
        detail = Text.Line(string.IsNullOrWhiteSpace(detail) ? Text.Cut(text, 200) : detail);
        return status switch
        {
            HttpStatusCode.Unauthorized => CliException.Auth("The server rejected the access token (invalid, expired or revoked). Run: bytebench login --server <url>"),
            HttpStatusCode.Forbidden when detail.Contains("scope", StringComparison.OrdinalIgnoreCase) =>
                CliException.Auth("The access token doesn't have the scope this needs (" + detail + ") Create a token with read_api/write_api."),
            HttpStatusCode.Forbidden => CliException.Failure("Not allowed: " + detail),
            HttpStatusCode.NotFound => CliException.Failure("Not found: " + detail),
            HttpStatusCode.TooManyRequests => CliException.Failure("Rate limited by the server: " + detail),
            _ => CliException.Failure($"The server answered {(int)status}: {detail}"),
        };
    }

    public void Dispose() => http.Dispose();
}
