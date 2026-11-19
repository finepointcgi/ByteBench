using System.Text.RegularExpressions;
using ByteBench.Data;

namespace ByteBench.Services.Sso;

/// <summary>Which protocol and defaults a provider uses. Presets fill in the authority, scopes, claim names and icon.</summary>
public enum SsoProviderType
{
    /// <summary>Any OpenID Connect provider (Keycloak, Okta, Authentik, Auth0, …): set <c>Authority</c>.</summary>
    Oidc,
    Google,
    /// <summary>Microsoft Entra ID (Azure AD): set <c>TenantId</c>.</summary>
    Microsoft,
    /// <summary>GitHub or GitHub Enterprise Server. OAuth 2.0, not OIDC: the user, emails and teams come from GitHub's API.</summary>
    GitHub,
}

/// <summary>What ByteBench does about its own two-factor sign-in for users who sign in through this provider.</summary>
public enum SsoTwoFactorPolicy
{
    /// <summary>Users who turned on ByteBench 2FA must also enter their ByteBench code (the default).</summary>
    ByteBench,
    /// <summary>Skip the ByteBench code when the provider says it did multi-factor authentication (the <c>amr</c> claim).</summary>
    TrustIdp,
}

/// <summary>Maps an external group (a group claim value, an LDAP group DN) to a ByteBench group membership.</summary>
public sealed class ExternalGroupMapping
{
    /// <summary>The value in the provider's group claim (<c>engineering</c>, a GUID for Entra, <c>org/team</c> for GitHub).</summary>
    public string ExternalGroup { get; set; } = "";
    /// <summary>The ByteBench group's full path, e.g. <c>acme/platform</c>.</summary>
    public string Group { get; set; } = "";
    public ProjectRole Role { get; set; } = ProjectRole.Developer;
}

/// <summary>Claim names read from the ID token / userinfo response.</summary>
public sealed class SsoClaimMappings
{
    public string Subject { get; set; } = "sub";
    public string UserName { get; set; } = "preferred_username";
    public string Email { get; set; } = "email";
    public string EmailVerified { get; set; } = "email_verified";
    public string DisplayName { get; set; } = "name";
    public string Groups { get; set; } = "groups";
}

/// <summary>One entry of <c>Authentication:Oidc:Providers</c>.</summary>
public sealed class SsoProviderOptions
{
    /// <summary>Short id used in URLs and as the external login provider name: lowercase letters, digits and '-'.</summary>
    public string Name { get; set; } = "";
    /// <summary>"Sign in with {DisplayName}".</summary>
    public string? DisplayName { get; set; }
    public SsoProviderType Type { get; set; } = SsoProviderType.Oidc;
    public bool Enabled { get; set; } = true;
    /// <summary>The issuer URL (OIDC discovery is read from <c>{Authority}/.well-known/openid-configuration</c>). For GitHub Enterprise, the server URL.</summary>
    public string? Authority { get; set; }
    /// <summary>Microsoft Entra ID: the directory (tenant) id.</summary>
    public string? TenantId { get; set; }
    /// <summary>GitHub: the API base URL (default <c>https://api.github.com</c>, or <c>{Authority}/api/v3</c> for Enterprise).</summary>
    public string? ApiUrl { get; set; }
    public string ClientId { get; set; } = "";
    /// <summary>Keep it out of appsettings: user-secrets or <c>Authentication__Oidc__Providers__0__ClientSecret</c>.</summary>
    public string? ClientSecret { get; set; }
    /// <summary>Null: the preset's scopes.</summary>
    public List<string>? Scopes { get; set; }
    public SsoClaimMappings ClaimMappings { get; set; } = new();
    /// <summary>Create a ByteBench account on the first sign-in (needs a verified email). Otherwise only linked accounts can sign in.</summary>
    public bool AutoCreateUsers { get; set; } = true;
    /// <summary>
    /// Link to an existing account with the same email on the first sign-in, only when the provider says the email is
    /// verified and the ByteBench account's email is confirmed too (or it has no password). Otherwise users link from their profile.
    /// </summary>
    public bool LinkByVerifiedEmail { get; set; } = true;
    /// <summary>Treat the provider's email as verified even without an <c>email_verified</c> claim (e.g. your own Entra tenant).</summary>
    public bool TrustEmailVerified { get; set; }
    /// <summary>If set, only verified emails in these domains may sign in or link (e.g. <c>example.com</c>).</summary>
    public List<string> AllowedEmailDomains { get; set; } = [];
    /// <summary>Group claim values that make the user a site admin (and revoke it again if this provider granted it).</summary>
    public List<string> AdminGroups { get; set; } = [];
    public List<ExternalGroupMapping> GroupMappings { get; set; } = [];
    /// <summary><c>google</c>, <c>github</c>, <c>microsoft</c>, <c>key</c>, or a site-relative image path like <c>/img/keycloak.svg</c>.</summary>
    public string? Icon { get; set; }
    public SsoTwoFactorPolicy TwoFactor { get; set; } = SsoTwoFactorPolicy.ByteBench;
    /// <summary>Only turn off for a local test provider over plain HTTP.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;
    /// <summary>OIDC: also read claims from the userinfo endpoint.</summary>
    public bool UseUserInfoEndpoint { get; set; } = true;

