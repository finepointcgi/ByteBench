using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace ByteBench.Services.Saml;

/// <summary>Namespaces, safe XML parsing and the HTTP-Redirect binding encoding.</summary>
public static class SamlXml
{
    public const string Protocol = "urn:oasis:names:tc:SAML:2.0:protocol";
    public const string Assertion = "urn:oasis:names:tc:SAML:2.0:assertion";
    public const string Metadata = "urn:oasis:names:tc:SAML:2.0:metadata";
    public const string DSig = "http://www.w3.org/2000/09/xmldsig#";
    public const string Success = "urn:oasis:names:tc:SAML:2.0:status:Success";
    public const string BearerMethod = "urn:oasis:names:tc:SAML:2.0:cm:bearer";
    public const string PostBinding = "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST";
    public const string RedirectBinding = "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect";
    public const string RsaSha256 = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256";

    /// <summary>
    /// Parses untrusted XML: no DTDs (so no entity expansion or external entities), no resolver, a character cap, and
    /// whitespace preserved (signatures are computed over it).
    /// </summary>
    public static XmlDocument Load(byte[] xml, int maxBytes)
    {
        if (xml.Length > maxBytes) throw new SamlException("too_large", "The SAML message is too large.");
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = maxBytes,
            MaxCharactersFromEntities = 0,
            IgnoreProcessingInstructions = true,
            IgnoreComments = false,
            CloseInput = true,
        };
        var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(xml), settings);
            doc.Load(reader);
        }
        catch (XmlException e)
        {
            throw new SamlException("malformed", "The SAML message isn't well-formed XML (DTDs are not allowed): " + e.Message);
        }
        return doc;
    }

    /// <summary>DEFLATE + base64, the HTTP-Redirect binding's encoding (before URL encoding).</summary>
    public static string DeflateBase64(string xml)
    {
        using var ms = new MemoryStream();
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(xml);
            deflate.Write(bytes, 0, bytes.Length);
        }
        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>Reverses <see cref="DeflateBase64"/>, with an output cap (a zip bomb stops at <paramref name="maxBytes"/>).</summary>
    public static byte[] InflateBase64(string value, int maxBytes)
    {
        byte[] compressed;
        try { compressed = Convert.FromBase64String(value); }
        catch (FormatException) { throw new SamlException("malformed", "The SAML message isn't valid base64."); }
        using var input = new DeflateStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int n;
        try
        {
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, n);
                if (output.Length > maxBytes) throw new SamlException("too_large", "The SAML message is too large.");
            }
        }
        catch (InvalidDataException) { throw new SamlException("malformed", "The SAML message isn't valid DEFLATE data."); }
        return output.ToArray();
    }

    /// <summary>
    /// The query string of an HTTP-Redirect binding message, signed with <paramref name="key"/> when given
    /// (SAMLBindings §3.4.4.1: the signature covers <c>SAMLRequest=…&amp;RelayState=…&amp;SigAlg=…</c> as sent).
    /// </summary>
    public static string RedirectQuery(string parameter, string xml, string? relayState, RSA? key)
    {
        var q = new StringBuilder(parameter).Append('=').Append(Uri.EscapeDataString(DeflateBase64(xml)));
        if (!string.IsNullOrEmpty(relayState)) q.Append("&RelayState=").Append(Uri.EscapeDataString(relayState));
        if (key != null)
        {
            q.Append("&SigAlg=").Append(Uri.EscapeDataString(RsaSha256));
            var signature = key.SignData(Encoding.UTF8.GetBytes(q.ToString()), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            q.Append("&Signature=").Append(Uri.EscapeDataString(Convert.ToBase64String(signature)));
        }
        return q.ToString();
    }

    /// <summary>A fresh XML ID (must not start with a digit).</summary>
    public static string NewId() => "_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();

    public static string Instant(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

    public static string Escape(string s) => System.Security.SecurityElement.Escape(s) ?? "";

    public static XmlElement? Child(XmlElement parent, string ns, string localName) =>
        parent.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.NamespaceURI == ns && e.LocalName == localName);

    public static IEnumerable<XmlElement> Children(XmlElement parent, string ns, string localName) =>
        parent.ChildNodes.OfType<XmlElement>().Where(e => e.NamespaceURI == ns && e.LocalName == localName);

    public static IEnumerable<XmlElement> Descendants(XmlNode root, string ns, string localName) =>
        root.SelectNodes("//*")!.OfType<XmlElement>().Where(e => e.NamespaceURI == ns && e.LocalName == localName);

    public static string? Attr(XmlElement e, string name) => e.HasAttribute(name) ? e.GetAttribute(name) : null;
}

/// <summary>A SAML message was refused. <see cref="Code"/> is logged and audited; the message is safe to log (no secrets).</summary>
public sealed class SamlException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
