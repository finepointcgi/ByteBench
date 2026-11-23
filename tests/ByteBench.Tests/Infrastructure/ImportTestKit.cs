using ByteBench.Data;
using ByteBench.Services.TrackerImport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests.Infrastructure;

/// <summary>The real reader factory and HTTP client; private networks allowed so the local fakes can be reached. The worker is off, imports run through the runner.</summary>
public sealed class SaasImportApp : TestApp
{
    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["TrackerImport:Enabled"] = "false";
        settings["TrackerImport:AllowPrivateNetworks"] = "true";
    }
}

/// <summary>Same, with the SSRF guard on (loopback refused).</summary>
public sealed class SaasImportStrictApp : TestApp
{
    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["TrackerImport:Enabled"] = "false";
        settings["TrackerImport:AllowPrivateNetworks"] = "false";
    }
}

/// <summary>Helpers shared by the Trello, Linear and Asana import tests.</summary>
public sealed class ImportTestKit(TestApp app)
{
    public async Task<(Project P, int Carol)> NewProjectAsync()
    {
        var p = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        return (p, app.UserId("carol"));
    }

    public async Task<TrackerImportJob> RunAsync(Project p, int actor, TrackerImportInput input)
    {
        await using var s = app.Scope(actor);
        var job = await s.Get<TrackerImportService>().StartAsync(p.Id, input, actor);
        await app.Services.GetRequiredService<TrackerImportRunner>().RunAsync(job.Id);
        await using var db = app.Db();
        return await db.TrackerImportJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id);
    }

    public async Task<string> AuditTextAsync(int projectId)
    {
        await using var db = app.Db();
        return string.Join("|", await db.AuditEvents.Where(a => a.ProjectId == projectId).Select(a => a.Details ?? "").ToListAsync());
    }

    /// <summary>Everything a secret could have leaked into: job row, warnings, settings and the project's audit events.</summary>
    public async Task<string> EverywhereAsync(Project p, TrackerImportJob job) =>
        string.Join("|", job.Error, job.WarningsJson, job.SettingsJson, job.SourceUrl, job.SourceProject, await AuditTextAsync(p.Id));

    public async Task<List<Issue>> IssuesAsync(int projectId)
    {
        await using var db = app.Db();
        return await db.Issues.AsNoTracking().Include(i => i.Labels).Include(i => i.Comments).Where(i => i.ProjectId == projectId).OrderBy(i => i.Number).ToListAsync();
    }
}
