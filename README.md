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
| ManageLabelsAndMilestones | | | ✓ | ✓ |
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
| `Data/SeedData.cs` | Creates the schema (`EnsureCreated`) and seeds demo data |
| `Services/IssueService.cs` | All data access and business logic (numbering, board ordering, activity log) |
| `Services/CurrentUser.cs` | The signed-in `User` for the current circuit/request |
| `Services/ProjectAccess.cs` | Roles, permissions and project visibility: the single authorization point |
| `Services/MemberService.cs` / `Services/AccessTokenService.cs` | Project membership; personal access tokens |
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

> Schema note: this adds the `AiReviews` table and the `Projects.AiReviewEnabled` column. The database is created with `EnsureCreated`, so delete `gitbench.db*` to pick them up.

## Next steps

- **Migrations:** switch from `EnsureCreated` to EF Core migrations before the schema changes.
- **More features:** issue links and sub-tasks, attachments, email delivery of notifications, @mention autocomplete.
