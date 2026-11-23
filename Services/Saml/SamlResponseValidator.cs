using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;

namespace ByteBench.Services.Saml;

/// <summary>A validated assertion: who the IdP says signed in, and what the replay and request checks need.</summary>
public sealed record SamlAssertion(string ResponseId, string InResponseTo, string AssertionId, string NameId, string? NameIdFormat,
    string? SessionIndex, IReadOnlyDictionary<string, IReadOnlyList<string>> Attributes, DateTime ExpiresAt)
{
    /// <summary>Values of the attribute whose Name or FriendlyName is <paramref name="name"/> (ignoring case).</summary>
    public IReadOnlyList<string> Values(string? name) =>
        string.IsNullOrWhiteSpace(name) ? [] : Attributes.TryGetValue(name.Trim(), out var v) ? v : [];

    public string? First(string? name) => Values(name).FirstOrDefault(v => v.Length > 0);
}

/// <summary>What the response must match: this SP and the configured IdP.</summary>
public sealed record SamlExpectations(string IdpEntityId, string SpEntityId, string AcsUrl, IReadOnlyList<X509Certificate2> Certificates,
    bool WantAssertionsSigned, TimeSpan ClockSkew, bool AllowSha1, DateTime NowUtc);

/// <summary>
/// Validates a SAML 2.0 <c>samlp:Response</c> (HTTP-POST binding) on <see cref="SignedXml"/>. Every rule throws a
/// <see cref="SamlException"/>:
/// <list type="bullet">
/// <item>Status must be Success; exactly one <c>saml:Assertion</c> in the whole document, a direct child of the Response;
/// no encrypted assertions or ids; no duplicate XML IDs.</item>
/// <item>Signatures are only looked for in two places, the Response's and the Assertion's direct <c>ds:Signature</c>
/// child. Each one present must verify against a configured certificate (never a key from the message), have exactly one
/// reference whose URI is <c>#</c> + the ID of the element it sits in, use only the enveloped-signature and exclusive-c14n
/// transforms and SHA-256 or better. At least one must be present; with WantAssertionsSigned the Assertion's. Because IDs
/// are unique and the reference must point at its own parent, the element whose signature was checked is the element
/// that is read (no signature wrapping).</item>
/// <item>Issuer (Response, if present, and Assertion) = the IdP entity id; Destination = the ACS URL (required when the
/// Response is signed); a bearer SubjectConfirmation with Recipient = the ACS URL, an unexpired NotOnOrAfter and a
/// matching InResponseTo; Conditions with NotBefore/NotOnOrAfter (with clock skew) and an AudienceRestriction naming
/// this SP; InResponseTo present (IdP-initiated responses are refused).</item>
/// </list>
/// The caller checks that InResponseTo is a request this server made, and that the assertion ID wasn't used before.
/// </summary>
public static class SamlResponseValidator
{
    private static readonly HashSet<string> Sha256Plus =
    [
        SignedXml.XmlDsigRSASHA256Url, SignedXml.XmlDsigRSASHA384Url, SignedXml.XmlDsigRSASHA512Url,
        SignedXml.XmlDsigSHA256Url, SignedXml.XmlDsigSHA384Url, SignedXml.XmlDsigSHA512Url,
    ];
    private static readonly HashSet<string> Sha1 = [SignedXml.XmlDsigRSASHA1Url, SignedXml.XmlDsigSHA1Url];
    private static readonly HashSet<string> Canonicalizations = [SignedXml.XmlDsigExcC14NTransformUrl, SignedXml.XmlDsigC14NTransformUrl];
    private static readonly HashSet<string> Transforms = [SignedXml.XmlDsigEnvelopedSignatureTransformUrl, SignedXml.XmlDsigExcC14NTransformUrl, SignedXml.XmlDsigC14NTransformUrl];

