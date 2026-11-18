# GitBench

A lightweight Jira/GitLab-style issue tracker built with **Blazor (Interactive Server)**, **.NET 10**, **EF Core** and **SQLite**.

## Run

```bash
dotnet run --launch-profile http
```

Open http://localhost:5108. On startup, EF Core migrations are applied to `gitbench.db` (SQLite) and, on an empty database, demo users, projects and issues are seeded. Delete `gitbench.db*` and `repos/` to reset.

**Schema changes:** edit the model, then `dotnet ef migrations add <Name> -o Data/Migrations` (the `dotnet-ef` tool is pinned in `dotnet-tools.json`; run `dotnet tool restore` once). Migrations apply automatically on the next start.

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

## Roles & permissions

Every project has members, each with one role. `Services/ProjectAccess.cs` makes every access decision. The services (`IssueService`, `RepositoryService`, `PullRequestService`, `MemberService`) check it at the start of each write and throw `ForbiddenException`. Reads only return projects the user can view. The UI hides or disables what you can't do, but the server enforces it either way.

| Permission | Viewer | Developer | Maintainer | Owner |
|---|:-:|:-:|:-:|:-:|
| ViewProject | ✓ | ✓ | ✓ | ✓ |
| EditIssues (create/edit issues, board moves) | | ✓ | ✓ | ✓ |
| Comment | | ✓ | ✓ | ✓ |
| PushCode (git push, create branches) | | ✓ | ✓ | ✓ |
| CreatePullRequest | | ✓ | ✓ | ✓ |
| ReviewPullRequest | | ✓ | ✓ | ✓ |
| ManageLabelsAndMilestones (also sprints: create, edit, start, complete, delete) | | | ✓ | ✓ |
| MergePullRequest | | | ✓ | ✓ |
| DeleteBranch | | | ✓ | ✓ |
| ManageRepositories | | | ✓ | ✓ |
| ManageMembers | | | | ✓ |
| ManageProjectSettings (name, description, visibility) | | | | ✓ |
| DeleteProject | | | | ✓ |

Some actions have extra rules:

- Deleting an issue, or editing or deleting another person's issue comment, requires Maintainer.
- A pull request's author can edit, decline and reopen it. Anyone else needs MergePullRequest.

The project creator becomes its **Owner**. A project's last Owner can't be removed or demoted. **Site admins** (`User.IsAdmin`) are treated as Owner of every project.

**Visibility** is set on the project's Settings page:

- **Private** projects are visible only to their members.
- **Internal** projects are visible to every signed-in user. Non-members get Viewer rights.

If you can't view a project, its pages show "Not found", so its existence isn't revealed.

Manage members at `/p/{KEY}/members`. Owners can add a member by username or email, change roles, and remove members.

Seeded demo data:

| User | Site | WEB (Internal) | OPS (Private) |
|---|---|---|---|
| alice | admin | Owner | Owner |
| bob | | Developer | |
| carol | | Maintainer | Developer |
| dan | | Developer | Maintainer |

**Using the API from other code:**

- Inject `ProjectAccess` and call `CanAsync(projectId, Permission.X, userId?)`, `RequireAsync(...)`, `GetRoleAsync(...)` or `VisibleProjectIdsAsync(userId?)`.
- With no `userId`, the signed-in user is used.
- Outside a circuit (HTTP endpoints, background jobs), pass `userId` or call `access.ActAs(userId)` before calling services. For trusted system work, call `access.ActAsSystem()`.

## Features

