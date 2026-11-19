using System.Security.Claims;
using ByteBench.Data;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services;

/// <summary>
/// The signed-in user for the current circuit/request, loaded from the authentication state.
/// </summary>
public class CurrentUser(IDbContextFactory<AppDbContext> dbFactory, AuthenticationStateProvider auth)
{
    private bool loaded;

    public User? User { get; private set; }
    public bool IsSignedIn => User != null;

    public int Id => User?.Id ?? throw new InvalidOperationException("No user is signed in.");

    public async Task EnsureLoadedAsync()
    {
        if (loaded) return;
        var state = await auth.GetAuthenticationStateAsync();
        if (int.TryParse(state.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            User = await db.Users.FindAsync(id);
        }
        loaded = true;
    }
}
