using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using GitBench.Data;
using GitBench.Services.Sso.Ldap;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace GitBench.Services.Sso;

/// <summary>
/// Wires single sign-on (OIDC providers and GitHub OAuth from <c>Authentication:Oidc:Providers</c>) and LDAP
/// (<c>Authentication:Ldap</c>) into the app.
/// <para>
/// Flow: the login page's "Sign in with X" buttons are plain links to <c>GET /account/sso/{name}</c>, which challenges the
/// provider's scheme (authorization code + PKCE, with state and nonce in correlation cookies). The provider redirects back
/// to <c>/signin-sso/{name}</c>, where the handler validates everything and signs the result into Identity's external
/// cookie, then <c>/account/sso/complete</c> lets <see cref="SsoService"/> find or create the account and sign in.
/// Links (not form posts) mean the CSP's <c>form-action</c> never has to allow the provider.
/// </para>
/// </summary>
public static class SsoAuthentication
{
    public static WebApplicationBuilder AddGitBenchSso(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var settings = SsoSettings.Load(builder.Configuration);
        services.AddSingleton(settings);
        services.AddScoped<SsoService>();
        services.AddScoped<ExternalGroupSync>();

        var auth = services.AddAuthentication();
        foreach (var p in settings.Providers)
        {
            if (p.Type == SsoProviderType.GitHub) auth.AddOAuth(p.Scheme, p.Title, o => ConfigureGitHub(o, p));
            else auth.AddOpenIdConnect(p.Scheme, p.Title, o => ConfigureOidc(o, p));
        }

        // ----- LDAP / Active Directory -----
        services.Configure<LdapOptions>(builder.Configuration.GetSection(LdapOptions.Section));
        services.AddSingleton<ILdapClient, NovellLdapClient>();
        services.AddScoped<LdapAuthService>();
        services.AddSingleton<LdapSyncJob>();
        services.AddHostedService(sp => sp.GetRequiredService<LdapSyncJob>());
        return builder;
    }

    private static void ConfigureOidc(OpenIdConnectOptions o, SsoProviderOptions p)
    {
        o.Authority = p.Authority;
        o.ClientId = p.ClientId;
        o.ClientSecret = p.ClientSecret;
        o.ResponseType = OpenIdConnectResponseType.Code;
        // query, not form_post: the callback is a top-level GET, so the SameSite=Lax correlation cookies come along (also over plain HTTP in development).
        o.ResponseMode = OpenIdConnectResponseMode.Query;
        o.UsePkce = true;
        o.CallbackPath = p.CallbackPath;
        o.SignInScheme = IdentityConstants.ExternalScheme;
        o.RequireHttpsMetadata = p.RequireHttpsMetadata;
        o.MapInboundClaims = false; // keep the JWT claim names (sub, email, groups, amr) the claim mappings refer to
        o.GetClaimsFromUserInfoEndpoint = p.UseUserInfoEndpoint;
        o.SaveTokens = false;
        o.DisableTelemetry = true;
        o.TokenValidationParameters.NameClaimType = p.ClaimMappings.DisplayName;
        o.Scope.Clear();
        foreach (var s in p.Scopes!) o.Scope.Add(s);
        var m = p.ClaimMappings;
        foreach (var key in new[] { m.UserName, m.Email, m.EmailVerified, m.DisplayName }) o.ClaimActions.MapUniqueJsonKey(key, key);
        o.ClaimActions.MapJsonKey(m.Groups, m.Groups);
        o.CorrelationCookie.SameSite = SameSiteMode.Lax;
        o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.NonceCookie.SameSite = SameSiteMode.Lax;
        o.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.Events.OnTicketReceived = ctx => OnTicketReceived(ctx, p);
        o.Events.OnRemoteFailure = ctx => OnRemoteFailure(ctx, p);
    }

