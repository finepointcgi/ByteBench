using System.Text;
using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Services.Attachments;
using ByteBench.Services.Git;
using ByteBench.Services.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Tests;

/// <summary>What starting a run takes. <see cref="Tag"/> must exist in a repository of the project; <see cref="CommitSha"/> is then the tag's commit.</summary>
public sealed class TestRunInput
{
    public string Name { get; set; } = "";
    public int? PlanId { get; set; }
    /// <summary>A label for the matrix (a version, build number, ...). Defaults to the tag.</summary>
    public string? Build { get; set; }
    public string? Tag { get; set; }
    public string? Ref { get; set; }
    public string? CommitSha { get; set; }
    /// <summary>Repository slug the tag lives in (needed only when several repositories have the tag).</summary>
    public string? Repository { get; set; }
    public int? PipelineId { get; set; }
    /// <summary>Run exactly these cases (numbers) instead of the plan's or all active manual cases.</summary>
    public IReadOnlyList<int>? CaseNumbers { get; set; }
}

public sealed record TestRunDetail(TestRun Run, TestRunSummary Summary, List<TestResult> Results);

public partial class TestService
{
    public const int MaxCasesPerRun = 5000;
    private static readonly Regex Sha = new("^[0-9a-fA-F]{7,40}$", RegexOptions.Compiled);

    private static string RunUrl(string baseUrl, string key, int number) => $"{baseUrl}/p/{key}/tests/runs/{number}";

    /// <summary>Resolves <paramref name="tag"/> to (repository, commit sha, release id) within the project, or throws.</summary>
    private async Task<(Repository Repo, string Sha, int? ReleaseId)> ResolveTagAsync(AppDbContext db, int projectId, string tag, string? slug)
    {
        var all = await db.Repositories.AsNoTracking().Include(r => r.Project).Where(r => r.ProjectId == projectId && (slug == null || r.Slug == slug)).ToListAsync();
        if (all.Count == 0) throw new InvalidOperationException(slug == null ? "The project has no repository." : $"Repository '{slug}' not found.");
        foreach (var repo in all)
        {
            if (!GitOps.IsValidTagName(tag)) break;
            var sha = await repos.Git(repo, p => GitOps.Exists(p) && GitOps.TagExists(p, tag) ? GitOps.ResolveSha(p, "refs/tags/" + tag) : null);
            if (sha == null) continue;
            var release = await db.Releases.Where(r => r.RepositoryId == repo.Id && r.TagName == tag).Select(r => (int?)r.Id).FirstOrDefaultAsync();
            return (repo, sha, release);
        }
        throw new InvalidOperationException($"Tag '{tag}' does not exist" + (slug == null ? " in this project's repositories." : $" in repository '{slug}'."));
    }

