using GitBench.Services;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Data;

/// <summary>
/// Demo sprints for WEB: six completed sprints, an active one and a planned one, with backdated issue activity
/// (sprint membership, status and estimate changes) so the burndown and velocity reports have history.
/// Runs once: skipped when WEB already has sprints or isn't the demo project.
/// </summary>
public static class SprintSeeder
{
    public static void Initialize(AppDbContext db)
    {
        var web = db.Projects.FirstOrDefault(p => p.Key == "WEB");
        if (web == null || db.Sprints.Any(s => s.ProjectId == web.Id)) return;
        var users = db.Users.Where(u => new[] { "alice", "bob", "carol", "dan" }.Contains(u.UserName)).ToDictionary(u => u.UserName!);
        if (users.Count < 4) return;
        var existing = db.Issues.Include(i => i.Activities).Where(i => i.ProjectId == web.Id).ToDictionary(i => i.Number);
        if (!existing.TryGetValue(3, out var kanban) || kanban.Title != "Drag-and-drop Kanban board") return; // not the demo data
        User alice = users["alice"], bob = users["bob"], carol = users["carol"], dan = users["dan"];

        var now = DateTime.UtcNow;
        var today = now.Date;
        Sprint MakeSprint(int n, int startOffset, string goal, SprintState state) => new()
        {
            ProjectId = web.Id, Name = $"WEB Sprint {n}", Goal = goal, State = state,
            StartDate = today.AddDays(startOffset), EndDate = today.AddDays(startOffset + 13),
            CreatedAt = today.AddDays(startOffset - 3),
            StartedAt = state == SprintState.Planned ? null : today.AddDays(startOffset).AddHours(9),
            CompletedAt = state == SprintState.Completed ? today.AddDays(startOffset + 13).AddHours(17) : null,
        };
        var history = new[]
        {
            MakeSprint(1, -91, "Walking skeleton: build, deploy, hello world.", SprintState.Completed),
            MakeSprint(2, -77, "Data model and first API endpoints.", SprintState.Completed),
            MakeSprint(3, -63, "Issue tracking basics.", SprintState.Completed),
        };
        var s1 = MakeSprint(4, -49, "Accounts: registration, sign-in and password reset.", SprintState.Completed);
        var s2 = MakeSprint(5, -35, "Project settings and search.", SprintState.Completed);
        var s3 = MakeSprint(6, -21, "CI pipeline and login fixes.", SprintState.Completed);
        var s4 = MakeSprint(7, -7, "Board polish and notifications.", SprintState.Active);
        var s5 = MakeSprint(8, 7, "Dark mode and editor improvements.", SprintState.Planned);
        db.Sprints.AddRange(history);
        db.Sprints.AddRange(s1, s2, s3, s4, s5);
        db.SaveChanges(); // sprint ids are needed for the activity links

        var sprintOf = new Dictionary<Issue, Sprint?>();
        DateTime D(Sprint s, int day, int hour = 11) => s.StartDate!.Value.AddDays(day).AddHours(hour);

        void Act(Issue i, User who, DateTime at, string text) => i.Activities.Add(new Activity { Actor = who, ActorId = who.Id, Text = text, CreatedAt = at });

        void Plan(Issue i, Sprint? to, DateTime at, User who)
        {
            var from = sprintOf.GetValueOrDefault(i);
            if (SprintHistory.SprintText(web.Key, from, to) is { } text) Act(i, who, at, text);
            sprintOf[i] = to;
            i.SprintId = to?.Id;
        }
        void Status(Issue i, IssueStatus to, DateTime at, User who)
        {
            Act(i, who, at, $"changed status from **{i.Status.Name()}** to **{to.Name()}**");
            i.Status = to;
            i.ClosedAt = to == IssueStatus.Done ? at : null;
            if (i.UpdatedAt < at) i.UpdatedAt = at;
        }
        void Estimate(Issue i, int to, DateTime at, User who)
        {
            Act(i, who, at, SprintHistory.EstimateText(i.StoryPoints, to));
            i.StoryPoints = to;
        }
        void Backdate(Issue i, DateTime created)
        {
            i.CreatedAt = created;
            foreach (var a in i.Activities.Where(a => a.Text == "created this issue")) a.CreatedAt = created;
            if (i.ClosedAt < created) i.ClosedAt = created;
        }
        Issue New(string title, IssueType type, int points, User reporter, User assignee, DateTime created)
        {
            var i = new Issue
            {
                ProjectId = web.Id, Number = web.NextIssueNumber++, Title = title, Type = type, Status = IssueStatus.Todo,
                Priority = IssuePriority.Medium, ReporterId = reporter.Id, AssigneeId = assignee.Id, StoryPoints = points,
                SortOrder = 100 + web.NextIssueNumber, CreatedAt = created, UpdatedAt = created,
            };
            Act(i, reporter, created, "created this issue");
            db.Issues.Add(i);
            return i;
        }
        // Planned before the sprint started, then worked through to Done on the given day.
        void Done(Issue i, Sprint s, int day, User who)
        {
            if (i.Status != IssueStatus.InProgress) Status(i, IssueStatus.InProgress, D(s, Math.Max(0, day - 2), 10), who);
            Status(i, IssueStatus.Done, D(s, day, 16), who);
        }

        // ----- Sprints 1-3 (completed): early history for the velocity chart -----
        var early = new (string title, IssueType type, int points, User who, int doneDay)[][]
        {
            [("Repository and solution layout", IssueType.Task, 2, alice, 2), ("Deploy to staging on merge", IssueType.Task, 5, dan, 9),
             ("Health check endpoint", IssueType.Task, 1, bob, 4), ("Error page and logging", IssueType.Story, 3, carol, -1)],
            [("Projects and issues schema", IssueType.Story, 5, alice, 5), ("Issue CRUD endpoints", IssueType.Feature, 5, bob, 10),
             ("Seed data for local development", IssueType.Task, 2, carol, 3)],
            [("Issue list page", IssueType.Feature, 5, carol, 6), ("Issue detail page", IssueType.Feature, 5, bob, 11),
             ("Labels", IssueType.Feature, 3, dan, 8), ("Comment threads", IssueType.Feature, 3, alice, -1)],
        };
        Issue? carried = null;
        for (var k = 0; k < history.Length; k++)
        {
            var hs = history[k];
            if (carried != null) { Done(carried, hs, 4, carried.Assignee ?? alice); carried = null; }
            foreach (var (title, type, points, who, doneDay) in early[k])
            {
                var i = New(title, type, points, alice, who, D(hs, -4));
                i.Assignee = who;
                Plan(i, hs, D(hs, -1, 15), carol);
                if (doneDay >= 0) Done(i, hs, doneDay, who);
                else
                {
                    Status(i, IssueStatus.InProgress, D(hs, 9), who);
                    Plan(i, k + 1 < history.Length ? history[k + 1] : s1, hs.CompletedAt!.Value, carol);
                    carried = i;
                }
            }
        }
        if (carried != null) Done(carried, s1, 5, carried.Assignee ?? alice);

        // ----- Sprint 4 (completed): one issue carried over -----
        var created1 = D(s1, -5);
        var tokens = New("Design system tokens", IssueType.Task, 3, alice, carol, created1);
        var register = New("User registration flow", IssueType.Story, 5, alice, bob, created1);
        var reset = New("Password reset emails", IssueType.Story, 3, carol, bob, created1);
        var audit = New("Audit logging spike", IssueType.Task, 2, dan, dan, created1);
        foreach (var i in new[] { tokens, register, reset, audit }) Plan(i, s1, D(s1, -1, 15), carol);
        Done(tokens, s1, 3, carol);
        Done(register, s1, 8, bob);
        Done(reset, s1, 12, bob);
        Status(audit, IssueStatus.InProgress, D(s1, 10), dan);
        Plan(audit, s2, s1.CompletedAt!.Value, carol);

        // ----- Sprint 5 (completed): a 5-point issue added mid-sprint, all done -----
        var created2 = D(s2, -4);
        var settings = New("Project settings page", IssueType.Feature, 5, alice, carol, created2);
        var search = New("Issue search endpoint", IssueType.Feature, 3, bob, bob, created2);
        var limit = New("Rate limiting for the public API", IssueType.Task, 3, dan, dan, created2);
        foreach (var i in new[] { settings, search, limit }) Plan(i, s2, D(s2, -1, 15), carol);
        Done(audit, s2, 2, dan);
        Done(search, s2, 5, bob);
        Done(settings, s2, 9, carol);
        Done(limit, s2, 12, dan);
        var retries = New("Webhook delivery retries", IssueType.Task, 5, alice, alice, D(s2, 4));
        Plan(retries, s2, D(s2, 5, 9), alice);
        Done(retries, s2, 11, alice);

        // ----- Sprint 6 (completed): existing WEB-1, WEB-2, WEB-5, WEB-10; two carried over -----
        Issue ci = existing[1], login = existing[2], slow = existing[5], safari = existing[10];
        var timeout = New("Session timeout warning", IssueType.Story, 2, carol, dan, D(s3, -3));
        foreach (var i in new[] { ci, login, slow, safari })
        {
            Backdate(i, D(s3, -3, 10 + i.Number % 5));
            i.Status = IssueStatus.Todo; // replayed below to its seeded status
            i.ClosedAt = null;
        }
        foreach (var i in new[] { ci, login, slow, safari, timeout }) Plan(i, s3, D(s3, -1, 15), carol);
        Done(ci, s3, 4, alice);
        Done(login, s3, 7, bob);
        Done(timeout, s3, 11, dan);
        Status(slow, IssueStatus.InProgress, D(s3, 9), bob);
        Plan(slow, s4, s3.CompletedAt!.Value, carol);
        Plan(safari, s4, s3.CompletedAt!.Value, carol);

        // ----- Sprint 7 (active): WEB-3, WEB-4 (re-estimated 3 → 5), WEB-5, WEB-10 (fixed), plus two quick wins -----
        Issue board = existing[3], email = existing[4];
        Backdate(board, D(s4, -4, 10));
        Backdate(email, D(s4, -4, 14));
        email.StoryPoints = 3;
        var shortcuts = New("Keyboard shortcuts on the board", IssueType.Feature, 2, alice, alice, D(s4, -3));
        var prefs = New("Notification preferences page", IssueType.Story, 3, bob, carol, D(s4, -3));
        foreach (var i in new[] { board, email, shortcuts, prefs }) Plan(i, s4, D(s4, -1, 15), carol);
        Status(slow, IssueStatus.InReview, D(s4, 1), bob);
        Done(shortcuts, s4, 2, alice);
        Estimate(email, 5, D(s4, 4, 14), carol);
        Done(safari, s4, 5, dan);
        Done(prefs, s4, 6, carol);

        // ----- Sprint 8 (planned) -----
        foreach (var n in new[] { 7, 8 }) Plan(existing[n], s5, today.AddDays(-1).AddHours(16), carol);

        db.SaveChanges();

        // Snapshots, computed by the same replay the reports use.
        foreach (var s in history.Concat([s1, s2, s3, s4]))
        {
            var b = SprintService.BuildBurndownAsync(db, web.Id, s.Id, now).GetAwaiter().GetResult()!;
            s.CommittedPoints = b.Days[0].ScopePoints;
            s.CommittedCount = b.Days[0].ScopeCount;
            if (s.State == SprintState.Completed)
            {
                var done = db.Issues.Where(i => i.SprintId == s.Id && i.Status == IssueStatus.Done).Select(i => i.StoryPoints).ToList();
                s.CompletedCount = done.Count;
                s.CompletedPoints = done.Sum(p => p ?? 0);
            }
        }
        db.SaveChanges();
    }
}
