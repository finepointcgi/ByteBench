using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Services.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Signing;

/// <summary>A commit's verification state as shown in the UI and used by branch protection.</summary>
/// <param name="Format">"gpg", "ssh", "bytebench" (a merge ByteBench itself made) or null when unsigned.</param>
public sealed record CommitSignatureResult(string Sha, CommitSignatureStatus Status, string? Format, string? Reason, int? SignerUserId,
    string? SignerName, string? KeyFingerprint)
{
    public bool IsVerified => Status == CommitSignatureStatus.Verified;
    public bool IsSigned => Status != CommitSignatureStatus.Unsigned;

    /// <summary>The badge text: "Verified", "Unverified", "Unverifiable" or empty for an unsigned commit.</summary>
    public string Label => Status switch
    {
        CommitSignatureStatus.Verified => "Verified",
        CommitSignatureStatus.Unverified => "Unverified",
        CommitSignatureStatus.Unverifiable => "Unverifiable",
        _ => "",
    };

    /// <summary>The tooltip: "Verified" with who signed, or "Unverified (reason)".</summary>
    public string Tooltip => Status switch
    {
        CommitSignatureStatus.Verified when Format == "bytebench" => "Verified: this commit was created by ByteBench's own merge, not by a signature",
        CommitSignatureStatus.Verified => $"Verified: good {(Format == "ssh" ? "SSH" : "GPG")} signature by {SignerName ?? "a registered key"}" +
            (KeyFingerprint == null ? "" : $" ({KeyFingerprint})") + ", and the committer email is one of their confirmed emails",
        CommitSignatureStatus.Unverified => $"Unverified ({Reason})",
        CommitSignatureStatus.Unverifiable => $"Unverifiable ({Reason})",
        _ => "Not signed",
    };

    public static CommitSignatureResult Unchecked(string sha, string reason) => new(sha, CommitSignatureStatus.Unverifiable, null, reason, null, null, null);
}

/// <summary>The pieces of a signed commit object (see <see cref="CommitSignatureParser"/>).</summary>
public sealed record SignedCommit(string Format, string Signature, byte[] Payload, string CommitterEmail);

/// <summary>Splits a raw commit object into its signature and the signed payload (the commit without its <c>gpgsig</c> header).</summary>
public static class CommitSignatureParser
{
    /// <summary>Null for an unsigned commit. Commit text is handled as Latin-1 so every byte survives unchanged.</summary>
    public static SignedCommit? Parse(byte[] raw, out string? unsupported)
    {
        unsupported = null;
        var text = Encoding.Latin1.GetString(raw);
        var headerEnd = text.IndexOf("\n\n", StringComparison.Ordinal);
        var header = headerEnd < 0 ? text : text[..headerEnd];
        var rest = headerEnd < 0 ? "" : text[headerEnd..];
        var lines = header.Split('\n');
        var kept = new List<string>();
        var sig = new List<string>();
        var inSig = false;
        string? email = null;
        foreach (var line in lines)
        {
            if (inSig)
            {
                if (line.StartsWith(' ')) { sig.Add(line[1..]); continue; }
                inSig = false;
            }
            if (line.StartsWith("gpgsig ", StringComparison.Ordinal) && sig.Count == 0)
            {
                inSig = true;
                sig.Add(line["gpgsig ".Length..]);
                continue;
            }
            if (line.StartsWith("gpgsig-sha256 ", StringComparison.Ordinal)) unsupported = "SHA-256 signature headers aren't supported";
            if (email == null && line.StartsWith("committer ", StringComparison.Ordinal))
            {
                var lt = line.IndexOf('<');
                var gt = line.IndexOf('>', lt + 1);
                if (lt >= 0 && gt > lt) email = Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(line[(lt + 1)..gt]));
            }
            kept.Add(line);
        }
        if (sig.Count == 0) return null;
        var signature = string.Join('\n', sig) + "\n";
        var format = signature.StartsWith("-----BEGIN SSH SIGNATURE-----", StringComparison.Ordinal) ? "ssh"
            : signature.StartsWith("-----BEGIN PGP SIGNATURE-----", StringComparison.Ordinal) ? "gpg" : "unknown";
        var payload = Encoding.Latin1.GetBytes(string.Join('\n', kept) + rest);
        return new SignedCommit(format, signature, payload, email ?? "");
    }
}

