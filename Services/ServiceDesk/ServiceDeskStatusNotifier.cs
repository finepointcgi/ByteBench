using System.Threading.Channels;
using ByteBench.Data;
using ByteBench.Services.Audit;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.ServiceDesk;

/// <summary>
/// Status changes of issues, queued by <see cref="Webhooks.WebhookNotifier.IssueUpdatedAsync"/> (which every issue update
/// path calls) so the request isn't slowed down. <see cref="ServiceDeskStatusWorker"/> emails the requester when the new
/// workflow status is one the project's settings chose.
/// </summary>
public sealed class ServiceDeskStatusQueue
{
    private readonly Channel<(int IssueId, int? StatusId)> channel = Channel.CreateBounded<(int, int?)>(
        new BoundedChannelOptions(5000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    internal ChannelReader<(int IssueId, int? StatusId)> Reader => channel.Reader;

    public void Enqueue(int issueId, int? toStatusId) => channel.Writer.TryWrite((issueId, toStatusId));
}

public sealed class ServiceDeskStatusWorker(ServiceDeskStatusQueue queue, IServiceScopeFactory scopes, ILogger<ServiceDeskStatusWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var (issueId, statusId) in queue.Reader.ReadAllAsync(stoppingToken))
            {
                try { WorkerPulse.Tick("service-desk-status"); await NotifyAsync(issueId, statusId, stoppingToken); WorkerPulse.Ok("service-desk-status"); }
                catch (Exception ex) when (ex is not OperationCanceledException) { WorkerPulse.Fail("service-desk-status", ex); log.LogError(ex, "Service desk status email for issue {IssueId} failed", issueId); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal async Task NotifyAsync(int issueId, int? statusId, CancellationToken ct)
    {
        if (statusId is not { } sid) return;
        await using var scope = scopes.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(ct);
        var ticket = await db.ServiceDeskTickets.Include(t => t.Contact).Include(t => t.Issue).ThenInclude(i => i.Project)
            .FirstOrDefaultAsync(t => t.IssueId == issueId, ct);
        if (ticket == null || ticket.Issue.WorkflowStatusId != sid) return; // not a ticket, or it has moved on already
        var settings = await db.ServiceDeskSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == ticket.ProjectId, ct);
        if (settings is not { Enabled: true } || !settings.NotifyStatusIds.Contains(sid)) return;
        var status = await db.WorkflowStatuses.Where(w => w.Id == sid).Select(w => w.Name).FirstOrDefaultAsync(ct) ?? "updated";
        var key = $"{ticket.Issue.Project.Key}-{ticket.Issue.Number}";
        var sent = await scope.ServiceProvider.GetRequiredService<ServiceDeskMailer>().SendStatusAsync(db, ticket, ticket.Contact, key, status);
        if (sent)
        {
            var bot = await ServiceDeskBot.EnsureAsync(db, ct);
            await scope.ServiceProvider.GetRequiredService<AuditLog>().RecordAsync("service_desk.status_notified", "issue", issueId, ticket.ProjectId,
                new { status, to = ticket.Contact.Email }, actorId: bot);
        }
    }
}
