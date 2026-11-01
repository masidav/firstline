#!/usr/bin/env bash
# On-sale spike through the full HTTP path, locally (tools/load/README.md, "Full HTTP path"). Starts a throwaway
# PostgreSQL 16 and the API's production image (Release, sized like one GKE pod) in Docker, applies the migrations,
# and for each peak arrival rate in LEVELS loads a fresh show, allocation, and enough signed-in synthetic fans
# (setup.sql), runs k6 (onsale.js, grafana/k6 image) against the API, then checks in the database that every fan who
# got to the register step is registered exactly once. Needs Docker and the .NET SDK; nothing is installed on the host.
#
#   tools/load/http/run.sh                          # peaks of 25, 50, 100, 200 fans a second, API at 0.5 CPU
#   LEVELS="100" API_CPUS=1 tools/load/http/run.sh  # one level, a bigger pod
#
# The signing key is generated for the run and thrown away; no real secret is used.
set -euo pipefail

LEVELS=${LEVELS:-"25 50 100 200"}
HOLD=${HOLD:-60}
API_CPUS=${API_CPUS:-0.5}       # the api Deployment's CPU limit (infrastructure/k8s/base/api-deployment.yaml)
API_MEMORY=${API_MEMORY:-512m}
API_POOL=${API_POOL:-20}        # Maximum Pool Size in the cloud connection string (terraform sql.tf)
DB_CPUS=${DB_CPUS:-2}           # prod's Cloud SQL tier has 2 vCPUs (terraform envs/prod)
TICKETS=${TICKETS:-2000}
PORT=${PORT:-55498}
K6_IMAGE=${K6_IMAGE:-grafana/k6:2.3.0}
NAME=fandom-load-http
HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/../../.." && pwd)
OUT=${OUT:-$(mktemp -d)}
mkdir -p "$OUT"

cleanup() { docker rm -f "$NAME-db" "$NAME-api" >/dev/null 2>&1 || true; docker network rm "$NAME" >/dev/null 2>&1 || true; }
cleanup
trap cleanup EXIT
docker network create "$NAME" >/dev/null

# A throwaway key in the format the API requires (base64 of 32 random bytes). k6 runs as this user to read it.
(umask 077; openssl rand -base64 32 > "$OUT/signing-key")

docker run -d --name "$NAME-db" --network "$NAME" --cpus "$DB_CPUS" -p "127.0.0.1:$PORT:5432" \
  -e POSTGRES_PASSWORD=load -e POSTGRES_USER=fandom -e POSTGRES_DB=fandom_score \
  postgres:16 -c max_connections=200 -c shared_buffers=512MB -c shared_preload_libraries=pg_stat_statements >/dev/null
until docker exec "$NAME-db" pg_isready -U fandom -d fandom_score >/dev/null 2>&1; do sleep 1; done
sleep 2
ConnectionStrings__Database="Host=127.0.0.1;Port=$PORT;Database=fandom_score;Username=fandom;Password=load" \
  dotnet run --project "$ROOT/database" | tail -1
docker exec "$NAME-db" psql -q -U fandom -d fandom_score -c "CREATE EXTENSION pg_stat_statements"
docker cp "$HERE/setup.sql" "$NAME-db:/setup.sql"
docker cp "$HERE/cleanup.sql" "$NAME-db:/cleanup.sql"
psql() { docker exec -i "$NAME-db" psql -q -U fandom -d fandom_score "$@"; }

echo "== Building the API image (Dockerfile target api)"
docker build -q --target api -t fandom-load-api:local "$ROOT" >/dev/null
Auth__SigningKey=$(cat "$OUT/signing-key") \
ConnectionStrings__Database="Host=$NAME-db;Database=fandom_score;Username=fandom;Password=load;Maximum Pool Size=$API_POOL;${API_CONNECTION_EXTRA:-}" \
  docker run -d --name "$NAME-api" --network "$NAME" --cpus "$API_CPUS" --memory "$API_MEMORY" \
    -e Auth__SigningKey -e ConnectionStrings__Database -e App__PublicOrigin=http://localhost \
    -e Storage__LocalPath=/tmp/storage fandom-load-api:local >/dev/null
for _ in $(seq 60); do
  docker run --rm --network "$NAME" --entrypoint wget "$K6_IMAGE" -qO- "http://$NAME-api:8080/v1/health" >/dev/null 2>&1 && break
  sleep 1
done

