using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services.Webhooks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Workbench;

/// <summary>
/// The SSRF guard for the Workbench address, like the webhook and mirror ones: http(s) only, no credentials in the URL, and no
/// loopback, private, link-local, CGNAT or metadata addresses unless <c>Workbench:AllowPrivateNetworks</c> (default: follow
/// <c>Webhooks:AllowPrivateNetworks</c>, on in Development) allows it. The connection re-checks every resolved address.
/// </summary>
public sealed class WorkbenchUrlGuard(IOptionsMonitor<WorkbenchOptions> options, IOptionsMonitor<WebhookOptions> webhookOptions)
{
    public bool AllowPrivateNetworks => options.CurrentValue.AllowPrivateNetworks ?? webhookOptions.CurrentValue.AllowPrivateNetworks;

    /// <summary>Null if <paramref name="url"/> is acceptable, else why not.</summary>
    public string? Validate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "Enter Workbench's address.";
        url = url.Trim();
        if (url.Length > 500) return "The address is too long.";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
            return "Enter an absolute http:// or https:// address.";
        if (url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return "The address can't contain spaces or control characters.";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "Don't put credentials in the address; use the API key field.";
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return "Enter just the address (no query or fragment).";
        if (AllowPrivateNetworks) return null;
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return "Workbench can't be on this server or another local address (set Workbench:AllowPrivateNetworks to allow it).";
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && WebhookSender.IsBlocked(ip))
            return $"Workbench can't be on a private, loopback, link-local or metadata address ({ip}).";
        return null;
    }

    /// <summary>Resolves the host and returns the allowed addresses; throws <see cref="WorkbenchBlockedException"/> when none is.</summary>
    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        if (AllowPrivateNetworks) return addresses;
        var allowed = addresses.Where(a => !WebhookSender.IsBlocked(a)).ToArray();
        if (allowed.Length == 0)
            throw new WorkbenchBlockedException(addresses.Length == 0 ? $"Could not resolve {host}."
                : $"Blocked: {host} resolves to a private, loopback, link-local or metadata address ({addresses[0]}). Set Workbench:AllowPrivateNetworks to allow it.");
        return allowed;
    }
}

public sealed class WorkbenchBlockedException(string message) : Exception(message);

/// <summary>The effective connection: the stored address (else <c>Workbench:BaseUrl</c>) and the decrypted API key, cached for a few seconds.</summary>
public sealed class WorkbenchCredentials(IDbContextFactory<AppDbContext> dbFactory, IOptionsMonitor<WorkbenchOptions> options, IDataProtectionProvider provider)
{
    public const string Purpose = "ByteBench.Workbench.ApiKey.v1";
    private readonly IDataProtector protector = provider.CreateProtector(Purpose);
    private readonly object gate = new();
    private (DateTime At, string? Url, string? Key, bool UrlFromSettings)? cached;

    public string Protect(string key) => protector.Protect(key);

    public void Invalidate() { lock (gate) cached = null; }

    /// <summary>The address without a trailing slash (null when none, or not an absolute http(s) URL).</summary>
    public string? BaseUrl => Load().Url;

    /// <summary>True when the address comes from the admin page rather than configuration.</summary>
    public bool UrlFromSettings => Load().UrlFromSettings;

    /// <summary>The API key, or null when none is stored or it can't be decrypted.</summary>
    public string? ApiKey => Load().Key;

    private (string? Url, string? Key, bool UrlFromSettings) Load()
    {
        lock (gate)
        {
            if (cached is { } c && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(10)) return (c.Url, c.Key, c.UrlFromSettings);
        }
        WorkbenchConnection? row = null;
        try
        {
            using var db = dbFactory.CreateDbContext();
            row = db.WorkbenchConnections.AsNoTracking().FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* before the migration ran: fall back to configuration */ }
        var url = WorkbenchService.Normalize(row?.BaseUrl);
        var fromSettings = url != null;
        url ??= WorkbenchService.Normalize(options.CurrentValue.BaseUrl);
        string? key = null;
        if (row?.ApiKeyProtected is { Length: > 0 } enc)
        {
            try { key = protector.Unprotect(enc); }
            catch (System.Security.Cryptography.CryptographicException) { key = null; }
        }
        lock (gate) cached = (DateTime.UtcNow, url, key, fromSettings);
        return (url, key, fromSettings);
    }
}

public enum WbStatus { Ok, NotConfigured, Unreachable, Forbidden, NotFound, Error }

/// <summary>Result of one call to the Workbench API. <see cref="Message"/> never contains the API key.</summary>
public sealed record WbResult<T>(WbStatus Status, T? Value, string? Message = null, int HttpStatus = 0)
{
    public bool Ok => Status == WbStatus.Ok;
    public static WbResult<T> Success(T value, int http = 200) => new(WbStatus.Ok, value, null, http);
    public static WbResult<T> Fail(WbStatus status, string message, int http = 0) => new(status, default, message, http);
    public WbResult<U> As<U>() => new(Status, default, Message, HttpStatus);
}

