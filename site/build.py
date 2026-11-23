#!/usr/bin/env python3
"""Builds the ByteBench documentation site into ./public (what GitHub Pages serves).

No dependencies: pages are HTML fragments in site/pages, wrapped in one layout.
Run:  python3 site/build.py            (writes public/)
      python3 site/build.py --check    (also fails on broken internal links or missing pages)
"""
import html, json, pathlib, re, shutil, sys

ROOT = pathlib.Path(__file__).resolve().parent
OUT = ROOT.parent / "public"
REPO_URL = "https://github.com/finepointcgi/ByteBench"

# (section, [(slug, nav title, one-line summary)]). Order = reading order.
SECTIONS = [
    ("Start here", [
        ("index", "Overview", "What ByteBench is and everything it does"),
        ("getting-started", "Getting started", "Run ByteBench, sign in and create your first project"),
        ("concepts", "Concepts", "Projects, groups, repositories, issues and how they fit together"),
        ("explore-shortcuts", "Explore, search and keyboard shortcuts", "Explore, stars, profiles, global search and every shortcut"),
        ("configuration", "Configuration reference", "Every setting, file and environment variable"),
    ]),
    ("Plan and track work", [
        ("issues", "Issues and workflows", "Issues, statuses, workflows, hierarchy, links and attachments"),
        ("boards-sprints", "Boards and sprints", "Kanban boards, sprints, backlog and roadmap"),
        ("fields-filters", "Fields, filters and search", "Custom fields, components, versions, saved filters, GQL and CSV"),
        ("importing-issues", "Importing from GitHub, GitLab and Jira", "Copy issues, comments, labels and milestones from another tracker"),
        ("automation", "Automation rules", "Triggers, conditions and actions"),
        ("reports", "Dashboards and delivery reports", "Dashboards, cycle time, flow and DORA metrics"),
        ("service-desk", "Service desk", "Let people without an account file issues by email or form"),
        ("workbench", "Estimates and Workbench", "Story points, estimates and the Workbench timesheet link"),
    ]),
    ("Code and review", [
        ("repositories", "Repositories and Git hosting", "Clone over HTTP and SSH, deploy keys, LFS, mirroring"),
        ("lfs-mirroring", "Git LFS and mirroring", "LFS server, pull and push mirrors, credentials and troubleshooting"),
        ("pull-requests", "Pull requests", "Reviews, threads, suggestions, code owners, merge strategies, forks"),
        ("branch-protection", "Branch protection and merge queue", "Protected branches and tags, required checks, the merge queue"),
        ("code-tools", "Code browsing and tools", "Code view, search, blame, compare, releases and tags"),
        ("insights-dependencies", "Insights and the dependency bot", "Contributors, activity and languages, and automatic dependency update pull requests"),
        ("web-editor", "Web editor and AI assistant", "Edit code in the browser with an AI assistant"),
        ("wiki-snippets", "Wiki and snippets", "Project wikis and shareable snippets"),
    ]),
    ("Build, test and ship", [
        ("ci", "CI pipelines", "The CI file format, runners, variables, artifacts, caches and schedules"),
        ("environments", "Environments and deployments", "Deploy approvals, rollback, freezes and traceability"),
        ("packages", "Package and container registry", "NuGet, npm and Docker images per project"),
        ("pages", "Static sites (Pages)", "Publish a site from a CI job"),
        ("security-scanning", "Security scanning", "Night Owl findings, triage and pull request checks"),
        ("feature-flags", "Feature flags", "Flags per environment, rules, rollouts and SDK keys"),
    ]),
    ("AI features", [
        ("ai", "AI overview and setup", "API key, Claude Code backend, and what each AI feature does"),
        ("ai-review-triage", "AI review and triage", "AI pull request review and issue triage"),
    ]),
    ("Integrations and API", [
        ("api", "REST API and webhooks", "Tokens, endpoints, GraphQL-style search and webhook events"),
        ("chat", "Slack and Microsoft Teams", "Notifications, slash commands and link previews"),
        ("notifications", "Notifications and email", "In-app, email, preferences and mentions"),
    ]),
    ("Administer", [
        ("accounts", "Accounts, sign-in and SSO", "Passwords, two-factor, OIDC, OAuth and LDAP"),
        ("permissions", "Roles, groups and permissions", "Who can do what, group inheritance, visibility"),
        ("admin", "Administration", "User admin, audit log, system page, rate limits"),
        ("production", "Production setup", "Docker, Postgres, reverse proxy, backups and upgrades"),
        ("troubleshooting", "FAQ and troubleshooting", "Common problems and known limits"),
        ("glossary", "Glossary", "Plain-language definitions of every term"),
    ]),
]
PAGES = [p for _, ps in SECTIONS for p in ps]