    /// <summary>The ASP.NET Core authentication scheme.</summary>
    public string Scheme => "sso-" + Name;
    /// <summary>The source recorded on synced group memberships and admin grants.</summary>
    public string Source => "oidc:" + Name;
    /// <summary>Where the provider redirects back to (register <c>{site}{CallbackPath}</c> at the provider).</summary>
    public string CallbackPath => "/signin-sso/" + Name;
    public string Title => DisplayName is { Length: > 0 } d ? d : Name;
    public bool HasSecret => !string.IsNullOrEmpty(ClientSecret);
}

/// <summary>
/// The SSO providers from <c>Authentication:Oidc:Providers</c>, with presets applied. Invalid entries are left out (and
/// listed in <see cref="Errors"/>, shown on <c>/admin/authentication</c>) instead of stopping the app.
/// </summary>
public sealed class SsoSettings
{
    public const string Section = "Authentication:Oidc";
    private static readonly Regex NamePattern = new("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant);

    public List<SsoProviderOptions> Providers { get; } = [];
    public List<string> Errors { get; } = [];

    public SsoProviderOptions? Find(string? name) =>
        name == null ? null : Providers.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public SsoProviderOptions? FindByScheme(string? scheme) => Providers.FirstOrDefault(p => p.Scheme == scheme);

    public static SsoSettings Load(IConfiguration config)
    {
        var settings = new SsoSettings();
        var configured = config.GetSection(Section + ":Providers").Get<List<SsoProviderOptions>>() ?? [];
        foreach (var p in configured)
        {
            if (!p.Enabled) continue;
            p.Name = (p.Name ?? "").Trim().ToLowerInvariant();
            if (!NamePattern.IsMatch(p.Name)) { settings.Errors.Add($"Provider '{p.Name}': Name must be lowercase letters, digits and '-'."); continue; }
            if (settings.Find(p.Name) != null) { settings.Errors.Add($"Provider '{p.Name}' is configured twice."); continue; }
            if (string.IsNullOrWhiteSpace(p.ClientId)) { settings.Errors.Add($"Provider '{p.Name}': ClientId is missing."); continue; }
            if (ApplyPreset(p) is { } error) { settings.Errors.Add($"Provider '{p.Name}': {error}"); continue; }
            settings.Providers.Add(p);
        }
        return settings;
    }

    /// <summary>Fills in the preset's defaults. Returns an error for an unusable configuration.</summary>
    private static string? ApplyPreset(SsoProviderOptions p)
    {
        switch (p.Type)
        {
            case SsoProviderType.Google:
                p.Authority ??= "https://accounts.google.com";
                p.DisplayName ??= "Google";
                p.Icon ??= "google";
                p.Scopes ??= ["openid", "email", "profile"];
                p.ClaimMappings.UserName = p.ClaimMappings.UserName == "preferred_username" ? "email" : p.ClaimMappings.UserName;
                break;
            case SsoProviderType.Microsoft:
                if (string.IsNullOrWhiteSpace(p.TenantId) && string.IsNullOrWhiteSpace(p.Authority))
                    return "TenantId is required for Microsoft Entra ID.";
                p.Authority ??= $"https://login.microsoftonline.com/{p.TenantId!.Trim()}/v2.0";
                p.DisplayName ??= "Microsoft";
                p.Icon ??= "microsoft";
                p.Scopes ??= ["openid", "email", "profile"];
                break;
            case SsoProviderType.GitHub:
                p.Authority = (p.Authority ?? "https://github.com").TrimEnd('/');
                p.ApiUrl = (p.ApiUrl ?? (p.Authority == "https://github.com" ? "https://api.github.com" : p.Authority + "/api/v3")).TrimEnd('/');
                p.DisplayName ??= "GitHub";
                p.Icon ??= "github";
                // read:org only when teams or orgs are needed for admin or group mappings.
                p.Scopes ??= p.AdminGroups.Count > 0 || p.GroupMappings.Count > 0 ? ["read:user", "user:email", "read:org"] : ["read:user", "user:email"];
                if (!p.HasSecret) return "ClientSecret is required for GitHub.";
                break;
            default:
                if (string.IsNullOrWhiteSpace(p.Authority)) return "Authority is required for an OIDC provider.";
                p.Icon ??= "key";
                p.Scopes ??= ["openid", "email", "profile"];
                break;
        }
        if (!Uri.TryCreate(p.Authority, UriKind.Absolute, out var authority) || authority.Scheme is not ("https" or "http"))
            return "Authority must be an absolute http(s) URL.";
        if (authority.Scheme == "http" && p.RequireHttpsMetadata) return "Authority must use https (or set RequireHttpsMetadata=false for a local test provider).";
        if (p.Type != SsoProviderType.GitHub && !p.Scopes.Contains("openid")) p.Scopes.Insert(0, "openid");
        p.AllowedEmailDomains = p.AllowedEmailDomains.Select(d => d.Trim().TrimStart('@').ToLowerInvariant()).Where(d => d.Length > 0).ToList();
        return null;
    }

    /// <summary>
    /// The origins of the configured identity providers, for the CSP's <c>form-action</c>: Chrome applies form-action to
    /// the redirects after a form post, so a form that leads to a provider needs its origin listed. (The sign-in buttons
    /// are plain links, so they don't depend on this.)
    /// </summary>
    public IEnumerable<string> FormActionOrigins() => Providers
        .Select(p => Uri.TryCreate(p.Authority, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : null)
        .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);
}
