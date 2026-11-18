using GitBench.Services;
using GitBench.Services.Git;

namespace GitBench.Data;

/// <summary>
/// Demo code-hosting data: a <c>web-app</c> repo in project WEB with history on <c>main</c>, a feature branch for
/// WEB-3 and an open, reviewed pull request. Idempotent: does nothing once the repository row exists.
/// </summary>
public static class RepoSeeder
{
    public static void Initialize(AppDbContext db, RepoStorage storage)
    {
        var web = db.Projects.FirstOrDefault(p => p.Key == "WEB");
        if (web == null || db.Repositories.Any(r => r.ProjectId == web.Id && r.Slug == "web-app")) return;

        var users = db.Users.Where(u => new[] { "alice", "bob", "carol", "dan" }.Contains(u.UserName)).ToDictionary(u => u.UserName!);
        if (users.Count < 4) return;
        GitIdentity Id(string name) => RepositoryService.IdentityOf(users[name]);

        // A repo directory without a row is left over from a previous (deleted) database: rebuild it.
        var path = storage.PathFor(web.Key, "web-app");
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);

        var now = DateTimeOffset.Now;
        DateTimeOffset Ago(double days) => now.AddDays(-days);
        GitOps.InitBare(path, "main");

        // ----- main -----
        GitOps.CommitFiles(path, "main", new Dictionary<string, string?>
        {
            ["README.md"] = Readme,
            [".gitignore"] = "node_modules/\ndist/\n.env\n",
        }, "Initial commit", Id("alice"), Ago(20));

        GitOps.CommitFiles(path, "main", new Dictionary<string, string?>
        {
            ["package.json"] = PackageJson("^5.4.0"),
            ["index.html"] = IndexHtml,
            ["src/main.tsx"] = MainTsx,
            ["src/App.tsx"] = AppTsx,
        }, "Add Vite + React app skeleton", Id("alice"), Ago(18.6));

        GitOps.CommitFiles(path, "main", new Dictionary<string, string?>
        {
            [".github/workflows/ci.yml"] = CiYml,
        }, "WEB-1: Add CI workflow\n\nRuns lint, type-check and tests on every push and pull request.", Id("alice"), Ago(17.2));

        GitOps.CommitFiles(path, "main", new Dictionary<string, string?>
        {
            ["src/auth/validateEmail.ts"] = ValidateEmail,
            ["src/auth/validateEmail.test.ts"] = ValidateEmailTest,
        }, "WEB-2: Accept plus-addressed emails on login\n\nThe old pattern rejected '+' in the local part.", Id("bob"), Ago(12.4));

        GitOps.CommitFiles(path, "main", new Dictionary<string, string?>
        {
            ["src/board/Board.tsx"] = BoardV1,
            ["src/board/Column.tsx"] = ColumnV1,
            ["src/board/types.ts"] = BoardTypes,
        }, "Add read-only board with status columns", Id("alice"), Ago(6.3));

        // ----- feature branch for WEB-3 -----
        const string feature = "feature/WEB-3-kanban-dnd";
        GitOps.CommitFiles(path, feature, new Dictionary<string, string?>
        {
            ["src/board/Card.tsx"] = Card,
            ["src/board/Column.tsx"] = ColumnV2,
        }, "WEB-3: Make board cards draggable", Id("alice"), Ago(3.2), startPoint: "main");

        GitOps.CommitFiles(path, feature, new Dictionary<string, string?>
        {
            ["src/board/useBoardDnd.ts"] = UseBoardDnd,
            ["src/board/Board.tsx"] = BoardV2,
        }, "WEB-3: Persist card order after drop\n\nOrder is stored as a float between neighbours so a move is a single update.", Id("alice"), Ago(2.1));

        GitOps.CommitFiles(path, feature, new Dictionary<string, string?>
        {
            ["src/board/Card.tsx"] = CardV2,
        }, "WEB-3: Keyboard support for moving cards", Id("alice"), Ago(1.4));

        // main moves on after the branch was cut, so the feature branch is 1 behind.
        GitOps.CommitFiles(path, "main", new Dictionary<string, string?>
        {
            ["package.json"] = PackageJson("^5.4.8"),
        }, "Bump vite to 5.4.8", Id("carol"), Ago(1.9));

        // ----- metadata + pull request -----
        var repo = new Repository
        {
            ProjectId = web.Id, Slug = "web-app", Name = "web-app",
            Description = "Customer-facing React front end.", CreatedAt = Ago(20).UtcDateTime,
        };
        db.Repositories.Add(repo);
        db.SaveChanges();

