using System.Net;
using System.Net.Sockets;
using System.Xml;
using ByteBench.Services.Webhooks;

namespace ByteBench.Services.Saml;

/// <summary>What an IdP's metadata says about it.</summary>
public sealed record SamlIdpMetadata(string EntityId, string? SsoUrl, string? SloUrl, List<string> Certificates);

/// <summary>
/// Fetches and reads an identity provider's SAML metadata, with the same SSRF guard as mirrors and importers
/// (<see cref="Mirroring.MirrorUrlGuard"/>, <see cref="TrackerImport.TrackerUrlGuard"/>): http(s) only, no credentials in the
/// URL, no loopback, private, link-local or metadata addresses unless <c>Saml:AllowPrivateNetworks</c> (default:
/// <c>Webhooks:AllowPrivateNetworks</c>), the checked address is the one connected to (no DNS rebinding), no redirects,
/// a 10-second timeout and a 1 MiB cap. The XML is parsed without DTDs.
/// </summary>
public sealed class SamlMetadataClient : IDisposable
{
    public const int MaxBytes = 1024 * 1024;
    private readonly IConfiguration configuration;
    private readonly HttpClient http;

    public SamlMetadataClient(IConfiguration configuration)
    {
        this.configuration = configuration;
        http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10), ConnectCallback = ConnectAsync,
        }) { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = MaxBytes };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ByteBench-SAML/1.0");
    }

    public bool AllowPrivateNetworks =>
        configuration.GetValue<bool?>("Saml:AllowPrivateNetworks") ?? configuration.GetValue<bool>("Webhooks:AllowPrivateNetworks");

    /// <summary>Null if <paramref name="url"/> may be fetched, else why not.</summary>
    public string? Validate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "Enter the metadata URL.";
        url = url.Trim();
        if (url.Length > 2048) return "The URL is too long.";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
            return "Enter an absolute https:// URL.";
        if (url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return "The URL can't contain spaces or control characters.";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "Don't put credentials in the URL.";
        if (AllowPrivateNetworks) return null;
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return "The metadata URL can't be on this server or another local address (set Saml:AllowPrivateNetworks to allow it).";
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && WebhookSender.IsBlocked(ip))
            return $"The metadata URL can't be on a private, loopback, link-local or metadata address ({ip}).";
        return null;
    }

    public async Task<SamlIdpMetadata> FetchAsync(string url, CancellationToken ct = default)
    {
        if (Validate(url) is { } invalid) throw new SamlException("metadata_url", invalid);
        try
        {
            using var response = await http.GetAsync(url.Trim(), HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400) throw new SamlException("metadata", $"The metadata URL redirected ({status}); redirects aren't followed. Use the final URL.");
            if (!response.IsSuccessStatusCode) throw new SamlException("metadata", $"The metadata URL answered HTTP {status}.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int n;
            while ((n = await stream.ReadAsync(buffer, ct)) > 0)
            {
                ms.Write(buffer, 0, n);
                if (ms.Length > MaxBytes) throw new SamlException("metadata", "The metadata is larger than 1 MiB.");
            }
            return Parse(ms.ToArray());
        }
        catch (HttpRequestException e) when (e.InnerException is SamlException blocked) { throw blocked; }
        catch (HttpRequestException e) { throw new SamlException("metadata", "Couldn't fetch the metadata: " + (e.InnerException?.Message ?? e.Message)); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new SamlException("metadata", "The metadata URL didn't answer within 10 seconds."); }
    }

    /// <summary>Reads an <c>md:EntityDescriptor</c> with an <c>IDPSSODescriptor</c>.</summary>
    public static SamlIdpMetadata Parse(byte[] xml)
    {
        var doc = SamlXml.Load(xml, MaxBytes);
        var root = doc.DocumentElement!;
        var entity = root.LocalName == "EntityDescriptor" && root.NamespaceURI == SamlXml.Metadata ? root
            : SamlXml.Descendants(doc, SamlXml.Metadata, "EntityDescriptor").FirstOrDefault(e => SamlXml.Child(e, SamlXml.Metadata, "IDPSSODescriptor") != null)
              ?? throw new SamlException("metadata", "The metadata has no EntityDescriptor.");
        var idp = SamlXml.Child(entity, SamlXml.Metadata, "IDPSSODescriptor") ?? throw new SamlException("metadata", "The metadata has no IDPSSODescriptor (is it an IdP's metadata?).");
        string? Location(string element) => SamlXml.Children(idp, SamlXml.Metadata, element)
            .FirstOrDefault(e => SamlXml.Attr(e, "Binding") == SamlXml.RedirectBinding) is { } el ? SamlXml.Attr(el, "Location") : null;
        var certs = SamlXml.Children(idp, SamlXml.Metadata, "KeyDescriptor")
            .Where(k => SamlXml.Attr(k, "use") is null or "signing")
            .SelectMany(k => SamlXml.Descendants(k, SamlXml.DSig, "X509Certificate").Where(c => IsWithin(c, k)))
            .Select(c => string.Concat(c.InnerText.Where(ch => !char.IsWhiteSpace(ch))))
            .Where(c => c.Length > 0).Distinct().ToList();
        return new SamlIdpMetadata(SamlXml.Attr(entity, "entityID") ?? "", Location("SingleSignOnService"), Location("SingleLogoutService"), certs);
    }

    private static bool IsWithin(XmlNode node, XmlNode ancestor)
    {
        for (var n = node.ParentNode; n != null; n = n.ParentNode) if (n == ancestor) return true;
        return false;
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        if (!AllowPrivateNetworks)
        {
            var all = addresses;
            addresses = addresses.Where(a => !WebhookSender.IsBlocked(a)).ToArray();
            if (addresses.Length == 0)
                throw new SamlException("metadata_url", all.Length == 0 ? $"Could not resolve {host}."
                    : $"Blocked: {host} resolves to a private, loopback, link-local or metadata address. Set Saml:AllowPrivateNetworks to allow it.");
        }
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException e) { socket.Dispose(); last = e; }
        }
        throw new HttpRequestException($"Couldn't connect to {host}.", last);
    }

    public void Dispose() => http.Dispose();
}
