using System.Text;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.IO;
using SshBuffer = Microsoft.DevTunnels.Ssh.Buffer;

namespace ByteBench.Services.Ssh;

/// <summary>
/// <c>ssh-ed25519</c> (RFC 8709) client-key verification for Microsoft.DevTunnels.Ssh, which ships RSA and ECDSA only.
/// Signatures are checked with BouncyCastle. Verify-only: ByteBench's host key is RSA, so nothing is ever signed with it.
/// </summary>
internal sealed class Ed25519Algorithm() : PublicKeyAlgorithm(Name, Name, "SHA512")
{
    public new const string Name = "ssh-ed25519";

    public override IKeyPair CreateKeyPair() => new KeyPair();

    public override IKeyPair GenerateKeyPair(int? keySizeInBits = null) =>
        throw new NotSupportedException("ByteBench only verifies ed25519 keys.");

    public override ISigner CreateSigner(IKeyPair keyPair) =>
        throw new NotSupportedException("ByteBench only verifies ed25519 keys.");

    public override IVerifier CreateVerifier(IKeyPair keyPair) => new Verifier((KeyPair)keyPair);

    private sealed class KeyPair : IKeyPair
    {
        public byte[] PublicKey { get; private set; } = [];
        public string KeyAlgorithmName => Name;
        public bool HasPrivateKey => false;
        public string? Comment { get; set; }

        public void SetPublicKeyBytes(SshBuffer keyBytes)
        {
            var reader = new SshDataReader(keyBytes);
            if (reader.ReadString(Encoding.ASCII) != Name) throw new ArgumentException("Not an ssh-ed25519 key.");
            var key = reader.ReadBinary();
            if (key.Count != 32) throw new ArgumentException("Invalid ssh-ed25519 key length.");
            PublicKey = key.ToArray();
        }

        public SshBuffer GetPublicKeyBytes(string? algorithmName = null)
        {
            SshDataWriter writer = default;
            writer.Write(Name, Encoding.ASCII);
            writer.WriteBinary(SshBuffer.From(PublicKey));
            return writer.ToBuffer();
        }

        public void Dispose() { }
    }

    private sealed class Verifier(KeyPair key) : IVerifier
    {
        public int DigestLength => 64;

        public bool Verify(SshBuffer data, SshBuffer signature)
        {
            if (signature.Count != 64 || key.PublicKey.Length != 32) return false;
            return Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.Verify(signature.ToArray(), 0, key.PublicKey, 0, data.ToArray(), 0, data.Count);
        }

        public void Dispose() { }
    }
}
