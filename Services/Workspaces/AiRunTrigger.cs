using ByteBench.Data;
using ByteBench.Services.Ai;
using ByteBench.Services.Audit;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Workspaces;

/// <summary>
/// Assigning an issue to the AI user asks for a run (called from <see cref="NotificationService"/> after an assignment is saved). The person who
/// assigned it is the requester and needs EditIssues, which assigning already required. A refused request (runs off, the AI user isn't a developer
/// in the project, the queue is full…) never fails the assignment: it is written to the audit log with the reason.
/// </summary>
public static class AiRunTrigger
{
    public static async Task OnAssignedAsync(IServiceProvider services, Issue issue, int assigneeId, int actorId)
    {
        try
        {
            var factory = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
                if (!await db.Users.AnyAsync(u => u.Id == assigneeId && u.UserName == AiBot.UserName)) return;
            var runs = services.GetRequiredService<AiRunService>();
            try { await runs.RequestAsync(issue.Id, actorId, "assignment"); }
            catch (InvalidOperationException ex) // includes ForbiddenException
            {
                await services.GetRequiredService<AuditLog>().RecordAsync("ai_run.refused", "issue", issue.Id, issue.ProjectId, new { reason = ex.Message, trigger = "assignment" }, actorId);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            services.GetService<ILoggerFactory>()?.CreateLogger("AiRunTrigger").LogWarning(ex, "Could not start an AI run for issue {Issue}", issue.Id);
        }
    }
}