/// <summary>
/// Verifies commit signatures without trusting the pusher. A commit is <b>Verified</b> only when
/// <list type="number">
/// <item>its signature is mathematically good (<c>ssh-keygen -Y verify</c> or <c>gpg --verify</c>) against a key registered on this server,</item>
/// <item>that key belongs to a user (an SSH key on their account, or a GPG key of theirs) and is neither revoked nor expired,</item>
/// <item>the user's account is active, and the committer email equals the user's confirmed email.</item>
/// </list>
/// Verification runs in a throw-away directory with a throw-away keyring / allowed-signers file built from the database; the
/// machine's keyrings and <c>~/.ssh</c> are never used. Without gpg / ssh-keygen a signed commit is "unverifiable". Results are
/// cached per commit sha (and recomputed when keys change, a key expires, or after <see cref="SigningOptions.CacheHours"/>).
/// Commits that ByteBench's own merge service created (the merge commit of a merged pull request) count as Verified with
/// format <c>bytebench</c>: ByteBench has no server signing key, it vouches for its own merges instead.
/// </summary>
public partial class CommitSignatureService(IDbContextFactory<AppDbContext> dbFactory, SigningTools tools, RepoStorage storage, IConfiguration config,
    IOptions<SigningOptions> options, ILogger<CommitSignatureService> log)
{
    private readonly SigningOptions opts = options.Value;
    private string? gitPath;
    private bool gitLooked;

    private string? GitPath
    {
        get
        {
            if (!gitLooked) { gitPath = GitHttpBackend.LocateGit(config); gitLooked = true; }
            return gitPath;
        }
    }

    public bool GpgAvailable => tools.GpgAvailable;
    public bool SshAvailable => tools.SshAvailable;

    [GeneratedRegex(@"Good ""git"" signature with \S+ key (SHA256:[A-Za-z0-9+/=]+)")]
    private static partial Regex SshGood();

    [GeneratedRegex(@"keyid ([0-9A-Fa-f]{16})")]
    private static partial Regex GpgKeyIdPattern();

    [GeneratedRegex(@"issuer fpr v\d+ ([0-9A-Fa-f]{40,64})")]
    private static partial Regex GpgIssuerFpr();

    [GeneratedRegex(@"issuer key ID ([0-9A-Fa-f]{16})")]
    private static partial Regex GpgIssuerKeyId();

    [GeneratedRegex(@"^[A-Za-z0-9._%+'-]+@[A-Za-z0-9.-]+$")]
    private static partial Regex SafePrincipal();

    // ---------- Public API ----------

    public async Task<CommitSignatureResult> GetAsync(Repository repo, string sha, CancellationToken ct = default) =>
        (await GetManyAsync(repo, [sha], ct)).TryGetValue(sha, out var r) ? r : CommitSignatureResult.Unchecked(sha, "the commit couldn't be read");

    /// <summary>Verifies (or reads from the cache) every sha. Past <see cref="SigningOptions.MaxCommitsPerCheck"/> new checks the rest are "unverifiable (not checked)".</summary>
    public async Task<IReadOnlyDictionary<string, CommitSignatureResult>> GetManyAsync(Repository repo, IEnumerable<string> shas, CancellationToken ct = default)
    {
        var wanted = shas.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
        var results = new Dictionary<string, CommitSignatureResult>();
        if (wanted.Count == 0) return results;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var stamp = await KeysStampAsync(db, ct);
        var now = DateTime.UtcNow;
        var cacheLimit = now - TimeSpan.FromHours(Math.Max(1, opts.CacheHours));
        var cached = await db.CommitSignatures.AsNoTracking().Where(c => wanted.Contains(c.Sha)).ToListAsync(ct);
        var names = new Dictionary<int, string>();
        var todo = new List<string>();
        foreach (var sha in wanted)
        {
            var c = cached.FirstOrDefault(x => x.Sha == sha);
            if (c != null && c.KeysStamp == stamp && c.VerifiedAt > cacheLimit && (c.ValidUntil == null || c.ValidUntil > now))
                results[sha] = await ToResultAsync(db, c, names, ct);
            else todo.Add(sha);
        }

        if (todo.Count > 0)
        {
            var path = storage.PathFor(repo.Project.Key, repo.Slug);
            var raws = await ReadCommitsAsync(path, todo.Take(opts.MaxCommitsPerCheck).ToList(), ct);
            foreach (var sha in todo)
            {
                if (!raws.TryGetValue(sha, out var raw))
                {
                    results[sha] = CommitSignatureResult.Unchecked(sha, todo.IndexOf(sha) >= opts.MaxCommitsPerCheck ? "not checked: too many commits" : "the commit couldn't be read");
                    continue;
                }
                var fresh = await VerifyAsync(db, sha, raw, ct);
                var row = await db.CommitSignatures.FirstOrDefaultAsync(c => c.Sha == sha, ct);
                if (row == null) db.CommitSignatures.Add(row = new CommitSignature { Sha = sha });
                row.Status = fresh.Result.Status;
                row.Format = fresh.Result.Format;
                row.Reason = fresh.Result.Reason;
                row.SignerUserId = fresh.Result.SignerUserId;
                row.KeyFingerprint = fresh.Result.KeyFingerprint;
                row.VerifiedAt = now;
                row.ValidUntil = fresh.ValidUntil;
                row.KeysStamp = stamp;
                try { await db.SaveChangesAsync(ct); }
                catch (DbUpdateException) { db.Entry(row).State = EntityState.Detached; } // another request cached the same commit first
                results[sha] = fresh.Result;
            }
        }

        // ByteBench's own merge commits (server-side merges aren't signed).
        var unsigned = results.Values.Where(r => !r.IsVerified).Select(r => r.Sha).ToList();
        if (unsigned.Count > 0)
        {
            var trusted = await db.PullRequests.AsNoTracking()
                .Where(p => p.RepositoryId == repo.Id && p.State == PullRequestState.Merged && p.MergeCommitSha != null && unsigned.Contains(p.MergeCommitSha))
                .Select(p => p.MergeCommitSha!).ToListAsync(ct);
            foreach (var sha in trusted)
                if (results[sha].Status is CommitSignatureStatus.Unsigned)
                    results[sha] = new CommitSignatureResult(sha, CommitSignatureStatus.Verified, "bytebench", null, null, "ByteBench", null);
        }
        return results;
    }

    // ---------- Verification ----------

    private sealed record Verification(CommitSignatureResult Result, DateTime? ValidUntil = null);

    private static Verification Fail(string sha, CommitSignatureStatus status, string format, string reason, int? userId = null, string? fp = null, string? name = null) =>
        new(new CommitSignatureResult(sha, status, format, reason, userId, name, fp));

    private async Task<Verification> VerifyAsync(AppDbContext db, string sha, byte[] raw, CancellationToken ct)
    {
        var signed = CommitSignatureParser.Parse(raw, out var unsupported);
        if (signed == null)
            return unsupported != null ? Fail(sha, CommitSignatureStatus.Unverifiable, "unknown", unsupported)
                : new Verification(new CommitSignatureResult(sha, CommitSignatureStatus.Unsigned, null, null, null, null, null));
        if (signed.CommitterEmail.Length == 0) return Fail(sha, CommitSignatureStatus.Unverified, signed.Format, "the commit has no committer email");
        try
        {
            return signed.Format switch
            {
                "ssh" => await VerifySshAsync(db, sha, signed, ct),
                "gpg" => await VerifyGpgAsync(db, sha, signed, ct),
                _ => Fail(sha, CommitSignatureStatus.Unverifiable, "unknown", "the signature format isn't supported"),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not verify the signature of {Sha}", sha);
            return Fail(sha, CommitSignatureStatus.Unverifiable, signed.Format, "the signature check couldn't run on this server");
        }
    }

    private async Task<Verification> VerifySshAsync(AppDbContext db, string sha, SignedCommit signed, CancellationToken ct)
    {
        if (!tools.SshAvailable) return Fail(sha, CommitSignatureStatus.Unverifiable, "ssh", "ssh-keygen isn't installed on this server");
        var home = SigningTools.NewTempDir();
        try
        {
            var sigFile = Path.Combine(home, "commit.sig");
            await File.WriteAllTextAsync(sigFile, signed.Signature, ct);
            // 1. Who signed? (check-novalidate proves the signature is good for the embedded key and prints the key's fingerprint.)
            var probe = await tools.RunSshKeygenAsync(home, ["-Y", "check-novalidate", "-n", "git", "-s", sigFile], signed.Payload, ct);
            if (probe.TimedOut) return Fail(sha, CommitSignatureStatus.Unverifiable, "ssh", "the signature check timed out");
            var m = SshGood().Match(probe.Stdout + "\n" + probe.Stderr);
            if (!probe.Ok || !m.Success) return Fail(sha, CommitSignatureStatus.Unverified, "ssh", "the signature doesn't match the commit, or isn't a valid SSH signature");
            var fingerprint = m.Groups[1].Value;

            // 2. Is it a registered user key, and does its owner own the committer email?
            var key = await db.SshKeys.AsNoTracking().Include(k => k.User).FirstOrDefaultAsync(k => k.Fingerprint == fingerprint, ct);
            if (key == null) return Fail(sha, CommitSignatureStatus.Unverified, "ssh", "signed with an SSH key that isn't registered on this server", fp: fingerprint);
            if (key.UserId == null || key.User == null) return Fail(sha, CommitSignatureStatus.Unverified, "ssh", "the key is a deploy key, not a user's signing key", fp: fingerprint);
            var now = DateTime.UtcNow;
            var owner = key.User;
            if (key.IsExpired(now)) return Fail(sha, CommitSignatureStatus.Unverified, "ssh", $"the signing key expired on {key.ExpiresAt:yyyy-MM-dd}", owner.Id, fingerprint, owner.DisplayName);
            if (owner.DeactivatedAt != null || owner.LockoutEnd > now) return Fail(sha, CommitSignatureStatus.Unverified, "ssh", "the key's owner account is deactivated or locked", owner.Id, fingerprint, owner.DisplayName);
            if (!owner.EmailConfirmed || !string.Equals(owner.Email, signed.CommitterEmail, StringComparison.OrdinalIgnoreCase))
                return Fail(sha, CommitSignatureStatus.Unverified, "ssh", $"the committer email ({signed.CommitterEmail}) isn't a confirmed email of {owner.DisplayName}", owner.Id, fingerprint, owner.DisplayName);
            if (!SafePrincipal().IsMatch(signed.CommitterEmail)) return Fail(sha, CommitSignatureStatus.Unverified, "ssh", "the committer email contains unsupported characters", owner.Id, fingerprint, owner.DisplayName);

            // 3. The real check, against an allowed-signers file that holds only this one (key, email) pair.
            var allowed = Path.Combine(home, "allowed_signers");
            await File.WriteAllTextAsync(allowed, $"{signed.CommitterEmail.ToLowerInvariant()} namespaces=\"git\" {key.KeyType} {key.PublicKey}\n", ct);
            var verify = await tools.RunSshKeygenAsync(home, ["-Y", "verify", "-f", allowed, "-I", signed.CommitterEmail.ToLowerInvariant(), "-n", "git", "-s", sigFile], signed.Payload, ct);
            if (!verify.Ok) return Fail(sha, CommitSignatureStatus.Unverified, "ssh", "the signature didn't verify against the owner's registered key", owner.Id, fingerprint, owner.DisplayName);
            return new Verification(new CommitSignatureResult(sha, CommitSignatureStatus.Verified, "ssh", null, owner.Id, owner.DisplayName, fingerprint), key.ExpiresAt);
        }
        finally { SigningTools.Delete(home); }
    }

    private async Task<Verification> VerifyGpgAsync(AppDbContext db, string sha, SignedCommit signed, CancellationToken ct)
    {
        if (!tools.GpgAvailable) return Fail(sha, CommitSignatureStatus.Unverifiable, "gpg", "gpg isn't installed on this server");
        var home = SigningTools.NewTempDir();
        try
        {
            var sigFile = Path.Combine(home, "commit.sig");
            var payloadFile = Path.Combine(home, "commit.txt");
            await File.WriteAllTextAsync(sigFile, signed.Signature, ct);
            await File.WriteAllBytesAsync(payloadFile, signed.Payload, ct);

            // 1. Which key made it? (Read from the signature itself; the key is looked up in the database, never a keyring.)
            var packets = await tools.RunGpgAsync(home, ["--list-packets", sigFile], null, ct);
            var text = packets.Stdout + "\n" + packets.Stderr;
            var ids = GpgKeyIdPattern().Matches(text).Select(m => m.Groups[1].Value)
                .Concat(GpgIssuerKeyId().Matches(text).Select(m => m.Groups[1].Value))
                .Concat(GpgIssuerFpr().Matches(text).Select(m => m.Groups[1].Value)).Select(s => s.ToUpperInvariant()).Distinct().ToList();
            if (ids.Count == 0) return Fail(sha, CommitSignatureStatus.Unverified, "gpg", "the signature doesn't say which key made it");
            var candidates = await db.GpgKeys.AsNoTracking().Include(k => k.User).ToListAsync(ct);
            var key = candidates.FirstOrDefault(k => k.KeyFingerprints.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(f => ids.Any(id => f.EndsWith(id, StringComparison.OrdinalIgnoreCase) || id.EndsWith(f, StringComparison.OrdinalIgnoreCase))));
            if (key == null) return Fail(sha, CommitSignatureStatus.Unverified, "gpg", "signed with a GPG key that isn't registered on this server", fp: ids[0]);

            // 2. The key's own state and its owner.
            var now = DateTime.UtcNow;
            var owner = key.User;
            string? Problem() =>
                key.RevokedAt != null ? "the signing key was revoked"
                : key.IsExpired(now) ? $"the signing key expired on {key.ExpiresAt:yyyy-MM-dd}"
                : owner.DeactivatedAt != null || owner.LockoutEnd > now ? "the key's owner account is deactivated or locked"
                : !owner.EmailConfirmed || !string.Equals(owner.Email, signed.CommitterEmail, StringComparison.OrdinalIgnoreCase)
                    ? $"the committer email ({signed.CommitterEmail}) isn't a confirmed email of {owner.DisplayName}"
                : !key.Emails.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(signed.CommitterEmail.ToLowerInvariant())
                    ? "the key has no user ID for the committer email"
                : null;
            if (Problem() is { } why) return Fail(sha, CommitSignatureStatus.Unverified, "gpg", why, owner.Id, key.Fingerprint, owner.DisplayName);

            // 3. The real check, in a keyring that holds only this key.
            var keyFile = Path.Combine(home, "key.asc");
            await File.WriteAllTextAsync(keyFile, key.PublicKey, ct);
            var import = await tools.RunGpgAsync(home, ["--import", keyFile], null, ct);
            if (!import.Ok) return Fail(sha, CommitSignatureStatus.Unverified, "gpg", "the registered key couldn't be loaded", owner.Id, key.Fingerprint, owner.DisplayName);
            var verify = await tools.RunGpgAsync(home, ["--status-fd", "1", "--verify", sigFile, payloadFile], null, ct);
            var status = verify.Stdout.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("[GNUPG:] ", StringComparison.Ordinal)).Select(l => l["[GNUPG:] ".Length..]).ToList();
            if (status.Any(l => l.StartsWith("REVKEYSIG ", StringComparison.Ordinal))) return Fail(sha, CommitSignatureStatus.Unverified, "gpg", "the signing key was revoked", owner.Id, key.Fingerprint, owner.DisplayName);
            if (status.Any(l => l.StartsWith("EXPKEYSIG ", StringComparison.Ordinal) || l.StartsWith("EXPSIG ", StringComparison.Ordinal))) return Fail(sha, CommitSignatureStatus.Unverified, "gpg", "the signing key or the signature has expired", owner.Id, key.Fingerprint, owner.DisplayName);
            if (status.Any(l => l.StartsWith("BADSIG ", StringComparison.Ordinal))) return Fail(sha, CommitSignatureStatus.Unverified, "gpg", "the signature doesn't match the commit", owner.Id, key.Fingerprint, owner.DisplayName);
            var valid = status.FirstOrDefault(l => l.StartsWith("VALIDSIG ", StringComparison.Ordinal))?.Split(' ');
            if (!verify.Ok && valid == null || !status.Any(l => l.StartsWith("GOODSIG ", StringComparison.Ordinal)) || valid == null)
                return Fail(sha, CommitSignatureStatus.Unverified, "gpg", "the signature didn't verify", owner.Id, key.Fingerprint, owner.DisplayName);
            var primary = valid[^1].ToUpperInvariant();
            if (!string.Equals(primary, key.Fingerprint, StringComparison.OrdinalIgnoreCase))
                return Fail(sha, CommitSignatureStatus.Unverified, "gpg", "the signature was made by a different key than the registered one", owner.Id, key.Fingerprint, owner.DisplayName);
            return new Verification(new CommitSignatureResult(sha, CommitSignatureStatus.Verified, "gpg", null, owner.Id, owner.DisplayName, key.Fingerprint), key.ExpiresAt);
        }
        finally { SigningTools.Delete(home); }
    }

    // ---------- Helpers ----------

    private async Task<CommitSignatureResult> ToResultAsync(AppDbContext db, CommitSignature c, Dictionary<int, string> names, CancellationToken ct)
    {
        string? name = null;
        if (c.SignerUserId is { } uid)
        {
            if (!names.TryGetValue(uid, out name))
                names[uid] = name = await db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "a user";
        }
        return new CommitSignatureResult(c.Sha, c.Status, c.Format, c.Reason, c.SignerUserId, name, c.KeyFingerprint);
    }

    /// <summary>Changes whenever a signing key or an account is added or removed: cached results then count as stale.</summary>
    private static async Task<string> KeysStampAsync(AppDbContext db, CancellationToken ct)
    {
        var gpg = await db.GpgKeys.CountAsync(ct);
        var gpgMax = await db.GpgKeys.MaxAsync(k => (int?)k.Id, ct) ?? 0;
        var gpgRevoked = await db.GpgKeys.CountAsync(k => k.RevokedAt != null, ct);
        var ssh = await db.SshKeys.CountAsync(k => k.UserId != null, ct);
        var sshMax = await db.SshKeys.MaxAsync(k => (int?)k.Id, ct) ?? 0;
        var users = await db.Users.CountAsync(ct);
        var confirmed = await db.Users.CountAsync(u => u.EmailConfirmed, ct);
        var locked = await db.Users.CountAsync(u => u.DeactivatedAt != null, ct);
        return string.Create(CultureInfo.InvariantCulture, $"g{gpg}.{gpgMax}.{gpgRevoked}s{ssh}.{sshMax}u{users}.{confirmed}.{locked}");
    }

    /// <summary>Reads many commit objects with one <c>git cat-file --batch</c> run. Missing or non-commit objects are left out.</summary>
    internal async Task<Dictionary<string, byte[]>> ReadCommitsAsync(string repoPath, IReadOnlyList<string> shas, CancellationToken ct)
    {
        var result = new Dictionary<string, byte[]>();
        if (shas.Count == 0 || GitPath is not { } git || !Directory.Exists(repoPath)) return result;
        var psi = new ProcessStartInfo(git)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToList()) psi.Environment.Remove(key);
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.ArgumentList.Add("--git-dir");
        psi.ArgumentList.Add(repoPath);
        psi.ArgumentList.Add("cat-file");
        psi.ArgumentList.Add("--batch");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start git");
        var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
        var feed = Task.Run(async () =>
        {
            try
            {
                foreach (var sha in shas) await p.StandardInput.WriteLineAsync(sha.AsMemory(), timeout.Token);
                p.StandardInput.Close();
            }
            catch (IOException) { }
        }, timeout.Token);
        try
        {
            var stream = p.StandardOutput.BaseStream;
            foreach (var sha in shas)
            {
                var line = await ReadLineAsync(stream, timeout.Token);
                if (line == null) break;
                var parts = line.Split(' ');
                if (parts.Length == 3 && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size <= 1_000_000)
                {
                    var buf = new byte[size];
                    await stream.ReadExactlyAsync(buf, timeout.Token);
                    await stream.ReadExactlyAsync(new byte[1], timeout.Token); // trailing LF
                    if (parts[1] == "commit") result[sha] = buf;
                }
                else if (parts.Length == 3 && long.TryParse(parts[2], out var other)) // a huge or non-commit object: skip its bytes
                {
                    var left = other + 1;
                    var skip = new byte[8192];
                    while (left > 0) { var n = await stream.ReadAsync(skip.AsMemory(0, (int)Math.Min(skip.Length, left)), timeout.Token); if (n == 0) break; left -= n; }
                }
                // "<sha> missing": nothing follows
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or EndOfStreamException)
        {
            log.LogWarning(ex, "Reading commits of {Path} failed", repoPath);
        }
        finally
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await feed; } catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
        }
        return result;
    }

    private static async Task<string?> ReadLineAsync(Stream s, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (await s.ReadAsync(one, ct) == 1)
        {
            if (one[0] == '\n') return sb.ToString();
            sb.Append((char)one[0]);
        }
        return sb.Length == 0 ? null : sb.ToString();
    }
}