    private static void ConfigureGitHub(OAuthOptions o, SsoProviderOptions p)
    {
        o.ClientId = p.ClientId;
        o.ClientSecret = p.ClientSecret!;
        o.AuthorizationEndpoint = p.Authority + "/login/oauth/authorize";
        o.TokenEndpoint = p.Authority + "/login/oauth/access_token";
        o.UserInformationEndpoint = p.ApiUrl + "/user";
        o.UsePkce = true;
        o.CallbackPath = p.CallbackPath;
        o.SignInScheme = IdentityConstants.ExternalScheme;
        o.SaveTokens = false;
        o.Scope.Clear();
        foreach (var s in p.Scopes!) o.Scope.Add(s);
        o.CorrelationCookie.SameSite = SameSiteMode.Lax;
        o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.Events.OnCreatingTicket = ctx => GitHubClaimsAsync(ctx, p);
        o.Events.OnTicketReceived = ctx => OnTicketReceived(ctx, p);
        o.Events.OnRemoteFailure = ctx => OnRemoteFailure(ctx, p);
    }

    /// <summary>
    /// GitHub isn't OIDC: read the user, the verified primary email and (when mappings need them) orgs and teams from its
    /// API, as the standard claim names (<c>sub</c>, <c>preferred_username</c>, <c>email</c>, <c>email_verified</c>,
    /// <c>groups</c>: <c>org</c> and <c>org/team-slug</c>).
    /// </summary>
    private static async Task GitHubClaimsAsync(OAuthCreatingTicketContext ctx, SsoProviderOptions p)
    {
        async Task<JsonElement?> GetAsync(string url)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.AccessToken);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("GitBench", "1.0"));
            using var res = await ctx.Backchannel.SendAsync(req, ctx.HttpContext.RequestAborted);
            if (!res.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ctx.HttpContext.RequestAborted)).RootElement.Clone();
        }
        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;

        var identity = ctx.Identity!;
        var user = await GetAsync(p.ApiUrl + "/user") ?? throw new InvalidOperationException("GitHub didn't return the user.");
        void Add(string type, string? value) { if (!string.IsNullOrEmpty(value)) identity.AddClaim(new Claim(type, value, ClaimValueTypes.String, p.Scheme)); }
        Add("sub", Str(user, "id"));
        Add("preferred_username", Str(user, "login"));
        Add("name", Str(user, "name") ?? Str(user, "login"));

        // The profile's email may be unverified (or hidden); /user/emails says which addresses GitHub verified.
        if (await GetAsync(p.ApiUrl + "/user/emails") is { ValueKind: JsonValueKind.Array } emails)
        {
            var list = emails.EnumerateArray().Select(e => new
            {
                Email = Str(e, "email"),
                Primary = e.TryGetProperty("primary", out var pr) && pr.ValueKind == JsonValueKind.True,
                Verified = e.TryGetProperty("verified", out var vr) && vr.ValueKind == JsonValueKind.True,
            }).Where(e => e.Email != null).ToList();
            var best = list.FirstOrDefault(e => e.Primary && e.Verified) ?? list.FirstOrDefault(e => e.Verified) ?? list.FirstOrDefault(e => e.Primary);
            if (best != null) { Add("email", best.Email); Add("email_verified", best.Verified ? "true" : "false"); }
        }
        else if (Str(user, "email") is { } publicEmail) { Add("email", publicEmail); Add("email_verified", "false"); }

        if (p.AdminGroups.Count > 0 || p.GroupMappings.Count > 0)
        {
            if (await GetAsync(p.ApiUrl + "/user/orgs?per_page=100") is { ValueKind: JsonValueKind.Array } orgs)
                foreach (var org in orgs.EnumerateArray()) Add("groups", Str(org, "login"));
            if (await GetAsync(p.ApiUrl + "/user/teams?per_page=100") is { ValueKind: JsonValueKind.Array } teams)
                foreach (var team in teams.EnumerateArray())
                    if (team.TryGetProperty("organization", out var org)) Add("groups", $"{Str(org, "login")}/{Str(team, "slug")}");
        }
    }

    /// <summary>Identity's external login needs the provider key as NameIdentifier; take it from the mapped subject claim.</summary>
    private static Task OnTicketReceived(TicketReceivedContext ctx, SsoProviderOptions p)
    {
        var identity = ctx.Principal?.Identity as ClaimsIdentity;
        var subject = ctx.Principal?.FindFirst(p.ClaimMappings.Subject)?.Value ?? ctx.Principal?.FindFirst("sub")?.Value;
        if (identity == null || string.IsNullOrEmpty(subject))
        {
            ctx.Fail("The provider didn't return a subject claim.");
            return Task.CompletedTask;
        }
        foreach (var c in identity.FindAll(ClaimTypes.NameIdentifier).ToList()) identity.RemoveClaim(c);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, subject, ClaimValueTypes.String, p.Scheme));
        return Task.CompletedTask;
    }

    /// <summary>Invalid state, a cancelled consent, a bad token…: back to the sign-in page (or profile) with a message, and audited.</summary>
    private static async Task OnRemoteFailure(RemoteFailureContext ctx, SsoProviderOptions p)
    {
        var cancelled = ctx.Failure?.Message.Contains("access_denied", StringComparison.OrdinalIgnoreCase) == true;
        var reason = cancelled ? SsoErrors.Cancelled : SsoErrors.Failed;
        ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GitBench.Sso")
            .LogWarning(ctx.Failure, "SSO callback from {Provider} failed", p.Name);
        await ctx.HttpContext.RequestServices.GetRequiredService<Audit.AuditLog>().RecordAsync("auth.sso_failed",
            details: new { provider = p.Name, reason, error = ctx.Failure?.Message }, anonymous: true);
        var linking = ctx.Properties?.Items.ContainsKey("XsrfId") == true;
        ctx.Response.Redirect(linking
            ? $"/account/profile?sso_error={reason}&provider={Uri.EscapeDataString(p.Name)}#connected-accounts"
            : SsoService.LoginError(reason, p.Name));
        ctx.HandleResponse();
    }

    // ------------------------------------------------------------------ endpoints

    public static IEndpointRouteBuilder MapGitBenchSso(this IEndpointRouteBuilder app)
    {
        // Start sign-in: a GET link from the login page.
        app.MapGet("/account/sso/{provider}", (string provider, string? returnUrl, SsoSettings settings, SignInManager<User> signIn) =>
        {
            if (settings.Find(provider) is not { } p) return Results.Redirect(SsoService.LoginError(SsoErrors.UnknownProvider, null));
            var redirect = "/account/sso/complete?returnUrl=" + Uri.EscapeDataString(SsoService.SafeReturnUrl(returnUrl));
            var props = signIn.ConfigureExternalAuthenticationProperties(p.Name, redirect);
            return Results.Challenge(props, [p.Scheme]);
        }).AllowAnonymous();

        app.MapGet("/account/sso/complete", async (HttpContext http, string? returnUrl, SsoService sso) =>
            Results.Redirect(await sso.CompleteSignInAsync(http, returnUrl))).AllowAnonymous();

        // Connect a provider to the signed-in account (Profile → Connected accounts). The token ties the link to that page.
        app.MapGet("/account/sso/{provider}/link", (string provider, string? t, HttpContext http, SsoSettings settings, SsoService sso,
            SignInManager<User> signIn, UserManager<User> users) =>
        {
            if (settings.Find(provider) is not { } p) return Results.Redirect("/account/profile#connected-accounts");
            if (users.GetUserId(http.User) is not { } uid || !int.TryParse(uid, out var userId) || !sso.ValidateLinkToken(t, userId))
                return Results.Redirect($"/account/profile?sso_error={SsoErrors.Expired}&provider={Uri.EscapeDataString(p.Name)}#connected-accounts");
            var props = signIn.ConfigureExternalAuthenticationProperties(p.Name, "/account/sso/complete-link", userId.ToString());
            return Results.Challenge(props, [p.Scheme]);
        }).RequireAuthorization();

        app.MapGet("/account/sso/complete-link", async (HttpContext http, SsoService sso, UserManager<User> users) =>
            users.GetUserId(http.User) is { } uid && int.TryParse(uid, out var userId)
                ? Results.Redirect(await sso.CompleteLinkAsync(http, userId))
                : Results.Redirect("/account/login")).RequireAuthorization();
        return app;
    }
}
