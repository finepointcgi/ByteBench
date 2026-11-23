using System.ComponentModel.DataAnnotations;

namespace ByteBench.Data;

/// <summary>How the dependency bot authenticates to a private registry.</summary>
public enum DependencyRegistryAuth { None, Bearer, Basic }

/// <summary>
/// A private registry of one repository's dependency bot. <see cref="Scope"/> is "" for the ecosystem's default registry,
/// <c>@scope</c> for an npm scoped registry and a generated <c>source-N</c> key for a NuGet source (ordered by <see cref="SortOrder"/>).
/// The credential and the user name are encrypted with Data Protection (see <c>RegistryCredentialProtector</c>) and are never
/// returned to a page, written to an audit event, a log, a pull request or a warning.
/// </summary>
public class DependencyBotRegistry
{
    public int Id { get; set; }
    public int RepositoryId { get; set; }
    [Required, MaxLength(20)] public string Ecosystem { get; set; } = "";
    [MaxLength(100)] public string Scope { get; set; } = "";
    [Required, MaxLength(2000)] public string Url { get; set; } = "";
    public DependencyRegistryAuth AuthKind { get; set; }
    /// <summary>Bearer token, or the password/token of basic authentication (ciphertext).</summary>
    [MaxLength(4000)] public string? EncryptedCredential { get; set; }
    /// <summary>The user name of basic authentication (ciphertext).</summary>
    [MaxLength(4000)] public string? EncryptedUsername { get; set; }
    /// <summary>NuGet: further origins (comma separated, scheme://host:port) whose resource URLs from the service index may receive the credential.</summary>
    [MaxLength(1000)] public string? ExtraOrigins { get; set; }
    public int SortOrder { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
