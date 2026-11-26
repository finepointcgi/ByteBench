using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Tests;

public enum TestChange { Unchanged, Regressed, Fixed, StillFailing, NewlyRun, NoLongerRun }

public sealed record TestCompareRow(TestCase Case, TestResultStatus? A, TestResultStatus? B, TestChange Change);

public sealed record TestCompare(TestRun A, TestRun B, List<TestCompareRow> Rows)
{
    public int Count(TestChange c) => Rows.Count(r => r.Change == c);
}

/// <summary>Cases (rows) against runs (columns) with the result of each; <see cref="Cells"/> is keyed by (case id, run id).</summary>
public sealed record TestMatrix(List<TestRun> Runs, List<TestCase> Cases, Dictionary<(int CaseId, int RunId), TestResultStatus> Cells);

public sealed record TestCaseHistoryRow(TestRun Run, TestResult Result);

public partial class TestService
{
    private static bool Bad(TestResultStatus? s) => s is TestResultStatus.Failed or TestResultStatus.Blocked;

    /// <summary>Classifies how a case changed from the result in <paramref name="a"/> to the one in <paramref name="b"/> (null: no result).</summary>
    public static TestChange Classify(TestResultStatus? a, TestResultStatus? b)
    {
        if (a is null or TestResultStatus.NotRun) a = null;
        if (b is null or TestResultStatus.NotRun) b = null;
        if (a == null && b == null) return TestChange.Unchanged;
        if (a == null) return TestChange.NewlyRun;
        if (b == null) return TestChange.NoLongerRun;
        if (a == TestResultStatus.Passed && Bad(b)) return TestChange.Regressed;
        if (Bad(a) && b == TestResultStatus.Passed) return TestChange.Fixed;
        if (Bad(a) && Bad(b)) return TestChange.StillFailing;
        return TestChange.Unchanged;
    }

    /// <summary>Compares two runs of the project (A is the baseline, B the candidate): which cases regressed, which were fixed.</summary>
    public async Task<TestCompare?> CompareAsync(int projectId, int runA, int runB)
    {
        await access.RequireAsync(projectId, Permission.ViewProject);
        await using var db = await dbFactory.CreateDbContextAsync();
        var runs = await db.TestRuns.AsNoTracking().Where(r => r.ProjectId == projectId && (r.Number == runA || r.Number == runB)).ToListAsync();
        var a = runs.FirstOrDefault(r => r.Number == runA);
        var b = runs.FirstOrDefault(r => r.Number == runB);
        if (a == null || b == null) return null;
        var results = await db.TestResults.AsNoTracking().Include(r => r.TestCase).Where(r => r.RunId == a.Id || r.RunId == b.Id).ToListAsync();
        var rows = results.GroupBy(r => r.TestCaseId).Select(g =>
        {
            var ra = g.FirstOrDefault(x => x.RunId == a.Id)?.Status;
            var rb = g.FirstOrDefault(x => x.RunId == b.Id)?.Status;
            return new TestCompareRow(g.First().TestCase, ra, rb, Classify(ra, rb));
        }).OrderBy(r => r.Change == TestChange.Regressed ? 0 : r.Change == TestChange.StillFailing ? 1 : r.Change == TestChange.Fixed ? 2 : 3)
            .ThenBy(r => r.Case.Number).ToList();
        return new TestCompare(a, b, rows);
    }

    /// <summary>The last <paramref name="runCount"/> runs (or the given run numbers) and every case that has a result in them.</summary>
    public async Task<TestMatrix> MatrixAsync(int projectId, IReadOnlyList<int>? runNumbers = null, int runCount = 8, bool onlyProblems = false)
    {
        await access.RequireAsync(projectId, Permission.ViewProject);
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.TestRuns.AsNoTracking().Where(r => r.ProjectId == projectId);
        if (runNumbers is { Count: > 0 }) q = q.Where(r => runNumbers.Contains(r.Number));
        var runs = (await q.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).Take(Math.Clamp(runCount, 1, 30)).ToListAsync())
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToList();
        var ids = runs.Select(r => r.Id).ToList();
        var results = await db.TestResults.AsNoTracking().Where(r => ids.Contains(r.RunId) && r.Status != TestResultStatus.NotRun)
            .Select(r => new { r.RunId, r.TestCaseId, r.Status }).ToListAsync();
        var cells = results.ToDictionary(r => (r.TestCaseId, r.RunId), r => r.Status);
        var caseIds = results.Select(r => r.TestCaseId).Distinct().ToList();
        var cases = await db.TestCases.AsNoTracking().Where(c => caseIds.Contains(c.Id)).OrderBy(c => c.Suite).ThenBy(c => c.Number).ToListAsync();
        if (onlyProblems)
            cases = cases.Where(c => runs.Any(r => cells.TryGetValue((c.Id, r.Id), out var s) && Bad(s))).ToList();
        return new TestMatrix(runs, cases, cells);
    }

    /// <summary>Every recorded result of one case, newest run first.</summary>
    public async Task<List<TestCaseHistoryRow>> CaseHistoryAsync(int projectId, int caseNumber, int take = 30)
    {
        await access.RequireAsync(projectId, Permission.ViewProject);
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.TestResults.AsNoTracking().Include(r => r.Run)
            .Where(r => r.TestCase.ProjectId == projectId && r.TestCase.Number == caseNumber && r.Status != TestResultStatus.NotRun)
            .OrderByDescending(r => r.Run.CreatedAt).ThenByDescending(r => r.Id).Take(take).ToListAsync();
        return rows.Select(r => new TestCaseHistoryRow(r.Run, r)).ToList();
    }

    /// <summary>The newest recorded status of each case (any run), for the case list.</summary>
    public async Task<Dictionary<int, (TestResultStatus Status, int RunNumber, DateTime At)>> LatestStatusesAsync(int projectId)
    {
        await access.RequireAsync(projectId, Permission.ViewProject);
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.TestResults.AsNoTracking().Where(r => r.Run.ProjectId == projectId && r.Status != TestResultStatus.NotRun)
            .Select(r => new { r.TestCase.Number, r.Status, RunNumber = r.Run.Number, At = r.ExecutedAt ?? r.Run.CreatedAt, r.Id }).ToListAsync();
        return rows.GroupBy(r => r.Number).ToDictionary(g => g.Key, g =>
        {
            var last = g.OrderByDescending(x => x.At).ThenByDescending(x => x.Id).First();
            return (last.Status, last.RunNumber, last.At);
        });
    }
}
