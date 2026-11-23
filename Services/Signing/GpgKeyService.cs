using System.Globalization;
using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Services.Audit;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Signing;

/// <summary>A public key read from an armored block (see <see cref="GpgKeyParser"/>).</summary>
public sealed record ParsedGpgKey(string Fingerprint, IReadOnlyList<string> Fingerprints, IReadOnlyList<string> Emails, string Algorithm,
    DateTime? ExpiresAt, bool Revoked, bool Expired, int PrimaryKeys);

/// <summary>Reads <c>gpg --show-keys --with-colons</c> output (a stable, documented format) into a <see cref="ParsedGpgKey"/>.</summary>
public static partial class GpgKeyParser
{
    [GeneratedRegex(@"<([^<>\s]+@[^<>\s]+)>")]
    private static partial Regex AngleEmail();

    [GeneratedRegex(@"^[^<>\s@]+@[^<>\s@]+\.[^<>\s@]+$")]
    private static partial Regex BareEmail();

    public static ParsedGpgKey? Parse(string colons)
    {
        string? primary = null, algorithm = null;
        DateTime? expires = null;
        bool revoked = false, expired = false, inPrimary = false, expectFpr = false;
        var primaries = 0;
        var fprs = new List<string>();
        var emails = new List<string>();
        foreach (var raw in colons.Split('\n'))
        {
            var f = raw.TrimEnd('\r').Split(':');
            switch (f[0])
            {
                case "pub":
                    primaries++;
                    if (primaries > 1) { inPrimary = false; expectFpr = false; break; }
                    inPrimary = true;
                    expectFpr = true;
                    revoked = f.Length > 1 && f[1] == "r";
                    expired = f.Length > 1 && f[1] == "e";
                    if (f.Length > 6 && long.TryParse(f[6], NumberStyles.None, CultureInfo.InvariantCulture, out var exp) && exp > 0)
                        expires = DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;
                    algorithm = AlgorithmName(f.Length > 3 ? f[3] : "", f.Length > 2 ? f[2] : "", f.Length > 16 ? f[16] : "");
                    break;
                case "sub":
                    expectFpr = inPrimary;
                    break;
                case "fpr":
                    if (inPrimary && expectFpr && f.Length > 9 && f[9].Length >= 32)
                    {
                        var fpr = f[9].ToUpperInvariant();
                        fprs.Add(fpr);
                        primary ??= fpr;
                    }
                    expectFpr = false;
                    break;
                case "uid":
                    if (inPrimary && f.Length > 9 && f.Length > 1 && f[1] is not ("r" or "e") && AngleEmail().Match(f[9]) is { Success: true } m)
                        emails.Add(m.Groups[1].Value.ToLowerInvariant());
                    else if (inPrimary && f.Length > 9 && f[1] is not ("r" or "e") && BareEmail().IsMatch(f[9].Trim()))
                        emails.Add(f[9].Trim().ToLowerInvariant());
                    break;
            }
        }
        return primary == null ? null : new ParsedGpgKey(primary, fprs, emails.Distinct().ToList(), algorithm ?? "", expires, revoked, expired, primaries);
    }

    private static string AlgorithmName(string algo, string bits, string curve) => algo switch
    {
        "1" or "2" or "3" => "RSA " + bits,
        "17" => "DSA " + bits,
        "19" => "ECDSA " + (curve.Length > 0 ? curve : bits),
        "22" => curve.Length > 0 ? curve : "EdDSA",
        "18" => "ECDH " + curve,
        _ => string.IsNullOrEmpty(algo) ? "" : "algorithm " + algo,
    };
}

/// <summary>
/// Users' OpenPGP signing keys (page: <c>/account/ssh-keys</c>, "GPG keys"). A key is checked once with <c>gpg --show-keys</c> in a
/// throw-away home directory: it must be a single, public, unexpired, unrevoked key with a user ID for the account's email. It is
/// stored as text and only ever imported into a throw-away keyring while a signature is verified. A user can revoke or delete
/// their own keys; revoked keys never verify anything.
/// </summary>
public class GpgKeyService(IDbContextFactory<AppDbContext> dbFactory, SigningTools tools, AuditLog audit)
{
    public const int MaxKeysPerUser = 50;
    private const int MaxKeyChars = 64 * 1024;

    public async Task<List<GpgKey>> ListAsync(int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.GpgKeys.AsNoTracking().Where(k => k.UserId == userId).OrderByDescending(k => k.CreatedAt).ToListAsync();
    }

