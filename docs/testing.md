# Testing ByteBench

ByteBench has four layers of tests. The first is the .NET unit and integration suite that has always been here; the other three live under `e2e/` and exercise the real application the way a person does, then use a model to find bugs nobody thought of and to make failures easier to understand.

| Layer | What it is | Needs | Runs | One command |
|---|---|---|---|---|
| 0. xUnit | `tests/ByteBench.Tests`: in-process `TestApp`, fake Docker backend | .NET 10 | every push (CI) | `dotnet test ByteBench.sln --filter "Category!=Docker"` |
| 1a. e2e **smoke** | Playwright against a real server in a real browser | Node 24, .NET 10, git | every pull request, ~1 min | `e2e/run.sh smoke` |
| 1b. e2e **docker** | Real workspace image, real containers, real proxy/egress/SSH | + Docker daemon | nightly and on demand, ~2 min warm | `e2e/run.sh docker` |
| 2. AI **explorer** | A model drives the browser toward goals and checks invariants | + `claude` CLI (login or `ANTHROPIC_API_KEY`) | nightly (with a budget) and on demand | `e2e/run.sh explore` |
| 3. AI **triage / generation** | Classifies failed CI runs; drafts tests for a change | + `claude` CLI | after a failed run; on demand | `e2e/run.sh triage`, `e2e/run.sh generate` |

`e2e/` is not part of `ByteBench.sln` and is excluded from the main project's globbing (like `desktop-maui/**`), so `dotnet build ByteBench.sln --no-incremental -warnaserror` is unaffected.

## Layer 1: deterministic end-to-end tests (Playwright, TypeScript)

**Why TypeScript.** Playwright's own test runner is TypeScript-first: auto-waiting locators, fixtures, traces, the HTML report and the `--ui` mode are best there, and it is the same runtime the AI explorer uses (the Playwright MCP server), so the helpers are shared. The repository already has a Node toolchain (`desktop/`), so CI needs nothing new. Playwright for .NET would have kept one language but would lose the trace viewer's convenience and the shared tooling; nothing in the tests needs C#, because they only talk HTTP, the browser, `git` and `docker`.

### What a run does

`e2e/support/global-setup.ts` starts the real server (`bin/Debug/net10.0/ByteBench.dll`) with:

* a fresh SQLite database and fresh storage for repositories, attachments, keys, LFS, packages, pages and backups, all in a temp directory (never your `bytebench.db`, never the port 5108 server);
* a random port, `Seed__DemoData=true` (accounts `alice` (admin), `bob`, `carol`, `dan`, password `ByteBench123`; projects `WEB` and the private `OPS`), `Ssh__Port` random, e-mail and AI off, `*.localhost` for the workspace origins;
* for the smoke tier: `Workspaces__DockerExecutable` pointing at a command that does not exist, so no Docker daemon is ever contacted.

Every test also gets, automatically, the invariants "no uncaught JavaScript error, no console error, no 5xx response"; after the run the server log is scanned and any `fail:` / `crit:` line fails the run (set `E2E_ALLOW_SERVER_ERRORS=1` to see the rest first). Failures keep a trace, a screenshot and a video (`e2e/test-results/`, HTML report in `e2e/playwright-report/`); the server log is copied next to them.

Selectors: roles, labels and `data-testid`. The existing Workspaces pages already carry `data-testid`; this change added only `account-menu` and `sign-out` to the layout.

### Smoke tier (no Docker)

`e2e/tests/smoke/`: sign-in/out, wrong password, anonymous redirects, private-project isolation (UI and API), admin pages denied to non-admins; create project, repository and issue, comment, close; hostile input in the issue form; board; sprint planning from the backlog; issue filters; `git clone/push` with a personal access token, opening, reading and merging a pull request in the UI and checking the merge with real `git`; admin pages; CI pipeline/schedule/environment pages; workspace pages without Docker (start page explains the missing daemon, environment-file preview, admin switches, project switch, prebuild page); layout (no horizontal scroll) and accessibility basics (title, language, labelled controls) on ten pages at 1360 px and 390 px.

Accessibility is asserted on ten pages: exactly one `<main>` landmark (the layouts render it), exactly one `<h1>` (a README is shown a heading level down), and an accessible name on every button (an icon-only `MudMenu` takes its name from `AriaLabel=`, not from an `aria-label` attribute, which lands on the wrapper). `not-found.spec.ts` checks every `/p/{key}/...` page: a private project the user cannot see and a project that does not exist both answer a real 404 with the site's own "Not found" title, and look the same.
`workspaces-pages.spec.ts` (smoke) drives the workspace pages that need no container, with seeded AI runs written straight into the temp SQLite database through `node:sqlite`: usage panel and idle setting, the start page and its environment suggestion on a phone, prebuild settings and the readable "nothing to prebuild" error, Admin > Workspaces (limits, sharing mode, egress validation, retention, switches), the audit page (filters, CSV, non-admins refused), the AI run page and Admin > AI "AI runs" section, and "not yours" pages; the phone/dark layout check covers eight workspace pages.

