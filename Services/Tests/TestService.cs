using ByteBench.Api;
using ByteBench.Data;
using ByteBench.Services.Attachments;
using ByteBench.Services.Audit;
using ByteBench.Services.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Tests;

/// <summary>What the case form (or the API) submits.</summary>
public sealed class TestCaseInput
{
    public string Title { get; set; } = "";
    public string? Preconditions { get; set; }
    public string? Steps { get; set; }
    public string? Expected { get; set; }
    public TestPriority Priority { get; set; } = TestPriority.Medium;
    public TestCaseType Type { get; set; } = TestCaseType.Manual;
    public string? Tags { get; set; }
    public string? Suite { get; set; }
    /// <summary>Issue numbers of the project, comma or space separated (<c>12, 15</c> or <c>WEB-12</c>).</summary>
    public string? LinkedIssues { get; set; }
    public string? Requirements { get; set; }
    public string? AutomatedName { get; set; }
    /// <summary>Optional note stored with the revision this edit creates.</summary>
    public string? Note { get; set; }
}

/// <summary>Counts of a run's results. <see cref="PassRate"/> is passed / (passed + failed + blocked): skipped and not-run cases don't count either way.</summary>
public sealed record TestRunSummary(int Passed, int Failed, int Blocked, int Skipped, int NotRun)
{
    public int Total => Passed + Failed + Blocked + Skipped + NotRun;
    public int Executed => Total - NotRun;
    /// <summary>0..100, or null if nothing counted yet.</summary>
    public double? PassRate => Passed + Failed + Blocked is var d and > 0 ? Math.Round(100.0 * Passed / d, 1) : null;
    /// <summary>0..100: share of cases with a result.</summary>
    public double Progress => Total == 0 ? 0 : Math.Round(100.0 * Executed / Total, 1);

    public static TestRunSummary Of(IEnumerable<TestResultStatus> statuses)
    {
        int p = 0, f = 0, b = 0, s = 0, n = 0;
        foreach (var st in statuses)
            switch (st)
            {
                case TestResultStatus.Passed: p++; break;
                case TestResultStatus.Failed: f++; break;
                case TestResultStatus.Blocked: b++; break;
                case TestResultStatus.Skipped: s++; break;
                default: n++; break;
            }
        return new TestRunSummary(p, f, b, s, n);
    }
}

public sealed record TestRunRow(TestRun Run, TestRunSummary Summary);

