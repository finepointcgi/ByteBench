using System.Text.RegularExpressions;
using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Tests;

/// <summary>An automated report to store: from the API, the UI, or a CI job (<see cref="PipelineId"/>).</summary>
public sealed class TestIngestInput
{
    /// <summary>junit, trx, xunit, ctrf, or null/"auto" to detect it.</summary>
    public string? Format { get; set; }
    public string Content { get; set; } = "";
    /// <summary>Add to this existing run instead of creating one.</summary>
    public int? RunNumber { get; set; }
    public string? Name { get; set; }
    public string? Build { get; set; }
    public string? Tag { get; set; }
    public string? Ref { get; set; }
    public string? CommitSha { get; set; }
    public string? Repository { get; set; }
    public int? PipelineId { get; set; }
    /// <summary>Create an automated test case for tests that match no existing case.</summary>
    public bool AutoCreateCases { get; set; } = true;
    public TestRunSource Source { get; set; } = TestRunSource.Api;
}

public sealed record TestIngestResult(int RunNumber, string Format, int Total, int Passed, int Failed, int Skipped, int Created, int Unmatched, bool Truncated);

public partial class TestService
{
    public const int MaxAutoCreatedCases = 5000;
    private static readonly Regex CaseToken = new(@"\bTC-(\d{1,9})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static int Rank(TestResultStatus s) => s switch
    {
        TestResultStatus.Failed => 4, TestResultStatus.Blocked => 3, TestResultStatus.Passed => 2, TestResultStatus.Skipped => 1, _ => 0,
    };

    /// <summary>
    /// Parses a JUnit / TRX / xUnit / CTRF report and records it as results of a run, mapping every test to a case: by a
    /// <c>TC-n</c> token in its name, else by the case's <see cref="TestCase.AutomatedName"/> (full name, then the plain name when
    /// only one case has it). A run for the same pipeline is reused, so the jobs of one pipeline add up. The run is closed when
    /// done. Needs RunTests (CI runs as the system).
    /// </summary>
    public async Task<TestIngestResult> IngestAsync(int projectId, TestIngestInput input)
    {
        await access.RequireAsync(projectId, Permission.RunTests);
        var actor = await access.ResolveUserIdAsync();
        ParsedTestReport report;
        try { report = TestReportParser.Parse(input.Content ?? "", input.Format); }
        catch (InvalidDataException ex) { throw new InvalidOperationException(ex.Message); }

        await using var db = await dbFactory.CreateDbContextAsync();
        TestRun? run = null;
        if (input.RunNumber is { } rn)
            run = await db.TestRuns.FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Number == rn) ?? throw new InvalidOperationException("Test run not found.");
        else if (input.PipelineId is { } pid)
            run = await db.TestRuns.FirstOrDefaultAsync(r => r.ProjectId == projectId && r.PipelineId == pid && r.Source != TestRunSource.Manual);
        if (run == null)
        {
            var created = await CreateRunAsync(projectId, new TestRunInput
            {
                Name = input.Name ?? "", Build = input.Build, Tag = input.Tag, Ref = input.Ref, CommitSha = input.CommitSha, Repository = input.Repository,
                PipelineId = input.PipelineId, CaseNumbers = [-1], // no manual rows: only the reported cases
            }, input.Source == TestRunSource.Manual ? TestRunSource.Api : input.Source);
            run = await db.TestRuns.FirstAsync(r => r.Id == created.Id);
            if (string.IsNullOrWhiteSpace(input.Name) && run.PipelineId != null) { run.Name = $"CI pipeline #{run.PipelineId}"; }
        }

        var cases = await db.TestCases.Where(c => c.ProjectId == projectId && !c.IsArchived).ToListAsync();
        var byNumber = cases.ToDictionary(c => c.Number);
        var byFull = new Dictionary<string, TestCase>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, List<TestCase>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cases.Where(c => !string.IsNullOrEmpty(c.AutomatedName)))
        {
            byFull.TryAdd(c.AutomatedName!, c);
            (byName.TryGetValue(c.AutomatedName!, out var l) ? l : byName[c.AutomatedName!] = []).Add(c);
        }
        var existing = await db.TestResults.Where(r => r.RunId == run.Id).ToDictionaryAsync(r => r.TestCaseId);