LAYOUT = """<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{title} · ByteBench</title>
<meta name="description" content="{summary}">
<link rel="icon" href="favicon.svg" type="image/svg+xml">
<link rel="stylesheet" href="style.css">
</head>
<body>
<header class="top">
  <button class="menu" aria-label="Toggle navigation" aria-expanded="false" aria-controls="side">☰</button>
  <a class="brand" href="index.html"><span class="logo" aria-hidden="true"></span> ByteBench</a>
  <div class="search"><input id="q" type="search" placeholder="Search the docs" aria-label="Search the docs" autocomplete="off"><ul id="results" hidden></ul></div>
  <a class="gh" href="{repo}">Source</a>
</header>
<div class="shell">
  <nav class="side" id="side" aria-label="Documentation">
{nav}
  </nav>
  <main>
    <article>
{body}
    </article>
    <footer class="pager">{prev}{next}</footer>
    <p class="foot">ByteBench documentation. Found a mistake? <a href="{repo}/issues">Open an issue</a>.</p>
  </main>
</div>
<script src="site.js"></script>
</body>
</html>
"""


def nav_html(current):
    out = []
    for section, pages in SECTIONS:
        out.append(f'    <h4>{html.escape(section)}</h4>\n    <ul>')
        for slug, title, _ in pages:
            cls = ' class="current" aria-current="page"' if slug == current else ""
            out.append(f'      <li><a{cls} href="{slug}.html">{html.escape(title)}</a></li>')
        out.append("    </ul>")
    return "\n".join(out)


def text_of(fragment):
    return re.sub(r"\s+", " ", html.unescape(re.sub(r"<[^>]+>", " ", fragment))).strip()


def build():
    if OUT.exists():
        shutil.rmtree(OUT)
    OUT.mkdir()
    for f in ("style.css", "site.js", "favicon.svg"):
        shutil.copy(ROOT / f, OUT / f)
    (OUT / ".nojekyll").write_text("")
    index, missing = [], []
    for i, (slug, title, summary) in enumerate(PAGES):
        src = ROOT / "pages" / f"{slug}.html"
        if not src.exists():
            missing.append(slug)
            body = f"<h1>{html.escape(title)}</h1><p class=\"lead\">{html.escape(summary)}</p><p>This page is being written.</p>"
        else:
            body = src.read_text(encoding="utf-8")
        prev = nxt = ""
        if i > 0:
            p = PAGES[i - 1]
            prev = f'<a class="prev" href="{p[0]}.html"><small>Previous</small>{html.escape(p[1])}</a>'
        if i < len(PAGES) - 1:
            n = PAGES[i + 1]
            nxt = f'<a class="next" href="{n[0]}.html"><small>Next</small>{html.escape(n[1])}</a>'
        page = LAYOUT.format(title=html.escape(title), summary=html.escape(summary), repo=REPO_URL,
                             nav=nav_html(slug), body=body, prev=prev, next=nxt)
        (OUT / f"{slug}.html").write_text(page, encoding="utf-8")
        # Search index: one entry per page, plus one per h2/h3 section.
        index.append({"u": f"{slug}.html", "t": title, "s": summary, "x": text_of(body)[:600]})
        for m in re.finditer(r'<h([23]) id="([^"]+)"[^>]*>(.*?)</h\1>(.*?)(?=<h[23] |$)', body, re.S):
            index.append({"u": f"{slug}.html#{m.group(2)}", "t": f"{title}: {text_of(m.group(3))}", "s": "", "x": text_of(m.group(4))[:300]})
    (OUT / "search-index.json").write_text(json.dumps(index, separators=(",", ":")), encoding="utf-8")
    return missing


def check():
    bad = []
    for f in OUT.glob("*.html"):
        text = f.read_text(encoding="utf-8")
        ids = set(re.findall(r'id="([^"]+)"', text))
        for href in re.findall(r'href="([^"]+)"', text):
            if href.startswith(("http://", "https://", "mailto:")):
                continue
            target, _, frag = href.partition("#")
            if target == "":
                if frag and frag not in ids:
                    bad.append(f"{f.name}: missing anchor #{frag}")
                continue
            t = OUT / target
            if not t.exists():
                bad.append(f"{f.name}: broken link {href}")
            elif frag and f'id="{frag}"' not in t.read_text(encoding="utf-8"):
                bad.append(f"{f.name}: missing anchor {href}")
    return bad


if __name__ == "__main__":
    missing = build()
    print(f"built {len(PAGES)} pages into {OUT}")
    problems = check() + [f"page not written: {m}" for m in missing]
    for p in problems:
        print("PROBLEM:", p)
    if problems and "--check" in sys.argv:
        sys.exit(1)
