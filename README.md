# GitBench

A lightweight Jira/GitLab-style issue tracker built with **Blazor (Interactive Server)**, **.NET 10**, **EF Core** and **SQLite**.

## Run

```bash
dotnet run --launch-profile http
```

Open http://localhost:5108. On first run a `gitbench.db` SQLite file is created and seeded with demo users, projects and issues. Delete it to reset.

## Features

- **Projects** with a short key (`WEB`), and issues numbered per project (`WEB-12`)
- **Issues** with type (task, bug, feature, story, epic), status, priority, assignee, labels, milestone, story points and due date
- **Issue list** with open/closed tabs, search (including by key), filters and sorting
- **Kanban board** with drag-and-drop between and within columns, filtered by sprint or assignee
- **Issue detail** with inline editing, Markdown descriptions and comments (Write/Preview), and an activity timeline
- **Milestones/sprints** with progress and story-point burn-up
- **Project settings**: rename, manage labels, delete the project
- **Dashboard**: your assigned work, all projects, recent activity

## Layout

| Path | Purpose |
|------|---------|
| `Data/Models.cs` | Entities: User, Project, Issue, Label, Milestone, Comment, Activity |
| `Data/AppDbContext.cs` | EF Core model configuration |
| `Data/SeedData.cs` | Creates the schema (`EnsureCreated`) and seeds demo data |
| `Services/IssueService.cs` | All data access and business logic (numbering, board ordering, activity log) |
| `Services/CurrentUser.cs` | Per-circuit "signed-in" user (demo user switcher) |
| `Services/Display.cs` | Formatting helpers and safe Markdown rendering (Markdig, raw HTML disabled) |
| `Components/Pages/Projects/` | Issue list, board, issue detail, new issue, milestones, settings |
| `Components/Shared/` | Avatar, label chip, status badge, Markdown editor, and more |

## Next steps

- **Auth:** replace `CurrentUser` with ASP.NET Core Identity. Right now anyone can act as any user through the top-bar switcher.
- **Migrations:** switch from `EnsureCreated` to EF Core migrations before the schema changes.
- **Real-time:** push board and issue updates to other open circuits (for example with a singleton event hub).
- **More features:** issue links and sub-tasks, attachments, @mentions and notifications, merge requests and repositories.