        // Collapse duplicates of the same case within one report: the worst outcome wins.
        var mapped = new Dictionary<int, (TestCase Case, ParsedTest Test)>();
        var newCases = new Dictionary<string, (TestCase Case, ParsedTest Test)>(StringComparer.OrdinalIgnoreCase);
        var unmatched = 0;
        var maxCreate = Math.Min(MaxAutoCreatedCases, config.GetValue("Tests:MaxAutoCreatedCases", MaxAutoCreatedCases));
        foreach (var t in report.Tests)
        {
            TestCase? c = null;
            if (CaseToken.Match(t.Name) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var num) && byNumber.TryGetValue(num, out var tc)) c = tc;
            c ??= byFull.GetValueOrDefault(t.FullName) ?? (byName.TryGetValue(t.Name, out var same) && same.Count == 1 ? same[0] : null);
            if (c == null)
            {
                if (newCases.TryGetValue(t.FullName, out var already)) { Merge(newCases, t.FullName, already.Case, t); continue; }
                if (!input.AutoCreateCases || newCases.Count >= maxCreate) { unmatched++; continue; }
                c = new TestCase
                {
                    ProjectId = projectId, Title = t.Name.Length > 200 ? t.Name[..200] : t.Name, Type = TestCaseType.Automated, AutomatedName = t.FullName,
                    Suite = NormalizeSuite((t.ClassName ?? t.Suite)?.Replace('.', '/')), CreatedById = actor, UpdatedById = actor,
                };
                newCases[t.FullName] = (c, t);
                continue;
            }
            if (mapped.TryGetValue(c.Id, out var prev)) { if (Rank(t.Status) > Rank(prev.Test.Status)) mapped[c.Id] = (c, t); }
            else mapped[c.Id] = (c, t);
        }

        // Allocate numbers for the new cases in one go.
        if (newCases.Count > 0)
        {
            var next = (await db.TestCases.Where(x => x.ProjectId == projectId).MaxAsync(x => (int?)x.Number) ?? 0) + 1;
            foreach (var (c, _) in newCases.Values) { c.Number = next++; db.TestCases.Add(c); }
            await db.SaveChangesAsync();
        }

        var now = DateTime.UtcNow;
        foreach (var (c, t) in mapped.Values.Concat(newCases.Values))
        {
            if (!existing.TryGetValue(c.Id, out var r))
            {
                r = new TestResult { RunId = run.Id, TestCaseId = c.Id };
                db.TestResults.Add(r);
                existing[c.Id] = r;
            }
            r.Status = t.Status;
            r.CaseVersion = c.Version;
            r.ExecutedAt = now;
            r.ExecutedById = actor;
            r.DurationMs = t.DurationMs;
            r.ErrorMessage = t.Message;
        }
        run.IsClosed = true;
        run.ClosedAt = now;
        await db.SaveChangesAsync();

        var all = mapped.Values.Concat(newCases.Values).Select(x => x.Test.Status).ToList();
        await audit.RecordAsync("test_run.ingested", "test_run", run.Id, projectId,
            new { run.Number, format = report.Format, tests = report.Tests.Count, created = newCases.Count, unmatched, pipeline = run.PipelineId }, actor);
        await RaiseCompletedAsync(db, run, actor);
        return new TestIngestResult(run.Number, report.Format, report.Tests.Count, all.Count(s => s == TestResultStatus.Passed),
            all.Count(s => s == TestResultStatus.Failed), all.Count(s => s == TestResultStatus.Skipped), newCases.Count, unmatched, report.Truncated);

        static void Merge(Dictionary<string, (TestCase, ParsedTest)> d, string k, TestCase c, ParsedTest t)
        {
            if (Rank(t.Status) > Rank(d[k].Item2.Status)) d[k] = (c, t);
        }
    }
}
