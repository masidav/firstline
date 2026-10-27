#!/usr/bin/env bash
# Runs the promoter journeys locally: a throwaway PostgreSQL 16 in Docker with migrations and seed data, then
# Playwright, which builds the dashboard and starts the API itself. Needs Docker, the .NET SDK, Node, Python 3, and
# Chrome (E2E_CHANNEL=chrome, the default here). Extra arguments go to Playwright.
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/../../.." && pwd)
NAME=${E2E_CONTAINER:-fandom-promoter-e2e}
PORT=${E2E_PORT:-55481}
export E2E_DATABASE_URL="postgres://fandom:e2e@127.0.0.1:$PORT/fandom_e2e"
export E2E_CHANNEL=${E2E_CHANNEL-chrome}

docker rm -f "$NAME" >/dev/null 2>&1 || true
docker run -d --name "$NAME" -e POSTGRES_USER=fandom -e POSTGRES_PASSWORD=e2e -e POSTGRES_DB=fandom_e2e \
  -p "127.0.0.1:$PORT:5432" postgres:16 >/dev/null
trap 'docker rm -f "$NAME" >/dev/null 2>&1' EXIT
until docker exec "$NAME" pg_isready -U fandom -d fandom_e2e >/dev/null 2>&1; do sleep 1; done
sleep 2

ConnectionStrings__Database="Host=127.0.0.1;Port=$PORT;Database=fandom_e2e;Username=fandom;Password=e2e" \
  dotnet run --project "$ROOT/database" | tail -1
python3 "$ROOT/tools/dev/seed.py" --fake --sql | docker exec -i "$NAME" psql -q -v ON_ERROR_STOP=1 -U fandom -d fandom_e2e >/dev/null
echo "Seeded."

cd "$HERE/.."
npx playwright test -c e2e/playwright.config.ts "$@"
