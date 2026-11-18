using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;

namespace GitBench.Data;

public static class SeedData
{
    /// <summary>Password for every seeded demo account. Development data only.</summary>
    public const string DemoPassword = "GitBench123";

    private static User DemoUser(string userName, string displayName, string color)
    {
        var email = $"{userName}@example.com";
        var user = new User
        {
            UserName = userName, NormalizedUserName = userName.ToUpperInvariant(),
            Email = email, NormalizedEmail = email.ToUpperInvariant(), EmailConfirmed = true, LockoutEnabled = true,
            SecurityStamp = Guid.NewGuid().ToString(), DisplayName = displayName, Color = color,
        };
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, DemoPassword);
        return user;
    }

    public static void Initialize(AppDbContext db)
    {
        db.Database.Migrate(); // applies pending EF Core migrations (Data/Migrations)
        if (db.Users.Any()) return;

        var alice = DemoUser("alice", "Alice Chen", "#6366f1");
        var bob = DemoUser("bob", "Bob Martinez", "#f59e0b");
        var carol = DemoUser("carol", "Carol Okafor", "#10b981");
        var dan = DemoUser("dan", "Dan Kowalski", "#ec4899");
        alice.IsAdmin = true;
        db.Users.AddRange(alice, bob, carol, dan);

        var bug = new Label { Name = "bug", Color = "#dc2626" };
        var enh = new Label { Name = "enhancement", Color = "#2563eb" };
        var docs = new Label { Name = "documentation", Color = "#0d9488" };
        var ui = new Label { Name = "ui", Color = "#a855f7" };
        var backend = new Label { Name = "backend", Color = "#f97316" };
        var perf = new Label { Name = "performance", Color = "#facc15" };

        var now = DateTime.UtcNow;
        var alpha = new Milestone { Title = "v0.8 Alpha", StartDate = now.AddDays(-21).Date, DueDate = now.AddDays(-7).Date, IsClosed = true };
        var beta = new Milestone { Title = "v0.9 Beta", StartDate = now.AddDays(-7).Date, DueDate = now.AddDays(7).Date, Description = "Beta for early users: board polish and notifications." };
        var v1 = new Milestone { Title = "v1.0 Release", DueDate = now.AddDays(30).Date, Description = "First public release." };

        var web = new Project
        {
            Key = "WEB", Name = "Web Platform", Description = "Customer-facing web application and APIs.",
            Labels = [bug, enh, docs, ui, backend, perf], Milestones = [alpha, beta, v1],
            Visibility = ProjectVisibility.Internal,
            Members =
            [
                new() { User = alice, Role = ProjectRole.Owner }, new() { User = bob, Role = ProjectRole.Developer },
                new() { User = carol, Role = ProjectRole.Maintainer }, new() { User = dan, Role = ProjectRole.Developer },
            ],
        };
        var ops = new Project
        {
            Key = "OPS", Name = "Infrastructure", Description = "CI/CD, hosting and observability.",
            Labels = [new() { Name = "ci", Color = "#0ea5e9" }, new() { Name = "security", Color = "#b91c1c" }],
            Visibility = ProjectVisibility.Private,
            Members =
            [
                new() { User = alice, Role = ProjectRole.Owner }, new() { User = carol, Role = ProjectRole.Developer },
                new() { User = dan, Role = ProjectRole.Maintainer },
            ],
        };
        db.Projects.AddRange(web, ops);

        // Groups: Acme › Platform owns WEB and OPS. dan inherits Developer from Acme (his direct project roles are at least that).
        var acme = new Group
        {
            Name = "Acme", Path = "acme", FullPath = "acme", Description = "Acme Corp: everything we build.", Visibility = ProjectVisibility.Internal,
            Color = "#0ea5e9", Members = [new() { User = alice, Role = ProjectRole.Owner }, new() { User = dan, Role = ProjectRole.Developer }],
        };
        var platform = new Group
        {
            Name = "Platform", Path = "platform", FullPath = "acme/platform", Description = "The web platform and the infrastructure under it.",
            Visibility = ProjectVisibility.Internal, Color = "#8b5cf6", Parent = acme,
        };
        db.Groups.AddRange(acme, platform);
        web.Group = platform;
        ops.Group = platform;
        db.SaveChanges(); // group labels need the group's id
        db.Labels.Add(new Label { GroupId = acme.Id, Name = "customer-reported", Color = "#db2777" });
        db.Milestones.Add(new Milestone { GroupId = acme.Id, Title = "Acme Q1 launch", DueDate = now.AddDays(60).Date, Description = "Cross-project launch for the whole group." });

        int n = 1; double order = 1;
        Issue Make(Project p, string title, IssueType type, IssueStatus status, IssuePriority prio, User reporter, User? assignee,
                   Milestone? ms, int? points, string? desc, params Label[] labels)
        {
            var created = now.AddDays(-Random.Shared.Next(2, 25)).AddHours(-Random.Shared.Next(0, 23));
            var i = new Issue
            {
                Project = p, Number = p == web ? n++ : p.NextIssueNumber++, Title = title, Type = type, Status = status, Priority = prio,
                Reporter = reporter, Assignee = assignee, Milestone = ms, StoryPoints = points, Description = desc,
                Labels = [.. labels], SortOrder = order++, CreatedAt = created,
                UpdatedAt = created.AddHours(Random.Shared.Next(1, 40)),
                ClosedAt = status == IssueStatus.Done ? created.AddDays(2) : null,
            };
            if (i.UpdatedAt > now) i.UpdatedAt = now;
            i.Activities.Add(new Activity { Actor = reporter, Text = "created this issue", CreatedAt = created });
            db.Issues.Add(i);
            return i;
        }

        Make(web, "Set up project skeleton and CI pipeline", IssueType.Task, IssueStatus.Done, IssuePriority.High, alice, alice, alpha, 3,
            "Create the solution layout, add GitHub Actions build, and wire up linting.");
        Make(web, "Login page rejects valid emails with a plus sign", IssueType.Bug, IssueStatus.Done, IssuePriority.Highest, carol, bob, alpha, 2,
            "### Steps to reproduce\n1. Go to `/login`\n2. Enter `user+tag@example.com`\n3. Submit\n\n**Expected:** login succeeds\n\n**Actual:** *Invalid email* error.", bug, backend);
        var kanban = Make(web, "Drag-and-drop Kanban board", IssueType.Feature, IssueStatus.InProgress, IssuePriority.High, alice, alice, beta, 8,
            "As a team member I want to drag issues between columns so that updating status is quick.\n\n- [x] Columns per status\n- [x] Drag between columns\n- [ ] Reorder within a column\n- [ ] Swimlanes by assignee", enh, ui);
        Make(web, "Email notifications when assigned", IssueType.Story, IssueStatus.Todo, IssuePriority.Medium, bob, carol, beta, 5,
            "Send an email to the assignee whenever an issue is assigned to them.", enh, backend);
        Make(web, "Issue list is slow with 10k+ issues", IssueType.Bug, IssueStatus.InReview, IssuePriority.High, dan, bob, beta, 3,
            "Rendering the list takes ~4s. Add pagination or virtualization and an index on `UpdatedAt`.", bug, perf);
        Make(web, "Write API documentation", IssueType.Task, IssueStatus.Backlog, IssuePriority.Low, alice, null, v1, 3,
            "Document all REST endpoints with examples.", docs);
        Make(web, "Dark mode", IssueType.Feature, IssueStatus.Todo, IssuePriority.Low, carol, dan, v1, 5, null, enh, ui);
        Make(web, "Markdown preview in comment editor", IssueType.Feature, IssueStatus.Backlog, IssuePriority.Medium, bob, null, v1, 2, null, enh, ui);
        Make(web, "Public release", IssueType.Epic, IssueStatus.Backlog, IssuePriority.Highest, alice, alice, v1, null,
            "Umbrella epic for everything required to ship v1.0.");
        Make(web, "Avatar images broken on Safari", IssueType.Bug, IssueStatus.Todo, IssuePriority.Medium, dan, dan, beta, 1, null, bug, ui);

        // ----- Linked work: an epic with children, sub-tasks and a "blocks" link -----
        Issue Web(string title) => db.Issues.Local.First(i => i.Project == web && i.Title == title);
        var release = Web("Public release");
        foreach (var t in new[] { "Drag-and-drop Kanban board", "Write API documentation", "Dark mode" }) Web(t).Parent = release;
        Make(web, "Drag between columns", IssueType.Subtask, IssueStatus.Done, IssuePriority.High, alice, alice, beta, 3, null).Parent = kanban;
        Make(web, "Reorder cards within a column", IssueType.Subtask, IssueStatus.Todo, IssuePriority.Medium, alice, bob, beta, 2,
            "Persist the new position when a card is dropped between two others.").Parent = kanban;
        db.IssueLinks.Add(new IssueLink { SourceIssue = Web("Avatar images broken on Safari"), TargetIssue = Web("Dark mode"), Type = IssueLinkType.Blocks, CreatedAt = now.AddDays(-1) });

        web.NextIssueNumber = n;

        Make(ops, "Migrate builds to self-hosted runners", IssueType.Task, IssueStatus.InProgress, IssuePriority.Medium, dan, dan, null, 5, null, ops.Labels[0]);
        Make(ops, "Rotate database credentials", IssueType.Task, IssueStatus.Todo, IssuePriority.Highest, carol, carol, null, 2, null, ops.Labels[1]);

        kanban.Comments.Add(new Comment { Author = bob, Body = "Should we support WIP limits per column?", CreatedAt = now.AddDays(-2) });
        kanban.Comments.Add(new Comment { Author = alice, Body = "Good idea — let's do it as a follow-up. Opening a separate issue.", CreatedAt = now.AddDays(-1) });
        kanban.Activities.Add(new Activity { Actor = alice, Text = "changed status from **To Do** to **In Progress**", CreatedAt = now.AddDays(-3) });

        db.SaveChanges();
    }
}
