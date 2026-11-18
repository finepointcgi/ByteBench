using Microsoft.EntityFrameworkCore;

namespace GitBench.Data;

/// <summary>
/// Demo custom fields, components, versions, an issue template and estimates for WEB. Runs once: skipped when
/// WEB already has custom fields or components, or isn't the demo project.
/// </summary>
public static class FieldsSeeder
{
    public static void Initialize(AppDbContext db)
    {
        var web = db.Projects.Include(p => p.Labels).FirstOrDefault(p => p.Key == "WEB");
        if (web == null || db.CustomFields.Any(f => f.ProjectId == web.Id) || db.ProjectComponents.Any(c => c.ProjectId == web.Id)) return;
        var users = db.Users.Where(u => new[] { "alice", "bob", "carol", "dan" }.Contains(u.UserName)).ToDictionary(u => u.UserName!);
        if (users.Count < 4) return;
        var issues = db.Issues.Where(i => i.ProjectId == web.Id).ToDictionary(i => i.Number);
        if (!issues.TryGetValue(3, out var kanban) || kanban.Title != "Drag-and-drop Kanban board") return; // not the demo data
        Issue? ByTitle(string title) => issues.Values.FirstOrDefault(i => i.Title == title);
        var today = DateTime.UtcNow.Date;

        // ----- Custom fields -----
        var severity = new CustomField
        {
            ProjectId = web.Id, Key = "severity", Name = "Severity", Type = CustomFieldType.SingleSelect, Required = true, DefaultValue = "Major",
            Options = ["Blocker", "Critical", "Major", "Minor", "Trivial"], IssueTypes = [IssueType.Bug], Order = 1,
            Description = "How badly the bug hurts users.",
        };
        var browsers = new CustomField
        {
            ProjectId = web.Id, Key = "browsers", Name = "Affected browsers", Type = CustomFieldType.MultiSelect,
            Options = ["Chrome", "Firefox", "Safari", "Edge"], IssueTypes = [IssueType.Bug], Order = 2,
        };
        var customer = new CustomField { ProjectId = web.Id, Key = "customer", Name = "Customer", Type = CustomFieldType.Text, Order = 3, Description = "Who asked for it, if anyone." };
        var qa = new CustomField { ProjectId = web.Id, Key = "qa_reviewer", Name = "QA reviewer", Type = CustomFieldType.User, Order = 4 };
        var spec = new CustomField
        {
            ProjectId = web.Id, Key = "spec_url", Name = "Spec", Type = CustomFieldType.Url, Order = 5,
            IssueTypes = [IssueType.Feature, IssueType.Story, IssueType.Epic],
        };
        var regression = new CustomField { ProjectId = web.Id, Key = "regression", Name = "Regression", Type = CustomFieldType.Checkbox, IssueTypes = [IssueType.Bug], Order = 6 };
        db.CustomFields.AddRange(severity, browsers, customer, qa, spec, regression);

        // ----- Components and versions -----
        var frontend = new ProjectComponent { ProjectId = web.Id, Name = "Frontend", Description = "Blazor pages and styling.", Lead = users["carol"], DefaultAssignee = users["bob"] };
        var api = new ProjectComponent { ProjectId = web.Id, Name = "API", Description = "REST endpoints and services.", Lead = users["dan"], DefaultAssignee = users["dan"] };
        var infra = new ProjectComponent { ProjectId = web.Id, Name = "Infrastructure", Description = "Build, CI and hosting.", Lead = users["alice"] };
        db.ProjectComponents.AddRange(frontend, api, infra);
        var v08 = new ProjectVersion { ProjectId = web.Id, Name = "0.8", ReleaseDate = today.AddDays(-7), IsReleased = true, Description = "Alpha." };
        var v09 = new ProjectVersion { ProjectId = web.Id, Name = "0.9", StartDate = today.AddDays(-7), ReleaseDate = today.AddDays(7), Description = "Beta for early users." };
        var v10 = new ProjectVersion { ProjectId = web.Id, Name = "1.0", ReleaseDate = today.AddDays(30), Description = "First public release." };
        db.ProjectVersions.AddRange(v08, v09, v10);

        // ----- Template -----
        var bugLabel = web.Labels.FirstOrDefault(l => l.Name == "bug");
        db.IssueTemplates.Add(new IssueTemplate
        {
            ProjectId = web.Id, Name = "Bug report", Type = IssueType.Bug, TitlePrefix = "[Bug] ", Order = 1,
            Body = "### Steps to reproduce\n1. \n2. \n\n**Expected:** \n\n**Actual:** \n\n### Environment\n- Browser: \n",
            LabelIds = bugLabel == null ? [] : [bugLabel.Id],
        });
        db.IssueTemplates.Add(new IssueTemplate
        {
            ProjectId = web.Id, Name = "User story", Type = IssueType.Story, Order = 2,
            Body = "As a **…** I want **…** so that **…**.\n\n### Acceptance criteria\n- [ ] \n",
        });
        db.SaveChanges();

        // ----- Values on the demo issues -----
        void Value(Issue? i, CustomField f, Action<CustomFieldValue> set)
        {
            if (i == null) return;
            var v = new CustomFieldValue { IssueId = i.Id, FieldId = f.Id };
            set(v);
            db.CustomFieldValues.Add(v);
        }
        void Versions(Issue? i, ProjectVersion fix, ProjectVersion? affects = null)
        {
            if (i == null) return;
            db.IssueVersions.Add(new IssueVersion { IssueId = i.Id, VersionId = fix.Id, Kind = VersionLinkKind.Fix });
            if (affects != null) db.IssueVersions.Add(new IssueVersion { IssueId = i.Id, VersionId = affects.Id, Kind = VersionLinkKind.Affects });
        }
        void Components(Issue? i, params ProjectComponent[] cs)
        {
            if (i == null) return;
            db.Entry(i).Collection(x => x.Components).Load();
            i.Components.AddRange(cs);
        }

        var login = ByTitle("Login page rejects valid emails with a plus sign");
        var slow = ByTitle("Issue list is slow with 10k+ issues");
        var safari = ByTitle("Avatar images broken on Safari");
        var email = ByTitle("Email notifications when assigned");
        var skeleton = ByTitle("Set up project skeleton and CI pipeline");
        var docs = ByTitle("Write API documentation");
        var dark = ByTitle("Dark mode");
        foreach (var (bug, sev) in new[] { (login, "Critical"), (slow, "Major"), (safari, "Minor") }) Value(bug, severity, v => v.TextValue = sev);
        Value(safari, browsers, v => v.TextValue = "Safari");
        Value(slow, browsers, v => v.TextValue = "Chrome");
        Value(slow, browsers, v => v.TextValue = "Firefox");
        Value(login, regression, v => v.BoolValue = true);
        Value(slow, qa, v => v.UserId = users["carol"].Id);
        Value(email, customer, v => v.TextValue = "Northwind Traders");
        Value(kanban, spec, v => v.TextValue = "https://example.com/specs/kanban");

        Components(kanban, frontend);
        Components(safari, frontend);
        Components(dark, frontend);
        Components(login, api);
        Components(slow, frontend, api);
        Components(email, api);
        Components(skeleton, infra);
        Components(docs, api);
        Versions(skeleton, v08);
        Versions(login, v08);
        Versions(kanban, v09);
        Versions(slow, v09, v08);
        Versions(safari, v09, v08);
        Versions(email, v09);
        Versions(docs, v10);
        Versions(dark, v10);

        // ----- Estimates -----
        void Estimate(Issue? i, int originalHours, int remainingHours)
        {
            if (i == null) return;
            (i.OriginalEstimateMinutes, i.RemainingEstimateMinutes) = (originalHours * 60, remainingHours * 60);
        }
        Estimate(kanban, 24, 10);
        Estimate(slow, 8, 3);
        Estimate(email, 12, 12);
        Estimate(safari, 2, 2);
        Estimate(skeleton, 6, 0);
        db.SaveChanges();
    }
}
