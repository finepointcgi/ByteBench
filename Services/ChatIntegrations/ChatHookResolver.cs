using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.ChatIntegrations;

/// <summary>
/// Which hooks receive a project's events: its own (generic webhooks and chat integrations) plus the active chat
/// integrations of its group and the group's ancestors, unless the project opted out of that one
/// (<see cref="ChatIntegrationOptOut"/>). Used by the webhook worker and by the "is anyone listening" shortcut.
/// </summary>
public static class ChatHookResolver
{
    public static async Task<List<Webhook>> ActiveHooksAsync(AppDbContext db, int projectId, CancellationToken ct = default)
    {
        var hooks = await db.Webhooks.Where(h => h.ProjectId == projectId && h.IsActive).ToListAsync(ct);
        hooks.AddRange(await InheritedAsync(db, projectId, ct));
        return hooks;
    }

    /// <summary>Active group-level integrations that apply to the project (opted-out ones left out).</summary>
    public static async Task<List<Webhook>> InheritedAsync(AppDbContext db, int projectId, CancellationToken ct = default)
    {
        var groupId = await db.Projects.Where(p => p.Id == projectId).Select(p => p.GroupId).FirstOrDefaultAsync(ct);
        if (groupId == null) return [];
        var chain = await ProjectAccess.GroupChainAsync(db, groupId.Value);
        return await db.Webhooks.Where(h => h.GroupId != null && chain.Contains(h.GroupId.Value) && h.IsActive && h.Kind != WebhookKind.Generic
                && !db.ChatIntegrationOptOuts.Any(o => o.ProjectId == projectId && o.WebhookId == h.Id))
            .ToListAsync(ct);
    }
}
