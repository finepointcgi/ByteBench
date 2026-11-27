using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Workspaces;

/// <summary>
/// The file changes of one assistant request in review mode, collected in memory while the tool loop runs: the first content a file had when
/// the assistant touched it and the content it wants it to have. Nothing here touches the workspace; <see cref="AiChangeService"/> stores
/// the result as a pending change set.
/// </summary>
public sealed class AiProposalCollector(int maxFiles = 50, long maxBytes = 5 * 1024 * 1024)
{
    public sealed record Entry(string Path, string? OriginalText, string? NewText);

    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly List<string> order = [];

    public Entry? Find(string path) => entries.GetValueOrDefault(path);

    /// <summary>Records the proposed content (null: delete). Returns a message for the model when a limit stops it.</summary>
    public string? Set(string path, string? original, string? newText)
    {
        if (!entries.ContainsKey(path))
        {
            if (entries.Count >= maxFiles) return $"Too many files proposed at once (the limit is {maxFiles}). Finish with what you have; the developer can ask for more.";
            order.Add(path);
        }
        var next = new Entry(path, entries.TryGetValue(path, out var old) ? old.OriginalText : original, newText);
        var total = entries.Where(e => e.Key != path).Sum(e => (long)(e.Value.NewText?.Length ?? 0) + (e.Value.OriginalText?.Length ?? 0)) + (next.NewText?.Length ?? 0) + (next.OriginalText?.Length ?? 0);
        if (total > maxBytes) return "The proposed changes are too large to review in one go. Make fewer or smaller changes.";
        entries[path] = next;
        return null;
    }

    /// <summary>Entries that actually differ from the original (a file edited and then edited back is not a change), in the order they were first touched.</summary>
    public IReadOnlyList<Entry> Changes => order.Select(p => entries[p]).Where(e => e.OriginalText != e.NewText).ToList();

    public bool HasChanges => Changes.Count > 0;
}

public sealed record AiHunkView(int Index, int OldStart, int NewStart, IReadOnlyList<string> Removed, IReadOnlyList<string> Added,
    IReadOnlyList<string> ContextBefore, IReadOnlyList<string> ContextAfter);

public sealed record AiChangeFileView(int Id, string Path, AiChangeKind Kind, AiChangeDecision Decision, string? Note, int AddedLines, int RemovedLines,
    IReadOnlyList<AiHunkView> Hunks, IReadOnlyList<int> AcceptedHunks);

public sealed record AiChangeSetView(int Id, string Prompt, string? Summary, AiChangeSetState State, DateTime CreatedAt, DateTime? ResolvedAt,
    IReadOnlyList<AiChangeFileView> Files);

public sealed record AiModeInfo(AiEditMode Effective, AiEditMode? WorkspaceMode, AiEditMode? DefaultMode);

/// <summary>What happened to a change set after a decision.</summary>
public sealed record AiDecisionResult(AiChangeSetView ChangeSet, int Applied, int Rejected, int Conflicts);

