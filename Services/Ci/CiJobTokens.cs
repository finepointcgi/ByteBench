using ByteBench.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Ci;

/// <summary>A running CI job a job token was issued for.</summary>
public sealed record CiJobIdentity(int JobId, int PipelineId, int ProjectId, int? TriggeredById, string JobName);

/// <summary>
/// CI job tokens (<c>gbj_…</c>): handed to every job as <c>BYTEBENCH_JOB_TOKEN</c> (and <c>BYTEBENCH_REGISTRY_PASSWORD</c>)
/// so its scripts can publish to and read from the project's package and container registry without a personal token.
/// A token is the job id protected with Data Protection (time-limited to the job timeout plus a margin); it is only
/// accepted while that job is still running, so it dies with the job. What it may do is decided by
/// <see cref="Packages.RegistryAuth"/>: Developer rights in the job's own project (through <see cref="ProjectAccess.RoleHas"/>).
/// </summary>
public sealed class CiJobTokens(IDataProtectionProvider dataProtection, IDbContextFactory<AppDbContext> dbFactory, IOptionsMonitor<CiOptions> options)
{
    public const string Prefix = "gbj_";
    /// <summary>The user name to pair with a job token in Basic credentials (any name is accepted).</summary>
    public const string UserName = "bytebench-ci-token";

    // Stored identifier: keep "GitBench" (the product was renamed to ByteBench). Changing it would make existing encrypted data/tokens unreadable.
    private ITimeLimitedDataProtector Protector => dataProtection.CreateProtector("GitBench.Ci.JobToken.v1").ToTimeLimitedDataProtector();

    public string Create(int jobId) =>
        Prefix + Protector.Protect(jobId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            TimeSpan.FromMinutes(Math.Max(1, options.CurrentValue.JobTimeoutMinutes) + 15));

    /// <summary>The running job <paramref name="token"/> was issued for, or null (forged, expired, or the job has ended).</summary>
    public async Task<CiJobIdentity?> ValidateAsync(string? token, CancellationToken ct = default)
    {
        if (token == null || !token.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        int jobId;
        try
        {
            if (!int.TryParse(Protector.Unprotect(token[Prefix.Length..]), out jobId)) return null;
        }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.PipelineJobs.AsNoTracking()
            .Where(j => j.Id == jobId && j.Status == PipelineStatus.Running)
            .Select(j => new CiJobIdentity(j.Id, j.PipelineId, j.Pipeline.Repository.ProjectId, j.Pipeline.TriggeredById, j.Name))
            .FirstOrDefaultAsync(ct);
    }
}