/// <summary>
/// Test management of a project (scoped). Reading needs ViewProject; writing, running and recording cases, runs and results needs
/// <see cref="Permission.RunTests"/> (Developer+); plans, archiving and deleting need <see cref="Permission.ManageTests"/> (Maintainer+).
/// Case edits keep their old text as revisions; every create, delete, plan change and run close is audited, and finished runs raise
/// the <c>test_run.completed</c> webhook. Split over <c>TestService.*.cs</c>.
/// </summary>
public partial class TestService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, AuditLog audit, WebhookDispatcher webhooks,
    PublicBaseUrl baseUrl, AttachmentStorage files, IssueService issues, RepositoryService repos, IConfiguration config, NotificationService notifications)
{
    public const int MaxEvidencePerResult = 20;
    public const long MaxEvidenceBytes = 25L * 1024 * 1024;

    private static string? Clean(string? s, int max, string field)
    {
        s = string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        if (s != null && s.Length > max) throw new InvalidOperationException($"{field} can be at most {max} characters.");
        return s;
    }

    private static string? NormalizeTags(string? tags)
    {
        var list = (tags ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant()).Distinct().Take(20).ToList();
        if (list.Any(t => t.Length > 40)) throw new InvalidOperationException("A tag can be at most 40 characters.");
        return list.Count == 0 ? null : string.Join(",", list);
    }

    private static string? NormalizeIssues(string? text, string projectKey)
    {
        var numbers = new SortedSet<int>();
        foreach (var part in (text ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = part.StartsWith(projectKey + "-", StringComparison.OrdinalIgnoreCase) ? part[(projectKey.Length + 1)..] : part.TrimStart('#');
            if (!int.TryParse(p, out var n) || n <= 0) throw new InvalidOperationException($"'{part}' is not an issue number of this project.");
            numbers.Add(n);
        }
        return numbers.Count == 0 ? null : string.Join(",", numbers.Take(30));
    }

    private static string? NormalizeSuite(string? suite)
    {
        var s = string.Join("/", (suite ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (s.Length > 200) throw new InvalidOperationException("The suite can be at most 200 characters.");
        return s.Length == 0 ? null : s;
    }

    private async Task<string> ProjectKeyAsync(AppDbContext db, int projectId) =>
        await db.Projects.Where(p => p.Id == projectId).Select(p => p.Key).FirstOrDefaultAsync() ?? throw new InvalidOperationException("Project not found.");

    /// <summary>User names for ids (to show who recorded a result). Only ids the caller already sees on a page of the project.</summary>
    public async Task<Dictionary<int, string>> UserNamesAsync(IEnumerable<int?> ids)
    {
        var list = ids.Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
        if (list.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Users.Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.UserName ?? "");
    }

    // ---------------------------------------------------------------- cases

    public sealed record CaseFilter(string? Query = null, string? Suite = null, TestPriority? Priority = null, TestCaseType? Type = null,
        string? Tag = null, bool IncludeArchived = false);

    public async Task<List<TestCase>> ListCasesAsync(int projectId, CaseFilter? filter = null)
    {
        await access.RequireAsync(projectId, Permission.ViewProject);
        filter ??= new CaseFilter();
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.TestCases.AsNoTracking().Where(c => c.ProjectId == projectId);
        if (!filter.IncludeArchived) q = q.Where(c => !c.IsArchived);
        if (filter.Priority is { } pr) q = q.Where(c => c.Priority == pr);
        if (filter.Type is { } ty) q = q.Where(c => c.Type == ty);
        var list = await q.OrderBy(c => c.Number).ToListAsync();
        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            var t = filter.Query.Trim();
            list = list.Where(c => c.Title.Contains(t, StringComparison.OrdinalIgnoreCase) || c.Key.Equals(t, StringComparison.OrdinalIgnoreCase)
                                   || (c.AutomatedName?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
                                   || (c.Steps?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        }
        if (!string.IsNullOrWhiteSpace(filter.Suite))
        {
            var s = filter.Suite.Trim().Trim('/');
            list = list.Where(c => c.Suite != null && (c.Suite.Equals(s, StringComparison.OrdinalIgnoreCase) || c.Suite.StartsWith(s + "/", StringComparison.OrdinalIgnoreCase))).ToList();
        }
        if (!string.IsNullOrWhiteSpace(filter.Tag))
        {
            var tag = filter.Tag.Trim().ToLowerInvariant();
            list = list.Where(c => c.TagList.Contains(tag)).ToList();
        }
        return list;
    }

    public async Task<TestCase?> GetCaseAsync(int projectId, int number)
    {
        await access.RequireAsync(projectId, Permission.ViewProject);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.TestCases.AsNoTracking().FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Number == number);
    }

    public async Task<List<TestCaseRevision>> RevisionsAsync(int projectId, int number)
    {
        await access.RequireAsync(projectId, Permission.ViewProject);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.TestCaseRevisions.AsNoTracking().Where(r => r.TestCase.ProjectId == projectId && r.TestCase.Number == number)
            .OrderByDescending(r => r.Version).ToListAsync();
    }

    public async Task<List<string>> SuitesAsync(int projectId)
    {
        await access.RequireAsync(projectId, Permission.ViewProject);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.TestCases.Where(c => c.ProjectId == projectId && c.Suite != null && !c.IsArchived).Select(c => c.Suite!).Distinct().OrderBy(s => s).ToListAsync();
    }

    private static void Apply(TestCase c, TestCaseInput input, string projectKey)
    {
        var title = Clean(input.Title, 200, "The title") ?? throw new InvalidOperationException("The title is required.");
        c.Title = title;
        c.Preconditions = Clean(input.Preconditions, 20000, "Preconditions");
        c.Steps = Clean(input.Steps, 50000, "Steps");
        c.Expected = Clean(input.Expected, 20000, "The expected result");
        if (!Enum.IsDefined(input.Priority)) throw new InvalidOperationException("Unknown priority.");
        if (!Enum.IsDefined(input.Type)) throw new InvalidOperationException("Unknown type.");
        c.Priority = input.Priority;
        c.Type = input.Type;
        c.Tags = NormalizeTags(input.Tags);
        c.Suite = NormalizeSuite(input.Suite);
        c.LinkedIssues = NormalizeIssues(input.LinkedIssues, projectKey);
        c.Requirements = Clean(input.Requirements, 500, "Requirements");
        c.AutomatedName = Clean(input.AutomatedName, 400, "The automated name");
    }

    public async Task<TestCase> CreateCaseAsync(int projectId, TestCaseInput input)
    {
        await access.RequireAsync(projectId, Permission.RunTests);
        var actor = await access.ResolveUserIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var key = await ProjectKeyAsync(db, projectId);
        var c = new TestCase { ProjectId = projectId, CreatedById = actor, UpdatedById = actor };
        Apply(c, input, key);
        await SaveNewCaseAsync(db, c);
        await audit.RecordAsync("test_case.created", "test_case", c.Id, projectId, new { number = c.Number, title = c.Title }, actor);
        return c;
    }

    /// <summary>Adds <paramref name="c"/> with the next number (retrying when two writers pick the same one).</summary>
    private static async Task SaveNewCaseAsync(AppDbContext db, TestCase c)
    {
        for (var attempt = 0; ; attempt++)
        {
            c.Number = (await db.TestCases.Where(x => x.ProjectId == c.ProjectId).MaxAsync(x => (int?)x.Number) ?? 0) + 1;
            db.TestCases.Add(c);
            try { await db.SaveChangesAsync(); return; }
            catch (DbUpdateException) when (attempt < 4) { db.Entry(c).State = EntityState.Detached; }
        }
    }

    public async Task<TestCase> UpdateCaseAsync(int projectId, int number, TestCaseInput input)
    {
        await access.RequireAsync(projectId, Permission.RunTests);
        var actor = await access.ResolveUserIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var key = await ProjectKeyAsync(db, projectId);
        var c = await db.TestCases.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Number == number) ?? throw new InvalidOperationException("Test case not found.");
        var before = new TestCaseRevision
        {
            TestCaseId = c.Id, Version = c.Version, Title = c.Title, Preconditions = c.Preconditions, Steps = c.Steps, Expected = c.Expected,
            Priority = c.Priority, Tags = c.Tags, Suite = c.Suite, EditedById = c.UpdatedById, EditedAt = c.UpdatedAt,
        };
        var oldText = (c.Title, c.Preconditions, c.Steps, c.Expected, c.Priority, c.Tags, c.Suite);
        Apply(c, input, key);
        var newText = (c.Title, c.Preconditions, c.Steps, c.Expected, c.Priority, c.Tags, c.Suite);
        if (!oldText.Equals(newText))
        {
            db.TestCaseRevisions.Add(before);
            c.Version++;
        }
        c.UpdatedAt = DateTime.UtcNow;
        c.UpdatedById = actor;
        await db.SaveChangesAsync();
        return c;
    }

    public async Task SetArchivedAsync(int projectId, int number, bool archived)
    {
        await access.RequireAsync(projectId, Permission.ManageTests);
        await using var db = await dbFactory.CreateDbContextAsync();
        var c = await db.TestCases.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Number == number) ?? throw new InvalidOperationException("Test case not found.");
        c.IsArchived = archived;
        await db.SaveChangesAsync();
        await audit.RecordAsync(archived ? "test_case.archived" : "test_case.restored", "test_case", c.Id, projectId, new { number });
    }

    public async Task DeleteCaseAsync(int projectId, int number)
    {
        await access.RequireAsync(projectId, Permission.ManageTests);
        await using var db = await dbFactory.CreateDbContextAsync();
        var c = await db.TestCases.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Number == number) ?? throw new InvalidOperationException("Test case not found.");
        var stored = await db.TestEvidence.Where(e => e.Result.TestCaseId == c.Id).Select(e => e.StoredName).ToListAsync();
        db.TestCases.Remove(c);
        await db.SaveChangesAsync();
        files.Delete(stored);
        await audit.RecordAsync("test_case.deleted", "test_case", c.Id, projectId, new { number, c.Title });
    }

    // ---------------------------------------------------------------- plans

    public async Task<List<(TestPlan Plan, int CaseCount)>> ListPlansAsync(int projectId)
    {
        await access.RequireAsync(projectId, Permission.ViewProject);
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.TestPlans.AsNoTracking().Where(p => p.ProjectId == projectId).OrderBy(p => p.Name)
            .Select(p => new { Plan = p, Count = p.Cases.Count }).ToListAsync();
        return rows.Select(r => (r.Plan, r.Count)).ToList();
    }

    public async Task<TestPlan?> GetPlanAsync(int projectId, int id)
    {
        await access.RequireAsync(projectId, Permission.ViewProject);
        await using var db = await dbFactory.CreateDbContextAsync();
        var p = await db.TestPlans.AsNoTracking().Include(x => x.Cases).ThenInclude(x => x.TestCase).FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Id == id);
        p?.Cases.Sort((a, b) => a.Order.CompareTo(b.Order));
        return p;
    }

    public async Task<TestPlan> SavePlanAsync(int projectId, int? id, string name, string? description, IEnumerable<int> caseNumbers)
    {
        await access.RequireAsync(projectId, Permission.ManageTests);
        var actor = await access.ResolveUserIdAsync();
        name = Clean(name, 120, "The name") ?? throw new InvalidOperationException("The name is required.");
        description = Clean(description, 5000, "The description");
        await using var db = await dbFactory.CreateDbContextAsync();
        TestPlan plan;
        if (id is { } pid)
            plan = await db.TestPlans.Include(p => p.Cases).FirstOrDefaultAsync(p => p.ProjectId == projectId && p.Id == pid) ?? throw new InvalidOperationException("Plan not found.");
        else
        {
            plan = new TestPlan { ProjectId = projectId, CreatedById = actor };
            db.TestPlans.Add(plan);
        }
        if (await db.TestPlans.AnyAsync(p => p.ProjectId == projectId && p.Name == name && p.Id != plan.Id)) throw new InvalidOperationException($"A plan named '{name}' already exists.");
        plan.Name = name;
        plan.Description = description;
        plan.UpdatedAt = DateTime.UtcNow;
        var numbers = caseNumbers.Distinct().ToList();
        var cases = await db.TestCases.Where(c => c.ProjectId == projectId && numbers.Contains(c.Number)).ToDictionaryAsync(c => c.Number);
        plan.Cases.Clear();
        var order = 0;
        foreach (var n in numbers)
            if (cases.TryGetValue(n, out var c)) plan.Cases.Add(new TestPlanCase { TestCaseId = c.Id, Order = order++ });
        await db.SaveChangesAsync();
        await audit.RecordAsync(id == null ? "test_plan.created" : "test_plan.updated", "test_plan", plan.Id, projectId, new { plan.Name, cases = plan.Cases.Count }, actor);
        return plan;
    }

    public async Task DeletePlanAsync(int projectId, int id)
    {
        await access.RequireAsync(projectId, Permission.ManageTests);
        await using var db = await dbFactory.CreateDbContextAsync();
        var plan = await db.TestPlans.FirstOrDefaultAsync(p => p.ProjectId == projectId && p.Id == id) ?? throw new InvalidOperationException("Plan not found.");
        db.TestPlans.Remove(plan);
        await db.SaveChangesAsync();
        await audit.RecordAsync("test_plan.deleted", "test_plan", id, projectId, new { plan.Name });
    }
}
