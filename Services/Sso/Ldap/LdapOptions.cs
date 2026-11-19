namespace ByteBench.Services.Sso.Ldap;

public enum LdapSecurity
{
    /// <summary>Plain LDAP (port 389). Passwords cross the network in clear text: only for a trusted local network or tests.</summary>
    None,
    /// <summary>LDAPS: TLS from the start (port 636).</summary>
    Ssl,
    /// <summary>Plain connection upgraded with StartTLS before binding (port 389).</summary>
    StartTls,
}

/// <summary>Directory attribute names.</summary>
public sealed class LdapAttributeMappings
{
    /// <summary>The login name; also the stable key that links the directory entry to the ByteBench account.</summary>
    public string UserName { get; set; } = "sAMAccountName";
    public string Email { get; set; } = "mail";
    public string DisplayName { get; set; } = "displayName";
    /// <summary>Multi-valued attribute listing the user's group DNs (Active Directory, OpenLDAP's memberof overlay). Empty: use <see cref="LdapOptions.GroupFilter"/>.</summary>
    public string MemberOf { get; set; } = "memberOf";
}

/// <summary>LDAP group DN → ByteBench group membership.</summary>
public sealed class LdapGroupMapping
{
    public string GroupDn { get; set; } = "";
    public string Group { get; set; } = "";
    public ByteBench.Data.ProjectRole Role { get; set; } = ByteBench.Data.ProjectRole.Developer;
}

public sealed class LdapSyncOptions
{
    /// <summary>Minutes between syncs of every directory account (0: no periodic sync; "Sync now" on /admin/authentication still works).</summary>
    public int IntervalMinutes { get; set; }
    /// <summary>Deactivate ByteBench accounts whose directory entry is gone (or no longer matches the user filter).</summary>
    public bool DeactivateMissingUsers { get; set; }
}

/// <summary>Configuration section <c>Authentication:Ldap</c>.</summary>
public sealed class LdapOptions
{
    public const string Section = "Authentication:Ldap";

    public bool Enabled { get; set; }
    /// <summary>Shown on the sign-in page hint, e.g. "Acme AD".</summary>
    public string DisplayName { get; set; } = "LDAP";
    public string Server { get; set; } = "";
    /// <summary>0: 636 for <see cref="LdapSecurity.Ssl"/>, else 389.</summary>
    public int Port { get; set; }
    public LdapSecurity Security { get; set; } = LdapSecurity.StartTls;
    /// <summary>Development only: accept any server certificate.</summary>
    public bool IgnoreCertificateErrors { get; set; }
    /// <summary>The service account that searches for users. Empty: anonymous search.</summary>
    public string BindDn { get; set; } = "";
    /// <summary>A secret: user-secrets or <c>Authentication__Ldap__BindPassword</c>, never appsettings.</summary>
    public string BindPassword { get; set; } = "";
    public string UserSearchBase { get; set; } = "";
    /// <summary><c>{0}</c> is the login, escaped (RFC 4515).</summary>
    public string UserFilter { get; set; } = "(&(objectClass=person)(sAMAccountName={0}))";
    public LdapAttributeMappings Attributes { get; set; } = new();
    /// <summary>For directories without memberOf: where to look for groups. <see cref="GroupFilter"/>'s <c>{0}</c> is the user's DN (escaped).</summary>
    public string GroupSearchBase { get; set; } = "";
    public string GroupFilter { get; set; } = "(&(objectClass=groupOfNames)(member={0}))";
    /// <summary>Members of this group DN are site admins (revoked again when they leave it, if LDAP granted it).</summary>
    public string AdminGroupDn { get; set; } = "";
    public List<LdapGroupMapping> GroupMappings { get; set; } = [];
    /// <summary>Create a ByteBench account on the first successful LDAP sign-in.</summary>
    public bool AutoCreateUsers { get; set; } = true;
    /// <summary>
    /// Let directory users use their LDAP password for git over HTTP (Basic auth), when they don't have ByteBench two-factor
    /// sign-in. Off: they must use personal access tokens.
    /// </summary>
    public bool AllowGitPassword { get; set; }
    public int TimeoutSeconds { get; set; } = 10;
    public LdapSyncOptions Sync { get; set; } = new();

    public int EffectivePort => Port > 0 ? Port : Security == LdapSecurity.Ssl ? 636 : 389;
    public bool HasBindPassword => !string.IsNullOrEmpty(BindPassword);
    public string Source => LdapAuthService.LoginProvider;
}