        var opened = Ago(1.3).UtcDateTime;
        var pr = new PullRequest
        {
            RepositoryId = repo.Id, Number = 1,
            Title = "WEB-3: Drag-and-drop Kanban board",
            Description = "Implements drag-and-drop on the board.\n\n- Cards are draggable between columns\n- Order is persisted after a drop\n- Cards can be moved with the keyboard (`Alt+←/→`)\n\nCloses WEB-3.",
            SourceBranch = feature, TargetBranch = "main", AuthorId = users["alice"].Id,
            CreatedAt = opened, UpdatedAt = Ago(0.2).UtcDateTime,
        };
        pr.Activities.Add(new PullRequestActivity { ActorId = users["alice"].Id, Text = "opened this pull request", CreatedAt = opened });
        pr.Comments.Add(new PullRequestComment
        {
            AuthorId = users["bob"].Id, CreatedAt = Ago(0.9).UtcDateTime,
            Body = "Nice work! Tried it locally and dragging feels smooth. Could we debounce the save so a quick series of moves doesn't fire a request each time?",
        });
        pr.Comments.Add(new PullRequestComment
        {
            AuthorId = users["bob"].Id, CreatedAt = Ago(0.85).UtcDateTime, FilePath = "src/board/useBoardDnd.ts", Line = 12,
            Body = "Midpoint ordering will eventually run out of float precision — fine for now, maybe leave a TODO?",
        });
        pr.Comments.Add(new PullRequestComment
        {
            AuthorId = users["alice"].Id, CreatedAt = Ago(0.6).UtcDateTime,
            Body = "Good call — I'll add debouncing in a follow-up so this PR stays focused.",
        });
        pr.Reviews.Add(new PullRequestReview { UserId = users["carol"].Id, State = ReviewState.Approved, CreatedAt = Ago(0.2).UtcDateTime });
        pr.Activities.Add(new PullRequestActivity { ActorId = users["carol"].Id, Text = "approved this pull request", CreatedAt = Ago(0.2).UtcDateTime });
        db.PullRequests.Add(pr);
        db.SaveChanges();
    }

    /// <summary>A small <c>infra</c> repo in the private OPS project (for exercising access control). Idempotent.</summary>
    public static void InitializeOps(AppDbContext db, RepoStorage storage)
    {
        var ops = db.Projects.FirstOrDefault(p => p.Key == "OPS");
        var dan = db.Users.FirstOrDefault(u => u.UserName == "dan");
        if (ops == null || dan == null || db.Repositories.Any(r => r.ProjectId == ops.Id && r.Slug == "infra")) return;
        var path = storage.PathFor(ops.Key, "infra");
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        GitOps.CreateBare(path, "main", "# infra\n\nTerraform and deployment scripts for the Infrastructure project.\n", RepositoryService.IdentityOf(dan));
        db.Repositories.Add(new Repository { ProjectId = ops.Id, Slug = "infra", Name = "infra", Description = "Terraform and deployment scripts." });
        db.SaveChanges();
    }

    // ----- file contents -----

    private const string Readme = """
        # web-app

        Customer-facing web application for the **Web Platform** project.

        ## Getting started

        ```bash
        npm install
        npm run dev
        ```

        ## Scripts

        | Command | What it does |
        | --- | --- |
        | `npm run dev` | Start the Vite dev server |
        | `npm test` | Run unit tests with Vitest |
        | `npm run build` | Production build into `dist/` |

        Issues are tracked in GitBench under the `WEB` key — mention them in branch names and commits (e.g. `WEB-3`).

        """;

    private static string PackageJson(string vite) => $$"""
        {
          "name": "web-app",
          "private": true,
          "version": "0.1.0",
          "type": "module",
          "scripts": {
            "dev": "vite",
            "build": "tsc -b && vite build",
            "test": "vitest run",
            "lint": "eslint src"
          },
          "dependencies": {
            "react": "^18.3.1",
            "react-dom": "^18.3.1"
          },
          "devDependencies": {
            "typescript": "^5.5.4",
            "vite": "{{vite}}",
            "vitest": "^2.0.5"
          }
        }

        """;

    private const string IndexHtml = """
        <!doctype html>
        <html lang="en">
          <head>
            <meta charset="UTF-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1.0" />
            <title>Web Platform</title>
          </head>
          <body>
            <div id="root"></div>
            <script type="module" src="/src/main.tsx"></script>
          </body>
        </html>

        """;

    private const string MainTsx = """
        import React from "react";
        import ReactDOM from "react-dom/client";
        import { App } from "./App";

        ReactDOM.createRoot(document.getElementById("root")!).render(
          <React.StrictMode>
            <App />
          </React.StrictMode>
        );

        """;

    private const string AppTsx = """
        export function App() {
          return (
            <main>
              <h1>Web Platform</h1>
              <p>Welcome! Sign in to see your board.</p>
            </main>
          );
        }

        """;

    private const string CiYml = """
        name: CI
        on:
          push:
          pull_request:

        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - uses: actions/checkout@v4
              - uses: actions/setup-node@v4
                with:
                  node-version: 20
                  cache: npm
              - run: npm ci
              - run: npm run lint
              - run: npm test

        """;

    private const string ValidateEmail = """
        // RFC 5322 is famously hairy; this is the pragmatic subset we accept.
        const EMAIL = /^[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+$/;

        export function validateEmail(value: string): boolean {
          return EMAIL.test(value.trim());
        }

        """;

    private const string ValidateEmailTest = """
        import { describe, expect, it } from "vitest";
        import { validateEmail } from "./validateEmail";

        describe("validateEmail", () => {
          it("accepts plus addressing", () => {
            expect(validateEmail("user+tag@example.com")).toBe(true);
          });

          it("rejects missing domain", () => {
            expect(validateEmail("user@")).toBe(false);
          });
        });

        """;

    private const string BoardTypes = """
        export type Status = "todo" | "inprogress" | "review" | "done";

        export interface BoardCard {
          id: number;
          key: string;
          title: string;
          status: Status;
          order: number;
        }

        """;

    private const string BoardV1 = """
        import { Column } from "./Column";
        import type { BoardCard, Status } from "./types";

        const COLUMNS: Status[] = ["todo", "inprogress", "review", "done"];

        export function Board({ cards }: { cards: BoardCard[] }) {
          return (
            <div className="board">
              {COLUMNS.map((status) => (
                <Column key={status} status={status} cards={cards.filter((c) => c.status === status)} />
              ))}
            </div>
          );
        }

        """;

    private const string BoardV2 = """
        import { Column } from "./Column";
        import { useBoardDnd } from "./useBoardDnd";
        import type { BoardCard, Status } from "./types";

        const COLUMNS: Status[] = ["todo", "inprogress", "review", "done"];

        export function Board({ cards: initial }: { cards: BoardCard[] }) {
          const { cards, moveCard } = useBoardDnd(initial);
          return (
            <div className="board">
              {COLUMNS.map((status) => (
                <Column
                  key={status}
                  status={status}
                  cards={cards.filter((c) => c.status === status).sort((a, b) => a.order - b.order)}
                  onDrop={(id, beforeId) => moveCard(id, status, beforeId)}
                />
              ))}
            </div>
          );
        }

        """;

    private const string ColumnV1 = """
        import type { BoardCard, Status } from "./types";

        export function Column({ status, cards }: { status: Status; cards: BoardCard[] }) {
          return (
            <section className="column">
              <h2>{status}</h2>
              {cards.map((c) => (
                <article key={c.id} className="card">
                  <span className="key">{c.key}</span> {c.title}
                </article>
              ))}
            </section>
          );
        }

        """;

    private const string ColumnV2 = """
        import { Card } from "./Card";
        import type { BoardCard, Status } from "./types";

        interface Props {
          status: Status;
          cards: BoardCard[];
          onDrop?: (cardId: number, beforeId: number | null) => void;
        }

        export function Column({ status, cards, onDrop }: Props) {
          return (
            <section
              className="column"
              onDragOver={(e) => e.preventDefault()}
              onDrop={(e) => onDrop?.(Number(e.dataTransfer.getData("text/card")), null)}
            >
              <h2>{status}</h2>
              {cards.map((c) => (
                <Card key={c.id} card={c} />
              ))}
            </section>
          );
        }

        """;

    private const string Card = """
        import type { BoardCard } from "./types";

        export function Card({ card }: { card: BoardCard }) {
          return (
            <article
              className="card"
              draggable
              onDragStart={(e) => e.dataTransfer.setData("text/card", String(card.id))}
            >
              <span className="key">{card.key}</span> {card.title}
            </article>
          );
        }

        """;

    private const string CardV2 = """
        import type { BoardCard } from "./types";

        interface Props {
          card: BoardCard;
          onMove?: (direction: -1 | 1) => void;
        }

        export function Card({ card, onMove }: Props) {
          return (
            <article
              className="card"
              tabIndex={0}
              draggable
              onDragStart={(e) => e.dataTransfer.setData("text/card", String(card.id))}
              onKeyDown={(e) => {
                if (!e.altKey) return;
                if (e.key === "ArrowLeft") onMove?.(-1);
                if (e.key === "ArrowRight") onMove?.(1);
              }}
            >
              <span className="key">{card.key}</span> {card.title}
            </article>
          );
        }

        """;

    private const string UseBoardDnd = """
        import { useState } from "react";
        import type { BoardCard, Status } from "./types";

        export function useBoardDnd(initial: BoardCard[]) {
          const [cards, setCards] = useState(initial);

          function moveCard(id: number, status: Status, beforeId: number | null) {
            setCards((prev) => {
              const column = prev.filter((c) => c.status === status && c.id !== id).sort((a, b) => a.order - b.order);
              const idx = beforeId == null ? -1 : column.findIndex((c) => c.id === beforeId);
              const order = idx < 0 ? (column.at(-1)?.order ?? 0) + 1 : idx === 0 ? column[0].order - 1 : (column[idx - 1].order + column[idx].order) / 2;
              const next = prev.map((c) => (c.id === id ? { ...c, status, order } : c));
              void fetch(`/api/cards/${id}`, { method: "PATCH", body: JSON.stringify({ status, order }) });
              return next;
            });
          }

          return { cards, moveCard };
        }

        """;
}
