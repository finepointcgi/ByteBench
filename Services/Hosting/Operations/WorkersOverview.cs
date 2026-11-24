namespace ByteBench.Services.Hosting.Operations;

/// <summary>One line of the workers page: what a worker is, whether it is on, when it last ran and how deep its queue is.</summary>
public sealed record WorkerRow(string Id, string Title, string Description, bool Known, bool Reported, bool Enabled,
    DateTime? LastTickUtc, DateTime? LastSuccessUtc, DateTime? LastErrorUtc, string? LastError, long Errors, string? Queue);

/// <summary>Merges the registry's live state with the catalogue of workers the app can host (so a worker that never started still shows up).</summary>
public static class WorkersOverview
{
    private sealed record Entry(string Id, string Title, string Description, Func<QueueDepths, string?>? Queue);

    private static string Q(long queued, long running) => running > 0 ? $"{queued:N0} queued, {running:N0} running" : $"{queued:N0} queued";

    private static readonly Entry[] Catalog =
    [
        new("ci-runner", "CI runner", "Runs queued pipelines in Docker (Ci:Enabled).", q => Q(q.CiQueued, q.CiRunning)),
        new("ci-schedules", "CI schedules", "Starts scheduled pipelines.", null),
        new("ci-cleanup", "CI cleanup", "Deletes expired artifacts and caches (hourly).", null),
        new("ci-pr-watcher", "CI pull request watcher", "Starts pipelines when pull requests change.", null),
        new("auto-merge", "Auto-merge", "Merges armed pull requests once they are ready.", q => $"{q.AutoMergeArmed:N0} armed"),
        new("merge-queue", "Merge queue", "Tests and merges queued pull requests.", q => Q(q.MergeQueueQueued, q.MergeQueueActive)),
        new("mirror", "Mirrors", "Pull mirror polling and push mirror syncs.", q => $"{q.MirrorsDue:N0} due"),
        new("repo-import", "Repository imports", "Clones repositories from other hosts.", q => Q(q.RepoImportQueued, q.RepoImportRunning)),
        new("tracker-import", "Tracker imports", "Imports issues from Jira, GitHub, GitLab, Trello, Linear, Asana.", q => Q(q.TrackerImportQueued, q.TrackerImportRunning)),
        new("webhook", "Webhook sender", "Delivers webhook events.", q => $"{q.WebhookPending:N0} pending, {q.WebhookFailed24h:N0} failed (24 h)"),
        new("webhook-retention", "Webhook retention", "Deletes old webhook deliveries.", null),
        new("dependency-bot", "Dependency bot", "Checks for dependency updates and opens pull requests.", q => $"{q.DependencyBotDue:N0} due"),
        new("workbench-push", "Workbench push", "Pushes work state to Workbench.", null),
        new("workbench-plan", "Workbench plan sync", "Syncs project plans with Workbench.", null),
        new("notification-email", "Notification e-mail", "Sends and batches e-mail notifications.", q => $"{q.EmailPending:N0} pending"),
        new("email", "E-mail sender", "Sends queued messages with retries.", null),
        new("slack-dm", "Slack direct messages", "Sends chat notifications.", q => $"{q.ChatPending:N0} pending"),
        new("service-desk-mail", "Service desk mail", "Polls inbound mail sources.", null),
        new("service-desk-status", "Service desk status e-mails", "Tells requesters about status changes.", null),
        new("ai-review", "AI pull request review", "Runs queued AI reviews.", q => Q(q.AiReviewPending, q.AiReviewRunning)),
        new("ai-triage", "AI triage", "Suggests labels and assignees for issues.", q => $"{q.TriagePending:N0} queued"),
        new("automation", "Automation", "Runs project automation rules and schedules.", null),
        new("backup", "Backup scheduler", "Scheduled backups (Backup:Enabled).", null),
        new("audit-retention", "Audit retention", "Deletes audit events past their retention.", null),
        new("ldap-sync", "LDAP sync", "Synchronises users and groups from LDAP.", null),
        new("package-sweeper", "Package sweeper", "Removes orphaned package files.", null),
        new("lfs-sweeper", "LFS sweeper", "Removes orphaned LFS objects.", null),
        new("pages-cleanup", "Pages cleanup", "Removes superseded Pages deployments.", null),
        new("ssh-server", "SSH server", "Git over SSH (Ssh:Enabled).", null),
    ];

    public static IReadOnlyList<WorkerRow> Build(IReadOnlyList<WorkerState> states, QueueDepths queues)
    {
        var byId = states.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var rows = new List<WorkerRow>();
        foreach (var c in Catalog)
        {
            byId.TryGetValue(c.Id, out var s);
            rows.Add(new(c.Id, c.Title, c.Description, true, s != null, s?.Enabled ?? false,
                s?.LastTickUtc, s?.LastSuccessUtc, s?.LastErrorUtc, s?.LastError, s?.Errors ?? 0, c.Queue?.Invoke(queues)));
        }
        foreach (var s in states.Where(s => Catalog.All(c => c.Id != s.Name)).OrderBy(s => s.Name, StringComparer.Ordinal))
            rows.Add(new(s.Name, s.Name, "", false, true, s.Enabled, s.LastTickUtc, s.LastSuccessUtc, s.LastErrorUtc, s.LastError, s.Errors, null));
        return rows;
    }
}
