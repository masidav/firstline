#!/usr/bin/env bash
# Runs the promoter journeys locally: the backend stack from tools/testkit/compose.yml (a throwaway PostgreSQL 16,
# migrations and seed data through the testkit image, the API image), then Playwright, which builds and serves the
# dashboard itself. Needs Docker (with compose), Node, curl, and Chrome (E2E_CHANNEL=chrome, the default here). No
# .NET SDK or Python: the backend runs from its images, built from this checkout unless E2E_BACKEND_TAG (and
# E2E_IMAGE_PREFIX, the registry) name published ones. Extra arguments go to Playwright.
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/../../.." && pwd)
COMPOSE=(docker compose -f "$ROOT/tools/testkit/compose.yml" -p "${E2E_PROJECT:-fandom-promoter-e2e}")

export E2E_DB_PORT=${E2E_PORT:-55481}
export E2E_API_PORT=5271                             # e2e/settings.ts
export E2E_APP_ORIGINS=http://127.0.0.1:8081,http://127.0.0.1:8082
export E2E_SIGNING_KEY=${E2E_SIGNING_KEY:-$(head -c 32 /dev/urandom | base64)}
export E2E_DATABASE_URL="postgres://fandom:e2e@127.0.0.1:$E2E_DB_PORT/fandom_e2e"
export E2E_CHANNEL=${E2E_CHANNEL-chrome}

"${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
trap '"${COMPOSE[@]}" down -v >/dev/null 2>&1' EXIT
if [ -n "${E2E_BACKEND_TAG:-}" ]; then
  "${COMPOSE[@]}" pull -q
else
  "${COMPOSE[@]}" build -q
fi
"${COMPOSE[@]}" up -d --wait
until curl -fsS "http://127.0.0.1:$E2E_API_PORT/v1/health" >/dev/null 2>&1; do sleep 1; done
echo "Backend ready (migrated and seeded)."

cd "$HERE/.."
npx playwright test -c e2e/playwright.config.ts "$@"
