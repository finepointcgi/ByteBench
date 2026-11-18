using System.Security.Cryptography;
using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Webhooks;

/// <summary>
/// Managing a project's webhooks and viewing their deliveries. Everything here requires
/// <see cref="Permission.ManageRepositories"/> (Maintainer or Owner): webhooks are integration settings at the same
/// level as repositories, and the payloads only carry data Maintainers can already read.
/// </summary>
public class WebhookService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, WebhookDispatcher dispatcher, WebhookSender sender)
{
    public const Permission RequiredPermission = Permission.ManageRepositories;
    public const int MaxConsecutiveFailures = 20;
    public const int MaxHooksPerProject = 20;

    public static string NewSecret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>"••••••••3f9a": enough to tell secrets apart without revealing them.</summary>
    public static string Mask(string secret) => "••••••••" + (secret.Length > 4 ? secret[^4..] : "");

    public async Task<List<Webhook>> ListAsync(int projectId)
    {
        await access.RequireAsync(projectId, RequiredPermission);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Webhooks.AsNoTracking().Where(h => h.ProjectId == projectId).OrderBy(h => h.Id).ToListAsync();
    }

    /// <summary>Creates a hook. A blank <paramref name="secret"/> is replaced by a random one. Returns the hook (with its secret, to show once).</summary>
    public async Task<Webhook> CreateAsync(int projectId, string url, string? secret, IEnumerable<string> events, bool active)
    {
        await access.RequireAsync(projectId, RequiredPermission);
        var actor = await access.ResolveUserIdAsync() ?? throw new ForbiddenException("Sign in to manage webhooks.");
        var hook = new Webhook { ProjectId = projectId, CreatedById = actor, IsActive = active };
        Apply(hook, url, secret, events, generateSecret: true);
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.Webhooks.CountAsync(h => h.ProjectId == projectId) >= MaxHooksPerProject)
            throw new InvalidOperationException($"A project can have at most {MaxHooksPerProject} webhooks.");
        db.Webhooks.Add(hook);
        await db.SaveChangesAsync();
        return hook;
    }

    /// <summary>Updates URL, events and active flag; a non-blank <paramref name="newSecret"/> replaces the secret. Re-activating resets the failure count.</summary>
    public async Task<Webhook> UpdateAsync(int projectId, int hookId, string url, string? newSecret, IEnumerable<string> events, bool active)
    {
        await access.RequireAsync(projectId, RequiredPermission);
        await using var db = await dbFactory.CreateDbContextAsync();
        var hook = await db.Webhooks.FirstOrDefaultAsync(h => h.Id == hookId && h.ProjectId == projectId)
                   ?? throw new InvalidOperationException("Webhook not found.");
        Apply(hook, url, newSecret, events, generateSecret: false);
        if (active && !hook.IsActive) { hook.ConsecutiveFailures = 0; hook.DisabledAt = null; }
        hook.IsActive = active;
        await db.SaveChangesAsync();
        return hook;
    }

    /// <summary>Replaces the secret with a new random one and returns it (shown once).</summary>
    public async Task<string> RegenerateSecretAsync(int projectId, int hookId)
    {
        await access.RequireAsync(projectId, RequiredPermission);
        var secret = NewSecret();
        await using var db = await dbFactory.CreateDbContextAsync();
        var n = await db.Webhooks.Where(h => h.Id == hookId && h.ProjectId == projectId)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.Secret, secret));
        if (n == 0) throw new InvalidOperationException("Webhook not found.");
        return secret;
    }

    public async Task DeleteAsync(int projectId, int hookId)
    {
        await access.RequireAsync(projectId, RequiredPermission);
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Webhooks.Where(h => h.Id == hookId && h.ProjectId == projectId).ExecuteDeleteAsync();
    }

    public async Task PingAsync(int projectId, int hookId)
    {
        await RequireHookAsync(projectId, hookId);
        dispatcher.EnqueuePing(hookId, await access.ResolveUserIdAsync());
    }

    public async Task<List<WebhookDelivery>> RecentDeliveriesAsync(int projectId, int hookId, int take = 25)
    {
        await RequireHookAsync(projectId, hookId);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.WebhookDeliveries.AsNoTracking().Where(d => d.WebhookId == hookId)
            .OrderByDescending(d => d.Id).Take(take).ToListAsync();
    }

    public async Task RedeliverAsync(int projectId, int deliveryRowId)
    {
        await access.RequireAsync(projectId, RequiredPermission);
        await using var db = await dbFactory.CreateDbContextAsync();
        var ok = await db.WebhookDeliveries.AnyAsync(d => d.Id == deliveryRowId
            && db.Webhooks.Any(h => h.Id == d.WebhookId && h.ProjectId == projectId));
        if (!ok) throw new InvalidOperationException("Delivery not found.");
        dispatcher.EnqueueRedelivery(deliveryRowId);
    }

    private async Task RequireHookAsync(int projectId, int hookId)
    {
        await access.RequireAsync(projectId, RequiredPermission);
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await db.Webhooks.AnyAsync(h => h.Id == hookId && h.ProjectId == projectId))
            throw new InvalidOperationException("Webhook not found.");
    }

    private void Apply(Webhook hook, string url, string? secret, IEnumerable<string> events, bool generateSecret)
    {
        url = url.Trim();
        if (url.Length > 2000) throw new InvalidOperationException("The URL is too long (max 2000 characters).");
        if (sender.ValidateUrl(url) is { } problem) throw new InvalidOperationException(problem);
        var list = events.Distinct().ToList();
        if (list.Count == 0) throw new InvalidOperationException("Choose at least one event.");
        if (list.FirstOrDefault(e => !WebhookEventNames.IsSubscribable(e)) is { } unknown) throw new InvalidOperationException($"Unknown event '{unknown}'.");
        hook.Url = url;
        hook.Events = string.Join(",", list);
        secret = secret?.Trim();
        if (!string.IsNullOrEmpty(secret))
        {
            if (secret.Length is < 16 or > 200) throw new InvalidOperationException("The secret must be 16 to 200 characters (or leave it blank to generate one).");
            hook.Secret = secret;
        }
        else if (generateSecret) hook.Secret = NewSecret();
    }
}
