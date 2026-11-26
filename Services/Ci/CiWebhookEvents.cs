using ByteBench.Data;
using ByteBench.Services.Webhooks;

namespace ByteBench.Services.Ci;

/// <summary>
/// Raises the CI webhook events (singleton): <c>pipeline.created</c>, <c>pipeline.started</c>, <c>pipeline.completed</c>,
/// <c>job.started</c> and <c>job.completed</c>. They go through <see cref="WebhookDispatcher"/>, so signing (HMAC), retries and
/// the SSRF guard are the same as for every other event. <c>Repository.Project</c> must be loaded on the pipelines passed in.
/// </summary>
public sealed class CiWebhookEvents(WebhookDispatcher webhooks, Api.PublicBaseUrl baseUrl, ILogger<CiWebhookEvents> log)
{
    public void PipelineCreated(Pipeline p) => Send(WebhookEventNames.PipelineCreated, p, null);
    public void PipelineStarted(Pipeline p) => Send(WebhookEventNames.PipelineStarted, p, null);
    public void PipelineCompleted(Pipeline p) => Send(WebhookEventNames.PipelineCompleted, p, null);
    public void JobStarted(Pipeline p, PipelineJob job) => Send(WebhookEventNames.JobStarted, p, job);
    public void JobCompleted(Pipeline p, PipelineJob job) => Send(WebhookEventNames.JobCompleted, p, job);

    private void Send(string eventName, Pipeline p, PipelineJob? job)
    {
        try
        {
            var pipeline = new
            {
                id = p.Id,
                status = p.Status.ToString().ToLowerInvariant(),
                @ref = p.Ref,
                commitSha = p.CommitSha,
                pullRequestId = p.PullRequestId,
                trigger = p.Trigger.ToString().ToLowerInvariant(),
                error = p.Error,
                coverage = p.Coverage,
                createdAt = p.CreatedAt,
                startedAt = p.StartedAt,
                finishedAt = p.FinishedAt,
                url = baseUrl.Value + PipelineUrls.Detail(p.Repository, p.Id),
            };
            var repository = new { id = p.Repository.Id, slug = p.Repository.Slug, name = p.Repository.Name };
            if (job == null)
                webhooks.Enqueue(p.Repository.ProjectId, eventName, new { pipeline, repository }, p.TriggeredById);
            else
                webhooks.Enqueue(p.Repository.ProjectId, eventName, new
                {
                    job = new
                    {
                        id = job.Id,
                        name = job.Name,
                        stage = CiJobExtras.Parse(job.ExtrasJson).Stage,
                        status = job.Status.ToString().ToLowerInvariant(),
                        allowFailure = job.AllowFailure,
                        coverage = job.Coverage,
                        environment = job.EnvironmentName,
                        startedAt = job.StartedAt,
                        finishedAt = job.FinishedAt,
                    },
                    pipeline,
                    repository,
                }, job.PlayedById ?? p.TriggeredById);
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not raise {Event} for pipeline {Id}", eventName, p.Id); }
    }
}
