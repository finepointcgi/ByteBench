# GitBench

A lightweight Jira/GitLab-style issue tracker built with **Blazor (Interactive Server)**, **.NET 10**, **MudBlazor**, **EF Core** and **SQLite**.

## Run

```bash
dotnet run --launch-profile http
```

Open http://localhost:5108. On startup, EF Core migrations are applied to `gitbench.db` (SQLite) and, on an empty database, demo users, projects and issues are seeded (in Development only, or when `Seed:DemoData` is `true`). Delete `gitbench.db*` and `repos/` to reset.

**Schema changes:** edit the model, then `dotnet ef migrations add <Name> -o Data/Migrations` (the `dotnet-ef` tool is pinned in `dotnet-tools.json`; run `dotnet tool restore` once). Migrations apply automatically on the next start.

## Running tests

```bash
dotnet test                              # everything (about 15 s)
dotnet test --filter "Category!=Docker"  # skip the Docker pipeline test (what CI runs)
```

The suite is in `tests/GitBench.Tests` (xUnit, referenced from `GitBench.sln`). Each test class boots the whole app with `WebApplicationFactory<Program>` (`Infrastructure/TestApp.cs`) on its own temp directory: a fresh SQLite database with the demo seed, bare repos and attachments, with AI and CI turned off. Tests sign in as the demo users or create their own projects and users. `GitHttpTests` runs real `git` against the app on a Kestrel port (`KestrelApp`), isolated from your git config and credential helpers, so `git` must be on `PATH`. `SshTests` (`[Trait("Category","Ssh")]`) does the same over the embedded SSH server on a free port with throw-away keys and the OpenSSH client (`ssh`, `ssh-keygen`); it is skipped when those aren't installed. `DockerPipelineTests` (`[Trait("Category","Docker")]`) turns CI on and runs a pipeline in `alpine:3.20`; it is skipped when no Docker daemon is reachable. GitHub Actions runs the suite on Ubuntu (`.github/workflows/ci.yml`).

## UI