    public static SamlAssertion Validate(XmlDocument doc, SamlExpectations x)
    {
        var response = doc.DocumentElement;
        if (response == null || response.NamespaceURI != SamlXml.Protocol || response.LocalName != "Response")
            throw new SamlException("not_response", "The message isn't a SAML Response.");
        if (SamlXml.Attr(response, "Version") != "2.0") throw new SamlException("version", "Only SAML 2.0 responses are accepted.");

        // ----- Status first: an error response is often unsigned, and never signs anyone in.
        var statusCode = SamlXml.Child(response, SamlXml.Protocol, "Status") is { } status ? SamlXml.Child(status, SamlXml.Protocol, "StatusCode") : null;
        var statusValue = statusCode == null ? null : SamlXml.Attr(statusCode, "Value");
        if (statusValue != SamlXml.Success)
        {
            var sub = statusCode == null ? null : SamlXml.Child(statusCode, SamlXml.Protocol, "StatusCode") is { } s2 ? SamlXml.Attr(s2, "Value") : null;
            throw new SamlException("status", $"The identity provider answered with status {statusValue ?? "(none)"}{(sub == null ? "" : " / " + sub)}.");
        }

        // ----- Structure (signature wrapping defences).
        if (SamlXml.Descendants(doc, SamlXml.Assertion, "EncryptedAssertion").Any() || SamlXml.Descendants(doc, SamlXml.Assertion, "EncryptedID").Any())
            throw new SamlException("encrypted", "Encrypted assertions are not supported; turn assertion encryption off at the identity provider.");
        var assertions = SamlXml.Descendants(doc, SamlXml.Assertion, "Assertion").ToList();
        if (assertions.Count != 1) throw new SamlException("assertion_count", $"The response must contain exactly one assertion (it has {assertions.Count}).");
        var assertion = assertions[0];
        if (assertion.ParentNode != response) throw new SamlException("assertion_position", "The assertion must be a direct child of the response.");
        CheckUniqueIds(doc);

        var responseId = Required(response, "ID", "The response has no ID.");
        var assertionId = Required(assertion, "ID", "The assertion has no ID.");

        // ----- Signatures: only the two enveloped positions count.
        var responseSig = SamlXml.Child(response, SamlXml.DSig, "Signature");
        var assertionSig = SamlXml.Child(assertion, SamlXml.DSig, "Signature");
        if (responseSig == null && assertionSig == null) throw new SamlException("unsigned", "The response and assertion are not signed.");
        if (x.WantAssertionsSigned && assertionSig == null) throw new SamlException("assertion_unsigned", "The assertion is not signed (WantAssertionsSigned is on).");
        if (responseSig != null) Verify(doc, response, responseSig, x, "response");
        if (assertionSig != null) Verify(doc, assertion, assertionSig, x, "assertion");

        var now = x.NowUtc;
        var skew = x.ClockSkew;

        // ----- Response attributes.
        var destination = SamlXml.Attr(response, "Destination");
        if (destination != null && !UrlEquals(destination, x.AcsUrl)) throw new SamlException("destination", $"The response's Destination ({destination}) isn't this server's ACS URL.");
        if (destination == null && responseSig != null) throw new SamlException("destination", "A signed response must have a Destination.");
        if (SamlXml.Child(response, SamlXml.Assertion, "Issuer") is { } rIssuer && rIssuer.InnerText.Trim() != x.IdpEntityId)
            throw new SamlException("issuer", $"The response's Issuer ({rIssuer.InnerText.Trim()}) isn't the configured identity provider.");
        var inResponseTo = SamlXml.Attr(response, "InResponseTo");
        if (string.IsNullOrEmpty(inResponseTo)) throw new SamlException("unsolicited", "Unsolicited (IdP-initiated) responses are not accepted: start the sign-in from ByteBench.");
        if (Time(SamlXml.Attr(response, "IssueInstant")) is { } issued && issued > now + skew)
            throw new SamlException("issue_instant", "The response was issued in the future (check the clocks).");

        // ----- Assertion.
        if (SamlXml.Attr(assertion, "Version") != "2.0") throw new SamlException("version", "Only SAML 2.0 assertions are accepted.");
        var aIssuer = SamlXml.Child(assertion, SamlXml.Assertion, "Issuer")?.InnerText.Trim();
        if (aIssuer != x.IdpEntityId) throw new SamlException("issuer", $"The assertion's Issuer ({aIssuer ?? "none"}) isn't the configured identity provider.");

        var subject = SamlXml.Child(assertion, SamlXml.Assertion, "Subject") ?? throw new SamlException("subject", "The assertion has no Subject.");
        var nameIdEl = SamlXml.Child(subject, SamlXml.Assertion, "NameID") ?? throw new SamlException("subject", "The assertion has no NameID.");
        var nameId = nameIdEl.InnerText.Trim(); // InnerText: every text node, so a comment can't truncate the value
        if (nameId.Length == 0 || nameId.Length > 400) throw new SamlException("subject", "The NameID is empty or too long.");

        DateTime? confirmationExpiry = null;
        foreach (var sc in SamlXml.Children(subject, SamlXml.Assertion, "SubjectConfirmation"))
        {
            if (SamlXml.Attr(sc, "Method") != SamlXml.BearerMethod) continue;
            if (SamlXml.Child(sc, SamlXml.Assertion, "SubjectConfirmationData") is not { } data) continue;
            var recipient = SamlXml.Attr(data, "Recipient");
            var notOnOrAfter = Time(SamlXml.Attr(data, "NotOnOrAfter"));
            var notBefore = Time(SamlXml.Attr(data, "NotBefore"));
            var scInResponseTo = SamlXml.Attr(data, "InResponseTo");
            if (recipient == null || !UrlEquals(recipient, x.AcsUrl)) continue;
            if (notOnOrAfter == null || now >= notOnOrAfter.Value + skew) continue;
            if (notBefore != null && notBefore.Value > now + skew) continue;
            if (scInResponseTo != null && scInResponseTo != inResponseTo) continue;
            confirmationExpiry = notOnOrAfter;
            break;
        }
        if (confirmationExpiry == null)
            throw new SamlException("subject_confirmation", "No valid bearer SubjectConfirmation: wrong Recipient, expired, or answering a different request.");

        var conditions = SamlXml.Child(assertion, SamlXml.Assertion, "Conditions") ?? throw new SamlException("conditions", "The assertion has no Conditions.");
        var cNotBefore = Time(SamlXml.Attr(conditions, "NotBefore"));
        var cNotOnOrAfter = Time(SamlXml.Attr(conditions, "NotOnOrAfter"));
        if (cNotBefore != null && cNotBefore.Value > now + skew) throw new SamlException("not_yet_valid", "The assertion is not valid yet (NotBefore; check the clocks).");
        if (cNotOnOrAfter != null && now >= cNotOnOrAfter.Value + skew) throw new SamlException("expired", "The assertion has expired (NotOnOrAfter).");
        var restrictions = SamlXml.Children(conditions, SamlXml.Assertion, "AudienceRestriction").ToList();
        if (restrictions.Count == 0) throw new SamlException("audience", "The assertion has no AudienceRestriction.");
        foreach (var r in restrictions)
            if (!SamlXml.Children(r, SamlXml.Assertion, "Audience").Any(a => a.InnerText.Trim() == x.SpEntityId))
                throw new SamlException("audience", $"The assertion is not meant for this service provider ({x.SpEntityId}).");

        var authn = SamlXml.Child(assertion, SamlXml.Assertion, "AuthnStatement");
        var sessionIndex = authn == null ? null : SamlXml.Attr(authn, "SessionIndex");

        var attributes = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var statement in SamlXml.Children(assertion, SamlXml.Assertion, "AttributeStatement"))
            foreach (var attr in SamlXml.Children(statement, SamlXml.Assertion, "Attribute"))
            {
                var values = SamlXml.Children(attr, SamlXml.Assertion, "AttributeValue").Select(v => v.InnerText.Trim()).Where(v => v.Length > 0).ToList();
                foreach (var key in new[] { SamlXml.Attr(attr, "Name"), SamlXml.Attr(attr, "FriendlyName") }.OfType<string>().Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!attributes.TryGetValue(key, out var list)) attributes[key] = list = [];
                    list.AddRange(values);
                }
            }

        var expires = new[] { confirmationExpiry.Value, cNotOnOrAfter ?? confirmationExpiry.Value }.Min() + skew;
        return new SamlAssertion(responseId, inResponseTo, assertionId, nameId, SamlXml.Attr(nameIdEl, "Format"), sessionIndex,
            attributes.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.OrdinalIgnoreCase), expires);
    }

    private static void Verify(XmlDocument doc, XmlElement signed, XmlElement signature, SamlExpectations x, string what)
    {
        var id = signed.GetAttribute("ID");
        var signedXml = new SignedXml(doc);
        try { signedXml.LoadXml(signature); }
        catch (System.Security.Cryptography.CryptographicException) { throw new SamlException("signature", $"The {what} signature is malformed."); }

        var info = signedXml.SignedInfo ?? throw new SamlException("signature", $"The {what} signature has no SignedInfo.");
        if (info.References.Count != 1) throw new SamlException("signature", $"The {what} signature must have exactly one reference.");
        var reference = (Reference)info.References[0]!;
        if (reference.Uri != "#" + id) throw new SamlException("wrapping", $"The {what} signature doesn't reference the {what} it is in.");
        foreach (Transform t in reference.TransformChain)
            if (t.Algorithm == null || !Transforms.Contains(t.Algorithm)) throw new SamlException("signature", $"The {what} signature uses a transform that isn't allowed ({t.Algorithm}).");
        if (info.CanonicalizationMethod == null || !Canonicalizations.Contains(info.CanonicalizationMethod)) throw new SamlException("signature", $"The {what} signature's canonicalization isn't allowed.");
        bool Allowed(string? alg) => alg != null && (Sha256Plus.Contains(alg) || (x.AllowSha1 && Sha1.Contains(alg)));
        if (!Allowed(info.SignatureMethod) || !Allowed(reference.DigestMethod))
            throw new SamlException("weak_algorithm", $"The {what} signature uses {info.SignatureMethod} / {reference.DigestMethod}; SHA-256 or better is required.");

        foreach (var cert in x.Certificates)
        {
            using var key = cert.GetRSAPublicKey();
            if (key == null) continue;
            try { if (signedXml.CheckSignature(key)) return; }
            catch (System.Security.Cryptography.CryptographicException) { throw new SamlException("signature", $"The {what} signature couldn't be checked."); }
        }
        throw new SamlException("signature", $"The {what} signature is not valid for the configured IdP certificate(s).");
    }

    /// <summary>Any two elements with the same ID/Id/id would let a reference point at a different element than the one read.</summary>
    private static void CheckUniqueIds(XmlDocument doc)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in doc.SelectNodes("//*")!.OfType<XmlElement>())
            foreach (var name in new[] { "ID", "Id", "id" })
                if (e.GetAttributeNode(name) is { } a && !seen.Add(a.Value))
                    throw new SamlException("duplicate_id", "The response contains duplicate XML IDs.");
    }

    private static string Required(XmlElement e, string attr, string message) =>
        SamlXml.Attr(e, attr) is { Length: > 0 } v ? v : throw new SamlException("structure", message);

    private static DateTime? Time(string? s) =>
        s == null ? null
        : DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t
        : throw new SamlException("malformed", $"'{s}' isn't a valid SAML time.");

    private static bool UrlEquals(string a, string b) => string.Equals(a.Trim().TrimEnd('/'), b.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
