using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Ci;

/// <summary>What <see cref="CiRecovery.RecoverInterruptedAsync"/> did.</summary>
public sealed record CiRecoveryResult(int Interrupted, int Requeued, int Errored);

/// <summary>
/// After a server restart: pipelines that were Running can't continue (their containers are gone). The first time a pipeline is
/// interrupted it goes back in the queue: jobs that already finished keep their result and artifacts, jobs that were running start
/// over and their log says so. If the same pipeline is interrupted again it ends in Error instead (no restart loop): its running jobs
/// are Failed and the rest Canceled, all of which can be retried from the pipeline page.
/// </summary>
public static class CiRecovery
{
    public const string ErrorAfterSecondRestart = "Interrupted by a server restart again (it was already queued once after an earlier restart). Retry the failed jobs, or re-run the pipeline.";

    public static async Task<CiRecoveryResult> RecoverInterruptedAsync(AppDbContext db, CancellationToken ct)
    {
        var running = await db.Pipelines.Where(p => p.Status == PipelineStatus.Running)
            .Include(p => p.Jobs).ThenInclude(j => j.Steps).ThenInclude(s => s.Log).AsSplitQuery().ToListAsync(ct);
        var now = DateTime.UtcNow;
        var requeued = 0;
        foreach (var p in running)
        {
            var interrupted = p.Jobs.Where(j => j.Status == PipelineStatus.Running).ToList();
            if (p.RecoveryAttempts < 1)
            {
                p.RecoveryAttempts++;
                p.Status = PipelineStatus.Queued;
                p.Error = null;
                p.FinishedAt = null;
                foreach (var j in interrupted)
                {
                    j.Status = PipelineStatus.Queued;
                    j.StartedAt = j.FinishedAt = null;
                    j.Coverage = null;
                    foreach (var s in j.Steps)
                    {
                        s.Status = PipelineStatus.Queued;
                        s.ExitCode = null;
                        s.StartedAt = s.FinishedAt = null;
                    }
                    if (j.Steps.OrderBy(s => s.Order).FirstOrDefault() is { } first)
                    {
                        first.Log ??= new PipelineStepLog { StepId = first.Id };
                        first.Log.Text = CiRunner.RecoveryNote;
                        first.Log.Truncated = false;
                    }
                }
                requeued++;
            }
            else
            {
                p.Status = PipelineStatus.Error;
                p.Error = ErrorAfterSecondRestart;
                p.FinishedAt = now;
                foreach (var j in p.Jobs.Where(j => j.Status is PipelineStatus.Queued or PipelineStatus.Running))
                {
                    j.Status = j.Status == PipelineStatus.Running ? PipelineStatus.Failed : PipelineStatus.Canceled;
                    j.FinishedAt = now;
                    foreach (var s in j.Steps.Where(s => s.Status is PipelineStatus.Queued or PipelineStatus.Running))
                    {
                        var wasRunning = s.Status == PipelineStatus.Running;
                        s.Status = j.Status;
                        s.FinishedAt = now;
                        if (wasRunning)
                        {
                            s.Log ??= new PipelineStepLog { StepId = s.Id };
                            s.Log.Text = (s.Log.Text.Length > 0 ? s.Log.Text + "\n" : "")
                                         + "[bytebench] The server restarted while this step was running, after an earlier restart already interrupted this pipeline. Retry the job to run it again.";
                        }
                    }
                }
            }
            // Deployments the interrupted jobs had recorded as running never finish: close them.
            var jobIds = interrupted.Select(j => j.Id).ToList();
            await db.Deployments.Where(d => jobIds.Contains(d.JobId) && d.Status == PipelineStatus.Running)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, PipelineStatus.Canceled).SetProperty(d => d.FinishedAt, now), ct);
        }
        await db.SaveChangesAsync(ct);
        return new CiRecoveryResult(running.Count, requeued, running.Count - requeued);
    }
}
