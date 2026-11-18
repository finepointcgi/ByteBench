using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace GitBench.Services.Ssh;

/// <summary>
/// A parsed and validated OpenSSH public key (one <c>authorized_keys</c> line: <c>type base64 [comment]</c>).
/// Accepted: <c>ssh-ed25519</c>, <c>ecdsa-sha2-nistp256/384/521</c> and <c>ssh-rsa</c> of at least 2048 bits.
/// </summary>
public sealed record SshPublicKey(string KeyType, byte[] Blob, string? Comment, int Bits)
{
    public const int MinRsaBits = 2048;

    /// <summary>The key blob in base64, as stored and as in <c>authorized_keys</c>.</summary>
    public string Base64 => Convert.ToBase64String(Blob);

    /// <summary><c>SHA256:…</c>, the format <c>ssh-keygen -l</c> and the OpenSSH client print.</summary>
    public string Fingerprint => FingerprintOf(Blob);

    public static string FingerprintOf(ReadOnlySpan<byte> blob) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');

    /// <summary>A short description for the UI, e.g. "ED25519 (256-bit)".</summary>
    public string Description => $"{ShortType(KeyType)} ({Bits}-bit)";

    public static string ShortType(string keyType) => keyType switch
    {
        "ssh-ed25519" => "ED25519",
        "ssh-rsa" => "RSA",
        _ when keyType.StartsWith("ecdsa-", StringComparison.Ordinal) => "ECDSA",
        _ => keyType,
    };

    /// <summary>Parses a pasted public key, or throws <see cref="FormatException"/> with a message fit for the user.</summary>
    public static SshPublicKey Parse(string? text)
    {
        var line = (text ?? "").Trim();
        if (line.Length == 0) throw new FormatException("Paste a public key.");
        if (line.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw new FormatException("That is a private key. Paste the public key instead (the contents of the .pub file); never share your private key.");
        if (line.Contains('\n') || line.Contains('\r'))
        {
            // Allow a trailing newline but not several keys at once.
            var lines = line.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length != 1) throw new FormatException("Paste one public key at a time.");
            line = lines[0];
        }
        if (line.Length > 8192) throw new FormatException("That key is too long.");

        var parts = line.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new FormatException("A public key looks like 'ssh-ed25519 AAAAC3Nza… you@example.com'.");
        var type = parts[0];
        if (type.StartsWith("sk-", StringComparison.Ordinal))
            throw new FormatException("Security-key (FIDO) SSH keys aren't supported yet. Use an ed25519, ECDSA or RSA key.");
        if (type is not ("ssh-ed25519" or "ssh-rsa" or "ecdsa-sha2-nistp256" or "ecdsa-sha2-nistp384" or "ecdsa-sha2-nistp521"))
            throw new FormatException($"Unsupported key type '{Truncate(type)}'. Use ssh-ed25519 (recommended), ecdsa-sha2-nistp256/384/521 or ssh-rsa (2048 bits or more).");

        byte[] blob;
        try { blob = Convert.FromBase64String(parts[1]); }
        catch (FormatException) { throw new FormatException("The key data isn't valid base64. Copy the whole line from the .pub file."); }

        var comment = parts.Length > 2 ? parts[2].Trim() : null;
        var bits = Validate(type, blob);
        return new SshPublicKey(type, blob, string.IsNullOrEmpty(comment) ? null : comment, bits);
    }

    /// <summary>Checks the blob's structure against its type and returns its size in bits.</summary>
    private static int Validate(string type, byte[] blob)
    {
        var r = new Reader(blob);
        try
        {
            if (r.ReadString() != type) throw new FormatException("The key data doesn't match its type. Copy the whole line from the .pub file.");
            int bits;
            switch (type)
            {
                case "ssh-ed25519":
                    if (r.ReadBytes().Length != 32) throw new FormatException("Invalid ed25519 key.");
                    bits = 256;
                    break;
                case "ssh-rsa":
                {
                    var e = r.ReadBytes();
                    var n = r.ReadBytes();
                    var modulus = new BigInteger(n, isUnsigned: true, isBigEndian: true);
                    var exponent = new BigInteger(e, isUnsigned: true, isBigEndian: true);
                    if (modulus.Sign <= 0 || exponent < 3 || exponent.IsEven) throw new FormatException("Invalid RSA key.");
                    bits = (int)modulus.GetBitLength();
                    if (bits < MinRsaBits) throw new FormatException($"RSA keys must have at least {MinRsaBits} bits (this one has {bits}). Generate a new one with: ssh-keygen -t ed25519");
                    break;
                }
                default: // ecdsa-sha2-nistpNNN
                {
                    var curve = r.ReadString();
                    var point = r.ReadBytes();
                    var (expectedCurve, size) = type switch
                    {
                        "ecdsa-sha2-nistp256" => ("nistp256", 32),
                        "ecdsa-sha2-nistp384" => ("nistp384", 48),
                        _ => ("nistp521", 66),
                    };
                    if (curve != expectedCurve || point.Length != 1 + 2 * size || point[0] != 4) throw new FormatException("Invalid ECDSA key.");
                    bits = expectedCurve == "nistp521" ? 521 : size * 8;
                    break;
                }
            }
            if (!r.AtEnd) throw new FormatException("The key data has trailing bytes.");
            return bits;
        }
        catch (ArgumentOutOfRangeException) { throw new FormatException("The key data is truncated. Copy the whole line from the .pub file."); }
    }

    private static string Truncate(string s) => s.Length <= 40 ? s : s[..40] + "…";

    /// <summary>Reads SSH wire-format strings (uint32 length + bytes).</summary>
    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> rest = data;

        public readonly bool AtEnd => rest.IsEmpty;

        public byte[] ReadBytes()
        {
            if (rest.Length < 4) throw new ArgumentOutOfRangeException(nameof(data));
            var len = BinaryPrimitives.ReadUInt32BigEndian(rest);
            if (len > rest.Length - 4) throw new ArgumentOutOfRangeException(nameof(data));
            var value = rest.Slice(4, (int)len).ToArray();
            rest = rest[(4 + (int)len)..];
            return value;
        }

        public string ReadString() => Encoding.ASCII.GetString(ReadBytes());
    }
}