# Fans the arrival-rate stages in onsale.js use (ramp 1 -> peak/4 for 30 s, 30 s there, 10 s up to the peak, HOLD s
# there, 10 s down), plus 5 % and 100 for k6 starting iterations a little early.
fans_for() { awk -v p="$1" -v h="$HOLD" 'BEGIN { b = int(p / 4 + 0.5); if (b < 1) b = 1;
  printf "%d", ((1 + b) / 2 * 30 + b * 30 + (b + p) / 2 * 10 + p * h + p / 2 * 10) * 1.05 + 100 }'; }

for peak in $LEVELS; do
  run="p${peak}x$RANDOM"
  fans=$(fans_for "$peak")
  echo
  echo "== Peak $peak fans/s: run $run, $fans fans, API ${API_CPUS} CPU / pool $API_POOL, database $DB_CPUS CPU"
  psql -v ON_ERROR_STOP=1 -v run="$run" -v fans="$fans" -v tickets="$TICKETS" \
    -v city=us-ak-anchorage -v other_city=us-hi-honolulu -f /setup.sql
  psql -c "ANALYZE"
  psql -c "SELECT pg_stat_statements_reset()" >/dev/null
  sleep 4   # registration opens 3 seconds after setup

  : > "$OUT/stats-$run.txt"
  ( while true; do docker stats --no-stream --format '{{.Name}} {{.CPUPerc}} {{.MemUsage}}' "$NAME-api" "$NAME-db" \
      >> "$OUT/stats-$run.txt" 2>/dev/null; sleep 2; done ) &
  stats=$!
  set +e
  docker run --rm --network "$NAME" --user "$(id -u):$(id -g)" -v "$HERE:/scripts:ro" -v "$OUT:/out" \
    -e BASE_URL="http://$NAME-api:8080" -e RUN="$run" -e FANS="$fans" -e PEAK="$peak" -e HOLD="$HOLD" \
    -e SIGNING_KEY_FILE=/out/signing-key "$K6_IMAGE" run -q --summary-export "/out/summary-$run.json" /scripts/onsale.js \
    > "$OUT/k6-$run.txt" 2>&1
  k6_status=$?
  set -e
  kill "$stats" 2>/dev/null; wait "$stats" 2>/dev/null || true
  python3 -I "$HERE/summary.py" "$OUT/summary-$run.json" "$peak"
  [ "$k6_status" = 0 ] && echo "k6: all thresholds passed" || echo "k6: THRESHOLDS FAILED (exit $k6_status; $OUT/k6-$run.txt)"

  awk -v api="$NAME-api" -v limit="$API_CPUS" '$1 == api { v = $2 + 0; if (v > m) m = v; s += v; n++ }
       END { printf "API CPU: peak %.0f%%, average %.0f%% of one core (limit %s)\n", m, s / n, limit }' \
    "$OUT/stats-$run.txt"
  awk -v db="$NAME-db" '$1 == db { v = $2 + 0; if (v > m) m = v; s += v; n++ }
       END { printf "Database CPU: peak %.0f%%, average %.0f%% of one core\n", m, s / n }' "$OUT/stats-$run.txt"
  # What the database spent its time on during the run.
  psql -c "SELECT calls, round(total_exec_time) AS total_ms, round(mean_exec_time::numeric, 3) AS mean_ms,
                  left(regexp_replace(query, '\s+', ' ', 'g'), 120) AS query
             FROM pg_stat_statements ORDER BY total_exec_time DESC LIMIT 20" > "$OUT/statements-$run.txt"

  # Every fan k6 sent to the register step must hold exactly one live registration; nobody else may have one.
  attempts=$(python3 -I "$HERE/summary.py" "$OUT/summary-$run.json" attempts)
  psql -At -v run="$run" -v attempts="$attempts" <<'SQL'
SELECT format('database: %s registrations, %s fans, %s with more than one, %s k6 attempts -> %s',
              count(*), count(DISTINCT user_id), count(*) - count(DISTINCT user_id), :attempts,
              CASE WHEN count(*) = :attempts AND count(DISTINCT user_id) = count(*) THEN 'OK, every fan registered once'
                   ELSE 'MISMATCH' END)
  FROM allocation_registrations
 WHERE allocation_id = md5('fandom-load:' || :'run' || ':allocation')::uuid;
SQL
done

echo
echo "== API errors logged: $(docker logs "$NAME-api" 2>&1 | grep -c '"LogLevel":"\(Error\|Critical\)"' || true)"
docker logs "$NAME-api" 2>&1 | grep '"LogLevel":"\(Error\|Critical\)"' | head -3 || true
echo "== Cleanup check (cleanup.sql on the last run)"
psql -v run="$run" -f /cleanup.sql
echo "Output in $OUT"
