using System.Text.Json;
using System.Text.RegularExpressions;
using ByteBench.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.TrackerImport;

/// <summary>What the wizard submits. The token is used for this import only (held in memory) and never stored.</summary>
public sealed class TrackerImportInput
{
    public TrackerSourceKind Source { get; set; }
    /// <summary>The source's address: blank for github.com and gitlab.com; a self-hosted GitLab or GitHub Enterprise address (GitHub: the API root, e.g. https://ghe.example.com/api/v3); Jira's address.</summary>
    public string? BaseUrl { get; set; }
    /// <summary><c>owner/repo</c> (GitHub), the project path or numeric id (GitLab), the project key (Jira).</summary>
    public string Project { get; set; } = "";
    /// <summary>Jira Cloud: the account email (with an API token as the secret). Blank for tokens that stand alone.</summary>
    public string? Username { get; set; }
    public string? Secret { get; set; }
    public TrackerImportSettings Settings { get; set; } = new();
}

/// <summary>Step 1's answer: what the source has, the statuses to map, and the project's own workflow to map them to.</summary>
public sealed record TrackerImportCheck(SourceProbe Probe, IReadOnlyList<SourceStatus> SourceStatuses, int LabelCount, int MilestoneCount);

/// <summary>
/// The tracker importers' backend (scoped): check a connection, start an import, watch it, cancel it. Everything needs Maintainer on the
/// project and is audited (<c>project.tracker_import_requested</c>, then <c>project.tracker_imported</c> from the job). Credentials are
/// checked for shape, held in memory for the run, and never stored or logged.
/// </summary>
public sealed class TrackerImportService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, Audit.AuditLog audit, TrackerImportRunner runner,
    ISourceReaderFactory readers, IOptionsMonitor<TrackerImportOptions> options)
{
    private static readonly Regex GitHubName = new(@"^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}$", RegexOptions.Compiled);
    private static readonly Regex GitLabPath = new(@"^([A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+){0,10}|[0-9]{1,12})$", RegexOptions.Compiled);
    private static readonly Regex JiraKey = new(@"^[A-Za-z][A-Za-z0-9_]{1,19}$", RegexOptions.Compiled);

    public TrackerImportOptions Limits => options.CurrentValue;

    /// <summary>Wizard step 1: validates the input and asks the source for the project, statuses, labels and milestones.</summary>
    public async Task<TrackerImportCheck> CheckAsync(int projectId, TrackerImportInput input, int? actorId = null, CancellationToken ct = default)
    {
        await access.RequireRoleAsync(projectId, ProjectRole.Maintainer, "import issues", actorId);
        var connection = Validate(input);
        try
        {
            using var reader = readers.Create(connection);
            var probe = await reader.ProbeAsync(ct);
            var statuses = await reader.ReadStatusesAsync(ct);
            var labels = await reader.ReadLabelsAsync(ct);
            var milestones = await reader.ReadMilestonesAsync(ct);
            return new TrackerImportCheck(probe, statuses, labels.Count, milestones.Count);
        }
        catch (SourceException ex) { throw new InvalidOperationException(ex.Message); }
    }

    /// <summary>Queues the import and returns it (watch it with <see cref="GetAsync"/>).</summary>
    public async Task<TrackerImportJob> StartAsync(int projectId, TrackerImportInput input, int? actorId = null)
    {
        await access.RequireRoleAsync(projectId, ProjectRole.Maintainer, "import issues", actorId);
        var uid = await access.ResolveUserIdAsync(actorId) ?? throw new ForbiddenException("Sign in to import issues.");
        var connection = Validate(input);
        await using var db = await dbFactory.CreateDbContextAsync();
        var active = await db.TrackerImportJobs.CountAsync(i => i.ProjectId == projectId && (i.Status == TrackerImportStatus.Queued || i.Status == TrackerImportStatus.Running));
        if (active >= Limits.MaxActivePerProject)
            throw new InvalidOperationException("This project already has an import in progress. Wait for it to finish or cancel it.");
        var job = new TrackerImportJob
        {
            ProjectId = projectId, RequestedById = uid, Source = input.Source, SourceUrl = (connection.BaseUrl ?? "").Trim().TrimEnd('/'),
            SourceProject = connection.Project, SettingsJson = JsonSerializer.Serialize(input.Settings, TrackerImportRunner.Json),
        };
        db.TrackerImportJobs.Add(job);
        await db.SaveChangesAsync();
        await audit.RecordAsync("project.tracker_import_requested", "tracker_import", job.Id, projectId,
            new { source = input.Source.ToString(), project = job.SourceProject, address = job.SourceUrl, credentials = !string.IsNullOrEmpty(input.Secret) }, uid);
        runner.Enqueue(job.Id, connection);
        return job;
    }

    public async Task<TrackerImportJob?> GetAsync(int importId, int? actorId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var job = await db.TrackerImportJobs.AsNoTracking().FirstOrDefaultAsync(i => i.Id == importId);
        return job != null && await access.GetRoleAsync(job.ProjectId, actorId) is { } role && role >= ProjectRole.Maintainer ? job : null;
    }

    public static IReadOnlyList<string> WarningsOf(TrackerImportJob job)
    {
        if (job.WarningsJson == null) return [];
        try { return JsonSerializer.Deserialize<List<string>>(job.WarningsJson) ?? []; } catch (JsonException) { return []; }
    }

    public async Task<List<TrackerImportJob>> ListAsync(int projectId, int take = 10, int? actorId = null)
    {
        await access.RequireRoleAsync(projectId, ProjectRole.Maintainer, "see imports", actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.TrackerImportJobs.AsNoTracking().Where(i => i.ProjectId == projectId).OrderByDescending(i => i.Id).Take(take).ToListAsync();
    }

    public async Task<bool> CancelAsync(int importId, int? actorId = null)
    {
        var job = await GetAsync(importId, actorId) ?? throw new InvalidOperationException("Import not found.");
        var cancelled = await runner.CancelAsync(importId);
        if (cancelled) await audit.RecordAsync("project.tracker_import_cancel_requested", "tracker_import", importId, job.ProjectId, new { source = job.Source.ToString() }, actorId);
        return cancelled;
    }

    /// <summary>The project's workflow, for the status-mapping step.</summary>
    public async Task<IReadOnlyList<WorkflowStatus>> WorkflowAsync(int projectId, int? actorId = null)
    {
        await access.RequireRoleAsync(projectId, ProjectRole.Maintainer, "import issues", actorId);
        return (await WorkflowService.LoadAsync(dbFactory, projectId)).Statuses.OrderBy(s => s.Order).ToList();
    }

    private SourceConnection Validate(TrackerImportInput input)
    {
        var project = (input.Project ?? "").Trim();
        var ok = input.Source switch
        {
            TrackerSourceKind.GitHub => GitHubName.IsMatch(project),
            TrackerSourceKind.GitLab => GitLabPath.IsMatch(project),
            TrackerSourceKind.Jira => JiraKey.IsMatch(project),
            _ => false,
        };
        if (!ok)
            throw new InvalidOperationException(input.Source switch
            {
                TrackerSourceKind.GitHub => "Enter the repository as owner/name.",
                TrackerSourceKind.GitLab => "Enter the project's path (group/project) or its numeric id.",
                _ => "Enter the Jira project key (for example PROJ).",
            });
        var baseUrl = string.IsNullOrWhiteSpace(input.BaseUrl) ? null : input.BaseUrl.Trim().TrimEnd('/');
        if (readers.ValidateAddress(input.Source, baseUrl) is { } bad) throw new InvalidOperationException(bad);
        var user = string.IsNullOrWhiteSpace(input.Username) ? null : input.Username.Trim();
        var secret = string.IsNullOrWhiteSpace(input.Secret) ? null : input.Secret.Trim();
        if (user?.Length > 200 || user?.Any(char.IsControl) == true) throw new InvalidOperationException("The username isn't valid.");
        if (secret?.Length > 2000 || secret?.Any(char.IsControl) == true) throw new InvalidOperationException("The token isn't valid.");
        input.Settings ??= new();
        return new SourceConnection(input.Source, baseUrl, project, user, secret);
    }
}