    public async Task<TestRun> CreateRunAsync(int projectId, TestRunInput input, TestRunSource source = TestRunSource.Manual)
    {
        await access.RequireAsync(projectId, Permission.RunTests);
        var actor = await access.ResolveUserIdAsync();
        var name = Clean(input.Name, 200, "The name");
        await using var db = await dbFactory.CreateDbContextAsync();
        var run = new TestRun
        {
            ProjectId = projectId, CreatedById = actor, Source = source, Build = Clean(input.Build, 200, "The build"),
            Ref = Clean(input.Ref, 250, "The ref"), PlanId = input.PlanId,
        };
        if (!string.IsNullOrWhiteSpace(input.Tag))
        {
            var tag = input.Tag.Trim();
            var (repo, sha, releaseId) = await ResolveTagAsync(db, projectId, tag, string.IsNullOrWhiteSpace(input.Repository) ? null : input.Repository.Trim());
            run.Tag = tag; run.RepositoryId = repo.Id; run.CommitSha = sha; run.ReleaseId = releaseId;
        }
        else if (!string.IsNullOrWhiteSpace(input.CommitSha))
        {
            if (!Sha.IsMatch(input.CommitSha.Trim())) throw new InvalidOperationException("The commit must be a hex SHA (7 to 40 characters).");
            run.CommitSha = input.CommitSha.Trim().ToLowerInvariant();
        }
        if (input.PipelineId is { } pipelineId)
        {
            var p = await db.Pipelines.AsNoTracking().Where(x => x.Id == pipelineId && x.Repository.ProjectId == projectId)
                .Select(x => new { x.Id, x.RepositoryId, x.CommitSha, x.Ref }).FirstOrDefaultAsync() ?? throw new InvalidOperationException("Pipeline not found in this project.");
            run.PipelineId = p.Id;
            run.RepositoryId ??= p.RepositoryId;
            run.CommitSha ??= p.CommitSha;
            run.Ref ??= p.Ref;
        }
        if (input.PlanId is { } planId && !await db.TestPlans.AnyAsync(p => p.Id == planId && p.ProjectId == projectId)) throw new InvalidOperationException("Plan not found.");

        List<TestCase> cases;
        if (input.CaseNumbers is { Count: > 0 } nums)
            cases = await db.TestCases.Where(c => c.ProjectId == projectId && !c.IsArchived && nums.Contains(c.Number)).OrderBy(c => c.Number).ToListAsync();
        else if (input.PlanId is { } pl)
            cases = await db.TestPlanCases.Where(x => x.PlanId == pl && !x.TestCase.IsArchived).OrderBy(x => x.Order).Select(x => x.TestCase).ToListAsync();
        else
            cases = await db.TestCases.Where(c => c.ProjectId == projectId && !c.IsArchived && c.Type == TestCaseType.Manual).OrderBy(c => c.Number).ToListAsync();
        if (cases.Count > MaxCasesPerRun) throw new InvalidOperationException($"A run can have at most {MaxCasesPerRun} cases; use a plan or a selection.");
        if (cases.Count == 0 && source == TestRunSource.Manual) throw new InvalidOperationException("There are no test cases to run. Write some first, or choose a plan that has cases.");
        foreach (var c in cases) run.Results.Add(new TestResult { TestCaseId = c.Id, CaseVersion = c.Version });
        run.Name = name ?? (run.Tag != null ? $"Test run for {run.Tag}" : run.Build != null ? $"Test run for {run.Build}" : "Test run");

        await SaveNewRunAsync(db, run);
        await audit.RecordAsync("test_run.created", "test_run", run.Id, projectId, new { run.Number, run.Name, run.Tag, run.CommitSha, cases = cases.Count, source = source.ToString() }, actor);
        return run;
    }

    private static async Task SaveNewRunAsync(AppDbContext db, TestRun run)
    {
        for (var attempt = 0; ; attempt++)
        {
            run.Number = (await db.TestRuns.Where(x => x.ProjectId == run.ProjectId).MaxAsync(x => (int?)x.Number) ?? 0) + 1;
            db.TestRuns.Add(run);
            try { await db.SaveChangesAsync(); return; }
            catch (DbUpdateException) when (attempt < 4) { db.Entry(run).State = EntityState.Detached; foreach (var r in run.Results) db.Entry(r).State = EntityState.Detached; }
        }
    }

    public async Task<Dictionary<int, TestRunSummary>> SummariesAsync(AppDbContext db, IReadOnlyCollection<int> runIds)
    {
        var rows = await db.TestResults.Where(r => runIds.Contains(r.RunId)).GroupBy(r => new { r.RunId, r.Status })
            .Select(g => new { g.Key.RunId, g.Key.Status, N = g.Count() }).ToListAsync();
        return runIds.ToDictionary(id => id, id => TestRunSummary.Of(rows.Where(r => r.RunId == id).SelectMany(r => Enumerable.Repeat(r.Status, r.N))));
    }

    public async Task<List<TestRunRow>> ListRunsAsync(int projectId, int take = 100, string? tag = null, string? commitSha = null, int? releaseId = null)
    {
        await access.RequireAsync(projectId, Permission.ViewProject);
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.TestRuns.AsNoTracking().Where(r => r.ProjectId == projectId);
        if (!string.IsNullOrEmpty(tag)) q = q.Where(r => r.Tag == tag);
        if (!string.IsNullOrEmpty(commitSha)) q = q.Where(r => r.CommitSha == commitSha);
        if (releaseId != null) q = q.Where(r => r.ReleaseId == releaseId);
        var runs = await q.Include(r => r.Plan).OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync();
        var sums = await SummariesAsync(db, runs.Select(r => r.Id).ToList());
        return runs.Select(r => new TestRunRow(r, sums[r.Id])).ToList();
    }

