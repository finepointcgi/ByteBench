using System.Text;
using System.Text.Json;
using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.TrackerImport;

/// <summary>The choices made in the wizard (stored with the job, never a credential).</summary>
public sealed class TrackerImportSettings
{
    public bool IncludeClosed { get; set; } = true;
    public bool IncludeComments { get; set; } = true;
    /// <summary>GitHub: also import pull requests as issues (labelled "pull-request"). Off by default: pull requests need their branches.</summary>
    public bool IncludePullRequests { get; set; }
    /// <summary>Source status name (any case) → workflow status id here. Names not listed are matched by name, then by category.</summary>
    public Dictionary<string, int> StatusMap { get; set; } = [];
    /// <summary>Source username (any case) → user id here. Users not listed are matched by email, then by user name.</summary>
    public Dictionary<string, int> UserMap { get; set; } = [];
}

/// <summary>
/// Turns the neutral <see cref="SourceIssue"/>s of a reader into issues, comments, labels and milestones of one project (one instance
/// per run). Nothing here sends notifications or webhooks. Safe to run again: <see cref="ImportedItem"/> remembers what exists, so a
/// second run adds only new issues and new comments and never changes what is already here (except giving an issue its epic/parent
/// when it has none yet, and adding a link that is missing).
/// </summary>
public sealed class TrackerImportWriter
{
    public const int MaxWarnings = 100;
    private const int BatchSize = 25;

    private readonly IDbContextFactory<AppDbContext> dbFactory;
    private readonly ProjectAccess access;
    private readonly TrackerImportJob job;
    private readonly TrackerImportSettings settings;
    private readonly string sourceKey, sourceName;
    private readonly int actorId;

    private Project project = null!;
    private List<WorkflowStatus> statuses = [];
    private readonly Dictionary<string, int> labelIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> milestoneIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> issueIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, User?> users = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SourceIssue> batch = [];
    private readonly List<(string Child, string Parent)> parents = [];
    private readonly List<(string From, string To, IssueLinkType Type)> links = [];
    private readonly List<string> warnings = [];
    private readonly List<int> touched = [];
    private readonly HashSet<string> unmatched = new(StringComparer.OrdinalIgnoreCase);
    private double sort;

    public int WarningCount { get; private set; }

    public TrackerImportWriter(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, TrackerImportJob job, TrackerImportSettings settings, string sourceKey, string sourceName, int actorId)
        => (this.dbFactory, this.access, this.job, this.settings, this.sourceKey, this.sourceName, this.actorId) = (dbFactory, access, job, settings, sourceKey, sourceName, actorId);

    public IReadOnlyList<int> TouchedIssueIds => touched;
    public string WarningsJson => JsonSerializer.Serialize(warnings);

    public void Warn(string message)
    {
        WarningCount++;
        if (warnings.Count < MaxWarnings) warnings.Add(message.Length > 300 ? message[..300] : message);
    }

    /// <summary>Loads the project's workflow and what an earlier run already created; creates the source's labels and milestones.</summary>
    public async Task PrepareAsync(ISourceReader reader, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == job.ProjectId, ct);
        statuses = (await WorkflowService.LoadAsync(dbFactory, job.ProjectId)).Statuses.OrderBy(s => s.Order).ToList();
        if (statuses.Count == 0) throw new SourceException("The project's workflow has no statuses.");
        sort = await db.Issues.Where(i => i.ProjectId == job.ProjectId).MaxAsync(i => (double?)i.SortOrder, ct) ?? 0;

        foreach (var m in await db.ImportedItems.AsNoTracking().Where(m => m.ProjectId == job.ProjectId && m.SourceKey == sourceKey && m.Kind != ImportedKind.Comment).ToListAsync(ct))
            (m.Kind switch { ImportedKind.Issue => issueIds, ImportedKind.Milestone => milestoneIds, _ => (Dictionary<string, int>)[] })[m.ExternalId] = m.LocalId;

        var chain = project.GroupId is { } gid ? await ProjectAccess.GroupChainAsync(db, gid) : [];
        foreach (var l in await db.Labels.AsNoTracking().Where(l => l.ProjectId == job.ProjectId || (l.GroupId != null && chain.Contains(l.GroupId.Value))).ToListAsync(ct))
            labelIds.TryAdd(l.Name, l.Id);

