using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ByteBench.Services.Sso.Ldap;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ByteBench.Tests.Sso;

/// <summary>
/// A real OpenLDAP server (osixia/openldap) in Docker for <see cref="NovellLdapClient"/>: search, bind, wrong password,
/// filter escaping and group membership. The container is removed afterwards.
/// </summary>
public sealed class OpenLdapContainer : IAsyncLifetime
{
    public const string AdminDn = "cn=admin,dc=example,dc=org";
    public const string AdminPassword = "test-admin-pw"; // throw-away container, test-only value
    private readonly string name = "bytebench-ldap-test-" + Guid.NewGuid().ToString("N")[..8];
    public int Port { get; private set; }

    public async Task InitializeAsync()
    {
        if (!DockerFactAttribute.Available.Value) return;
        Port = FreePort();
        await RunAsync("docker", "run", "-d", "--rm", "--name", name, "-p", $"127.0.0.1:{Port}:389",
            "-e", "LDAP_ORGANISATION=Example", "-e", "LDAP_DOMAIN=example.org", "-e", "LDAP_ADMIN_PASSWORD=" + AdminPassword,
            "-e", "LDAP_TLS=false", "osixia/openldap:1.5.0");
        // Wait for slapd, then add an OU, two users and a group.
        for (var i = 0; i < 60; i++)
        {
            var (code, _) = await RunAsync("docker", "exec", name, "ldapsearch", "-x", "-H", "ldap://localhost", "-b", "dc=example,dc=org",
                "-D", AdminDn, "-w", AdminPassword, "-s", "base");
            if (code == 0) break;
            await Task.Delay(1000);
        }
        const string ldif = """
            dn: ou=people,dc=example,dc=org
            objectClass: organizationalUnit
            ou: people

            dn: ou=groups,dc=example,dc=org
            objectClass: organizationalUnit
            ou: groups

            dn: uid=jdoe,ou=people,dc=example,dc=org
            objectClass: inetOrgPerson
            uid: jdoe
            cn: Jane Doe
            sn: Doe
            displayName: Jane Doe
            mail: jdoe@example.org
            userPassword: jdoe-secret

            dn: uid=jdoe2,ou=people,dc=example,dc=org
            objectClass: inetOrgPerson
            uid: jdoe2
            cn: John Doe
            sn: Doe
            mail: jdoe2@example.org
            userPassword: jdoe2-secret

            dn: cn=devs,ou=groups,dc=example,dc=org
            objectClass: groupOfNames
            cn: devs
            member: uid=jdoe,ou=people,dc=example,dc=org

            """;
        var file = Path.Combine(Path.GetTempPath(), name + ".ldif");
        await File.WriteAllTextAsync(file, ldif);
        await RunAsync("docker", "cp", file, name + ":/tmp/seed.ldif");
        File.Delete(file);
        var (addCode, addOut) = await RunAsync("docker", "exec", name, "ldapadd", "-x", "-H", "ldap://localhost", "-D", AdminDn, "-w", AdminPassword, "-f", "/tmp/seed.ldif");
        if (addCode != 0) throw new InvalidOperationException("ldapadd failed: " + addOut);
    }

    public async Task DisposeAsync()
    {
        if (Port != 0) await RunAsync("docker", "rm", "-f", name);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<(int, string)> RunAsync(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, output);
    }
}

[Trait("Category", "Docker")]
public class LdapDockerTests(OpenLdapContainer ldap) : IClassFixture<OpenLdapContainer>
{
    private NovellLdapClient Client(Action<LdapOptions>? configure = null)
    {
        var o = new LdapOptions
        {
            Enabled = true, Server = "127.0.0.1", Port = ldap.Port, Security = LdapSecurity.None,
            BindDn = OpenLdapContainer.AdminDn, BindPassword = OpenLdapContainer.AdminPassword,
            UserSearchBase = "ou=people,dc=example,dc=org", UserFilter = "(&(objectClass=inetOrgPerson)(uid={0}))",
        };
        configure?.Invoke(o);
        return new NovellLdapClient(new StaticOptions(o), NullLogger<NovellLdapClient>.Instance);
    }

    private sealed class StaticOptions(LdapOptions value) : IOptionsMonitor<LdapOptions>
    {
        public LdapOptions CurrentValue => value;
        public LdapOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<LdapOptions, string?> listener) => null;
    }

    private static string Filter(string login) => LdapFilter.Format("(&(objectClass=inetOrgPerson)(uid={0}))", login);

    [DockerFact]
    public async Task Search_bind_and_escaping_against_openldap()
    {
        var client = Client();
        var found = Assert.Single(await client.SearchAsync("ou=people,dc=example,dc=org", Filter("jdoe"), ["uid", "mail", "displayName"]));
        Assert.Equal("uid=jdoe,ou=people,dc=example,dc=org", found.Dn);
        Assert.Equal("jdoe@example.org", found.First("mail"));
        Assert.Equal("Jane Doe", found.First("displayName"));

        Assert.True(await client.CheckPasswordAsync(found.Dn, "jdoe-secret"));
        Assert.False(await client.CheckPasswordAsync(found.Dn, "wrong"));
        Assert.False(await client.CheckPasswordAsync(found.Dn, "")); // never an unauthenticated bind

        // Escaped wildcards match nothing; unescaped, the same filter would match both users.
        Assert.Empty(await client.SearchAsync("ou=people,dc=example,dc=org", Filter("jdoe*"), ["uid"]));
        Assert.Empty(await client.SearchAsync("ou=people,dc=example,dc=org", Filter("*)(uid=*"), ["uid"]));
        Assert.Equal(2, (await client.SearchAsync("ou=people,dc=example,dc=org", "(&(objectClass=inetOrgPerson)(uid=jdoe*))", ["uid"])).Count);

        // Group search by member DN (no memberOf overlay needed).
        var groups = await client.SearchAsync("ou=groups,dc=example,dc=org", LdapFilter.Format("(&(objectClass=groupOfNames)(member={0}))", found.Dn), ["cn"]);
        Assert.Equal("cn=devs,ou=groups,dc=example,dc=org", Assert.Single(groups).Dn);
    }

    [DockerFact]
    public async Task Bad_service_password_and_unreachable_server_are_reported()
    {
        await Assert.ThrowsAsync<LdapUnavailableException>(() =>
            Client(o => o.BindPassword = "wrong").SearchAsync("ou=people,dc=example,dc=org", Filter("jdoe"), ["uid"]));
        await Assert.ThrowsAsync<LdapUnavailableException>(() =>
            Client(o => { o.Port = 1; o.TimeoutSeconds = 2; }).SearchAsync("ou=people,dc=example,dc=org", Filter("jdoe"), ["uid"]));
    }
}
