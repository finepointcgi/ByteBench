using System.Text;
using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Workbench;

/// <summary>The "Workbench sync" system account that creates and updates synced issues (no password, can't sign in), like the other bots.</summary>
public static class WorkbenchBot
{
    public const string UserName = "bytebench-workbench-sync";
    public const string DisplayName = "Workbench sync";

    public static async Task<int> EnsureAsync(AppDbContext db, CancellationToken ct = default)
    {
        var normalized = UserName.ToUpperInvariant();
        var id = await db.Users.Where(u => u.NormalizedUserName == normalized).Select(u => (int?)u.Id).FirstOrDefaultAsync(ct);
        if (id is { } existing) return existing;
        const string email = "bytebench-workbench-sync@bytebench.local";
        var bot = new User
        {
            UserName = UserName, NormalizedUserName = normalized, Email = email, NormalizedEmail = email.ToUpperInvariant(), EmailConfirmed = true,
            PasswordHash = null, LockoutEnabled = true, LockoutEnd = DateTimeOffset.MaxValue, SecurityStamp = Guid.NewGuid().ToString(),
            DisplayName = DisplayName, Color = "#2563eb",
        };
        db.Users.Add(bot);
        try { await db.SaveChangesAsync(ct); return bot.Id; }
        catch (DbUpdateException)
        {
            db.Entry(bot).State = EntityState.Detached;
            return await db.Users.Where(u => u.NormalizedUserName == normalized).Select(u => u.Id).FirstAsync(ct);
        }
    }
}

/// <summary>What one plan task becomes in ByteBench.</summary>
public sealed record DesiredIssue(PlanNode Node, IssueType Type, Guid? ParentTaskId, string Title, DateTime? Start, DateTime? Due, int? EstimateMinutes, string? Wbs, string Block);

/// <summary>
/// Plan task to issue mapping (mirrors Workbench's GitLab connector): a summary task at one of the first <c>epicLevels</c> levels becomes an
/// epic; epics can't nest in ByteBench, so nested summaries are flattened to top-level epics, and a summary below the epic levels becomes a
/// task under its nearest epic; a detail task becomes a task under its epic, or a sub-task under a summary task. Every flattening is noted.
/// </summary>
public static class WorkbenchPlanMapper
{
    public const string BlockStart = "<!-- workbench-sync:start -->";
    public const string BlockEnd = "<!-- workbench-sync:end -->";