- **Projects** with a short key (`WEB`), and issues numbered per project (`WEB-12`)
- **Issues** with type (task, bug, feature, story, epic), status, priority, assignee, labels, milestone, story points and due date
- **Issue list** with open/closed tabs, search (including by key), filters and sorting
- **Kanban board** with drag-and-drop between and within columns, filtered by sprint or assignee
- **Issue detail** with inline editing, Markdown descriptions and comments (Write/Preview), and an activity timeline
- **Milestones** with progress and story-point burn-up
- **Sprints**: backlog planning, start/complete, burndown and velocity reports (see [Sprints](#sprints))
- **Project settings**: rename, manage labels, delete the project
- **Dashboard**: your assigned work, all projects, recent activity

## Sprints

Scrum-style sprints per project (`Sprint` in `Data/Models.cs`, `Issue.SprintId`; logic in `Services/SprintService.cs`).

- **States:** Planned → Active → Completed. At most one sprint per project is Active; a filtered unique index (`IX_Sprints_OneActivePerProject`) enforces it in the database too.
- **Permissions:** creating, editing, starting, completing and deleting sprints needs `ManageLabelsAndMilestones` (Maintainer+). Moving issues in and out of sprints and ranking them needs `EditIssues` (Developer+).
- **Backlog** (`/p/{KEY}/backlog`, sidebar **🏃 Backlog**): the active sprint, the planned sprints, then the backlog (open issues in no sprint). Each section shows its issue count and story points. Drag issues between sections or within one to rank them (ranking reuses `Issue.SortOrder`, the same order as the board). Each row also has a **Move…** menu (top, up, down, bottom, or another sprint) for keyboards and touch screens.
  - **+ Create sprint** creates one inline ("WEB Sprint 9" is suggested).
  - **Start sprint** asks for dates and a goal, and snapshots the committed scope (`CommittedCount`/`CommittedPoints`).
  - **Complete sprint** shows the done and open counts, snapshots what was done (`CompletedCount`/`CompletedPoints`), and moves the open issues to a planned sprint, a new sprint, or the backlog. Done issues stay in the completed sprint.
  - Deleting a planned or active sprint sends its issues back to the backlog. Completed sprints are kept for reports.
- **Board:** a sprint selector (defaults to the active sprint) with an "All issues" option. **Issue detail:** a Sprint picker in the sidebar (`Components/Sprints/IssueSprintField.razor`). **Issue list:** a sprint filter.
- **Reports** (`/p/{KEY}/reports`, sidebar **📈 Reports**), drawn as hand-written inline SVG (`Components/Sprints/BurndownChart.razor`, `VelocityChart.razor`), each with hover tooltips and a data table. A toggle switches between story points and issue count.
  - **Burndown:** remaining work per day from the start date to the end date, against an ideal line. History is rebuilt from issue `Activity` rows by `Services/SprintHistory.cs`: starting from each issue's current state, it undoes status changes to/from Done, estimate changes and sprint membership changes, newest first. Sprint activities link to the sprint (`…/backlog#sprint-{id}`), so renaming a sprint doesn't break history. Estimate changes record the old value ("changed the estimate from **3** to **5** story points"). Unestimated issues count as 0 points.
  - **Velocity:** committed vs completed for the last 8 completed sprints, with the average completed.
- **Live updates:** sprint changes publish `SprintChanged` (plus `BoardChanged`, and `IssueChanged` for moved issues), so open backlogs, boards, reports and issue pages refresh. Moving an issue also fires the `issue.updated` webhook with the activity text.
- **API:** `GET /api/v1/projects/{key}/sprints` (`?state=`) and `/sprints/{id}`; issues carry `sprintId` and can be filtered with `?sprint={id}`, `none` or `active`.
- **Seed:** WEB has six completed sprints, an active one (WEB Sprint 7) and a planned one, with backdated activity so the charts have data (`Data/SprintSeeder.cs`; runs once, on a database whose WEB project has no sprints).

## Cloning & pushing

Each repository is served over the Git Smart HTTP protocol at `/git/{PROJECTKEY}/{slug}.git`:

```bash
git clone http://localhost:5108/git/WEB/web-app.git
cd web-app
git push origin main
```

- **Credentials:** clone, fetch and push all require a GitBench account. Enter your username (or email) and password when git prompts. In development the demo users `alice`, `bob`, `carol` and `dan` share the password defined in `SeedData.DemoPassword` (`Data/SeedData.cs`). Use a credential helper (`git config credential.helper osxkeychain`/`manager`/`store`) rather than putting the password in the URL. Failed logins count toward Identity lockout.
- **Permissions:**
  - Clone and fetch need ViewProject. Push needs PushCode (Developer or higher).
  - If you can't view the project, git gets `404 Repository not found`, the same as for a repository that doesn't exist.
  - A Viewer who tries to push gets `403`.
- **Requirements:** the server runs `git http-backend`, so `git` must be on the server's `PATH` (or set `Git:Executable` in `appsettings.json`). If git is missing, the endpoints answer `503` and the error is logged at startup.
- **Personal access tokens:** you can use a token instead of your password.
  - Create one under **Profile → Access tokens** (`/account/profile`). Give it a name, choose scopes, and pick an expiry: 7, 30, 90 or 365 days, or never.
  - The token looks like `gbp_` followed by 43 base64url characters. It's shown **once**. GitBench stores only its SHA-256 hash and a short display prefix.
  - When git prompts, enter your username and paste the token as the password: `git clone http://bob@localhost:5108/git/WEB/web-app.git`.
  - `read_repository` allows clone and fetch. `write_repository` allows push, and also clone and fetch. A token with only `read_repository` gets `403` on push.
  - Revoked or expired tokens are rejected like a wrong password. Token hashes are compared in constant time, and *Last used* is updated on each use.
  - Project permissions still apply on top of the token's scopes.
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
| `Data/SeedData.cs` | Applies migrations (`Data/Migrations`) and seeds demo data |
| `Services/IssueService.cs` | All data access and business logic (numbering, board ordering, activity log) |
| `Services/CurrentUser.cs` | The signed-in `User` for the current circuit/request |
| `Services/ProjectAccess.cs` | Roles, permissions and project visibility: the single authorization point |
| `Services/MemberService.cs` / `Services/AccessTokenService.cs` | Project membership; personal access tokens |
| `Api/` | REST API (`/api/v1`): endpoints, DTOs, auth filter, paging, OpenAPI |
| `Services/Webhooks/` | Outgoing webhooks: dispatcher, worker, sender (SSRF guard), push capture, notifier |
| `Components/Account/` | Login, register and profile pages (static SSR), `RedirectToLogin` |
| `Services/Display.cs` | Formatting helpers and safe Markdown rendering (Markdig, raw HTML disabled) |
| `Components/Pages/Projects/` | Issue list, board, issue detail, new issue, milestones, settings |
| `Components/Shared/` | Avatar, label chip, status badge, Markdown editor, and more |
| `Services/SprintService.cs` / `Services/SprintHistory.cs` | Sprints, backlog ranking, reports; activity texts and burndown replay |
| `Components/Sprints/` | Sprint picker and filters, burndown and velocity SVG charts |

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

## Notifications & live updates

**Notifications** (`Services/NotificationService.cs`, `Notification` entity) show in the 🔔 bell in the top bar, which has a live unread count and a dropdown with the latest 10. Click one to mark it read and open it, or use **Mark all read**. `/notifications` lists all of them, with All/Unread tabs and pagination.

| Event | Who is notified |
|-------|-----------------|
| Issue assigned (on create or change) | The new assignee |
| `@username` in an issue description, issue comment, PR description or PR comment | That user |
| Comment, status change, close or reopen on an issue | The issue's watchers |
| Comment, review, merge, decline or reopen on a PR | The PR's watchers. Reviews also always go to the PR author |

- The person who acted is never notified, and each user gets at most one notification per event. Assignment and mention take priority over the watcher notification.
- Every recipient list goes through `NotificationService.FilterRecipientsAsync(projectId, userIds)`, the single place to restrict notifications to users who can see the project.
- Edited descriptions notify only users who are newly mentioned. Edited comments don't notify anyone.

**Watching** (`Watch` entity: a user plus exactly one issue or PR). The reporter, the assignee and every commenter auto-watch an issue. The author, reviewers and commenters auto-watch a PR. The **Watch / Watching** button on the issue and PR pages toggles it. On a fresh database, existing seeded issues and PRs are backfilled at startup.

**Mentions** (`Services/Mentions.cs`) are a Markdig inline parser. Mentions are matched case-insensitively against existing usernames and are never recognized inside code spans or code blocks. Known users render as a highlighted `<span class="mention">`. Raw HTML stays disabled, so text like `@<script>` is escaped.

**Live updates** (`Services/LiveEvents.cs`, a singleton in-process hub). Services publish typed events after saving: `IssueChanged(projectId, issueId)`, `BoardChanged(projectId)`, `PullRequestChanged(prId, projectId)`, `NotificationsChanged(userId)` and `ActivityChanged`. The board, issue list, issue detail, PR list, PR detail, dashboard activity feed, notifications page and bell subscribe once interactive (`OnAfterRender(firstRender)`), refresh with `InvokeAsync(StateHasChanged)`, and unsubscribe in `Dispose`. For example, two browsers on the same board see a card move without reloading. A refresh replaces only the loaded data, so a description, title or comment you are editing is left alone. The hub is in-process, so a multi-server deployment would need a backplane such as Redis or the SignalR backplane.

```csharp
// Publish from any service, after SaveChangesAsync:
live.Publish(new IssueChanged(projectId, issueId), new BoardChanged(projectId));
// Subscribe in a component (dispose in Dispose()):
sub = Live.Subscribe<IssueChanged>(e => e.IssueId == id ? InvokeAsync(ReloadAndRender) : Task.CompletedTask);
```


## AI code review

GitBench AI reviews pull requests with Claude (official Anthropic C# SDK, model `claude-opus-5-5` by default, effort `high`). Its comments come from a bot account, `gitbench-ai` ("GitBench AI"), which is created at startup and has no password, so nobody can sign in as it. Its comments and reviews carry an **AI** badge.

**Setup.** Provide an API key in either of these ways:

```bash
export ANTHROPIC_API_KEY=sk-ant-...                 # environment variable
# or user-secrets (run `dotnet user-secrets init` once if the project has no UserSecretsId yet):
dotnet user-secrets set "Ai:ApiKey" "sk-ant-..."
```

Without a key, every AI control hides itself or shows "AI review isn't set up (set ANTHROPIC_API_KEY)", and nothing is queued.

**What it does**

- **Triggers:** a PR is created; new commits are pushed to an open PR's source branch (debounced by 30 seconds, so a burst of pushes is reviewed once); or someone clicks **Ask GitBench AI to review** / **Re-run AI review** in the PR sidebar. A head commit is reviewed only once unless someone re-runs it.
- **Input:** the PR title and description, its commit messages, and the merge-base diff with file headers and new-side line numbers.
- **Large diffs are never cut silently.** Binary files, lockfiles (`package-lock.json`, `yarn.lock`, …), minified/generated files and build output are skipped. Diffs over about 120k characters are reviewed in per-file batches (files that are too big are split at hunk boundaries), up to 6 batches. The summary lists everything skipped or not reviewed.
- **Output:** structured JSON (summary, findings with file, line, severity `bug`/`security`/`suggestion`/`nit`, title and body, plus a verdict). Each finding is checked against the diff. Valid ones become inline comments. A finding a few lines off is moved to the nearest changed line and marked as moved. Findings that can't be placed are listed in the summary comment.
- **Verdict:** if there is a blocking bug or security finding, the bot records **Changes requested**. Otherwise it posts comments only, and a clean re-review withdraws its earlier request for changes. It **never approves**.
- **Status:** the PR sidebar shows "AI review: queued… / running… / completed, N findings / failed (error)" and updates live. Reviews run one at a time in a background worker (`AiReviewWorker`), and a failure is shown with its error. Each run is stored in the `AiReviews` table.
- **PR descriptions:** on the New PR page, **Write description with AI** streams a description from the commits and diff into the editor. You can edit it before creating the PR.

**Cost.** Every Claude call is logged in the `AiUsages` table, with feature (`pr-review` and `pr-description`), model, project, and input, output and cache tokens. The reviewer's system prompt is fixed, so it is served from the prompt cache. A review makes one call per batch, which is one call for most PRs.

```bash
sqlite3 gitbench.db "select Feature, count(*), sum(InputTokens), sum(OutputTokens), sum(CacheReadTokens) from AiUsages group by Feature"
```

**Turning it off**

- Per project: **Settings → AI code review** turns off automatic reviews. Manual "Ask GitBench AI to review" still works.
- Everywhere: set `"Ai": { "Enabled": false }` in `appsettings.json`, or remove the API key.

Code: `Services/Ai/AiReviewService.cs` (orchestration and streaming descriptions), `AiReviewLogic.cs` (pure functions: skipping, chunking, prompts, validation, summary), `AiReviewQueue.cs` (queue, worker, push listener), `AiClient.Structured.cs` (generic `CompleteJsonAsync<T>` structured-output helper) and `AiBot.cs`.


## Branch protection, reviewers & PR templates

**Branch protection rules** are set per repository at `/p/{KEY}/repos/{slug}/settings/branches` (Maintainer+, under the repo's **Settings** tab → **Branch protection**). A rule matches branches by name: an exact name (`main`) or a glob (`release/*`; `*` and `?` stay within one path segment, `**` crosses `/`). When several rules match, the strictest setting wins. Protected branches show a 🔒 badge on the Branches page.

| Setting | Effect |
|---|---|
| Required approvals (0–10) | Merging needs this many distinct approvals from people other than the author |
| Block direct pushes | The branch changes only through PR merges: git pushes and creating matching branches are refused |
| Block force pushes | Non-fast-forward pushes are refused |
| Block deletion | Deleting via git push, the Branches page or "delete source branch after merge" is refused |
| Require checks to pass | Any merge check (e.g. a pipeline) that is Pending or Failed blocks merging. Neutral never blocks |
| Dismiss approvals when new commits are pushed | Only approvals on the PR's current head count; a push to the source branch removes older ones |
| Allow admins to bypass | Site admins and project Owners are exempt (off by default). A bypassed merge is noted in the PR timeline |
| Default reviewers | Added to new PRs that target a matching branch |

"Changes requested" by anyone always blocks merging into a protected branch. Reviews record the head commit they were given on (`PullRequestReview.CommitSha`).

**Where it's enforced:**
- **Git push over HTTP.** `Services/Git/ProtectedPushGuard.cs` reads the receive-pack ref update commands before handing the request to `git http-backend`. It refuses deletes, creates and direct pushes with a proper report-status `ng` line plus a sideband message, so git prints `! [remote rejected] main -> main (branch 'main' is protected: changes must go through a pull request)`. A force push can only be detected once the pack has arrived, so for those refs a tiny `pre-receive` hook (written to `{RepositoryRoot}/.hooks`, enabled for that request only via `core.hooksPath`) runs `git merge-base --is-ancestor old new`. Requires git ≥ 2.31 (`GIT_CONFIG_COUNT`).
- **Branch create/delete** in `RepositoryService`, and **PR merge** in `PullRequestService.MergeAsync`, which refuses with the list of reasons.

**Merge checks.** `Services/MergeGate.cs` collects every registered `IMergeCheck` (`Services/MergeChecks.cs`) plus the approval and change-request results, and decides whether a PR can be merged. The PR page's merge box lists every check with its state, and the Merge button is disabled while blocked (it reads "Merge (bypass protection)" for admins who may bypass). To add a check, register `services.AddScoped<IMergeCheck, MyCheck>()`.

**Assigned reviewers.** Pick reviewers on the New PR page (project members with ReviewPullRequest) or with **Edit reviewers** in the PR sidebar (the author or a Maintainer+). The sidebar shows each one as Pending, Approved or Changes requested. Added reviewers get a "requested your review on" notification and start watching the PR. The PR list's **Review requested** button (`?filter=review-requested`) shows open PRs waiting for your verdict.

**PR templates.** The New PR description is prefilled from the target branch's `.gitbench/pull_request_template.md`, falling back to `PULL_REQUEST_TEMPLATE.md`, then `.github/pull_request_template.md`.
## CI pipelines

Each repository can run a CI pipeline from a `.gitbench-ci.yml` file at its root. Jobs run in **Docker** containers on the GitBench server.

```yaml
image: alpine:3.20          # default image for jobs (alpine:3.20 if omitted)
on:                         # optional; default: every push and every pull request
  branches: [main, "feature/*"]   # `*` matches within one path segment, `**` across segments
  pull_requests: true
jobs:
  build:
    image: mcr.microsoft.com/dotnet/sdk:10.0   # optional per-job override
    steps:
      - name: Build
        run: dotnet build
  test:
    needs: [build]          # optional; jobs without needs run in parallel
    steps: [ { name: Test, run: ./test.sh } ]
```

- **Format.** `jobs` is required. Each job has `steps` (each with `run` and an optional `name`), plus optional `image` and `needs`. Job names use letters, digits, `_`, `.` and `-`. Limits: 20 jobs, 50 steps per job, 64 KB per file. Unknown keys, missing `run`, unknown or circular `needs` and invalid image names are errors: the pipeline is created in the **Error** state with a readable message, such as `invalid YAML at line 4, column 5: Property 'stepz' not found`.
- **Running a job.** Each job gets a fresh export of the commit's files, mounted at `/workspace`. Its steps run in order, each in its own container: `docker run --rm --entrypoint sh <image> -e -c "<run>"`. A multi-line `run` stops at its first failing command. The first failing step fails the job, and the job's later steps are skipped. A job whose `needs` didn't pass is skipped. Jobs don't share files, so a later job can't see what an earlier job built. Steps can read `CI`, `GITBENCH_PIPELINE_ID`, `GITBENCH_JOB`, `GITBENCH_REF`, `GITBENCH_COMMIT_SHA` and `GITBENCH_REPOSITORY`.
- **Triggers.**
  - A push runs a pipeline for each branch it created or moved, if the branch matches `on.branches` or is the source of an open PR. Deleted branches are skipped. A merge done in GitBench counts as a push to the target branch.
  - Opening a PR runs a pipeline for its source head, unless that commit already has one.
  - A commit runs at most once per branch automatically.
  - **Run pipeline** (Pipelines tab) runs any branch's tip and ignores the filters. **Re-run** runs the same commit again. Both need PushCode (Developer or higher), as does **Cancel**.
  - Pushes are noticed by `CiPushListener`. PR events reach `CiPullRequestWatcher` through `LiveEvents`.
  - Push listeners aren't told which refs moved, so GitBench remembers each repository's branch tips, starting from a snapshot taken at startup. A branch created in the UI is picked up by the next push.
- **Statuses.** A pipeline is Queued, Running, Passed, Failed, Canceled or Error. Jobs and steps can also be Skipped. Icons (✓ ✗ ● spinner ⊘) appear on the commit list, the branch list and the PR header.
- **Logs.** stdout and stderr stream into the database about once a second, and the pipeline page tails them live. Each step's log is capped at about 1 MB, with a note when it's truncated. The page shows the last 200 KB. **Raw log** opens the full text.
- **Pages.** `/p/{KEY}/repos/{slug}/pipelines` lists pipelines. `/p/{KEY}/repos/{slug}/pipelines/{id}` shows jobs, steps and logs, with Cancel and Re-run buttons. Everything needs ViewProject.
- **Merge check.** `PipelineMergeCheck` (an `IMergeCheck`) reports the latest pipeline on the PR's current source head: Passed, Pending (queued, running or none yet) or Failed (failed, canceled or error). It reports nothing if the head has no `.gitbench-ci.yml`.
- **Notifications.** When a pipeline fails or errors, the person who triggered it gets a notification.
- **Restarts.** Pipelines that were running when the server stopped are marked Error. Queued ones run again. This instance's leftover containers and workspaces are removed.
- **Docker missing.** If the docker CLI can't reach a daemon, pipelines end in Error with "Docker is not available on the server".

**Configuration** (`appsettings.json`, section `Ci`):

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Turns off every trigger and the Run button |
| `MaxConcurrentJobs` | `2` | Jobs that run at once, across all pipelines |
| `JobTimeoutMinutes` | `20` | A job's container is killed after this |
| `Network` | `bridge` | `docker run --network`. Use `none` to block network access |
| `Memory` / `Cpus` | `2g` / `2` | `--memory` / `--cpus` per container. Empty means no limit |
| `PidsLimit` | `1024` | `--pids-limit` |
| `DockerExecutable` | `docker` | Path of the docker CLI |
| `WorkDirectory` | `{temp}/gitbench-ci/{instance}` | Where workspaces are exported. Docker must be able to mount it |

### Security model

**Pipelines run arbitrary code from the repository on the server host, inside Docker.** Anyone who can push (Developer or higher) can run any command in any image they choose. Treat PushCode as "can run code on the CI host".

- The docker CLI is always started with an argument list, never a shell command line, and image names are validated so they can't be read as flags.
- Containers never get the Docker socket, host environment variables or any GitBench secret. They get only the `GITBENCH_*` variables above.
- Containers run with `--security-opt no-new-privileges`, memory, CPU and PID limits, a per-job timeout, and `--rm`.
- Only the exported workspace is mounted. Symlinks in the repository are created after every regular file, so nothing is written through them. Submodules aren't fetched.
- Containers still have network access by default (`bridge`). They run as the image's user, often root inside the container, and Docker isolation is not a hard security boundary. On a shared or exposed server, use `Network: none`, rootless Docker or a separate CI host, and let only trusted people push.

Code: `Services/Ci/` (`CiFile` parser, `CiTriggerService`, `CiRunner` worker, `CiDocker`, `PipelineService`, `PipelineMergeCheck`) and `Components/Pages/Pipelines/`. The seeded `WEB/web-app` repository has a sample `.gitbench-ci.yml`.

## Code tools

### Syntax highlighting

Code is highlighted in the browser with **highlight.js 11.11.1**, vendored in `wwwroot/lib/highlightjs/` (the cdnjs "common" bundle of ~35 languages plus Dockerfile, PowerShell and F#; checked against the cdnjs SRI hashes, never loaded from a CDN at runtime). It applies to:

- the file viewer (`RepoSource`),
- diffs (commit view, PR files tab, new-PR preview); the old side (context + deleted lines) and new side (context + added lines) are each highlighted as one document, so multi-line comments and strings work, and the +/- row colours stay,
- fenced code blocks with a language (```` ```csharp ````) in Markdown: issue and PR descriptions, comments, README rendering and the editor preview.

The language comes from the file extension or name (`Services/Code/CodeLanguages.cs`). Unknown types, and files or diffs over 200 KB, stay plain.

Why client-side: highlight.js is maintained, small and covers far more languages than the .NET options (ColorCode covers few languages; TextMateSharp needs native Oniguruma binaries). To stay safe under Blazor's DOM diffing, each code line is rendered as a `MarkupString` (`<span class="hl-line">…</span>`). Blazor tracks the span and replaces it whole when the line changes, and `wwwroot/js/code-tools.js` only rewrites the span's children. A single `MutationObserver` reruns the idempotent highlight pass after every render, so it survives re-renders, enhanced navigation and live updates with no per-component JS interop. Token colours are CSS variables in `wwwroot/css/code-tools.css`. The app has no dark mode yet; adding one only means overriding the `--hl-*` variables.

**Line links:** line numbers in the file viewer are links. `#L42` highlights and scrolls to a line, shift-click selects a range (`#L10-L20`), and the URL updates so it can be shared.

### Code search

| Route | Page |
| --- | --- |
| `/p/{KEY}/repos/{slug}/search?q=&ref=&case=1&regex=1` | Search one repository at a branch, tag or commit (default branch when `ref` is empty) |
| `/p/{KEY}/code-search?q=&case=1&regex=1` | Search the default branch of every repository in the project ("Code search" in the sidebar) |

Every repository page also has a search box in its header and a **Search** tab.

- **File contents:** `git --git-dir <bare> grep -n -I -z --fixed-strings` against the resolved commit. It is case-insensitive unless *Match case* is ticked. With *Regex* ticked, `--perl-regexp` is used, falling back to `--extended-regexp` if git was built without PCRE.
- **File names:** `git ls-tree -r -z --name-only`, matching the full path (up to 50).
- **Safety and limits:** git runs with `ProcessStartInfo.ArgumentList` (no shell), the query is passed after `-e` (so it can't be an option), `GIT_*` variables are cleared, there is a 10-second timeout (the process is killed), at most 50 matches per file, 200 per repository and 400 per project search, and queries of at most 200 characters.
- **Results** are grouped by file, with one line of context around each match. Matches are marked, and each line number links to `file#L{n}` at the searched ref.
- Everything requires **ViewProject**. Users who can't view the project get "Not found".

Code: `Services/Code/CodeSearchService.cs`, `Components/Repositories/SearchResults.razor`, `Components/Pages/Repositories/RepoSearch.razor` and `ProjectCodeSearch.razor`.

### Repository settings

`/p/{KEY}/repos/{slug}/settings` (the **Settings** tab, shown to Maintainers and Owners) requires **ManageRepositories**. The left-hand sub-nav (`Components/Repositories/RepoSettingsNav.razor`) has a *General* page; add further settings pages there.

- **General:** name, description and default branch (a dropdown of existing branches). Changing the default branch also points the bare repo's `HEAD` at it, so new clones check it out.
- **Rename** changes the slug and moves `{KEY}/{old}.git` to `{KEY}/{new}.git`. It refuses if a repository or anything on disk already has the new slug. The database update and the directory move run inside one transaction (if the commit fails, the directory is moved back). The old slug is stored in `RepositoryRedirects`:
  - Web URLs with the old slug (source, commits, branches, PRs, search, settings) redirect to the new one, keeping the query and `#L` anchor.
  - Git Smart HTTP at `/git/{KEY}/{old}.git` keeps serving the renamed repository, so existing clones keep fetching and pushing. Update remotes with `git remote set-url`.
  - Renaming back to a former slug removes that redirect. A real repository always wins over a redirect.
- **Archive / unarchive:** an archived repository is read-only and shows an **Archived** badge in its header and in the repository list.
  - `git push` fails with `remote error: This repository is archived and read-only…` (sent as an `ERR` packet so git prints it).
  - Creating, deleting and merging branches and pull requests is refused by the services and disabled in the UI. Clone, fetch, browsing and search still work.
- **Delete:** type the repository name to confirm. The database goes first: the repository row is deleted, and FK cascades remove its pull requests with their comments, reviews, activity, watches and AI reviews, plus its redirects. Notifications that link into the repository are also removed. Then the bare repo is moved to a `.trash-…` name and deleted. A disk failure after the database delete is logged and leaves only an unreferenced trash directory.

Code: `Services/RepositorySettingsService.cs`, `Services/Git/GitOps.CodeTools.cs`, `Components/Pages/Repositories/RepoSettings.razor`. Schema: `Repository.IsArchived` / `ArchivedAt` and the `RepositoryRedirect` entity (migration `CodeTools`).

## REST API & webhooks

### REST API (`/api/v1`)

Minimal APIs in `Api/`. A short reference page is at `/developers/api` (the **API** link in the top bar), and the full OpenAPI 3.1 document is at **`/api/v1/openapi.json`**.

**Authentication.** Send a personal access token as a bearer token. A signed-in browser session (cookie) works too, so the UI can call the API.

```bash
curl -H "Authorization: Bearer gbp_…" http://localhost:5108/api/v1/user
```

| Scope | Allows |
|---|---|
| `read_api` | GET requests |
| `write_api` | Every request (also reads) |
| `read_repository` / `write_repository` | Git over HTTP only; they don't grant API access |

- Create tokens under **Profile → Access tokens** and tick the API scopes. A token without an API scope gets `403` from the API, and an API-only token gets `403` from git.
- Missing, invalid, expired or revoked tokens get `401` with `WWW-Authenticate: Bearer`.
- Cookie-authenticated writes must send `Content-Type: application/json` or an `X-Requested-With` header. A cross-site HTML form can do neither, so this blocks CSRF.

**Permissions** are the usual project roles, checked through `ProjectAccess` and the services:

- A project you can't see answers `404`, the same as a project that doesn't exist.
- A visible project where you lack the permission answers `403`. For example, a Viewer creating an issue, or a Developer merging.

**Conventions:**

- JSON is camelCase. Enums are camelCase strings (`inProgress`, `merged`). Timestamps are ISO 8601 UTC and due dates are `yyyy-MM-dd`.
- Responses are DTO records (`Api/ApiDtos.cs`); EF entities are never serialized.
- Errors are RFC 9457 problem details (`application/problem+json`): `400` bad input or JSON, `401`, `403`, `404`, `409` merge conflict or already merged, `415` non-JSON body, `422` validation.
- Lists take `?page=` (default 1) and `?perPage=` (default 30, max 100). They return `X-Total-Count` and a `Link` header with `first`/`prev`/`next`/`last`.

| Method | Path (under `/api/v1`) | Notes |
|---|---|---|
| GET | `/user` | You, with the token's scopes |
| GET | `/projects`, `/projects/{key}` | Includes your role |
| GET | `/projects/{key}/issues` | `?status=open` (default), `closed`, `all` or a status category; `?workflowStatus=` a workflow status id (see `/workflow`; without `?status=` it matches open and closed issues); `?assignee=` username or `none`; `?label=`; `?milestone=` id or `none`; `?sprint=` id, `none` or `active`; `?q=`; `?sort=updated` (default), `created`, `priority`, `due` or `number` |
| POST | `/projects/{key}/issues` | `{title, description?, type?, priority?, status?, assignee?, labels?: [names], milestoneId?, storyPoints?, dueDate?}`; returns `201` |
| GET / PATCH | `/projects/{key}/issues/{number}` | PATCH changes only the fields you send; `null` clears a field and `labels` replaces the set. Each change is logged in the issue's activity |
| GET / POST | `/projects/{key}/issues/{number}/comments` | `{body}` |
| GET | `/projects/{key}/labels`, `/projects/{key}/milestones` | |
| GET | `/projects/{key}/sprints`, `/projects/{key}/sprints/{id}` | `?state=planned`, `active` or `completed`. Counts, points and the committed/completed snapshots |
| GET | `/projects/{key}/repos`, `/projects/{key}/repos/{slug}` | |
| GET | `/projects/{key}/repos/{slug}/branches` | |
| GET | `/projects/{key}/repos/{slug}/commits` | `?ref=` branch, tag or SHA (default branch if omitted), `?path=` |
| GET | `/projects/{key}/repos/{slug}/raw/{path}` | `?ref=`. Served as `text/plain` or `application/octet-stream` (never HTML), with an `ETag`. 20 MB limit |
| GET | `/projects/{key}/pull-requests` | Across repositories; `?state=open` (default), `merged`, `declined` or `all`; `?repo=` |
| GET / POST | `/projects/{key}/repos/{slug}/pull-requests` | POST `{title, sourceBranch, targetBranch, description?}` |
| GET | `/projects/{key}/repos/{slug}/pull-requests/{number}` | |
| POST | `/projects/{key}/repos/{slug}/pull-requests/{number}/merge` | Optional `{deleteSourceBranch, fastForward}`. Conflicts return `409` with `conflictPaths` |
| GET / POST | `/projects/{key}/repos/{slug}/pull-requests/{number}/comments` | `{body, path?, line?, oldLine?}` (with `path`, it's an inline comment) |

Writes go through `IssueService` and `PullRequestService`, so activity, notifications, live updates, AI review and webhooks behave exactly as in the UI. Links in responses use the request's host, or the `PublicBaseUrl` setting if it is set.

### Webhooks

Each project can POST events to other services. Maintainers and Owners manage webhooks at **Settings → Webhooks** (`/p/{KEY}/settings/webhooks`).

They require `Permission.ManageRepositories`. Webhooks are integration settings at the same level as repositories (GitLab also gives them to Maintainers), and payloads only contain data that a Maintainer can already read.

**On the settings page** you can:

- Set a URL, a secret, the events and an Active flag.
- Leave the secret blank and GitBench generates one. The secret is shown once and masked afterwards. **New secret** rotates it.
- **Secrets are encrypted at rest** with ASP.NET Core Data Protection (purpose `GitBench.Webhooks.Secret.v1`, `Services/Webhooks/WebhookSecrets.cs`). The stored value is `dp:` + ciphertext, and only the delivery worker decrypts it to sign; the settings page gets a masked value. Secrets saved before encryption (no `dp:` prefix) still work and are encrypted at the next start. If the Data Protection keys are lost, stored secrets can't be decrypted: deliveries fail with a message saying so, and **New secret** fixes the hook.
- Use **Send test ping**.
- Look at the last 25 deliveries: status, duration, attempts, the payload and the first 4 KB of the response. Each has a **Redeliver** button, which resends the same body under the same delivery id with `X-GitBench-Redelivery: true`.

| Event | When |
|---|---|
| `push` | A git push moved a ref, or a pull request was merged in GitBench (UI or API): the merge sends one for the target branch and, if "delete source branch" was ticked, a `deleted: true` one for the source branch. One event per ref: `ref`, `before`, `after`, `created`, `deleted`, `repository`, `commits` (newest first, up to 20), `totalCommits`, `headCommit`, `compareUrl` |
| `issue.created` / `issue.updated` / `issue.commented` | `issue` (plus `change`, the activity text, for updates; plus `comment` for comments) |
| `pull_request.opened` / `.merged` / `.declined` | `pullRequest` and `repository` |
| `pull_request.updated` | Same, plus `action`: `edited`, `reopened`, `approved`, `changes_requested` or `review_dismissed` |
| `pull_request.commented` | `pullRequest`, `repository` and `comment` |
| `pipeline.completed` | Raised by CI code, not by GitBench itself (see below) |
| `ping` | Only from **Send test ping**. Always sent, even to an inactive hook |

**Payload and headers.** Every body is JSON that starts with `event`, `deliveryId`, `timestamp`, `project` and `sender`, followed by the event's own fields. Each request carries these headers:

- `X-GitBench-Event`
- `X-GitBench-Delivery` (a GUID)
- `X-GitBench-Hook-ID`
- `X-GitBench-Signature-256: sha256=<hex HMAC-SHA256(secret, raw body)>`

Verify the signature on the raw body bytes and compare in constant time:

```python
import hashlib, hmac

def verify(secret: str, body: bytes, header: str) -> bool:
    expected = "sha256=" + hmac.new(secret.encode(), body, hashlib.sha256).hexdigest()
    return hmac.compare_digest(expected, header or "")
```

```csharp
var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));
var ok = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(header ?? ""));
```

**Delivery** (`Services/Webhooks/`):

- Events are written to an in-memory `Channel` and sent by a background worker (`WebhookWorker`), so a request never waits for a receiver. Up to 8 deliveries run in parallel.
- Each attempt has a 10 s timeout. Network errors, timeouts and 5xx responses are retried up to 3 times, after 1 s, 5 s and 15 s.
- 4xx responses, redirects (which are never followed) and blocked addresses fail at once.
- Every delivery is stored in `WebhookDeliveries` with its status code, duration, attempts, error and the first 4 KB of the response.
- After 20 failed deliveries in a row, the hook is turned off and the page shows *Disabled after 20 failures*. Ticking **Active** again resets the counter.
- Deliveries left pending by a restart are resent on startup.

**SSRF protection:**

- Only `http`/`https` URLs are accepted, without credentials in the URL.
- DNS is resolved when the request is sent. The socket connects only to an address that passed the check, so DNS rebinding can't swap it.
- These are rejected: loopback, private (RFC 1918, ULA), link-local (including the `169.254.169.254` metadata address), CGNAT, multicast/reserved, IPv4-mapped/NAT64/6to4 forms of those, and `localhost`.
- No proxy is used and redirects aren't followed.
- To allow private targets, set `Webhooks:AllowPrivateNetworks` to `true`. It defaults to `false`. `appsettings.Development.json` turns it on so you can test against a local listener. Leave it off in production.

**Raising events from other code.** `WebhookDispatcher` is a singleton and its `Enqueue` method is the public entry point. It never blocks and never throws for delivery problems:

```csharp
dispatcher.Enqueue(projectId, "pipeline.completed", new { pipeline = new { id, status, @ref, sha, webUrl } }, senderUserId);
```

The payload is serialized with the API's camelCase settings and merged into the envelope. `IssueService` and `PullRequestService` call `WebhookNotifier` (one line per event). Pushes are handled by `WebhookPushListener` (an `IGitPushListener`), which reads the ref updates that `WebhookPushCaptureMiddleware` copies from the start of the `git-receive-pack` request body. Both it and `PullRequestService.MergeAsync` hand ref updates to `WebhookPushEvents.SendAsync`, which builds the payload after checking that each ref really is at `after`. Other push listeners can inject `PushCapture` to get them too.

## Linked work: sub-tasks, epics and issue links

**Hierarchy.** `Issue.ParentId` links an issue to a parent in the same project, up to three levels: **Epic › Story/Task/Bug/Feature › Sub-task** (`IssueType.Subtask`).

- An epic has no parent; its children can be any type except Epic and Subtask.
- A sub-task must have a parent that is neither an epic nor a sub-task, and has no children of its own.
- Other types may have an epic as parent, or none; their children can only be sub-tasks.
- No cycles. The rules live in `Services/HierarchyRules.cs` and `IssueService` checks them on every create and update (UI and API alike), so changing a type can be refused too (e.g. turning a story with sub-tasks into an epic).
- Deleting an issue deletes its sub-tasks (the UI says how many first). An epic's children are kept and unlinked.

**Issue links.** `IssueLink` stores Source → Target with a type: **blocks**, **relates to**, **duplicates** or **clones**. Each side shows its own phrase ("blocks" / "is blocked by", "duplicates" / "is duplicated by"). Links can cross projects, but only to issues you can view, and links to issues you can't view are hidden. Self-links and a second link of the same type between the same two issues (in either direction) are refused.

**In the UI** (`Components/LinkedWork/`, styles in `wwwroot/css/linked-work.css`):

- Issue detail: a parent breadcrumb above the title, a **Parent** field with a search-as-you-type picker (`IssuePicker`, reused for links), a **Child issues** / **Sub-tasks** panel (progress by count and points, inline create, add existing, remove) and a **Linked issues** panel grouped by relationship with each issue's status.
- Adding a "duplicates" link offers to close the issue. An issue with an open "is blocked by" link shows ⛔ on its board card, list row and links panel. Moving it to In Progress or Done still works, but shows a warning.
- Issue list: the type filter includes Sub-task, plus an **epic** filter (its children and their sub-tasks) and **Hide sub-tasks**. Board cards show the parent key and child progress (☑ 2/5).
- **Epics** page (`/p/{KEY}/epics`, in the sidebar): each epic with its child count, points and progress. New issues can start with a parent: `/p/{KEY}/issues/new?parent=WEB-9`.
- Parent changes and link changes are written to the activity of every issue involved (both ends of a link) and notify their watchers (`IssueHierarchyChanged`, `IssueLinked`).

**API.** Issues carry `parentId` (the parent's issue id). `POST /issues` and `PATCH /issues/{number}` accept `parentId` (`null` removes it); rule violations return `422`.

| Method | Path (under `/api/v1/projects/{key}/issues/{number}`) | Notes |
|---|---|---|
| GET | `/links` | `[{id, type, issue: {id, key, number, project, title, type, status, webUrl}, createdAt}]`; `type` is from this issue's side: `blocks`, `isBlockedBy`, `relatesTo`, `duplicates`, `isDuplicatedBy`, `clones`, `isClonedBy` |
| POST | `/links` | `{type, issue: "OPS-2"}`; returns `201`. Needs EditIssues here and view access to the other issue |
| DELETE | `/links/{linkId}` | `204` |
| GET | `/children` | Child issues (an epic's issues, or sub-tasks) as issue objects |

Code: `Services/LinkedWorkService.cs`, `Services/HierarchyRules.cs`, `Services/LinkedWorkNotifications.cs`, `Api/LinkEndpoints.cs`. Schema: `Issue.ParentId` and `IssueLink` (migration `LinkedWork`). The seed makes WEB "Public release" an epic with three children, gives the Kanban story two sub-tasks, and has "Avatar images broken on Safari" block "Dark mode".

## Attachments, saved filters & bulk edit

### Attachments

Files can be attached to issues from the **Attachments** panel on the issue page, or by picking (📎 Attach), dragging or pasting files into the description or a comment editor, which uploads them to the issue and inserts `![name](url)` for images or `[name](url)` for other files at the cursor. The panel shows image thumbnails (click for a lightbox; arrow keys step through images), file icons, size and uploader.

- **Permissions:** uploading needs EditIssues (Comment for uploads from a comment box). The uploader can delete their own files, and Maintainers+ can delete anyone's. Uploads and deletes are recorded in the issue's activity.
- **Storage:** bytes are streamed (never buffered whole) to `Attachments:Root` (default `{ContentRoot}/attachments/`, git-ignored) under a random 32-hex id; the user's filename is never part of a path. The database keeps the sanitized original name, size, content type, SHA-256, uploader and time. Deleting an issue (or many, or a project) deletes the rows first, then the files.
- **Limits:** `Attachments:MaxBytes` (default 25 MB per file) and `Attachments:MaxPerIssue` (default 50).
- **Download:** `GET /attachments/{id}/{fileName}` needs a signed-in user who can view the project (otherwise 401/404). Responses carry `X-Content-Type-Options: nosniff` and `Content-Security-Policy: sandbox; default-src 'none'; …`. Only png, jpeg, gif and webp are served inline, and only when the file's bytes matched that type at upload; everything else (SVG, HTML, PDF, …) is sent as `application/octet-stream` with `Content-Disposition: attachment`.
- **API:** `GET /api/v1/projects/{key}/issues/{number}/attachments` lists them, `POST` the same path with `multipart/form-data` uploads one (201 with the attachment, including a ready-made `markdown` link), `GET …/attachments/{id}` downloads and `DELETE …/attachments/{id}` deletes.

```bash
curl -H "Authorization: Bearer gbp_…" -F "file=@screenshot.png" http://localhost:5108/api/v1/projects/WEB/issues/12/attachments
```

### Saved filters

The issue list keeps its whole filter in the URL (`/p/WEB/issues?type=Bug&assigneeId=3&sort=priority`), so every filtered view is a link. `IssueFilterQuery` maps every public property of `IssueFilter` to a camelCase query parameter (defaults are left out; an empty value means null, e.g. `open=` for open and closed), so a new `IssueFilter` property is URL- and saved-filter-ready with no other change.

**Status filter.** The status menu lists the project's workflow statuses grouped by category (`workflowStatusId=12`), plus one "Any … status" entry per category (`status=Done`, e.g. "Any Done status" matches every Done-category status). Picking either clears the Open/Closed tab, since a status already implies it.

A saved filter is a name plus that query string. **Filters ▾** on the issue list applies, stars and saves filters (and offers to update the last-applied one after you change it); **Manage filters…** (`/p/{key}/filters`) renames, deletes, shares and unshares them. Filters are private to their owner; sharing with the project needs Developer+, and Maintainers+ can also unshare or delete shared filters. Anyone who can see a filter can star it, and starred filters appear in the sidebar under the project.

### Bulk edit

Users with EditIssues get checkboxes on the issue list (plus "select all on this page"). While issues are selected a sticky bar shows "N selected" and actions: status (the project's workflow statuses, grouped by category), assignee, priority, milestone, add label, remove label, and Delete (Maintainer+, with confirmation). Each action is one `IssueService.BulkUpdateAsync` call that checks permissions for every affected project and validates values before changing anything, applies all changes in one transaction with a per-issue activity entry, publishes the live events in one batch (the issue list coalesces them into one reload), and sends each watcher or new assignee at most one notification for the batch ("2 issues in WEB: WEB-5, WEB-10"). A bulk status change goes through `ReconcileStatusAsync` for each issue, like a single move, so the workflow's transitions apply. If one issue may not move, nothing changes and the message names that issue.

Code: `Services/Attachments/`, `Api/AttachmentEndpoints.cs`, `Services/IssueFilterQuery.cs`, `Services/SavedFilterService.cs`, `Services/IssueService.Bulk.cs`, `Services/NotificationService.Bulk.cs`, `Components/Shared/{AttachmentsPanel,MarkdownEditor,SavedFiltersMenu,StarredFiltersNav,BulkActionBar}.razor`, `Components/Pages/Projects/SavedFilters.razor`, `wwwroot/js/attachments.js`, `wwwroot/css/attachments-filters.css`. Schema: `Attachment`, `SavedFilter`, `SavedFilterStar` (migration `AttachmentsFilters`).

## Workflows & audit log

### Custom workflows

Each project has its own **workflow**: an ordered list of statuses (name, colour, category) and, optionally, the moves allowed between them. Project Owners edit it at **Settings → Edit workflow** (`/p/{KEY}/settings/workflow`, needs `ManageProjectSettings`).

- **Categories.** Every status belongs to one of the five built-in categories (Backlog, To Do, In Progress, In Review, Done). `Issue.Status` *is* the category and `Issue.WorkflowStatusId` is the project's status. The two always agree, so code that only knows categories keeps working: sprints, filters, open/closed counts, AI search and the API's `status` field. Done-category statuses count as closed.
- **Defaults.** New projects get the five statuses matching the categories. At startup, `WorkflowService.EnsureDefaults` gives any project without a workflow the default one and maps every issue whose workflow status is missing or inconsistent to the first status of its category.
- **Editing.** Add, rename, recolour, reorder (▲/▼) and recategorise statuses. Changing a status's category also changes its issues' category (and closes or reopens them). A status with issues can only be deleted after moving them to another status, which the page offers; each moved issue gets an activity entry. The workflow must keep at least one Done status and at least one open status.
- **Transitions.** A matrix of checkboxes: tick where issues may move *to* from each status. A status with nothing ticked may move anywhere. The board greys out and refuses drops on disallowed columns, the issue's status picker disables them, "Close issue"/"Reopen" pick the first allowed Done/open status (and hide when there is none), and the service refuses the move with a clear message (422 in the API). Admin operations (deleting a status) ignore transitions.
- **Board** columns are the project's statuses, in order. **Status chips** (issue list, issue page, linked issues) show the workflow status name and colour, falling back to the category (`Display.StatusName(issue)`, `<StatusBadge Issue="issue" />`).

**For code that changes statuses.** Every change goes through `IssueService.ReconcileStatusAsync`, which `UpdateIssueAsync` calls after every change. Inside an `UpdateIssueAsync` callback (bulk edits included), you can set either `issue.Status` (a category: the issue keeps its workflow status if it is already in that category, otherwise it gets the category's first status) or `issue.WorkflowStatusId`. The other field follows, and transitions are enforced. Use `SetStatusAsync` (category), `SetWorkflowStatusAsync` and `MoveOnBoardAsync` (either overload) for single moves. Code that writes `Status` with `ExecuteUpdate`, bypassing `IssueService`, leaves `WorkflowStatusId` stale. Display then falls back to the category, and the next update or app start repairs it.

**API.** Issues carry `workflowStatus: {id, name, category}`. `PATCH …/issues/{n}` and `POST …/issues` accept `status` (category) or `workflowStatusId`. If both are sent they must agree. `GET /api/v1/projects/{key}/workflow` returns `{statuses: [{id, name, color, order, category}], transitions: [{from, to}]}`.

### Audit log

`AuditLog` (`Services/Audit/AuditLog.cs`) records security-relevant events as `AuditEvent` rows: time, actor (user id and username, null for system or anonymous), IP, user agent, a dotted action, target type and id, project id, and JSON details.

- **What's recorded:**
  - `auth.sign_in`, `auth.sign_in_failed`, `auth.lockout`, `auth.sign_out`, `auth.registered`, `user.password_changed`
  - `token.created`, `token.revoked`
  - `project.created`, `project.settings_changed` (including the AI review toggle), `project.visibility_changed`, `project.deleted`
  - `member.added`, `member.role_changed`, `member.removed`
  - `repository.created`, `repository.settings_changed`, `repository.renamed`, `repository.archived`, `repository.unarchived`, `repository.deleted`, `repository.default_branch_changed`
  - `branch_protection.created`, `branch_protection.updated`, `branch_protection.deleted`, `pull_request.merged_bypassing_protection`
  - `webhook.created`, `webhook.updated`, `webhook.secret_rotated`, `webhook.deleted`
  - `workflow.status_added`, `workflow.status_updated`, `workflow.status_reordered`, `workflow.status_deleted`, `workflow.transitions_changed`
  - `issue.deleted`, `audit.exported`
  - There is no UI for changing the site-admin flag, so there's no event for it yet.
- **No secrets.** Passwords, tokens (not even their prefix) and webhook secrets are never passed. Webhook URLs are logged as scheme and host only, because paths and queries often carry credentials. As a backstop, detail keys that look like secrets (`password`, `secret`, `token`, `hash`, …) are dropped before storing.
- **Actor, IP and user agent.** The actor is the explicit `actorId`, else the acting user (`ProjectAccess`), else the signed-in user of the HTTP request. Blazor circuits have no reliable `HttpContext`, so `AuditCircuitHandler` copies IP and user agent from the connection's request into the scoped `AuditRequestInfo` when the circuit opens. Static SSR pages, the API and minimal endpoints read them from `IHttpContextAccessor`. `RemoteIpAddress` is used as-is: add `ForwardedHeaders` middleware if you run behind a proxy.
- **Append-only.** `AppDbContext` refuses tracked updates and deletes of audit rows, and the table has no foreign keys, so events outlive the users and projects they name. The only deletion is retention: `AuditRetentionService` deletes events older than `Audit:RetentionDays` (default 365; `0` keeps them forever) at startup and then daily.
- **Pages:**
  - `/admin/audit` (site admins, linked in the sidebar) filters by actor username (or `system`), action prefix (e.g. `member.`), project key and UTC date range. It shows 50 events per page, and each row's **Details** expands to show the JSON and user agent.
  - `/p/{KEY}/settings/audit` (project Owners, linked from project Settings) shows the same view limited to that project.
  - **Export CSV** downloads every matching event (up to 100,000) from `GET /admin/audit/export.csv?project=&actor=&action=&from=&to=`, which is checked the same way. Cells that start like a spreadsheet formula are prefixed with `'`.
- **Recording from new code:** `await audit.RecordAsync("area.verb", "target_type", targetId, projectId, new { …details… });`. For example, a bulk delete would record `issue.bulk_deleted` with the issue numbers. Recording never throws.

## Next steps

- **More features:** email delivery of notifications, @mention autocomplete.