    /// <summary>Runs that ran the given version: the commit, or the tag, or the release (any of them matching). Newest first.</summary>
    public async Task<List<TestRunRow>> RunsForVersionAsync(int projectId, string? commitSha, string? tag, int? releaseId, int take = 10)
    {
        await access.RequireAsync(projectId, Permission.ViewProject);
        await using var db = await dbFactory.CreateDbContextAsync();
        var sha = string.IsNullOrEmpty(commitSha) ? null : commitSha;
        var runs = await db.TestRuns.AsNoTracking().Where(r => r.ProjectId == projectId
                && ((sha != null && r.CommitSha == sha) || (tag != null && r.Tag == tag) || (releaseId != null && r.ReleaseId == releaseId)))
            .Include(r => r.Plan).OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).Take(take).ToListAsync();
        var sums = await SummariesAsync(db, runs.Select(r => r.Id).ToList());
        return runs.Select(r => new TestRunRow(r, sums[r.Id])).ToList();
    }

    public async Task<TestRunDetail?> GetRunAsync(int projectId, int number)
    {
        await access.RequireAsync(projectId, Permission.ViewProject);
        await using var db = await dbFactory.CreateDbContextAsync();
        var run = await db.TestRuns.AsNoTracking().Include(r => r.Plan).FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Number == number);
        if (run == null) return null;
        var results = await db.TestResults.AsNoTracking().Include(r => r.TestCase).Include(r => r.Evidence).Where(r => r.RunId == run.Id).ToListAsync();
        results = results.OrderBy(r => r.TestCase.Suite ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(r => r.TestCase.Number).ToList();
        return new TestRunDetail(run, TestRunSummary.Of(results.Select(r => r.Status)), results);
    }

    private async Task<(TestRun Run, TestResult Result, TestCase Case)> LoadResultAsync(AppDbContext db, int projectId, int runNumber, int caseNumber)
    {
        var run = await db.TestRuns.FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Number == runNumber) ?? throw new InvalidOperationException("Test run not found.");
        var c = await db.TestCases.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Number == caseNumber) ?? throw new InvalidOperationException("Test case not found.");
        var result = await db.TestResults.FirstOrDefaultAsync(r => r.RunId == run.Id && r.TestCaseId == c.Id)
                     ?? throw new InvalidOperationException($"TC-{caseNumber} is not part of run #{runNumber}.");
        return (run, result, c);
    }

    public async Task<TestResult> RecordResultAsync(int projectId, int runNumber, int caseNumber, TestResultStatus status, string? comment)
    {
        await access.RequireAsync(projectId, Permission.RunTests);
        if (!Enum.IsDefined(status)) throw new InvalidOperationException("Unknown result status.");
        comment = Clean(comment, 10000, "The comment");
        var actor = await access.ResolveUserIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var (run, result, c) = await LoadResultAsync(db, projectId, runNumber, caseNumber);
        if (run.IsClosed) throw new InvalidOperationException("This run is closed. Reopen it to record results.");
        result.Status = status;
        result.Comment = comment;
        result.CaseVersion = c.Version;
        if (status == TestResultStatus.NotRun) { result.ExecutedAt = null; result.ExecutedById = null; }
        else { result.ExecutedAt = DateTime.UtcNow; result.ExecutedById = actor; }
        await db.SaveChangesAsync();
        await audit.RecordAsync("test_result.recorded", "test_run", run.Id, projectId, new { run = run.Number, @case = c.Number, status = status.ToString() }, actor);
        return result;
    }

    public async Task SetRunClosedAsync(int projectId, int number, bool closed)
    {
        await access.RequireAsync(projectId, Permission.RunTests);
        var actor = await access.ResolveUserIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var run = await db.TestRuns.FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Number == number) ?? throw new InvalidOperationException("Test run not found.");
        if (run.IsClosed == closed) return;
        run.IsClosed = closed;
        run.ClosedAt = closed ? DateTime.UtcNow : null;
        await db.SaveChangesAsync();
        await audit.RecordAsync(closed ? "test_run.closed" : "test_run.reopened", "test_run", run.Id, projectId, new { run.Number, run.Name }, actor);
        if (closed) await RaiseCompletedAsync(db, run, actor);
    }

    internal async Task RaiseCompletedAsync(AppDbContext db, TestRun run, int? actor)
    {
        var sum = (await SummariesAsync(db, [run.Id]))[run.Id];
        var key = await ProjectKeyAsync(db, run.ProjectId);
        webhooks.Enqueue(run.ProjectId, WebhookEventNames.TestRunCompleted, new
        {
            run = new
            {
                run.Number, run.Name, build = run.Label, run.Tag, run.CommitSha, run.Ref, source = run.Source.ToString().ToLowerInvariant(),
                passed = sum.Passed, failed = sum.Failed, blocked = sum.Blocked, skipped = sum.Skipped, notRun = sum.NotRun, passRate = sum.PassRate,
                webUrl = RunUrl(baseUrl.Value, key, run.Number),
            },
        }, actor);

        // In-app notification (and the user's "Pipelines" preference decides email/in-app) for whoever started the run.
        // A person closing their own manual run doesn't need telling; automated (ingested) runs always notify the starter.
        if (run.CreatedById is { } creator && !(run.Source == TestRunSource.Manual && creator == actor))
        {
            var text = sum.Failed + sum.Blocked > 0
                ? $"{sum.Failed} failed, {sum.Blocked} blocked, {sum.Passed} passed of {sum.Total}"
                : $"All clear: {sum.Passed} passed, {sum.Skipped} skipped, {sum.NotRun} not run";
            try { await notifications.OnTestRunCompletedAsync(run.ProjectId, creator, $"{run.Name} (run #{run.Number})", $"/p/{key}/tests/runs/{run.Number}", text); }
            catch (Exception) { /* a failed notification never fails closing the run */ }
        }
    }

    public async Task DeleteRunAsync(int projectId, int number)
    {
        await access.RequireAsync(projectId, Permission.ManageTests);
        await using var db = await dbFactory.CreateDbContextAsync();
        var run = await db.TestRuns.FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Number == number) ?? throw new InvalidOperationException("Test run not found.");
        var stored = await db.TestEvidence.Where(e => e.Result.RunId == run.Id).Select(e => e.StoredName).ToListAsync();
        db.TestRuns.Remove(run);
        await db.SaveChangesAsync();
        files.Delete(stored);
        await audit.RecordAsync("test_run.deleted", "test_run", run.Id, projectId, new { run.Number, run.Name });
    }

    // ---------------------------------------------------------------- evidence

    public async Task<TestEvidence> AddEvidenceAsync(int projectId, int runNumber, int caseNumber, string? fileName, Stream content, CancellationToken ct = default)
    {
        await access.RequireAsync(projectId, Permission.RunTests);
        var actor = await access.ResolveUserIdAsync();
        var name = AttachmentStorage.SanitizeFileName(fileName);
        int resultId;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var (run, result, _) = await LoadResultAsync(db, projectId, runNumber, caseNumber);
            if (run.IsClosed) throw new InvalidOperationException("This run is closed. Reopen it to add evidence.");
            if (await db.TestEvidence.CountAsync(e => e.ResultId == result.Id, ct) >= MaxEvidencePerResult)
                throw new InvalidOperationException($"A result can have at most {MaxEvidencePerResult} evidence files.");
            resultId = result.Id;
        }
        var stored = await files.SaveAsync(content, name, ct, MaxEvidenceBytes);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            var e = new TestEvidence
            {
                ResultId = resultId, StoredName = stored.StoredName, FileName = name, ContentType = stored.ContentType, Size = stored.Size,
                Sha256 = stored.Sha256, UploaderId = actor,
            };
            db.TestEvidence.Add(e);
            await db.SaveChangesAsync(CancellationToken.None);
            return e;
        }
        catch
        {
            files.Delete([stored.StoredName]);
            throw;
        }
    }

    public async Task DeleteEvidenceAsync(int projectId, int evidenceId)
    {
        await access.RequireAsync(projectId, Permission.RunTests);
        await using var db = await dbFactory.CreateDbContextAsync();
        var e = await db.TestEvidence.FirstOrDefaultAsync(x => x.Id == evidenceId && x.Result.Run.ProjectId == projectId) ?? throw new InvalidOperationException("Evidence not found.");
        db.TestEvidence.Remove(e);
        await db.SaveChangesAsync();
        files.Delete([e.StoredName]);
    }

    public async Task<(TestEvidence Evidence, string Path)?> OpenEvidenceAsync(int projectId, int evidenceId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        var e = await db.TestEvidence.AsNoTracking().FirstOrDefaultAsync(x => x.Id == evidenceId && x.Result.Run.ProjectId == projectId);
        return e == null ? null : (e, files.PathFor(e.StoredName));
    }

    // ---------------------------------------------------------------- bug from a failure

    /// <summary>
    /// Files a bug for a failed (or blocked) result with the case, run, build and failure details filled in, and links it to the result.
    /// Needs EditIssues (checked by the issue service). Returns the existing bug when the result already has one.
    /// </summary>
    public async Task<int> CreateBugAsync(int projectId, int runNumber, int caseNumber)
    {
        await access.RequireAsync(projectId, Permission.RunTests);
        var actor = await access.ResolveUserIdAsync() ?? throw new InvalidOperationException("Sign in to file a bug.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var (run, result, c) = await LoadResultAsync(db, projectId, runNumber, caseNumber);
        if (result.IssueNumber is { } existing) return existing;
        if (result.Status is not (TestResultStatus.Failed or TestResultStatus.Blocked)) throw new InvalidOperationException("Only a failed or blocked result can get a bug.");
        var key = await ProjectKeyAsync(db, projectId);
        var url = baseUrl.Value;
        var sb = new StringBuilder();
        sb.AppendLine($"Found by test case [{c.Key}]({url}/p/{key}/tests/cases/{c.Number}) in [{run.Name} (run #{run.Number})]({RunUrl(url, key, run.Number)}).").AppendLine();
        sb.AppendLine($"- Build: {run.Label}");
        if (run.Tag != null) sb.AppendLine($"- Tag: {run.Tag}");
        if (run.CommitSha != null) sb.AppendLine($"- Commit: `{run.CommitSha[..Math.Min(12, run.CommitSha.Length)]}`");
        if (run.PipelineId != null) sb.AppendLine($"- Pipeline: #{run.PipelineId}");
        sb.AppendLine($"- Result: {result.Status}").AppendLine();
        if (!string.IsNullOrWhiteSpace(c.Preconditions)) sb.AppendLine("**Preconditions**").AppendLine().AppendLine(c.Preconditions).AppendLine();
        if (!string.IsNullOrWhiteSpace(c.Steps)) sb.AppendLine("**Steps**").AppendLine().AppendLine(c.Steps).AppendLine();
        if (!string.IsNullOrWhiteSpace(c.Expected)) sb.AppendLine("**Expected result**").AppendLine().AppendLine(c.Expected).AppendLine();
        if (!string.IsNullOrWhiteSpace(result.Comment)) sb.AppendLine("**Tester's comment**").AppendLine().AppendLine(result.Comment).AppendLine();
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage)) sb.AppendLine("**Failure**").AppendLine().AppendLine("```").AppendLine(result.ErrorMessage).AppendLine("```");
        var title = $"{c.Key} failed: {c.Title}";
        var issue = await issues.CreateIssueAsync(projectId, new Issue
        {
            Title = title.Length > 200 ? title[..200] : title, Description = sb.ToString(), Type = IssueType.Bug,
            Priority = c.Priority switch { TestPriority.Critical => IssuePriority.Highest, TestPriority.High => IssuePriority.High, TestPriority.Low => IssuePriority.Low, _ => IssuePriority.Medium },
        }, [], actor);
        result.IssueNumber = issue.Number;
        // The bug joins the case's linked issues (kept within the column's limit), so the case shows the bugs it found.
        var linked = c.LinkedIssueNumbers.ToHashSet();
        if (linked.Add(issue.Number) && string.Join(",", linked.Order()).Length <= 300)
        {
            c.LinkedIssues = string.Join(",", linked.Order());
            c.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        await audit.RecordAsync("test_result.bug_created", "test_run", run.Id, projectId, new { run = run.Number, @case = c.Number, issue = issue.Number }, actor);
        return issue.Number;
    }
}
