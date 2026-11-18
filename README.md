# GitBench

A lightweight Jira/GitLab-style issue tracker built with **Blazor (Interactive Server)**, **.NET 10**, **EF Core** and **SQLite**.

## Run

```bash
dotnet run --launch-profile http
```

Open http://localhost:5108. On first run a `gitbench.db` SQLite file is created and seeded with demo users, projects and issues. Delete it to reset.

## Accounts & sign-in

Every page requires a signed-in user; anonymous requests are redirected to `/account/login`. Sign-in uses ASP.NET Core Identity with a cookie.

The seed data creates four demo accounts. All of them use the password in `SeedData.DemoPassword` (`Data/SeedData.cs`).

| Username | Email | Name |
|----------|-------|------|
| `alice` | alice@example.com | Alice Chen |
| `bob` | bob@example.com | Bob Martinez |
| `carol` | carol@example.com | Carol Okafor |
| `dan` | dan@example.com | Dan Kowalski |

- **Sign in** at `/account/login` with a username or an email. Five failed attempts lock an account for five minutes.
- **Register** at `/account/register`. New users get a random avatar color and are signed in right away.
- **Profile** at `/account/profile` (click your avatar in the top bar): change display name, email, avatar color and password.
- **Sign out** with the button in the top bar.

Account pages run as static server-side rendering (`[ExcludeFromInteractiveRouting]`) because sign-in has to write cookies; the rest of the app stays Interactive Server.

## Features

- **Projects** with a short key (`WEB`), and issues numbered per project (`WEB-12`)
- **Issues** with type (task, bug, feature, story, epic), status, priority, assignee, labels, milestone, story points and due date
- **Issue list** with open/closed tabs, search (including by key), filters and sorting
- **Kanban board** with drag-and-drop between and within columns, filtered by sprint or assignee
- **Issue detail** with inline editing, Markdown descriptions and comments (Write/Preview), and an activity timeline
- **Milestones/sprints** with progress and story-point burn-up
- **Project settings**: rename, manage labels, delete the project
- **Dashboard**: your assigned work, all projects, recent activity

## Cloning & pushing

Each repository is served over the Git Smart HTTP protocol at `/git/{PROJECTKEY}/{slug}.git`:

```bash
git clone http://localhost:5108/git/WEB/web-app.git
cd web-app
git push origin main
```

- **Credentials:** clone, fetch and push all require a GitBench account (repos are private). Enter your username (or email) and password when git prompts. In development the demo users `alice`, `bob`, `carol` and `dan` share the password defined in `SeedData.DemoPassword` (`Data/SeedData.cs`). Use a credential helper (`git config credential.helper osxkeychain`/`manager`/`store`) rather than putting the password in the URL. Failed logins count toward Identity lockout.
- **Requirements:** the server runs `git http-backend`, so `git` must be on the server's `PATH` (or set `Git:Executable` in `appsettings.json`). If git is missing, the endpoints answer `503` and the error is logged at startup.
- Protocol v2 (`git -c protocol.version=2 ...`) is supported; the legacy "dumb" HTTP protocol is not.
- Repositories live as bare repos under `Git:RepositoryRoot` (default `./repos`).
- **Smart commits:** after each push, commits whose message mentions an issue key (e.g. `WEB-5: virtualize the list`) are added to that issue's activity and show in its Development panel (`Services/Git/IssueCommitPushListener.cs`). Each commit is linked once per issue.
- Code can react to pushes by registering an `IGitPushListener` (`Services/Git/GitHttpPushListener.cs`) in DI.

## AI for issues

Optional Claude-powered helpers (model `claude-opus-5-5` via the official Anthropic C# SDK). Set `ANTHROPIC_API_KEY` (or `Ai:ApiKey`) to turn them on; without a key every AI control is hidden and the new-issue page shows a one-line hint. AI never changes data by itself: each feature returns text or filters that you review and apply.

| Where | Feature |
|-------|---------|
| New issue | **✨ Draft with AI**: type a rough sentence or paste an error log; Claude fills title, Markdown description (repro steps / expected / actual for bugs, acceptance criteria for features), type, priority, labels (only existing project labels) and story points. Nothing is created until you click *Create issue*. |
| New issue | **Possible duplicates**: when the title loses focus with 4+ words (or after drafting), open issues are prefiltered by keyword overlap (top 25, no AI call), then Claude picks likely duplicates with a one-line reason. No candidates means no AI call. |
| Issue detail | **✨ Catch me up**: a streamed summary of the description, comments and activity (current state, decisions, open questions, next steps). Cached in memory per issue until the issue's `UpdatedAt` changes. Labelled *AI-generated* with a timestamp. |
| Issue list | **✨ Ask**: plain-English search ("my open high priority bugs in sprint 2 with the ui label"). Claude returns structured filters, which are mapped to the normal filter bar with fuzzy matching against real people, labels and milestones (`me`/`my` = you, `unassigned`, `no milestone`, closed/all). Each applied filter appears as a removable chip; unknown names fall back to text search. |
| Milestones | **✨ Release notes**: Markdown grouped into Features / Fixes / Other from the milestone's closed issues and merged PRs that mention their keys, in a dialog with preview and a copyable Markdown box. |

Implementation: `Services/Ai/IssueAiService.cs` (data gathering and Claude calls; every call records usage through `AiClient`) and `Services/Ai/IssueAiLogic.cs` (pure prompt building, response parsing and filter mapping). UI lives in `Components/Ai/`. Search and drafting run at `low` effort, summaries at `medium`; JSON answers use structured outputs, and refusals or truncated answers become a friendly message.

**Prompt-injection hygiene:** issue text is untrusted. It is placed inside delimited data tags (`<description>`, `<comment>`, `<query>`…, with look-alike tags in the content neutralised), and the shared cached system prompt tells Claude to treat that content as data and never follow instructions in it. Model output is rendered only through `Display.Markdown` (raw HTML disabled) or as plain text.

## Layout

| Path | Purpose |
|------|---------|
| `Data/Models.cs` | Entities: User, Project, Issue, Label, Milestone, Comment, Activity |
| `Data/AppDbContext.cs` | EF Core model configuration |
| `Data/SeedData.cs` | Creates the schema (`EnsureCreated`) and seeds demo data |
| `Services/IssueService.cs` | All data access and business logic (numbering, board ordering, activity log) |
| `Services/CurrentUser.cs` | The signed-in `User` for the current circuit/request |
| `Components/Account/` | Login, register and profile pages (static SSR), `RedirectToLogin` |
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

- **Migrations:** switch from `EnsureCreated` to EF Core migrations before the schema changes.
- **Real-time:** push board and issue updates to other open circuits (for example with a singleton event hub).
- **More features:** issue links and sub-tasks, attachments, @mentions and notifications, merge requests and repositories.