    public static (IReadOnlyList<DesiredIssue> Items, IReadOnlyList<string> Notes, string? Error) Map(PlanSnapshot snap, string? rootWbs, int epicLevels)
    {
        var notes = new List<string>();
        var all = snap.Nodes;
        IEnumerable<PlanNode> nodes = all;
        var baseDepth = 0;
        if (!string.IsNullOrWhiteSpace(rootWbs))
        {
            var root = all.FirstOrDefault(n => string.Equals(n.WbsCode, rootWbs, StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault(n => string.Equals(n.Outline, rootWbs, StringComparison.OrdinalIgnoreCase));
            if (root == null) return ([], notes, $"Workbench project {snap.Project.Code} has no WBS element {rootWbs}.");
            var ids = new HashSet<Guid> { root.Id };
            var sub = new List<PlanNode>();
            foreach (var n in all) if (n.ParentId is { } p && ids.Contains(p)) { ids.Add(n.Id); sub.Add(n); }
            nodes = sub;
            baseDepth = root.Depth + 1;
        }
        var list = nodes.ToList();
        var kind = new Dictionary<Guid, IssueType>();
        var byId = list.ToDictionary(n => n.Id);
        var result = new List<DesiredIssue>();
        var flattened = false;
        foreach (var n in list)
        {
            var rel = n.Depth - baseDepth;
            // nearest ancestor (inside the synced part) that is an issue
            PlanNode? anc = n.ParentId is { } parentId && byId.TryGetValue(parentId, out var a) ? a : null;
            IssueType type;
            Guid? parent = null;
            if (n.IsSummary && rel < epicLevels)
            {
                type = IssueType.Epic;
                if (anc != null) flattened = true;
            }
            else
            {
                var epicAnc = anc;
                while (epicAnc != null && kind[epicAnc.Id] != IssueType.Epic) epicAnc = epicAnc.ParentId is { } pid && byId.TryGetValue(pid, out var up) ? up : null;
                if (n.IsSummary)
                {
                    type = IssueType.Task; parent = epicAnc?.Id;
                    if (anc != null && anc.Id != epicAnc?.Id) flattened = true;
                }
                else if (anc != null && kind[anc.Id] == IssueType.Task) { type = IssueType.Subtask; parent = anc.Id; }
                else { type = IssueType.Task; parent = anc != null && kind[anc.Id] == IssueType.Epic ? anc.Id : epicAnc?.Id; }
            }
            kind[n.Id] = type;
            var due = n.Finish?.ToDateTime(TimeOnly.MinValue);
            var start = n.Start?.ToDateTime(TimeOnly.MinValue);
            int? est = n.Hours is { } h && !n.IsSummary ? (int)Math.Round(h * 60m) : null;
            var wbs = n.WbsCode != null && n.WbsCode.Length <= 50 && System.Text.RegularExpressions.Regex.IsMatch(n.WbsCode, @"^[A-Za-z0-9][A-Za-z0-9._\-/]*$") ? n.WbsCode : null;
            var title = (n.Title.Trim().Length == 0 ? "(untitled task)" : n.Title.Trim());
            result.Add(new DesiredIssue(n, type, parent, title.Length > 200 ? title[..200] : title, start, due, est, wbs, Block(n, snap.Project)));
        }
        if (flattened) notes.Add("Epics can't nest in ByteBench: nested summary tasks were flattened (nested epics became top-level epics; deeper summaries became tasks under their epic). The WBS code on each issue keeps the structure.");
        if (snap.HoursHidden) notes.Add("The key's roles can't read project financials, so planned hours come only from a Hours column on the plan (if it has one).");
        return (result, notes, null);
    }

    /// <summary>The block the sync owns inside an issue's description: plan facts and the plan's Notes column.</summary>
    public static string Block(PlanNode n, WbProject project)
    {
        var sb = new StringBuilder();
        sb.AppendLine(BlockStart);
        sb.Append("**Workbench plan** (").Append(project.Code).Append(')');
        if (n.WbsCode != null) sb.Append(" · WBS `").Append(n.WbsCode).Append('`');
        if (n.Outline != null) sb.Append(" · outline ").Append(n.Outline);
        if (n.Hours is { } h) sb.Append(" · planned ").Append(h.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append(" h");
        if (n.Start != null || n.Finish != null) sb.Append(" · ").Append(n.Start?.ToString("yyyy-MM-dd") ?? "?").Append(" to ").Append(n.Finish?.ToString("yyyy-MM-dd") ?? "?");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(n.Notes)) { sb.AppendLine(); sb.AppendLine(n.Notes.Trim()); }
        sb.AppendLine();
        sb.Append("_Managed by the Workbench sync: dates, estimate and WBS come from Workbench and are put back if edited here. Edit outside this block._").AppendLine();
        sb.Append(BlockEnd);
        return sb.ToString();
    }

    /// <summary>Puts <paramref name="block"/> in <paramref name="description"/>: replaces an existing managed block, else appends it.</summary>
    public static string ApplyBlock(string? description, string block)
    {
        var d = description ?? "";
        var s = d.IndexOf(BlockStart, StringComparison.Ordinal);
        var e = s < 0 ? -1 : d.IndexOf(BlockEnd, s, StringComparison.Ordinal);
        if (s >= 0 && e > s) return d[..s] + block + d[(e + BlockEnd.Length)..];
        return d.Length == 0 ? block : d.TrimEnd() + "\n\n" + block;
    }
}
