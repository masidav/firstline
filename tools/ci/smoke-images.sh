#!/usr/bin/env bash
# Starts the server images the way the cluster does (infrastructure/k8s) and checks they come up: the migrator
# applies every migration to an empty PostgreSQL, the API answers GET /v1/health, and the worker's liveness
# endpoint answers GET /healthz with both loops reporting. Runtime images are chiseled (no shell), so this is the
# only check before a deploy that the published apps start at all with production settings (App__PublicOrigin set,
# no dev sign-in, secrets only from the environment). CI job "images" in .github/workflows/ci.yml; locally:
#
#   docker build --target migrate -t fandomscore-migrate . && docker build --target api -t fandomscore-api . \
#     && docker build --target worker -t fandomscore-worker . && tools/ci/smoke-images.sh
#
# Images come from MIGRATE_IMAGE, API_IMAGE, and WORKER_IMAGE (defaults above). Exit status is non-zero on any failure.
set -euo pipefail

MIGRATE_IMAGE=${MIGRATE_IMAGE:-fandomscore-migrate}
API_IMAGE=${API_IMAGE:-fandomscore-api}
WORKER_IMAGE=${WORKER_IMAGE:-fandomscore-worker}
run_id="smoke-$$"
network="$run_id"
db_password="smoke-only-$(head -c 12 /dev/urandom | base64 | tr -dc 'a-zA-Z0-9')"
connection="Host=db;Port=5432;Database=fandom_score;Username=fandom;Password=$db_password"
failed=0

cleanup() {
  for c in api worker db; do docker rm -f "$run_id-$c" >/dev/null 2>&1 || true; done
  docker network rm "$network" >/dev/null 2>&1 || true
}
trap cleanup EXIT

fail() {
  echo "::error::$1"
  failed=1
}

# Repeats a command until it succeeds or the deadline (seconds) passes.
wait_for() {
  local deadline=$(( $(date +%s) + $1 )); shift
  until "$@" >/dev/null 2>&1; do
    if [ "$(date +%s)" -ge "$deadline" ]; then return 1; fi
    sleep 1
  done
}

# A curl inside the network (the chiseled images have none), so the test never depends on published ports.
probe() { docker run --rm --network "$network" curlimages/curl:8.11.1 -fsS --max-time 5 "$@"; }

docker network create "$network" >/dev/null
docker run -d --name "$run_id-db" --network "$network" --network-alias db \
  -e POSTGRES_DB=fandom_score -e POSTGRES_USER=fandom -e POSTGRES_PASSWORD="$db_password" postgres:16 >/dev/null
wait_for 60 docker exec "$run_id-db" pg_isready -U fandom -d fandom_score || { echo "PostgreSQL did not start"; exit 1; }

echo "== migrate"
if docker run --rm --network "$network" -e ConnectionStrings__Database="$connection" "$MIGRATE_IMAGE"; then
  echo "migrations applied"
else
  fail "The migrate image failed to apply the migrations."
fi

echo "== api"
# Production settings as in the cluster: a public origin (required outside Development) and no dev sign-in.
docker run -d --name "$run_id-api" --network "$network" --network-alias api \
  -e ConnectionStrings__Database="$connection" \
  -e App__PublicOrigin=http://api:8080 \
  -e Auth__SigningKey="$(head -c 32 /dev/urandom | base64)" \
  "$API_IMAGE" >/dev/null
if wait_for 60 probe http://api:8080/v1/health; then
  echo "GET /v1/health: $(probe http://api:8080/v1/health)"
else
  fail "The API image did not answer GET /v1/health within 60 s."
fi
# No `/v1` route may be reachable without sign-in: a 401 proves authentication is on in the published build.
status=$(probe -o /dev/null -w '%{http_code}' http://api:8080/v1/me || true)
[ "$status" = "401" ] || fail "GET /v1/me without a token answered $status, expected 401."
echo "GET /v1/me without a token: $status"

echo "== worker"
# Secret-free, like the redaction Deployment: Spotify, Ticketmaster, and push stay off when their settings are empty.
docker run -d --name "$run_id-worker" --network "$network" --network-alias worker \
  -e ConnectionStrings__Database="$connection" \
  "$WORKER_IMAGE" >/dev/null
if wait_for 60 probe http://worker:8081/healthz; then
  echo "GET /healthz: $(probe http://worker:8081/healthz)"
else
  fail "The worker image did not answer GET /healthz within 60 s."
fi
# The worker registers a schedule per recurring job kind once its loops run (workers/jobs).
if wait_for 60 sh -c "docker exec $run_id-db psql -U fandom -d fandom_score -Atc 'SELECT count(*) FROM job_schedules' | grep -qv '^0\$'"; then
  echo "job schedules registered: $(docker exec "$run_id-db" psql -U fandom -d fandom_score -Atc 'SELECT count(*) FROM job_schedules')"
else
  fail "The worker registered no job schedules within 60 s."
fi

# Both log JSON lines; nothing may have logged at Error or Critical while starting. (The chiseled images print
# "Cannot load library libgssapi_krb5.so.2" once when Npgsql probes GSSAPI; harmless with password authentication,
# infrastructure/TODO.md, and not a log line.)
for c in api worker; do
  if docker logs "$run_id-$c" 2>&1 | grep -qE '"LogLevel":"(Error|Critical)"'; then
    fail "The $c logged an error while starting:"
    docker logs "$run_id-$c" 2>&1 | grep -E '"LogLevel":"(Error|Critical)"' | head -20
  fi
done

if [ "$failed" -ne 0 ]; then
  echo "== api log"; docker logs "$run_id-api" 2>&1 | tail -60
  echo "== worker log"; docker logs "$run_id-worker" 2>&1 | tail -60
  exit 1
fi
echo "All images started and answered."
