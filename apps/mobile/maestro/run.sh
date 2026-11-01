#!/usr/bin/env bash
# Runs the native Maestro journeys (maestro/README.md) against the real API and a seeded database, like the browser
# suite (e2e/run.sh): PostgreSQL 16 with migrations and seed data, the API from source, the journeys' accounts and
# allocation (setup.mjs), then `maestro test maestro/journeys`.
#
# Needs an Android emulator (or device) on adb with the app installed, or MAESTRO_APK=path/to/app.apk to install it.
# The app must be built with EXPO_PUBLIC_API_URL=http://127.0.0.1:5271/v1 and EXPO_PUBLIC_DEV_SIGN_IN=true; this
# script forwards the emulator's port 5271 to the API here (`adb reverse`). Also needs Docker (unless
# E2E_DATABASE_URL names a prepared database), the .NET SDK, Node, Python 3, and the maestro CLI. Output (JUnit
# report, screenshots, the API log) goes to maestro-output/. Extra arguments go to `maestro test`; MAESTRO_FLOWS
# picks other flows (e.g. maestro/journeys/journal.yaml).
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
MOBILE=$(cd "$HERE/.." && pwd)
ROOT=$(cd "$MOBILE/../.." && pwd)
OUT="$MOBILE/maestro-output"
API_PORT=5271
mkdir -p "$OUT"

cleanup() {
  [ -n "${API_PID:-}" ] && kill "$API_PID" 2>/dev/null || true
  [ -n "${CONTAINER:-}" ] && docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
}
trap cleanup EXIT

if [ -z "${E2E_DATABASE_URL:-}" ]; then
  CONTAINER=${MAESTRO_CONTAINER:-fandom-maestro}
  PORT=${MAESTRO_DB_PORT:-55481}
  export E2E_DATABASE_URL="postgres://fandom:e2e@127.0.0.1:$PORT/fandom_e2e"
  docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
  docker run -d --name "$CONTAINER" -e POSTGRES_USER=fandom -e POSTGRES_PASSWORD=e2e -e POSTGRES_DB=fandom_e2e \
    -p "127.0.0.1:$PORT:5432" postgres:16 >/dev/null
  until docker exec "$CONTAINER" pg_isready -U fandom -d fandom_e2e >/dev/null 2>&1; do sleep 1; done
  sleep 2
fi

# postgres://user:password@host:port/db as an Npgsql connection string, for the migrator and the API.
NPGSQL=$(node -e '
  const u = new URL(process.argv[1]);
  console.log(`Host=${u.hostname};Port=${u.port || 5432};Database=${u.pathname.slice(1)};` +
    `Username=${decodeURIComponent(u.username)};Password=${decodeURIComponent(u.password)}`);
' "$E2E_DATABASE_URL")

ConnectionStrings__Database="$NPGSQL" dotnet run --project "$ROOT/database" | tail -1
if [ -n "${CONTAINER:-}" ]; then
  python3 "$ROOT/tools/dev/seed.py" --fake --sql | docker exec -i "$CONTAINER" psql -q -v ON_ERROR_STOP=1 -U fandom -d fandom_e2e >/dev/null
else
  python3 "$ROOT/tools/dev/seed.py" --fake --sql | psql "$E2E_DATABASE_URL" -q -v ON_ERROR_STOP=1 >/dev/null
fi
echo "Seeded."

dotnet build "$ROOT/services/api" -v quiet -nologo | tail -2
ASPNETCORE_ENVIRONMENT=Development \
  ASPNETCORE_URLS="http://127.0.0.1:$API_PORT" \
  ConnectionStrings__Database="$NPGSQL" \
  Auth__SigningKey="$(head -c 32 /dev/urandom | base64)" \
  Auth__DevSignIn=true \
  Auth__PermitsPerMinute=1000 \
  Storage__LocalPath="$(mktemp -d)" \
  dotnet run --project "$ROOT/services/api" --no-build --no-launch-profile > "$OUT/api.log" 2>&1 &
API_PID=$!
for _ in $(seq 1 120); do
  curl -fsS "http://127.0.0.1:$API_PORT/v1/health" >/dev/null 2>&1 && break
  kill -0 "$API_PID" 2>/dev/null || { echo "The API exited:"; tail -n 40 "$OUT/api.log"; exit 1; }
  sleep 1
done
curl -fsS "http://127.0.0.1:$API_PORT/v1/health" >/dev/null
echo "API up."

adb wait-for-device
adb reverse "tcp:$API_PORT" "tcp:$API_PORT"
if [ -n "${MAESTRO_APK:-}" ]; then
  adb install -r "$MAESTRO_APK" >/dev/null
  echo "Installed $MAESTRO_APK."
fi

cd "$MOBILE"
ARGS=()
DATA=$(node maestro/setup.mjs)
while IFS= read -r line; do ARGS+=(-e "$line"); done <<< "$DATA"
echo "Journey data ready ($(( ${#ARGS[@]} / 2 )) values)."

export MAESTRO_CLI_NO_ANALYTICS=1
maestro test --format junit --output "$OUT/report.xml" --test-output-dir "$OUT" --debug-output "$OUT/debug" \
  "${ARGS[@]}" "$@" "${MAESTRO_FLOWS:-maestro/journeys}"
