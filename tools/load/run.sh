#!/usr/bin/env bash
# On-sale load test for allocation registration (tools/load/README.md). Starts a throwaway PostgreSQL 16 in Docker,
# applies the migrations, loads FANS fans, and runs pgbench: every fan registers once at each client count, then the
# per-request standing lookup and the standing snapshot load. Needs Docker and the .NET SDK.
set -euo pipefail

FANS=${FANS:-20000}
CLIENTS=${CLIENTS:-"10 50 200"}
PORT=${PORT:-55499}
NAME=${NAME:-fandom-load}
HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/../.." && pwd)
read -r -a CLIENT_LIST <<< "$CLIENTS"
SHOWS=${#CLIENT_LIST[@]}

docker rm -f "$NAME" >/dev/null 2>&1 || true
docker run -d --name "$NAME" -e POSTGRES_PASSWORD=load -e POSTGRES_USER=fandom -e POSTGRES_DB=fandom_score \
  -p "$PORT:5432" postgres:16 -c max_connections=400 -c shared_buffers=512MB >/dev/null
trap 'docker rm -f "$NAME" >/dev/null 2>&1' EXIT
until docker exec "$NAME" pg_isready -U fandom -d fandom_score >/dev/null 2>&1; do sleep 1; done
sleep 2

ConnectionStrings__Database="Host=localhost;Port=$PORT;Database=fandom_score;Username=fandom;Password=load" \
  dotnet run --project "$ROOT/database" | tail -1
for f in setup.sql register.sql standing_me.sql snapshot_load.sql; do docker cp "$HERE/$f" "$NAME:/$f"; done
docker exec "$NAME" psql -q -v ON_ERROR_STOP=1 -v fans="$FANS" -v shows="$SHOWS" -U fandom -d fandom_score -f /setup.sql
# Planner statistics, as autovacuum keeps them on a real database (a bulk load leaves none).
docker exec "$NAME" psql -q -U fandom -d fandom_score -c "ANALYZE"
sleep 2   # registration opens a second after setup

bench() { docker exec -e PGOPTIONS="$1" "$NAME" pgbench -n -U fandom -d fandom_score "${@:2}" 2>&1 \
            | grep -E '^tps|latency average|failed transactions' | tr '\n' ' '; echo; }

echo "== Registrations: $FANS fans, each registers once per run"
for i in "${!CLIENT_LIST[@]}"; do
  clients=${CLIENT_LIST[$i]}
  allocation=$(docker exec "$NAME" psql -U fandom -d fandom_score -Atc "SELECT allocation_id FROM load_shows WHERE n = $((i + 1))")
  docker exec "$NAME" psql -q -U fandom -d fandom_score -c "SELECT setval('load_seq', 1, false)" >/dev/null
  printf "clients=%-4s " "$clients"
  bench "-c load.allocation=$allocation -c load.fans=$FANS" -c "$clients" -j 8 -t $((FANS / clients)) -f /register.sql
done
docker exec "$NAME" psql -U fandom -d fandom_score -Atc \
  "SELECT 'registered per show: ' || string_agg(c::text, ', ') FROM (SELECT count(*) c FROM allocation_registrations GROUP BY allocation_id ORDER BY 1) x"

docker exec "$NAME" psql -q -U fandom -d fandom_score -c "ANALYZE allocation_registrations"
allocation=$(docker exec "$NAME" psql -U fandom -d fandom_score -Atc "SELECT allocation_id FROM load_shows WHERE n = 1")
fan=$(docker exec "$NAME" psql -U fandom -d fandom_score -Atc "SELECT id FROM load_fans WHERE n = 777")
echo "== Standing, per request (50 clients, 15 s)"
bench "-c load.allocation=$allocation -c load.fan=$fan" -c 50 -j 8 -T 15 -f /standing_me.sql
echo "== Standing snapshot load ($FANS registrations, 1 client, 10 s)"
bench "-c load.allocation=$allocation" -c 1 -T 10 -f /snapshot_load.sql
