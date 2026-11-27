using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services.Accounts;
using ByteBench.Services.Audit;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Workspaces;

/// <summary>A workspace's network policy as it started, with the proxy's counters and blocked-request log.</summary>
public sealed record WorkspaceEgressView(string Mode, string AdminMode, bool RepoNarrowed, List<string> Rules, List<string> Warnings,
    IReadOnlyList<EgressBlock> Blocked, long Allowed, long BlockedTotal);

/// <summary>Reads a workspace's stored egress record (owner-facing: the caller has already checked ownership).</summary>
public sealed class WorkspaceEgressReader(IDbContextFactory<AppDbContext> dbFactory, WorkspaceEgress egress)
{
    public async Task<WorkspaceEgressView> GetAsync(Workspace ws)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var r = await db.WorkspaceEgressRecords.AsNoTracking().FirstOrDefaultAsync(x => x.WorkspaceId == ws.Id);
        var counts = egress.Counts(ws.Id);
        var blocked = egress.Blocked(ws.PublicId);
        if (r == null) return new WorkspaceEgressView(ws.EgressMode, ws.EgressMode, false, [], [], blocked, counts.Allowed, counts.Blocked);
        var warnings = JsonSerializer.Deserialize<List<string>>(r.WarningsJson) ?? [];
        var rules = WorkspaceEgressPolicy.RulesFromJson(r.RulesJson).Select(x => x.ToString()).ToList();
        return new WorkspaceEgressView(r.Mode, r.AdminMode, r.RepoNarrowed, rules, warnings, blocked, counts.Allowed, counts.Blocked);
    }
}

/// <summary>The admin's egress policy (Admin → Workspaces): a default mode and the allow-list. Changes are audited and apply to workspaces started afterwards.</summary>
public sealed class WorkspaceEgressAdmin(WorkspaceEgress egress, ProjectAccess access, SiteSettings siteSettings, AuditLog audit)
{
    public Task<EffectiveEgress> GetAsync() => egress.GetAdminPolicyAsync();

    /// <summary>The raw allow-list text (entries and <c>@presets</c>) as stored, or the default list.</summary>
    public async Task<string> GetEntriesTextAsync() =>
        await siteSettings.GetAsync(WorkspaceEgress.AllowKey) ?? string.Join("\n", WorkspaceEgressPolicy.DefaultAllow);

    /// <summary>Saves the policy. Returns the validation errors (nothing is saved when there are any).</summary>
    public async Task<List<string>> SetAsync(string mode, string entriesText, int adminId)
    {
        if (!await access.IsAdminAsync(adminId)) throw new ForbiddenException("Only site administrators can do this.");
        var errors = new List<string>();
        if (!WorkspaceEgressPolicy.IsMode(mode)) errors.Add("Unknown mode.");
        var rules = WorkspaceEgressPolicy.ParseEntries(WorkspaceEgressPolicy.SplitEntries(entriesText), errors);
        if (errors.Count > 0) return errors;
        await siteSettings.SetAsync(WorkspaceEgress.ModeKey, mode, adminId);
        await siteSettings.SetAsync(WorkspaceEgress.AllowKey, entriesText.Trim(), adminId);
        await audit.RecordAsync(WorkspaceAuditActions.AdminEgress, "workspaces", null, details: new { mode, rules = rules.Select(r => r.ToString()).ToList() }, actorId: adminId);
        return errors;
    }
}
