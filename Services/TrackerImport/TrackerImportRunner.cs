using System.Collections.Concurrent;
using System.Text.Json;
using ByteBench.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.TrackerImport;

/// <summary>
/// Runs tracker imports (singleton; callers check permissions). The credentials are held in memory for the one run and never written
/// anywhere; the address went through <see cref="TrackerUrlGuard"/> when the import was requested and again on every request. Cancelling or
/// a restart stops the run; what was already written stays (and a new run continues where it stopped, see <see cref="TrackerImportWriter"/>).
/// </summary>
public sealed class TrackerImportRunner(IDbContextFactory<AppDbContext> dbFactory, ISourceReaderFactory readers, IServiceScopeFactory scopes,
    IOptionsMonitor<TrackerImportOptions> options, ILogger<TrackerImportRunner> log)
{
    public const string InterruptedMessage = "The server restarted before this import finished. Issues already imported are kept; start the import again to continue.";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly System.Threading.Channels.Channel<int> queue = System.Threading.Channels.Channel.CreateUnbounded<int>();
    private readonly ConcurrentDictionary<int, SourceConnection> connections = new();
    private readonly ConcurrentDictionary<int, CancellationTokenSource> running = new();
    private readonly ConcurrentDictionary<int, bool> userCancelled = new();

    public void Enqueue(int importId, SourceConnection connection)
    {
        connections[importId] = connection;
        queue.Writer.TryWrite(importId);
    }

    public IAsyncEnumerable<int> ReadQueueAsync(CancellationToken ct) => queue.Reader.ReadAllAsync(ct);

    public async Task<bool> CancelAsync(int importId)
    {
        if (running.TryGetValue(importId, out var cts)) { userCancelled[importId] = true; cts.Cancel(); return true; }
        await using var db = await dbFactory.CreateDbContextAsync();
        var n = await db.TrackerImportJobs.Where(i => i.Id == importId && i.Status == TrackerImportStatus.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, TrackerImportStatus.Cancelled).SetProperty(i => i.FinishedAt, DateTime.UtcNow));
        connections.TryRemove(importId, out _);
        return n > 0;
    }

    /// <summary>At start-up: imports a previous process left queued or running can't continue (their credentials are gone).</summary>
    public async Task RecoverInterruptedAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.TrackerImportJobs.Where(i => i.Status == TrackerImportStatus.Queued || i.Status == TrackerImportStatus.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, TrackerImportStatus.Failed).SetProperty(i => i.Error, InterruptedMessage).SetProperty(i => i.FinishedAt, DateTime.UtcNow));
    }

    /// <summary>Runs one queued import to the end. Tests call this directly with <c>TrackerImport:Enabled=false</c>.</summary>
    public async Task RunAsync(int importId, CancellationToken stopping = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var claimed = await db.TrackerImportJobs.Where(i => i.Id == importId && i.Status == TrackerImportStatus.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, TrackerImportStatus.Running).SetProperty(i => i.StartedAt, DateTime.UtcNow));
        if (claimed == 0) return;
        var job = await db.TrackerImportJobs.AsNoTracking().FirstAsync(i => i.Id == importId);
        if (!connections.TryRemove(importId, out var connection))
        {
            await FinishAsync(importId, TrackerImportStatus.Failed, InterruptedMessage);
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        running[importId] = cts;
        TrackerImportWriter? writer = null;
        try
        {
            var settings = JsonSerializer.Deserialize<TrackerImportSettings>(job.SettingsJson ?? "{}", Json) ?? new();
            using var scope = scopes.CreateScope();
            var access = scope.ServiceProvider.GetRequiredService<ProjectAccess>();
            var actorId = job.RequestedById ?? await db.Users.AsNoTracking().Where(u => u.IsAdmin).Select(u => u.Id).FirstOrDefaultAsync(cts.Token);
            var sourceName = job.Source.ToString();
            using var reader = readers.Create(connection);
            var probe = await reader.ProbeAsync(cts.Token);
            await using (var pdb = await dbFactory.CreateDbContextAsync(cts.Token))
                await pdb.TrackerImportJobs.Where(i => i.Id == importId).ExecuteUpdateAsync(s => s.SetProperty(i => i.IssuesExpected, probe.IssueCount), cts.Token);

            writer = new TrackerImportWriter(dbFactory, access, job, settings, SourceKeyOf(connection), sourceName, actorId);
            await writer.PrepareAsync(reader, cts.Token);
            var max = Math.Clamp(options.CurrentValue.MaxIssues, 1, 1_000_000);
            var read = new SourceReadOptions(settings.IncludeClosed, settings.IncludeComments, settings.IncludePullRequests, max + 1); // one more than allowed, so hitting the limit is noticed
            var n = 0;
            await foreach (var issue in reader.ReadIssuesAsync(read, cts.Token))
            {
                if (++n > max) { writer.Warn($"Stopped after {max} issues (TrackerImport:MaxIssues). Run the import again to continue with the rest."); break; }
                await writer.AddAsync(issue, cts.Token);
            }
            await writer.FinishAsync(cts.Token);
            await FinishAsync(importId, TrackerImportStatus.Done, null, writer);
            await AfterAsync(scope, job, writer, actorId);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            var byUser = userCancelled.TryRemove(importId, out _);
            await FinishAsync(importId, TrackerImportStatus.Cancelled,
                byUser ? "Cancelled. Issues already imported are kept; start the import again to continue." : InterruptedMessage, writer);
        }
        catch (SourceException ex) { await FinishAsync(importId, TrackerImportStatus.Failed, ex.Message, writer); }
        catch (Exception ex)
        {
            log.LogError(ex, "Tracker import {Id} crashed", importId);
            await FinishAsync(importId, TrackerImportStatus.Failed, "Unexpected error while importing (details are in the server log). Issues already imported are kept; start the import again to continue.", writer);
        }
        finally { running.TryRemove(importId, out _); userCancelled.TryRemove(importId, out _); connections.TryRemove(importId, out _); }
    }

    private async Task AfterAsync(IServiceScope scope, TrackerImportJob job, TrackerImportWriter writer, int actorId)
    {
        try
        {
            var live = scope.ServiceProvider.GetRequiredService<LiveEvents>();
            var audit = scope.ServiceProvider.GetRequiredService<Audit.AuditLog>();
            live.Publish([.. writer.TouchedIssueIds.Take(500).Select(id => (LiveEvent)new IssueChanged(job.ProjectId, id)), new BoardChanged(job.ProjectId), new ActivityChanged()]);
            await using var db = await dbFactory.CreateDbContextAsync();
            var done = await db.TrackerImportJobs.AsNoTracking().FirstAsync(i => i.Id == job.Id);
            await audit.RecordAsync("project.tracker_imported", "tracker_import", job.Id, job.ProjectId,
                new { source = job.Source.ToString(), project = job.SourceProject, created = done.IssuesCreated, skipped = done.IssuesSkipped, comments = done.CommentsCreated, warnings = done.WarningCount }, actorId);
        }
        catch (Exception ex) { log.LogWarning(ex, "Tracker import {Id}: after-steps failed", job.Id); }
    }

    private async Task FinishAsync(int importId, TrackerImportStatus status, string? error, TrackerImportWriter? writer = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.TrackerImportJobs.Where(i => i.Id == importId).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.Status, status).SetProperty(i => i.Error, error).SetProperty(i => i.FinishedAt, DateTime.UtcNow)
            .SetProperty(i => i.WarningCount, writer?.WarningCount ?? 0).SetProperty(i => i.WarningsJson, writer?.WarningsJson));
    }

    /// <summary>Names the source project for <see cref="ImportedItem.SourceKey"/>: kind, address and project, in lower case.</summary>
    public static string SourceKeyOf(SourceConnection c) => $"{c.Kind}:{(c.BaseUrl ?? "").Trim().TrimEnd('/')}/{c.Project.Trim()}".ToLowerInvariant();
}
