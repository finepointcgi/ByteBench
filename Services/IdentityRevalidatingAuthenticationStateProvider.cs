using System.Security.Claims;
using ByteBench.Data;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace ByteBench.Services;

/// <summary>
/// Re-checks the user's security stamp every minute while a Blazor circuit is open, so a password
/// change, deleted account, or an admin's lock or forced sign-out ends the other open sessions.
/// </summary>
public sealed class IdentityRevalidatingAuthenticationStateProvider(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<IdentityOptions> options)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await users.GetUserAsync(state.User);
        if (user is null) return false;
        if (!users.SupportsUserSecurityStamp) return true;
        var stamp = state.User.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType);
        return stamp == await users.GetSecurityStampAsync(user);
    }
}
