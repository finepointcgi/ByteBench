using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ByteBench.Services.Push;

/// <summary>
/// Message encryption for Web Push (RFC 8291) with the <c>aes128gcm</c> content coding (RFC 8188), on
/// <see cref="System.Security.Cryptography"/> only: an ephemeral ECDH P-256 key per message, HKDF-SHA-256 and AES-128-GCM.
/// The output is the complete request body: salt, record size, the ephemeral public key and one encrypted record.
/// </summary>
public static class WebPushCrypto
{
    public const int RecordSize = 4096;
    /// <summary>The largest plaintext that fits one 4096-byte record (RFC 8291 section 4: 3993 bytes after the 103-byte overhead).</summary>
    public const int MaxPlaintext = 3993;

    /// <summary>Encrypts <paramref name="plaintext"/> for a subscription's <c>p256dh</c> (65-byte uncompressed point) and <c>auth</c> (16 bytes).</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, byte[] uaPublic, byte[] authSecret)
    {
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return Encrypt(plaintext, uaPublic, authSecret, ephemeral, RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>The deterministic core: the application server key pair and salt are given (the RFC 8291 test vector uses this).</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, byte[] uaPublic, byte[] authSecret, ECDiffieHellman asKey, byte[] salt)
    {
        if (plaintext.Length > MaxPlaintext) throw new ArgumentException("The push payload is too large.", nameof(plaintext));
        if (salt.Length != 16) throw new ArgumentException("The salt must be 16 bytes.", nameof(salt));
        var asPublic = PublicKeyBytes(asKey);
        var (cek, nonce) = DeriveKeys(asKey, uaPublic, asPublic, authSecret, salt);

        // One record, the last one: plaintext followed by the 0x02 delimiter (no further padding).
        var padded = new byte[plaintext.Length + 1];
        plaintext.CopyTo(padded);
        padded[^1] = 0x02;
        var cipher = new byte[padded.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(cek, 16)) gcm.Encrypt(nonce, padded, cipher, tag);

        // Header: salt (16) | rs (uint32 BE) | idlen (1) | keyid = as_public (65)
        var body = new byte[16 + 4 + 1 + asPublic.Length + cipher.Length + tag.Length];
        salt.CopyTo(body, 0);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(16), RecordSize);
        body[20] = (byte)asPublic.Length;
        asPublic.CopyTo(body, 21);
        cipher.CopyTo(body, 21 + asPublic.Length);
        tag.CopyTo(body, 21 + asPublic.Length + cipher.Length);
        return body;
    }

    /// <summary>Decrypts a body made by <see cref="Encrypt(ReadOnlySpan{byte}, byte[], byte[])"/> with the user agent's private key (what a browser does; used by tests).</summary>
    public static byte[] Decrypt(byte[] body, ECDiffieHellman uaKey, byte[] authSecret)
    {
        var salt = body[..16];
        var idlen = body[20];
        var asPublic = body[21..(21 + idlen)];
        var uaPublic = PublicKeyBytes(uaKey);
        using var asKey = ImportPublicKey(asPublic);
        var ecdh = uaKey.DeriveRawSecretAgreement(asKey.PublicKey);
        var (cek, nonce) = DeriveFromSecret(ecdh, uaPublic, asPublic, authSecret, salt);
        var payload = body[(21 + idlen)..];
        var cipher = payload[..^16];
        var tag = payload[^16..];
        var plain = new byte[cipher.Length];
        using (var gcm = new AesGcm(cek, 16)) gcm.Decrypt(nonce, cipher, tag, plain);
        var end = Array.LastIndexOf(plain, (byte)0x02);
        if (end < 0 || plain.Skip(end + 1).Any(b => b != 0)) throw new CryptographicException("Bad padding.");
        return plain[..end];
    }

    private static (byte[] Cek, byte[] Nonce) DeriveKeys(ECDiffieHellman asKey, byte[] uaPublic, byte[] asPublic, byte[] authSecret, byte[] salt)
    {
        using var ua = ImportPublicKey(uaPublic);
        var ecdhSecret = asKey.DeriveRawSecretAgreement(ua.PublicKey);
        return DeriveFromSecret(ecdhSecret, uaPublic, asPublic, authSecret, salt);
    }

    private static (byte[] Cek, byte[] Nonce) DeriveFromSecret(byte[] ecdhSecret, byte[] uaPublic, byte[] asPublic, byte[] authSecret, byte[] salt)
    {
        // key_info = "WebPush: info" || 0x00 || ua_public || as_public; IKM = HKDF(auth_secret, ecdh_secret, key_info, 32)
        var keyInfo = Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), uaPublic, asPublic);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, 32, authSecret, keyInfo);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));
        return (cek, nonce);
    }

    /// <summary>The uncompressed point (0x04 | X | Y, 65 bytes) of a P-256 key.</summary>
    public static byte[] PublicKeyBytes(ECAlgorithm key)
    {
        var p = key.ExportParameters(false).Q;
        return Concat([0x04], p.X!, p.Y!);
    }

    /// <summary>Imports a 65-byte uncompressed P-256 point; throws <see cref="CryptographicException"/> if it isn't a valid point.</summary>
    public static ECDiffieHellman ImportPublicKey(byte[] point)
    {
        if (point.Length != 65 || point[0] != 0x04) throw new CryptographicException("Not an uncompressed P-256 public key.");
        var key = ECDiffieHellman.Create();
        try
        {
            key.ImportParameters(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = point[1..33], Y = point[33..65] } });
            return key;
        }
        catch { key.Dispose(); throw; }
    }

    /// <summary>A P-256 private key from its raw 32-byte scalar (and public point).</summary>
    public static ECDiffieHellman ImportPrivateKey(byte[] d, byte[] point)
    {
        var key = ECDiffieHellman.Create();
        key.ImportParameters(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = d, Q = new ECPoint { X = point[1..33], Y = point[33..65] } });
        return key;
    }

    public static string Base64Url(ReadOnlySpan<byte> data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decodes base64url (or standard base64), with or without padding. Throws <see cref="FormatException"/>.</summary>
    public static byte[] FromBase64Url(string value)
    {
        var s = value.Trim().Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var p in parts) { p.CopyTo(result, offset); offset += p.Length; }
        return result;
    }
}
