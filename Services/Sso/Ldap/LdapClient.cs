using System.Net.Security;
using Microsoft.Extensions.Options;
using Novell.Directory.Ldap;

namespace ByteBench.Services.Sso.Ldap;

/// <summary>A directory entry: its DN and the requested attributes (names case-insensitive, every value as a string).</summary>
public sealed class LdapDirectoryEntry(string dn, IDictionary<string, string[]> attributes)
{
    public string Dn { get; } = dn;
    public IReadOnlyDictionary<string, string[]> Attributes { get; } = new Dictionary<string, string[]>(attributes, StringComparer.OrdinalIgnoreCase);

    public string? First(string attribute) =>
        attribute.Length > 0 && Attributes.TryGetValue(attribute, out var v) && v.Length > 0 && v[0].Length > 0 ? v[0] : null;

    public string[] All(string attribute) => attribute.Length > 0 && Attributes.TryGetValue(attribute, out var v) ? v : [];
}

/// <summary>Thrown when the directory can't be reached or the service account can't bind (as opposed to a wrong user password).</summary>
public class LdapUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The two directory operations ByteBench needs, so the logic in <see cref="LdapAuthService"/> can be tested with a fake.
/// Filters arrive already built and escaped (<see cref="LdapFilter"/>).
/// </summary>
public interface ILdapClient
{
    /// <summary>Subtree search as the service account (<see cref="LdapOptions.BindDn"/>). Throws <see cref="LdapUnavailableException"/>.</summary>
    Task<IReadOnlyList<LdapDirectoryEntry>> SearchAsync(string baseDn, string filter, string[] attributes, CancellationToken ct = default);

    /// <summary>
    /// Binds as <paramref name="dn"/> with <paramref name="password"/>: true if the directory accepts it, false for bad
    /// credentials. Callers must refuse empty passwords first (an empty password is an "unauthenticated bind" that many
    /// servers accept).
    /// </summary>
    Task<bool> CheckPasswordAsync(string dn, string password, CancellationToken ct = default);
}

/// <summary>
/// <see cref="ILdapClient"/> on Novell.Directory.Ldap.NETStandard. Chosen over System.DirectoryServices.Protocols because
/// it's fully managed: no native libldap to install in the container image or on macOS, and the same TLS stack
/// (SslStream) everywhere. One short-lived connection per operation (ByteBench binds rarely: sign-ins and the sync).
/// </summary>
public sealed class NovellLdapClient(IOptionsMonitor<LdapOptions> options, ILogger<NovellLdapClient> log) : ILdapClient
{
    public async Task<IReadOnlyList<LdapDirectoryEntry>> SearchAsync(string baseDn, string filter, string[] attributes, CancellationToken ct = default)
    {
        var o = options.CurrentValue;
        using var conn = await ConnectAsync(o, ct);
        try
        {
            if (o.BindDn.Length > 0) await conn.BindAsync(o.BindDn, o.BindPassword, ct);
        }
        catch (LdapException ex)
        {
            throw new LdapUnavailableException($"The service account could not bind ({ex.ResultCode}: {ex.LdapErrorMessage ?? ex.Message}).", ex);
        }
        try
        {
            var constraints = new LdapSearchConstraints { TimeLimit = o.TimeoutSeconds * 1000, ServerTimeLimit = o.TimeoutSeconds, MaxResults = 10000 };
            var results = await conn.SearchAsync(baseDn, LdapConnection.ScopeSub, filter, attributes, false, constraints, ct);
            var list = new List<LdapDirectoryEntry>();
            while (await results.HasMoreAsync(ct))
            {
                LdapEntry entry;
                try { entry = await results.NextAsync(ct); }
                catch (LdapReferralException) { continue; } // referrals to other servers aren't followed
                var attrs = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in attributes)
                {
                    var a = entry.GetOrDefault(name, null!);
                    if (a != null) attrs[name] = a.StringValueArray;
                }
                list.Add(new LdapDirectoryEntry(entry.Dn, attrs));
            }
            return list;
        }
        catch (LdapException ex) when (ex.ResultCode == LdapException.NoSuchObject)
        {
            return [];
        }
        catch (LdapException ex)
        {
            throw new LdapUnavailableException($"The search failed ({ex.ResultCode}: {ex.LdapErrorMessage ?? ex.Message}).", ex);
        }
    }

    public async Task<bool> CheckPasswordAsync(string dn, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(password)) return false;
        var o = options.CurrentValue;
        using var conn = await ConnectAsync(o, ct);
        try
        {
            await conn.BindAsync(dn, password, ct);
            return conn.Bound;
        }
        catch (LdapException ex) when (ex.ResultCode is LdapException.InvalidCredentials or LdapException.InappropriateAuthentication
                                           or LdapException.UnwillingToPerform or LdapException.ConstraintViolation)
        {
            log.LogInformation("LDAP bind refused for {Dn}: {Code}", dn, ex.ResultCode);
            return false;
        }
        catch (LdapException ex)
        {
            throw new LdapUnavailableException($"The bind failed ({ex.ResultCode}: {ex.LdapErrorMessage ?? ex.Message}).", ex);
        }
    }

    private async Task<LdapConnection> ConnectAsync(LdapOptions o, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(o.Server)) throw new LdapUnavailableException("Authentication:Ldap:Server is not set.");
        var connOptions = new LdapConnectionOptions();
        if (o.Security == LdapSecurity.Ssl) connOptions.UseSsl();
        if (o.IgnoreCertificateErrors) connOptions.ConfigureRemoteCertificateValidationCallback((_, _, _, _) => true);
        else connOptions.ConfigureRemoteCertificateValidationCallback((_, _, _, errors) => errors == SslPolicyErrors.None);
        var conn = new LdapConnection(connOptions) { ConnectionTimeout = o.TimeoutSeconds * 1000 };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, o.TimeoutSeconds)));
            await conn.ConnectAsync(o.Server, o.EffectivePort, timeout.Token);
            if (o.Security == LdapSecurity.StartTls) await conn.StartTlsAsync(timeout.Token);
            return conn;
        }
        catch (Exception ex) when (ex is LdapException or System.Net.Sockets.SocketException or IOException or OperationCanceledException
                                       or System.Security.Authentication.AuthenticationException)
        {
            conn.Dispose();
            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
            throw new LdapUnavailableException($"Could not connect to {o.Server}:{o.EffectivePort} ({ex.Message}).", ex);
        }
    }
}
