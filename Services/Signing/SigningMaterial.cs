using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Signing;

/// <summary>Where the push-time signature check (the <c>pre-receive</c> hook) finds the registered keys.</summary>
/// <param name="Dir">Holds <c>allowed_signers</c> (SSH), <c>gnupg/</c> (a keyring of the registered GPG keys) and <c>gpg-fprs</c> (primary fingerprint and owner email per line).</param>
public sealed record SigningMaterialInfo(string Dir, string? GpgPath, string? SshKeygenPath);

/// <summary>
/// Builds, for the push-time check, a snapshot of every usable registered signing key: an allowed-signers file for SSH keys and a
/// throw-away keyring for GPG keys, under <c>{repository root}/.signing/{hash}</c>. The folder name is a hash of its content (and
/// the day), so an unchanged key set is built once and shared by every push; it holds public keys only. Never the machine's keyring.
/// </summary>
public partial class SigningMaterial(IDbContextFactory<AppDbContext> dbFactory, SigningTools tools, RepoStorage storage)
{
    private const string Folder = ".signing";
    private static readonly SemaphoreSlim Build = new(1, 1);

    [GeneratedRegex(@"^[A-Za-z0-9._%+'-]+@[A-Za-z0-9.-]+$")]
    private static partial Regex SafeEmail();

    public async Task<SigningMaterialInfo> EnsureAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ssh = (await db.SshKeys.AsNoTracking().Include(k => k.User)
            .Where(k => k.UserId != null && (k.ExpiresAt == null || k.ExpiresAt > now)).ToListAsync(ct))
            .Where(k => k.User is { } u && u.EmailConfirmed && u.DeactivatedAt == null && !(u.LockoutEnd > now) && u.Email != null && SafeEmail().IsMatch(u.Email))
            .OrderBy(k => k.Id).ToList();
        var gpg = (await db.GpgKeys.AsNoTracking().Include(k => k.User)
            .Where(k => k.RevokedAt == null && (k.ExpiresAt == null || k.ExpiresAt > now)).ToListAsync(ct))
            .Where(k => k.User.EmailConfirmed && k.User.DeactivatedAt == null && !(k.User.LockoutEnd > now) && k.User.Email != null)
            .OrderBy(k => k.Id).ToList();

        var allowed = new StringBuilder();
        foreach (var k in ssh) allowed.Append(k.User!.Email!.ToLowerInvariant()).Append(" namespaces=\"git\" ").Append(k.KeyType).Append(' ').Append(k.PublicKey).Append('\n');
        var fprs = new StringBuilder();
        foreach (var k in gpg) fprs.Append(k.Fingerprint).Append(' ').Append(k.User.Email!.ToLowerInvariant()).Append('\n');

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            allowed + "\0" + fprs + "\0" + string.Join('\0', gpg.Select(k => k.PublicKey)) + "\0" + now.ToString("yyyy-MM-dd") + "\0" + tools.GpgPath + "\0" + tools.SshKeygenPath)))[..20].ToLowerInvariant();
        var root = Path.Combine(storage.Root, Folder);
        var dir = Path.Combine(root, hash);
        var info = new SigningMaterialInfo(dir, tools.GpgPath, tools.SshKeygenPath);
        if (Directory.Exists(dir)) return info;

        await Build.WaitAsync(ct);
        try
        {
            if (Directory.Exists(dir)) return info;
            Directory.CreateDirectory(root);
            var tmp = Path.Combine(root, ".tmp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            await File.WriteAllTextAsync(Path.Combine(tmp, "allowed_signers"), allowed.ToString(), ct);
            await File.WriteAllTextAsync(Path.Combine(tmp, "gpg-fprs"), fprs.ToString(), ct);
            Directory.CreateDirectory(Path.Combine(tmp, "gnupg"));
            if (!OperatingSystem.IsWindows())
            {
                var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                File.SetUnixFileMode(Path.Combine(tmp, "gnupg"), mode);
            }
            if (tools.GpgAvailable)
            {
                foreach (var k in gpg)
                {
                    var file = Path.Combine(tmp, "key-" + k.Id + ".asc");
                    await File.WriteAllTextAsync(file, k.PublicKey, ct);
                    await tools.RunGpgAsync(tmp, ["--import", file], null, ct); // a key gpg refuses just stays out of the keyring
                    File.Delete(file);
                }
            }
            try { Directory.Move(tmp, dir); }
            catch (IOException) { SigningTools.Delete(tmp); } // lost a race: the same content is already there
            Prune(root, dir);
        }
        finally { Build.Release(); }
        return info;
    }

    /// <summary>Removes snapshots older than two days (a running push uses at most the current one).</summary>
    private static void Prune(string root, string keep)
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(root))
                if (d != keep && Directory.GetCreationTimeUtc(d) < DateTime.UtcNow.AddDays(-2)) SigningTools.Delete(d);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
