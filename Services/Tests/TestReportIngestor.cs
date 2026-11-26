using ByteBench.Data;
using ByteBench.Services.Ci;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Tests;

/// <summary>
/// Reads the test reports a CI job published (<c>artifacts.reports</c> of type junit, trx, xunit or ctrf) from its workspace and
/// records them in the project's test run for the pipeline (scoped; runs as the system: the pipeline already passed the
/// repository's own access rules). A pattern such as <c>**/TestResults/*.xml</c> may match several files. Cases that match no
/// existing case are created as automated cases unless <c>Tests:AutoCreateCases</c> is false.
/// </summary>
public class TestReportIngestor(IDbContextFactory<AppDbContext> dbFactory, TestService tests, ProjectAccess access, IConfiguration config, ILogger<TestReportIngestor> log)
{
    public const int MaxFilesPerPattern = 50;

    public async Task<List<string>> IngestWorkspaceAsync(Pipeline p, PipelineJob job, string workspace, IReadOnlyDictionary<string, string> reports)
    {
        var notes = new List<string>();
        int projectId;
        await using (var db = await dbFactory.CreateDbContextAsync())
            projectId = await db.Pipelines.Where(x => x.Id == p.Id).Select(x => x.Repository.ProjectId).FirstOrDefaultAsync();
        if (projectId == 0) return notes;
        access.ActAsSystem();
        var auto = config.GetValue("Tests:AutoCreateCases", true);
        foreach (var (type, pattern) in reports.Where(r => TestReportParser.IsFormat(r.Key)))
        {
            var files = pattern.Contains('*') || pattern.Contains('?')
                ? CiArtifacts.FindFiles(workspace, [pattern]).Take(MaxFilesPerPattern).ToList()
                : [pattern];
            if (files.Count == 0) { notes.Add($"[bytebench] Test report '{pattern}' ({type}): no file matched."); continue; }
            foreach (var file in files)
            {
                var text = Security.SecurityIngestor.ReadReport(workspace, file, out var problem);
                if (text == null) { notes.Add($"[bytebench] Test report '{file}' ({type}): {problem}"); continue; }
                try
                {
                    var r = await tests.IngestAsync(projectId, new TestIngestInput
                    {
                        Format = type, Content = text, PipelineId = p.Id, AutoCreateCases = auto, Source = TestRunSource.Ci,
                        Name = $"CI pipeline #{p.Id}",
                    });
                    notes.Add($"[bytebench] Test report {file} ({type}): {r.Total} test(s), {r.Passed} passed, {r.Failed} failed, {r.Skipped} skipped"
                              + (r.Created > 0 ? $", {r.Created} new case(s)" : "") + (r.Unmatched > 0 ? $", {r.Unmatched} not mapped" : "")
                              + $" → run #{r.RunNumber}.");
                }
                catch (InvalidOperationException ex)
                {
                    log.LogInformation("Test report {File} of job {Job} was refused: {Message}", file, job.Id, ex.Message);
                    notes.Add($"[bytebench] Test report {file} ({type}) could not be read: {ex.Message}");
                }
            }
        }
        return notes;
    }
}
