using ByteBench.Data;

namespace ByteBench.Services.Ci;

/// <summary>
/// A job as the scheduler sees it. <see cref="Manual"/>: <c>when: manual</c>. <see cref="NeedsApproval"/>: it targets a
/// protected environment, so it waits for a Maintainer like a manual job (and always blocks). <see cref="Played"/>:
/// someone clicked Run (or Retry), so it no longer waits.
/// </summary>
public sealed record CiJobState(string Name, IReadOnlyList<string> Needs, PipelineStatus Status, bool Manual = false,
    bool AllowFailure = false, bool NeedsApproval = false, bool Played = false)
{
    /// <summary>Waiting for a person: a manual job, or one that needs approval.</summary>
    public bool Gated => (Manual || NeedsApproval) && !Played;
    /// <summary>A waiting job that holds the pipeline in the Manual (blocked) state.</summary>
    public bool Blocking => NeedsApproval || !AllowFailure;
}

public enum CiJobAction { Start, WaitForManual, Skip, Cancel }

/// <summary>
/// The pipeline state machine, kept free of I/O so it can be tested. <see cref="Next"/> says what to do with each
/// Queued job; <see cref="Result"/> gives the pipeline's status once nothing can run.
/// <list type="bullet">
/// <item>A need is satisfied when it Passed, or failed with <c>allow_failure</c>.</item>
/// <item>A need that was Skipped or Canceled, or failed without <c>allow_failure</c>, skips the job.</item>
/// <item>A job waits while a need is Queued, Running or Manual.</item>
/// <item>Once its needs are satisfied, a gated job (manual, or targeting a protected environment) goes to Manual; others start.</item>
/// <item>A Manual job that is played goes back to Queued with <see cref="CiJobState.Played"/> set, and starts.</item>
/// </list>
/// </summary>
public static class CiJobGraph
{
    public static List<(string Job, CiJobAction Action)> Next(IReadOnlyCollection<CiJobState> jobs, bool canceled)
    {
        var byName = jobs.ToDictionary(j => j.Name);
        var actions = new List<(string, CiJobAction)>();
        foreach (var job in jobs.Where(j => j.Status == PipelineStatus.Queued || canceled && j.Status == PipelineStatus.Manual))
        {
            if (canceled) { actions.Add((job.Name, CiJobAction.Cancel)); continue; }
            var waiting = false;
            var skip = false;
            foreach (var n in job.Needs)
            {
                if (!byName.TryGetValue(n, out var need)) continue; // validated files can't do this
                if (IsSatisfied(need)) continue;
                if (need.Status is PipelineStatus.Queued or PipelineStatus.Running or PipelineStatus.Manual) waiting = true;
                else skip = true;
            }
            if (skip) actions.Add((job.Name, CiJobAction.Skip));
            else if (!waiting) actions.Add((job.Name, job.Gated ? CiJobAction.WaitForManual : CiJobAction.Start));
        }
        return actions;
    }

    public static bool IsSatisfied(CiJobState need) =>
        need.Status == PipelineStatus.Passed || need.AllowFailure && !need.NeedsApproval && need.Status is PipelineStatus.Failed or PipelineStatus.Error;

    /// <summary>
    /// The pipeline's status when nothing more can run now: Canceled if it was canceled; Failed if a job failed (or was
    /// canceled) without <c>allow_failure</c>; Manual if a blocking job waits for someone; otherwise Passed. Jobs still
    /// Queued behind a manual job don't count.
    /// </summary>
    public static PipelineStatus Result(IReadOnlyCollection<CiJobState> jobs, bool canceled)
    {
        if (canceled) return PipelineStatus.Canceled;
        if (jobs.Any(j => j.Status is PipelineStatus.Failed or PipelineStatus.Error or PipelineStatus.Canceled && !(j.AllowFailure && !j.NeedsApproval)))
            return PipelineStatus.Failed;
        if (jobs.Any(j => j.Status == PipelineStatus.Manual && j.Blocking)) return PipelineStatus.Manual;
        return PipelineStatus.Passed;
    }

    /// <summary>The column of each job in the pipeline graph: 0 for jobs without needs, else one more than its deepest need.</summary>
    public static Dictionary<string, int> Stages(IEnumerable<(string Name, IEnumerable<string> Needs)> jobs)
    {
        var map = jobs.ToDictionary(j => j.Name, j => j.Needs.ToList());
        var stage = new Dictionary<string, int>();
        int Depth(string n, int guard)
        {
            if (stage.TryGetValue(n, out var d)) return d;
            if (guard > map.Count || !map.TryGetValue(n, out var needs)) return 0;
            d = needs.Count == 0 ? 0 : needs.Where(map.ContainsKey).Select(x => Depth(x, guard + 1) + 1).DefaultIfEmpty(0).Max();
            stage[n] = d;
            return d;
        }
        foreach (var n in map.Keys) Depth(n, 0);
        return stage;
    }
}