/// <summary>
/// Review mode for the workspace assistant: the per-user / per-workspace "review first" or "apply directly" setting, the pending change sets the
/// assistant produces, and accepting or rejecting them per file, per hunk or all at once. Only the workspace's owner may see or decide; accepted
/// text is written into the workspace volume through the same checked tools the assistant uses (as the unprivileged assistant user), and only
/// when the file still holds what the proposal was made against. Every proposal and decision is audited (<c>workspace.ai_*</c>).
/// </summary>
public sealed class AiChangeService(IDbContextFactory<AppDbContext> dbFactory, WorkspaceService workspaces, IWorkspaceBackend backend,
    IOptionsMonitor<WorkspaceOptions> options, AuditLog audit)
{
    // ---------- the setting ----------

    public async Task<AiModeInfo> GetModeInfoAsync(int userId, int workspaceId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.AiWorkspacePreferences.AsNoTracking().Where(p => p.UserId == userId && (p.WorkspaceId == workspaceId || p.WorkspaceId == null)).ToListAsync();
        var ws = rows.FirstOrDefault(r => r.WorkspaceId == workspaceId)?.Mode;
        var def = rows.FirstOrDefault(r => r.WorkspaceId == null)?.Mode;
        return new AiModeInfo(ws ?? def ?? AiEditMode.ReviewFirst, ws, def);
    }

    public async Task<AiModeInfo> GetModeInfoAsync(string publicId, int userId)
    {
        var w = await workspaces.GetMineAsync(publicId, userId) ?? throw new InvalidOperationException("Workspace not found.");
        return await GetModeInfoAsync(userId, w.Workspace.Id);
    }

    /// <summary>Sets the mode for this workspace (<paramref name="forAllWorkspaces"/> false) or as the user's default; null clears it (back to the default / review first).</summary>
    public async Task<AiModeInfo> SetModeAsync(string publicId, int userId, AiEditMode? mode, bool forAllWorkspaces)
    {
        var w = (await workspaces.GetMineAsync(publicId, userId) ?? throw new InvalidOperationException("Workspace not found.")).Workspace;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            int? scope = forAllWorkspaces ? null : w.Id;
            var row = await db.AiWorkspacePreferences.FirstOrDefaultAsync(p => p.UserId == userId && p.WorkspaceId == scope);
            if (mode == null) { if (row != null) db.AiWorkspacePreferences.Remove(row); }
            else if (row == null) db.AiWorkspacePreferences.Add(new AiWorkspacePreference { UserId = userId, WorkspaceId = scope, Mode = mode.Value });
            else { row.Mode = mode.Value; row.UpdatedAt = DateTime.UtcNow; }
            await db.SaveChangesAsync();
        }
        await audit.RecordAsync("workspace.ai_mode", "workspace", w.PublicId, w.ProjectId, new { mode = mode?.ToString(), scope = forAllWorkspaces ? "default" : "workspace" }, userId);
        return await GetModeInfoAsync(userId, w.Id);
    }

    // ---------- proposals ----------

    /// <summary>Stores what the assistant proposed as a pending change set (null if it proposed nothing that differs from the workspace).</summary>
    public async Task<AiChangeSet?> SaveProposalsAsync(Workspace ws, int userId, string prompt, string? summary, AiProposalCollector collector)
    {
        var changes = collector.Changes;
        if (changes.Count == 0) return null;
        var set = new AiChangeSet
        {
            WorkspaceId = ws.Id, UserId = userId, Prompt = Cap(prompt.Trim(), 300), Summary = summary is { Length: > 0 } ? Cap(summary, 2000) : null,
            Files = changes.Select(c => new AiChangeFile
            {
                Path = c.Path, OriginalText = c.OriginalText, NewText = c.NewText,
                Kind = c.OriginalText == null ? AiChangeKind.Create : c.NewText == null ? AiChangeKind.Delete : AiChangeKind.Modify,
            }).ToList(),
        };
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.AiChangeSets.Add(set);
            await db.SaveChangesAsync();
        }
        await audit.RecordAsync("workspace.ai_changes_proposed", "workspace", ws.PublicId, ws.ProjectId,
            new { changeSetId = set.Id, files = set.Files.Select(f => new { f.Path, kind = f.Kind.ToString() }).ToArray() }, userId);
        return set;
    }

    // ---------- reading ----------

    public async Task<List<AiChangeSetView>> ListAsync(string publicId, int userId, bool pendingOnly = false)
    {
        var w = (await workspaces.GetMineAsync(publicId, userId) ?? throw new InvalidOperationException("Workspace not found.")).Workspace;
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.AiChangeSets.AsNoTracking().Include(c => c.Files).Where(c => c.WorkspaceId == w.Id);
        if (pendingOnly) q = q.Where(c => c.State == AiChangeSetState.Pending);
        var sets = await q.OrderByDescending(c => c.Id).Take(50).ToListAsync();
        return sets.Select(View).ToList();
    }

    public async Task<AiChangeSetView> GetAsync(string publicId, int userId, int changeSetId)
    {
        var (_, set) = await LoadAsync(publicId, userId, changeSetId);
        return View(set);
    }

    // ---------- deciding ----------

    /// <summary>
    /// Decides one file: <paramref name="acceptedHunks"/> null accepts all of it, an empty list rejects it, otherwise only those hunks are applied
    /// (the rest are discarded). Applies to the workspace at once. A file already decided can't be decided again.
    /// </summary>
    public async Task<AiDecisionResult> DecideFileAsync(string publicId, int userId, int changeSetId, int fileId, IReadOnlyCollection<int>? acceptedHunks, CancellationToken ct = default)
    {
        var (ws, set) = await LoadAsync(publicId, userId, changeSetId);
        var file = set.Files.FirstOrDefault(f => f.Id == fileId) ?? throw new InvalidOperationException("That file isn't part of this change set.");
        if (file.Decision != AiChangeDecision.Pending) throw new InvalidOperationException("This file was already decided.");
        var tools = await ToolsForAsync(publicId, userId, needed: acceptedHunks == null || acceptedHunks.Count > 0);
        var counts = await DecideAsync(ws, set, file, acceptedHunks, tools, ct);
        await FinishAsync(ws, set, userId);
        return new AiDecisionResult(View(set), counts.Applied, counts.Rejected, counts.Conflicts);
    }

    public Task<AiDecisionResult> AcceptAllAsync(string publicId, int userId, int changeSetId, CancellationToken ct = default) => DecideAllAsync(publicId, userId, changeSetId, true, ct);
    public Task<AiDecisionResult> RejectAllAsync(string publicId, int userId, int changeSetId, CancellationToken ct = default) => DecideAllAsync(publicId, userId, changeSetId, false, ct);

    private async Task<AiDecisionResult> DecideAllAsync(string publicId, int userId, int changeSetId, bool accept, CancellationToken ct)
    {
        var (ws, set) = await LoadAsync(publicId, userId, changeSetId);
        var pending = set.Files.Where(f => f.Decision == AiChangeDecision.Pending).ToList();
        var tools = pending.Count > 0 ? await ToolsForAsync(publicId, userId, needed: accept) : null;
        int applied = 0, rejected = 0, conflicts = 0;
        foreach (var f in pending)
        {
            var c = await DecideAsync(ws, set, f, accept ? null : [], tools, ct);
            applied += c.Applied; rejected += c.Rejected; conflicts += c.Conflicts;
        }
        await FinishAsync(ws, set, userId);
        return new AiDecisionResult(View(set), applied, rejected, conflicts);
    }

    private async Task<(int Applied, int Rejected, int Conflicts)> DecideAsync(Workspace ws, AiChangeSet set, AiChangeFile file, IReadOnlyCollection<int>? acceptedHunks,
        WorkspaceAiTools? tools, CancellationToken ct)
    {
        var diff = LineDiff.Compute(file.OriginalText, file.NewText);
        var all = Enumerable.Range(0, diff.Hunks.Count).ToHashSet();
        var chosen = acceptedHunks == null ? all : acceptedHunks.Where(all.Contains).ToHashSet();
        var wantsAny = acceptedHunks == null || acceptedHunks.Count > 0;
        if (file.Kind == AiChangeKind.Delete) chosen = wantsAny ? all : []; // a deletion is all or nothing

        AiChangeDecision decision;
        string? note = null;
        if (!wantsAny || (diff.Hunks.Count > 0 && chosen.Count == 0)) decision = AiChangeDecision.Rejected;
        else
        {
            var (exists, current, error) = await tools!.ReadRawAsync(file.Path, ct);
            if (error != null) { decision = AiChangeDecision.Conflict; note = error; }
            else if (file.Kind == AiChangeKind.Create && exists && current != file.NewText) { decision = AiChangeDecision.Conflict; note = "The file exists in the workspace now."; }
            else if (file.Kind != AiChangeKind.Create && (!exists ? file.Kind != AiChangeKind.Delete : current != file.OriginalText)) { decision = AiChangeDecision.Conflict; note = exists ? "The file changed in the workspace since this was proposed." : "The file no longer exists in the workspace."; }
            else
            {
                var full = chosen.Count == diff.Hunks.Count;
                (bool Ok, string Message) result;
                if (file.Kind == AiChangeKind.Delete) result = exists ? await tools.DeleteAsync(file.Path, ct) : (true, "Already gone.");
                else if (file.Kind == AiChangeKind.Create && exists) result = (true, "Already there.");
                else result = await tools.WriteTextAsync(file.Path, full ? file.NewText ?? "" : diff.Apply(chosen), ct);
                if (!result.Ok) { decision = AiChangeDecision.Conflict; note = result.Message; }
                else
                {
                    decision = full ? AiChangeDecision.Accepted : AiChangeDecision.Partial;
                    if (!full) file.AcceptedHunksJson = JsonSerializer.Serialize(chosen.Order());
                }
            }
        }

        file.Decision = decision;
        file.Note = note is { Length: > 0 } ? Cap(note, 300) : null;
        file.DecidedAt = DateTime.UtcNow;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var (d, hunks, n, at) = (file.Decision, file.AcceptedHunksJson, file.Note, file.DecidedAt);
            await db.AiChangeFiles.Where(f => f.Id == file.Id).ExecuteUpdateAsync(s => s.SetProperty(f => f.Decision, d)
                .SetProperty(f => f.AcceptedHunksJson, hunks).SetProperty(f => f.Note, n).SetProperty(f => f.DecidedAt, at), ct);
        }
        var action = decision switch
        {
            AiChangeDecision.Accepted or AiChangeDecision.Partial => "workspace.ai_change_applied",
            AiChangeDecision.Rejected => "workspace.ai_change_rejected",
            _ => "workspace.ai_change_conflict",
        };
        await audit.RecordAsync(action, "workspace", ws.PublicId, ws.ProjectId,
            new { changeSetId = set.Id, path = file.Path, kind = file.Kind.ToString(), decision = decision.ToString(), hunks = decision == AiChangeDecision.Partial ? $"{chosen.Count}/{diff.Hunks.Count}" : null, note }, set.UserId);
        return decision is AiChangeDecision.Accepted or AiChangeDecision.Partial ? (1, 0, 0) : decision == AiChangeDecision.Rejected ? (0, 1, 0) : (0, 0, 1);
    }

    private async Task FinishAsync(Workspace ws, AiChangeSet set, int userId)
    {
        if (set.State == AiChangeSetState.Resolved || set.Files.Any(f => f.Decision == AiChangeDecision.Pending)) return;
        set.State = AiChangeSetState.Resolved;
        set.ResolvedAt = DateTime.UtcNow;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.AiChangeSets.Where(c => c.Id == set.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.State, AiChangeSetState.Resolved).SetProperty(c => c.ResolvedAt, set.ResolvedAt));
        }
        await audit.RecordAsync("workspace.ai_changeset_resolved", "workspace", ws.PublicId, ws.ProjectId, new
        {
            changeSetId = set.Id,
            accepted = set.Files.Count(f => f.Decision == AiChangeDecision.Accepted),
            partial = set.Files.Count(f => f.Decision == AiChangeDecision.Partial),
            rejected = set.Files.Count(f => f.Decision == AiChangeDecision.Rejected),
            conflicts = set.Files.Count(f => f.Decision == AiChangeDecision.Conflict),
        }, userId);
    }

    // ---------- helpers ----------

    private async Task<(Workspace Ws, AiChangeSet Set)> LoadAsync(string publicId, int userId, int changeSetId)
    {
        var w = (await workspaces.GetMineAsync(publicId, userId) ?? throw new InvalidOperationException("Workspace not found.")).Workspace;
        await using var db = await dbFactory.CreateDbContextAsync();
        var set = await db.AiChangeSets.AsNoTracking().Include(c => c.Files).FirstOrDefaultAsync(c => c.Id == changeSetId && c.WorkspaceId == w.Id)
                  ?? throw new InvalidOperationException("Change set not found.");
        set.Files = set.Files.OrderBy(f => f.Id).ToList();
        return (w, set);
    }

    /// <summary>The assistant's file tools for the owner's running workspace (used to write accepted changes); null when nothing needs writing.</summary>
    private async Task<WorkspaceAiTools?> ToolsForAsync(string publicId, int userId, bool needed)
    {
        if (!needed) return null;
        var (live, ws) = await workspaces.GetRunningAsync(publicId, userId); // throws if it isn't the owner's or isn't running
        return new WorkspaceAiTools(backend, live.ContainerId, ws.Repository.Slug, options.CurrentValue.Ai);
    }

    private static AiChangeSetView View(AiChangeSet s) => new(s.Id, s.Prompt, s.Summary, s.State, s.CreatedAt, s.ResolvedAt, s.Files.OrderBy(f => f.Id).Select(FileView).ToList());

    private static AiChangeFileView FileView(AiChangeFile f)
    {
        var diff = LineDiff.Compute(f.OriginalText, f.NewText);
        var accepted = f.AcceptedHunksJson is { Length: > 0 } j ? JsonSerializer.Deserialize<List<int>>(j) ?? [] : [];
        return new AiChangeFileView(f.Id, f.Path, f.Kind, f.Decision, f.Note, diff.AddedLines, diff.RemovedLines,
            diff.Hunks.Select(h => new AiHunkView(h.Index, h.OldStart, h.NewStart, h.OldLines.Select(Trim).ToList(), h.NewLines.Select(Trim).ToList(),
                h.ContextBefore.Select(Trim).ToList(), h.ContextAfter.Select(Trim).ToList())).ToList(), accepted);
    }

    private static string Trim(string line) => line.TrimEnd('\n');
    private static string Cap(string s, int max) => s.Length <= max ? s : s[..max];
}
