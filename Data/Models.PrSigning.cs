using System.ComponentModel.DataAnnotations;

namespace ByteBench.Data;

// ----- Auto-merge ("Merge when ready") -----

/// <summary>Where a pull request's auto-merge request stands.</summary>
public enum AutoMergeState
{
    /// <summary>Waiting for every requirement to be satisfied.</summary>
    Armed = 0,
    /// <summary>One worker claimed it and is merging right now (claims older than the lease can be re-taken).</summary>
    Merging = 1,
    /// <summary>The pull request was merged (or queued in the merge queue) by the auto-merge.</summary>
    Merged = 2,
    /// <summary>Cancelled by the person who armed it, or by ByteBench (see <see cref="PullRequestAutoMerge.CancelReason"/>).</summary>
    Cancelled = 3,
}

/// <summary>
/// "Merge when ready": a request to merge the pull request as soon as every requirement is met. At most one row per pull
/// request (arming again after a cancel reuses it). The person who armed it is recorded as the merger.
/// </summary>
public class PullRequestAutoMerge
{
    public int Id { get; set; }
    public int PullRequestId { get; set; }
    public PullRequest PullRequest { get; set; } = null!;
    public int ArmedById { get; set; }
    public User ArmedBy { get; set; } = null!;
    public MergeStrategy Strategy { get; set; }
    public bool DeleteSourceBranch { get; set; }
    /// <summary>The source branch tip when it was armed; a push by someone else after that cancels the request.</summary>
    [MaxLength(64)] public string? ArmedHeadSha { get; set; }
    /// <summary>The target branch when it was armed; retargeting the pull request cancels the request.</summary>
    [MaxLength(250)] public string? ArmedTargetBranch { get; set; }
    public AutoMergeState State { get; set; }
    public DateTime ArmedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClaimedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    [MaxLength(300)] public string? CancelReason { get; set; }
    /// <summary>Last time the worker evaluated it, and why it was still waiting.</summary>
    public DateTime? LastCheckedAt { get; set; }
    [MaxLength(500)] public string? WaitingFor { get; set; }
}

// ----- Signed commits -----

/// <summary>
/// An OpenPGP public key a user registered to sign commits with. The key is only ever imported into a throw-away
/// keyring when a signature is verified; it is never added to a real keyring.
/// </summary>
public class GpgKey
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    [Required, MaxLength(100)] public string Title { get; set; } = "";
    /// <summary>Upper-case hex fingerprint of the primary key (unique across users).</summary>
    [Required, MaxLength(64)] public string Fingerprint { get; set; } = "";
    /// <summary>Space-separated upper-case fingerprints of the primary key and every subkey (what a signature's issuer can be).</summary>
    [Required, MaxLength(1000)] public string KeyFingerprints { get; set; } = "";
    /// <summary>Space-separated e-mail addresses of the key's user IDs (lower-case).</summary>
    [MaxLength(1000)] public string Emails { get; set; } = "";
    /// <summary>ASCII-armored public key block.</summary>
    [Required] public string PublicKey { get; set; } = "";
    [MaxLength(40)] public string Algorithm { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    /// <summary>Set when the owner revokes the key here, or the uploaded key block carries a revocation.</summary>
    public DateTime? RevokedAt { get; set; }

    public bool IsExpired(DateTime nowUtc) => ExpiresAt is { } e && e <= nowUtc;
    public bool IsUsable(DateTime nowUtc) => RevokedAt == null && !IsExpired(nowUtc);
}

public enum CommitSignatureStatus
{
    /// <summary>No signature.</summary>
    Unsigned = 0,
    /// <summary>Good signature by a registered, usable key of a user whose e-mail matches the committer's.</summary>
    Verified = 1,
    /// <summary>Signed, but not trusted (see the reason): bad signature, unknown / revoked / expired key, e-mail mismatch.</summary>
    Unverified = 2,
    /// <summary>Signed, but this server can't check it (gpg / ssh-keygen missing, or an unsupported format).</summary>
    Unverifiable = 3,
}

/// <summary>The cached verification result of one commit (a commit's signature never changes; the keys it depends on can).</summary>
public class CommitSignature
{
    public int Id { get; set; }
    [Required, MaxLength(64)] public string Sha { get; set; } = "";
    public CommitSignatureStatus Status { get; set; }
    /// <summary>"gpg", "ssh" or null for unsigned commits.</summary>
    [MaxLength(10)] public string? Format { get; set; }
    [MaxLength(300)] public string? Reason { get; set; }
    public int? SignerUserId { get; set; }
    [MaxLength(80)] public string? KeyFingerprint { get; set; }
    public DateTime VerifiedAt { get; set; } = DateTime.UtcNow;
    /// <summary>A Verified result is only good until the signing key expires.</summary>
    public DateTime? ValidUntil { get; set; }
    /// <summary>Fingerprint of the set of registered keys at the time: the result is recomputed when keys change.</summary>
    [MaxLength(80)] public string KeysStamp { get; set; } = "";
}

public partial class BranchProtectionRule
{
    /// <summary>Every new commit pushed to a matching branch, and every commit of a pull request merged into it, must be Verified.</summary>
    public bool RequireSignedCommits { get; set; }
}
