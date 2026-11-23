using System.Security.Cryptography;
using System.Text;
using ByteBench.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.DependencyBot;

/// <summary>Encrypts registry credentials at rest with ASP.NET Core Data Protection.</summary>
public sealed class RegistryCredentialProtector(IDataProtectionProvider provider)
{
    // Stored identifier: keep "GitBench" (the product was renamed to ByteBench). Changing it would make existing encrypted data/tokens unreadable.
    public const string CredentialPurpose = "GitBench.DependencyBot.RegistryCredential.v1";
    // Stored identifier: keep "GitBench" (the product was renamed to ByteBench). Changing it would make existing encrypted data/tokens unreadable.
    public const string UsernamePurpose = "GitBench.DependencyBot.RegistryUsername.v1";

    private readonly IDataProtector credential = provider.CreateProtector(CredentialPurpose);
    private readonly IDataProtector username = provider.CreateProtector(UsernamePurpose);

    public string ProtectCredential(string value) => credential.Protect(value);
    public string ProtectUsername(string value) => username.Protect(value);
    /// <summary>Throws <see cref="CryptographicException"/> when the keys are gone.</summary>
    public string UnprotectCredential(string stored) => credential.Unprotect(stored);
    public string UnprotectUsername(string stored) => username.Unprotect(stored);
}

/// <summary>An origin is scheme + host + port. A credential is only ever sent to the origin it was configured for.</summary>
public static class RegistryOrigin
{
    public static string Of(Uri uri) => $"{uri.Scheme.ToLowerInvariant()}://{uri.IdnHost.ToLowerInvariant()}:{uri.Port}";

    public static string? Of(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? Of(u) : null;

    public static bool IsLoopback(Uri uri) =>
        uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The Authorization header of one registry plus the only origin that may receive it. The text form never shows the header.</summary>
public sealed class RegistryAuth
{
    public string Origin { get; }
    private readonly string header;

    private RegistryAuth(string origin, string header) { Origin = origin; this.header = header; }

    public static RegistryAuth Bearer(string origin, string token) => new(origin, "Bearer " + token);

    public static RegistryAuth Basic(string origin, string user, string secret) =>
        new(origin, "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + secret)));

    internal string HeaderValue => header;

    /// <summary>The same credential for another origin the maintainer explicitly trusted (NuGet additional origins).</summary>
    internal RegistryAuth Rebind(string origin) => new(origin, header);

    public override string ToString() => "RegistryAuth(" + Origin + ", credential hidden)";
}

/// <summary>A registry address the bot reads from, with its credential (if any) and, for NuGet, the extra origins a service index may name.</summary>
public sealed class RegistryEndpoint(string url, RegistryAuth? auth, IReadOnlyList<string>? extraOrigins = null, string? brokenReason = null)
{
    public string Url { get; } = url;
    public RegistryAuth? Auth { get; } = auth;
    public IReadOnlyList<string> ExtraOrigins { get; } = extraOrigins ?? [];
    /// <summary>The stored credential can't be used (e.g. the encryption keys changed): every lookup in this registry fails with this text.</summary>
    public string? BrokenReason { get; } = brokenReason;

    public override string ToString() => "RegistryEndpoint(" + RegistryOrigin.Of(Url) + ")";
}

/// <summary>
/// The private registries of one repository for one check: npm scoped registries and default, ordered NuGet sources, pip and Go.
/// Anything not configured falls back to the public registry from <c>DependencyBot:Registries</c>. Also caches NuGet service-index results for the run.
/// </summary>
public sealed class RegistrySet
{
    public static readonly RegistrySet Empty = new();

    public Dictionary<string, RegistryEndpoint> NpmScopes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public RegistryEndpoint? Npm { get; set; }
    public List<RegistryEndpoint> NuGet { get; } = [];
    public RegistryEndpoint? Pip { get; set; }
    public RegistryEndpoint? Go { get; set; }

    internal Dictionary<string, string> NuGetBases { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, RegistryException> NuGetFailures { get; } = new(StringComparer.Ordinal);

    public RegistryEndpoint? NpmFor(string name)
    {
        if (name.StartsWith('@') && name.IndexOf('/') is var slash and > 0 && NpmScopes.TryGetValue(name[..slash], out var scoped)) return scoped;
        return Npm;
    }

    /// <summary>Loads the repository's registries; unreadable credentials become broken endpoints (never a silent fall back to the public registry).</summary>
    public static async Task<RegistrySet> LoadAsync(AppDbContext db, int repositoryId, RegistryCredentialProtector protector, CancellationToken ct)
    {
        var rows = await db.DependencyBotRegistries.AsNoTracking().Where(r => r.RepositoryId == repositoryId).OrderBy(r => r.SortOrder).ThenBy(r => r.Id).ToListAsync(ct);
        var set = new RegistrySet();
        foreach (var row in rows)
        {
            var endpoint = Endpoint(row, protector);
            switch (row.Ecosystem)
            {
                case Ecosystems.Npm when row.Scope.Length > 0: set.NpmScopes[row.Scope] = endpoint; break;
                case Ecosystems.Npm: set.Npm = endpoint; break;
                case Ecosystems.NuGet: set.NuGet.Add(endpoint); break;
                case Ecosystems.Pip: set.Pip = endpoint; break;
                case Ecosystems.Go: set.Go = endpoint; break;
            }
        }
        return set;
    }

    /// <summary>The endpoint of a stored registry (decrypting its credential).</summary>
    public static RegistryEndpoint Endpoint(DependencyBotRegistry row, RegistryCredentialProtector protector)
    {
        var extra = (row.ExtraOrigins ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (row.AuthKind == DependencyRegistryAuth.None || string.IsNullOrEmpty(row.EncryptedCredential))
            return new RegistryEndpoint(row.Url, null, extra);
        try
        {
            var origin = RegistryOrigin.Of(row.Url) ?? "";
            var secret = protector.UnprotectCredential(row.EncryptedCredential);
            var auth = row.AuthKind == DependencyRegistryAuth.Bearer
                ? RegistryAuth.Bearer(origin, secret)
                : RegistryAuth.Basic(origin, row.EncryptedUsername == null ? "" : protector.UnprotectUsername(row.EncryptedUsername), secret);
            return new RegistryEndpoint(row.Url, auth, extra);
        }
        catch (CryptographicException)
        {
            return new RegistryEndpoint(row.Url, null, extra, "The stored credential for this registry can't be read (the server's encryption keys changed): enter it again on the Dependencies settings page.");
        }
    }
}
