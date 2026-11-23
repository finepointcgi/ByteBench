using System.Security.Claims;
using ByteBench.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SignInResult = Microsoft.AspNetCore.Identity.SignInResult;

namespace ByteBench.Services.Accounts;

/// <summary>The service accounts (AI, automation, service desk and CI job tokens): never reset, confirmed, signed in or administered.</summary>
public static class SystemAccounts
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        Ai.AiBot.UserName, Automation.AutomationBot.UserName, ServiceDesk.ServiceDeskBot.UserName, Ci.CiJobTokens.UserName,
    };

    public static bool IsSystem(User? u) => u?.UserName != null && Names.Contains(u.UserName);
    public static bool IsSystem(string? userName) => userName != null && Names.Contains(userName);
}

/// <summary>Token options of the password reset provider: the lifespan is <c>Account:PasswordResetMinutes</c> (default 60).</summary>
public sealed class PasswordResetTokenProviderOptions : DataProtectionTokenProviderOptions
{
    public PasswordResetTokenProviderOptions() { Name = PasswordResetTokenProvider.ProviderName; TokenLifespan = TimeSpan.FromHours(1); }
}

/// <summary>
/// Identity's data-protection token provider with its own purpose and lifespan, so a reset link can be short-lived without
/// shortening confirmation links. Tokens embed the security stamp: using one (or any other password change) kills it.
/// </summary>
public sealed class PasswordResetTokenProvider(IDataProtectionProvider dataProtection, IOptions<PasswordResetTokenProviderOptions> options,
    ILogger<DataProtectorTokenProvider<User>> logger) : DataProtectorTokenProvider<User>(dataProtection, options, logger)
{
    public const string ProviderName = "ByteBenchPasswordReset";
}

/// <summary>
/// Adds <c>Account:RequireEmailConfirmation</c> to password sign-in, but only after the password was right, so the answer
/// never tells a stranger that an account exists. Accounts without a local password (SSO, LDAP) are not affected: their
/// provider vouched for the address.
/// </summary>
public class ByteBenchSignInManager(UserManager<User> userManager, IHttpContextAccessor contextAccessor, IUserClaimsPrincipalFactory<User> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor, ILogger<SignInManager<User>> logger, IAuthenticationSchemeProvider schemes, IUserConfirmation<User> confirmation,
    IOptionsMonitor<AccountOptions> account)
    : SignInManager<User>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    public override async Task<SignInResult> CheckPasswordSignInAsync(User user, string password, bool lockoutOnFailure)
    {
        var result = await base.CheckPasswordSignInAsync(user, password, lockoutOnFailure);
        if (result.Succeeded && NeedsConfirmation(user, account.CurrentValue)) return SignInResult.NotAllowed;
        return result;
    }

    public static bool NeedsConfirmation(User user, AccountOptions o) =>
        o.RequireEmailConfirmation && !user.EmailConfirmed && user.PasswordHash != null && !SystemAccounts.IsSystem(user);
}

/// <summary>Adds the <c>bb:must_change_password</c> claim, which <see cref="MustChangePasswordMiddleware"/> acts on.</summary>
public sealed class ByteBenchClaimsPrincipalFactory(UserManager<User> userManager, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<User>(userManager, options)
{
    public const string MustChangeClaim = "bb:must_change_password";

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(User user)
    {
        var id = await base.GenerateClaimsAsync(user);
        if (user.MustChangePassword) id.AddClaim(new Claim(MustChangeClaim, "1"));
        return id;
    }
}

/// <summary>
/// While the signed-in user has a temporary password, every page and request other than the "choose a new password" page,
/// sign-out and static files redirects there. (Personal access tokens, which are separate credentials, are unaffected.)
/// </summary>
public sealed class MustChangePasswordMiddleware(RequestDelegate next)
{
    public const string Path = "/account/change-password";

    public Task InvokeAsync(HttpContext ctx)
    {
        if (ctx.User.Identity?.IsAuthenticated == true && ctx.User.HasClaim(ByteBenchClaimsPrincipalFactory.MustChangeClaim, "1") && !Allowed(ctx.Request.Path))
        {
            if (ctx.Request.Path.StartsWithSegments("/api")) { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; }
            ctx.Response.Redirect(Path);
            return Task.CompletedTask;
        }
        return next(ctx);
    }

    private static readonly HashSet<string> StaticExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".css", ".js", ".mjs", ".map", ".png", ".jpg", ".svg", ".ico", ".woff", ".woff2", ".ttf" };

    private static bool Allowed(PathString p) =>
        p.StartsWithSegments(Path) || p.StartsWithSegments("/account/logout") || p.StartsWithSegments("/_content") || p.StartsWithSegments("/_framework")
        || p.StartsWithSegments("/health") || p.Value is { } v && StaticExtensions.Contains(System.IO.Path.GetExtension(v));
}

public static class AccountSecurityExtensions
{
    /// <summary>
    /// Account security services: <c>Account</c> and <c>Registration</c> options, the password reset token provider (named
    /// provider with its own lifespan), the sign-in manager, password reset and email confirmation, registration policy.
    /// Chain <see cref="AddAccountTokenProviders"/> onto <c>AddIdentityCore</c> in Program.cs.
    /// </summary>
    public static IServiceCollection AddAccountSecurity(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AccountOptions>(config.GetSection("Account"));
        services.Configure<RegistrationOptions>(config.GetSection("Registration"));
        services.AddOptions<PasswordResetTokenProviderOptions>().Configure<IOptions<AccountOptions>>((o, a) =>
            o.TokenLifespan = TimeSpan.FromMinutes(Math.Clamp(a.Value.PasswordResetMinutes, 5, 60 * 24 * 7)));
        services.AddOptions<DataProtectionTokenProviderOptions>().Configure<IOptions<AccountOptions>>((o, a) =>
            o.TokenLifespan = TimeSpan.FromHours(Math.Clamp(a.Value.EmailConfirmationHours, 1, 24 * 14)));
        services.Configure<IdentityOptions>(o => o.Tokens.PasswordResetTokenProvider = PasswordResetTokenProvider.ProviderName);
        services.AddScoped<IUserClaimsPrincipalFactory<User>, ByteBenchClaimsPrincipalFactory>();

        services.AddSingleton<AccountThrottle>();
        services.AddScoped<SiteSettings>();
        services.AddScoped<RegistrationPolicy>();
        services.AddSingleton<IAccountMailer, QueueAccountMailer>();
        services.AddScoped<AccountEmails>();
        services.AddScoped<PasswordResetService>();
        services.AddScoped<EmailConfirmationService>();
        return services;
    }

    /// <summary>The named token provider for password resets and the sign-in manager that enforces email confirmation.</summary>
    public static IdentityBuilder AddAccountTokenProviders(this IdentityBuilder builder) =>
        builder.AddTokenProvider<PasswordResetTokenProvider>(PasswordResetTokenProvider.ProviderName)
               .AddSignInManager<ByteBenchSignInManager>();

    /// <summary>Redirects users with a temporary password to the change-password page. Place after authentication.</summary>
    public static IApplicationBuilder UseMustChangePassword(this IApplicationBuilder app) => app.UseMiddleware<MustChangePasswordMiddleware>();
}