    /// <summary>Adds the pasted public key block to the user's account. Throws <see cref="InvalidOperationException"/> with a user-facing message.</summary>
    public async Task<GpgKey> AddAsync(int userId, string title, string armored, CancellationToken ct = default)
    {
        armored = (armored ?? "").Replace("\r\n", "\n").Trim();
        if (armored.Length == 0) throw new InvalidOperationException("Paste the armored public key (it starts with -----BEGIN PGP PUBLIC KEY BLOCK-----).");
        if (armored.Length > MaxKeyChars) throw new InvalidOperationException("That key is too large.");
        if (armored.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("That is a PRIVATE key. Never upload it anywhere. Export the public key with: gpg --armor --export <key id>");
        if (!armored.StartsWith("-----BEGIN PGP PUBLIC KEY BLOCK-----", StringComparison.Ordinal) || !armored.EndsWith("-----END PGP PUBLIC KEY BLOCK-----", StringComparison.Ordinal))
            throw new InvalidOperationException("That doesn't look like an armored public key (it starts with -----BEGIN PGP PUBLIC KEY BLOCK----- and ends with -----END PGP PUBLIC KEY BLOCK-----).");
        if (!tools.GpgAvailable)
            throw new InvalidOperationException("GPG keys can't be added: gpg isn't installed on this server, so signatures couldn't be verified anyway.");

        var parsed = await ShowKeysAsync(armored, ct);
        if (parsed.PrimaryKeys != 1) throw new InvalidOperationException("Paste exactly one public key.");
        if (parsed.Revoked) throw new InvalidOperationException("That key is revoked.");
        if (parsed.Expired || parsed.ExpiresAt is { } e && e <= DateTime.UtcNow) throw new InvalidOperationException("That key has expired. Extend its expiry date and export it again.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new InvalidOperationException("User not found.");
        if (!user.EmailConfirmed || string.IsNullOrEmpty(user.Email))
            throw new InvalidOperationException("Confirm your email address first: a commit is only Verified when the key belongs to a confirmed email of yours.");
        if (!parsed.Emails.Contains(user.Email.ToLowerInvariant()))
            throw new InvalidOperationException($"None of the key's user IDs has your account email ({user.Email}). Add it with `gpg --edit-key {parsed.Fingerprint} adduid`, then export the key again.");
        if (await db.GpgKeys.CountAsync(k => k.UserId == userId, ct) >= MaxKeysPerUser)
            throw new InvalidOperationException($"You can have at most {MaxKeysPerUser} GPG keys.");
        if (await db.GpgKeys.AnyAsync(k => k.Fingerprint == parsed.Fingerprint, ct))
            throw new InvalidOperationException("This key is already registered, on your account or another one.");

        var t = (title ?? "").Trim();
        if (t.Length == 0) t = "GPG key " + parsed.Fingerprint[^16..];
        if (t.Length > 100) t = t[..100];
        var key = new GpgKey
        {
            UserId = userId, Title = t, Fingerprint = parsed.Fingerprint, KeyFingerprints = string.Join(' ', parsed.Fingerprints),
            Emails = string.Join(' ', parsed.Emails), PublicKey = armored, Algorithm = parsed.Algorithm, ExpiresAt = parsed.ExpiresAt,
        };
        db.GpgKeys.Add(key);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw new InvalidOperationException("This key is already registered, on your account or another one."); }
        await audit.RecordAsync("gpg_key.added", "gpg_key", key.Id, details: new { title = t, fingerprint = key.Fingerprint, algorithm = key.Algorithm, expiresAt = key.ExpiresAt }, actorId: userId);
        return key;
    }

    /// <summary>Marks the key revoked: commits signed with it stop being Verified. Irreversible (upload a new key instead).</summary>
    public async Task<bool> RevokeAsync(int userId, int keyId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        var n = await db.GpgKeys.Where(k => k.Id == keyId && k.UserId == userId && k.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, now));
        if (n == 0) return false;
        var fp = await db.GpgKeys.Where(k => k.Id == keyId).Select(k => k.Fingerprint).FirstAsync();
        await audit.RecordAsync("gpg_key.revoked", "gpg_key", keyId, details: new { fingerprint = fp }, actorId: userId);
        return true;
    }

    public async Task DeleteAsync(int userId, int keyId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var key = await db.GpgKeys.FirstOrDefaultAsync(k => k.Id == keyId && k.UserId == userId);
        if (key == null) return;
        db.GpgKeys.Remove(key);
        await db.SaveChangesAsync();
        await audit.RecordAsync("gpg_key.removed", "gpg_key", keyId, details: new { title = key.Title, fingerprint = key.Fingerprint }, actorId: userId);
    }

    /// <summary>Reads the block with <c>gpg --show-keys</c> in an empty throw-away home (nothing is imported anywhere).</summary>
    internal async Task<ParsedGpgKey> ShowKeysAsync(string armored, CancellationToken ct)
    {
        var home = SigningTools.NewTempDir();
        try
        {
            var r = await tools.RunGpgAsync(home, ["--show-keys", "--with-colons", "--fixed-list-mode", "--with-fingerprint", "--with-subkey-fingerprints"],
                System.Text.Encoding.UTF8.GetBytes(armored), ct);
            if (!r.Ok) throw new InvalidOperationException("gpg couldn't read that key. Check that you copied the whole block.");
            return GpgKeyParser.Parse(r.Stdout) ?? throw new InvalidOperationException("gpg found no public key in that text.");
        }
        finally { SigningTools.Delete(home); }
    }
}