        foreach (var l in await reader.ReadLabelsAsync(ct)) await EnsureLabelAsync(l.Name, l.Color, ct);
        foreach (var m in await reader.ReadMilestonesAsync(ct))
        {
            if (milestoneIds.ContainsKey(m.ExternalId)) continue;
            await using var mdb = await dbFactory.CreateDbContextAsync(ct);
            var title = Clip(m.Title, 100);
            var existing = await mdb.Milestones.FirstOrDefaultAsync(x => x.ProjectId == job.ProjectId && x.Title == title, ct);
            if (existing == null)
            {
                existing = new Milestone { ProjectId = job.ProjectId, Title = title, Description = m.Description, StartDate = Day(m.StartDate), DueDate = Day(m.DueDate), IsClosed = m.Closed };
                mdb.Milestones.Add(existing);
                job.MilestonesCreated++;
            }
            mdb.ImportedItems.Add(new ImportedItem { ProjectId = job.ProjectId, SourceKey = sourceKey, Kind = ImportedKind.Milestone, ExternalId = m.ExternalId, ImportId = job.Id });
            await mdb.SaveChangesAsync(ct);
            var item = await mdb.ImportedItems.FirstAsync(i => i.ProjectId == job.ProjectId && i.SourceKey == sourceKey && i.Kind == ImportedKind.Milestone && i.ExternalId == m.ExternalId, ct);
            item.LocalId = existing.Id;
            await mdb.SaveChangesAsync(ct);
            milestoneIds[m.ExternalId] = existing.Id;
        }
    }

    private async Task<int> EnsureLabelAsync(string rawName, string? color, CancellationToken ct)
    {
        var name = Clip(rawName.Trim(), 40);
        if (labelIds.TryGetValue(name, out var id)) return id;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var label = new Label { ProjectId = job.ProjectId, Name = name, Color = NormalizeColor(color) };
        db.Labels.Add(label);
        await db.SaveChangesAsync(ct);
        db.ImportedItems.Add(new ImportedItem { ProjectId = job.ProjectId, SourceKey = sourceKey, Kind = ImportedKind.Label, ExternalId = name.ToLowerInvariant(), LocalId = label.Id, ImportId = job.Id });
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateException) { /* the mapping for this name already exists from an earlier run */ }
        job.LabelsCreated++;
        return labelIds[name] = label.Id;
    }

    /// <summary>Adds an issue to the current batch and writes the batch when it is full. Returns false for an issue imported earlier.</summary>
    public async Task AddAsync(SourceIssue issue, CancellationToken ct)
    {
        job.IssuesRead++;
        foreach (var note in issue.Notes) Warn($"{issue.DisplayKey}: {note}");
        if (issue.ParentExternalId != null) parents.Add((issue.ExternalId, issue.ParentExternalId));
        foreach (var l in issue.Links) links.Add((issue.ExternalId, l.TargetExternalId, l.Type));
        batch.Add(issue);
        if (batch.Count >= BatchSize) await FlushAsync(ct);
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        if (batch.Count == 0) return;
        var items = batch.ToList();
        batch.Clear();
        foreach (var l in items.SelectMany(i => i.Labels)) await EnsureLabelAsync(l, null, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var proj = await db.Projects.FirstAsync(p => p.Id == job.ProjectId, ct);
        var existingComments = (await db.ImportedItems.AsNoTracking()
            .Where(m => m.ProjectId == job.ProjectId && m.SourceKey == sourceKey && m.Kind == ImportedKind.Comment)
            .Select(m => m.ExternalId).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var created = new List<(SourceIssue Source, Issue Issue, List<(SourceComment C, Comment Row)> Comments)>();
        var usedLabelIds = items.SelectMany(i => i.Labels).Select(l => Clip(l.Trim(), 40)).Where(l => l.Length > 0).Select(l => labelIds[l]).Distinct().ToList();
        var labelRows = await db.Labels.Where(l => usedLabelIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, ct);

        foreach (var src in items)
        {
            if (issueIds.TryGetValue(src.ExternalId, out var existingId))
            {
                job.IssuesSkipped++;
                if (settings.IncludeComments && src.Comments.Any(c => !existingComments.Contains(CommentKey(src, c))))
                    await AddMissingCommentsAsync(db, existingId, src, existingComments, ct);
                continue;
            }
            var author = await FindUserAsync(src.Author, ct);
            var assignee = await FindUserAsync(src.Assignee, ct);
            var status = ResolveStatus(src);
            var now = DateTime.UtcNow;
            var createdAt = Utc(src.CreatedAt) ?? now;
            var issue = new Issue
            {
                ProjectId = job.ProjectId,
                Number = proj.NextIssueNumber++,
                Title = Clip(src.Title.Trim().Length == 0 ? src.DisplayKey : src.Title.Trim(), 200),
                Description = Describe(src, author, assignee),
                Type = src.Type,
                Priority = src.Priority ?? IssuePriority.Medium,
                Status = status.Category,
                WorkflowStatusId = status.Id,
                StoryPoints = src.StoryPoints is >= 0 and <= 100 ? src.StoryPoints : null,
                DueDate = Day(src.DueDate),
                SortOrder = ++sort,
                AssigneeId = assignee?.Id,
                ReporterId = author?.Id ?? actorId,
                MilestoneId = src.MilestoneId != null && milestoneIds.TryGetValue(src.MilestoneId, out var mid) ? mid : null,
                CreatedAt = createdAt,
                UpdatedAt = Utc(src.UpdatedAt) ?? createdAt,
                ClosedAt = status.Category.IsClosed() ? Utc(src.ClosedAt) ?? Utc(src.UpdatedAt) ?? createdAt : null,
                Labels = src.Labels.Select(l => Clip(l.Trim(), 40)).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(l => labelRows[labelIds[l]]).ToList(),
            };
            if (src.Title.Trim().Length > 200) issue.Description = $"**Full title:** {src.Title.Trim()}\n\n{issue.Description}";
            issue.Activities.Add(new Activity { ActorId = actorId, Text = $"imported from {sourceName} {src.DisplayKey}", CreatedAt = now });
            var comments = new List<(SourceComment C, Comment Row)>();
            if (settings.IncludeComments)
                foreach (var c in src.Comments.Where(c => !string.IsNullOrWhiteSpace(c.Body)))
                {
                    var cu = await FindUserAsync(c.Author, ct);
                    var row = new Comment { AuthorId = cu?.Id ?? actorId, Body = cu != null ? c.Body : Attribution(c.Author, c.CreatedAt, c.Body), CreatedAt = Utc(c.CreatedAt) ?? createdAt };
                    issue.Comments.Add(row);
                    comments.Add((c, row));
                }
            db.Issues.Add(issue);
            created.Add((src, issue, comments));
        }
        await db.SaveChangesAsync(ct);

        foreach (var (src, issue, comments) in created)
        {
            db.ImportedItems.Add(new ImportedItem { ProjectId = job.ProjectId, SourceKey = sourceKey, Kind = ImportedKind.Issue, ExternalId = src.ExternalId, LocalId = issue.Id, ImportId = job.Id });
            foreach (var (c, row) in comments)
                db.ImportedItems.Add(new ImportedItem { ProjectId = job.ProjectId, SourceKey = sourceKey, Kind = ImportedKind.Comment, ExternalId = CommentKey(src, c), LocalId = row.Id, ImportId = job.Id });
            issueIds[src.ExternalId] = issue.Id;
            touched.Add(issue.Id);
            job.CommentsCreated += comments.Count;
        }
        job.IssuesCreated += created.Count;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await SaveProgressAsync(ct);
    }

    private async Task AddMissingCommentsAsync(AppDbContext db, int issueId, SourceIssue src, HashSet<string> known, CancellationToken ct)
    {
        foreach (var c in src.Comments.Where(c => !string.IsNullOrWhiteSpace(c.Body) && !known.Contains(CommentKey(src, c))))
        {
            var cu = await FindUserAsync(c.Author, ct);
            var row = new Comment { IssueId = issueId, AuthorId = cu?.Id ?? actorId, Body = cu != null ? c.Body : Attribution(c.Author, c.CreatedAt, c.Body), CreatedAt = Utc(c.CreatedAt) ?? DateTime.UtcNow };
            db.Comments.Add(row);
            await db.SaveChangesAsync(ct);
            db.ImportedItems.Add(new ImportedItem { ProjectId = job.ProjectId, SourceKey = sourceKey, Kind = ImportedKind.Comment, ExternalId = CommentKey(src, c), LocalId = row.Id, ImportId = job.Id });
            await db.SaveChangesAsync(ct);
            known.Add(CommentKey(src, c));
            job.CommentsCreated++;
        }
    }

    /// <summary>Writes the last batch, then gives issues their parent (epic) and links, honouring the hierarchy rules.</summary>
    public async Task FinishAsync(CancellationToken ct)
    {
        await FlushAsync(ct);
        if (unmatched.Count > 0)
            Warn($"{unmatched.Count} {(unmatched.Count == 1 ? "person has" : "people have")} no account that can see this project, so their issues and comments are attributed to the person who ran the import (with their name in the text): " +
                 string.Join(", ", unmatched.Take(15)) + (unmatched.Count > 15 ? ", ..." : "") + ". Add them as members (matched by email or user name) and run the import again to see how it looks - existing issues are not changed.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var (child, parent) in parents.Distinct())
        {
            if (!issueIds.TryGetValue(child, out var cid)) continue;
            if (!issueIds.TryGetValue(parent, out var pid)) { Warn($"Parent {parent} of {child} wasn't imported (closed, filtered out, or in another project), so {child} has no parent."); continue; }
            var issue = await db.Issues.FirstOrDefaultAsync(i => i.Id == cid, ct);
            if (issue == null || issue.ParentId != null || cid == pid) continue;
            issue.ParentId = pid;
            try { await HierarchyRules.ValidateAsync(db, issue); }
            catch (InvalidOperationException ex)
            {
                issue.ParentId = null;
                Warn($"{child} wasn't put under {parent}: {ex.Message}");
                continue;
            }
            await HierarchyRules.RecordAsync(db, issue, null, actorId);
            await db.SaveChangesAsync(ct);
        }
        var seenPairs = new HashSet<(int, int)>();
        foreach (var (from, to, type) in links.Distinct())
        {
            if (!issueIds.TryGetValue(from, out var a) || !issueIds.TryGetValue(to, out var b) || a == b) continue;
            if (!seenPairs.Add((Math.Min(a, b), Math.Max(a, b)))) continue;
            if (await db.IssueLinks.AnyAsync(l => (l.SourceIssueId == a && l.TargetIssueId == b) || (l.SourceIssueId == b && l.TargetIssueId == a), ct)) continue;
            db.IssueLinks.Add(new IssueLink { SourceIssueId = a, TargetIssueId = b, Type = type, CreatedById = actorId });
            job.LinksCreated++;
        }
        await db.SaveChangesAsync(ct);
        await SaveProgressAsync(ct);
    }

    private async Task SaveProgressAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.TrackerImportJobs.Where(i => i.Id == job.Id).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.IssuesRead, job.IssuesRead).SetProperty(i => i.IssuesCreated, job.IssuesCreated).SetProperty(i => i.IssuesSkipped, job.IssuesSkipped)
            .SetProperty(i => i.CommentsCreated, job.CommentsCreated).SetProperty(i => i.LabelsCreated, job.LabelsCreated)
            .SetProperty(i => i.MilestonesCreated, job.MilestonesCreated).SetProperty(i => i.LinksCreated, job.LinksCreated)
            .SetProperty(i => i.WarningCount, WarningCount).SetProperty(i => i.WarningsJson, WarningsJson), ct);
    }

    // ---------------------------------------------------------------- mapping helpers

    private static string CommentKey(SourceIssue issue, SourceComment c) => issue.ExternalId + ":" + c.ExternalId;

    private WorkflowStatus ResolveStatus(SourceIssue src)
    {
        if (src.StatusName is { Length: > 0 } name)
        {
            var mapped = settings.StatusMap.FirstOrDefault(kv => kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (mapped.Key != null && statuses.FirstOrDefault(s => s.Id == mapped.Value) is { } byMap) return byMap;
            if (statuses.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } byName) return byName;
        }
        return DefaultFor(statuses, src.Category);
    }

    /// <summary>The workflow status a source category falls back to: the first status of that category, else the nearest open one.</summary>
    public static WorkflowStatus DefaultFor(IReadOnlyList<WorkflowStatus> workflow, IssueStatus category) =>
        workflow.OrderBy(s => s.Order).FirstOrDefault(s => s.Category == category)
        ?? (category.IsClosed() ? workflow.OrderBy(s => s.Order).Last() : workflow.OrderBy(s => s.Order).FirstOrDefault(s => s.Category == IssueStatus.Todo) ?? workflow.OrderBy(s => s.Order).First());

    private async Task<User?> FindUserAsync(SourceUser? who, CancellationToken ct)
    {
        if (who == null || (who.Login ?? who.Email ?? who.Name) is not { Length: > 0 } key) return null;
        if (users.TryGetValue(key, out var cached)) return cached;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        User? found = null;
        if (who.Login != null && settings.UserMap.FirstOrDefault(kv => kv.Key.Equals(who.Login, StringComparison.OrdinalIgnoreCase)) is { Key: not null } m)
            found = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == m.Value, ct);
        if (found == null && who.Email is { Length: > 0 } email)
        {
            var upper = email.ToUpperInvariant();
            found = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedEmail == upper, ct);
        }
        if (found == null && who.Login is { Length: > 0 } login)
        {
            var upper = login.ToUpperInvariant();
            found = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedUserName == upper, ct);
        }
        if (found == null && who.Login == null && who.Email == null && who.Name is { Length: > 0 } display)
        {
            // Jira Cloud shows neither a user name nor an email: accept an exact display-name match only when exactly one account has it.
            var same = await db.Users.AsNoTracking().Where(u => u.DisplayName == display).Take(2).ToListAsync(ct);
            if (same.Count == 1) found = same[0];
        }
        if (found != null && !await access.CanAsync(job.ProjectId, Permission.ViewProject, found.Id)) found = null;
        if (found == null) unmatched.Add(Name(who) ?? key);
        return users[key] = found;
    }

    private string Describe(SourceIssue src, User? author, User? assignee)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(src.Body)) sb.Append(src.Body.Trim()).Append("\n\n");
        sb.Append("---\n");
        var from = src.Url != null ? $"[{sourceName} {src.DisplayKey}]({src.Url})" : $"{sourceName} {src.DisplayKey}";
        sb.Append("_Imported from ").Append(from);
        if (author == null && Name(src.Author) is { } n) sb.Append(". Opened by ").Append(n);
        sb.Append(" on ").Append(src.CreatedAt.ToString("yyyy-MM-dd")).Append("._");
        if (assignee == null && Name(src.Assignee) is { } a) sb.Append("\n\n_Assigned in ").Append(sourceName).Append(" to ").Append(a).Append(" (no matching user here)._");
        var files = src.Attachments.Where(f => !string.IsNullOrWhiteSpace(f.FileName)).ToList();
        if (files.Count > 0)
        {
            sb.Append("\n\n**Attachments in ").Append(sourceName).Append(" (not copied):**\n");
            foreach (var f in files) sb.Append("- ").Append(f.Url != null ? $"[{f.FileName}]({f.Url})" : f.FileName).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private static string Attribution(SourceUser? who, DateTime at, string body) =>
        $"_{Name(who) ?? "Someone"} commented on {at:yyyy-MM-dd}:_\n\n{body.Trim()}";

    private static string? Name(SourceUser? u) => u == null ? null : u.Name is { Length: > 0 } n ? (u.Login is { Length: > 0 } l && l != n ? $"{n} (@{l})" : n) : u.Login is { Length: > 0 } l2 ? "@" + l2 : null;

    private static DateTime? Utc(DateTime? d) => d == null ? null : d.Value.Kind == DateTimeKind.Utc ? d : d.Value.Kind == DateTimeKind.Local ? d.Value.ToUniversalTime() : DateTime.SpecifyKind(d.Value, DateTimeKind.Utc);
    private static DateTime? Day(DateTime? d) => Utc(d) is { } u ? DateTime.SpecifyKind(u.Date, DateTimeKind.Utc) : null;
    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    private static string NormalizeColor(string? c)
    {
        c = c?.Trim().TrimStart('#');
        return c is { Length: 6 } && c.All(Uri.IsHexDigit) ? "#" + c.ToLowerInvariant() : "#6b7280";
    }
}
