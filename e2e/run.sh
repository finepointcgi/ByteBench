#!/usr/bin/env bash
# One command per layer of the ByteBench test suite (see docs/testing.md).
#
#   e2e/run.sh smoke            Playwright against a real server (no Docker needed)             ~1 min, every PR
#   e2e/run.sh docker           real Docker workspaces (builds the e2e workspace image)           ~2 min warm, nightly
#   e2e/run.sh explore [args]   the AI exploratory tester (needs the `claude` CLI or ANTHROPIC_API_KEY)
#   e2e/run.sh triage [args]    the AI triage of a failed run (needs ANTHROPIC_API_KEY or the `claude` CLI)
#   e2e/run.sh generate [args]  draft Playwright tests for an issue or a diff (never merged automatically)
#   e2e/run.sh serve            start a disposable seeded server and keep it running (poke at what the tests see)
#   e2e/run.sh docker-clean     remove leftovers of an aborted docker run   (--images also drops the cached e2e image)
#   e2e/run.sh typecheck        type-check the e2e code
#
# Extra arguments go to the underlying tool, e.g.  e2e/run.sh smoke -g "pull request"   or   e2e/run.sh smoke --headed
set -euo pipefail
cd "$(dirname "$0")"

cmd="${1:-help}"
[ $# -gt 0 ] && shift

ensure_deps() {
  if [ ! -d node_modules ]; then
    echo "[e2e] installing npm dependencies"
    npm ci --no-audit --no-fund
  fi
}

ensure_browser() {
  # Installs Chromium once (cached in ~/.cache/ms-playwright). In CI the workflow installs it with system deps.
  npx playwright install chromium >/dev/null
}

case "$cmd" in
  smoke)
    ensure_deps; ensure_browser
    E2E_TIER=smoke exec npx playwright test --project=smoke "$@"
    ;;
  docker)
    command -v docker >/dev/null || { echo "docker not found: the docker tier needs a Docker daemon (use 'smoke' otherwise)" >&2; exit 2; }
    docker info >/dev/null 2>&1 || { echo "Docker is installed but its daemon is not running" >&2; exit 2; }
    ensure_deps; ensure_browser
    E2E_TIER=docker exec npx playwright test --project=docker "$@"
    ;;
  explore)
    ensure_deps; ensure_browser
    exec npx tsx ai-explorer/explore.ts "$@"
    ;;
  triage)
    ensure_deps
    exec npx tsx ai-triage/triage.ts "$@"
    ;;
  generate)
    ensure_deps
    exec npx tsx ai-triage/generate-tests.ts "$@"
    ;;
  serve)
    ensure_deps
    exec npx tsx support/dev-server.ts "$@"
    ;;
  docker-clean)
    ensure_deps
    exec npx tsx support/docker-clean.ts "$@"
    ;;
  typecheck)
    ensure_deps
    exec npx tsc --noEmit
    ;;
  help|-h|--help|*)
    sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'
    ;;
esac