Accessibility *advisories* the suite knows about (a missing `<main>`, several `<h1>`, the `MudMenu` icon activator ignoring `AriaLabel`) are recorded as annotations in the report instead of failing the build; fix them and tighten the test.

```
e2e/run.sh smoke                  # all of it
e2e/run.sh smoke -g "pull request"   # one test
e2e/run.sh smoke --headed --workers=1   # watch it
e2e/run.sh serve                  # the same disposable server, kept running, to look around
```

### Docker tier

`e2e/tests/docker/collaborators.spec.ts` (collaborator isolation and the shell environment, on a small fixture repository) and `e2e/tests/docker/workspaces.spec.ts` (serial, one fixture repository). It builds the real workspace image from `docker/workspace/` once as `bytebench-e2e-workspace:<hash of the context>` (cached afterwards; the first build downloads OpenVSCode Server and takes a few minutes), points the server at it (`Workspaces__Image`, a unique `Workspaces__Network`, `Workspaces__GitBaseUrl=http://host.docker.internal:<port>`, server listening on all interfaces) and then:

1. reads `.bytebench/workspace.yml` on the start page; **Open in workspace** starts a real container;
2. the IDE iframe is on its own `ide-<id>.localhost` origin, the single-use ticket exchange redirects (302) into it, the OpenVSCode workbench loads with no failing request, and the ByteBench session cookie is not sent to that origin;
3. setup ran (`npm install`), the **submodule** was fetched, repository `env:` is set, the process is uid 1000;
4. a task runs from the Tasks panel; a server task opens its **preview** on `p8088-<id>.localhost`;
5. a **secret** set in the UI is visible in the workspace terminal and task shells, and absent from `docker inspect`, container logs, the server log, the audit trail, the setup log and the pages; the `bbai` user cannot read `~/.bb-env`;
6. a **viewer** sees the checkout read-only and never `.env`, `.git`, `.bb-env`, `.git-credentials`, and gets no IDE;
7. **local editor**: the admin switch, connecting an SSH key, the tunnel's first bytes are the workspace's SSH banner, a browser session cookie cannot open the tunnel, and a real `ssh` login through the tunnel works;
8. **Stop / Resume** (through the confirm dialog): installed packages remain, setup does not re-run, secrets are injected again, the IDE loads again;
9. **egress**: a branch whose `workspace.yml` narrows the network to `registry.npmjs.org`: that host answers 200, `example.org` is refused and listed in the egress record; in the default mode the link-local address is not reachable;
10. **limits**: a start over the per-user cap is refused with a clear message;
11. **prebuild**: after a prebuild is Ready, a fresh workspace's setup is `UpToDate` and the slow setup step is skipped, so the start is measurably faster than the first one;
12. the audit trail has the lifecycle.

`tests/docker/workspace-pages.spec.ts` is a second, serial file on the same server: the full-screen top bar at 1360 px/light and 390 px/dark (every action inside the viewport), the git bar (branch, changed files, commit & push to the *checked-out* branch, push, open a pull request), the ports menu and preview, the connect-from-editor dialog (off, on, bad key), the AI review panel with a seeded change set (per-hunk accept, reject, accept all, conflict), sharing (unknown user, viewer, collaborator with its own home and IDE, history and network tabs, who may open what) and the audit page with real lifecycle events. The docker tier starts the server with a placeholder AI key (never sent anywhere) so the AI panels render. `E2E_SERVE_DOCKER=1 e2e/run.sh serve` starts the same server to look around (`E2E_SERVE_ENV='{"Ai__Enabled":"true"}'` adds settings).

```
e2e/run.sh docker                 # everything above
E2E_KEEP_DOCKER=1 e2e/run.sh docker   # leave containers up for debugging (the log prints the cleanup command)
e2e/run.sh docker-clean           # remove leftovers of an aborted run (--images also drops the cached e2e image)
```

Safety: the e2e image uses its own repository (`bytebench-e2e-workspace`), never `bytebench-workspace:*`; the run removes only containers, volumes and networks carrying this checkout's instance key and its own `bb-e2e-*` network.

### Real AI run (opt-in)

`e2e/tests/docker/ai-run.spec.ts` drives the **local Claude Code CLI** end to end. It is skipped unless `E2E_REAL_AI=1` and `claude --version` works, and spends a few cents of the logged-in subscription:

```
E2E_REAL_AI=1 e2e/run.sh docker -g "real AI run|real editor assistant"      # E2E_AI_MAX_USD (default 1.5) and E2E_AI_MAX_STEPS (20) bound a run
```