/// <summary>
/// Calls Workbench's REST API (<c>/api/v1</c>) with the stored key as <c>Authorization: Bearer</c>. Guarded against SSRF (checked
/// addresses at connect time, no proxy, no redirects), time-limited, and it never throws for network or HTTP problems: callers
/// get a <see cref="WbResult{T}"/>. ByteBench only reads through here, plus one task-status write; there is no time-entry call.
/// </summary>
public sealed class WorkbenchClient : IDisposable
{
    public const int MaxResponseBytes = 8 * 1024 * 1024;
    private readonly HttpClient http;
    private readonly WorkbenchCredentials credentials;
    private readonly WorkbenchUrlGuard guard;
    private readonly IOptionsMonitor<WorkbenchOptions> options;

    public WorkbenchClient(WorkbenchCredentials credentials, WorkbenchUrlGuard guard, IOptionsMonitor<WorkbenchOptions> options)
    {
        (this.credentials, this.guard, this.options) = (credentials, guard, options);
        http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = ConnectAsync,
        }) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = MaxResponseBytes };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ByteBench-Workbench/1.0");
    }

    public bool IsConfigured => credentials.BaseUrl != null && credentials.ApiKey != null;

    public Task<WbResult<JsonElement>> GetAsync(string path, CancellationToken ct = default) => SendAsync(HttpMethod.Get, path, null, ct);

    public Task<WbResult<JsonElement>> PostAsync(string path, object body, CancellationToken ct = default) => SendAsync(HttpMethod.Post, path, body, ct);

    public async Task<WbResult<JsonElement>> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var (baseUrl, key) = (credentials.BaseUrl, credentials.ApiKey);
        if (baseUrl == null || key == null) return WbResult<JsonElement>.Fail(WbStatus.NotConfigured, "Workbench isn't connected.");
        if (guard.Validate(baseUrl) is { } invalid) return WbResult<JsonElement>.Fail(WbStatus.Error, invalid);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.CurrentValue.TimeoutSeconds, 1, 60)));
        using var request = new HttpRequestMessage(method, baseUrl + "/api/v1" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            if (status is 401 or 403)
                return WbResult<JsonElement>.Fail(WbStatus.Forbidden, status == 401 ? "Workbench rejected the API key (revoked, expired or wrong)." : "The API key isn't allowed to do that.", status);
            if (status == 404) return WbResult<JsonElement>.Fail(WbStatus.NotFound, "Not found in Workbench.", status);
            if (status is >= 300 and < 400) return WbResult<JsonElement>.Fail(WbStatus.Error, $"Workbench answered with a redirect ({status}); redirects aren't followed.", status);
            if (status < 200 || status >= 300)
                return WbResult<JsonElement>.Fail(status >= 500 ? WbStatus.Unreachable : WbStatus.Error, $"Workbench answered HTTP {status}.", status);
            var text = await ReadAsync(response, timeout.Token);
            if (string.IsNullOrWhiteSpace(text)) return WbResult<JsonElement>.Success(default, status);
            try { using var doc = JsonDocument.Parse(text); return WbResult<JsonElement>.Success(doc.RootElement.Clone(), status); }
            catch (JsonException) { return WbResult<JsonElement>.Fail(WbStatus.Error, "Workbench's answer wasn't JSON (is the address the Workbench site?).", status); }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return WbResult<JsonElement>.Fail(WbStatus.Unreachable, $"Workbench didn't answer within {options.CurrentValue.TimeoutSeconds} s.");
        }
        catch (HttpRequestException ex) when (Find<WorkbenchBlockedException>(ex) is { } blocked)
        {
            return WbResult<JsonElement>.Fail(WbStatus.Error, blocked.Message);
        }
        catch (HttpRequestException ex)
        {
            return WbResult<JsonElement>.Fail(WbStatus.Unreachable, "Couldn't reach Workbench: " + Truncate(ex.InnerException?.Message ?? ex.Message, 200));
        }
    }

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > MaxResponseBytes) throw new HttpRequestException("Workbench's answer was too large.");
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await guard.ResolveAsync(context.DnsEndPoint.Host, ct);
        if (addresses.Length == 0) throw new HttpRequestException($"Could not resolve {context.DnsEndPoint.Host}.");
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex) { socket.Dispose(); last = ex; }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException(last?.Message ?? "Could not connect.", last);
    }

    private static T? Find<T>(Exception? ex) where T : Exception
    {
        for (; ex != null; ex = ex.InnerException) if (ex is T t) return t;
        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public void Dispose() => http.Dispose();
}