The UI uses [MudBlazor](https://mudblazor.com) 9 (no Bootstrap).

- **Theme:** `Components/Layout/GitBenchTheme.cs` holds the light and dark palettes, typography and radii.
- **Dark mode:** the moon/sun button in the app bar toggles it. The choice is kept in the browser's `localStorage` (`wwwroot/js/theme.js`); until you choose, it follows the OS setting.
- **Shell:** `MainLayout.razor` is a MudLayout with an app bar (project switcher, New issue, notifications, account menu) and a responsive drawer. It also hosts the Popover, Dialog and Snackbar providers. Confirmations use `IDialogService` and transient messages use `ISnackbar`.
- **CSS:** component styles live in scoped `.razor.css` files. `wwwroot/app.css` keeps only shared bits, and its `--bg`, `--surface`, `--text`, `--muted`, `--border` and `--accent` variables map onto `--mud-palette-*`, so custom CSS follows the theme.
- **Account pages** (sign-in, registration, profile) are static SSR, so they post real HTML forms with inputs styled like MudBlazor's (`Components/Account/AccountLayout.razor.css`).

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
- **Two-factor sign-in** at `/account/two-factor` (see [Two-factor sign-in](#two-factor-sign-in-totp)). Site admins manage accounts at `/admin/users` (see [User administration](#user-administration)).
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
- Projects can also inherit roles from their groups; see [Groups](#groups).

## Groups

GitLab-style namespaces that own projects (`Group`, `GroupMember`, `GroupRedirect` and `Project.GroupId` in `Data/Models.cs`; logic in `Services/GroupService.cs` and `GroupService.Labels.cs`; permissions in `Services/ProjectAccess.Groups.cs`; UI in `Components/Groups/` and `Components/Pages/Groups/`).

- **Structure:** a group has a name, a path segment, a description, a visibility and an avatar colour. Subgroups nest at most 4 levels deep (`Group.MaxDepth`). A group's URL is `/g/{full path}`, e.g. `/g/acme/platform`; its sections are under `/-/` (`/g/acme/-/issues`, `-/pull-requests`, `-/labels`, `-/members`, `-/settings`).
- **Projects** optionally belong to one group (`Project.GroupId`; null = top-level). Project keys stay globally unique, so `/p/{KEY}`, git URLs (`/git/{KEY}/…`), webhooks, CI and the API still find projects by key. Create a project in a group from the group page or the group picker on **New project**; move it between groups under **Settings → Group** (needs Owner of the project and Maintainer in the target group).
- **Visibility:** *Private* groups are seen only by their members, members of a subgroup or of a project in them (they see the chain of parent groups), and admins. *Internal* groups are seen by every signed-in user. A subgroup can't be more visible than its parent. Group visibility never opens a Private project: an Internal group with a Private project still hides the project from non-members.
- **Roles and inheritance:** group members have the usual roles (Viewer, Developer, Maintainer, Owner). A member of a group has at least that role in every subgroup and project below it. A user's **effective project role** is the highest of their direct project role, their roles in the project's group and its ancestors, Viewer if the project is Internal, and Owner for site admins. `ProjectAccess.GetRoleAsync` and `VisibleProjectIdsAsync` compute this in one query each (the ancestor chain is joined through `Parent`, which the depth limit keeps to 4 hops).
- **Group permissions** (`GroupPermission`): view (any role, or visible as above); create projects and subgroups, manage group labels and milestones: Maintainer; manage members and settings, delete: Owner. The creator of a group becomes its Owner. A top-level group must keep a direct Owner; subgroups inherit their parents' Owners.
- **Members pages:** `/g/{path}/-/members` lists direct members (Owners add, change and remove them) and the members inherited from parent groups. A project's Members page shows inherited members read-only with their source ("via acme").
- **Settings:** rename and change visibility/colour; change the path; move the group under another group (Owner of the group plus Maintainer in the new parent; the depth limit and visibility rule are checked); delete. Old paths of the group and all its subgroups keep working as redirects (`GroupRedirect`, also in the API) until a new group takes the path. An empty group is deleted after a confirmation; a group with subgroups or projects needs its full path typed, and then deletes everything in it (projects with their issues, repositories and files).
- **Overview:** subgroups and projects as a collapsible tree with subgroup, project, open issue and open pull request counts.
- **Aggregated lists:** group **Issues** (search, project, assignee, group label and milestone, type, sort) and **Pull requests** (by state) span every project of the group and its subgroups that you can view.
- **Group labels and milestones** (`Label.GroupId` / `Milestone.GroupId`, with `ProjectId` null) are defined on the group's **Labels & milestones** page and inherited by every project below it: they appear next to the project's own labels and milestones in pickers, filters, bulk edit and the API, read-only in the project (marked *Group*). When a project or group moves so that a project loses access to a group label or milestone its issues use, the project gets its own copy and its issues switch to it.
- **Breadcrumbs:** project pages in a group show `Groups › acme › platform › project`; group pages show their chain.
- **Audit:** `group.created`, `group.settings_changed`, `group.path_changed`, `group.moved`, `group.deleted`, `group.member_added`/`_role_changed`/`_removed`, `group.label_*`, `group.milestone_*` and `project.transferred`.
- **API:** `GET /api/v1/groups` (`?q=`), `/groups/{group}`, `/groups/{group}/subgroups`, `/groups/{group}/projects` (`?includeSubgroups=true`) and `/groups/{group}/issues` (`?status=`, `?q=`, `?sort=`). `{group}` is the id or the URL-encoded full path (`acme%2Fplatform`). Groups you can't view are 404.
- **Seed:** a fresh database gets the Internal group **Acme** (alice Owner, dan Developer) with the Internal subgroup **Platform** owning WEB and OPS, plus a group label (`customer-reported`) and milestone on Acme. Existing databases migrate with every project top-level and no groups.

## Features

- **Projects** with a short key (`WEB`), and issues numbered per project (`WEB-12`)
- **Issues** with type (task, bug, feature, story, epic), status, priority, assignee, labels, milestone, story points and due date
- **Issue list** with open/closed tabs, search (including by key), filters and sorting
- **Kanban board** with drag-and-drop between and within columns, filtered by sprint or assignee
- **Issue detail** with inline editing, Markdown descriptions and comments (Write/Preview), and an activity timeline
- **Milestones** with progress and story-point burn-up
- **Sprints**: backlog planning, start/complete, burndown and velocity reports (see [Sprints](#sprints))
- **Project settings**: rename, manage labels, delete the project
- **Dashboard**: your assigned work, all projects, activity from your projects
- **Explore, stars, profiles, global search and keyboard shortcuts** (see [Explore, profiles & search](#explore-profiles--search))

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

### Git over SSH

GitBench has its own SSH server for git, so you can clone and push with an SSH key instead of a password or token. It is **off by default** (on in `appsettings.Development.json` and in the Docker image).

```bash
git clone ssh://git@localhost:2222/WEB/web-app.git      # Development: port 2222
git clone git@gitbench.example.com:WEB/web-app.git      # scp-like form, only when the public port is 22
```

- **Your keys:** add them under **Profile → SSH keys** (`/account/ssh-keys`): paste the `.pub` file, give it a title (default: the key's comment) and optionally an expiry. The list shows each key's `SHA256:` fingerprint (as `ssh-keygen -lf key.pub` prints it), when it was added and last used. Supported: `ssh-ed25519` (recommended), `ecdsa-sha2-nistp256/384/521` and `ssh-rsa` of 2048 bits or more. FIDO (`sk-…`) keys aren't supported yet.
  - A key acts as you, with your project permissions: clone and fetch need ViewProject, push needs PushCode. A project you can't see answers `Repository not found`, like a missing repository.
  - Two-factor sign-in doesn't apply: SSH keys work for accounts with 2FA, as access tokens do.
  - Keys of deactivated or locked accounts stop working at once, as do expired keys.
  - A key (by fingerprint) can be registered only once across all users and deploy keys.
  - Adding and removing keys is audited (`ssh_key.added` / `ssh_key.removed`).
- **Deploy keys:** under a repository's **Settings → Deploy keys** (Maintainer or Owner), add an SSH key for a server or CI system. It works for that repository only, read-only unless *Allow write access* is ticked. A write deploy key pushes like a Developer: branch protection applies, with no admin bypass. Its pushes run webhooks, pipelines and smart commits as the person who added it, and it can't push once that person's account is deactivated. Audited as `deploy_key.added` / `deploy_key.removed`.
- **Clone URLs:** the repository page has *HTTPS* and *SSH* tabs. The SSH URL is `ssh://git@{Ssh:PublicHost}:{Ssh:PublicPort}/KEY/slug.git` (host defaults to the site's host, port to `Ssh:Port`); when the public port is 22 the scp-like `git@host:KEY/slug.git` is shown too. Any SSH user name works; use `git`. Old slugs of renamed repositories keep working, as over HTTP.
- **Same rules as HTTP:** archived repositories refuse pushes; branch protection and release tags are checked before git sees the push (the same `ProtectedPushGuard` checks, so a refused push gets `! [remote rejected] main -> main (reason)` and writes nothing), and force pushes to protected branches are caught by the same pre-receive hook. After a push, every `IGitPushListener` runs (webhooks with the pushed refs, CI, smart commits, stale approvals, AI review). Commands are rate limited per user or deploy key (`RateLimiting:Ssh`). Protocol v2 works (git sends `GIT_PROTOCOL`).
- **Only git:** the server runs `git-upload-pack` and `git-receive-pack` for `KEY/slug.git` paths and nothing else. Password and keyboard-interactive auth are not offered; `ssh git@host` prints "Hi alice! You've successfully authenticated, but GitBench does not provide shell access."; other commands, PTYs, port/agent/X11 forwarding and subsystems such as sftp are refused.
- **Host key:** an RSA 3072 host key is generated on first start at `Ssh:HostKeyPath` (default `{DataProtection:KeysPath}/ssh_host_rsa_key`, i.e. `keys/` locally and `/data/keys` in Docker; gitignored, mode 600) and its fingerprint is logged and shown on the SSH keys page. Back it up with the key ring: a new host key makes every client warn about a changed host.
- **Library:** the server is built on [Microsoft.DevTunnels.Ssh](https://github.com/microsoft/dev-tunnels-ssh) (Microsoft's maintained, async SSH library with server sessions, public-key auth and exec channels; FxSsh was the alternative, but it is synchronous, thread-per-channel and also lacks ed25519). ed25519 signatures are verified with BouncyCastle (`Services/Ssh/Ed25519Algorithm.cs`).
- **Post-quantum warning:** OpenSSH 10.1+ prints "WARNING: connection is not using a post-quantum key exchange algorithm" because the library offers ECDH and DH key exchange only. The connection is still encrypted; silence it per host with `WarnWeakCrypto no-pq-kex` in `~/.ssh/config`.
- **Testing locally:** `ssh -p 2222 git@localhost` should greet you; `GIT_SSH_COMMAND="ssh -i ~/.ssh/other_key -o IdentitiesOnly=yes" git clone …` picks a specific key.

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
| `Services/GroupService.cs` / `Services/ProjectAccess.Groups.cs` | Groups: subgroups, members, group labels/milestones, transfers; group permissions |
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

## Email, notification preferences & autocomplete

### Email delivery

Notifications can also go out by email. Code: `Services/Email/`.

- **Senders** (`IEmailSender`): `SmtpEmailSender` (MailKit) or `PickupDirectoryEmailSender`, which writes `.eml` files to `Email:PickupDirectory` (default `./mail-drop`, git-ignored) so nothing leaves the machine. `Email:Sender` picks one (`Pickup` or `Smtp`). It defaults to `Pickup` in Development and `Smtp` elsewhere.
- **Off by default:** `Email:Enabled` is `false`. To try it locally, run `dotnet run --launch-profile http -- --Email:Enabled=true` and open the files in `mail-drop/` with any mail client.
- **Queue:** emails go on an in-memory channel (`EmailQueue`) and a background `EmailWorker` sends them, so requests never wait on SMTP. A failed send is retried 3 times (after 2 s, 10 s and 30 s), then logged and dropped. Logs mask addresses (`b***@example.com`).
- **SMTP:** never put credentials in appsettings. Use user-secrets in development (the project already has a `UserSecretsId`), or environment variables such as `Email__Smtp__Password` in production:

```bash
dotnet user-secrets set "Email:Enabled" "true"
dotnet user-secrets set "Email:Sender" "Smtp"
dotnet user-secrets set "Email:Smtp:Host" "smtp.example.com"
dotnet user-secrets set "Email:Smtp:Port" "587"
dotnet user-secrets set "Email:Smtp:User" "gitbench@example.com"
dotnet user-secrets set "Email:Smtp:Password" "<password>"
dotnet user-secrets set "Email:From" "GitBench <gitbench@example.com>"
```

| Key | Default | Meaning |
|---|---|---|
| `Email:Enabled` | `false` | Master switch. While it's off, nothing is queued and pending notification emails are dropped, so turning it on doesn't send a backlog |
| `Email:Sender` | `Pickup` in Development, else `Smtp` | Which sender to use |
| `Email:From` | `GitBench <gitbench@localhost>` | Sender address |
| `Email:PickupDirectory` | `mail-drop` | Folder for `.eml` files (relative to the content root) |
| `Email:DigestMinutes` | `0` | `0` sends each notification right away. `N` sends at most one email per user every N minutes: the first one at once, later ones batched into a digest |
| `Email:BaseUrl` | (none) | Absolute site URL for links in emails. Falls back to `PublicBaseUrl`, then the URL of the first request |
| `Email:Smtp:Host` / `Port` / `User` / `Password` / `UseStartTls` | `587`, STARTTLS | SMTP server. With `UseStartTls: false`, port 465 uses implicit TLS and other ports are plain text (local relays only) |

**Content.** Each email is built from the same `Notification` row the bell shows: actor, verb, title and a link to the item. It also includes an excerpt of the comment or description (`Notification.Excerpt`, at most 1000 characters). The HTML part renders the excerpt with the app's safe Markdown pipeline (raw HTML disabled), and the plain-text part quotes the Markdown source. Every other piece of user content is HTML-encoded. Each email links to the preferences page and has a signed one-click unsubscribe link (`/email/unsubscribe?t=…`, valid 90 days, also sent as `List-Unsubscribe` / `List-Unsubscribe-Post` headers), which turns off all notification emails for that user. `NotificationEmailWorker` picks up pending notifications whenever `NotificationsChanged` fires, and also every 30 seconds. It skips anything already read in the app.

**Identity emails.** `IdentityEmailSender` is registered as `IEmailSender<User>`, so Identity's confirmation and password-reset emails use the same queue and sender. The app has no such flow yet, so nothing calls it today.

### Notification preferences

`/account/notifications`, linked from the profile page and the 🔔 dropdown. For each group, choose **In-app + email**, **In-app only** or **Off**:

| Group | Kinds | Default |
|---|---|---|
| Assigned to me | Assigned | In-app + email |
| Mentions | Mentioned | In-app + email |
| Review requests | ReviewRequested | In-app + email |
| Pull request reviews | PullRequestReviewed | In-app |
| Comments on watched items | IssueCommented, PullRequestCommented | In-app |
| Changes to watched items | issue status/close/reopen, PR merge/decline/reopen | In-app |
| Issue links & hierarchy | IssueLinked, IssueHierarchyChanged | In-app |
| Pipelines | PipelineFailed | In-app |

There are no sprint notifications, so sprints have no group. A master switch turns all notification emails off. "Don't notify me about my own actions" is shown and always on, as before. Stored in `NotificationPreferences` (a row only when it differs from the default) and `UserNotificationSettings`; logic in `Services/NotificationPreferences.cs`.

Preferences are applied in `NotificationService.DeliverAsync` (`Services/NotificationService.Delivery.cs`), the single fan-out point every notification passes through (single events, bulk edits and pipelines). If a user qualifies for several kinds in one event (say, mentioned in a comment on an issue they watch) and has turned the first one off, they get the next one.

**Mute.** Watchers get a **🔕 Mute** button next to **Watching** on issues and PRs. A muted watch (`Watch.Muted`) stays a watch but receives no watcher notifications. Direct ones (assigned, mentioned, review requested, reviews of your own PR) still arrive. Unwatching isn't the same thing: GitBench auto-watches you again when you comment, are assigned or review, so an unwatch doesn't stick. A mute does, because auto-watching only adds missing rows. Unwatching clears the mute.

### Autocomplete in the Markdown editor

Typing `@` and a letter in any Markdown editor (issue description, new issue, issue comments, PR description, PR comments and inline diff comments) shows up to 8 people who can view the project. Each row shows the display name, `@username` and avatar colour. People who can view a project are site admins and members, plus everyone if the project is Internal; the `gitbench-ai` bot is never suggested. Typing `#` and a number or text suggests issues by number or title, and a project key like `WEB-` suggests that project's issues. Picking one inserts `@bob ` or `WEB-12 `.

- Keys: ↑/↓ to move, Enter or Tab to pick, Esc to close. The mouse works too.
- `wwwroot/js/mention-autocomplete.js` is an ES module the editor imports. It places a `position: fixed` dropdown at the caret, using a mirror element to measure the caret position, and builds it with `textContent` only.
- Lookups are debounced (120 ms) and go to the editor's `[JSInvokable] Suggest` method, which calls `Services/MentionLookup.cs`. That runs as the signed-in user and returns nothing unless they can view the project, including a project named by a typed key.
- The editor gets the project from its new optional `ProjectId` parameter. Without it, there's no autocomplete.


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
## Pull request features: drafts, threads, suggestions, code owners & merge strategies

**Draft pull requests.** Click **Create draft** on the New PR page (or send `"draft": true` to the API). A draft shows a grey **Draft** badge, can never be merged (MergeGate adds a failed "Draft" check that admins can't bypass, protected branch or not), and its assigned reviewers are recorded but **not notified** until the author or a Maintainer+ clicks **Ready for review**. **Convert to draft** goes back. The PR list has a **Drafts and ready / Ready for review / Drafts only** filter (`?draft=ready|only`). Webhooks: `pull_request.updated` with action `ready_for_review` or `converted_to_draft`.

**Comment threads.** An inline diff comment starts a thread; **Reply…** adds to it (replies copy the thread's file and line, `PullRequestComment.ParentId`). The PR's author, the thread's author or a Maintainer+ can **Resolve** or **Unresolve** it. Resolved threads collapse in the diff. The PR header shows the number of unresolved threads. A thread is marked **Outdated** when a later push changed its line: each new inline comment records the head commit (`CommitSha`) and the commented line's text (`LineText`), and the line at the current head is compared with it.

**Suggested changes.** In an inline comment on a new-side line, a fenced block opened with ` ```suggestion ` proposes its content as the replacement for the commented line(s) (an empty block deletes them; the API's `startLine` makes a comment cover several lines). **Suggest a change** in the comment box inserts one prefilled with the line. It renders as a mini diff with **Apply suggestion** for users who can push to the source branch (Developer+, branch protection on the source branch allowing it, repository not archived). Applying writes a commit to the source branch in the bare repo, authored by the applier, with the message "Apply suggestion from @user" and `Co-authored-by` the suggester, then resolves the thread. **Apply all suggestions** commits every pending one in a single commit. It's refused, with nothing written, if the suggested lines changed since the comment or two suggestions overlap.

**Code owners.** A `CODEOWNERS` file on the target branch (`.gitbench/CODEOWNERS`, then `CODEOWNERS`, then `docs/CODEOWNERS`) in GitHub syntax: `pattern @username @other email@example.com`, `#` comments, last matching rule wins, gitignore-style patterns (`*.md`, `/build/`, `docs/*`, `src/**/test.cs`). Owners of the changed files are added as reviewers when a PR is created (if they can review), and the Files tab shows the owners of each file. `@group/path` (or a bare `@acme` that isn't a username) stands for that group's direct members with Developer or higher. Owners who can't see the project, and names that match no user or group, are ignored with a warning on the PR. The branch protection option **Require code owner approval** blocks merging until each changed file with owners is approved by one of them (files owned only by the PR's author need none).

**Require all threads resolved** is another branch protection option: any unresolved inline thread blocks merging.

**Merge strategies.** The merge box offers **Merge commit** (and fast-forward if possible), **Squash** (one commit on the target with an editable message, authored by the PR's author, with `Co-authored-by` trailers for the other commit authors) and **Rebase** (the PR's commits replayed onto the target, which is fast-forwarded; merge commits are dropped, and a conflict refuses cleanly). Maintainers choose the allowed strategies and the default under the repository's **Settings → Pull requests** (`/settings/merge`). The strategy is recorded on the PR (`mergeStrategy` in the API and the `pull_request.merged` payload), in the activity ("squashed and merged…"), and as a `Merge-Strategy:` trailer on merge and squash commits.

**Compare branches.** `/p/{KEY}/repos/{slug}/compare/{base}...{head}` picks two branches and shows the commits on head that aren't on base plus the diff since their merge base, with a prefilled **Create pull request** button. The Branches page links to it (toolbar **Compare** and a per-branch button).

**API.**

| Endpoint | |
|---|---|
| `POST …/pull-requests` | `draft` (bool) |
| `PATCH …/pull-requests/{n}` | `title`, `description`, `draft` (only fields present change) |
| `POST …/pull-requests/{n}/comments` | `parentId` (reply), `startLine` (multi-line comment) |
| `POST …/pull-requests/{n}/comments/{id}/resolve` / `unresolve` | resolve or reopen the comment's thread |
| `POST …/pull-requests/{n}/comments/{id}/apply-suggestion` | commit one suggestion (409 if its lines changed) |
| `POST …/pull-requests/{n}/apply-suggestions` | `{ "commentIds": [..] }` in one commit |
| `POST …/pull-requests/{n}/merge` | `strategy`: `merge`, `squash` or `rebase` (default: the repository's default); `commitMessage` for squash |
| `GET …/repos/{slug}/compare?base=&head=` | commits and changed files |

Comments in the API carry `parentId`, `startLine`, `resolved`, `commitSha`, `hasSuggestion` and `appliedCommitSha`; pull requests carry `isDraft` and `mergeStrategy`.

Code: `Services/PullRequestService.Features.cs`, `Services/PullRequests/` (CODEOWNERS, suggestions), `Services/Git/GitOps.PrFeatures.cs` (squash, rebase, line edits), `Components/Repositories/DiffThread.razor`, `Components/Pages/Repositories/RepoCompare.razor` and `RepoMergeSettings.razor`. Tests: `tests/GitBench.Tests/PullRequestFeatureTests.cs`.

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
- Containers never get the Docker socket, host environment variables or any GitBench secret. They get only the `GITBENCH_*` variables above, plus the [CI variables](#ci-secret-variables) the pipeline is allowed to see.
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

Why client-side: highlight.js is maintained, small and covers far more languages than the .NET options (ColorCode covers few languages; TextMateSharp needs native Oniguruma binaries). To stay safe under Blazor's DOM diffing, each code line is rendered as a `MarkupString` (`<span class="hl-line">…</span>`). Blazor tracks the span and replaces it whole when the line changes, and `wwwroot/js/code-tools.js` only rewrites the span's children. A single `MutationObserver` reruns the idempotent highlight pass after every render, so it survives re-renders, enhanced navigation and live updates with no per-component JS interop. Token colours are CSS variables in `wwwroot/css/code-tools.css`, defined as `light-dark()` pairs that follow the MudBlazor theme's colour scheme.

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
| GET | `/groups`, `/groups/{group}`, `/groups/{group}/subgroups`, `/groups/{group}/projects`, `/groups/{group}/issues` | `{group}` is an id or URL-encoded full path; see [Groups](#groups) |
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
| GET | `/projects/{key}/repos/{slug}/tags` | Newest first: `name`, `sha` (commit), `objectSha`, `annotated`, `message`, tagger, `zipUrl`, `tarGzUrl` |
| GET | `/projects/{key}/repos/{slug}/releases`, `…/releases/{id}`, `…/releases/latest`, `…/releases/tags/{tag}` | Drafts only for Developer+. `latest` is the newest published release that isn't a pre-release |
| POST | `/projects/{key}/repos/{slug}/releases` | `{tagName, title?, notes?, target?, tagMessage?, prerelease?, draft?, generateNotes?}`. Developer+. Returns `201`. A missing tag is created from `target` |

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
| `release.published` | A release was published (created as non-draft, or a draft published): `release` (with `assets`, `zipUrl`, `tarGzUrl`) and `repository` |
| `tag.created` / `tag.deleted` | A tag was created or deleted in GitBench or by a push: `tag` and `repository` |
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

## Tags & releases

**Tags** (`/p/{KEY}/repos/{slug}/tags`, the **Tags** tab) lists every tag, newest first. Each row shows the commit, the release that uses the tag, **zip** and **tar.gz** source downloads, and, for annotated tags, the tagger, date and message.

- **Create** (Developer+, `PushCode`): give a name, a branch or commit (anything git can resolve, such as `main~3` or a SHA) and an optional message. With a message it's an annotated tag (tagger = you); without one it's a lightweight tag. Names must be valid git ref names: no spaces, `..` or `~^:?*[\`, no leading `-`, and at most 250 characters. A tag can't have the same name as a branch.
- **Delete** (Maintainer+, `DeleteBranch`). A tag that a release uses can't be deleted, either in the UI or by `git push --delete`. Such a push is refused with `! [remote rejected] … tag 'v1.0' is used by a release`. Delete the release first.
- Pushed tags arrive through git as usual. `TagPushListener` raises `tag.created`/`tag.deleted` webhooks for them and records pushed tag deletions in the audit log. Branch protection and CI triggers only look at `refs/heads/*`. The `push` webhook is still sent for tags; annotated tags have no commit list.

**Releases** (the **Releases** tab) attach a title, Markdown notes and files to a tag.

- **New release** (Developer+):
  - Pick an existing tag or type a new one. A new tag is created from the branch or SHA you choose when you save, and is annotated if you give a tag message.
  - Set a title (defaults to the tag), notes, and **pre-release** if it applies.
  - Click **Publish release** or **Save draft**.
- **Drafts** and their assets are visible only to Developer+. Publishing a draft keeps its first publish date.
- **Generate notes** fills the notes from git history since the previous release. The previous release is the other release whose tag is the closest ancestor of this one. The notes list:
  - merged pull requests (matched by merge commit)
  - non-merge commits
  - the issues mentioned in their messages, with issue keys linked (`[WEB-12](/p/WEB/issues/12)`)

  Without an earlier release, the whole history is used (up to 300 commits).
- **Assets**: upload files on the release page (Developer+).
  - They're stored like issue attachments, under random names in `Attachments:Root`, but in their own table (`ReleaseAssets`).
  - Limits: `Releases:MaxAssetBytes` per file (default 512 MB) and 50 per release.
  - `GET /release-assets/{id}/{name}` serves them with the attachment rules: `nosniff`, a sandbox CSP, and `Content-Disposition: attachment` except for verified png/jpeg/gif/webp.
  - Download counts are shown.
- Each release also lists **Source code (zip / tar.gz)**.
- **Edit** changes the title, notes and flags, but not the tag.
- **Delete** (Maintainer+) removes the release and its asset files, and optionally the tag.
- The repository's **Source** page shows a **Latest** badge for the newest published release that isn't a pre-release.

**Source archives**: `GET /p/{KEY}/repos/{slug}/archive/{ref}.zip` or `.tar.gz` (also `.tgz`) works for any branch, tag or SHA.

- The ref is resolved to a commit SHA first. `git archive` then runs with an argument list (never a shell) and streams straight to the response, with files under `{slug}-{ref}/`.
- Access needs ViewProject, through a cookie session or a `Bearer` token with any scope. Otherwise the response is `401` or `404`.
- Limits (section `Archives`): `MaxBytes` (default 1 GB), `TimeoutSeconds` (300) and `MaxConcurrent` (4; beyond that, `503` with `Retry-After`). An archive that hits a limit is cut off by aborting the connection, so it never looks complete.

**Audit**: `release.created`, `release.published`, `release.deleted`, `tag.deleted`. **Webhooks**: `release.published`, `tag.created`, `tag.deleted`.

Code: `Services/Releases/` (`ReleaseService`, `ReleaseDownloads`, `TagPushListener`), `Services/Git/GitOps.Tags.cs`, `Api/ReleaseEndpoints.cs`, `Components/Pages/Repositories/Repo{Tags,Releases,Release,ReleaseEdit}.razor`, `Components/Releases/ReleaseCard.razor`, `wwwroot/css/releases.css`. Schema: `Release`, `ReleaseAsset` (migration `Releases`).

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
- Parent changes and link changes are written to the activity of every issue involved (both ends of a link) and notify their watchers (`IssueHierarchyChanged`, `IssueLinked`). A link notification says what changed ("WEB-5 Avatar images broken · link added: blocks OPS-2"). Like the activity text, it names an issue in another project only to watchers who can view that project; the others read "link added: blocks an issue in another project".

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
- **Storage:** bytes are streamed (never buffered whole) to `Attachments:Root` (default `{ContentRoot}/attachments/`, git-ignored) under a random 32-hex id; the user's filename is never part of a path. The database keeps the sanitized original name, size, content type, SHA-256, uploader and time. Deleting an issue (or many, or a project) deletes the rows first, then the files. Deleting a project also removes its bare repositories (`{Git:RepositoryRoot}/{KEY}`).
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
  - `release.created`, `release.published`, `release.deleted`, `tag.deleted` (also when a push deletes a tag)
  - There is no UI for changing the site-admin flag, so there's no event for it yet.
- **No secrets.** Passwords, tokens (not even their prefix) and webhook secrets are never passed. Webhook URLs are logged as scheme and host only, because paths and queries often carry credentials. As a backstop, detail keys that look like secrets (`password`, `secret`, `token`, `hash`, …) are dropped before storing.
- **Actor, IP and user agent.** The actor is the explicit `actorId`, else the acting user (`ProjectAccess`), else the signed-in user of the HTTP request. Blazor circuits have no reliable `HttpContext`, so `AuditCircuitHandler` copies IP and user agent from the connection's request into the scoped `AuditRequestInfo` when the circuit opens. Static SSR pages, the API and minimal endpoints read them from `IHttpContextAccessor`. Behind a reverse proxy, set `ReverseProxy:Enabled` (see [Deploying GitBench](#deploying-gitbench)) so `RemoteIpAddress` is the client's address.
- **Append-only.** `AppDbContext` refuses tracked updates and deletes of audit rows, and the table has no foreign keys, so events outlive the users and projects they name. The only deletion is retention: `AuditRetentionService` deletes events older than `Audit:RetentionDays` (default 365; `0` keeps them forever) at startup and then daily.
- **Pages:**
  - `/admin/audit` (site admins, linked in the sidebar) filters by actor username (or `system`), action prefix (e.g. `member.`), project key and UTC date range. It shows 50 events per page, and each row's **Details** expands to show the JSON and user agent.
  - `/p/{KEY}/settings/audit` (project Owners, linked from project Settings) shows the same view limited to that project.
  - **Export CSV** downloads every matching event (up to 100,000) from `GET /admin/audit/export.csv?project=&actor=&action=&from=&to=`, which is checked the same way. Cells that start like a spreadsheet formula are prefixed with `'`.
- **Recording from new code:** `await audit.RecordAsync("area.verb", "target_type", targetId, projectId, new { …details… });`. For example, a bulk delete would record `issue.bulk_deleted` with the issue numbers. Recording never throws.

## User admin, two-factor sign-in & CI secrets

### User administration

Site admins manage accounts at `/admin/users` (sidebar **👥 Users**, next to the Audit log). Logic: `Services/AdminUserService.cs`; page: `Components/Pages/Admin/UserAdminPage.razor`.

- **List:** search by username, display name or email, 25 per page. Each row shows the username and display name, email, admin flag, status (active, locked until …, or deactivated), whether 2FA is on, the last sign-in (the newest `auth.sign_in` audit event, so it's limited by audit retention) and the number of project memberships.
- **Actions** (each asks for confirmation on the page and records an audit event):

  | Action | Effect | Audit event |
  |---|---|---|
  | Make admin / Revoke admin | Sets `User.IsAdmin`. GitBench refuses to revoke the last admin, so you can't lock everyone out | `admin.user_admin_granted` / `admin.user_admin_revoked` |
  | Lock | Identity lockout for 1 hour, 1 day, 7 days or until unlocked, and signs the user out everywhere | `admin.user_locked` |
  | Unlock / Reactivate | Clears the lockout, the failed sign-in counter and the deactivation | `admin.user_unlocked` |
  | Sign out | Rotates the security stamp: every browser session ends within a minute. Access tokens keep working | `admin.user_signed_out` |
  | Reset 2FA | For a user who lost their device: turns 2FA off, replaces the authenticator key and deletes the recovery codes | `admin.user_2fa_reset` |
  | Deactivate | Locks the account indefinitely (`User.DeactivatedAt`), signs it out and revokes every personal access token. Reactivating doesn't bring the tokens back | `admin.user_deactivated` |

- You can't lock or deactivate yourself. The GitBench AI bot account is listed but can't be changed.
- **Locked and deactivated accounts can't use tokens either:** `AccessTokenService` refuses tokens of locked or deactivated users (REST API Bearer and git), and git's Basic auth checks lockout before the password or token.
- **How fast it applies:** the cookie's security stamp is re-checked every minute (`SecurityStampValidatorOptions.ValidationInterval`), and open Blazor circuits re-check it every minute too (`IdentityRevalidatingAuthenticationStateProvider`).

### Two-factor sign-in (TOTP)

Users turn it on at `/account/two-factor` (**Profile → Two-factor sign-in**). It uses ASP.NET Core Identity's authenticator support (RFC 6238 time-based codes, 6 digits, 30 seconds).

- **Set up:** the page shows a QR code (the `otpauth://` URI drawn as an inline SVG with [QRCoder](https://github.com/codebude/QRCoder)) and the key for manual entry. Enter a current code to turn 2FA on. GitBench then shows **10 recovery codes once**; each works one time.
- **Manage:** see how many recovery codes are left, generate new ones (the old ones stop working), or turn 2FA off (asks for your password; the key and recovery codes are discarded).
- **Signing in:** after a correct password, `/account/login-2fa` asks for the authenticator code, with **Remember this device** (14 days, Identity's two-factor remember-me cookie), or a recovery code (`?mode=recovery`). Wrong codes count toward the normal lockout (5 failures, 5 minutes).
- **Git and the API:** personal access tokens keep working. Git Basic auth with the account **password** is refused for 2FA users with `401 Password authentication is not supported for accounts with two-factor sign-in. Use a personal access token…` (only after the password was checked, so it doesn't reveal who has 2FA).
- **Audit events:** `user.2fa_enabled`, `user.2fa_disabled`, `user.2fa_recovery_codes_regenerated`, `auth.2fa_failed` (wrong code; details say `authenticator` or `recovery_code`), and `auth.sign_in` with `twoFactor` in its details for a successful two-factor sign-in.
- A lost device: use a recovery code, or ask a site admin to **Reset 2FA** at `/admin/users`.

### CI secret variables

Pipelines can get secrets (deploy keys, API tokens, …) as environment variables. Code: `Services/Ci/CiVariableService.cs`, `Services/Ci/CiSecretMasker.cs`, `CiRunner`; UI: `Components/Shared/CiVariablesEditor.razor`.

- **Where:** per project in the **CI variables** card at `/p/{KEY}/settings` (every repository in the project gets them), and per repository at `/p/{KEY}/repos/{slug}/settings/ci-variables` (**Settings → CI variables**). A repository variable overrides a project variable with the same name.
- **Who:** Maintainers and Owners (`ManageRepositories`).
- **Fields:** a name (letters, digits and `_`, not starting with a digit; `CI` and `GITBENCH_*` are reserved), a single-line value (up to 10,000 characters), and **Protected**.
- **Storage:** values are encrypted with ASP.NET Core Data Protection (purpose `GitBench.CiVariables.v1`) and never shown again. The list only says "updated *date* by *user*". Editing with an empty value keeps the stored value. If the Data Protection keys are lost, the variable is skipped with a note in the step log, and must be saved again.
- **Protected variables** only reach pipelines whose branch matches a [branch protection rule](#branch-protection-reviewers--pr-templates). Pull request pipelines run on the PR's source branch, so a PR from an unprotected branch never sees them. The step log says how many protected variables were held back.
- **Injection:** for each step GitBench writes the variables to a temp file (mode 600, in a mode-700 folder under the CI work directory, outside the mounted workspace) and passes it with `docker run --env-file`. The file is deleted when the step ends (and the folder at startup). Values never appear on a command line. The step log lists the variable names it got.
- **Masking:** every value at least 4 characters long is replaced with `***` in step logs. The whole log is masked each time it's stored (about once a second), not each chunk, so a value that arrives in several pieces is still masked, and a tail that looks like the start of a value is hidden until the rest arrives.
- **Audit events:** `ci_variable.created`, `ci_variable.updated` and `ci_variable.deleted` with the name, scope and protected flag, never the value (`valueChanged` says whether the value was replaced).
- **Limits of masking:** anyone who can change `.gitbench-ci.yml` on a branch that gets a variable (Developer+ for unprotected variables) can still leak it, e.g. by base64-encoding it or sending it over the network. Use **Protected** for anything sensitive, and protect the branches that get it so changes go through review.

## Explore, profiles & search

Code: `Services/Explore/` (`StarService`, `ExploreService`, `UserActivityService`, `SearchService`, `PushActivityListener`), pages `Components/Pages/Explore.razor`, `UserProfilePage.razor` and `SearchPage.razor`, the app-bar box `Components/Layout/GlobalSearchBox.razor`, and `wwwroot/js/shortcuts.js`. Everything is filtered with `ProjectAccess.VisibleProjectIdsAsync`, so nothing from a project you can't see shows up anywhere: not in search, not in Explore, and not on someone else's profile.

### Explore & stars

- **`/explore`** (sidebar **Explore**) lists every project you can view, with its visibility, open issues, repositories, star count and last activity (the newest issue update, pull request update or push).
- **Tabs:** All, Starred, Mine (projects you're a member of). **Filter** by key, name or description. **Sort** by name, recently active or most stars.
- **Stars:** the star button on each Explore row, and next to the project name in the sidebar on any project page, stars or unstars it and shows the count. Starred projects come first in the sidebar's **Projects** list, marked ★. Anyone who can view a project can star it (`ProjectStar`, one per user and project). A star on a project you lose access to stays in the table but is never shown.

### User profiles

- **`/u/{username}`** (case-insensitive): avatar, display name, `@username`, member since (`User.CreatedAt`; for accounts created before it was recorded, the date of their first project membership), and the projects they're a member of that you can see.
- **Activity feed:** issues opened and closed (a status change into a Done-category workflow status), issue comments, pull requests opened and merged, pull request comments, and pushes.
- **Pushes:** `PushActivityListener` (an `IGitPushListener`) records a `PushEvent` for each branch or tag a push actually moved, with the number of new commits (capped at 100). Pushes made before this feature existed aren't in the feed.
- **Contribution calendar:** a GitHub-style SVG heatmap of the last 52 weeks (plus the current one). Each day counts the feed events above, plus one per pushed commit. Hover a day for its count. Colors use the MudBlazor palette, so it works in both themes. On narrow screens it scrolls and opens on the newest weeks.
- **Links:** issue and pull request authors, commenters, activity actors and assignees on the issue and pull request pages link to profiles, and so do names in activity feeds.
- **Dashboard:** "Activity from your projects" uses the same feed across every project you can see.

### Global search

- **App bar:** type in **Search or jump to…** (or press `/`). After a short pause it suggests the top 3 issues, pull requests, projects and users. The first suggestion is the issue when you type a key such as `WEB-12`, otherwise "Search for …", so **Enter** jumps to the issue or opens the results page.
- **`/search?q=`** groups the results into issues (by key, title or description), pull requests (title, description), projects (key, name, description) and users (username, display name). Each group has a tab with its total. A query that is a visible issue key redirects straight to that issue.
- **Code** is a separate tab, because it runs `git grep` (`CodeSearchService`). It searches the default branch of each visible, non-archived repository, as plain case-insensitive text, with caps of 25 repositories, 20 matches per repository and 100 matches in all. Use a project's **Code search** for regular expressions and other branches.
- **Matching** is SQL `LIKE` (case-insensitive for ASCII in SQLite), with `%`, `_` and `\` in your query matched literally. There's no full-text index.

### Keyboard shortcuts

Press `?` for the list. Shortcuts are ignored while you type in an input, textarea, select or contenteditable, and while a dialog is open. The switch in the `?` dialog turns them off for this browser (`localStorage` key `gitbench-shortcuts`).

| Keys | Action |
|---|---|
| `?` | Show the shortcuts |
| `/` | Focus the search box |
| `g d` / `g e` | Dashboard / Explore |
| `g i` / `g b` / `g p` / `g r` | The current project's issues / board / pull requests / repositories |
| `c` | New issue in the current project |
| `j` / `k`, then `o` or Enter | Move the selection on an issue list, then open it |
| `e` / `a` | On an issue: edit the description / assign to me |

The script is a file (`wwwroot/js/shortcuts.js`) with one `keydown` listener and no inline handlers, so it works under the CSP. It calls back into `MainLayout.ShowShortcutHelp` for the dialog. Pages opt buttons in with `data-shortcut="edit"` and `data-shortcut="assign-me"`.

### API

| Method | Path | |
|---|---|---|
| GET | `/api/v1/search?q=&type=` | `type` is `issues`, `pull_requests`, `projects`, `users` or `code`. Without it, every type except code is searched. `perPage` caps each group. `jumpUrl` is set for a visible issue key. |
| GET | `/api/v1/users/{username}` | Profile: member since, visible projects with roles, and the latest 30 activity items you can see |
| GET | `/api/v1/user/starred` | Projects you have starred |
| PUT / DELETE | `/api/v1/projects/{key}/star` | Star / unstar. Returns `{ project, starred, stars }`. |

## Deploying GitBench

GitBench is one ASP.NET Core process with a SQLite database and three folders: repositories, attachments (which also hold release assets), and the Data Protection key ring. Run it as a single instance. SQLite and the in-process queues (CI, webhooks, AI review) aren't built for several replicas.

### Docker

```bash
docker compose up -d --build        # builds the image from the Dockerfile and starts it on 127.0.0.1:8080
docker compose logs -f gitbench     # JSON logs
```

- **Image.** The `Dockerfile` is multi-stage: an SDK build, then an `aspnet:10.0` runtime with `git`, `curl` and `sqlite3`. It runs as the non-root `app` user (uid 1654). State lives in four volumes: `/data/db`, `/data/repos`, `/data/attachments` and `/data/keys`.
- **Image defaults.** The image sets:
  - `ASPNETCORE_ENVIRONMENT=Production`
  - `Seed__DemoData=false`, so no demo users with the published password
  - `Ci__Enabled=false`
  - `Security__HttpsRedirection=false`, because TLS ends at the proxy
- **First admin.** Register your account in the UI, then restart once with `Bootstrap__AdminUsers=<your username>`; separate several usernames with commas. The setting only ever promotes users, so it's fine to leave it set.
- **TLS.** Production cookies are `Secure`, so serve GitBench over HTTPS. `docker-compose.yml` has a commented-out Caddy service that gets certificates automatically: uncomment it, set the domain, and remove the `ports:` mapping of `gitbench`. For a quick trial over plain HTTP, set `Security__SecureCookies=false` instead. GitBench logs a warning when it sees plain HTTP with Secure cookies on.
- **Health checks.** The container health check calls `/health/live`; compose checks `/health/ready`.
- **Git over SSH.** The image turns the SSH server on (`Ssh__Enabled=true`) on port 2222 (`EXPOSE 2222`); its host key lives in the `/data/keys` volume, so it survives upgrades. `docker-compose.yml` publishes it as `2222:2222`. To offer the usual `git@host:KEY/slug.git` URLs, map the host's port 22 instead (`"22:2222"`, after moving the host's own sshd to another port) and set `Ssh__PublicPort=22`; set `Ssh__PublicHost` if the SSH host name differs from the web one. The TLS proxy doesn't carry SSH: publish the port directly or use a TCP (layer 4) proxy.

**CI in the container.** Pipelines need a Docker daemon, so they're off by default. There are three options:

1. **Mount the host's Docker socket** (`/var/run/docker.sock`).
   - Build the image with `--build-arg WITH_DOCKER_CLI=true`, mount the socket and set `Ci__Enabled=true`.
   - Set `Ci__WorkDirectory` to a folder mounted at the **same path** on the host and in the container, because the daemon mounts workspaces by host path.
   - The socket's group must let uid 1654 use it (`group_add`).
   - **Security:** the Docker socket gives root on the host. Anyone who can push can run any image, so every Developer effectively becomes root on the host. Only do this on a dedicated machine where you trust everyone who can push.
2. **Run GitBench on the host** (`dotnet GitBench.dll` under systemd or similar) with Docker installed. The trust caveat is the same, without the extra container layer. Rootless Docker reduces the risk.
3. **Use a separate Docker host.** Point the CLI at a remote daemon with `DOCKER_HOST=tcp://ci-host:2376`, using TLS (`DOCKER_TLS_VERIFY=1` and `DOCKER_CERT_PATH`). Builds then can't touch the GitBench host. `Ci:WorkDirectory` must be on storage the CI host sees at the same path. This is the safest option.

Whichever you choose, keep `Ci:Network=none` unless jobs need the network (see [CI pipelines](#ci-pipelines)).

### Configuration reference

Settings come from these sources; later ones override earlier ones:

1. `appsettings.json`
2. `appsettings.{Environment}.json`
3. environment variables, written `Section__Key` (e.g. `Ci__Enabled=false`), with arrays as `Section__Key__0`
4. command-line arguments

| Key | Default | Meaning |
|---|---|---|
| `ConnectionStrings:Default` | `Data Source=gitbench.db` | SQLite connection string |
| `PublicBaseUrl` | learned from the first request | Absolute site URL for links in API responses and webhooks. **Set it in production** |
| `AllowedHosts` | `*` | Host header filter (e.g. `gitbench.example.com`) |
| `Seed:DemoData` | `true` in Development, else `false` | Seed demo users, projects and repositories into an empty database |
| `Bootstrap:AdminUsers` | none | Usernames made site admins at startup |
| `Git:RepositoryRoot` | `{ContentRoot}/repos` | Bare repositories |
| `Git:Executable` | `git` on `PATH` | The git used for Smart HTTP, `git grep` and `git archive` |
| `Attachments:Root` | `{ContentRoot}/attachments` | Attachment and release asset files |
| `Attachments:MaxBytes` / `Attachments:MaxPerIssue` | 25 MB / 50 | Issue attachment limits |
| `Releases:MaxAssetBytes` | 512 MB | Size limit per release asset |
| `Archives:MaxBytes` / `TimeoutSeconds` / `MaxConcurrent` | 1 GB / 300 / 4 | Source archive limits |
| `DataProtection:KeysPath` | `{ContentRoot}/keys` | Key ring folder (created with mode 700). Back it up and keep it private |
| `DataProtection:ApplicationName` | `GitBench` | Keep it the same across deployments that share keys |
| `DataProtection:CertificatePath` / `CertificatePassword` | none | A `.pfx` that encrypts the keys at rest. Recommended on Linux, where the keys are otherwise stored as plain XML |
| `DataProtection:DecryptionCertificatePaths` | none | Older `.pfx` files that are still accepted for reading keys after a certificate rotation |
| `ReverseProxy:Enabled` | `false` | Honour `X-Forwarded-For` and `X-Forwarded-Proto` from trusted proxies |
| `ReverseProxy:KnownProxies` / `KnownNetworks` | loopback only | Trusted proxy IPs / CIDR ranges (e.g. `172.16.0.0/12` for compose networks) |
| `ReverseProxy:ForwardLimit` | `1` | Number of proxy hops to unwind |
| `ReverseProxy:ForwardHost` | `false` | Also honour `X-Forwarded-Host` (then set `ReverseProxy:AllowedHosts`) |
| `Security:SecureCookies` | `true` outside Development | Send sign-in cookies only over HTTPS |
| `Security:HttpsRedirection` | `true` outside Development | Redirect HTTP to HTTPS (the Docker image turns it off) |
| `Security:HstsMaxAgeDays` / `HstsIncludeSubDomains` | 365 / `false` | HSTS settings. HSTS is sent on HTTPS responses outside Development |
| `Security:ContentSecurityPolicy` | `true` | Send the CSP on HTML pages |
| `Security:ContentSecurityPolicyReportOnly` | `false` | Send the CSP as `Content-Security-Policy-Report-Only` instead, while testing |
| `Security:CspInlineHandlers` | none | Extra inline `on…=""` handler strings to allow by hash |
| `RateLimiting:Enabled` | `true` (`false` in `appsettings.Development.json`) | Turns all rate limits on or off |
| `RateLimiting:Auth` | 10 / 60 s | POSTs to `/account/*` (sign-in, registration, two-factor), per client IP |
| `RateLimiting:Api` | 300 / 60 s | `/api/*`: per access token, else per signed-in user, else per IP |
| `RateLimiting:Git` | 120 / 60 s | `/git/*`: per user named in the credentials, else per IP |
| `RateLimiting:WebhookPing` | 5 / 60 s | Webhook test pings, per user |
| `RateLimiting:Ssh` | 120 / 60 s | Git commands over SSH, per user or deploy key. Over the limit, git prints "Too many requests. Try again in N seconds." |
| `Ssh:Enabled` | `false` (`true` in Development and the Docker image) | Run the git SSH server (see [Git over SSH](#git-over-ssh)) |
| `Ssh:Port` / `Ssh:ListenAddress` | 2222 / all interfaces | Where the SSH server listens |
| `Ssh:HostKeyPath` | `{DataProtection:KeysPath}/ssh_host_rsa_key` | SSH host key (PEM), generated on first start. Keep it with the key ring |
| `Ssh:PublicHost` / `Ssh:PublicPort` | the site's host / `Ssh:Port` | Host and port shown in SSH clone URLs, e.g. `22` when the host maps port 22 to the container's 2222 |
| `Ssh:MaxConnections` / `Ssh:AuthTimeoutSeconds` | 200 / 30 | Simultaneous SSH connections; seconds a connection may take to authenticate |
| `Ci:*` | see [CI pipelines](#ci-pipelines) | `Enabled`, `MaxConcurrentJobs`, `JobTimeoutMinutes`, `Network`, `Memory`, `Cpus`, `PidsLimit`, `DockerExecutable`, `WorkDirectory`, `MaxLogChars` |
| `Webhooks:AllowPrivateNetworks` | `false` | Allow webhook targets on private networks |
| `Audit:RetentionDays` | 365 | Audit event retention (`0` keeps them forever) |
| `Ai:Enabled` / `Ai:Model` / `Ai:ApiKey` | `true` / `claude-opus-5-5` / none | AI features. Prefer `ANTHROPIC_API_KEY` for the key |
| `Logging:*` | | Standard .NET logging. `appsettings.Production.json` switches the console to JSON and keeps EF Core's SQL logging at Warning |

**Rate limits.** Each rate limit section takes `PermitLimit` and `WindowSeconds`, e.g. `RateLimiting__Api__PermitLimit=600`. A `PermitLimit` of `0` turns that limit off. A request over the limit gets `429` with `Retry-After`: problem+json for the API, plain text elsewhere. For webhook test pings, the page shows "Too many test pings. Try again in N seconds."

**Secrets** belong in environment variables or a secrets store, never in `appsettings.json` or the image. Examples: `ANTHROPIC_API_KEY`, `DataProtection__CertificatePassword`, SMTP passwords. With compose, use an `env_file` with mode 600, or Docker secrets.

### Security headers & health checks

**Headers.** Every response gets:

- `X-Content-Type-Options: nosniff`
- `X-Frame-Options: DENY`
- `Referrer-Policy: strict-origin-when-cross-origin`
- `Permissions-Policy`
- `Cross-Origin-Opener-Policy: same-origin`
- HSTS on HTTPS, outside Development

**Content Security Policy.** HTML pages get a CSP with `default-src 'self'`:

- **Scripts** come from the site. Blazor's import map is allowed by a per-request nonce, and the app's few inline handlers (`onfocus="this.select()"` and the copy buttons) by hash.
- **Styles** allow `'unsafe-inline'`, because components set `style` attributes (label colours, charts).
- **`connect-src`** covers the circuit's `ws:`/`wss:` connection to the same host.
- The policy also sets `frame-ancestors 'none'`, `form-action 'self'` and `object-src 'none'`.
- Responses with their own CSP (attachments, release assets) keep theirs.
- New inline `<script>` blocks or `on…=""` attributes will be blocked. Put the code in a `.js` file in `wwwroot/js`, or add the handler string to `Security:CspInlineHandlers`.

**Health checks.** Both endpoints are anonymous, return JSON without error details, and aren't rate limited.

- `GET /health/live` returns 200 while the process runs.
- `GET /health/ready` checks that:
  - the database answers, and the repository and attachment folders are writable. If not, it reports `Unhealthy` (503).
  - Docker is reachable when CI is on. If not, it reports `Degraded` but still returns 200.

### Reverse proxy

- **TLS and trust.** Terminate TLS at the proxy and forward to GitBench's HTTP port. Set `ReverseProxy__Enabled=true` and put the proxy's address in `KnownProxies` or `KnownNetworks`. The audit log, rate limits and git logs then see the real client IP, and cookies, HSTS and generated links see `https`. Requests from untrusted addresses keep their own IP, so clients can't spoof it with headers.
- **Headers.** Pass the original `Host` header. Caddy does this by default. With nginx, set `proxy_set_header Host $host;`, plus `X-Forwarded-For $proxy_add_x_forwarded_for` and `X-Forwarded-Proto $scheme`.
- **WebSockets.** Blazor needs them on `/_blazor`. With nginx, use `proxy_http_version 1.1; proxy_set_header Upgrade $http_upgrade; proxy_set_header Connection $connection_upgrade;` and a long `proxy_read_timeout`.
- **Large and long requests.** Git pushes and uploads need large request bodies and long timeouts. With nginx, set `client_max_body_size 0; proxy_request_buffering off; proxy_buffering off;` for `/git/`.
- **Public URL.** Set `PublicBaseUrl` to the public `https://` URL.

### Upgrades & backups

1. **Back up first.** `scripts/backup.sh [out-dir]` writes `gitbench-backup-<UTC time>.tar.gz`. It contains a consistent SQLite copy (made with `sqlite3 .backup`, safe while GitBench runs), the repositories, the attachments and the key ring.
   - If those aren't in the checkout, set `GITBENCH_DB`, `GITBENCH_REPOS`, `GITBENCH_ATTACHMENTS` and `GITBENCH_KEYS`.
   - The image includes the script at `/app/scripts/backup.sh`; its header has the `docker compose exec` command.
   - The archive contains password hashes, webhook secrets and the keys, so store it encrypted.
2. **Deploy the new version**: `git pull && docker compose up -d --build`, or replace the published files and restart.
3. **Migrations run automatically at startup.** Watch the log for `Applying migration`. If startup fails, restore the backup and run the previous version.

**Restore**:

1. Stop GitBench and extract the archive.
2. Copy `db/gitbench.db` over the database, and delete any `-wal`/`-shm` files next to it.
3. Replace the `repos`, `attachments` and `keys` folders with the archived ones.
4. Start GitBench.

Without the original `keys`, everyone has to sign in again and encrypted secrets can't be read.

## Next steps

- **More features:** email delivery of notifications, @mention autocomplete.