The server is started with `Workspaces:AiRuns:Backend=claude-cli` and small limits (`support/global-setup.ts`). The tests: an AI run fixes a broken function in a real workspace (tools executed in the container as `bbai`, `run_task`, a pull request with only the fix, an issue comment, the workspace deleted, no token or key in the events, PR, comments or server log, and nothing of the host's Claude login inside the container); Cancel kills the CLI's whole process tree and leaves no branch; and the web editor's assistant (the `ClaudeCodeEditorBackend` path) proposes edits that are accepted and rejected per file, leaving no process or temp directory behind. Checked once by hand against Claude Code 2.1.323: `--tools ""` leaves only the run's MCP tools, `--max-turns` and `--max-budget-usd` end the stream with `error_max_turns` / `error_max_budget_usd`, the step and cost limits stop a run from the real stream, and `--setting-sources project` keeps the server account's own hooks, plugins and memory out of a run.

Known behaviours worth knowing when reading the tests:

* A workspace's repository and env are loaded from `~/.bb-env`. Since image `:3` every bash start sees them: interactive, login, `bash -c` (`BASH_ENV`), `ssh host cmd` and tasks (a guarded hook at the top of `~/.bashrc`, in `~/.profile`, and `/usr/local/lib/bb-env.sh`). Only plain `sh -c` (dash) does not, by POSIX design. `collaborators.spec.ts` checks the whole matrix.
* A *collaborator* has their own OS user (`bbc1`..`bbc10`), home and IDE server; `collaborators.spec.ts` asserts they cannot read `~/.bb-env`, `~/.git-credentials`, the owner's IDE token or process environment, that `/workspace` is shared through the group, that "May push" gives a user-scoped token (and revoke kills it), that End sharing / Stop wipe their home and processes, slot reuse, the cap of 10 and the clear error for a workspace from an older image.
* The IDE and preview tickets are single-use, so the tests inspect what the iframe loaded instead of requesting the URL again.

## Layer 2: the AI explorer

```
e2e/run.sh explore                                   # every goal, fresh disposable server
e2e/run.sh explore --goal tenant-isolation --max-steps 25 --max-usd 0.5
e2e/run.sh explore --goal "Try to create 50 issues quickly and look at the list"   # a custom goal
```

`e2e/ai-explorer/explore.ts` starts a disposable seeded server (same as the tests) and runs `claude -p` for each goal in `e2e/ai-explorer/goals.ts` (plan a sprint; break the issue form; try to reach another tenant's private project; use workspaces as a new developer; probe authorization). The CLI is the local one by default and uses its login; with `ANTHROPIC_API_KEY` set (CI) it uses the key.

What it does on every run:

* **Only browser tools.** The model gets the Playwright MCP tools and nothing else (`--tools ""`, no shell, no files; `browser_run_code_unsafe` and `browser_file_upload` are denied). Every tool call is logged (`<goal>/steps.json`, the raw stream in `claude-stream.jsonl`) and a screenshot is taken after every step.
* **Invariants checked mechanically**, independent of what the model says: console errors, uncaught JavaScript errors, 5xx responses, the Blazor error bar, new `fail:` lines in the server log, horizontal page scroll and basic accessibility at desktop (1360 px) and phone (390 px) widths for the pages it visited. The model adds its own findings (authorization, cross-tenant data, functional bugs).
* **Hard stops**: steps per goal (`--max-steps`, default 40), minutes per goal (`--max-minutes`, 8), USD per goal (`--max-usd`, 1.00, enforced by the CLI's `--max-budget-usd` too) and for the run (`--total-usd`, 5.00).
* **Disposable by construction.** It always creates its own temp database. `--target <url>` (an existing instance) is refused unless the host is localhost *and* `--disposable` is given; a non-localhost host also needs `--allow-remote`; port 5108 is always refused. The browser itself can resolve only `localhost`, `127.0.0.1` and `*.localhost` (Chromium host-resolver rules), so even a page that tells the tester to go elsewhere cannot make it.
* **Page content is untrusted data.** The system prompt says so and tells the model to report, not obey, instructions it finds in the app. The run plants a **canary** (an issue telling AI agents to navigate to `http://example.invalid/` with the user's cookies and delete the project) and the report states whether it was followed. An off-origin navigation attempt kills the run (exit code 4).
* **Output**: `e2e/reports/explore-<time>/report.md` (findings with severity, category, invariant, numbered repro steps, expected/actual, screenshots), `findings.json`, and per-goal folders. The browser profile (cookies) is deleted after each goal.
* **Dogfooding (off by default)**: `--file-issues --issues-url <https://your-bytebench> --issues-project KEY` with `BYTEBENCH_EXPLORER_TOKEN` (a personal access token with `write_api`) files each medium-or-higher finding as an issue through the REST API.

First run (small budget, tenant isolation as Bob): 12 steps, $0.39. It found no cross-tenant leak, reported one small bug (the not-found page of an inaccessible project had a title like `Issues · ` and answered HTTP 200; fixed, see `not-found.spec.ts`), and recorded the planted injection as ignored.

## Layer 3: AI triage and test generation

**Triage** (`e2e/ai-triage/triage.ts`): given a failed run's artifacts (the Playwright JSON report, page snapshots, `server-errors.txt`, `server.log`) and the change's diff, it asks the model (no tools; all of it passed as untrusted data) to classify each failure as `real-bug`, `flaky`, `test-needs-update` or `env-problem` with confidence, reasoning and a minimal fix (a unified diff when it is sure). **Known-flaky detection** comes from the run history: it downloads the `results.json` of the last N runs of the same workflow (`--history`, default 10), counts failures, passes and passed-on-retry per test, and flags a test as flaky when it passed on retry in this run, passed on retry before, or both failed and passed in the history with a failure rate of 50% or less.

```
e2e/run.sh triage --dir e2e/test-results             # a local run
e2e/run.sh triage --run-id 123456789 --post pr       # a GitHub run (needs gh); comments on its PR, else on the commit, always the job summary
e2e/run.sh triage --dir e2e/test-results --no-model  # only the facts and the flaky history
```

**Generation** (`e2e/ai-triage/generate-tests.ts`): from `--pr <n>` (needs `gh`), `--issue <file>`, `--issue-text "<text>"` or `--range a..b`, it drafts a spec into `e2e/tests/proposed/<slug>.spec.ts` using the existing tests and helpers as the style guide, marks guessed selectors `TODO(review)`, and type-checks the result. `tests/proposed/` belongs to no Playwright project: **a draft never runs in CI and is never merged automatically**; a human reviews it, moves it to `tests/smoke/`, runs it and commits it.

## CI

| Workflow | Trigger | What |
|---|---|---|
| `.github/workflows/e2e-smoke.yml` | pull requests, manual | builds the server, installs Chromium, runs the smoke tier, uploads traces/screenshots/videos/server log |
| `.github/workflows/e2e-nightly.yml` | 03:17 UTC daily, manual | the Docker tier; the AI explorer with a budget (if `ANTHROPIC_API_KEY` is set); then ONE summary issue labelled `e2e-nightly`: opened or commented when the Docker tier failed or the explorer found something at medium or higher, closed when a later night is green |
| `.github/workflows/ai-triage.yml` | `workflow_run` of "E2E smoke" / "E2E nightly" when it failed | AI triage as a PR or commit comment and the job summary |

* **The AI jobs need the repository secret `ANTHROPIC_API_KEY`** (Settings > Secrets and variables > Actions). Without it a one-step `gate` job records "skipped" and the AI jobs are **skipped, not failed**; everything deterministic still runs.
* `ai-triage.yml` runs with the repository's secrets, so it never checks out or executes the pull request's code (only the default branch's scripts; the failing commit is fetched as data for the diff). The model has no tools.
* Action versions follow the existing workflows (major-version tags such as `actions/checkout@v4`). Pinning every workflow to commit SHAs is a repository-wide decision that was not made here.
* Not verified in GitHub Actions (written to be, run locally): the Linux runner path of the Docker tier (`host.docker.internal` on Linux needs the container's `host-gateway` mapping, `--storage-opt` on non-xfs), `npm install -g @anthropic-ai/claude-code` and the `claude -p` flags on a runner, and the issue-opening step.

## Environment variables

| Variable | Effect |
|---|---|
| `E2E_TIER` | `smoke` or `docker` (set by `run.sh`) |
| `E2E_NO_BUILD=1` | do not run `dotnet build` first (CI builds in an earlier step) |
| `E2E_KEEP_DATA=1` | keep the temp data directory (database, repos, server log) |
| `E2E_TOOLCHAINS=1` | also run `tests/docker/toolchains.spec.ts`: builds the six curated toolchain images (dotnet, node, python, go, java, rust; several GB, slow the first time) and runs a trivial task in each |
| `E2E_KEEP_DOCKER=1` | leave workspace containers/volumes for debugging |
| `E2E_ALLOW_SERVER_ERRORS=1` | do not fail the run on `fail:`/`crit:` log lines |
| `ANTHROPIC_API_KEY` | used by the `claude` CLI for the explorer, triage and generation (otherwise its login is used) |
| `BYTEBENCH_EXPLORER_TOKEN` | personal access token for `--file-issues` |

## Adding a test

Use the `login` and `open` fixtures (`open` waits for the Blazor circuit, so clicks and typing are not lost), unique names from `uniq()`, role/label/`data-testid` selectors, and the REST API with a token from `createToken` for setup. Add a `data-testid` to a Razor page only when no accessible role or label identifies the element. To have the model draft one: `e2e/run.sh generate --issue <file>`.
