using System.Net;
using System.Net.Sockets;
using System.Text;
using ByteBench.Services.Git;
using ByteBench.Services.Webhooks;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Ci;

/// <summary>The text of an included file, or why it could not be read.</summary>
public sealed record CiIncludeRead(string? Text, string? Error)
{
    public static CiIncludeRead Ok(string text) => new(text, null);
    public static CiIncludeRead Fail(string error) => new(null, error);
}

/// <summary>Reads the files a pipeline file's <c>include:</c> names (see <see cref="CiFile.Parse"/>).</summary>
public interface ICiIncludeProvider
{
    /// <summary>A file of the repository at the pipeline's commit, by path from the repository root.</summary>
    CiIncludeRead ReadLocal(string path);
    /// <summary>A file fetched from a URL; refused unless the server allows remote includes.</summary>
    CiIncludeRead ReadRemote(string url);
}

/// <summary>
/// Includes of one commit: local files come from the repository's git objects (never the working tree of a job), remote ones
/// through <see cref="CiRemoteIncludes"/> (never for a fork pull request, whose author is outside the project).
/// </summary>
public sealed class CiIncludeProvider(string repoPath, string rev, CiRemoteIncludes remote, bool allowRemote = true) : ICiIncludeProvider
{
    public CiIncludeRead ReadLocal(string path)
    {
        var p = path.Trim();
        if (p.Length == 0 || p.Length > 200 || p.Any(c => char.IsControl(c) || c == '\\'))
            return CiIncludeRead.Fail("is not a valid file path");
        p = p.TrimStart('/');
        var segments = p.Split('/');
        if (segments.Any(s => s is "" or "." or ".." || s.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            return CiIncludeRead.Fail("must be a path inside the repository (no '..', '.git' or empty parts)");
        BlobInfo? blob;
        try { blob = GitOps.GetBlob(repoPath, rev, p); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return CiIncludeRead.Fail("could not be read from the repository"); }
        if (blob == null) return CiIncludeRead.Fail($"was not found in the repository at this commit ({p})");
        if (blob.TooLarge || blob.IsBinary || blob.Text == null) return CiIncludeRead.Fail("is too large or not a text file");
        return CiIncludeRead.Ok(blob.Text);
    }

    public CiIncludeRead ReadRemote(string url) =>
        allowRemote ? remote.Fetch(url) : CiIncludeRead.Fail("is not fetched for pull requests from forks (their author is not trusted to name addresses)");
}

/// <summary>
/// Fetches <c>include: { remote: https://... }</c> files (singleton). Off unless <c>Ci:AllowRemoteIncludes</c> is true, because a
/// pipeline file is written by whoever can push: https only (plain http only with <c>Ci:AllowPrivateNetworks</c>, for local tests),
/// no credentials in the URL, optionally only hosts under <c>Ci:RemoteIncludeAllowedHosts</c>, every resolved address checked
/// against the same blocklist as webhooks (loopback, private, link-local, CGNAT, metadata) at connect time so DNS rebinding can't
/// help, no redirects, no proxy, a 10 second timeout, and at most <see cref="CiFile.MaxFileChars"/> characters of the answer.
/// Answers are cached for a few minutes.
/// </summary>
public sealed class CiRemoteIncludes
{
    private readonly IOptionsMonitor<CiOptions> options;
    private readonly IOptionsMonitor<WebhookOptions> webhookOptions;
    private readonly HttpClient http;
    private readonly Dictionary<string, (DateTime At, CiIncludeRead Result)> cache = [];
    private readonly Lock gate = new();
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    public CiRemoteIncludes(IOptionsMonitor<CiOptions> options, IOptionsMonitor<WebhookOptions> webhookOptions)
    {
        (this.options, this.webhookOptions) = (options, webhookOptions);
        http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2), ConnectCallback = ConnectAsync,
        }) { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = CiFile.MaxFileChars * 4 + 1024 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ByteBench-CI/1.0");
    }

    public bool Enabled => options.CurrentValue.AllowRemoteIncludes;
    private bool AllowPrivate => options.CurrentValue.AllowPrivateNetworks ?? webhookOptions.CurrentValue.AllowPrivateNetworks;

    /// <summary>Why <paramref name="url"/> may not be fetched, or null.</summary>
    public string? Validate(string url)
    {
        if (!Enabled) return "remote includes are turned off on this server (the administrator can set Ci:AllowRemoteIncludes)";
        if (url.Length > 500 || url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return "is not a valid address";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return "is not an absolute address";
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && AllowPrivate)) return "must be an https:// address";
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) return "can't contain credentials or a fragment";
        var allowed = options.CurrentValue.RemoteIncludeAllowedHosts;
        if (allowed is { Length: > 0 } && !allowed.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + h.TrimStart('.'), StringComparison.OrdinalIgnoreCase)))
            return $"is on a host that is not in Ci:RemoteIncludeAllowedHosts ({uri.Host})";
        if (!AllowPrivate)
        {
            if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
                return "can't be on this server or another local address";
            if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && WebhookSender.IsBlocked(ip))
                return $"can't be on a private, loopback, link-local or metadata address ({ip})";
        }
        return null;
    }

    public CiIncludeRead Fetch(string url)
    {
        if (Validate(url) is { } invalid) return CiIncludeRead.Fail(invalid);
        lock (gate)
            if (cache.TryGetValue(url, out var hit) && DateTime.UtcNow - hit.At < CacheFor) return hit.Result;
        CiIncludeRead result;
        try { result = FetchAsync(url).GetAwaiter().GetResult(); }
        catch (Exception ex) { result = CiIncludeRead.Fail("could not be fetched: " + ex.Message); }
        lock (gate)
        {
            if (cache.Count > 100) cache.Clear();
            if (result.Text != null) cache[url] = (DateTime.UtcNow, result);
        }
        return result;
    }

    private async Task<CiIncludeRead> FetchAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("text/yaml, text/plain, application/yaml, */*;q=0.1");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400) return CiIncludeRead.Fail("redirects to another address; include the final address (redirects are not followed)");
        if (status != 200) return CiIncludeRead.Fail($"answered HTTP {status}");
        if (response.Content.Headers.ContentLength is > CiFile.MaxFileChars * 4L) return CiIncludeRead.Fail($"is larger than {CiFile.MaxFileChars / 1024} KB");
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[CiFile.MaxFileChars * 4 + 1];
        var filled = 0;
        int n;
        while (filled < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(filled))) > 0) filled += n;
        if (filled == buffer.Length) return CiIncludeRead.Fail($"is larger than {CiFile.MaxFileChars / 1024} KB");
        var text = new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(buffer, 0, filled);
        return text.Length > CiFile.MaxFileChars ? CiIncludeRead.Fail($"is larger than {CiFile.MaxFileChars / 1024} KB") : CiIncludeRead.Ok(text);
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        if (!AllowPrivate)
        {
            var blocked = addresses.Where(WebhookSender.IsBlocked).ToList();
            addresses = addresses.Where(a => !WebhookSender.IsBlocked(a)).ToArray();
            if (addresses.Length == 0)
                throw new HttpRequestException(blocked.Count == 0 ? $"Could not resolve {host}."
                    : $"Blocked: {host} resolves to a private, loopback, link-local or metadata address ({blocked[0]}).");
        }
        if (addresses.Length == 0) throw new HttpRequestException($"Could not resolve {host}.");
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
        throw last!;
    }
}
