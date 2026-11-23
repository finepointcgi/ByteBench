using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services.Audit;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Workbench;

/// <summary>What one probed Workbench API area allows the stored key. Null means "couldn't tell".</summary>
public sealed record WorkbenchProbe(string Area, string Label, string Purpose, bool? CanRead, bool? CanWrite, string? Note);

public sealed record WorkbenchTestResult(bool Ok, string Summary, DateTime At, IReadOnlyList<WorkbenchProbe> Probes);

/// <summary>The connection as the admin page shows it. The key is never included: only whether there is one and its last characters.</summary>
public sealed record WorkbenchConnectionInfo(string? EffectiveUrl, string? StoredUrl, string? ConfiguredUrl, bool UrlFromSettings, bool HasKey, string? KeyHint,
    DateTime? KeySetAt, string? KeySetBy, WorkbenchTestResult? LastTest, bool AllowPrivateNetworks);

/// <summary>
/// The site-admin side of the Workbench integration: the address (overriding <c>Workbench:BaseUrl</c>), the API key (encrypted with
/// Data Protection, never shown back, replaceable and removable), the connection test and the audit trail. Site administrators only.
/// </summary>
public sealed class WorkbenchConnectionService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, AuditLog audit, WorkbenchCredentials credentials,
    WorkbenchClient client, WorkbenchUrlGuard guard, WorkbenchDirectory directory, Microsoft.Extensions.Options.IOptionsMonitor<WorkbenchOptions> options)
{
    public const string AuditPrefix = "workbench.";

    private async Task RequireAdminAsync(string action)
    {
        if (!await access.IsAdminAsync()) throw new ForbiddenException($"Only site administrators can {action}.");
    }

    public async Task<WorkbenchConnectionInfo> GetAsync()
    {
        await RequireAdminAsync("see the Workbench connection");
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.WorkbenchConnections.AsNoTracking().FirstOrDefaultAsync();
        string? by = row?.KeySetById is { } uid ? await db.Users.Where(u => u.Id == uid).Select(u => u.UserName).FirstOrDefaultAsync() : null;
        return new WorkbenchConnectionInfo(credentials.BaseUrl, row?.BaseUrl, WorkbenchService.Normalize(options.CurrentValue.BaseUrl), credentials.UrlFromSettings,
            row?.ApiKeyProtected != null, row?.KeyHint, row?.KeySetAt, by, ParseTest(row), guard.AllowPrivateNetworks);
    }

    /// <summary>
    /// Saves the address and, when <paramref name="apiKey"/> is given, replaces the key. A blank address falls back to
    /// <c>Workbench:BaseUrl</c>; a blank key leaves the stored key as it is. Audited without any key material.
    /// </summary>
    public async Task SaveAsync(string? baseUrl, string? apiKey)
    {
        await RequireAdminAsync("change the Workbench connection");
        var url = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim().TrimEnd('/');
        if (url != null && guard.Validate(url) is { } invalid) throw new InvalidOperationException(invalid);
        if (url != null && WorkbenchService.Normalize(url) == null) throw new InvalidOperationException("Enter an absolute http:// or https:// address.");
        var key = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        if (key != null && (key.Length < 12 || key.Length > 300 || key.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))))
            throw new InvalidOperationException("That doesn't look like a Workbench API key (they start with wbk_ and have no spaces).");
        if (key != null && !key.StartsWith("wbk_", StringComparison.Ordinal))
            throw new InvalidOperationException("Workbench API keys start with wbk_. Create one under Admin > Integrations > API keys in Workbench.");

        var actor = await access.ResolveUserIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.WorkbenchConnections.FirstOrDefaultAsync();
        if (row == null) db.WorkbenchConnections.Add(row = new WorkbenchConnection());
        var urlChanged = !string.Equals(row.BaseUrl, url, StringComparison.Ordinal);
        row.BaseUrl = url;
        if (key != null)
        {
            row.ApiKeyProtected = credentials.Protect(key);
            row.KeyHint = key[^4..];
            (row.KeySetAt, row.KeySetById) = (DateTime.UtcNow, actor);
        }
        if (urlChanged || key != null) (row.LastTestAt, row.LastTestOk, row.LastTestJson) = (null, null, null);
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        credentials.Invalidate();
        directory.Clear();
        if (urlChanged || key != null)
            await audit.RecordAsync("workbench.connection_changed", "workbench", WorkbenchConnection.SingletonId, null,
                new { baseUrl = url, urlChanged, keyReplaced = key != null, keyHint = key == null ? null : row.KeyHint }, actor);
    }

    /// <summary>Forgets the stored key (the integration goes dark). It doesn't revoke the key in Workbench: do that under Admin > Integrations > API keys there.</summary>
    public async Task RemoveKeyAsync()
    {
        await RequireAdminAsync("change the Workbench connection");
        var actor = await access.ResolveUserIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.WorkbenchConnections.FirstOrDefaultAsync();
        if (row?.ApiKeyProtected == null) return;
        (row.ApiKeyProtected, row.KeyHint, row.KeySetAt, row.KeySetById) = (null, null, null, null);
        (row.LastTestAt, row.LastTestOk, row.LastTestJson) = (null, null, null);
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        credentials.Invalidate();
        directory.Clear();
        await audit.RecordAsync("workbench.key_removed", "workbench", WorkbenchConnection.SingletonId, null, new { }, actor);
    }

    /// <summary>
    /// Probes what the stored key can do, without changing anything: a read of each area ByteBench uses (projects, sheets), a
    /// scope-only check of the time area (which ByteBench must not have), the financials permission (planned vs charged hours),
    /// and a deliberately invalid status update against an id that doesn't exist to tell whether the key can write sheets.
    /// </summary>
    public async Task<WorkbenchTestResult> TestAsync(CancellationToken ct = default)
    {
        await RequireAdminAsync("test the Workbench connection");
        var actor = await access.ResolveUserIdAsync();
        var result = await ProbeAsync(ct);
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var row = await db.WorkbenchConnections.FirstOrDefaultAsync(ct);
            if (row == null) db.WorkbenchConnections.Add(row = new WorkbenchConnection());
            (row.LastTestAt, row.LastTestOk, row.LastTestJson) = (result.At, result.Ok, JsonSerializer.Serialize(result, WorkbenchClient.JsonOptions));
            await db.SaveChangesAsync(ct);
        }
        directory.Clear();
        await audit.RecordAsync("workbench.connection_tested", "workbench", WorkbenchConnection.SingletonId, null,
            new { ok = result.Ok, summary = result.Summary, areas = result.Probes.Select(p => new { p.Area, p.CanRead, p.CanWrite }) }, actor);
        return result;
    }

    private async Task<WorkbenchTestResult> ProbeAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (credentials.BaseUrl == null) return new(false, "No Workbench address is set.", now, []);
        if (guard.Validate(credentials.BaseUrl) is { } blocked) return new(false, blocked, now, []);
        if (credentials.ApiKey == null) return new(false, "No API key is stored.", now, []);

        var probes = new List<WorkbenchProbe>();
        var projects = await client.GetAsync("/projects", ct);
        if (projects.Status is WbStatus.Unreachable or WbStatus.Error)
            return new(false, projects.Message ?? "Couldn't reach Workbench.", now, []);
        if (projects.Status == WbStatus.NotFound)
            return new(false, "Workbench answered, but not like a Workbench API (is the address right?).", now, []);
        if (projects.Status == WbStatus.Forbidden && projects.HttpStatus == 401) return new(false, projects.Message!, now, []);

        string? firstProject = null;
        int count = 0;
        if (projects.Ok && projects.Value.ValueKind == JsonValueKind.Array)
        {
            count = projects.Value.GetArrayLength();
            firstProject = projects.Value.EnumerateArray().Select(p => p.TryGetProperty("id", out var i) ? i.GetString() : null).FirstOrDefault(i => i != null);
        }
        probes.Add(new("projects", "Projects and contracts", "Pickers and read-only context (projects, WBS elements)", projects.Ok, null,
            projects.Ok ? $"{count} project(s) visible to the key" : projects.Message));

        var sheets = await client.GetAsync("/sheets", ct);
        probes.Add(new("sheets", "Sheets, dashboards and tasks", "Task title, dates, status and % complete from the project's plan sheet", sheets.Ok, null,
            sheets.Ok ? null : sheets.Message));

        // Hours: needs the key's roles to read project financials (Finance, administrators or the project's manager).
        bool? hours = null;
        string? hoursNote = "Add a project to test.";
        if (firstProject != null)
        {
            var access2 = await client.GetAsync($"/projects/{firstProject}/financials/access", ct);
            hours = access2.Ok ? access2.Value.ValueKind == JsonValueKind.True : access2.Status == WbStatus.Forbidden ? false : null;
            hoursNote = hours == true ? null : "Planned vs charged hours stay hidden: the key's roles can't read project financials.";
        }
        probes.Add(new("financials", "Project financials (hours)", "Planned vs charged hours per WBS task (optional)", hours, null, hoursNote));

        // Write: a status update against ids that can't exist. 403 = the key has no sheets:write scope; anything else = the scope passed.
        var write = await client.PostAsync($"/sheets/{Guid.Empty}/tasks/{Guid.Empty}/status", new { status = (string?)null, percentComplete = (decimal?)null }, ct);
        bool? canWrite = write.Status == WbStatus.Forbidden ? false : write.Status is WbStatus.NotFound or WbStatus.Error ? true : (bool?)null;
        probes[1] = probes[1] with
        {
            CanWrite = canWrite,
            Note = canWrite == true ? "Can write task status (needed only for pushing work state)." : canWrite == false ? "Read only: work state can't be pushed." : probes[1].Note,
        };

        // Time must NOT be reachable: scope check only (a made-up path: 403 = no scope, 404 = the scope let it through).
        var time = await client.GetAsync("/time/bytebench-scope-check", ct);
        bool? timeRead = time.Status == WbStatus.Forbidden ? false : time.Status is WbStatus.NotFound or WbStatus.Ok ? true : (bool?)null;
        probes.Add(new("time", "Time and expenses", "Not used: ByteBench never reads or writes timesheets", timeRead, null,
            timeRead == true ? "This key can reach timesheets. ByteBench doesn't need that: create a new key without the time scope and replace this one." : null));

        var ok = projects.Ok && sheets.Ok;
        var summary = !ok ? "Connected, but the key can't read " + string.Join(" or ", new[] { projects.Ok ? null : "projects", sheets.Ok ? null : "sheets" }.Where(x => x != null)) + ": add projects:read and sheets:read."
            : (canWrite == true ? "Connected. Reads projects and sheets; can write task status." : "Connected. Reads projects and sheets; read only.");
        return new(ok, summary, now, probes);
    }

    private static WorkbenchTestResult? ParseTest(WorkbenchConnection? row)
    {
        if (row?.LastTestJson == null) return null;
        try { return JsonSerializer.Deserialize<WorkbenchTestResult>(row.LastTestJson, WorkbenchClient.JsonOptions); }
        catch (JsonException) { return null; }
    }

    /// <summary>The newest <c>workbench.*</c> audit events (connection changes, tests, pushes). Site administrators only.</summary>
    public async Task<List<AuditEvent>> RecentAuditAsync(int count = 25)
    {
        await RequireAdminAsync("see the Workbench audit trail");
        var page = await audit.SearchAsync(new AuditQuery { ActionPrefix = AuditPrefix, PageSize = count });
        return page.Events;
    }

    /// <summary>Sets <c>Workbench:BaseUrl</c>-style defaults aside: whether the integration can reach Workbench at all (address and key present).</summary>
    public bool IsConnected => client.IsConfigured;
}
