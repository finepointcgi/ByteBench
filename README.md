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

## Repositories & pull requests

Each project can host Git repositories, Bitbucket-style. Bare repos live on disk at `{Git:RepositoryRoot}/{KEY}/{slug}.git` (default `./repos`, see `Services/RepoStorage.cs`); the `Repository` table holds their metadata. All Git access goes through **LibGit2Sharp 0.32.0** (`Services/Git/GitOps.cs`), which works directly on the bare repos: object database, refs, diffs and in-memory merges, with no working tree.

| Route | Page |
| --- | --- |
| `/p/{KEY}/repos` | Repository list and "Create repository" (starts with a README commit on `main`) |
| `/p/{KEY}/repos/{slug}?ref=&path=` | Source browser: branch/tag picker, breadcrumbs, file tree with last commit per entry, file viewer, rendered README, clone URL |
| `/p/{KEY}/repos/{slug}/commits?ref=&path=&page=` | Commit history, optionally for one path |
| `/p/{KEY}/repos/{slug}/commits/{sha}` | Commit detail with unified diff |
| `/p/{KEY}/repos/{slug}/branches` | Branches with ahead/behind against the default branch; create, delete, "Create PR" |
| `/p/{KEY}/pull-requests?state=&repo=` | Pull requests across the project's repos (Open / Merged / Declined) |
| `/p/{KEY}/repos/{slug}/pull-requests` | The same list, scoped to one repository |
| `/p/{KEY}/pull-requests/new?repo=&source=&target=` | New PR with a commit and diff preview and a conflict check |
| `/p/{KEY}/repos/{slug}/pull-requests/{n}?tab=` | PR detail: overview and timeline, commits, files with inline comments, approvals, merge, decline, reopen |

Refs and paths are passed as query parameters because branch names contain `/`.

**Merging** happens inside the bare repo. GitBench computes the merge tree in memory; if there are conflicts nothing is written and the conflicting files are listed. Otherwise it writes a two-parent merge commit, or fast-forwards when "Fast-forward if possible" is selected and the target hasn't moved. The PR stores the source and target tips at merge or decline time, so its diff stays viewable after the source branch is deleted.

**Issue linking (Jira-style):** issue keys such as `WEB-3` in PR titles, source branch names and commit messages link to issues. A PR lists its linked issues. Each issue's sidebar shows a **Development** panel with the branches, PRs and recent commits that mention it. Merging a PR adds a "merged pull request web-app#N" entry to each linked issue's activity.

The demo seed (`Data/RepoSeeder.cs`) creates `WEB/web-app` with history on `main`, a `feature/WEB-3-kanban-dnd` branch, and an open, reviewed PR #1. It runs only when the `web-app` row doesn't exist. If you delete `gitbench.db*` and a stale `repos/WEB/web-app.git` is left over, the seeder rebuilds it.

## Next steps

- **Auth:** replace `CurrentUser` with ASP.NET Core Identity. Right now anyone can act as any user through the top-bar switcher.
- **Migrations:** switch from `EnsureCreated` to EF Core migrations before the schema changes.
- **Real-time:** push board and issue updates to other open circuits (for example with a singleton event hub).
- **More features:** issue links and sub-tasks, attachments, @mentions and notifications, merge requests and repositories.
